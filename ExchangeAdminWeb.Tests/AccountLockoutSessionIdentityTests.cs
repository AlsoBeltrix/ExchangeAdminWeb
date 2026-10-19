using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A5: lockout remediation matched a logon session by the bare USERNAME column
/// <c>quser</c> prints, which carries no domain, so guarding <c>A\alex</c> could log off
/// <c>B\alex</c> or a local <c>alex</c>.
/// </summary>
/// <remarks>
/// These run the shipped script. <c>RemoteSessionScript</c> is read off the service by
/// reflection and executed in a local runspace with <c>quser.exe</c>, the CIM calls and
/// <c>logoff.exe</c> replaced by functions - PowerShell resolves a function ahead of a native
/// command, so the script under test is unmodified. That makes these behaviour tests rather
/// than source scans, which matters more here than anywhere else in the repo: this is the
/// module that signs people out.
///
/// What they do NOT cover is stated on the finding: no real <c>quser</c>, no real WinRM hop and
/// no real machine.
/// </remarks>
public class AccountLockoutSessionIdentityTests
{
    private const string AlexInDomainA = "S-1-5-21-111-111-111-1001";
    private const string AlexInDomainB = "S-1-5-21-222-222-222-1001";

    private static string Script() =>
        (string)typeof(AccountLockoutRemediationService)
            .GetField("RemoteSessionScript", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetValue(null)!;

    /// <summary>
    /// <paramref name="OwnerReadable"/> false models what the real GetOwnerSid is documented to
    /// do on a process the caller cannot inspect - it returns a non-zero code rather than a SID.
    /// </summary>
    private sealed record SessionProcess(int SessionId, string OwnerSid, bool OwnerReadable = true);

    private sealed record ScriptRun(List<PSObject> Rows, List<string> LogoffCalls)
    {
        public List<string> CallOrder { get; init; } = [];

        public PSObject Single() => Assert.Single(Rows);
        public static string Field(PSObject row, string name) => row.Properties[name]?.Value?.ToString() ?? "";
        public IEnumerable<string> Actions => Rows.Select(r => Field(r, "Action"));
    }

    /// <summary>
    /// Two quser columns are mandatory for the script's own parsing (at least five, and a
    /// numeric session id), so the rows are built rather than hand-written.
    /// </summary>
    private static string QuserRow(string userName, int sessionId, string state) =>
        $"{userName}               console             {sessionId}  {state}          none   1/1/2026 9:00";

    private static ScriptRun Run(
        string[] targetSids,
        IEnumerable<SessionProcess> processes,
        IEnumerable<string> quserRows,
        bool execute = true,
        Dictionary<string, string>? displayNames = null)
    {
        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        var quserText = new List<string> { "USERNAME              SESSIONNAME        ID  STATE   IDLE TIME  LOGON TIME" };
        quserText.AddRange(quserRows);

        runspace.SessionStateProxy.SetVariable("stubQuser", quserText.ToArray());
        runspace.SessionStateProxy.SetVariable("stubProcs", processes
            .Select(p => new PSObject(new { SessionId = p.SessionId, OwnerSid = p.OwnerSid, Readable = p.OwnerReadable }))
            .ToArray());
        runspace.SessionStateProxy.SetVariable("stubLogoffCalls", new List<string>());
        runspace.SessionStateProxy.SetVariable("stubCallOrder", new List<string>());

        // Functions win over native commands in PowerShell's command resolution, so the shipped
        // script runs unaltered while quser, CIM and logoff are all under the test's control.
        ps.AddScript("""
            function quser.exe { $global:stubCallOrder.Add('quser'); $global:LASTEXITCODE = 0; $global:stubQuser }
            function Get-CimInstance { param($ClassName, $Filter, $ErrorAction) $global:stubCallOrder.Add('cim'); $global:stubProcs }
            function Invoke-CimMethod {
                param($InputObject, $MethodName, $ErrorAction)
                if (-not $InputObject.Readable) { [pscustomobject]@{ ReturnValue = 2; Sid = $null } }
                else { [pscustomobject]@{ ReturnValue = 0; Sid = $InputObject.OwnerSid } }
            }
            function logoff.exe { param($id) $global:stubLogoffCalls.Add([string]$id); $global:LASTEXITCODE = 0 }
            """);
        ps.Invoke();
        ps.Commands.Clear();

        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [Environment.MachineName] = targetSids,
        };

        ps.AddScript(Script())
          .AddArgument(map)
          .AddArgument(execute)
          .AddArgument(displayNames ?? []);

        var rows = ps.Invoke().ToList();
        Assert.True(ps.Streams.Error.Count == 0,
            "Script wrote to the error stream: " + string.Join(" | ", ps.Streams.Error.Select(e => e.ToString())));

        var calls = (List<string>)runspace.SessionStateProxy.GetVariable("stubLogoffCalls");
        var order = (List<string>)runspace.SessionStateProxy.GetVariable("stubCallOrder");
        return new ScriptRun(rows, calls) { CallOrder = order };
    }

    // ---- the defect itself ---------------------------------------------------------------

    [Fact]
    public void ASessionOwnedByTheSameNameInAnotherDomain_IsNotLoggedOff()
    {
        // One machine, one session, owned by B\alex. The target is A\alex. quser shows "alex"
        // for both - which is exactly how the old code logged off the wrong person.
        //
        // The display name is supplied deliberately: without it nothing in this run carries the
        // string "alex" on the target's side, and a name-matching regression would pass for the
        // wrong reason. With it, any code that matches on the displayed name logs off session 2.
        var run = Run(
            targetSids: [AlexInDomainA],
            processes: [new SessionProcess(2, AlexInDomainB)],
            quserRows: [QuserRow("alex", 2, "Active")],
            displayNames: new Dictionary<string, string> { [AlexInDomainA] = "alex" });

        Assert.Empty(run.LogoffCalls);
        Assert.Equal("not-logged-on", ScriptRun.Field(run.Single(), "Action"));
    }

    [Fact]
    public void TheTargetsOwnSession_IsLoggedOff()
    {
        var run = Run(
            targetSids: [AlexInDomainA],
            processes: [new SessionProcess(2, AlexInDomainA)],
            quserRows: [QuserRow("alex", 2, "Active")],
            displayNames: new Dictionary<string, string> { [AlexInDomainA] = "alex" });

        Assert.Equal(["2"], run.LogoffCalls);
        var row = run.Single();
        Assert.Equal("logged-off", ScriptRun.Field(row, "Action"));
        Assert.Equal("alex", ScriptRun.Field(row, "User"));
        Assert.Equal("2", ScriptRun.Field(row, "SessionId"));
    }

    [Fact]
    public void TheRightSessionIsPickedOutOfSeveralSharingTheDisplayedName()
    {
        var run = Run(
            targetSids: [AlexInDomainA],
            processes: [new SessionProcess(2, AlexInDomainB), new SessionProcess(3, AlexInDomainA)],
            quserRows: [QuserRow("alex", 2, "Active"), QuserRow("alex", 3, "Disc")],
            displayNames: new Dictionary<string, string> { [AlexInDomainA] = "alex" });

        // Session 2 is the other domain's alex. Matching on the displayed name would take both.
        Assert.Equal(["3"], run.LogoffCalls);
        Assert.Contains("logged-off", run.Actions);
    }

    // ---- fail closed ------------------------------------------------------------------------

    [Fact]
    public void ASessionWhoseOwnerCannotBeIdentified_IsRefusedAndReported()
    {
        // No process rows for session 2, so nothing attributes it to anyone. It may well BE the
        // target, which is the whole reason it must not be acted on silently.
        var run = Run(
            targetSids: [AlexInDomainA],
            processes: [],
            quserRows: [QuserRow("alex", 2, "Active")]);

        Assert.Empty(run.LogoffCalls);
        var unverified = Assert.Single(run.Rows, r => ScriptRun.Field(r, "Action") == "owner-unverified");
        Assert.False((bool)unverified.Properties["Success"].Value);
        Assert.Equal("2", ScriptRun.Field(unverified, "SessionId"));
    }

    [Fact]
    public void ASessionWithSeveralDistinctOwners_IsRefusedRatherThanGuessedAt()
    {
        var run = Run(
            targetSids: [AlexInDomainA],
            processes: [new SessionProcess(2, AlexInDomainA), new SessionProcess(2, AlexInDomainB)],
            quserRows: [QuserRow("alex", 2, "Active")]);

        Assert.Empty(run.LogoffCalls);
        Assert.Contains("owner-unverified", run.Actions);
    }

    [Fact]
    public void MachineAndServiceIdentitiesNeverCountAsASessionOwner()
    {
        // Every interactive session also runs processes as SYSTEM and friends. If those counted,
        // the "exactly one owner" rule would refuse every real session.
        var run = Run(
            targetSids: [AlexInDomainA],
            processes:
            [
                new SessionProcess(2, "S-1-5-18"),
                new SessionProcess(2, "S-1-5-19"),
                new SessionProcess(2, "S-1-5-20"),
                new SessionProcess(2, "S-1-5-80-3880718306-3832830129-1677859214-2598158968-1052248003"),
                new SessionProcess(2, AlexInDomainA),
            ],
            quserRows: [QuserRow("alex", 2, "Active")]);

        Assert.Equal(["2"], run.LogoffCalls);
        Assert.Contains("logged-off", run.Actions);
    }

    // ---- review of aud-a5 -------------------------------------------------------------------

    [Fact]
    public void AnUnreadableProcessOwnerMakesTheSessionUnverified_NotSomebodyElsesProof()
    {
        // Review finding 1. B owns session 2 and is running something as A. B's own processes
        // cannot be read; A's can. Discarding the unreadable ones left the set {A}, so targeting
        // A logged off B. Windows allows running a process as another account inside your own
        // desktop, and GetOwnerSid is documented to return access-denied.
        var run = Run(
            targetSids: [AlexInDomainA],
            processes:
            [
                new SessionProcess(2, AlexInDomainB, OwnerReadable: false),
                new SessionProcess(2, AlexInDomainA),
            ],
            quserRows: [QuserRow("alex", 2, "Active")]);

        Assert.Empty(run.LogoffCalls);
        var row = Assert.Single(run.Rows, r => ScriptRun.Field(r, "Action") == "owner-unverified");
        Assert.Contains("could not be read", ScriptRun.Field(row, "Detail"), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableProcessInTheTargetsOwnSession_StillRefuses()
    {
        // Fail closed means fail closed: even when the one readable owner IS the target, an
        // owner we could not read might be the real occupant.
        var run = Run(
            targetSids: [AlexInDomainA],
            processes:
            [
                new SessionProcess(3, AlexInDomainA),
                new SessionProcess(3, "S-1-5-21-999-999-999-1001", OwnerReadable: false),
            ],
            quserRows: [QuserRow("alex", 3, "Active")]);

        Assert.Empty(run.LogoffCalls);
        Assert.Contains("owner-unverified", run.Actions);
    }

    [Fact]
    public void SessionOwnershipIsReadAfterTheSessionList_NotBeforeIt()
    {
        // Review finding 2. Ownership used to be collected before quser ran, so a session that
        // ended in between and had its id reused - which Windows documents doing - could join a
        // new occupant's row to the previous occupant's cached SID.
        var run = Run(
            targetSids: [AlexInDomainA],
            processes: [new SessionProcess(2, AlexInDomainA)],
            quserRows: [QuserRow("alex", 2, "Active")]);

        var quser = run.CallOrder.IndexOf("quser");
        var cim = run.CallOrder.IndexOf("cim");
        Assert.True(quser >= 0 && cim >= 0, "Both quser and the CIM enumeration must have run.");
        Assert.True(cim > quser,
            "Session ownership must be read after the session list it is joined to, not before it.");
    }

    [Fact]
    public void ADryRunReportsTheRightSessionAndLogsNobodyOff()
    {
        var run = Run(
            targetSids: [AlexInDomainA],
            processes: [new SessionProcess(2, AlexInDomainB), new SessionProcess(3, AlexInDomainA)],
            quserRows: [QuserRow("alex", 2, "Active"), QuserRow("alex", 3, "Active")],
            execute: false);

        Assert.Empty(run.LogoffCalls);
        var row = Assert.Single(run.Rows, r => ScriptRun.Field(r, "Action") == "would-log-off");
        Assert.Equal("3", ScriptRun.Field(row, "SessionId"));
    }

    [Fact]
    public void WhenSessionOwnersCannotBeReadAtAll_NothingIsLoggedOff()
    {
        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        runspace.SessionStateProxy.SetVariable("stubLogoffCalls", new List<string>());
        ps.AddScript("""
            function quser.exe { $global:LASTEXITCODE = 0; @('header','row') }
            function Get-CimInstance { param($ClassName, $Filter, $ErrorAction) throw 'RPC server is unavailable' }
            function logoff.exe { param($id) $global:stubLogoffCalls.Add([string]$id); $global:LASTEXITCODE = 0 }
            """);
        ps.Invoke();
        ps.Commands.Clear();

        ps.AddScript(Script())
          .AddArgument(new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [Environment.MachineName] = [AlexInDomainA] })
          .AddArgument(true)
          .AddArgument(new Dictionary<string, string>());

        var rows = ps.Invoke().ToList();

        Assert.Empty((List<string>)runspace.SessionStateProxy.GetVariable("stubLogoffCalls"));
        var row = Assert.Single(rows);
        Assert.Equal("owner-query-failed", row.Properties["Action"]?.Value?.ToString());
        Assert.False((bool)row.Properties["Success"].Value);
    }
}
