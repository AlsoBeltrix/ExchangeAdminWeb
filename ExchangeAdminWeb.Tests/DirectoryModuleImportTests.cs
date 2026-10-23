using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.SelfServiceGroups;
using Microsoft.Extensions.Configuration;
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
/// import whose module had loaded. All five now import through ActiveDirectoryModuleImport. psl-8 audited every other
/// import and found the same in EmergencyDisableService and SelfServiceGroupService, which now import through it too.
///
/// Every session here is local: Import-Module and each read are ADVANCED functions, so -ErrorAction acts on them as
/// on the cmdlets; module autoloading is off; nothing loads a module or reaches a directory.
/// </remarks>
public sealed class DirectoryModuleImportTests : IDisposable
{
    // The Emergency Disable service's trace needs a log root; it is disabled, so nothing is written there.
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "eaw-psl8-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
                Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

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

    // ---- Emergency Disable (psl-8) --------------------------------------------------------------------
    // Each of its four directory steps imported the module clearing only the commands, so a record the import wrote past
    // Stop stayed in the stream and the step's own check took it: the pre-state read threw it as the read's error, the
    // lockdown move refused as if its OU could not be read, and the disable and the password reset - their writes made -
    // reported FAILED.

    private const string TargetDn = "CN=Alex Doe,OU=Staff,DC=example,DC=test";
    private const string TargetGuid = "3c6e2a1f-9b7d-4e2a-8f51-0d4c1b2a7e90";
    private const string LockdownOu = "OU=Lockdown,DC=example,DC=test";

    private static readonly ResolvedDirectoryPrincipal Target = new(
        Source: "Test", DisplayName: "Alex Doe", UserPrincipalName: "alex.doe@example.test", SamAccountName: "adoe",
        PrimarySmtpAddress: "alex.doe@example.test", DistinguishedName: TargetDn, ObjectGuid: TargetGuid, EntraObjectId: null);

    private static readonly (string Name, string Body)[] DisableDirectory =
    [
        ("Get-ADUser", $$"""
            [CmdletBinding()] param($Identity, $Properties, $Credential)
            $global:DirectoryCalls.Add("Get-ADUser|$Identity")
            [pscustomobject]@{ DistinguishedName = '{{TargetDn}}'; ObjectGUID = [guid]'{{TargetGuid}}'; Enabled = $true; info = $null }
            """),
        ("Set-ADUser", """
            [CmdletBinding()] param($Identity, $Enabled, $Replace, $Credential)
            $global:DirectoryCalls.Add("Set-ADUser|$Identity")
            """),
        ("Set-ADAccountPassword", """
            [CmdletBinding()] param($Identity, $NewPassword, [switch] $Reset, $Credential)
            $global:DirectoryCalls.Add("Set-ADAccountPassword|$Identity")
            """),
        ("Get-ADOrganizationalUnit", """
            [CmdletBinding()] param($Identity, $Credential)
            $global:DirectoryCalls.Add("Get-ADOrganizationalUnit|$Identity")
            [pscustomobject]@{ DistinguishedName = $Identity }
            """),
        ("Move-ADObject", """
            [CmdletBinding()] param($Identity, $TargetPath, $Credential)
            $global:DirectoryCalls.Add("Move-ADObject|$Identity|$TargetPath")
            """),
    ];

    /// <summary>The service with only its directory session replaced; its trace disabled.</summary>
    private sealed class StandInEmergencyDisable(Func<Runspace> open, OperationTraceService trace)
        : EmergencyDisableService(null!, null!, null!, null!, trace, null!, null!, null!, null!, null!, null!,
            NullLogger<EmergencyDisableService>.Instance)
    {
        internal override Runspace CreateDirectoryRunspace() => open();
    }

    private EmergencyDisableService EmergencyDisable(Func<Runspace> open)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _dir,
            ["OperationTrace:Enabled"] = "false",
        }).Build();
        return new StandInEmergencyDisable(open,
            new OperationTraceService(config, new JsonlLogService(config, NullLogger<JsonlLogService>.Instance)));
    }

    [Theory]
    [InlineData("the pre-state read")]
    [InlineData("the disable")]
    [InlineData("the lockdown move")]
    [InlineData("the password reset")]
    public async Task EmergencyDisable_AnImportThatReportsAnError_IsNotTakenForTheStepsError(string step)
    {
        var calls = new List<string>();
        var service = EmergencyDisable(() => StandIn(Import.ReportsErrorWithoutThrowing, calls, DisableDirectory));

        switch (step)
        {
            case "the pre-state read":
                Assert.True(await service.ReadAdEnabledState(Target, Creds));
                Assert.Equal(["Import-Module|ActiveDirectory", $"Get-ADUser|{TargetDn}"], calls);
                break;
            case "the disable":
                Assert.Equal(new DisableStepResult("DisableAD", "OK", null), await service.ExecuteDisableAD(Target, Creds, adSlotHeld: true));
                Assert.Equal(["Import-Module|ActiveDirectory", $"Get-ADUser|{TargetDn}", $"Set-ADUser|{TargetDn}"], calls);
                break;
            case "the lockdown move":
                var (steps, outcome, stamp) = await service.ExecuteMoveToLockdownOu(Target, Creds, LockdownOu, adSlotHeld: true);
                Assert.Equal(LockdownOutcome.Moved, outcome);
                Assert.Equal(LockdownStampOutcome.Stamped, stamp);
                Assert.Equal(["LockdownMove", "LockdownStamp"], steps.Select(s => s.Step));
                Assert.All(steps, s => Assert.Equal("OK", s.Status));
                Assert.Contains($"Move-ADObject|{TargetDn}|{LockdownOu}", calls);
                break;
            default:
                Assert.Equal(new DisableStepResult("ResetPassword", "OK", null), await service.ExecuteResetPassword(Target, Creds, adSlotHeld: true));
                Assert.Equal(["Import-Module|ActiveDirectory", $"Get-ADUser|{TargetDn}", $"Set-ADAccountPassword|{TargetDn}"], calls);
                break;
        }
    }

    [Theory]
    [InlineData("the pre-state read")]
    [InlineData("the disable")]
    [InlineData("the lockdown move")]
    [InlineData("the password reset")]
    public async Task EmergencyDisable_AFailingImport_IsTheStepsFailure_AndNoReadIsSent(string step)
    {
        var calls = new List<string>();
        var service = EmergencyDisable(() => StandIn(Import.Fails, calls, DisableDirectory));

        var failure = step switch
        {
            "the pre-state read" => (await Assert.ThrowsAnyAsync<RuntimeException>(() => service.ReadAdEnabledState(Target, Creds))).Message,
            "the disable" => Failed(await service.ExecuteDisableAD(Target, Creds, adSlotHeld: true), "DisableAD"),
            "the lockdown move" => Failed(await service.ExecuteMoveToLockdownOu(Target, Creds, LockdownOu, adSlotHeld: true)),
            _ => Failed(await service.ExecuteResetPassword(Target, Creds, adSlotHeld: true), "ResetPassword"),
        };

        Assert.Contains(ImportFailure, failure, StringComparison.Ordinal);
        Assert.Equal(["Import-Module|ActiveDirectory"], calls);
    }

    private static string Failed(DisableStepResult result, string step)
    {
        Assert.Equal(step, result.Step);
        Assert.Equal("FAILED", result.Status);
        return Assert.IsType<string>(result.Detail);
    }

    private static string Failed((List<DisableStepResult> Steps, LockdownOutcome Outcome, LockdownStampOutcome Stamp) move)
    {
        Assert.Equal(LockdownOutcome.Failed, move.Outcome);
        Assert.Equal(LockdownStampOutcome.NotAttempted, move.Stamp);
        return Failed(Assert.Single(move.Steps), "LockdownMove");
    }

    // ---- Emergency Disable's bound-object re-read (psl-10) ------------------------------------------------
    // VerifyBoundObject re-reads the target by its DN before each write, and cleared only its commands, so a record the re-read
    // wrote past Stop was the next check's: the disable and the password reset reported FAILED after their write had been
    // made, and the lockdown move refused as if its OU could not be read.

    private const string ReReadNoise = "A record the bound-object re-read wrote past the callers ErrorAction.";

    // DisableDirectory with one difference: Get-ADUser, when it is the bound-object re-read - the one read that asks for
    // ObjectGUID - leaves a record without throwing, and answers the bound object as before.
    private static readonly (string Name, string Body)[] DisableDirectoryWithReReadNoise =
    [
        .. DisableDirectory.Where(r => r.Name != "Get-ADUser"),
        ("Get-ADUser", $$"""
            [CmdletBinding()] param($Identity, $Properties, $Credential)
            $global:DirectoryCalls.Add("Get-ADUser|$Identity")
            if ($Properties -contains 'ObjectGUID') { Write-Error -Message '{{ReReadNoise}}' -ErrorAction Continue }
            [pscustomobject]@{ DistinguishedName = '{{TargetDn}}'; ObjectGUID = [guid]'{{TargetGuid}}'; Enabled = $true; info = $null }
            """),
    ];

    [Theory]
    [InlineData("the disable")]
    [InlineData("the lockdown move")]
    [InlineData("the password reset")]
    public async Task EmergencyDisable_ARecordTheBoundObjectReReadLeaves_IsNotTakenForTheStepsError(string step)
    {
        // The re-read answers the bound object and leaves a record: the object is verified, so the step goes on, and its own
        // check reads only its own call - the write made and OK, the move made and stamped.
        var calls = new List<string>();
        var service = EmergencyDisable(() => StandIn(Import.Loads, calls, DisableDirectoryWithReReadNoise));

        switch (step)
        {
            case "the disable":
                Assert.Equal(new DisableStepResult("DisableAD", "OK", null), await service.ExecuteDisableAD(Target, Creds, adSlotHeld: true));
                Assert.Equal(["Import-Module|ActiveDirectory", $"Get-ADUser|{TargetDn}", $"Set-ADUser|{TargetDn}"], calls);
                break;
            case "the lockdown move":
                var (steps, outcome, stamp) = await service.ExecuteMoveToLockdownOu(Target, Creds, LockdownOu, adSlotHeld: true);
                Assert.Equal(LockdownOutcome.Moved, outcome);
                Assert.Equal(LockdownStampOutcome.Stamped, stamp);
                Assert.Equal(["LockdownMove", "LockdownStamp"], steps.Select(s => s.Step));
                Assert.All(steps, s => Assert.Equal("OK", s.Status));
                Assert.Equal([
                    "Import-Module|ActiveDirectory", $"Get-ADUser|{TargetDn}", $"Get-ADOrganizationalUnit|{LockdownOu}",
                    $"Move-ADObject|{TargetDn}|{LockdownOu}", $"Get-ADUser|{TargetGuid}", $"Set-ADUser|{TargetGuid}",
                ], calls);
                break;
            default:
                Assert.Equal(new DisableStepResult("ResetPassword", "OK", null), await service.ExecuteResetPassword(Target, Creds, adSlotHeld: true));
                Assert.Equal(["Import-Module|ActiveDirectory", $"Get-ADUser|{TargetDn}", $"Set-ADAccountPassword|{TargetDn}"], calls);
                break;
        }
    }

    // ---- Self-Service Groups (psl-8) --------------------------------------------------------------------
    // PrepareAdRunspace imported the module clearing only the commands. The member listing and the membership change
    // re-read the group by objectGUID straight after it, and the re-read's HadErrors check took the import's record:
    // "That group no longer exists, or could not be read." The owned-groups list and the single-group search met it at
    // the first group's DACL check, which excluded that group. The add path's group probe imported the same way, so its
    // check said no group answered.

    private const string SelfServiceGroupGuid = "6f1c2c7e-3b9a-4d55-9a51-1c2f0e4b7a10";

    private const string GetAdObjectProbe = """
        [CmdletBinding()] param($LDAPFilter, $Credential)
        $global:DirectoryCalls.Add('Get-ADObject')
        [pscustomobject]@{ Name = 'Finance Approvers'; ObjectClass = 'group' }
        """;

    private static PSCredential ProbeCredential() => new("EXAMPLE\\svc-directory", new System.Security.SecureString());

    [Fact]
    public void SelfService_AnImportThatReportsAnError_IsNotTakenForTheGroupReReadsError()
    {
        var calls = new List<string>();
        using var runspace = StandIn(Import.ReportsErrorWithoutThrowing, calls, ("Get-ADGroup", GetAdGroup));
        using var ps = Shell(runspace);

        SelfServiceGroupService.PrepareAdRunspace(ps);
        var group = SelfServiceGroupService.ResolveGroupByGuid(ps, ProbeCredential(), SelfServiceGroupGuid);

        Assert.NotNull(group);
        Assert.False(ps.HadErrors);
        Assert.Equal(["Import-Module|ActiveDirectory", $"Get-ADGroup|{SelfServiceGroupGuid}|"], calls);
    }

    [Fact]
    public void SelfService_AnImportThatReportsAnError_IsNotTakenForTheGroupProbesError()
    {
        var calls = new List<string>();
        using var runspace = StandIn(Import.ReportsErrorWithoutThrowing, calls, ("Get-ADObject", GetAdObjectProbe));
        using var ps = Shell(runspace);

        Assert.True(SelfServiceGroupService.GroupWithIdentityExists(ps, ProbeCredential(), "Finance Approvers"));
        Assert.Equal(["Import-Module|ActiveDirectory", "Get-ADObject"], calls);
    }

    [Fact]
    public void SelfService_AFailingImport_IsTheImportsFailure_AndNoReadIsSent()
    {
        var calls = new List<string>();
        using var runspace = StandIn(Import.Fails, calls, ("Get-ADGroup", GetAdGroup), ("Get-ADObject", GetAdObjectProbe));
        using var ps = Shell(runspace);

        var prepared = Assert.ThrowsAny<RuntimeException>(() => SelfServiceGroupService.PrepareAdRunspace(ps));
        var probed = Assert.ThrowsAny<RuntimeException>(
            () => SelfServiceGroupService.GroupWithIdentityExists(ps, ProbeCredential(), "Finance Approvers"));

        Assert.Contains(ImportFailure, prepared.Message, StringComparison.Ordinal);
        Assert.Contains(ImportFailure, probed.Message, StringComparison.Ordinal);
        Assert.Equal(["Import-Module|ActiveDirectory", "Import-Module|ActiveDirectory"], calls);
    }

    // ---- Self-Service Groups' caller and group reads (psl-10) -----------------------------------------
    // The owned-groups list and the single-group search resolve the caller by SID, read the groups, then check each group's
    // DACL on the same PowerShell. The caller read and the group read cleared only their commands, so a record either wrote
    // past Stop was the first DACL check's - HadErrors is true while any record remains - and that group was left out of the
    // list, or the searched group answered "not found, or you do not have permission". Both paths run for real here on a
    // stand-in session (the seams: the session, and the module's credential lookup).

    private const string CallerSid = "S-1-5-21-1000000000-1000000000-1000000000-1105";
    private const string SecondGroupDn = "CN=Travel Approvers,OU=Groups,DC=example,DC=test";
    private const string ReadNoise = "A record the read wrote past the callers ErrorAction.";

    // The caller (by SID), the groups (by -LDAPFilter: the caller's two for the ownership filter, the first by name for the
    // search's) and each group's DACL - one Allow WriteProperty on member for the caller, psl-5's stand-in. The read named
    // leaves a record without throwing and answers as before. Every group names the caller as its manager, so no other
    // owner is looked up.
    private static (string Name, string Body)[] SelfServiceDirectory(string noisyRead)
    {
        var callerNoise = noisyRead == "the caller read" ? $"Write-Error -Message '{ReadNoise}' -ErrorAction Continue" : "";
        var groupNoise = noisyRead == "the group read" ? $"Write-Error -Message '{ReadNoise}' -ErrorAction Continue" : "";
        return
        [
            ("Get-ADUser", $$"""
                [CmdletBinding()] param($Identity, $Credential)
                $global:DirectoryCalls.Add("Get-ADUser|$Identity")
                {{callerNoise}}
                [pscustomobject]@{ DistinguishedName = '{{TargetDn}}' }
                """),
            ("Get-ADGroup", $$"""
                [CmdletBinding()] param($Identity, $LDAPFilter, $Properties, $Credential)
                if ($LDAPFilter) {
                    $owned = $LDAPFilter -like '*managedBy=*'
                    $global:DirectoryCalls.Add("Get-ADGroup|" + $(if ($owned) { 'owned' } else { 'by name' }))
                    {{groupNoise}}
                    $dns = if ($owned) { '{{GroupDn}}', '{{SecondGroupDn}}' } else { '{{GroupDn}}' }
                    foreach ($dn in $dns) {
                        $name = ($dn -split ',')[0].Substring(3)
                        [pscustomobject]@{
                            DistinguishedName = $dn; ObjectGUID = [guid]::NewGuid(); Name = $name; SamAccountName = $name.Replace(' ', '')
                            GroupCategory = 'Security'; GroupScope = 'Global'; managedBy = '{{TargetDn}}'; msExchCoManagedByLink = $null
                        }
                    }
                    return
                }
                $global:DirectoryCalls.Add("Get-ADGroup|DACL|$Identity")
                $sd = [pscustomobject]@{}
                $sd | Add-Member -MemberType ScriptMethod -Name GetAccessRules -Value {
                    param($includeExplicit, $includeInherited, $targetType)
                    [pscustomobject]@{
                        AccessControlType = 'Allow'; ActiveDirectoryRights = 32; ObjectType = [guid]'bf9679c0-0de6-11d0-a285-00aa003049e2'
                        IdentityReference = [pscustomobject]@{ Value = '{{CallerSid}}' }; IsInherited = $false; PropagationFlags = 0
                    }
                }
                [pscustomobject]@{ DistinguishedName = $Identity; nTSecurityDescriptor = $sd }
                """),
        ];
    }

    /// <summary>The service with only its directory session and its credential replaced.</summary>
    private sealed class StandInSelfService(Func<Runspace> open)
        : SelfServiceGroupService(null!, null!, null!, NullLogger<SelfServiceGroupService>.Instance)
    {
        internal override Runspace CreateDirectoryRunspace() => open();

        internal override Task<ModuleCredentialLookup> LookupCredentialsAsync(string purpose) =>
            Task.FromResult(new ModuleCredentialLookup(Creds, ModuleCredentialFailure.None));
    }

    [Fact]
    public void SelfService_TheCallerRead_LeavesNoRecordForTheNextRead()
    {
        // The two paths read the groups straight after the caller, and that read clears too - so the caller read's own
        // clear is pinned here, on its own: its record gone, and the next read that checks HadErrors (the group's re-read by
        // objectGUID) answering.
        var calls = new List<string>();
        using var runspace = StandIn(Import.Loads, calls, SelfServiceDirectory("the caller read")[0], ("Get-ADGroup", GetAdGroup));
        using var ps = Shell(runspace);

        Assert.Equal(TargetDn, SelfServiceGroupService.ResolveCallerDn(ps, ProbeCredential(), CallerSid));
        Assert.Empty(ps.Streams.Error);
        Assert.Empty(ps.Commands.Commands);
        Assert.NotNull(SelfServiceGroupService.ResolveGroupByGuid(ps, ProbeCredential(), SelfServiceGroupGuid));
        Assert.Equal([$"Get-ADUser|{CallerSid}", $"Get-ADGroup|{SelfServiceGroupGuid}|"], calls);
    }

    [Theory]
    [InlineData("the caller read")]
    [InlineData("the group read")]
    public async Task SelfService_ARecordTheCallerOrGroupReadLeaves_DoesNotHideTheFirstOwnedGroup(string noisyRead)
    {
        var calls = new List<string>();
        var service = new StandInSelfService(() => StandIn(Import.Loads, calls, SelfServiceDirectory(noisyRead)));

        var owned = await service.GetOwnedGroupsAsync(CallerSid, [], []);

        Assert.Equal([GroupDn, SecondGroupDn], owned.Select(g => g.DistinguishedName));
        Assert.All(owned, g => Assert.True(g.CanManageMembers));
        Assert.Equal([
            "Import-Module|ActiveDirectory", $"Get-ADUser|{CallerSid}", "Get-ADGroup|owned",
            $"Get-ADGroup|DACL|{GroupDn}", $"Get-ADGroup|DACL|{SecondGroupDn}",
        ], calls);
    }

    [Theory]
    [InlineData("the caller read")]
    [InlineData("the group read")]
    public async Task SelfService_ARecordTheCallerOrGroupReadLeaves_DoesNotHideTheSearchedGroup(string noisyRead)
    {
        var calls = new List<string>();
        var service = new StandInSelfService(() => StandIn(Import.Loads, calls, SelfServiceDirectory(noisyRead)));

        var found = await service.SearchManageableGroupAsync(CallerSid, [], [], "Finance Approvers");

        Assert.Null(found.Message);
        var group = Assert.IsType<ManageableGroup>(found.Group);
        Assert.Equal(GroupDn, group.DistinguishedName);
        Assert.True(group.CanManageMembers);
        Assert.Equal(["Import-Module|ActiveDirectory", $"Get-ADUser|{CallerSid}", "Get-ADGroup|by name", $"Get-ADGroup|DACL|{GroupDn}"], calls);
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
