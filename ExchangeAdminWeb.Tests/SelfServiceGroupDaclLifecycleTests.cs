using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Security;
using ExchangeAdminWeb.Services.SelfServiceGroups;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// psl-5. GetOwnedGroupsAsync calls CallerCanManageMembers once per owned group on one PowerShell.
/// A DACL read that leaves an error record and then throws is excluded (fail-closed, correct), but
/// the record used to survive, and <c>HadErrors</c> after the NEXT group's clean read is true while
/// any record remains, so that group was silently excluded too.
///
/// The existing private method is reached by reflection, as psl-1 did; local functions stand in for
/// Get-ADGroup and the DACL's GetAccessRules. No AD module, directory or credential use.
/// </summary>
public sealed class SelfServiceGroupDaclLifecycleTests : IDisposable
{
    private const string CallerSid = "S-1-5-21-1000000000-1000000000-1000000000-1105";

    private readonly Runspace _runspace = RunspaceFactory.CreateRunspace();
    private readonly PowerShell _ps = PowerShell.Create();
    private readonly List<string> _calls = new();

    public SelfServiceGroupDaclLifecycleTests()
    {
        _runspace.Open();
        _runspace.SessionStateProxy.SetVariable("calls", _calls);
        _runspace.SessionStateProxy.SetVariable("callerSid", CallerSid);
        using (var init = PowerShell.Create())
        {
            init.Runspace = _runspace;
            // A plain function with an ordinary ErrorAction parameter, as in the psl-1 stand-ins.
            // CN=broken leaves a record and then throws; CN=soft leaves a record without throwing;
            // every group's DACL holds one explicit Allow WriteProperty on `member` for the caller.
            init.AddScript("""
                function global:Get-ADGroup {
                    param($Identity, $Properties, $Credential, $ErrorAction)
                    $global:calls.Add("Get-ADGroup|$Identity")
                    if ($Identity -eq 'CN=broken') { Write-Error 'dacl context' -ErrorAction Continue; throw 'dacl read failed' }
                    if ($Identity -eq 'CN=soft') { Write-Error 'dacl soft' -ErrorAction Continue }
                    $sd = [pscustomobject]@{}
                    $sd | Add-Member -MemberType ScriptMethod -Name GetAccessRules -Value {
                        param($includeExplicit, $includeInherited, $targetType)
                        [pscustomobject]@{
                            AccessControlType = 'Allow'
                            ActiveDirectoryRights = 32
                            ObjectType = [guid]'bf9679c0-0de6-11d0-a285-00aa003049e2'
                            IdentityReference = [pscustomobject]@{ Value = $global:callerSid }
                            IsInherited = $false
                            PropagationFlags = 0
                        }
                    }
                    [pscustomobject]@{ nTSecurityDescriptor = $sd }
                }
                """);
            init.Invoke();
            Assert.Empty(init.Streams.Error);
        }
        _ps.Runspace = _runspace;
    }

    public void Dispose()
    {
        _ps.Dispose();
        _runspace.Dispose();
    }

    [Fact]
    public void ACleanDaclRead_GrantingTheCaller_IsManageable()
    {
        Assert.True(CanManage("CN=next"));
    }

    [Fact]
    public void AFailedDaclRead_IsExcluded_AndDoesNotHideTheNextGroup()
    {
        Assert.False(CanManage("CN=broken"));
        Assert.True(CanManage("CN=next"));
        Assert.Equal(["Get-ADGroup|CN=broken", "Get-ADGroup|CN=next"], _calls);
        Assert.Empty(_ps.Streams.Error);
        Assert.Empty(_ps.Commands.Commands);
    }

    // Control: the non-throw HadErrors branch already cleared; it stays fail-closed for its own group.
    [Fact]
    public void ARecordWithoutAThrow_ExcludesOnlyItsOwnGroup()
    {
        Assert.False(CanManage("CN=soft"));
        Assert.True(CanManage("CN=next"));
        Assert.Empty(_ps.Streams.Error);
    }

    // aud-a4b finding 7 made the method an instance member (it logs through the service), so the
    // test constructs a service whose only live dependency is the logger; nothing else is touched.
    private readonly SelfServiceGroupService _service = new(
        null!, null!, null!, NullLogger<SelfServiceGroupService>.Instance);

    private bool CanManage(string groupDn) =>
        (bool)typeof(SelfServiceGroupService)
            .GetMethod("CallerCanManageMembers", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_service, [_ps, new PSCredential("svc-groups", new SecureString()), groupDn, CallerSid,
                Array.Empty<string>(), Array.Empty<string>()])!;
}
