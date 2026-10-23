using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a68, part 5. <c>ExecuteBulkBatchAction</c> named every run's activity "Removing completed batches" and
/// set the busy reason "clearing", which the batch list's busy text read as "Removing completed batches and reloading...",
/// though the executor runs six actions: Delete, Complete, Schedule, Stop, Remove Completed and Resume.
/// </summary>
/// <remarks>
/// The page's own handlers over <see cref="MigrationStubExchange"/>, with a progress channel that records each activity's
/// label and how it ended, and the page's compiled render tree read while a run is under way. No Exchange.
/// </remarks>
public sealed class MigrationBulkBatchActivityTests : IDisposable
{
    private const string Batch = "Wave 12";
    private const string Ticket = "INC0042";

    private readonly MigrationStubExchange _exchange = new();
    private readonly RecordingProgress _progress = new();

    public void Dispose() => _exchange.Dispose();

    private RenderableMigration Page()
    {
        var page = _exchange.Page(_exchange.Service());
        Set(page, "Progress", _progress);
        Field("migrationBatches").SetValue(page, new List<MigrationBatchInfo>());
        return page;
    }

    // The finding: each of the six, through its own caller, names the action it runs - as a single action's activity does
    // ("<action> on <target>") - where all six read "Removing completed batches".
    [Theory]
    [InlineData("DeleteSelectedBatches", "RemoveMigrationBatch")]
    [InlineData("CompleteSelectedBatches", "CompleteMigrationBatch")]
    [InlineData("ScheduleSelectedBatches", "ScheduleMigrationBatchCompletion")]
    [InlineData("StopSelectedBatches", "StopMigrationBatch")]
    [InlineData("RemoveCompletedSelectedBatches", "RemoveMigrationBatch")]
    [InlineData("ResumeSelectedBatches", "StartMigrationBatch")]
    public async Task EachBulkBatchAction_NamesItsActionInItsActivity(string caller, string action)
    {
        var page = Page();
        Field("confirmedScheduleUtc").SetValue(page, DateTime.UtcNow.AddDays(1));

        await Call(page, caller, new MigrationBatchActionPlan([Batch], []), Ticket);

        var run = _progress.Handles[0];
        Assert.Equal($"{action} on 1 batch(es)", run.Label);
        Assert.NotNull(run.Outcome);
        Assert.DoesNotContain(_progress.Handles, handle => handle.Label == "Removing completed batches");
    }

    // The busy text over the batch list while a run is under way names that run, and is gone when it ends. Read from inside
    // the run's own call, where the executor is busy.
    [Fact]
    public async Task TheBusyText_NamesTheRunUnderWay()
    {
        var page = Page();
        Field("tabIndex").SetValue(page, 2);
        string? busy = null;
        Func<string, Task<PermissionResult>> stop = _ =>
        {
            busy = page.RenderText();
            return Task.FromResult(PermissionResult.Ok());
        };

        await Call(page, "ExecuteBulkBatchAction", (IReadOnlyList<string>)[Batch], (IReadOnlyList<MigrationBatchSkip>)[], stop,
            "StopMigrationBatch", "Migration_StopSelected", "Queued stop of", Ticket);

        Assert.NotNull(busy);
        Assert.Contains("StopMigrationBatch on 1 batch(es) and reloading...", busy, StringComparison.Ordinal);
        Assert.DoesNotContain("Removing completed batches", busy, StringComparison.Ordinal);
        Assert.DoesNotContain("and reloading...", page.RenderText(), StringComparison.Ordinal);
    }
}
