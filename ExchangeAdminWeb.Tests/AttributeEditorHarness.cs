using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Drives ADAttributeEditorService's real lookup and save paths - and the undo service on top of
/// them - with no directory, no Delinea and no SMTP.
/// </summary>
/// <remarks>
/// The service's two test seams are overridden. <c>CreateDirectoryRunspace</c> hands back a
/// session in which Import-Module, Get-ADUser and Set-ADUser are local functions over
/// <see cref="LocalAd"/>, with module autoloading off so nothing in it can reach the real
/// ActiveDirectory module; <c>GetCredentialsAsync</c> returns a dummy credential. Everything else
/// is the shipped code: the allowlist store, validation, the protected-principal decision (fed by
/// a fake check), the servicer grant over a real section-access store, the audit writer and the
/// notification call, observed on a substituted <see cref="EmailService"/>.
/// </remarks>
internal sealed class AttributeEditorHarness : IDisposable
{
    public const string OperatorSid = "S-1-5-21-1000-2000-3000-1111";
    public const string ServicerSid = "S-1-5-21-1000-2000-3000-2222";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "eaw-attr-" + Guid.NewGuid().ToString("N"));
    private readonly SectionAccessRepository _sections;
    private readonly IConfigStore _allowlistStore;

    public AttributeEditorHarness()
    {
        Directory.CreateDirectory(_root);

        Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _root,
            ["Audit:RotationPeriod"] = "daily",
            ["Delinea:SecretServerUrl"] = "https://credentials.invalid",
        }).Build();

        Env = Substitute.For<IWebHostEnvironment>();
        Env.ContentRootPath.Returns(_root);

        var catalog = new ModuleCatalog();
        ModuleConfig = new ModuleConfigService(
            catalog, Env, TestConfigStore.CreateModuleConfig(_root), NullLogger<ModuleConfigService>.Instance);

        _allowlistStore = TestConfigStore.Create(_root);
        Allowlist = new AttributeEditorRepository(_allowlistStore);
        Allowlist.SaveAllowlist(
        [
            new AttributeRow("title", "Job Title", "String", null, false, true, null, null, 1),
            new AttributeRow("department", "Department", "String", null, false, true, null, null, 1),
            new AttributeRow("extensionAttribute5", "Cost Centre", "String", null, false, true, null, null, 2),
        ]);

        SectionStore = new SwitchableStore(TestConfigStore.Create(_root));
        _sections = new SectionAccessRepository(SectionStore);
        // Always configured, so the legacy AllowedGroups fallback is out of the picture.
        _sections.SaveAll(new Dictionary<string, string[]> { ["ADAttributeEditor"] = [OperatorSid] });
        var sectionAccess = new SectionAccessService(
            Config, NullLogger<SectionAccessService>.Instance, Env, catalog, _sections);
        Servicers = new ProtectedPrincipalServicerService(
            sectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance);

        var log = new JsonlLogService(Config, NullLogger<JsonlLogService>.Instance);
        Trace = new OperationTraceService(Config, log);
        Audit = new AuditService(log, Trace);

        Email = Substitute.For<EmailService>(Config, NullLogger<EmailService>.Instance);
        Email.SendAdminNotificationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>())
            .Returns(call =>
            {
                lock (_notices)
                {
                    _notices.Add(new Notice(
                        call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<string>(2), call.ArgAt<bool>(3),
                        call.ArgAt<string>(4),
                        new Dictionary<string, string>(call.ArgAt<IReadOnlyDictionary<string, string>>(5)),
                        call.ArgAt<string?>(6)));
                }

                return FailSends
                    ? Task.FromException(new InvalidOperationException("smtp unavailable"))
                    : Task.CompletedTask;
            });

        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new NoNetwork()));
        var extendedLog = new ExtendedLogService(
            Config, Env, TestConfigStore.CreateAppSettings(_root), NullLogger<ExtendedLogService>.Instance);
        var delinea = new DelineaService(http, Config, NullLogger<DelineaService>.Instance, extendedLog, Trace);
        var credentials = new ModuleCredentialService(ModuleConfig, delinea, NullLogger<ModuleCredentialService>.Instance);

        Protection = new FakeProtection(Env, Config, ModuleConfig, TestConfigStore.CreateProtectedPrincipal(_root), delinea);
        Service = new HarnessService(this, credentials, Protection, Servicers, Trace, Audit, Email, ModuleConfig, Allowlist, Env, Logger);
    }

    public IConfiguration Config { get; }
    public IWebHostEnvironment Env { get; }
    public ModuleConfigService ModuleConfig { get; }
    public AttributeEditorRepository Allowlist { get; }
    public SwitchableStore SectionStore { get; }
    public ProtectedPrincipalServicerService Servicers { get; }
    public OperationTraceService Trace { get; }
    public AuditService Audit { get; }
    public EmailService Email { get; }
    public FakeProtection Protection { get; }
    public CapturingLogger Logger { get; } = new();
    public HarnessService Service { get; }

    /// <summary>The stand-in directory the harness session's AD functions read and write.</summary>
    public LocalAd Ad { get; } = new();

    /// <summary>False makes the credential seam return nothing, as an unconfigured secret does.</summary>
    public bool CredentialsAvailable { get; set; } = true;

    /// <summary>True makes every notification send fail, as an unreachable mail server does.</summary>
    public bool FailSends { get; set; }

    private readonly List<Notice> _notices = [];

    /// <summary>Every administrator notice sent, in order.</summary>
    public IReadOnlyList<Notice> Notices
    {
        get { lock (_notices) return _notices.ToList(); }
    }

    public sealed record Notice(
        string PerformedBy, string Ip, string Action, bool Success, string Ticket,
        IReadOnlyDictionary<string, string> Details, string? Error);

    /// <summary>An authenticated operator holding <paramref name="groupSids"/> (default: the module's access group).</summary>
    public static ClaimsPrincipal Operator(params string[] groupSids)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, "EXAMPLE\\operator") };
        var groups = groupSids.Length == 0 ? new[] { OperatorSid } : groupSids;
        claims.AddRange(groups.Select(sid => new Claim(ClaimTypes.Role, sid)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    /// <summary>The principal a lookup of <paramref name="user"/> would have resolved.</summary>
    public static ResolvedDirectoryPrincipal PrincipalFor(LocalAd.User user) =>
        new("OnPremAD", user.DisplayName, user.Upn, user.Sam, user.Mail, user.Dn, user.Guid.ToString(), null);

    public void ConfigureSearchBases(params string[] searchBases) =>
        ModuleConfig.SaveModuleConfig("ADAttributeEditor", new Dictionary<string, string>
        {
            ["DefaultSearchBase"] = string.Join(";", searchBases),
        });

    /// <summary>
    /// Leaves a row the allowlist cannot parse, which is the store's corrupt state: the service's
    /// load returns null and every save refuses.
    /// </summary>
    public void CorruptAllowlist()
    {
        _allowlistStore.Write((connection, transaction) =>
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO editable_attribute (name, label, type, choices_json, required, allow_clear, level) "
                + "VALUES ('employeeType', 'Employee Type', 'Choice', '{not valid json', 0, 1, 1);";
            insert.ExecuteNonQuery();
        });
        Service.InvalidateAllowlistCache();
    }

    /// <summary>Authorises <see cref="ServicerSid"/> to service protected principals in this module.</summary>
    public void GrantServicer() =>
        _sections.SaveAll(new Dictionary<string, string[]>
        {
            ["ADAttributeEditor"] = [OperatorSid],
            [ProtectedPrincipalServicerService.SectionKeyFor("ADAttributeEditor")] = [ServicerSid],
        });

    /// <summary>
    /// The audit record a successful forward edit of <paramref name="attribute"/> leaves, as the
    /// event log hands it to undo.
    /// </summary>
    public static Dictionary<string, object?> ForwardEdit(LocalAd.User user, string attribute, string? from, string? to) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["category"] = "ADAttributeEditor",
            ["action"] = "ADAttributeEditor_Update",
            ["result"] = "Success",
            ["target"] = user.Upn,
            ["operationId"] = "op-forward-1",
            ["targetObjectGuid"] = user.Guid.ToString(),
            ["changedAttributes"] = new[] { attribute },
            [$"old_{attribute}"] = from,
            [$"new_{attribute}"] = to,
            ["_undoMaxLevel"] = 3,
        };

    public ADAttributeEditorUndoService CreateUndoService()
    {
        var enablementRepository = TestConfigStore.CreateModuleEnablement(_root);
        enablementRepository.SaveAll(new Dictionary<string, bool> { ["ADAttributeEditor"] = true });
        var enablement = new ModuleEnablementService(
            new ModuleCatalog(), Env, ModuleConfig, enablementRepository, Config, NullLogger<ModuleEnablementService>.Instance);

        return new ADAttributeEditorUndoService(
            Service, Protection, Servicers, enablement, Audit, Trace, Email,
            NullLogger<ADAttributeEditorUndoService>.Instance);
    }

    /// <summary>Every audit record written so far, oldest first.</summary>
    public IReadOnlyList<JsonElement> AuditEvents()
    {
        var folder = Path.Combine(_root, "ExchangeAdminWeb");
        var events = new List<JsonElement>();
        if (!Directory.Exists(folder))
            return events;

        var files = Directory.GetFiles(folder, "exchangeadmin_*.jsonl")
            .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith("_trace", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.Ordinal);

        foreach (var line in files.SelectMany(File.ReadAllLines).Where(l => !string.IsNullOrWhiteSpace(l)))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("eventType", out var type) && type.GetString() == "audit")
                events.Add(root.Clone());
        }

        return events;
    }

    public void Dispose()
    {
        SqliteConnection.ClearPool(new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_root, "exchangeadmin.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString()));

        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort: a pooled connection can still hold the file */ }
    }

    /// <summary>The service under test, with only its two seams replaced.</summary>
    internal sealed class HarnessService : ADAttributeEditorService
    {
        private readonly AttributeEditorHarness _harness;
        private int _sessions;

        public HarnessService(
            AttributeEditorHarness harness,
            ModuleCredentialService credentials,
            ProtectedPrincipalService protection,
            ProtectedPrincipalServicerService servicers,
            OperationTraceService trace,
            AuditService audit,
            EmailService email,
            ModuleConfigService moduleConfig,
            AttributeEditorRepository repository,
            IWebHostEnvironment env,
            ILogger<ADAttributeEditorService> logger)
            : base(credentials, protection, servicers, trace, audit, email, moduleConfig, repository, env, logger)
        {
            _harness = harness;
        }

        /// <summary>How many directory sessions the service has opened: zero means it never got that far.</summary>
        public int SessionsOpened => Volatile.Read(ref _sessions);

        internal override Runspace CreateDirectoryRunspace()
        {
            Interlocked.Increment(ref _sessions);
            return RunspaceFactory.CreateRunspace(_harness.Ad.CreateSessionState());
        }

        internal override Task<(string username, string password, string domain)?> GetCredentialsAsync(string purpose) =>
            Task.FromResult<(string username, string password, string domain)?>(
                _harness.CredentialsAvailable ? ("svc-attribute-editor", "not-a-secret", "EXAMPLE") : null);
    }

    /// <summary>A protected-principal service whose decision the test sets.</summary>
    internal sealed class FakeProtection : ProtectedPrincipalService
    {
        public FakeProtection(
            IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
            ProtectedPrincipalRepository repository, DelineaService delinea)
            : base(env, config, moduleConfig, repository, delinea, NullLogger<ProtectedPrincipalService>.Instance)
        {
        }

        public Func<ResolvedDirectoryPrincipal, ProtectedPrincipalResult> Decide { get; set; } =
            _ => ProtectedPrincipalResult.NotProtected();

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target) =>
            Task.FromResult(Decide(target));
    }

    /// <summary>A config store whose reads can be made to fail, as an unreadable database does.</summary>
    internal sealed class SwitchableStore(IConfigStore inner) : IConfigStore
    {
        public bool ReadsFail { get; set; }

        public long GetChangeToken() => inner.GetChangeToken();

        public T Read<T>(Func<SqliteConnection, T> read) =>
            ReadsFail ? throw new InvalidOperationException("config store unreadable") : inner.Read(read);

        public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write) => inner.Write(write);

        public void Write(Action<SqliteConnection, SqliteTransaction> write) => inner.Write(write);
    }

    /// <summary>Records warnings, so a test can say which base a lookup reported as failed.</summary>
    internal sealed class CapturingLogger : ILogger<ADAttributeEditorService>
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get { lock (_warnings) return _warnings.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Warning)
                return;

            lock (_warnings)
                _warnings.Add(formatter(state, exception));
        }
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("this harness has no network");
    }
}

/// <summary>
/// The stand-in for Active Directory behind <see cref="AttributeEditorHarness"/>.
/// </summary>
/// <remarks>
/// The session functions are ADVANCED functions on purpose, so <c>-ErrorAction</c> is the real
/// common parameter and a failure takes the same path a compiled cmdlet's does:
/// <c>$PSCmdlet.ThrowTerminatingError</c> throws out of Invoke whatever the ErrorAction, and
/// <c>$PSCmdlet.WriteError</c> under SilentlyContinue writes no record but still sets HadErrors
/// (both checked against this SDK, 2026-10-21). A script <c>throw</c> would not do: under
/// SilentlyContinue an advanced function swallows it, and a failure the code under test never
/// sees proves nothing.
/// </remarks>
internal sealed class LocalAd
{
    private readonly List<User> _users = [];
    private readonly List<string> _calls = [];
    private readonly List<SetCall> _sets = [];

    public sealed class User
    {
        public required Guid Guid { get; init; }
        public required string Dn { get; init; }
        public required string Upn { get; init; }
        public string? Sam { get; init; }
        public string? Mail { get; init; }
        public string DisplayName { get; init; } = "";
        public Dictionary<string, string?> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed record SetCall(string Identity, IReadOnlyDictionary<string, string?> Replace, IReadOnlyList<string> Clear);

    /// <summary>
    /// Per search base, how Get-ADUser fails there: <c>"terminating"</c> (the cmdlet's
    /// ThrowTerminatingError - an unreachable server, a base that does not exist),
    /// <c>"nonterminating"</c> (its WriteError, and nothing returned), <c>"partial"</c> (its
    /// WriteError, and the matches returned anyway) or <c>"recorded"</c> (an error RECORD left on
    /// the stream despite SilentlyContinue, as a nested command's error is, and nothing returned).
    /// </summary>
    public Dictionary<string, string> FailingBases { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>When set, Set-ADUser fails with this message as a terminating error.</summary>
    public string? SetFailure { get; set; }

    /// <summary>When set, Import-Module fails with this message, as on a host without the module.</summary>
    public string? ImportFailure { get; set; }

    public IReadOnlyList<string> Calls
    {
        get { lock (_calls) return _calls.ToList(); }
    }

    public IReadOnlyList<SetCall> Sets
    {
        get { lock (_sets) return _sets.ToList(); }
    }

    public User Add(string upn, string dn, string displayName, params (string Name, string? Value)[] attributes)
    {
        var user = new User
        {
            Guid = Guid.NewGuid(),
            Dn = dn,
            Upn = upn,
            Sam = upn.Split('@')[0],
            Mail = upn,
            DisplayName = displayName,
        };

        foreach (var (name, value) in attributes)
            user.Attributes[name] = value;

        return Add(user);
    }

    public User Add(User user)
    {
        lock (_users)
            _users.Add(user);

        return user;
    }

    /// <summary>The usual subject: an ordinary staff account with a title and a department.</summary>
    public User AddAlex(string title = "Analyst", string ou = "OU=Staff,DC=example,DC=test") =>
        Add("alex.morgan@example.test", $"CN=Alex Morgan,{ou}", "Alex Morgan",
            ("title", title), ("department", "Finance"));

    // ---- called from the session functions ------------------------------------------------

    public void Note(string call)
    {
        lock (_calls)
            _calls.Add(call);
    }

    public string FailureFor(string searchBase) =>
        !string.IsNullOrEmpty(searchBase) && FailingBases.TryGetValue(searchBase, out var mode) ? mode : "";

    /// <summary>
    /// The users a Get-ADUser call returns: by <paramref name="identity"/> (DN or GUID) when one
    /// is given, otherwise by the lookup's LDAP filter within <paramref name="searchBase"/>.
    /// </summary>
    public PSObject[] Find(string identity, string ldapFilter, string searchBase)
    {
        List<User> matches;
        lock (_users)
        {
            matches = !string.IsNullOrEmpty(identity)
                ? _users.Where(u => string.Equals(u.Dn, identity, StringComparison.OrdinalIgnoreCase)
                                    || string.Equals(u.Guid.ToString(), identity, StringComparison.OrdinalIgnoreCase)).ToList()
                : _users.Where(u => MatchesFilter(u, ldapFilter) && WithinBase(u, searchBase)).ToList();
        }

        return matches.Select(ToObject).ToArray();
    }

    public void RecordSet(string identity, object? replace, object? clear)
    {
        var replaced = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (Unwrap(replace) is IDictionary map)
        {
            foreach (DictionaryEntry entry in map)
                replaced[entry.Key.ToString()!] = Unwrap(entry.Value)?.ToString();
        }

        var cleared = new List<string>();
        switch (Unwrap(clear))
        {
            case string one:
                cleared.Add(one);
                break;
            case IEnumerable names:
                foreach (var name in names)
                    cleared.Add(Unwrap(name)?.ToString() ?? "");
                break;
        }

        lock (_sets)
            _sets.Add(new SetCall(identity, replaced, cleared));

        lock (_users)
        {
            var user = _users.FirstOrDefault(u => string.Equals(u.Dn, identity, StringComparison.OrdinalIgnoreCase));
            if (user is null)
                return;

            foreach (var (name, value) in replaced)
                user.Attributes[name] = value;
            foreach (var name in cleared)
                user.Attributes[name] = null;
        }
    }

    // ---- the session -----------------------------------------------------------------------

    public InitialSessionState CreateSessionState()
    {
        // CreateDefault, as production builds, not CreateDefault2: with autoloading off the latter
        // has no Utility cmdlets, and a stand-in reaching for one (Write-Error) fails with
        // command-not-found - which SilentlyContinue then swallows, so the case proves nothing.
        var state = InitialSessionState.CreateDefault();
        state.Variables.Add(new SessionStateVariableEntry("AttributeDirectory", this, "local stand-in for the directory"));
        // Belt and braces: a command the code under test reaches that is not defined below fails
        // as not found instead of loading the real ActiveDirectory module.
        state.Variables.Add(new SessionStateVariableEntry("PSModuleAutoloadingPreference", "None", "no autoloading"));

        state.Commands.Add(new SessionStateFunctionEntry("Import-Module", """
            [CmdletBinding()] param([Parameter(Position = 0)] $Name)
            $global:AttributeDirectory.Note("Import-Module|$Name")
            if ($global:AttributeDirectory.ImportFailure) {
                $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
                    [System.IO.FileNotFoundException]::new($global:AttributeDirectory.ImportFailure),
                    'Modules_ModuleNotFound', [System.Management.Automation.ErrorCategory]::ResourceUnavailable, $Name))
            }
            """));

        state.Commands.Add(new SessionStateFunctionEntry("Get-ADUser", """
            [CmdletBinding()] param($Identity, $LDAPFilter, $SearchBase, $Properties, $Credential)
            $directory = $global:AttributeDirectory
            $directory.Note("Get-ADUser|$Identity|$SearchBase")
            $failure = $directory.FailureFor([string]$SearchBase)
            if ($failure -eq 'terminating') {
                $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
                    [System.InvalidOperationException]::new("The server is not operational: $SearchBase"),
                    'BaseUnavailable', [System.Management.Automation.ErrorCategory]::ResourceUnavailable, $SearchBase))
            }
            if ($failure -in 'nonterminating', 'partial') {
                $PSCmdlet.WriteError([System.Management.Automation.ErrorRecord]::new(
                    [System.InvalidOperationException]::new("A referral was returned from the server: $SearchBase"),
                    'BaseIncomplete', [System.Management.Automation.ErrorCategory]::ReadError, $SearchBase))
                if ($failure -eq 'nonterminating') { return }
            }
            if ($failure -eq 'recorded') {
                Write-Error -Message "Access to the search base was denied: $SearchBase" -ErrorAction Continue
                return
            }
            $found = $directory.Find([string]$Identity, [string]$LDAPFilter, [string]$SearchBase)
            if ($Identity -and $found.Length -eq 0) {
                $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
                    [System.InvalidOperationException]::new("Cannot find an object with identity: '$Identity'"),
                    'ObjectNotFound', [System.Management.Automation.ErrorCategory]::ObjectNotFound, $Identity))
            }
            $found
            """));

        state.Commands.Add(new SessionStateFunctionEntry("Set-ADUser", """
            [CmdletBinding()] param($Identity, $Credential, $Replace, $Clear)
            $directory = $global:AttributeDirectory
            $directory.Note("Set-ADUser|$Identity")
            if ($directory.SetFailure) {
                $PSCmdlet.ThrowTerminatingError([System.Management.Automation.ErrorRecord]::new(
                    [System.InvalidOperationException]::new($directory.SetFailure),
                    'WriteRefused', [System.Management.Automation.ErrorCategory]::PermissionDenied, $Identity))
            }
            $directory.RecordSet([string]$Identity, $Replace, $Clear)
            """));

        return state;
    }

    private static bool MatchesFilter(User user, string filter) =>
        !string.IsNullOrEmpty(filter)
        && (Names(filter, "userPrincipalName", user.Upn)
            || Names(filter, "mail", user.Mail)
            || Names(filter, "sAMAccountName", user.Sam));

    private static bool Names(string filter, string attribute, string? value) =>
        !string.IsNullOrEmpty(value) && filter.Contains($"({attribute}={value})", StringComparison.OrdinalIgnoreCase);

    private static bool WithinBase(User user, string searchBase) =>
        string.IsNullOrEmpty(searchBase)
        || user.Dn.EndsWith("," + searchBase, StringComparison.OrdinalIgnoreCase)
        || string.Equals(user.Dn, searchBase, StringComparison.OrdinalIgnoreCase);

    private static PSObject ToObject(User user)
    {
        var result = new PSObject();
        result.Properties.Add(new PSNoteProperty("ObjectGUID", user.Guid));
        result.Properties.Add(new PSNoteProperty("DistinguishedName", user.Dn));
        result.Properties.Add(new PSNoteProperty("DisplayName", user.DisplayName));
        result.Properties.Add(new PSNoteProperty("Name", user.DisplayName));
        result.Properties.Add(new PSNoteProperty("UserPrincipalName", user.Upn));
        result.Properties.Add(new PSNoteProperty("SamAccountName", user.Sam));
        result.Properties.Add(new PSNoteProperty("mail", user.Mail));
        foreach (var (name, value) in user.Attributes)
            result.Properties.Add(new PSNoteProperty(name, value));
        return result;
    }

    private static object? Unwrap(object? value) => value is PSObject wrapped ? wrapped.BaseObject : value;
}
