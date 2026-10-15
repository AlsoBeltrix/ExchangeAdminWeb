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
/// InvokeOptional also consumes non-terminating errors, preserves thrown diagnostics and
/// classifies connection failures before clearing the shared command/error state (psl-1).
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

        public static Collection<PSObject> CallInvokeOptional(PowerShell ps, ConnectionErrorTracker? tracker = null)
            => InvokeOptional(ps, tracker ?? new ConnectionErrorTracker());
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

    [Fact]
    public void InvokeOptional_Success_ReturnsOutputAndClearsCommands()
    {
        using var ps = NewPs();
        ps.AddScript("1; 2");

        var result = Harness.CallInvokeOptional(ps);

        Assert.Equal(new object[] { 1, 2 }, result.Select(item => item.BaseObject));
        Assert.Empty(ps.Commands.Commands);
        Assert.Empty(ps.Streams.Error);
    }

    [Fact]
    public void InvokeOptional_NonTerminatingError_IsConsumedWithOutput()
    {
        using var ps = NewPs();
        ps.AddScript("Write-Error 'optional missing permission' -ErrorAction Continue; 'value'");

        var result = Harness.CallInvokeOptional(ps);

        Assert.Equal("value", Assert.Single(result).BaseObject);
        Assert.Empty(ps.Streams.Error);
        Assert.Empty(ps.Commands.Commands);
    }

    [Fact]
    public void InvokeOptional_TerminatingError_ClearsCommands()
    {
        using var ps = NewPs();
        ps.AddScript("throw 'optional fatal'");

        Assert.ThrowsAny<RuntimeException>(() => Harness.CallInvokeOptional(ps));

        Assert.Empty(ps.Commands.Commands);
    }

    [Fact]
    public void InvokeOptional_TerminatingError_ClearsErrorStream()
    {
        using var ps = NewPs();
        ps.AddScript("Write-Error 'optional context' -ErrorAction Continue; throw 'optional fatal'");

        Assert.ThrowsAny<RuntimeException>(() => Harness.CallInvokeOptional(ps));

        Assert.Empty(ps.Streams.Error);
    }

    [Fact]
    public void InvokeOptional_AfterCaughtFailure_NextCommandRunsInIsolation()
    {
        using var ps = NewPs();
        ps.AddScript("throw 'optional fatal'");
        Assert.ThrowsAny<RuntimeException>(() => Harness.CallInvokeOptional(ps));

        ps.AddScript("'next result'");
        Collection<PSObject>? result = null;
        var nextFailure = Record.Exception(() => { result = Harness.CallInvoke(ps); });

        Assert.Null(nextFailure);
        Assert.Equal("next result", Assert.Single(result!).BaseObject);
        Assert.Empty(ps.Streams.Error);
        Assert.Empty(ps.Commands.Commands);
    }

    [Fact]
    public void InvokeOptional_TerminatingError_PreservesOriginalExceptionAndDiagnostics()
    {
        using var ps = NewPs();
        Exception? original = null;
        ps.InvocationStateChanged += (_, args) =>
        {
            if (args.InvocationStateInfo.State == PSInvocationState.Failed)
                original = args.InvocationStateInfo.Reason;
        };
        ps.AddScript("Write-Error 'first context' -ErrorAction Continue; Write-Error 'second context' -ErrorAction Continue; throw 'optional fatal'");

        var failure = Assert.ThrowsAny<RuntimeException>(() => Harness.CallInvokeOptional(ps));

        Assert.NotNull(original);
        Assert.Same(original, failure);
        Assert.Contains("optional fatal", failure.Message, StringComparison.Ordinal);
        Assert.Equal(new[] { "first context", "second context" },
            Assert.IsType<string[]>(failure.Data[ExchangeServiceBase.PsErrorsDataKey]));
        // RunAsync consumes this same resolver after the helper has emptied the live stream.
        Assert.Equal(("first context", "second context"), ExchangeServiceBase.ResolvePsErrors(failure));
        Assert.Empty(ps.Streams.Error);
    }

    [Theory]
    [InlineData(false, "ordinary mailbox failure", false, false)]
    [InlineData(false, "The remote session was unexpectedly closed", true, false)]
    [InlineData(false, "GetCurrentConnectionContext requires authentication", true, true)]
    [InlineData(true, "ordinary mailbox failure", false, false)]
    [InlineData(true, "The remote session was unexpectedly closed", true, false)]
    [InlineData(true, "GetCurrentConnectionContext requires authentication", true, true)]
    public void InvokeOptional_ClassifiesStreamBeforeCleanup(
        bool terminating, string streamError, bool connectionFailure, bool retriablePrecheck)
    {
        using var ps = NewPs();
        var tracker = new ExchangeServiceBase.ConnectionErrorTracker();
        // The thrown message deliberately carries neither classifier signature.
        ps.AddScript($"Write-Error '{streamError}' -ErrorAction Continue; " +
            (terminating ? "throw 'optional fatal'" : "'value'"));

        if (terminating)
            Assert.ThrowsAny<RuntimeException>(() => Harness.CallInvokeOptional(ps, tracker));
        else
            Harness.CallInvokeOptional(ps, tracker);

        Assert.Equal(connectionFailure, tracker.HasConnectionError);
        Assert.Equal(retriablePrecheck, tracker.HasRetriablePrecheckError);
        Assert.Empty(ps.Streams.Error);
        Assert.Empty(ps.Commands.Commands);
    }

    [Theory]
    [InlineData("The remote session was unexpectedly closed", false)]
    [InlineData("GetCurrentConnectionContext requires authentication", true)]
    public void InvokeOptional_ClassifiesThrownFailureWithoutStreamErrors(string message, bool retriablePrecheck)
    {
        using var ps = NewPs();
        var tracker = new ExchangeServiceBase.ConnectionErrorTracker();
        ps.AddScript($"throw '{message}'");

        Assert.ThrowsAny<RuntimeException>(() => Harness.CallInvokeOptional(ps, tracker));

        Assert.True(tracker.HasConnectionError);
        Assert.Equal(retriablePrecheck, tracker.HasRetriablePrecheckError);
        Assert.Empty(ps.Streams.Error);
        Assert.Empty(ps.Commands.Commands);
    }
}
