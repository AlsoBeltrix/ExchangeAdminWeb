using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Uses the actual PowerShell SDK and the service's operations. The AD-named functions are
/// harmless local stand-ins: no AD module is loaded and no directory is contacted.
/// </summary>
public class Comms10kPowerShellLifecycleTests
{
    [Theory]
    [InlineData("clear")]
    [InlineData("add")]
    [InlineData("members")]
    [InlineData("primary")]
    [InlineData("target")]
    public void ATerminatingFailureLeavesNoQueuedCommand(string operation)
    {
        using var harness = new Harness(operation);

        var error = Assert.ThrowsAny<RuntimeException>(() => harness.Invoke(operation));

        Assert.Contains($"fatal {operation}", error.Message, StringComparison.Ordinal);
        Assert.Empty(harness.PowerShell.Commands.Commands);
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("add")]
    [InlineData("members")]
    [InlineData("primary")]
    [InlineData("target")]
    public void ATerminatingFailureLeavesNoErrorStreamContamination(string operation)
    {
        using var harness = new Harness(operation);

        Assert.ThrowsAny<RuntimeException>(() => harness.Invoke(operation));

        Assert.Empty(harness.PowerShell.Streams.Error);
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("add")]
    public void RetryingAFailedWriteExecutesOnlyTheNewAttempt(string operation)
    {
        using var harness = new Harness(operation);
        Assert.ThrowsAny<RuntimeException>(() => harness.Invoke(operation));

        // Retry the same real service operation after the local function's one-shot failure.
        // The retry classifier has separate coverage; this pins what the SDK actually invokes.
        harness.Invoke(operation);

        Assert.Equal(new[] { operation, operation }, harness.Calls);
        Assert.Empty(harness.PowerShell.Commands.Commands);
        Assert.Empty(harness.PowerShell.Streams.Error);
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("add")]
    public void TheWritersReadBackAfterAFailureCannotReplayTheMutation(string operation)
    {
        using var harness = new Harness(operation);

        var result = Comms10kReplaceWriter.ExecuteSequence(
            "Local broadcast", [Harness.NewMember],
            clear: () => harness.Invoke("clear"),
            addBatch: _ => harness.Invoke("add"),
            readBack: () => Comms10kService.ReadBackMembers(
                harness.PowerShell, harness.Credential, Harness.Target));

        var expectedCalls = operation == "clear"
            ? new[] { "clear", "members", "primary" }
            : new[] { "clear", "add", "members", "primary" };
        Assert.Equal(expectedCalls, harness.Calls);
        Assert.Equal(operation == "clear" ? 1 : 0, result.FinalCount);
        Assert.Equal(Comms10kOutcome.PartlyApplied, result.Outcome);
        Assert.Empty(harness.PowerShell.Commands.Commands);
        Assert.Empty(harness.PowerShell.Streams.Error);
    }

    private sealed class Harness : IDisposable
    {
        public const string NewMember = "CN=New member,DC=example,DC=test";
        public static readonly Comms10kTarget Target = new(
            "CN=Local broadcast,DC=example,DC=test", Comms10kService.TargetGroupGuid);

        private readonly Runspace _runspace;
        public PowerShell PowerShell { get; }
        public PSCredential Credential { get; } = new("EXAMPLE\\tester", new SecureString());
        public List<string> Calls { get; } = new();

        public Harness(string failingOperation)
        {
            var state = InitialSessionState.CreateDefault2();
            state.Commands.Add(new SessionStateFunctionEntry("Invoke-LocalOperation", """
                param([string] $Operation)
                $global:C9Calls.Add($Operation)
                if ($global:C9FailOperation -eq $Operation) {
                    $global:C9FailOperation = $null
                    Write-Error "soft $Operation" -ErrorAction Continue
                    throw "fatal $Operation"
                }
                """));
            state.Commands.Add(new SessionStateFunctionEntry("Set-ADGroup", """
                [CmdletBinding()]
                param($Identity, [string] $Clear, [hashtable] $Add, [pscredential] $Credential)
                if ($PSBoundParameters.ContainsKey('Clear')) {
                    Invoke-LocalOperation 'clear'
                    $global:C9Members.Clear()
                } else {
                    Invoke-LocalOperation 'add'
                    foreach ($dn in $Add['member']) { $global:C9Members.Add($dn) }
                }
                """));
            state.Commands.Add(new SessionStateFunctionEntry("Get-ADGroup", """
                [CmdletBinding()]
                param($Identity, [string[]] $Properties, [pscredential] $Credential)
                if ($Properties -contains 'member') {
                    Invoke-LocalOperation 'members'
                } else {
                    Invoke-LocalOperation 'target'
                }
                [pscustomobject]@{
                    DistinguishedName = 'CN=Local broadcast,DC=example,DC=test'
                    ObjectGUID = $global:C9TargetGuid
                    SID = 'S-1-5-21-100-200-300-4242'
                    member = $global:C9Members.ToArray()
                }
                """));
            state.Commands.Add(new SessionStateFunctionEntry("Get-ADObject", """
                [CmdletBinding()]
                param([string] $LDAPFilter, [pscredential] $Credential)
                Invoke-LocalOperation 'primary'
                """));

            _runspace = RunspaceFactory.CreateRunspace(state);
            _runspace.Open();
            _runspace.SessionStateProxy.SetVariable("C9Calls", Calls);
            _runspace.SessionStateProxy.SetVariable("C9TargetGuid", Target.ObjectGuid);
            _runspace.SessionStateProxy.SetVariable("C9FailOperation", failingOperation);
            _runspace.SessionStateProxy.SetVariable("C9Members", new List<string>
            {
                "CN=Existing member,DC=example,DC=test"
            });
            PowerShell = PowerShell.Create();
            PowerShell.Runspace = _runspace;
        }

        public void Invoke(string operation)
        {
            switch (operation)
            {
                case "clear":
                    Comms10kService.ClearMembers(PowerShell, Credential, Target);
                    break;
                case "add":
                    Comms10kService.AddMemberBatch(PowerShell, Credential, Target, [NewMember]);
                    break;
                case "members":
                case "primary":
                    Comms10kService.ReadBackMembers(PowerShell, Credential, Target);
                    break;
                case "target":
                    Comms10kService.ReadTargetByGuid(PowerShell, Credential, Target.ObjectGuid);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        public void Dispose()
        {
            PowerShell.Dispose();
            _runspace.Dispose();
            Credential.Password.Dispose();
        }
    }
}
