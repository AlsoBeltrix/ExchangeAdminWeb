using System.Reflection;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Microsoft.JSInterop.Infrastructure;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using Page = ExchangeAdminWeb.Components.Pages.MessageTrace;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-70. Message Trace's live detail download did not stop for the operator's cancel: the detail read is fail-soft
/// - it answers a cancel as a per-message error and never throws - so the loop ran on through every message, pushed the CSV
/// to the browser and filed a successful download. And its cancel catch took every cancellation as the operator's, so a
/// browser call that timed out was filed as their cancel. The loop now checks the activity's token after each message: the
/// operator's cancel stops it before the next one, nothing is pushed, and the row is filed Cancelled in the app's phrase for
/// one (kd-57). Only that token counts: any other cancellation is a download that failed, in its own words.
/// </summary>
/// <remarks>
/// The page's handler by reflection, as kd-57's <c>MessageTraceDetailDownloadCancelTests</c> runs it, over the real service.
/// Three messages: the first answered locally (a backend the service does not know), the second on-premises - its credential
/// fetch waits at the stand-in vault until the operator cancels (<see cref="CancellableVaultCredentials"/>) - and the third
/// in Exchange Online, whose read would open a pool connection the test counts. No vault, no Exchange.
/// </remarks>
public sealed class MessageTraceLiveDownloadCancelTests : IDisposable
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly TimeSpan Bound = CancellableVaultCredentials.Bound;

    private readonly CancellableVaultCredentials _credentials = new("MessageTrace");
    private readonly MigrationStubExchange _exchange = new();
    private readonly IJSRuntime _js = Substitute.For<IJSRuntime>();
    private int _cloudConnects;

    public void Dispose()
    {
        _exchange.Dispose();
        _credentials.Dispose();
    }

    private static MessageTraceResult Message(string id, string backend) => new()
    {
        Received = DateTime.UtcNow,
        SenderAddress = "sender@contoso.test",
        RecipientAddress = "rcpt@contoso.test",
        Subject = "Quarterly figures",
        Status = "Delivered",
        MessageId = id,
        MessageTraceId = Guid.NewGuid().ToString(),
        Backend = backend,
    };

    private Page PageOver(RecordingProgress progress, params MessageTraceResult[] messages)
    {
        var page = new Page();
        Set(page, "MsgTrace", new MessageTraceService(
            _exchange.Pool(connects: false, beforeConnect: () => Interlocked.Increment(ref _cloudConnects)), null!,
            NullLogger<MessageTraceService>.Instance, _credentials.Configuration, _credentials.Service, _credentials.Trace));
        Set(page, "Audit", _credentials.Audit);
        Set(page, "Progress", progress);
        Set(page, "JS", _js);
        Set(page, "response", new MessageTraceResponse { Results = [.. messages] });
        var selected = (HashSet<int>)Get(page, "selectedIndices")!;
        for (var i = 0; i < messages.Length; i++)
            selected.Add(i);
        Set(page, "currentUser", "CONTOSO\\operator");
        Set(page, "clientIpAddress", "10.0.0.5");
        Set(page, "ticketNumber", "INC0001");
        return page;
    }

    private static Task Download(Page page) =>
        (Task)typeof(Page).GetMethod("DownloadSelectedDetails", Members)!.Invoke(page, null)!;

    private int DownloadsPushed() => _js.ReceivedCalls().Count(call =>
        call.GetMethodInfo().Name == nameof(IJSRuntime.InvokeAsync) && call.GetArguments().FirstOrDefault() as string == "downloadFile");

    // The finding: the operator cancels while the second message's read waits. Before, the loop ran on - the third message
    // read, the CSV pushed to the browser, the row filed as a successful download.
    [Fact]
    public async Task TheOperatorsCancel_StopsTheDownloadBeforeTheNextMessage_PushesNothing_AndIsFiledCancelled()
    {
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var page = PageOver(progress,
            Message("<m1@contoso.test>", "NotABackend"), Message("<m2@contoso.test>", "OnPrem"), Message("<m3@contoso.test>", "ExchangeOnline"));

        var run = Download(page);
        await _credentials.SecretRequested.WaitAsync(Bound, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await run.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal(0, Volatile.Read(ref _cloudConnects)); // the third message was never read
        Assert.Equal(0, DownloadsPushed());
        Assert.Null(Get(page, "detailActionError"));
        Assert.False((bool)Get(page, "detailActionBusy")!);
        Assert.Equal((false, "Cancelled by the operator"), Assert.Single(progress.Handles).Outcome);
        var row = Assert.Single(_credentials.AuditRows());
        Assert.Equal("MessageTrace_DetailDownload", row.GetProperty("action").GetString());
        Assert.Equal("Cancelled", row.GetProperty("result").GetString());
        Assert.Equal("Cancelled by the operator", row.GetProperty("error").GetString());
        Assert.Equal("INC0001", row.GetProperty("ticket").GetString());
    }

    // The control, kd-57's rule: a cancellation the operator did not ask for - the browser call timing out, the activity's
    // token live - is a download that failed, shown and filed in its own words. It was filed as the operator's cancel.
    [Fact]
    public async Task ABrowserCallThatTimesOut_IsADownloadThatFailed_NotTheOperatorsCancel()
    {
        _js.InvokeAsync<IJSVoidResult>("downloadFile", Arg.Any<object?[]?>())
            .Returns(_ => ValueTask.FromException<IJSVoidResult>(new TaskCanceledException("the browser call timed out")));
        var progress = new RecordingProgress();
        var page = PageOver(progress, Message("<m1@contoso.test>", "NotABackend"), Message("<m2@contoso.test>", "NotABackend"));

        await Download(page).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal(1, DownloadsPushed());
        Assert.Equal("the browser call timed out", Get(page, "detailActionError"));
        var row = Assert.Single(_credentials.AuditRows());
        Assert.Equal("MessageTrace_DetailDownload", row.GetProperty("action").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal("the browser call timed out", row.GetProperty("error").GetString());
    }

    // And a download nobody cancels still runs through every message and pushes its CSV, a per-message failure carried
    // into the export as before.
    [Fact]
    public async Task ADownloadNobodyCancels_StillReadsEveryMessage_AndPushesTheCsv()
    {
        _credentials.TimesOut = true; // the on-premises read fails on its own: a per-message error, not a cancel
        var progress = new RecordingProgress();
        var page = PageOver(progress,
            Message("<m1@contoso.test>", "NotABackend"), Message("<m2@contoso.test>", "OnPrem"), Message("<m3@contoso.test>", "ExchangeOnline"));

        await Download(page).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref _cloudConnects)); // the third message was read
        Assert.Equal(1, DownloadsPushed());
        Assert.Null(Get(page, "detailActionError"));
        Assert.Equal((true, "3 message(s) exported"), Assert.Single(progress.Handles).Outcome);
        var row = Assert.Single(_credentials.AuditRows());
        Assert.Equal("Success", row.GetProperty("result").GetString());
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
