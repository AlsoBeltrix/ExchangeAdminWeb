using System.Reflection;
using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using Page = ExchangeAdminWeb.Components.Pages.MessageTrace;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-73 (1), Message Trace. A search wider than ten days runs the cloud read one window at a time, and the loop
/// never looked at the activity's token, so the operator's cancel came out only once every window had run - and not at all
/// when the on-premises half was already past its credential fetch. The loop now looks at the token between windows: a
/// cancel ends the search before the next window, filed Cancelled as kd-59 files one (kd-57's words).
/// </summary>
/// <remarks>
/// The real <see cref="MessageTraceService"/> on a pool whose stand-in Exchange Online answers Get-MessageTraceV2 with no
/// messages, logs every call - one per window - and with TraceWindowStalls holds a call until the test opens it. No
/// on-premises server is configured, so that half returns at once with its "skipped" warning and the cloud windows are the
/// only work a cancel can stop. The page's RunTrace runs by reflection with a progress channel whose activity carries a
/// token the test cancels, as the navigation guard's OK does. Every wait is bounded. No Exchange, no vault.
/// </remarks>
public sealed class MessageTraceWindowCancelTests : IDisposable
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private readonly MigrationStubExchange _exchange = new();
    private readonly ManualResetEventSlim _stall = new(false);

    public void Dispose()
    {
        // Opened first, so a held window ends before its pool is disposed. The event is not disposed: a pipeline thread may
        // still be returning from its Wait, and a slim event holds no handle to free.
        _stall.Set();
        _exchange.Dispose();
    }

    // Thirty days or a little more: three or four windows of at most ten, so there is a window after the first.
    private static DateTime Start => DateTime.Today.AddDays(-30);

    private MessageTraceService Service() =>
        new(_exchange.Pool(), null!, NullLogger<MessageTraceService>.Instance, new ConfigurationBuilder().Build(), null!,
            _exchange.Trace);

    private string[] WindowsRun() =>
        _exchange.CallList().Where(call => call.StartsWith("Get-MessageTraceV2", StringComparison.Ordinal)).ToArray();

    private IReadOnlyList<JsonElement> AuditRows() =>
        _exchange.AuditEvents()
            .Where(row => row.TryGetProperty("eventType", out var type) && type.GetString() == "audit")
            .ToList();

    private Page PageOver(RecordingProgress progress)
    {
        var page = new Page();
        Set(page, "MsgTrace", Service());
        Set(page, "Audit", _exchange.Audit);
        Set(page, "Progress", progress);
        Set(page, "currentUser", "CONTOSO\\operator");
        Set(page, "clientIpAddress", "10.0.0.5");
        Set(page, "ticketNumber", "INC0001");
        Set(page, "sender", "sender@contoso.test");
        Set(page, "startDate", Start);
        return page;
    }

    private static Task RunTrace(Page page) => (Task)typeof(Page).GetMethod("RunTrace", Members)!.Invoke(page, null)!;

    // The control, and the premise of the test below: a wide search nobody cancels asks the cloud once per window, every
    // window, and comes back whole.
    [Fact]
    public async Task AWideSearchNobodyCancels_RunsEveryWindow()
    {
        var end = DateTime.Today;
        using var cancel = new CancellationTokenSource();

        var response = await Service().GetMessageTraceAsync("sender@contoso.test", null, Start, end, null, null, cancel.Token)
            .WaitAsync(Bound, TestContext.Current.CancellationToken);

        var windows = MessageTraceWindowPlanner.Split(Start, end).Count;
        Assert.True(windows > 1, $"a thirty-day search is {windows} window(s)");
        Assert.Equal(windows, WindowsRun().Length);
        Assert.Empty(response.FailedBackends);
    }

    // The finding: cancelled while the first window is out, the search starts no further window and ends as the operator's
    // cancel - shown, the activity ended not succeeded with the phrase, the one row filed Cancelled. Before, the loop ran
    // every other window and the search was filed as one that ran to its end.
    [Fact]
    public async Task ACancelAfterTheFirstWindow_StartsNoFurtherWindow_AndIsFiledCancelled()
    {
        _exchange.Behaviour["TraceWindowStalls"] = _stall;
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var page = PageOver(progress);

        var run = RunTrace(page);
        await WaitUntil(() => WindowsRun().Length > 0);
        await cancel.CancelAsync();
        _stall.Set();
        await run.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Single(WindowsRun());
        var shown = Assert.IsType<MessageTraceResponse>(Get(page, "response"));
        Assert.Equal("Cancelled by the operator", shown.Error);
        Assert.Equal((false, "Cancelled by the operator"), Assert.Single(progress.Handles).Outcome);
        Assert.False((bool)Get(page, "isLoading")!);
        var row = Assert.Single(AuditRows());
        Assert.Equal("MessageTrace", row.GetProperty("action").GetString());
        Assert.Equal("Cancelled", row.GetProperty("result").GetString());
        Assert.Equal("Cancelled by the operator", row.GetProperty("error").GetString());
        Assert.Equal("INC0001", row.GetProperty("ticket").GetString());
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting for the condition");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
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
