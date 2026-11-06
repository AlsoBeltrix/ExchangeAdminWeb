using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>Exercises the real SDK wiring using local functions, without importing AD.</summary>
public class Comms10kTargetPinTests
{
    private static readonly Guid ApprovedGuid = new("87663f48-10a7-44b3-8da3-0a7e4cc3de0b");

    [Fact]
    public void TheTargetIsFixedAndHasNoConfigFieldOrConfigRead()
    {
        Assert.Equal(ApprovedGuid, Comms10kService.TargetGroupGuid);
        var module = new ModuleCatalog().GetById("Comms10k");
        Assert.NotNull(module);
        Assert.Equal("DelineaSecretId", Assert.Single(module.ConfigFields).Key);

        var service = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "Comms10kService.cs"));
        Assert.DoesNotContain("TargetGroupName", service, StringComparison.Ordinal);
        Assert.Contains("return ReadTargetByGuid(ps, credential, TargetGroupGuid);", service, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("lookup")]
    [InlineData("preview")]
    [InlineData("clear")]
    [InlineData("add")]
    [InlineData("readback")]
    public void EveryGroupCommandCarriesTheApprovedGuidEvenWhenTheDnChanges(string operation)
    {
        using var harness = new Harness();

        harness.Invoke(operation);

        Assert.Equal(ApprovedGuid.ToString(), Assert.Single(harness.Identities));
        if (operation == "add")
            Assert.Equal([$"<GUID={Harness.MemberGuid:D}>"], harness.AddedMembers);
    }

    [Theory]
    [InlineData("lookup", "missing")]
    [InlineData("lookup", "multiple")]
    [InlineData("lookup", "wrong-guid")]
    [InlineData("lookup", "missing-guid")]
    [InlineData("lookup", "empty-guid")]
    [InlineData("lookup", "missing-dn")]
    [InlineData("lookup", "error")]
    [InlineData("preview", "missing")]
    [InlineData("preview", "multiple")]
    [InlineData("preview", "wrong-guid")]
    [InlineData("preview", "missing-guid")]
    [InlineData("preview", "empty-guid")]
    [InlineData("preview", "missing-dn")]
    [InlineData("preview", "error")]
    [InlineData("readback", "missing")]
    [InlineData("readback", "multiple")]
    [InlineData("readback", "wrong-guid")]
    [InlineData("readback", "missing-guid")]
    [InlineData("readback", "empty-guid")]
    [InlineData("readback", "missing-dn")]
    [InlineData("readback", "error")]
    public void EachGroupReadRefusesAnythingOtherThanExactlyThePinnedTarget(string operation, string response)
    {
        using var harness = new Harness(response);

        Assert.Throws<InvalidOperationException>(() => harness.Invoke(operation));

        Assert.Equal(ApprovedGuid.ToString(), Assert.Single(harness.Identities));
        Assert.Empty(harness.PrimaryQueries);
        Assert.Empty(harness.AddedMembers);
        Assert.Empty(harness.PowerShell.Commands.Commands);
    }

    [Theory]
    [InlineData("lookup")]
    [InlineData("clear")]
    [InlineData("add")]
    [InlineData("readback")]
    public void AnUnpinnedTargetIsRefusedBeforeAnyCommandIsInvoked(string operation)
    {
        using var harness = new Harness();

        Assert.Throws<InvalidOperationException>(() => harness.Invoke(operation, Guid.NewGuid()));

        Assert.Empty(harness.Identities);
        Assert.Empty(harness.AddedMembers);
        Assert.Empty(harness.PowerShell.Commands.Commands);
    }

    private sealed class Harness : IDisposable
    {
        public const string MemberDn = "CN=Uploaded member,DC=example,DC=test";
        public static readonly Guid MemberGuid = new("9d4e5c1d-ac4a-428c-a6d7-047828a30c58");
        private readonly Runspace _runspace;
        private readonly PSCredential _credential = new("EXAMPLE\\tester", new SecureString());
        public PowerShell PowerShell { get; }
        public List<string> Identities { get; } = new();
        public List<string> AddedMembers { get; } = new();
        public List<string> PrimaryQueries { get; } = new();

        public Harness(string response = "valid")
        {
            var state = InitialSessionState.CreateDefault2();
            state.Commands.Add(new SessionStateFunctionEntry("Get-ADGroup", """
                [CmdletBinding()]
                param($Identity, [string[]] $Properties, [pscredential] $Credential)
                $global:C1Identities.Add([string] $Identity)
                if ($global:C1Response -eq 'missing') { return }
                if ($global:C1Response -eq 'error') { Write-Error 'lookup unavailable' -ErrorAction Continue }
                $guid = $global:C1Guid
                if ($global:C1Response -eq 'wrong-guid') { $guid = [guid] '0d93e79c-11e9-4669-bb43-c89c715053ff' }
                if ($global:C1Response -eq 'missing-guid') { $guid = $null }
                if ($global:C1Response -eq 'empty-guid') { $guid = [guid]::Empty }
                $dn = 'CN=Moved broadcast,OU=New,DC=example,DC=test'
                if ($global:C1Response -eq 'missing-dn') { $dn = $null }
                $result = [pscustomobject]@{
                    ObjectGUID = $guid
                    DistinguishedName = $dn
                    member = @('CN=Existing member,DC=example,DC=test')
                    SID = 'S-1-5-21-100-200-300-4242'
                }
                $result
                if ($global:C1Response -eq 'multiple') { $result }
                """));
            state.Commands.Add(new SessionStateFunctionEntry("Set-ADGroup", """
                [CmdletBinding()]
                param($Identity, [string] $Clear, [hashtable] $Add, [pscredential] $Credential)
                $global:C1Identities.Add([string] $Identity)
                if ($Add) {
                    foreach ($dn in $Add['member']) { $global:C1AddedMembers.Add([string] $dn) }
                }
                """));
            state.Commands.Add(new SessionStateFunctionEntry("Get-ADObject", """
                [CmdletBinding()]
                param([string] $LDAPFilter, [string[]] $Properties, [pscredential] $Credential)
                if ($LDAPFilter -notlike '(primaryGroupID=*') {
                    [pscustomobject]@{ DistinguishedName = 'CN=Existing member,DC=example,DC=test'; ObjectGUID = [guid] '6263592c-0648-49c4-853f-bd8dfd1a590d' }
                    return
                }
                $global:C1PrimaryQueries.Add($LDAPFilter)
                """));

            _runspace = RunspaceFactory.CreateRunspace(state);
            _runspace.Open();
            _runspace.SessionStateProxy.SetVariable("C1Identities", Identities);
            _runspace.SessionStateProxy.SetVariable("C1AddedMembers", AddedMembers);
            _runspace.SessionStateProxy.SetVariable("C1PrimaryQueries", PrimaryQueries);
            _runspace.SessionStateProxy.SetVariable("C1Response", response);
            _runspace.SessionStateProxy.SetVariable("C1Guid", ApprovedGuid);
            PowerShell = PowerShell.Create();
            PowerShell.Runspace = _runspace;
        }

        public void Invoke(string operation, Guid? targetGuid = null)
        {
            // The previous DN could now belong to another group. Only the GUID is authoritative.
            var target = new Comms10kTarget("CN=Replacement at former path,DC=example,DC=test", targetGuid ?? ApprovedGuid);
            switch (operation)
            {
                case "lookup":
                    Comms10kService.ReadTargetByGuid(PowerShell, _credential, target.ObjectGuid);
                    break;
                case "preview":
                    Comms10kService.ReadMemberDns(PowerShell, _credential);
                    break;
                case "clear":
                    Comms10kService.ClearMembers(PowerShell, _credential, target);
                    break;
                case "add":
                    Comms10kService.AddMemberBatch(PowerShell, _credential, target, [MemberGuid]);
                    break;
                case "readback":
                    Comms10kService.ReadBackMembers(PowerShell, _credential, target);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        public void Dispose()
        {
            PowerShell.Dispose();
            _runspace.Dispose();
            _credential.Password.Dispose();
        }
    }
}
