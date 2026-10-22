using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a2d's harness: the Exchange writes of the four Exchange-written modules, driven for real
/// against stand-in cmdlets that record exactly what they were bound with.
/// </summary>
/// <remarks>
/// A real <see cref="ExoConnectionPool"/> over a temp SQLite config store; only
/// Connect-ExchangeOnline is replaced, through the internal connect seam, by an opened local
/// runspace whose functions stand in for the Exchange cmdlets (the psl-1 shape, as in
/// <see cref="ExchangeServiceBasePooledQueryTests"/>). <c>Get-Mailbox</c> and the other reads answer
/// from <see cref="Mailboxes"/>; every cmdlet records its bound parameters in <see cref="Calls"/>.
/// The on-prem and Active Directory paths get the same stand-ins through their own test seams
/// (<see cref="DefineOnPremSession"/>, <see cref="OpenDirectoryRunspace"/>). No Exchange module,
/// credential, directory or network connection is used.
/// </remarks>
internal sealed class ExchangeWriteHarness : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "eaw-a2d-" + Guid.NewGuid().ToString("N"));
    public IConfiguration Config { get; }
    public ModuleConfigService ModuleConfig { get; }
    public ModuleEnablementService Enablement { get; }
    public OperationTraceService Trace { get; }
    public JsonlLogService Jsonl { get; }
    public AuditService Audit { get; }
    public ExoConnectionPool Pool { get; }

    /// <summary>Every stand-in call in order: a Hashtable of its bound parameters plus <c>__Command</c>.</summary>
    public IList Calls { get; } = ArrayList.Synchronized(new ArrayList());

    /// <summary>
    /// What the stand-in reads answer, keyed by any identity that resolves to the entry - the GUID,
    /// the primary address and any alias registered. Case-insensitive, as Exchange is.
    /// </summary>
    public Hashtable Mailboxes { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>What the stand-in <c>Get-ADUser -Identity</c> answers, keyed by objectGUID.</summary>
    public Hashtable DirectoryObjects { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>Command name to error message: that stand-in records its call, then throws the message.</summary>
    /// <remarks>
    /// Only for a command the code calls with <c>-ErrorAction Stop</c> (or none). Called with
    /// <c>SilentlyContinue</c>, PowerShell suppresses the throw and the stand-in carries on and answers.
    /// </remarks>
    public Hashtable Failures { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The primary addresses the stand-in <c>Get-DistributionGroupMember</c> reports on any list: all of
    /// them to <c>-ResultSize Unlimited</c>, the first N to a number, and none - an error - to a read
    /// without the parameter (aud-a2h).
    /// </summary>
    public IList ListMembers { get; } = ArrayList.Synchronized(new ArrayList());

    /// <summary>
    /// Exchange Online's default page for <c>Get-DistributionGroupMember</c>: the most members a read
    /// without <c>-ResultSize</c> returns. A test list one longer has a member only the whole list shows.
    /// </summary>
    public const int ExchangeDefaultResultSize = 1000;

    /// <summary>
    /// What the stand-in <c>Get-DistributionGroup</c> reports as <c>IsDirSynced</c>, by list name: a
    /// bool, or null to leave the property out. A list not named here reports False - a cloud list.
    /// </summary>
    public Hashtable ListSyncState { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>The objectGUID of the one group the stand-in <c>Get-ADGroup</c> search returns unless a test changes it.</summary>
    public const string DefaultDirectoryGroupGuid = "99999999-9999-9999-9999-999999999999";

    /// <summary>The objectGUIDs the stand-in <c>Get-ADGroup</c> returns for any filter: one group unless a test changes it.</summary>
    public IList DirectoryGroupGuids { get; } = ArrayList.Synchronized(new ArrayList { DefaultDirectoryGroupGuid });

    /// <summary>
    /// The stand-in directory's one domain, as the DC part of a DN. Its groups live in it; to the stand-in
    /// <c>Get-ADGroupMember</c> a member DN outside it is a member in another domain (aud-a2i).
    /// </summary>
    public const string DirectoryDomainDn = "DC=contoso,DC=test";

    public const string OtherDirectoryDomainDn = "DC=fabrikam,DC=test";

    public IList DirectoryDomains { get; } = ArrayList.Synchronized(new ArrayList { DirectoryDomainDn, OtherDirectoryDomainDn });

    public Hashtable DirectoryGroupDomains { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>A stand-in group's DN from its objectGUID and its domain: what the stand-in <c>Get-ADGroup</c> reports as DistinguishedName.</summary>
    public const string DirectoryGroupDnFormat = "CN=Group {0},OU=Groups,{1}";

    /// <summary>The DN of the stand-in group whose objectGUID is <paramref name="objectGuid"/>, in the default domain.</summary>
    public static string DirectoryGroupDn(string objectGuid) => DirectoryGroupDn(objectGuid, DirectoryDomainDn);

    public static string DirectoryGroupDn(string objectGuid, string domainDn) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, DirectoryGroupDnFormat, objectGuid, domainDn);

    public const string GlobalCatalogHost = "gc01.contoso.test";

    public const string GlobalCatalogEndpoint = GlobalCatalogHost + ":3268";

    public IList GlobalCatalogs { get; } = ArrayList.Synchronized(new ArrayList { GlobalCatalogHost });

    /// <summary>
    /// The member DNs on the stand-in group's linked <c>member</c> attribute - what
    /// <c>Get-ADGroup -Properties member</c> returns, and what the stand-in <c>Get-ADGroupMember</c>
    /// resolves against <see cref="DirectoryObjects"/>: none unless a test adds them (aud-a2i).
    /// </summary>
    public IList DirectoryGroupMembers { get; } = ArrayList.Synchronized(new ArrayList());

    /// <summary>
    /// What the stand-in <c>Get-ADGroup -Identity</c> answers as ObjectGUID, by the identity asked: a
    /// test's way of making Active Directory answer with another object. An identity not named here
    /// answers as its own group.
    /// </summary>
    public Hashtable DirectoryGroupReadBack { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    public Hashtable DirectoryGroupMemberReadBack { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// The default ADWS <c>MaxGroupOrMemberEntries</c>: past this many members the real
    /// <c>Get-ADGroupMember</c> refuses the read ("The size limit for this request was exceeded"), and so
    /// does the stand-in.
    /// </summary>
    public const int AdwsMaxGroupOrMemberEntries = 5000;

    private int _connects;

    /// <summary>How many Exchange connections the pool opened - zero proves nothing was borrowed.</summary>
    public int Connects => Volatile.Read(ref _connects);

    public ExchangeWriteHarness()
    {
        Directory.CreateDirectory(Dir);
        Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = Dir,
            ["Delinea:SecretServerUrl"] = "https://vault.invalid",
        }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(Dir);

        var store = TestConfigStore.Create(Dir);
        var catalog = new ModuleCatalog();
        ModuleConfig = new ModuleConfigService(catalog, env, new ModuleConfigRepository(store), NullLogger<ModuleConfigService>.Instance);
        Enablement = new ModuleEnablementService(catalog, env, ModuleConfig, new ModuleEnablementRepository(store), Config,
            NullLogger<ModuleEnablementService>.Instance);
        Enablement.SaveEnablement(new Dictionary<string, bool> { [ExoConnectionPool.ConfigModuleKey] = true });
        ModuleConfig.SaveModuleConfig(ExoConnectionPool.ConfigModuleKey, new Dictionary<string, string>
        {
            [ExoConnectionPool.ConfigAppIdKey] = "11111111-1111-1111-1111-111111111111",
            [ExoConnectionPool.ConfigOrganizationKey] = "contoso.onmicrosoft.com",
            [ExoConnectionPool.ConfigCertSubjectKey] = "CN=EXO-Automation",
        });
        Jsonl = new JsonlLogService(Config, NullLogger<JsonlLogService>.Instance);
        Trace = new OperationTraceService(Config, Jsonl);
        Audit = new AuditService(Jsonl, Trace);
        Pool = new ExoConnectionPool(Config, ModuleConfig, Enablement, NullLogger<ExoConnectionPool>.Instance, Trace,
            connect: (_, generation, _) =>
            {
                Interlocked.Increment(ref _connects);
                return StandIn(generation);
            });
    }

    public void Dispose()
    {
        Pool.Dispose();
        try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Registers a mailbox under its GUID, its primary address and every alias given. An alias
    /// registered on a DIFFERENT mailbox is how a test makes the typed string and the validated
    /// mailbox name two objects.
    /// </summary>
    public void AddMailbox(string primary, Guid exchangeGuid, string recipientTypeDetails = "UserMailbox",
        string calendarFolder = "Calendar", params string[] aliases)
    {
        var entry = new Hashtable
        {
            ["PrimarySmtpAddress"] = primary,
            ["ExchangeGuid"] = exchangeGuid,
            ["RecipientTypeDetails"] = recipientTypeDetails,
            ["DisplayName"] = primary,
            ["Identity"] = "OBJ:" + primary,
            ["CalendarFolder"] = calendarFolder,
        };
        Mailboxes[exchangeGuid.ToString("D")] = entry;
        Mailboxes[primary] = entry;
        foreach (var alias in aliases)
            Mailboxes[alias] = entry;
    }

    /// <summary>
    /// A real <see cref="PermissionValidator"/> with no protection configured, asking Exchange
    /// through <paramref name="resolver"/> - so the bulk loops bind each row through a real check.
    /// </summary>
    public PermissionValidator NothingProtectedValidator(IIdentityResolver resolver)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(Dir);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var extLog = new ExtendedLogService(Config, env, TestConfigStore.CreateAppSettings(Dir),
            NullLogger<ExtendedLogService>.Instance);
        var delinea = new DelineaService(httpClientFactory, Config, NullLogger<DelineaService>.Instance, extLog, Trace);
        var protectedPrincipals = new ProtectedPrincipalService(env, Config, ModuleConfig,
            TestConfigStore.CreateProtectedPrincipal(Dir), delinea, NullLogger<ProtectedPrincipalService>.Instance);
        var servicers = new ProtectedPrincipalServicerService(
            new SectionAccessService(Config, NullLogger<SectionAccessService>.Instance, env, new ModuleCatalog(),
                new SectionAccessRepository(TestConfigStore.Create(Dir))),
            NullLogger<ProtectedPrincipalServicerService>.Instance);
        var scopeFactory = Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        var scope = Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        provider.GetService(typeof(IIdentityResolver)).Returns(resolver);
        return new PermissionValidator(Config, ModuleConfig, Pool, protectedPrincipals, servicers,
            NullLogger<PermissionValidator>.Instance, scopeFactory);
    }

    /// <summary>
    /// An Exchange resolver that knows <paramref name="typed"/> as <paramref name="mailbox"/> and
    /// nobody else, and never matches self-grant object ids (unstubbed, both would be "" and match).
    /// </summary>
    public static IIdentityResolver ExchangeKnows(string typed, string primary, Guid mailbox)
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.ResolveToObjectIdAsync(Arg.Any<string>()).Returns((string?)null);
        resolver.ResolveRecipientAsync(Arg.Any<string>()).Returns((ResolvedRecipient?)null);
        resolver.ResolveRecipientAsync(typed).Returns(
            new ResolvedRecipient(primary, "ext-" + primary, "UserMailbox", ExistsOnPrem: false, ExchangeGuid: mailbox));
        return resolver;
    }

    /// <summary>A validated mailbox as the protected-principal check would hand it on.</summary>
    public static ValidatedMailbox Validated(Guid exchangeGuid, string? directoryObjectGuid = null) =>
        ValidatedMailbox.From(new ResolvedDirectoryPrincipal(
            "Test", "test", "test@contoso.test", null, "test@contoso.test", null, directoryObjectGuid, null,
            ExchangeGuid: exchangeGuid))!;

    public IReadOnlyList<Hashtable> CallsTo(string command) =>
        Calls.Cast<Hashtable>().Where(c => (string)c["__Command"]! == command).ToList();

    public IReadOnlyList<string> Commands => Calls.Cast<Hashtable>().Select(c => (string)c["__Command"]!).ToList();

    /// <summary>The commands that change something - every recorded call that is not a read.</summary>
    public IReadOnlyList<string> Writes => Commands.Where(c => !c.StartsWith("Get-", StringComparison.Ordinal)
        && c is not "Invoke-Command" and not "Import-Module").ToList();

    public static object? Bound(Hashtable call, string parameter) =>
        call[parameter] is PSObject wrapped ? wrapped.BaseObject : call[parameter];

    public static string? AddedMember(Hashtable call) =>
        Bound(call, "Add") is IDictionary add && add["member"] is { } member
            ? (member is PSObject wrapped ? wrapped.BaseObject : member).ToString()
            : null;

    /// <summary>The <paramref name="index"/>-th argument an <c>Invoke-Command</c> sent to the remote script.</summary>
    public static string? Argument(Hashtable call, int index)
    {
        var item = Bound(call, "ArgumentList") switch
        {
            IList list when list.Count > index => list[index],
            _ => null,
        };
        return (item is PSObject wrapped ? wrapped.BaseObject : item)?.ToString();
    }

    public List<JsonElement> AuditRows()
    {
        var rows = new List<JsonElement>();
        foreach (var path in Jsonl.GetAuditLogPaths(DateTime.Now).Where(File.Exists))
        {
            foreach (var line in File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("eventType", out var type) && type.GetString() == "audit")
                    rows.Add(document.RootElement.Clone());
            }
        }
        return rows;
    }

    /// <summary>
    /// What an on-prem <c>ConnectOnPrem</c> override does: defines the stand-ins in the service's own
    /// runspace, an <c>Invoke-Command</c> that records the script and its arguments and then runs it
    /// locally against them, and a marker session.
    /// </summary>
    public void DefineOnPremSession(PowerShell ps)
    {
        Define(ps.Runspace, StandIns + OnPremStandIns);
        ps.Runspace.SessionStateProxy.SetVariable("onpremSession", "stand-in-session");
    }

    /// <summary>What a Conference Rooms directory-runspace override returns: AD stand-ins, opened.</summary>
    public Runspace OpenDirectoryRunspace()
    {
        var rs = RunspaceFactory.CreateRunspace();
        rs.Open();
        Define(rs, DirectoryStandIns);
        return rs;
    }

    /// <summary>The Exchange stand-ins, opened - what the pool's connect seam hands out; also for testing a stand-in itself.</summary>
    public Runspace OpenExchangeRunspace()
    {
        var rs = RunspaceFactory.CreateRunspace();
        rs.Open();
        Define(rs, StandIns);
        return rs;
    }

    private PooledRunspace StandIn(long generation)
    {
        var rs = OpenExchangeRunspace();
        var ps = PowerShell.Create();
        ps.Runspace = rs;
        return new PooledRunspace(rs, ps, generation, "stand-in");
    }

    private void Define(Runspace rs, string definitions)
    {
        rs.SessionStateProxy.SetVariable("Calls", Calls);
        rs.SessionStateProxy.SetVariable("Mailboxes", Mailboxes);
        rs.SessionStateProxy.SetVariable("DirectoryObjects", DirectoryObjects);
        rs.SessionStateProxy.SetVariable("Failures", Failures);
        rs.SessionStateProxy.SetVariable("ListMembers", ListMembers);
        rs.SessionStateProxy.SetVariable("ListSyncState", ListSyncState);
        rs.SessionStateProxy.SetVariable("DirectoryGroupGuids", DirectoryGroupGuids);
        rs.SessionStateProxy.SetVariable("DirectoryGroupMembers", DirectoryGroupMembers);
        rs.SessionStateProxy.SetVariable("DirectoryGroupReadBack", DirectoryGroupReadBack);
        rs.SessionStateProxy.SetVariable("DirectoryGroupMemberReadBack", DirectoryGroupMemberReadBack);
        rs.SessionStateProxy.SetVariable("DirectoryGroupDnFormat", DirectoryGroupDnFormat);
        rs.SessionStateProxy.SetVariable("DirectoryDomainDn", DirectoryDomainDn);
        rs.SessionStateProxy.SetVariable("DirectoryDomains", DirectoryDomains);
        rs.SessionStateProxy.SetVariable("DirectoryGroupDomains", DirectoryGroupDomains);
        rs.SessionStateProxy.SetVariable("GlobalCatalogs", GlobalCatalogs);
        rs.SessionStateProxy.SetVariable("AdwsMaxGroupOrMemberEntries", AdwsMaxGroupOrMemberEntries);
        using var init = PowerShell.Create();
        init.Runspace = rs;
        init.AddScript(definitions);
        init.Invoke();
        if (init.HadErrors)
            throw new InvalidOperationException("stand-in definitions failed: "
                + string.Join(" | ", init.Streams.Error.Select(e => e.ToString())));
    }

    // Recording helper plus one stand-in per cmdlet the four modules call. Advanced functions, so
    // a parameter the code passes and the stand-in does not declare is a binding error - a
    // stand-in cannot silently swallow an argument the real cmdlet would have received.
    private const string StandIns = """
        function global:Write-StandInCall([string] $Command, $Bound) {
            $row = @{ '__Command' = $Command }
            foreach ($key in $Bound.Keys) { if ($key -ne 'ErrorAction') { $row[$key] = $Bound[$key] } }
            [void]$global:Calls.Add($row)
            $failure = $global:Failures[$Command]
            if ($failure) { throw $failure }
        }
        # Not found is a non-terminating error, as on the real cmdlets: -ErrorAction Stop makes it
        # throw, -ErrorAction Ignore swallows it.
        function global:Find-StandInMailbox($Identity) {
            $entry = $global:Mailboxes["$Identity"]
            if ($null -eq $entry) { Write-Error "The operation couldn't be performed because object '$Identity' couldn't be found on 'stand-in'." }
            $entry
        }
        function global:Get-Mailbox {
            [CmdletBinding()] param([Parameter(Position = 0)] $Identity)
            Write-StandInCall 'Get-Mailbox' $PSBoundParameters
            $entry = Find-StandInMailbox $Identity
            if ($null -ne $entry) { New-Object psobject -Property $entry }
        }
        function global:Get-Recipient {
            [CmdletBinding()] param([Parameter(Position = 0)] $Identity)
            Write-StandInCall 'Get-Recipient' $PSBoundParameters
            $entry = $global:Mailboxes["$Identity"]
            if ($null -ne $entry) { New-Object psobject -Property $entry } else { [pscustomobject]@{ PrimarySmtpAddress = "$Identity" } }
        }
        function global:Add-MailboxPermission {
            [CmdletBinding()] param($Identity, $User, $AccessRights, $AutoMapping, $Confirm)
            Write-StandInCall 'Add-MailboxPermission' $PSBoundParameters
        }
        function global:Remove-MailboxPermission {
            [CmdletBinding()] param($Identity, $User, $AccessRights, $Confirm)
            Write-StandInCall 'Remove-MailboxPermission' $PSBoundParameters
        }
        function global:Add-RecipientPermission {
            [CmdletBinding()] param($Identity, $Trustee, $AccessRights, $Confirm)
            Write-StandInCall 'Add-RecipientPermission' $PSBoundParameters
        }
        function global:Remove-RecipientPermission {
            [CmdletBinding()] param($Identity, $Trustee, $AccessRights, $Confirm)
            Write-StandInCall 'Remove-RecipientPermission' $PSBoundParameters
        }
        function global:Get-MailboxFolderStatistics {
            [CmdletBinding()] param($Identity, $FolderScope)
            Write-StandInCall 'Get-MailboxFolderStatistics' $PSBoundParameters
            $entry = Find-StandInMailbox $Identity
            if ($null -ne $entry) { [pscustomobject]@{ FolderPath = '\' + $entry.CalendarFolder; FolderType = 'Calendar'; Name = $entry.CalendarFolder } }
        }
        function global:Get-MailboxFolderPermission {
            [CmdletBinding()] param($Identity)
            Write-StandInCall 'Get-MailboxFolderPermission' $PSBoundParameters
            [pscustomobject]@{ User = 'Default' }
            [pscustomobject]@{ User = 'previous.editor@contoso.test' }
        }
        function global:Set-MailboxFolderPermission {
            [CmdletBinding()] param($Identity, $User, $AccessRights)
            Write-StandInCall 'Set-MailboxFolderPermission' $PSBoundParameters
        }
        function global:Add-MailboxFolderPermission {
            [CmdletBinding()] param($Identity, $User, $AccessRights)
            Write-StandInCall 'Add-MailboxFolderPermission' $PSBoundParameters
        }
        function global:Remove-MailboxFolderPermission {
            [CmdletBinding()] param($Identity, $User, $Confirm)
            Write-StandInCall 'Remove-MailboxFolderPermission' $PSBoundParameters
        }
        function global:Get-MailboxAutoReplyConfiguration {
            [CmdletBinding()] param($Identity)
            Write-StandInCall 'Get-MailboxAutoReplyConfiguration' $PSBoundParameters
            if ($null -ne (Find-StandInMailbox $Identity)) {
                [pscustomobject]@{ AutoReplyState = 'Disabled'; InternalMessage = $null; ExternalMessage = $null; StartTime = $null; EndTime = $null }
            }
        }
        function global:Set-MailboxAutoReplyConfiguration {
            [CmdletBinding()] param($Identity, $AutoReplyState, $InternalMessage, $ExternalMessage, $StartTime, $EndTime)
            Write-StandInCall 'Set-MailboxAutoReplyConfiguration' $PSBoundParameters
        }
        function global:Get-Place {
            [CmdletBinding()] param($Identity)
            Write-StandInCall 'Get-Place' $PSBoundParameters
            if ($null -ne (Find-StandInMailbox $Identity)) {
                [pscustomobject]@{ City = ''; Building = ''; Capacity = 0; Floor = ''; FloorLabel = ''; DisplayDeviceName = ''; VideoDeviceName = ''; CountryOrRegion = ''; State = '' }
            }
        }
        function global:Set-Place {
            [CmdletBinding()] param($Identity, $Building, $Capacity, $Floor, $FloorLabel, $DisplayDeviceName, $VideoDeviceName)
            Write-StandInCall 'Set-Place' $PSBoundParameters
        }
        function global:Get-MailboxRegionalConfiguration {
            [CmdletBinding()] param($Identity)
            Write-StandInCall 'Get-MailboxRegionalConfiguration' $PSBoundParameters
            if ($null -ne (Find-StandInMailbox $Identity)) { [pscustomobject]@{ TimeZone = '' } }
        }
        function global:Set-MailboxRegionalConfiguration {
            [CmdletBinding()] param($Identity, $TimeZone)
            Write-StandInCall 'Set-MailboxRegionalConfiguration' $PSBoundParameters
        }
        function global:Set-MailboxCalendarConfiguration {
            [CmdletBinding()] param($Identity, $WorkingHoursTimeZone, $WorkDays)
            Write-StandInCall 'Set-MailboxCalendarConfiguration' $PSBoundParameters
        }
        function global:Set-CalendarProcessing {
            [CmdletBinding()] param($Identity, $AllowConflicts, $AutomateProcessing, $DeleteSubject, $AddOrganizerToSubject,
                $DeleteNonCalendarItems, $DeleteAttachments, $AllowRecurringMeetings, $MaximumDurationInMinutes,
                $EnforceSchedulingHorizon, $DeleteComments, $AllBookInPolicy, $AddAdditionalResponse, $AdditionalResponse,
                $BookingWindowInDays, $ProcessExternalMeetingMessages, $BookInPolicy, $ResourceDelegates)
            Write-StandInCall 'Set-CalendarProcessing' $PSBoundParameters
        }
        function global:Set-Mailbox {
            [CmdletBinding()] param($Identity, $CustomAttribute9, $MailTip)
            Write-StandInCall 'Set-Mailbox' $PSBoundParameters
        }
        function global:Get-DistributionGroup {
            [CmdletBinding()] param($Identity)
            Write-StandInCall 'Get-DistributionGroup' $PSBoundParameters
            $list = @{ Name = "$Identity"; PrimarySmtpAddress = "list@contoso.test"; IsDirSynced = $false }
            if ($global:ListSyncState.ContainsKey("$Identity")) {
                $state = $global:ListSyncState["$Identity"]
                if ($null -eq $state) { $list.Remove('IsDirSynced') } else { $list['IsDirSynced'] = $state }
            }
            New-Object psobject -Property $list
        }
        function global:New-DistributionGroup {
            [CmdletBinding()] param($Name, [switch] $RoomList)
            Write-StandInCall 'New-DistributionGroup' $PSBoundParameters
        }
        # aud-a2h. Exchange Online returns at most -ResultSize members, 1000 when it is not passed. A read
        # without it is refused - an error and no members - rather than answered short, so no test can
        # see a member through a read the real cmdlet would have cut off. Write-Error then return, not
        # throw: called with -ErrorAction SilentlyContinue, as the service calls it, a throw is
        # suppressed and the function carries on.
        function global:Get-DistributionGroupMember {
            [CmdletBinding()] param($Identity, $ResultSize)
            Write-StandInCall 'Get-DistributionGroupMember' $PSBoundParameters
            if (-not $PSBoundParameters.ContainsKey('ResultSize')) {
                Write-Error "stand-in: Get-DistributionGroupMember without -ResultSize - Exchange Online would answer with its default page only, not the whole list."
                return
            }
            $members = @($global:ListMembers)
            if ("$ResultSize" -ne 'Unlimited') { $members = @($members | Select-Object -First ([int]"$ResultSize")) }
            foreach ($member in $members) { [pscustomobject]@{ PrimarySmtpAddress = $member } }
        }
        function global:Add-DistributionGroupMember {
            [CmdletBinding()] param($Identity, $Member)
            Write-StandInCall 'Add-DistributionGroupMember' $PSBoundParameters
        }
        function global:Disconnect-ExchangeOnline { param($ConnectionId, $Confirm) }

        """;

    // The remote session, run locally: the script the service sends and the arguments it binds,
    // recorded, then executed against the stand-ins above. Send As pipes a mailbox object into
    // the ADPermission cmdlets, so those bind -Identity by property name, as the real ones do.
    private const string OnPremStandIns = """
        function global:Invoke-Command {
            [CmdletBinding()] param($Session, [scriptblock] $ScriptBlock, [object[]] $ArgumentList)
            [void]$global:Calls.Add(@{ '__Command' = 'Invoke-Command'; Session = $Session; Script = $ScriptBlock.ToString(); ArgumentList = $ArgumentList })
            & $ScriptBlock @ArgumentList
        }
        function global:Remove-PSSession { param($Session) }
        function global:Add-ADPermission {
            [CmdletBinding()] param([Parameter(ValueFromPipelineByPropertyName)] $Identity, $User, $ExtendedRights, $Confirm)
            process { Write-StandInCall 'Add-ADPermission' $PSBoundParameters }
        }
        function global:Remove-ADPermission {
            [CmdletBinding()] param([Parameter(ValueFromPipelineByPropertyName)] $Identity, $User, $ExtendedRights, $Confirm)
            process { Write-StandInCall 'Remove-ADPermission' $PSBoundParameters }
        }

        """;

    // Active Directory, for the Conference Rooms City/State/Country and room-list steps.
    private const string DirectoryStandIns = """
        function global:Write-StandInCall([string] $Command, $Bound) {
            $row = @{ '__Command' = $Command }
            foreach ($key in $Bound.Keys) { if ($key -ne 'ErrorAction' -and $key -ne 'Credential') { $row[$key] = $Bound[$key] } }
            [void]$global:Calls.Add($row)
            $failure = $global:Failures[$Command]
            if ($failure) { throw $failure }
        }
        function global:Import-Module { param($Name, $ErrorAction) }
        function global:Get-ADUser {
            [CmdletBinding()] param($Identity, $Filter, $Credential, $Properties)
            Write-StandInCall 'Get-ADUser' $PSBoundParameters
            if ($PSBoundParameters.ContainsKey('Identity')) {
                $entry = $global:DirectoryObjects["$Identity"]
                if ($null -eq $entry) { throw "Cannot find an object with identity: '$Identity'" }
                New-Object psobject -Property $entry
            }
        }
        function global:Set-ADUser {
            [CmdletBinding()] param($Identity, $Replace, $Credential)
            Write-StandInCall 'Set-ADUser' $PSBoundParameters
        }
        # aud-a2i. A search answers with every group in DirectoryGroupGuids, by objectGUID and DN. A read by
        # -Identity (a DN or an objectGUID) answers with that group - as another object when
        # DirectoryGroupReadBack names the identity - and with its member attribute when -Properties asks for
        # it; Failures['Get-ADGroup -Identity'] fails that read alone. Its errors are Write-Error then
        # return, so they stay errors under any -ErrorAction.
        function global:Get-ADForest {
            [CmdletBinding()] param($Credential, $Server)
            Write-StandInCall 'Get-ADForest' $PSBoundParameters
            [pscustomobject]@{ GlobalCatalogs = [string[]]@($global:GlobalCatalogs) }
        }
        function global:Get-StandInDomainName([string] $DomainDn) {
            (@($DomainDn -split ',') | ForEach-Object { $_.Trim() -replace '^DC=', '' }) -join '.'
        }
        function global:Get-StandInReach($Bound) {
            if (-not $Bound.ContainsKey('Server')) { return 'default' }
            $server = "$($Bound['Server'])"
            if ($server.StartsWith(':')) { return 'default' }
            foreach ($gc in @($global:GlobalCatalogs)) {
                if (-not [string]::IsNullOrWhiteSpace("$gc") -and $server -eq "$($gc):3268") { return 'forest' }
            }
            foreach ($domain in @($global:DirectoryDomains)) {
                if ($server -eq (Get-StandInDomainName "$domain")) { return "$domain" }
            }
            return $null
        }
        function global:Test-StandInReaches($Reach, [string] $DomainDn) {
            if ($Reach -eq 'forest') { return $true }
            if ($Reach -eq 'default') { return $DomainDn -eq $global:DirectoryDomainDn }
            return $DomainDn -eq "$Reach"
        }
        function global:Get-StandInGroupDomain([string] $Guid) {
            $domain = $global:DirectoryGroupDomains[$Guid]
            if ($domain) { return "$domain" }
            return $global:DirectoryDomainDn
        }
        function global:Get-StandInGroupDn([string] $Guid) { $global:DirectoryGroupDnFormat -f $Guid, (Get-StandInGroupDomain $Guid) }
        function global:Find-StandInGroups($Reach) {
            foreach ($g in @($global:DirectoryGroupGuids)) { if (Test-StandInReaches $Reach (Get-StandInGroupDomain "$g")) { "$g" } }
        }
        function global:Get-ADGroup {
            [CmdletBinding()] param($Filter, $Identity, $Properties, $Server, $Credential)
            Write-StandInCall 'Get-ADGroup' $PSBoundParameters
            $reach = Get-StandInReach $PSBoundParameters
            if ($null -eq $reach) { Write-Error "Unable to contact the server '$Server'."; return }
            $reached = @(Find-StandInGroups $reach)
            if (-not $PSBoundParameters.ContainsKey('Identity')) {
                foreach ($group in $reached) {
                    [pscustomobject]@{ ObjectGUID = [guid]"$group"; DistinguishedName = (Get-StandInGroupDn "$group") }
                }
                return
            }
            $failure = $global:Failures['Get-ADGroup -Identity']
            if ($failure) { Write-Error $failure; return }
            $group = @($reached | Where-Object { "$Identity" -eq (Get-StandInGroupDn "$_") -or "$Identity" -eq "$_" })
            if ($group.Count -ne 1) { Write-Error "Cannot find an object with identity: '$Identity'"; return }
            $memberRead = @($Properties) -contains 'member'
            $answer = $global:DirectoryGroupReadBack["$Identity"]
            if ($memberRead -and $global:DirectoryGroupMemberReadBack.ContainsKey("$Identity")) { $answer = $global:DirectoryGroupMemberReadBack["$Identity"] }
            $row = @{ ObjectGUID = [guid]"$(if ($answer) { $answer } else { $group[0] })"; DistinguishedName = (Get-StandInGroupDn "$($group[0])") }
            if ($memberRead) { $row['member'] = [string[]]@($global:DirectoryGroupMembers) }
            New-Object psobject -Property $row
        }
        function global:Set-ADGroup {
            [CmdletBinding()] param($Identity, $Add, $Server, $Credential)
            Write-StandInCall 'Set-ADGroup' $PSBoundParameters
            $reach = Get-StandInReach $PSBoundParameters
            if ($null -eq $reach) { Write-Error "Unable to contact the server '$Server'."; return }
            if ($reach -eq 'forest') { Write-Error 'The server is unwilling to process the request.'; return }
            $group = @(Find-StandInGroups $reach | Where-Object { "$Identity" -eq "$_" -or "$Identity" -eq (Get-StandInGroupDn "$_") })
            if ($group.Count -ne 1) { Write-Error "Cannot find an object with identity: '$Identity'"; return }
        }
        # aud-a2i. The real cmdlet resolves every member server-side: Active Directory Web Services refuses a
        # group of more than MaxGroupOrMemberEntries members (5,000 by default), and the read faults on a
        # member in another domain it cannot chase. The stand-in refuses both - Write-Error then return, so
        # it stays refused under any -ErrorAction - and otherwise answers each member's objectGUID from
        # DirectoryObjects. No production code calls it (GroupMemberListingTests pins that).
        function global:Get-ADGroupMember {
            [CmdletBinding()] param($Identity, $Server, $Credential)
            Write-StandInCall 'Get-ADGroupMember' $PSBoundParameters
            $members = @($global:DirectoryGroupMembers)
            if ($members.Count -gt $global:AdwsMaxGroupOrMemberEntries) { Write-Error 'The size limit for this request was exceeded'; return }
            if (@($members | Where-Object { -not "$_".EndsWith(",$($global:DirectoryDomainDn)", [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
                Write-Error 'An operations error occurred'
                return
            }
            foreach ($dn in $members) {
                $object = @($global:DirectoryObjects.Values | Where-Object { "$($_['DistinguishedName'])" -eq "$dn" })
                [pscustomobject]@{ DistinguishedName = "$dn"; ObjectGUID = $(if ($object.Count -eq 1) { $object[0]['ObjectGUID'] }) }
            }
        }
        function global:Add-ADGroupMember {
            [CmdletBinding()] param($Identity, $Members, $Server, $Credential)
            Write-StandInCall 'Add-ADGroupMember' $PSBoundParameters
            $reach = Get-StandInReach $PSBoundParameters
            if ($null -eq $reach) { Write-Error "Unable to contact the server '$Server'."; return }
            if ($reach -eq 'forest') { Write-Error 'The server is unwilling to process the request.'; return }
            $domain = if ($reach -eq 'default') { $global:DirectoryDomainDn } else { "$reach" }
            $group = @(Find-StandInGroups $reach | Where-Object { "$Identity" -eq "$_" -or "$Identity" -eq (Get-StandInGroupDn "$_") })
            if ($group.Count -ne 1) { Write-Error "Cannot find an object with identity: '$Identity' under: '$domain'."; return }
            foreach ($member in @($Members)) {
                $object = $global:DirectoryObjects["$member"]
                if ($null -eq $object) { $object = @($global:DirectoryObjects.Values | Where-Object { "$($_['DistinguishedName'])" -eq "$member" }) | Select-Object -First 1 }
                if ($null -eq $object -or -not "$($object['DistinguishedName'])".EndsWith(",$domain", [StringComparison]::OrdinalIgnoreCase)) {
                    Write-Error "Cannot find an object with identity: '$member' under: '$domain'."
                    return
                }
            }
        }

        """;
}
