using System.Reflection;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using Page = ExchangeAdminWeb.Components.Pages.MessageTrace;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-59, Message Trace. The module fetched its on-premises credential with no cancellation token, so an operator's
/// cancel left the vault request running and the search or the delivery detail carried on behind it. Both now pass the
/// page's activity token to the fetch: the search's, which the service had no parameter for, and the detail's, which it
/// already took and did not pass on. A cancel stopped that way is filed Cancelled, in the app's phrase for one (kd-57); a
/// vault request that times out with nobody cancelling is still a read that failed (kd-14, kd-37).
/// </summary>
/// <remarks>
/// The real <see cref="MessageTraceService"/> over a pool that cannot connect - so the cloud half fails at once and the
/// on-premises half is the one under test - with its credentials through a real <see cref="ModuleCredentialService"/> and a
/// real <see cref="DelineaService"/> whose secret request waits until the caller cancels
/// (<see cref="CancellableVaultCredentials"/>). The page's handlers run by reflection with a progress channel whose
/// activities carry a token the test cancels, as the navigation guard's OK does. Every wait is bounded, so a path that
/// passes no token fails on the bound rather than hanging. No vault, no Exchange.
/// </remarks>
public sealed class MessageTraceCancellationTests : IDisposable
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly TimeSpan Bound = CancellableVaultCredentials.Bound;

    private readonly CancellableVaultCredentials _credentials = new("MessageTrace");
    private readonly MigrationStubExchange _exchange = new();

    public void Dispose()
    {
        _exchange.Dispose();
        _credentials.Dispose();
    }

    private MessageTraceService Service() =>
        new(_exchange.Pool(connects: false), null!, NullLogger<MessageTraceService>.Instance,
            _credentials.Configuration, _credentials.Service, _credentials.Trace);

    private static MessageTraceResult OnPremMessage() => new()
    {
        Received = DateTime.UtcNow,
        SenderAddress = "sender@contoso.test",
        RecipientAddress = "rcpt@contoso.test",
        Subject = "Quarterly figures",
        Status = "Delivered",
        MessageId = "<m1@contoso.test>",
        Backend = "OnPrem",
    };

    private Page PageOver(RecordingProgress progress)
    {
        var page = new Page();
        Set(page, "MsgTrace", Service());
        Set(page, "Audit", _credentials.Audit);
        Set(page, "Progress", progress);
        Set(page, "currentUser", "CONTOSO\\operator");
        Set(page, "clientIpAddress", "10.0.0.5");
        Set(page, "ticketNumber", "INC0001");
        Set(page, "sender", "sender@contoso.test");
        return page;
    }

    private static Task RunTrace(Page page) => (Task)typeof(Page).GetMethod("RunTrace", Members)!.Invoke(page, null)!;

    private static Task ToggleDetail(Page page, MessageTraceResult message) =>
        (Task)typeof(Page).GetMethod("ToggleDetail", Members)!.Invoke(page, [0, message])!;

    // The finding, the search, at the service: before, the cancel reached nothing, the vault request ran to its own end and
    // the search came back with the on-premises half reported missing, as if nobody had asked it to stop.
    [Fact]
    public async Task ACancelDuringTheOnPremCredentialRead_EndsTheSearchAsACancel()
    {
        using var cancel = new CancellationTokenSource();
        var trace = Service().GetMessageTraceAsync("sender@contoso.test", null, DateTime.UtcNow.AddDays(-1), DateTime.UtcNow,
            null, null, cancel.Token);
        await _credentials.SecretRequested.WaitAsync(Bound, TestContext.Current.CancellationToken);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => trace.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.True(_credentials.RequestCancelled);
    }

    // The same for one message's delivery trail. The method already took a token - it stopped short of the vault. The read
    // stays fail-soft (the detail job relies on that) and says the cancel stopped it.
    [Fact]
    public async Task ACancelDuringTheDetailsCredentialRead_EndsTheDetailRead_AndSaysTheCancelStoppedIt()
    {
        using var cancel = new CancellationTokenSource();
        var detail = Service().GetMessageDetailAsync(OnPremMessage(), cancel.Token);
        await _credentials.SecretRequested.WaitAsync(Bound, TestContext.Current.CancellationToken);

        await cancel.CancelAsync();
        var fetched = await detail.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.True(fetched.Cancelled);
        Assert.Empty(fetched.Events);
        Assert.NotNull(fetched.Error);
        Assert.True(_credentials.RequestCancelled);
    }

    // The finding at the page: the search's activity token is the one the navigation guard cancels, and the page passes it.
    // Filed Cancelled in the app's phrase for a cancel, not Failed in the framework's words.
    [Fact]
    public async Task ACancelOfTheSearchsActivity_DuringTheOnPremCredentialRead_IsFiledCancelled()
    {
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var page = PageOver(progress);

        var run = RunTrace(page);
        await _credentials.SecretRequested.WaitAsync(Bound, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await run.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.True(_credentials.RequestCancelled);
        var shown = Assert.IsType<MessageTraceResponse>(Get(page, "response"));
        Assert.Equal("Cancelled by the operator", shown.Error);
        Assert.Equal((false, "Cancelled by the operator"), Assert.Single(progress.Handles).Outcome);
        Assert.False((bool)Get(page, "isLoading")!);
        var row = Assert.Single(_credentials.AuditRows());
        Assert.Equal("MessageTrace", row.GetProperty("action").GetString());
        Assert.Equal("Cancelled", row.GetProperty("result").GetString());
        Assert.Equal("Cancelled by the operator", row.GetProperty("error").GetString());
        Assert.Equal("INC0001", row.GetProperty("ticket").GetString());
    }

    // The same for the delivery detail a row opens.
    [Fact]
    public async Task ACancelOfTheDetailsActivity_DuringTheOnPremCredentialRead_IsFiledCancelled()
    {
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var page = PageOver(progress);

        var run = ToggleDetail(page, OnPremMessage());
        await _credentials.SecretRequested.WaitAsync(Bound, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await run.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.True(_credentials.RequestCancelled);
        var shown = Assert.IsType<MessageTraceDetail>(Get(page, "detail"));
        Assert.Equal("Cancelled by the operator", shown.Error);
        Assert.Equal((false, "Cancelled by the operator"), Assert.Single(progress.Handles).Outcome);
        Assert.False((bool)Get(page, "detailLoading")!);
        var row = Assert.Single(_credentials.AuditRows());
        Assert.Equal("MessageTrace_Detail", row.GetProperty("action").GetString());
        Assert.Equal("Cancelled", row.GetProperty("result").GetString());
        Assert.Equal("Cancelled by the operator", row.GetProperty("error").GetString());
    }

    // The control, kd-14's and kd-37's rule: a vault request that gives up by itself, with nobody cancelling, is a read that
    // failed - the on-premises half reported missing and the search filed Failed, as before. Not the operator's cancel.
    [Fact]
    public async Task AVaultRequestThatTimesOutUnasked_LeavesTheSearchAReadThatFailed()
    {
        _credentials.TimesOut = true;
        var progress = new RecordingProgress();
        var page = PageOver(progress);

        await RunTrace(page).WaitAsync(Bound, TestContext.Current.CancellationToken);

        var shown = Assert.IsType<MessageTraceResponse>(Get(page, "response"));
        Assert.Contains("On-prem", shown.FailedBackends);
        Assert.Contains(shown.Warnings, warning => warning.StartsWith(
            "On-prem results are MISSING from this list: On-prem message tracking failed:", StringComparison.Ordinal));
        var row = Assert.Single(_credentials.AuditRows());
        Assert.Equal("MessageTrace", row.GetProperty("action").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.DoesNotContain("Cancelled", row.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    // The same control for the detail: its own words, filed Failed.
    [Fact]
    public async Task AVaultRequestThatTimesOutUnasked_LeavesTheDetailAReadThatFailed()
    {
        _credentials.TimesOut = true;
        var progress = new RecordingProgress();
        var page = PageOver(progress);

        await ToggleDetail(page, OnPremMessage()).WaitAsync(Bound, TestContext.Current.CancellationToken);

        var shown = Assert.IsType<MessageTraceDetail>(Get(page, "detail"));
        Assert.Equal("On-prem delivery detail unavailable: credentials could not be retrieved from Delinea.", shown.Error);
        Assert.False(shown.Cancelled);
        var row = Assert.Single(_credentials.AuditRows());
        Assert.Equal("MessageTrace_Detail", row.GetProperty("action").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal(shown.Error, row.GetProperty("error").GetString());
    }

    // A cancellation the search's own token did not cause - here the cloud sign-in's, with the token live - is that
    // backend's failure, reported missing, never let out as the operator's cancel.
    [Fact]
    public async Task ACancellationTheSearchsTokenDidNotCause_IsABackendThatFailed()
    {
        _exchange.ConnectFailure = new OperationCanceledException("the sign-in was cancelled");
        _credentials.TimesOut = true;
        using var cancel = new CancellationTokenSource();

        var response = await Service().GetMessageTraceAsync("sender@contoso.test", null, DateTime.UtcNow.AddDays(-1),
            DateTime.UtcNow, null, null, cancel.Token).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Contains("Exchange Online", response.FailedBackends);
        Assert.Contains(response.Warnings, warning => warning.StartsWith(
            "Exchange Online results are MISSING from this list: Exchange Online trace failed: the sign-in was cancelled",
            StringComparison.Ordinal));
    }

    // The seam itself: a cancellation marks the detail only when the caller's own token was cancelled.
    [Fact]
    public async Task TheDetailSaysCancelled_OnlyForACancellationOfItsCallersOwnToken()
    {
        var summary = OnPremMessage();
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var ownCancel = await MessageTraceService.RunDetailBackendAsync(summary,
            () => throw new OperationCanceledException(cancel.Token), cancellationToken: cancel.Token);
        var otherCancel = await MessageTraceService.RunDetailBackendAsync(summary,
            () => throw new OperationCanceledException("not the caller's"), cancellationToken: CancellationToken.None);
        var otherFailure = await MessageTraceService.RunDetailBackendAsync(summary,
            () => throw new InvalidOperationException("busy"), cancellationToken: cancel.Token);

        Assert.True(ownCancel.Cancelled);
        Assert.False(otherCancel.Cancelled);
        Assert.False(otherFailure.Cancelled);
        Assert.All(new[] { ownCancel, otherCancel, otherFailure }, detail => Assert.NotNull(detail.Error));
    }

    private static void Set(object page, string name, object value)
    {
        var property = typeof(Page).GetProperty(name, Members);
        if (property is not null)
        {
            property.SetValue(page, value);
            return;
        }

        typeof(Page).GetField(name, Members)!.SetValue(page, value);
    }

    private static object? Get(object page, string name) => typeof(Page).GetField(name, Members)!.GetValue(page);
}
