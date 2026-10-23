using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// psl-3. Runs the service's actual authorize and deauthorize sequences (module import, domain
/// controller discovery with its fallback, the remote change) on an opened local runspace whose
/// functions stand in for Import-Module, Get-ADDomainController and Invoke-Command. Nothing is
/// imported, no credential is used and no remote command runs.
///
/// <c>HadErrors</c> after an invocation is true while <c>Streams.Error</c> holds any record, stale
/// ones included, so a record a failed discovery attempt left behind both joined a later change's
/// failure text and turned a successful change into "Failed to authorize".
/// </summary>
public sealed class DhcpAuthorizationPowerShellLifecycleTests
{
    private const string DnsName = "dhcp01.example.test";
    private const string Ip = "10.0.0.5";
    private const string Dc = "dc01.example.test";
    private static readonly (string username, string password, string domain) Creds =
        ("svc-dhcp", "not-a-secret", "EXAMPLE");

    private const string FoundDc = "[pscustomobject]@{ HostName = 'dc01.example.test' }";
    private const string FailsAfterARecord =
        "Write-Error 'discover context' -ErrorAction Continue; throw 'discover failed'";

    [Theory]
    [InlineData("authorize")]
    [InlineData("deauthorize")]
    public void FailedDiscovery_Fallback_ThenChangeFailure_ReportsOnlyTheChangesErrors(string operation)
    {
        using var session = new Session(discover: FailsAfterARecord,
            invokeCommand: "Write-Error 'remote change refused' -ErrorAction Continue");

        var result = session.Run(operation);

        Assert.False(result.Success);
        Assert.Equal($"Failed to {operation}: remote change refused", result.Message);
        // The failed discovery did not replay into the fallback or the change.
        Assert.Equal([
            "Import-Module|ActiveDirectory",
            "Get-ADDomainController|Discover",
            "Get-ADDomainController|Filter=*",
            $"Invoke-Command|{Dc}|{Cmdlet(operation)}|{DnsName},{Ip}"
        ], session.Calls);
    }

    [Theory]
    [InlineData("authorize")]
    [InlineData("deauthorize")]
    public void FailedDiscovery_Fallback_ThenCleanChange_Succeeds(string operation)
    {
        using var session = new Session(discover: FailsAfterARecord);

        var result = session.Run(operation);

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Successfully {operation}d DHCP server {DnsName} ({Ip}).", result.Message);
    }

    [Theory]
    [InlineData("authorize")]
    [InlineData("deauthorize")]
    public void DiscoveryThatSucceedsWithARecord_ThenCleanChange_Succeeds(string operation)
    {
        using var session = new Session(discover: "Write-Error 'discover soft' -ErrorAction Continue; " + FoundDc);

        var result = session.Run(operation);

        Assert.True(result.Success, result.Message);
        Assert.Equal([
            "Import-Module|ActiveDirectory",
            "Get-ADDomainController|Discover",
            $"Invoke-Command|{Dc}|{Cmdlet(operation)}|{DnsName},{Ip}"
        ], session.Calls);
    }

    [Theory]
    [InlineData("authorize")]
    [InlineData("deauthorize")]
    public void EmptyDiscovery_FallbackThatSucceedsWithARecord_ThenCleanChange_Succeeds(string operation)
    {
        using var session = new Session(discover: "",
            filter: "Write-Error 'filter soft' -ErrorAction Continue; " + FoundDc);

        var result = session.Run(operation);

        Assert.True(result.Success, result.Message);
    }

    [Theory]
    [InlineData("authorize")]
    [InlineData("deauthorize")]
    public void ImportNoise_ThenCleanChange_Succeeds(string operation)
    {
        using var session = new Session(importModule: "Write-Error 'import noise' -ErrorAction Continue");

        var result = session.Run(operation);

        Assert.True(result.Success, result.Message);
    }

    // Control: the fix must not drop the change's OWN diagnostics before its consumer reads them.
    [Theory]
    [InlineData("authorize")]
    [InlineData("deauthorize")]
    public void TheChangesOwnRecords_AreAllReported(string operation)
    {
        using var session = new Session(invokeCommand:
            "Write-Error 'first refusal' -ErrorAction Continue; Write-Error 'second refusal' -ErrorAction Continue");

        var result = session.Run(operation);

        Assert.False(result.Success);
        Assert.Equal($"Failed to {operation}: first refusal; second refusal", result.Message);
    }

    // ---- psl-10: the remote script's import cannot fail the change after it ----------------------------
    // The script imports DhcpServer and makes the change in ONE Invoke-Command, whose records come back in the one stream the
    // change's HadErrors check reads, so a record the import wrote past Stop reported a change that had been made as failed.
    // Here the script itself runs: the stand-in Invoke-Command runs the script block it is handed in a second runspace,
    // standing for the remote session, whose Import-Module and Add-/Remove-DhcpServerInDC are local advanced functions, and
    // writes that session's error records into its own stream - a terminating error as a record (*assumption, documented
    // remoting behaviour, not probed*: Invoke-Command -ComputerName without -ErrorAction Stop does that).

    private const string RemoteImportNoise = "A record the DhcpServer import wrote past the callers ErrorAction.";
    private const string RemoteImportFailure =
        "The specified module DhcpServer was not loaded because no valid module file was found in any module directory.";

    [Theory]
    [InlineData("authorize")]
    [InlineData("deauthorize")]
    public void ARemoteImportThatLeavesARecord_DoesNotFailTheChangeAfterIt(string operation)
    {
        using var session = new RemoteSession(remoteImport: $"Write-Error -Message '{RemoteImportNoise}' -ErrorAction Continue");

        var result = session.Run(operation);

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Successfully {operation}d DHCP server {DnsName} ({Ip}).", result.Message);
        Assert.Equal([
            "Import-Module|ActiveDirectory", "Get-ADDomainController|Discover", $"Invoke-Command|{Dc}",
            "remote Import-Module|DhcpServer", $"remote {Cmdlet(operation)}-DhcpServerInDC|{DnsName}|{Ip}",
        ], session.Calls);
    }

    [Theory]
    [InlineData("authorize")]
    [InlineData("deauthorize")]
    public void ARemoteImportThatFails_FailsTheChange_AndTheChangeIsNotMade(string operation)
    {
        // Stop still throws the import's own failure, so the script stops before the change: the redirection drops only a
        // record the import writes and carries on past.
        using var session = new RemoteSession(remoteImport: $"Write-Error -Message '{RemoteImportFailure}'");

        var result = session.Run(operation);

        Assert.False(result.Success);
        Assert.StartsWith($"Failed to {operation}: ", result.Message, StringComparison.Ordinal);
        Assert.Contains(RemoteImportFailure, result.Message, StringComparison.Ordinal);
        Assert.Equal("remote Import-Module|DhcpServer", session.Calls[^1]);
    }

    [Theory]
    [InlineData("authorize")]
    [InlineData("deauthorize")]
    public void TheRemoteChangesOwnError_StillFailsIt(string operation)
    {
        // Control: the change's own record is not the import's, and still fails the change.
        using var session = new RemoteSession(remoteChange: "Write-Error -Message 'remote change refused' -ErrorAction Continue");

        var result = session.Run(operation);

        Assert.False(result.Success);
        Assert.Equal($"Failed to {operation}: remote change refused", result.Message);
    }

    private static string Cmdlet(string operation) => operation == "authorize" ? "Add" : "Remove";

    private sealed class RemoteSession : IDisposable
    {
        private readonly Runspace _runspace = RunspaceFactory.CreateRunspace();
        private readonly DhcpAuthorizationService _service =
            new(NullLogger<DhcpAuthorizationService>.Instance, null!, null!);

        public PowerShell Ps { get; } = PowerShell.Create();
        public List<string> Calls { get; } = new();

        public RemoteSession(string remoteImport = "", string remoteChange = "")
        {
            _runspace.Open();
            _runspace.SessionStateProxy.SetVariable("calls", Calls);
            // The remote session's commands: advanced functions, so the script's -ErrorAction Stop acts on them as on the
            // cmdlets; autoloading off, so nothing there can load a module.
            _runspace.SessionStateProxy.SetVariable("remoteDefinitions", $$"""
                $global:PSModuleAutoloadingPreference = 'None'
                function global:Import-Module {
                    [CmdletBinding()] param([Parameter(Position = 0)] $Name)
                    $global:calls.Add("remote Import-Module|$Name")
                    {{remoteImport}}
                }
                function global:Add-DhcpServerInDC {
                    [CmdletBinding()] param($DnsName, $IPAddress)
                    $global:calls.Add("remote Add-DhcpServerInDC|$DnsName|$IPAddress")
                    {{remoteChange}}
                }
                function global:Remove-DhcpServerInDC {
                    [CmdletBinding()] param($DnsName, $IPAddress)
                    $global:calls.Add("remote Remove-DhcpServerInDC|$DnsName|$IPAddress")
                    {{remoteChange}}
                }
                """);
            using (var init = PowerShell.Create())
            {
                init.Runspace = _runspace;
                init.AddScript("""
                    function global:Import-Module {
                        param($Name, $ErrorAction)
                        $global:calls.Add("Import-Module|$Name")
                    }
                    function global:Get-ADDomainController {
                        param($Discover, $Service, $Filter, $Credential, $ErrorAction)
                        $global:calls.Add('Get-ADDomainController|Discover')
                        [pscustomobject]@{ HostName = 'dc01.example.test' }
                    }
                    function global:Invoke-Command {
                        [CmdletBinding()] param($ComputerName, $Credential, $ScriptBlock, $ArgumentList)
                        $global:calls.Add("Invoke-Command|$ComputerName")
                        $remote = [runspacefactory]::CreateRunspace()
                        $remote.Open()
                        $remote.SessionStateProxy.SetVariable('calls', $global:calls)
                        $p = [powershell]::Create()
                        $p.Runspace = $remote
                        try {
                            $null = $p.AddScript($global:remoteDefinitions).Invoke()
                            $p.Commands.Clear()
                            $null = $p.AddScript($ScriptBlock.ToString())
                            foreach ($argument in $ArgumentList) { $null = $p.AddArgument($argument) }
                            try { $null = $p.Invoke() }
                            catch {
                                $PSCmdlet.WriteError([System.Management.Automation.ErrorRecord]::new(
                                    $_.Exception.InnerException, 'RemoteScriptTerminated', 'NotSpecified', $ComputerName))
                            }
                            foreach ($record in $p.Streams.Error) { $PSCmdlet.WriteError($record) }
                        }
                        finally {
                            $p.Dispose()
                            $remote.Dispose()
                        }
                    }
                    """);
                init.Invoke();
                Assert.Empty(init.Streams.Error);
            }
            Ps.Runspace = _runspace;
        }

        public DhcpOperationResult Run(string operation) => operation == "authorize"
            ? _service.AuthorizeServer(Ps, Creds, DnsName, Ip)
            : _service.DeauthorizeServer(Ps, Creds, DnsName, Ip);

        public void Dispose()
        {
            Ps.Dispose();
            _runspace.Dispose();
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly Runspace _runspace = RunspaceFactory.CreateRunspace();
        private readonly DhcpAuthorizationService _service =
            new(NullLogger<DhcpAuthorizationService>.Instance, null!, null!);

        public PowerShell Ps { get; } = PowerShell.Create();
        public List<string> Calls { get; } = new();

        // Plain (non-advanced) functions: ErrorAction binds as an ordinary parameter, so an
        // injected throw stays terminating at the SDK boundary, as in the psl-1 stand-ins.
        public Session(string importModule = "", string discover = FoundDc, string filter = FoundDc, string invokeCommand = "")
        {
            _runspace.Open();
            _runspace.SessionStateProxy.SetVariable("calls", Calls);
            using (var init = PowerShell.Create())
            {
                init.Runspace = _runspace;
                init.AddScript($$"""
                    function global:Import-Module {
                        param($Name, $ErrorAction)
                        $global:calls.Add("Import-Module|$Name")
                        {{importModule}}
                    }
                    function global:Get-ADDomainController {
                        param($Discover, $Service, $Filter, $Credential, $ErrorAction)
                        if ($Discover) {
                            $global:calls.Add('Get-ADDomainController|Discover')
                            {{discover}}
                        } else {
                            $global:calls.Add("Get-ADDomainController|Filter=$Filter")
                            {{filter}}
                        }
                    }
                    function global:Invoke-Command {
                        param($ComputerName, $Credential, $ScriptBlock, $ArgumentList)
                        $cmdlet = if ("$ScriptBlock" -match 'Add-DhcpServerInDC') { 'Add' } elseif ("$ScriptBlock" -match 'Remove-DhcpServerInDC') { 'Remove' } else { '?' }
                        $global:calls.Add("Invoke-Command|$ComputerName|$cmdlet|$($ArgumentList -join ',')")
                        {{invokeCommand}}
                    }
                    """);
                init.Invoke();
                Assert.Empty(init.Streams.Error);
            }
            Ps.Runspace = _runspace;
        }

        public DhcpOperationResult Run(string operation) => operation == "authorize"
            ? _service.AuthorizeServer(Ps, Creds, DnsName, Ip)
            : _service.DeauthorizeServer(Ps, Creds, DnsName, Ip);

        public void Dispose()
        {
            Ps.Dispose();
            _runspace.Dispose();
        }
    }
}
