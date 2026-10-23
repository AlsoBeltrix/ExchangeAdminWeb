using System.Reflection;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-2. Opening a different migration batch never cleared the mailbox ticks: a mailbox ticked in batch A stayed in
/// the selection while batch B was open, so B's pane drew "Actions (1)" over rows none of which was ticked - and every
/// reader of the selection that is not pruned to the open batch (the action bar, its count, the report export) read A's
/// tick in B.
/// </summary>
/// <remarks>
/// The page's own handlers over <see cref="MigrationStubExchange"/> - the batch list and each batch's mailboxes from the
/// stand-in Exchange Online - with a navigation stand-in that records where the page sends the browser, and the page's
/// compiled render tree. No Exchange.
/// </remarks>
public sealed class MigrationMailboxSelectionTests : IDisposable
{
    private const string BatchA = "Wave A";
    private const string BatchB = "Wave B";
    private const string Alice = "alice@contoso.test";
    private const string Bob = "bob@contoso.test";

    private readonly MigrationStubExchange _exchange = new();

    public MigrationMailboxSelectionTests()
    {
        _exchange.Batches[BatchA] = Batch(BatchA);
        _exchange.Batches[BatchB] = Batch(BatchB);
        _exchange.BatchUsers[BatchA] = new List<object> { Row(("Identity", Alice), ("Status", "Synced")) };
        _exchange.BatchUsers[BatchB] = new List<object> { Row(("Identity", Bob), ("Status", "Completed")) };
    }

    public void Dispose() => _exchange.Dispose();

    private async Task<RenderableMigration> PageWithTheBatchListLoaded()
    {
        var page = _exchange.Page(_exchange.Service());
        Set(page, "Navigation", new RecordingNavigation());
        Field("canManage").SetValue(page, true);
        await Call(page, "LoadBatchList");
        return page;
    }

    private static void Tick(Migration page, string address) =>
        typeof(Migration).GetMethod("ToggleMailboxSelected", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [address, true]);

    private static HashSet<string> Ticked(Migration page) => (HashSet<string>)Field("selectedMailboxes").GetValue(page)!;

    private static string? OpenBatch(Migration page) => (string?)Field("expandedBatch").GetValue(page);

    // The rendered "Actions (N)" bars. The ticked batch has one; the mailbox pane draws a second only while a mailbox is
    // ticked - the "Actions (1)" the finding saw over a batch where nothing was.
    private static int ActionBars(RenderableMigration page) =>
        Regex.Matches(page.RenderText(), Regex.Escape("Actions (")).Count;

    // The finding, by a row click. Before: B open, Alice still ticked, "Actions (1)" over Bob's untouched row.
    [Fact]
    public async Task OpeningAnotherBatch_CarriesNoTickOver()
    {
        var page = await PageWithTheBatchListLoaded();
        await Call(page, "SelectOnlyBatch", BatchA);
        Tick(page, Alice);
        Assert.Equal(2, ActionBars(page));

        await Call(page, "SelectOnlyBatch", BatchB);

        Assert.Equal(BatchB, OpenBatch(page));
        Assert.Empty(Ticked(page));
        Assert.Equal(1, ActionBars(page));
    }

    // The same by the tick boxes: unticking A closes it, ticking B opens it.
    [Fact]
    public async Task OpeningAnotherBatchByItsTickBox_CarriesNoTickOver()
    {
        var page = await PageWithTheBatchListLoaded();
        await Call(page, "ToggleBatchSelected", BatchA, true);
        Tick(page, Alice);

        await Call(page, "ToggleBatchSelected", BatchA, false);
        await Call(page, "ToggleBatchSelected", BatchB, true);

        Assert.Equal(BatchB, OpenBatch(page));
        Assert.Empty(Ticked(page));
        Assert.Equal(1, ActionBars(page));
    }

    // The inbound half: browser Back or a pasted link opens another batch through the address, not through a click.
    [Fact]
    public async Task OpeningAnotherBatchFromTheAddress_CarriesNoTickOver()
    {
        var page = await PageWithTheBatchListLoaded();
        using var renderer = new InteractiveRenderer();
        renderer.Attach(page);
        page.OpenBatchParameter = BatchA;
        await renderer.Dispatcher.InvokeAsync(() => Call(page, "SyncOpenBatchFromUrl"));
        await renderer.Dispatcher.InvokeAsync(() => Tick(page, Alice));

        page.OpenBatchParameter = BatchB;
        await renderer.Dispatcher.InvokeAsync(() => Call(page, "SyncOpenBatchFromUrl"));

        Assert.Equal(BatchB, OpenBatch(page));
        Assert.Empty(Ticked(page));
        Assert.Equal(1, ActionBars(page));
    }

    // The control: the batch already open is not a different list. Clicking its row again, or refreshing its mailboxes,
    // keeps the tick.
    [Theory]
    [InlineData("its row clicked again")]
    [InlineData("its mailboxes refreshed")]
    public async Task TheSameBatch_KeepsItsTick(string how)
    {
        var page = await PageWithTheBatchListLoaded();
        await Call(page, "SelectOnlyBatch", BatchA);
        Tick(page, Alice);

        await (how == "its row clicked again" ? Call(page, "SelectOnlyBatch", BatchA) : Call(page, "RefreshBatchUsers", BatchA));

        Assert.Equal(BatchA, OpenBatch(page));
        Assert.Equal([Alice], Ticked(page));
        Assert.Equal(2, ActionBars(page));
    }

    private sealed class RecordingNavigation : NavigationManager
    {
        public RecordingNavigation() => Initialize("https://example.test/", "https://example.test/migration");

        protected override void NavigateToCore(string uri, bool forceLoad) { }
    }

#pragma warning disable BL0006 // a bare renderer, only so the page reports itself interactive
    private sealed class InteractiveRenderer() : Renderer(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        protected override RendererInfo RendererInfo { get; } = new("Server", isInteractive: true);
        public int Attach(IComponent component) => AssignRootComponentId(component);
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
    }
#pragma warning restore BL0006
}
