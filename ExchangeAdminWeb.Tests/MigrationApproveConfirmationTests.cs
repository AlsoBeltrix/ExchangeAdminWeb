using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a23n. (1) The approval read its own batch back after <c>Complete-MigrationBatch</c> and reported the
/// approval FAILED unless the batch still showed a completion time, so a cmdlet that clears or stops returning that field
/// once it has accepted the completion made an irreversible write read as a failure, inviting a retry (Known Failure
/// Class 1). (2) The read before the writes asked only whether a batch named for the mailbox existed, not what state it was
/// in, so a batch the approval cannot complete passed it, and the approval then failed at a later step after the earlier
/// steps had already written. aud-a55: a mailbox in a batch of many - no batch named for it - was approved without
/// reading any status at all, so a stale page or a second operator could run its writes from a state that no longer
/// allows them; its own status is now read first and judged by the Approve action's rule.
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

    private static string NotCompletable(string status) =>
        $"{Address} was not approved: approving it completes its migration batch {Address}, which is {status} - not a status the batch Complete action completes a batch from. Nothing was changed.";

    // The finding (aud-a23n), and aud-a23r's: a batch named for the mailbox, in a state the batch Complete action does not
    // complete a batch from, is refused naming the state, with the read the only call. aud-a23n refused a list of six, so
    // Syncing, SyncedWithErrors, Corrupted and any state nobody listed ran Set-MigrationUser, Set-MigrationBatch
    // -CompleteAfter, Set-MoveRequest and Resume-MoveRequest before Complete-MigrationBatch refused.
    [Theory]
    [InlineData("Failed")]
    [InlineData("Removing")]
    [InlineData("Stopped")]
    [InlineData("Stopping")]
    [InlineData("Completing")]
    [InlineData("CompletedWithErrors")]
    [InlineData(" stopped ")]
    [InlineData("Syncing")]
    [InlineData("SyncedWithErrors")]
    [InlineData("Corrupted")]
    [InlineData("AStatusNothingHereNames")]
    public async Task ABatchInAStateAnApprovalCannotCompleteFrom_IsRefusedBeforeAnyWrite(string status)
    {
        Assert.False(MigrationBatchActionPlanner.Applies(MigrationBatchAction.Complete, status));
        _exchange.Batches[Address] = Batch(Address, status: status);

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.False(result.Success);
        Assert.Equal(NotCompletable(status.Trim()), result.Message);
        Assert.Equal([$"Get-MigrationBatch {Address}"], _exchange.CallList());
    }

    // aud-a23r. One owner: over every status this suite can name, the approval runs its batch steps exactly when the batch
    // Complete action would complete that batch, and otherwise writes nothing. A copy of the rule would drift from it.
    [Theory]
    [InlineData("Synced")]
    [InlineData("Syncing")]
    [InlineData("SyncedWithErrors")]
    [InlineData("Starting")]
    [InlineData("Created")]
    [InlineData("Waiting")]
    [InlineData("IncrementalSyncing")]
    [InlineData("Corrupted")]
    [InlineData("Failed")]
    [InlineData("Stopped")]
    [InlineData("Stopping")]
    [InlineData("Removing")]
    [InlineData("Completing")]
    [InlineData("CompletedWithErrors")]
    public async Task TheApproval_CompletesItsBatchExactlyWhenTheCompleteActionWould(string status)
    {
        _exchange.Batches[Address] = Batch(Address, status: status);

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        if (MigrationBatchActionPlanner.Applies(MigrationBatchAction.Complete, status))
        {
            Assert.True(result.Success, result.Message);
            Assert.Equal(EveryStep, _exchange.CallList());
        }
        else
        {
            Assert.False(result.Success);
            Assert.Equal(NotCompletable(status), result.Message);
            Assert.Equal([$"Get-MigrationBatch {Address}"], _exchange.CallList());
        }
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

    // The control: a batch the Complete action completes runs every step, as before - and only such a batch (aud-a23r:
    // this theory let Syncing and SyncedWithErrors run every step).
    [Theory]
    [InlineData("Synced")]
    [InlineData(" synced ")]
    [InlineData("SYNCED")]
    public async Task ABatchTheCompleteActionCompletes_RunsEveryStep(string status)
    {
        Assert.True(MigrationBatchActionPlanner.Applies(MigrationBatchAction.Complete, status));
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
        Assert.Equal(NotCompletable("Stopped"), audited.GetProperty("error").GetString());
        Assert.Equal([$"Get-MigrationBatch {Address}"], _exchange.CallList());
    }

    // ---- aud-a55: a mailbox in a batch of many is approved only from a status the Approve action accepts --------------

    // No batch is named for the mailbox, so the existence read answers not found and the batch steps do not apply.
    private static readonly string[] MailboxSteps =
    [
        $"Get-MigrationBatch {Address}",
        $"Get-MigrationUser {Address}",
        $"Set-MigrationUser -ApproveSkippedItems {Address}",
        $"Set-MigrationUser -CompleteAfter {Address}",
        $"Set-MoveRequest -SkippedItemApprovalTime {Address}",
        $"Resume-MoveRequest {Address}",
    ];

    private static readonly string[] TheTwoReads = [$"Get-MigrationBatch {Address}", $"Get-MigrationUser {Address}"];

    private static string NotAwaitingApproval(string status) =>
        $"{Address} was not approved: its migration status is {status} - not a status the Approve action approves a mailbox from. Nothing was changed.";

    // The migration user Get-MigrationUser -Identity answers; a null status leaves the property out, as a row that reports none.
    private void AMigrationUser(string? status) =>
        _exchange.MigrationUsers[Address] = status is null
            ? Row(("Identity", Address))
            : Row(("Identity", Address), ("Status", status));

    // The finding. The page offered Approve while the mailbox was awaiting approval; by the time the approval ran, Exchange
    // said otherwise - another operator had approved it, it had completed, failed or been stopped. Before, nothing was read
    // and Set-MigrationUser -CompleteAfter, Set-MoveRequest and Resume-MoveRequest ran anyway. Now it is refused naming the
    // status, and the two reads are the only calls.
    [Theory]
    [InlineData("Synced")]
    [InlineData("Completed")]
    [InlineData("Completing")]
    [InlineData("Failed")]
    [InlineData("Stopped")]
    [InlineData(" stopped ")]
    public async Task AMailboxInABatchOfMany_WhoseStatusMovedOn_IsRefusedBeforeAnyWrite(string status)
    {
        Assert.False(MigrationUserActionPlanner.Applies(MigrationUserAction.Approve, status));
        AMigrationUser(status);

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.False(result.Success);
        Assert.Equal(NotAwaitingApproval(status.Trim()), result.Message);
        Assert.Equal(TheTwoReads, _exchange.CallList());
    }

    // One owner: over every status this suite can name, the mailbox steps run exactly when the Approve action applies to
    // that status, and otherwise nothing is written. A copy of the rule would drift from it.
    [Theory]
    [InlineData("NeedsApproval")]
    [InlineData(" needsapproval ")]
    [InlineData("NEEDSAPPROVAL")]
    [InlineData("Synced")]
    [InlineData("Syncing")]
    [InlineData("SyncedWithErrors")]
    [InlineData("Queued")]
    [InlineData("Provisioning")]
    [InlineData("IncrementalSyncing")]
    [InlineData("Completing")]
    [InlineData("Completed")]
    [InlineData("CompletedWithWarnings")]
    [InlineData("CompletionFailed")]
    [InlineData("Corrupted")]
    [InlineData("Failed")]
    [InlineData("Stopped")]
    [InlineData("Stopping")]
    [InlineData("Removing")]
    [InlineData("AStatusNothingHereNames")]
    public async Task AMailboxInABatchOfMany_IsApprovedExactlyWhenTheApproveActionWould(string status)
    {
        AMigrationUser(status);

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        if (MigrationUserActionPlanner.Applies(MigrationUserAction.Approve, status))
        {
            Assert.True(result.Success, result.Message);
            Assert.Equal(MailboxSteps, _exchange.CallList());
        }
        else
        {
            Assert.False(result.Success);
            Assert.Equal(NotAwaitingApproval(status.Trim()), result.Message);
            Assert.Equal(TheTwoReads, _exchange.CallList());
        }
    }

    // A migration user that reports no status cannot be told to be awaiting approval: refused, nothing written.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AMigrationUserThatReportsNoStatus_IsRefused_AndNothingIsWritten(string? status)
    {
        AMigrationUser(status);

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.False(result.Success);
        Assert.Equal(
            $"{Address} was not approved: its migration user reports no status, so whether it is awaiting approval is not known. Nothing was changed.",
            result.Message);
        Assert.Equal(TheTwoReads, _exchange.CallList());
    }

    // A status read that fails is no answer: refused in the cmdlet's own words, nothing written - the existence read's shape.
    [Theory]
    [InlineData("throw")]
    [InlineData("error")]
    public async Task AStatusReadThatFails_ChangesNothing(string how)
    {
        const string refusal = "the migration service is unavailable";
        AMigrationUser("NeedsApproval");
        _exchange.Failures["Get-MigrationUser"] = how == "throw" ? refusal : Refused(refusal);

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.False(result.Success);
        Assert.Equal($"Could not read the migration status of {Address}: {refusal}. Nothing was changed.", result.Message);
        Assert.Equal(TheTwoReads, _exchange.CallList());
    }

    // Exchange's own "no such object", in each shape it is read from: there is no migration to approve.
    [Theory]
    [InlineData("category")]
    [InlineData("type")]
    [InlineData("identifier")]
    public async Task NoMigrationUser_IsRefused_AndNothingIsWritten(string shape)
    {
        _exchange.NotFound = shape switch { "category" => NotFoundByCategory, "type" => NotFoundByType, _ => NotFoundByIdentifier };

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.False(result.Success);
        Assert.Equal(
            $"{Address} was not approved: Exchange has no migration user {Address}, so there is nothing to approve. Nothing was changed.",
            result.Message);
        Assert.Equal(TheTwoReads, _exchange.CallList());
    }

    [Fact]
    public async Task SeveralMigrationUsers_AreRefused_AndNothingIsWritten()
    {
        _exchange.MigrationUsers[Address] = new[]
        {
            Row(("Identity", Address), ("Status", "NeedsApproval")),
            Row(("Identity", Address), ("Status", "NeedsApproval")),
        };

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.False(result.Success);
        Assert.Equal($"Exchange returned 2 migration users for {Address}. Nothing was changed.", result.Message);
        Assert.Equal(TheTwoReads, _exchange.CallList());
    }

    // Through the page's single action: the refusal is the row's failure, audited once as Failed with the refusal as its
    // error.
    [Fact]
    public async Task ARefusalThroughExecuteUserAction_IsOneFailedAuditRow()
    {
        AMigrationUser("Synced");
        var service = _exchange.Service();
        var page = _exchange.Page(service);
        Func<Task<PermissionResult>> approve = () => service.ApproveMigrationUserAsync(Address);

        await Call(page, "ExecuteUserAction", approve, "ApproveMigrationUser", Address, "INC0001");

        Assert.False(Assert.IsType<PermissionResult>(Field("batchActionResult").GetValue(page)).Success);
        var audited = Assert.Single(_exchange.AuditEvents(), e => e.GetProperty("action").GetString() == "ApproveMigrationUser");
        Assert.Equal("Failed", audited.GetProperty("result").GetString());
        Assert.Equal(NotAwaitingApproval("Synced"), audited.GetProperty("error").GetString());
        Assert.Equal(TheTwoReads, _exchange.CallList());
    }

    // The stale page through the mailbox bar: the list it planned from says NeedsApproval, Exchange now says Synced. The
    // refusal is on the row, the row is audited Failed and counted refused in the banner, and nothing is written.
    [Fact]
    public async Task AStaleBulkApproval_IsRefusedOnItsRow_AndAuditedFailed()
    {
        AMigrationUser("Synced");
        var service = _exchange.Service();
        var page = _exchange.Page(service);
        var loaded = new List<MigrationUserInfo> { new() { EmailAddress = Address, Status = "NeedsApproval" } };
        Field("batchUsers").SetValue(page, loaded);
        var plan = MigrationUserActionPlanner.Plan(loaded, [Address], MigrationUserAction.Approve);
        Assert.Equal([Address], plan.Eligible);
        Func<string, Task<PermissionResult>> approve = email => service.ApproveMigrationUserAsync(email);

        await Call(page, "ExecuteBulkMailboxAction", plan, approve, "ApproveMigrationUser", "Migration_ApproveMailboxes", "Queued approval of", "INC0001");

        var outcomes = Assert.IsType<Dictionary<string, string>>(Field("actionOutcomes").GetValue(page));
        Assert.Equal(NotAwaitingApproval("Synced"), outcomes[Address]);
        var shown = Assert.IsType<PermissionResult>(Field("batchActionResult").GetValue(page));
        Assert.False(shown.Success);
        Assert.Contains("1 refused", shown.Message, StringComparison.Ordinal);
        var audited = Assert.Single(_exchange.AuditEvents(), e => e.GetProperty("action").GetString() == "ApproveMigrationUser");
        Assert.Equal("Failed", audited.GetProperty("result").GetString());
        Assert.Equal(NotAwaitingApproval("Synced"), audited.GetProperty("error").GetString());
        Assert.Equal(TheTwoReads, _exchange.CallList());
    }
}
