using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a23h. A stand-in Exchange Online for the Migration module: the real <see cref="MigrationService"/> on a real
/// <see cref="ExoConnectionPool"/>, whose connect seam hands out an in-process runspace of stub cmdlets over in-memory
/// migration users, mailboxes and batches, and the Migration page driven through its compiled render tree and its
/// handlers. No Exchange, no directory.
/// </summary>
/// <remarks>
/// The stubs are advanced functions, so -ErrorAction binds as a common parameter and Stop and Ignore do to them what
/// they do to a cmdlet. Each logs the step it was asked to run ("Set-MigrationBatch -CompleteAfter
/// alice@contoso.test"), then fails as <see cref="Failures"/> says - a string is thrown, a factory's ErrorRecord is
/// written - or answers. A lookup for an identity it does not hold writes <see cref="NotFound"/>'s record.
/// </remarks>
internal sealed class MigrationStubExchange : IDisposable
{
    public const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "migration-stub-" + Guid.NewGuid().ToString("N"));
    private readonly IConfiguration _config;
    private readonly ModuleConfigService _moduleConfig;
    private readonly ModuleEnablementService _enablement;
    private readonly OperationTraceService _trace;
    private readonly List<ExoConnectionPool> _pools = [];

    public MigrationStubExchange()
    {
        Directory.CreateDirectory(_directory);
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _directory,
            ["ExchangeOnline:AppId"] = "11111111-1111-1111-1111-111111111111",
            ["ExchangeOnline:Organization"] = "contoso.onmicrosoft.com",
        }).Build();
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(_directory);
        Environment = environment;
        var store = TestConfigStore.Create(_directory);
        var catalog = new ModuleCatalog();
        _moduleConfig = new ModuleConfigService(catalog, environment,
            new ModuleConfigRepository(store), NullLogger<ModuleConfigService>.Instance);
        _enablement = new ModuleEnablementService(catalog, environment, _moduleConfig,
            new ModuleEnablementRepository(store), _config, NullLogger<ModuleEnablementService>.Instance);
        _enablement.SaveEnablement(new Dictionary<string, bool> { [ExoConnectionPool.ConfigModuleKey] = true });
        var log = new JsonlLogService(_config, NullLogger<JsonlLogService>.Instance);
        _trace = new OperationTraceService(_config, log);
        Audit = new AuditService(log, _trace);
    }

    public IWebHostEnvironment Environment { get; }

    public AuditService Audit { get; }

    /// <summary>How a step fails instead of answering, keyed "step identity" or "step": a string, or a factory.</summary>
    public Hashtable Failures { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>Get-MigrationUser -Identity answers these.</summary>
    public Hashtable MigrationUsers { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>Get-MigrationUser -BatchId answers these: batch name to a list of rows.</summary>
    public Hashtable BatchUsers { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>Get-Mailbox -Identity answers these.</summary>
    public Hashtable Mailboxes { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>Get-MigrationBatch answers these; Set-MigrationBatch -CompleteAfter writes their CompleteAfter.</summary>
    public Hashtable Batches { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Switches: SetBatchChangesNothing - Set-MigrationBatch reports success and writes nothing; ReadBackRefused (a factory) -
    /// Get-MigrationBatch refuses a batch once it holds a completion time, so only a read-back fails;
    /// NestedSuppressedError - Remove-BlockedSenderAddress suppresses an error of its own.
    /// </summary>
    public Hashtable Behaviour { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>Every step a stub was asked to run, in order.</summary>
    public ArrayList Calls { get; } = ArrayList.Synchronized(new ArrayList());

    /// <summary>The record a lookup writes for an identity it does not hold. Defaults to the module's FQID form.</summary>
    public Func<string, ErrorRecord> NotFound { get; set; } = NotFoundByIdentifier;

    /// <summary>Cmdlets the session does not have: a call to one is PowerShell's CommandNotFoundException.</summary>
    public HashSet<string> Missing { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ---- the shapes of an error ------------------------------------------------------------------

    /// <summary>A refusal: no category, an identifier that names nothing.</summary>
    public static Func<string, ErrorRecord> Refused(string message) => identity =>
        new ErrorRecord(new InvalidOperationException(message), "StubRefused", ErrorCategory.NotSpecified, identity);

    /// <summary>PowerShell's own "not found": the ObjectNotFound category.</summary>
    public static ErrorRecord NotFoundByCategory(string identity) =>
        new(new InvalidOperationException($"No object {identity} exists."), "StubLookup", ErrorCategory.ObjectNotFound, identity);

    /// <summary>Exchange's not-found exception type as the record's reason, under no category.</summary>
    public static ErrorRecord NotFoundByType(string identity) =>
        new(new ManagementObjectNotFoundException($"No object {identity} exists."), "StubLookup", ErrorCategory.NotSpecified, identity);

    /// <summary>The Exchange Online module's form: no category, the exception type in the FQID's FailureCategory.</summary>
    public static ErrorRecord NotFoundByIdentifier(string identity) =>
        new(new InvalidOperationException($"The operation couldn't be performed because object '{identity}' couldn't be found on 'AB1PR01MB0001.PROD.OUTLOOK.COM'."),
            "[Server=AB1PR01MB0001,RequestId=00000000-0000-0000-0000-000000000000,TimeStamp=10/22/2026 12:00:00 PM] "
            + "[FailureCategory=Cmdlet-ManagementObjectNotFoundException] 2B5E15D7,Microsoft.Exchange.Management.RecipientTasks.GetMailbox",
            ErrorCategory.NotSpecified, identity);

    /// <summary>Named like Exchange's: a record whose reason is this type is Exchange saying there is no such object.</summary>
    public sealed class ManagementObjectNotFoundException(string message) : Exception(message);

    // ---- rows ------------------------------------------------------------------------------------

    public static PSObject Row(params (string Name, object? Value)[] fields)
    {
        var row = new PSObject();
        foreach (var (name, value) in fields)
            row.Properties.Add(new PSNoteProperty(name, value));
        return row;
    }

    public static PSObject CloudMailbox(string address) => Row(("PrimarySmtpAddress", address), ("RecipientTypeDetails", "UserMailbox"));

    public static PSObject Batch(string name, object? completeAfter = null) =>
        Row(("Identity", name), ("Status", "Synced"), ("TotalCount", 1), ("CompleteAfter", completeAfter));

    public string[] CallList() => Calls.Cast<string>().ToArray();

    // ---- the service and the page ----------------------------------------------------------------

    public OperationTraceService Trace => _trace;

    /// <param name="connects">False: opening the connection throws, before any cmdlet runs.</param>
    public ExoConnectionPool Pool(bool connects = true)
    {
        var pool = new ExoConnectionPool(_config, _moduleConfig, _enablement,
            NullLogger<ExoConnectionPool>.Instance, _trace, (_, generation, _) =>
            {
                if (!connects)
                    throw new InvalidOperationException("the Exchange Online sign-in could not be completed");
                return StandIn(generation);
            });
        _pools.Add(pool);
        return pool;
    }

    /// <param name="connects">False: opening the connection throws, before any cmdlet runs.</param>
    public MigrationService Service(bool connects = true) =>
        new(_config, Pool(connects), null!, NullLogger<MigrationService>.Instance, _moduleConfig, null!, _trace, null!, null!);

    private PooledRunspace StandIn(long generation)
    {
        var state = InitialSessionState.CreateDefault2();
        state.Variables.Add(new SessionStateVariableEntry("Failures", Failures, null));
        state.Variables.Add(new SessionStateVariableEntry("MigrationUsers", MigrationUsers, null));
        state.Variables.Add(new SessionStateVariableEntry("BatchUsers", BatchUsers, null));
        state.Variables.Add(new SessionStateVariableEntry("Mailboxes", Mailboxes, null));
        state.Variables.Add(new SessionStateVariableEntry("Batches", Batches, null));
        state.Variables.Add(new SessionStateVariableEntry("Behaviour", Behaviour, null));
        state.Variables.Add(new SessionStateVariableEntry("Calls", Calls, null));
        state.Variables.Add(new SessionStateVariableEntry("NotFound", NotFound, null));
        state.Commands.Add(new SessionStateFunctionEntry("Disconnect-ExchangeOnline",
            "[CmdletBinding()] param($ConnectionId, [switch] $Confirm)"));

        foreach (var (name, parameters, answer) in Stubs)
        {
            if (!Missing.Contains(name))
                state.Commands.Add(new SessionStateFunctionEntry(name, Stub(name, parameters, answer)));
        }

        var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.Open();
        var powershell = PowerShell.Create();
        powershell.Runspace = runspace;
        return new PooledRunspace(runspace, powershell, generation);
    }

    private const string Lookup = "$PSCmdlet.WriteError($NotFound.Invoke([string]$Identity))";

    private static readonly (string Name, string Parameters, string Answer)[] Stubs =
    [
        ("Get-MigrationUser", "[string] $Identity, [string] $BatchId, [string] $ResultSize", $$"""
            if ($BatchId) { if ($BatchUsers.ContainsKey($BatchId)) { foreach ($row in $BatchUsers[$BatchId]) { $row } }; return }
            if (-not $Identity) { foreach ($row in $MigrationUsers.Values) { $row }; return }
            if ($MigrationUsers.ContainsKey($Identity)) { return $MigrationUsers[$Identity] }
            {{Lookup}}
            """),
        ("Get-Mailbox", "[string] $Identity", $$"""
            if ($Mailboxes.ContainsKey($Identity)) { return $Mailboxes[$Identity] }
            {{Lookup}}
            """),
        ("Get-MigrationBatch", "[string] $Identity", $$"""
            if (-not $Identity) { foreach ($row in $Batches.Values) { $row }; return }
            if ($null -ne $Behaviour['ReadBackRefused'] -and $Batches.ContainsKey($Identity) -and $null -ne $Batches[$Identity].CompleteAfter) {
                $PSCmdlet.WriteError($Behaviour['ReadBackRefused'].Invoke([string]$Identity)); return
            }
            if ($Batches.ContainsKey($Identity)) { return $Batches[$Identity] }
            {{Lookup}}
            """),
        ("Set-MigrationBatch", "[string] $Identity, [object] $ApproveSkippedItems, [object] $CompleteAfter", $$"""
            if (-not $Batches.ContainsKey($Identity)) { {{Lookup}}; return }
            if ($PSBoundParameters.ContainsKey('CompleteAfter') -and -not $Behaviour['SetBatchChangesNothing']) {
                $Batches[$Identity].CompleteAfter = $CompleteAfter
            }
            """),
        ("Set-MigrationUser", "[string] $Identity, [object] $ApproveSkippedItems, [object] $CompleteAfter, [switch] $Confirm", ""),
        ("Set-MoveRequest", "[string] $Identity, [object] $SkippedItemApprovalTime", ""),
        ("Resume-MoveRequest", "[string] $Identity", ""),
        ("Complete-MigrationBatch", "[string] $Identity, [switch] $Confirm", $$"""
            if (-not $Batches.ContainsKey($Identity)) { {{Lookup}}; return }
            """),
        ("Stop-MigrationUser", "[string] $Identity, [switch] $Confirm", ""),
        ("New-MigrationBatch", "[string] $Name, [object] $CSVData, [object] $TargetDeliveryDomain, [object] $AutoStart, [object] $AutoComplete", ""),
        ("Get-MailboxStatistics", "[string] $Identity, [switch] $Archive", """
            [pscustomobject]@{ TotalItemSize = '1.5 GB (1,610,612,736 bytes)' }
            """),
        ("Get-MigrationUserStatistics", "[string] $Identity, [object] $IncludeReport", """
            [pscustomobject]@{ Status = 'Synced'; Report = 'the move report' }
            """),
        // aud-a23i. A Blocked Senders write, for the base class's failure message outside Migration. NestedSuppressedError:
        // a command inside it fails under -ErrorAction SilentlyContinue, which flags the call and writes no record.
        ("Remove-BlockedSenderAddress", "[string] $SenderAddress, [string] $Reason", """
            if ($Behaviour['NestedSuppressedError']) { Get-Item 'C:\definitely-not-here-a23i' -ErrorAction SilentlyContinue }
            """),
    ];

    // Logs the step, then fails as the table says - a string thrown, a factory's record written - or answers.
    private static string Stub(string name, string parameters, string answer) => $$"""
        [CmdletBinding()]
        param({{parameters}})
        $switches = @($PSBoundParameters.Keys | Where-Object { $_ -notin 'Identity', 'BatchId', 'ResultSize', 'IncludeReport', 'ErrorAction', 'Confirm', 'Archive', 'Name', 'CSVData', 'TargetDeliveryDomain', 'AutoStart', 'AutoComplete', 'SenderAddress', 'Reason' } | ForEach-Object { " -$_" }) -join ''
        $step = '{{name}}' + $switches
        $null = $Calls.Add(("$step $Identity").Trim())
        $failure = $Failures["$step $Identity"]
        if ($null -eq $failure) { $failure = $Failures[$step] }
        if ($null -ne $failure) {
            if ($failure -is [string]) { throw $failure; return }
            $PSCmdlet.WriteError($failure.Invoke([string]$Identity)); return
        }
        {{answer}}
        """;

    /// <summary>The Migration page over <paramref name="service"/>, its injected services set by hand.</summary>
    public RenderableMigration Page(MigrationService service)
    {
        var page = new RenderableMigration();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "CONTOSO\\operator")], "test"));
        var auth = Substitute.For<AuthenticationStateProvider>();
        auth.GetAuthenticationStateAsync().Returns(new AuthenticationState(principal));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        Set(page, "MigrationSvc", service);
        Set(page, "Audit", Audit);
        Set(page, "Email", new EmailService(_config, NullLogger<EmailService>.Instance));
        Set(page, "AuthStateProvider", auth);
        Set(page, "AuthorizationService", authorization);
        Set(page, "ReportStore", new MigrationReportStore(Environment));
        Set(page, "Logger", NullLogger<Migration>.Instance);
        Set(page, "Progress", new ExchangeAdminWeb.Services.Progress.ActivityProgressService());
        return page;
    }

    public static void Set(Migration page, string property, object? value) =>
        typeof(Migration).GetProperty(property, Members)!.SetValue(page, value);

    public static FieldInfo Field(string name) =>
        typeof(Migration).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    public static Task Call(Migration page, string method, params object?[] arguments) =>
        (Task)typeof(Migration).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, arguments)!;

    /// <summary>Every audit event written so far, oldest first.</summary>
    public IReadOnlyList<JsonElement> AuditEvents()
    {
        var folder = Path.Combine(_directory, "ExchangeAdminWeb");
        if (!Directory.Exists(folder))
            return [];
        return Directory.GetFiles(folder, "exchangeadmin_*.jsonl")
            .Where(file => !Path.GetFileNameWithoutExtension(file).EndsWith("_trace", StringComparison.OrdinalIgnoreCase))
            .SelectMany(File.ReadAllLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();
    }

    public void Dispose()
    {
        foreach (var pool in _pools)
            pool.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>The page with its compiled render tree readable as text.</summary>
    internal sealed class RenderableMigration : Migration
    {
#pragma warning disable BL0006 // the page's compiled render tree is read directly; no renderer is involved
        public string RenderText()
        {
            using var builder = new RenderTreeBuilder();
            BuildRenderTree(builder);
            var frames = builder.GetFrames();
            return string.Concat(frames.Array.Take(frames.Count).Select(frame => frame.FrameType switch
            {
                RenderTreeFrameType.Text => frame.TextContent,
                RenderTreeFrameType.Markup => frame.MarkupContent,
                _ => "",
            }));
        }
#pragma warning restore BL0006
    }

    /// <summary>aud-a23i. A progress channel that keeps how each activity ended: the first Complete wins, as the real one's does.</summary>
    internal sealed class RecordingProgress : IActivityProgress
    {
        private readonly List<RecordingHandle> _handles = [];

        public IReadOnlyList<RecordingHandle> Handles
        {
            get { lock (_handles) return _handles.ToList(); }
        }

        public IActivityHandle Begin(string moduleId, string label, ActivitySize size)
        {
            var handle = new RecordingHandle(label);
            lock (_handles) _handles.Add(handle);
            return handle;
        }

        public IReadOnlyList<ProgressActivity> Active => [];

        public bool HasActive => false;

        public void Cancel(string id) { }

        public void CancelAll() { }

        public event Action? Changed { add { } remove { } }
    }

    internal sealed class RecordingHandle(string label) : IActivityHandle
    {
        public string Label { get; } = label;

        public (bool Success, string? Message)? Outcome { get; private set; }

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public CancellationToken CancellationToken => CancellationToken.None;

        public void Report(int done, string? detail = null) { }

        public void Step(string detail) { }

        public void Complete(bool success, string? message = null) => Outcome ??= (success, message);

        public IProgress<ActivityUpdate> AsProgress() => new Progress<ActivityUpdate>(_ => { });

        public void Dispose() => Complete(false, "This did not finish.");
    }

    /// <summary>aud-a23i. A browser that records the name of every file the page hands it.</summary>
    internal sealed class RecordingJs : IJSRuntime
    {
        public List<string> FileNames { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("downloadFile", identifier);
            FileNames.Add(Assert.IsType<string>(args![0]));
            return ValueTask.FromResult(default(TValue)!);
        }
    }
}
