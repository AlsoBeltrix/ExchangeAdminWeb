using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// psl-7: a directory import is judged by its throw alone and leaves nothing behind for a later read.
/// </summary>
/// <remarks>
/// GroupManagementService, ADAttributeEditorService and ADOrganizationalUnitService imported the ActiveDirectory
/// module and cleared only the commands, so a record the import wrote past -ErrorAction Stop stayed in the stream and
/// a later read's HadErrors check - true while any record remains (psl-2) - took it for that read's error.
/// AccountLockoutRemediationService and ADDirectorySearchService checked HadErrors straight after the import, which
/// under Stop never sees the import's own error (it is thrown, and leaves no record), so the check failed only an
/// import whose module had loaded. All five now import through ActiveDirectoryModuleImport.
///
/// Every session here is local: Import-Module and each read are ADVANCED functions, so -ErrorAction acts on them as
/// on the cmdlets; module autoloading is off; nothing loads a module or reaches a directory.
/// </remarks>
public sealed class DirectoryModuleImportTests
{
    private const string ImportNoise = "A record the module load wrote past the callers ErrorAction.";
    private const string ImportFailure =
        "The specified module ActiveDirectory was not loaded because no valid module file was found in any module directory.";

    private static readonly (string username, string password, string domain) Creds =
        ("svc-directory", "not-a-secret", "EXAMPLE");

    private enum Import
    {
        Loads,
        ReportsErrorWithoutThrowing,
        Fails,
    }

    /// <summary>
    /// A local session whose Import-Module does what <paramref name="import"/> says, with the reads named, every call
    /// noted in <paramref name="calls"/>.
    /// </summary>
    /// <remarks>
    /// ReportsErrorWithoutThrowing is Write-Error with its own -ErrorAction Continue, which the caller's Stop does not
    /// throw: a record no throw carried. Fails is a bare Write-Error, a non-terminating error that only the caller's
    /// Stop turns into a throw - the real cmdlet's shape for a module it cannot find (aud-a2r measured it).
    /// </remarks>
    private static Runspace StandIn(Import import, List<string> calls, params (string Name, string Body)[] reads)
    {
        var state = InitialSessionState.CreateDefault();
        state.Variables.Add(new SessionStateVariableEntry("PSModuleAutoloadingPreference", "None", "no autoloading"));
        state.Variables.Add(new SessionStateVariableEntry("DirectoryCalls", calls, "the calls the session took"));

        var outcome = import switch
        {
            Import.ReportsErrorWithoutThrowing => $"Write-Error -Message '{ImportNoise}' -ErrorAction Continue",
            Import.Fails => $"Write-Error -Message '{ImportFailure}'",
            _ => "",
        };
        state.Commands.Add(new SessionStateFunctionEntry("Import-Module",
            "[CmdletBinding()] param([Parameter(Position = 0)] $Name)\n$global:DirectoryCalls.Add(\"Import-Module|$Name\")\n" + outcome));

        foreach (var (name, body) in reads)
            state.Commands.Add(new SessionStateFunctionEntry(name, body));

        return RunspaceFactory.CreateRunspace(state);
    }

    private static PowerShell Shell(Runspace runspace)
    {
        runspace.Open();
        var ps = PowerShell.Create();
        ps.Runspace = runspace;
        return ps;
    }

    private const string GetThing = """
        [CmdletBinding()] param()
        $global:DirectoryCalls.Add('Get-Thing')
        'answer'
        """;

    [Fact]
    public void TheStandIns_ReportOrFailAsTheTestsBelowRelyOn()
    {
        // So no test below can pass for want of what it describes: under Stop the reporting import does not throw
        // and leaves its record; the failing one throws and leaves none, and without Stop leaves a record instead.
        var calls = new List<string>();

        using (var runspace = StandIn(Import.ReportsErrorWithoutThrowing, calls))
        using (var ps = Shell(runspace))
        {
            ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
            ps.Invoke();
            Assert.True(ps.HadErrors);
            Assert.Equal(ImportNoise, Assert.Single(ps.Streams.Error).Exception.Message);
        }

        using (var runspace = StandIn(Import.Fails, calls))
        using (var ps = Shell(runspace))
        {
            ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
            var thrown = Assert.ThrowsAny<RuntimeException>(() => ps.Invoke());
            Assert.Contains(ImportFailure, thrown.Message, StringComparison.Ordinal);
            Assert.Empty(ps.Streams.Error);

            ps.Commands.Clear();
            ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory");
            ps.Invoke();
            Assert.Equal(ImportFailure, Assert.Single(ps.Streams.Error).Exception.Message);
        }

        Assert.Equal(["Import-Module|ActiveDirectory", "Import-Module|ActiveDirectory", "Import-Module|ActiveDirectory"], calls);
    }

    // ---- the helper ----------------------------------------------------------------------------------

    [Fact]
    public void TheImport_ThatReportsAnErrorWithoutThrowing_LeavesNoRecordAndNothingQueued()
    {
        var calls = new List<string>();
        using var runspace = StandIn(Import.ReportsErrorWithoutThrowing, calls, ("Get-Thing", GetThing));
        using var ps = Shell(runspace);

        ActiveDirectoryModuleImport.Import(ps);

        Assert.Empty(ps.Streams.Error);
        Assert.Empty(ps.Commands.Commands);
        ps.AddCommand("Get-Thing").AddParameter("ErrorAction", "Stop");
        Assert.Equal("answer", Assert.Single(ps.Invoke()).BaseObject);
        Assert.False(ps.HadErrors);
        Assert.Equal(["Import-Module|ActiveDirectory", "Get-Thing"], calls);
    }

    [Fact]
    public void TheImport_ThatFails_ThrowsItsOwnError_AndLeavesNothingQueued()
    {
        var calls = new List<string>();
        using var runspace = StandIn(Import.Fails, calls);
        using var ps = Shell(runspace);

        var thrown = Assert.ThrowsAny<RuntimeException>(() => ActiveDirectoryModuleImport.Import(ps));

        Assert.Contains(ImportFailure, thrown.Message, StringComparison.Ordinal);
        Assert.Empty(ps.Streams.Error);
        Assert.Empty(ps.Commands.Commands);
        Assert.Equal(["Import-Module|ActiveDirectory"], calls);
    }

    // ---- Group Management ------------------------------------------------------------------------------

    private const string GroupDn = "CN=Finance Approvers,OU=Groups,DC=example,DC=test";

    private const string GetAdGroup = """
        [CmdletBinding()] param($Identity, $LDAPFilter, $Properties, $Credential, $Server)
        $global:DirectoryCalls.Add("Get-ADGroup|$Identity|$Server")
        [pscustomobject]@{
            Name = 'Finance Approvers'
            SamAccountName = 'FinanceApprovers'
            mail = 'finance.approvers@example.test'
            DistinguishedName = $Identity
            ObjectGUID = [guid]'6f1c2c7e-3b9a-4d55-9a51-1c2f0e4b7a10'
        }
        """;

    /// <summary>The service with only its directory session replaced.</summary>
    private sealed class StandInGroups(Func<Runspace> open)
        : GroupManagementService(null!, null!, null!, null!, NullLogger<GroupManagementService>.Instance)
    {
        internal override Runspace CreateDirectoryRunspace() => open();
    }

    [Fact]
    public void GroupManagement_AnImportThatReportsAnError_IsNotTakenForTheGroupReadsError()
    {
        // ResolveGroupForWrite by DN: the read's HadErrors check took the import's record for the read's error and
        // refused the write as "not found by its distinguished name".
        var calls = new List<string>();
        var service = new StandInGroups(() => StandIn(Import.ReportsErrorWithoutThrowing, calls, ("Get-ADGroup", GetAdGroup)));

        var resolved = service.ResolveGroupForWrite(Creds, null, GroupDn);

        Assert.Null(resolved.Error);
        Assert.True(resolved.IsGroup);
        Assert.Equal(GroupDn, resolved.Principal!.DistinguishedName);
        Assert.Equal(["Import-Module|ActiveDirectory", $"Get-ADGroup|{GroupDn}|example.test"], calls);
    }

    [Fact]
    public void GroupManagement_AFailingImport_IsTheImportsFailure_AndNoReadIsSent()
    {
        var calls = new List<string>();
        var service = new StandInGroups(() => StandIn(Import.Fails, calls, ("Get-ADGroup", GetAdGroup)));

        var thrown = Assert.ThrowsAny<RuntimeException>(() => service.ResolveGroupForWrite(Creds, null, GroupDn));

        Assert.Contains(ImportFailure, thrown.Message, StringComparison.Ordinal);
        Assert.Equal(["Import-Module|ActiveDirectory"], calls);
    }

    // ---- AD Attribute Editor ------------------------------------------------------------------------------

    [Fact]
    public async Task AttributeEditor_AnImportThatReportsAnError_IsNotTakenForTheLookupsError()
    {
        // No search base: the read's HadErrors check returned the import's record as the lookup's error.
        using var h = new AttributeEditorHarness();
        var alex = h.Ad.AddAlex();
        h.Ad.ImportReportsErrorWithoutThrowing = ImportNoise;

        var result = await h.Service.LookupAsync(alex.Upn);

        Assert.True(result.Success, result.Error);
        Assert.Equal(alex.Guid.ToString(), result.Principal!.ObjectGuid);
        Assert.Equal(["Import-Module|ActiveDirectory", $"Get-ADUser||"], h.Ad.Calls);
    }

    [Fact]
    public async Task AttributeEditor_AnImportThatReportsAnError_IsNotTakenForTheFirstSearchBasesError()
    {
        // With search bases each is judged on its own read (psl-6), and the first one's took the import's record and
        // was reported as a base that could not be searched.
        using var h = new AttributeEditorHarness();
        var alex = h.Ad.AddAlex();
        h.ConfigureSearchBases("OU=Staff,DC=example,DC=test", "OU=Contractors,DC=example,DC=test");
        h.Ad.ImportReportsErrorWithoutThrowing = ImportNoise;

        var result = await h.Service.LookupAsync(alex.Upn);

        Assert.True(result.Success, result.Error);
        Assert.Empty(h.Logger.Warnings);
    }

    [Fact]
    public async Task AttributeEditor_AFailingImport_IsTheLookupsFailure_AndNoReadIsSent()
    {
        using var h = new AttributeEditorHarness();
        var alex = h.Ad.AddAlex();
        h.Ad.ImportFailure = ImportFailure;

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => h.Service.LookupAsync(alex.Upn));

        Assert.Contains(ImportFailure, thrown.ToString(), StringComparison.Ordinal);
        Assert.Equal(["Import-Module|ActiveDirectory"], h.Ad.Calls);
    }

    [Fact]
    public void TheHarness_ImportReportsAnErrorWithoutThrowing_UnderStop()
    {
        // The knob the editor's tests above rest on.
        using var h = new AttributeEditorHarness();
        h.Ad.ImportReportsErrorWithoutThrowing = ImportNoise;
        using var runspace = RunspaceFactory.CreateRunspace(h.Ad.CreateSessionState());
        using var ps = Shell(runspace);

        ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
        ps.Invoke();

        Assert.True(ps.HadErrors);
        Assert.Equal(ImportNoise, Assert.Single(ps.Streams.Error).Exception.Message);
    }

    // ---- the OU browser --------------------------------------------------------------------------------

    private const string GetAdOrganizationalUnit = """
        [CmdletBinding()] param($Filter, $Properties)
        $global:DirectoryCalls.Add("Get-ADOrganizationalUnit|$Filter")
        [pscustomobject]@{ Name = 'Staff'; DistinguishedName = 'OU=Staff,DC=example,DC=test' }
        [pscustomobject]@{ Name = 'Groups'; DistinguishedName = 'OU=Groups,DC=example,DC=test' }
        """;

    [Fact]
    public void OrganizationalUnits_AnImportThatReportsAnError_IsNotTakenForTheEnumerationsError()
    {
        // The enumeration's HadErrors check threw the import's record as its failure, and the OU browser was hidden.
        var calls = new List<string>();
        var logger = new CapturingLogger<ADOrganizationalUnitService>();
        var service = new ADOrganizationalUnitService(logger)
        {
            CreateDirectoryRunspace = () => StandIn(Import.ReportsErrorWithoutThrowing, calls, ("Get-ADOrganizationalUnit", GetAdOrganizationalUnit)),
        };

        var units = service.GetOrganizationalUnits();

        Assert.True(service.IsAvailable);
        Assert.Equal(["Groups", "Staff"], units.Select(u => u.Name));
        Assert.Empty(logger.Warnings);
        Assert.Equal(["Import-Module|ActiveDirectory", "Get-ADOrganizationalUnit|*"], calls);
    }

    [Fact]
    public void OrganizationalUnits_AFailingImport_IsTheImportsFailure()
    {
        var calls = new List<string>();
        var logger = new CapturingLogger<ADOrganizationalUnitService>();
        var service = new ADOrganizationalUnitService(logger)
        {
            CreateDirectoryRunspace = () => StandIn(Import.Fails, calls, ("Get-ADOrganizationalUnit", GetAdOrganizationalUnit)),
        };

        var units = service.GetOrganizationalUnits();

        Assert.Empty(units);
        Assert.False(service.IsAvailable);
        Assert.Contains(ImportFailure, Assert.Single(logger.Warnings).Exception?.Message, StringComparison.Ordinal);
        Assert.Equal(["Import-Module|ActiveDirectory"], calls);
    }

    // ---- Account Lockout Remediation ------------------------------------------------------------------

    private const string GetAdDomain = """
        [CmdletBinding()] param($Credential)
        $global:DirectoryCalls.Add('Get-ADDomain')
        [pscustomobject]@{ PDCEmulator = 'dc01.example.test' }
        """;

    [Fact]
    public void AccountLockout_AnImportThatReportsAnError_IsNeitherTheImportsFailureNorTheReadsError()
    {
        // The HadErrors check after the import threw the record as the import's failure, though the module loaded.
        var calls = new List<string>();
        using var runspace = StandIn(Import.ReportsErrorWithoutThrowing, calls, ("Get-ADDomain", GetAdDomain));
        runspace.Open();

        using var ps = AccountLockoutRemediationService.CreateAdPowerShell(runspace);
        ps.AddCommand("Get-ADDomain").AddParameter("ErrorAction", "Stop");
        var domain = AccountLockoutRemediationService.Invoke(ps);

        Assert.Equal("dc01.example.test", Assert.Single(domain).Properties["PDCEmulator"].Value);
        Assert.Equal(["Import-Module|ActiveDirectory", "Get-ADDomain"], calls);
    }

    [Fact]
    public void AccountLockout_AFailingImport_IsTheImportsFailure()
    {
        var calls = new List<string>();
        using var runspace = StandIn(Import.Fails, calls, ("Get-ADDomain", GetAdDomain));
        runspace.Open();

        var thrown = Assert.ThrowsAny<RuntimeException>(() => AccountLockoutRemediationService.CreateAdPowerShell(runspace));

        Assert.Contains(ImportFailure, thrown.Message, StringComparison.Ordinal);
        Assert.Equal(["Import-Module|ActiveDirectory"], calls);
    }

    // ---- the directory autocomplete's availability ----------------------------------------------------

    [Fact]
    public void DirectorySearch_AnImportThatReportsAnError_LeavesTheDirectoryAvailable()
    {
        // The HadErrors check after the import marked the directory unavailable, though the module loaded.
        var calls = new List<string>();
        var logger = new CapturingLogger<ADDirectorySearchService>();
        var service = new ADDirectorySearchService(logger)
        {
            CreateProbeRunspace = () => StandIn(Import.ReportsErrorWithoutThrowing, calls),
        };

        Assert.True(service.IsAvailable);
        Assert.Empty(logger.Warnings);
        Assert.Equal(["Import-Module|ActiveDirectory"], calls);
    }

    [Fact]
    public void DirectorySearch_AFailingImport_IsTheImportsFailure()
    {
        var calls = new List<string>();
        var logger = new CapturingLogger<ADDirectorySearchService>();
        var service = new ADDirectorySearchService(logger)
        {
            CreateProbeRunspace = () => StandIn(Import.Fails, calls),
        };

        Assert.False(service.IsAvailable);
        Assert.Contains(ImportFailure, Assert.Single(logger.Warnings).Exception?.Message, StringComparison.Ordinal);
        Assert.Equal(["Import-Module|ActiveDirectory"], calls);
    }

    private sealed record Logged(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<Logged> _entries = [];

        public IReadOnlyList<Logged> Warnings
        {
            get { lock (_entries) return _entries.Where(e => e.Level == LogLevel.Warning).ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add(new Logged(logLevel, formatter(state, exception), exception));
        }
    }
}
