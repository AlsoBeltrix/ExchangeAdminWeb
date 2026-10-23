using ExchangeAdminWeb.Models;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a23n. (1) The approval read its own batch back after <c>Complete-MigrationBatch</c> and reported the
/// approval FAILED unless the batch still showed a completion time, so a cmdlet that clears or stops returning that field
/// once it has accepted the completion made an irreversible write read as a failure, inviting a retry (Known Failure
/// Class 1). (2) The read before the writes asked only whether a batch named for the mailbox existed, not what state it was
/// in, so a batch the approval cannot complete passed it, and the approval then failed at a later step after the earlier
/// steps had already written.
/// </summary>
/// <remarks>
/// The real service over <see cref="MigrationStubExchange"/>, whose stand-in cmdlets record every step they are asked to
/// run, and the page's own handler for the audit row. No Exchange.
/// </remarks>
public sealed class MigrationApproveConfirmationTests : IDisposable
{
    private const string Address = "alice@contoso.test";

    private static readonly string[] EveryStep =
    [
        $"Get-MigrationBatch {Address}",
        $"Set-MigrationUser -ApproveSkippedItems {Address}",
        $"Set-MigrationUser -CompleteAfter {Address}",
        $"Set-MigrationBatch -ApproveSkippedItems {Address}",
        $"Set-MigrationBatch -CompleteAfter {Address}",
        $"Set-MoveRequest -SkippedItemApprovalTime {Address}",
        $"Resume-MoveRequest {Address}",
        $"Complete-MigrationBatch {Address}",
        $"Get-MigrationBatch {Address}",
    ];

    private const string Accepted = $"Exchange accepted Complete-MigrationBatch for its migration batch {Address}";

    private readonly MigrationStubExchange _exchange = new();

    public void Dispose() => _exchange.Dispose();

    // ---- part 1: an accepted completion is the completion ------------------------------------------

    // The finding. Exchange accepts Complete-MigrationBatch and the batch then reads back with no completion time. Before,
    // that was "Completion of the migration batch ... was not confirmed" - a failed approval after every write had landed.
    [Fact]
    public async Task ACompletionWhoseReadBackOmitsTheSchedule_IsASuccess_SayingTheScheduleWasNotConfirmed()
    {
        _exchange.Batches[Address] = Batch(Address);
        _exchange.Behaviour["CompleteClearsSchedule"] = true;

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Approved skipped items and initiated completion for {Address}.", result.Message);
        Assert.Equal(
            $"{Accepted}; read back, the batch reports status Synced and no completion time, so the read-back did not confirm the schedule.",
            result.Detail);
        Assert.Equal(EveryStep, _exchange.CallList());
    }

    // What the read-back shows is reported as read: a status the completion moved the batch to is named, not interpreted.
    [Fact]
    public async Task AReadBackThatShowsANewStatus_NamesItInTheDetail()
    {
        _exchange.Batches[Address] = Batch(Address);
        _exchange.Behaviour["CompletionStatus"] = "Completing";

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.True(result.Success, result.Message);
        Assert.Equal($"{Accepted}; read back, the batch reports status Completing with a completion time set.", result.Detail);
    }

    // A read-back with no status and no completion time is still the accepted completion, and says both.
    [Fact]
    public async Task AReadBackWithNoStatusAndNoSchedule_IsStillASuccess_SayingSo()
    {
        _exchange.Batches[Address] = Batch(Address);
        _exchange.Behaviour["CompleteClearsSchedule"] = true;
        _exchange.Behaviour["CompletionStatus"] = " ";

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            $"{Accepted}; read back, the batch reports no status and no completion time, so the read-back did not confirm the schedule.",
            result.Detail);
    }

    // ---- part 2: the batch's state is read before any write ----------------------------------------

    // The finding. A batch named for the mailbox, in a state the approval cannot complete it from: refused, naming the
    // state, with the read the only call. Before, the writes ran and the approval failed at a later step.
    [Theory]
    [InlineData("Failed")]
    [InlineData("Removing")]
    [InlineData("Stopped")]
    [InlineData("Stopping")]
    [InlineData("Completing")]
    [InlineData("CompletedWithErrors")]
    [InlineData(" stopped ")]
    public async Task ABatchInAStateAnApprovalCannotCompleteFrom_IsRefusedBeforeAnyWrite(string status)
    {
        _exchange.Batches[Address] = Batch(Address, status: status);

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.False(result.Success);
        Assert.Equal(
            $"{Address} was not approved: its migration batch {Address} is {status.Trim()}, a state an approval does not complete a batch from. Nothing was changed.",
            result.Message);
        Assert.Equal([$"Get-MigrationBatch {Address}"], _exchange.CallList());
    }

    // Already completed is its own outcome: the migration has finished, which is not a fault, and nothing is written.
    [Fact]
    public async Task AnAlreadyCompletedBatch_IsItsOwnOutcome_AndNothingIsWritten()
    {
        _exchange.Batches[Address] = Batch(Address, status: "Completed");

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.False(result.Success);
        Assert.Equal(
            $"{Address} was not approved: its migration batch {Address} is already Completed, so its migration has finished and there is nothing left to approve. Nothing was changed.",
            result.Message);
        Assert.Equal([$"Get-MigrationBatch {Address}"], _exchange.CallList());
    }

    // A batch that reports no status cannot be told apart from one in a refused state: refused, nothing written.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABatchThatReportsNoStatus_IsRefused_AndNothingIsWritten(string? status)
    {
        _exchange.Batches[Address] = Batch(Address, status: status);

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.False(result.Success);
        Assert.Equal(
            $"{Address} was not approved: its migration batch {Address} reports no status, so whether it can be completed is not known. Nothing was changed.",
            result.Message);
        Assert.Equal([$"Get-MigrationBatch {Address}"], _exchange.CallList());
    }

    // The control: a batch in a state the approval completes from runs every step, as before.
    [Theory]
    [InlineData("Synced")]
    [InlineData("Syncing")]
    [InlineData("SyncedWithErrors")]
    public async Task ABatchInAnyOtherState_RunsEveryStep(string status)
    {
        _exchange.Batches[Address] = Batch(Address, status: status);

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.True(result.Success, result.Message);
        Assert.Equal(EveryStep, _exchange.CallList());
    }

    // Through the page's own action: the refusal is the row's failure, audited once as Failed with the refusal as its error.
    [Fact]
    public async Task ARefusalThroughThePage_IsOneFailedAuditRow_NamingTheState()
    {
        _exchange.Batches[Address] = Batch(Address, status: "Stopped");
        var service = _exchange.Service();
        var page = _exchange.Page(service);
        Func<Task<PermissionResult>> approve = () => service.ApproveMigrationUserAsync(Address);

        await Call(page, "ExecuteUserAction", approve, "ApproveMigrationUser", Address, "INC0001");

        var result = Assert.IsType<PermissionResult>(Field("batchActionResult").GetValue(page));
        Assert.False(result.Success);
        var audited = Assert.Single(_exchange.AuditEvents(), e => e.GetProperty("action").GetString() == "ApproveMigrationUser");
        Assert.Equal("Failed", audited.GetProperty("result").GetString());
        Assert.Equal(
            $"{Address} was not approved: its migration batch {Address} is Stopped, a state an approval does not complete a batch from. Nothing was changed.",
            audited.GetProperty("error").GetString());
        Assert.Equal([$"Get-MigrationBatch {Address}"], _exchange.CallList());
    }
}
