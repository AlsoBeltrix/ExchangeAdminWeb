using System.Collections.ObjectModel;
using System.Management.Automation;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Covers the shared <c>ExchangeServiceBase.Invoke</c> helper. The regression these guard
/// against (Defect B in docs/ConferenceRooms-SyncedRoomSetMailbox-Plan.md): on a failure,
/// Invoke previously threw WITHOUT clearing the PowerShell command queue or error stream, so
/// the failed command stayed queued on the shared pooled runspace and re-ran on every
/// subsequent step - turning one expected failure into a cascade.
///
/// Invoke is invoked via a test subclass because it is protected. A real in-process runspace
/// is used (no Exchange) so command-queue/stream semantics match production.
/// </summary>
public class ExchangeServiceBaseInvokeTests
{
    // Minimal concrete subclass to reach the protected static helpers. The base ctor needs
    // dependencies we don't use here; the static helpers don't touch instance state, so we
    // call them through a tiny reflection-free accessor exposed by the harness subclass.
    private sealed class Harness : ExchangeServiceBase
    {
        private Harness() : base(null!, null!, null!, "") { }

        public static Collection<PSObject> CallInvoke(PowerShell ps)
            => Invoke(ps, new ConnectionErrorTracker());

        public static Collection<PSObject> CallInvokeBestEffort(PowerShell ps, out IReadOnlyList<string> errors)
            => InvokeBestEffort(ps, new ConnectionErrorTracker(), out errors);
    }

    private static PowerShell NewPs()
    {
        var ps = PowerShell.Create();
        // Run in a fresh isolated runspace so tests don't interfere.
        return ps;
    }

    [Fact]
    public void Invoke_TerminatingError_ClearsCommandQueue()
    {
        using var ps = NewPs();
        ps.AddScript("throw 'boom'");

        Assert.Throws<InvalidOperationException>(() => Harness.CallInvoke(ps));

        // The failed command must not remain queued, or the next AddCommand would re-run it.
        Assert.Empty(ps.Commands.Commands);
    }

    [Fact]
    public void Invoke_TerminatingError_ClearsErrorStream()
    {
        using var ps = NewPs();
        ps.AddScript("Write-Error 'first'; throw 'fatal'");

        Assert.Throws<InvalidOperationException>(() => Harness.CallInvoke(ps));

        Assert.Empty(ps.Streams.Error);
    }

    [Fact]
    public void Invoke_AfterFailure_NextCommandRunsInIsolation()
    {
        using var ps = NewPs();
        ps.AddScript("throw 'boom'");
        Assert.Throws<InvalidOperationException>(() => Harness.CallInvoke(ps));

        // Because the queue was cleared, a fresh command now runs cleanly.
        ps.AddScript("'ok'");
        var result = Harness.CallInvoke(ps);

        Assert.Equal("ok", result.Single().BaseObject);
    }

    [Fact]
    public void Invoke_ThrownException_CarriesStructuredDetail()
    {
        using var ps = NewPs();
        ps.AddScript("throw 'detailed failure'");

        var ex = Assert.Throws<InvalidOperationException>(() => Harness.CallInvoke(ps));

        Assert.Contains("detailed failure", ex.Message);
        var (primary, _) = ExchangeServiceBase.ResolvePsErrors(ex);
        Assert.Contains("detailed failure", primary);
    }

    [Fact]
    public void Invoke_SuccessPath_ReturnsResultsAndClearsCommands()
    {
        using var ps = NewPs();
        ps.AddScript("1 + 1");

        var result = Harness.CallInvoke(ps);

        Assert.Equal(2, result.Single().BaseObject);
        Assert.Empty(ps.Commands.Commands);
    }

    [Fact]
    public void InvokeBestEffort_NonTerminatingError_DoesNotThrow_CapturesErrorAndClears()
    {
        using var ps = NewPs();
        ps.AddScript("Write-Error 'soft error'");

        var result = Harness.CallInvokeBestEffort(ps, out var errors);

        Assert.NotNull(result);
        Assert.Contains(errors, e => e.Contains("soft error"));
        Assert.Empty(ps.Streams.Error);
        Assert.Empty(ps.Commands.Commands);
    }

    [Fact]
    public void InvokeBestEffort_Success_NoErrors()
    {
        using var ps = NewPs();
        ps.AddScript("'value'");

        var result = Harness.CallInvokeBestEffort(ps, out var errors);

        Assert.Equal("value", result.Single().BaseObject);
        Assert.Empty(errors);
    }
}
