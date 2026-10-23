using System.Collections;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a23m. (1) <c>CheckSingleEligibility</c>'s catch covered the audit write and the admin notification as
/// well as the check, so an audit or a notification that threw replaced a verdict already reached with "Could not be
/// determined: &lt;that error&gt;" - and the catch's own audit could then throw out of the handler. (2) Every write on the
/// Migration page completed its progress activity as succeeded whatever it did; and the two create handlers sent their
/// admin notification outside any guard, so one that threw replaced a created batch's result with a failure.
/// </summary>
/// <remarks>
/// The page's own handlers over <see cref="MigrationStubExchange"/>, with the recording progress channel (first Complete
/// wins, as the real one's does), a recording or throwing admin notification, and the stub's protection stand-in where a
/// create has to get through. A throwing audit is stood in for by no audit service at all: AuditService's methods are not
/// virtual and its writer swallows its own failures (aud-a28q), so a call on none throws, as any throw out of it would.
/// No Exchange, no directory, no mail.
/// </remarks>
public sealed class MigrationVerdictAndWriteOutcomeTests : IDisposable
{
    private const string Address = "alice@contoso.test";
    private const string BatchName = "Wave 12";
    private const string Refusal = "the account lacks the Mail Recipients role";
    private const string Undetermined = $"Could not be determined: Get-MigrationUser failed: {Refusal}";

    private readonly MigrationStubExchange _exchange = new();
    private readonly RecordingProgress _progress = new();

    public void Dispose() => _exchange.Dispose();

    private RenderableMigration Page(MigrationService service, RecordingEmail? email = null)
    {
        var page = _exchange.Page(service);
        Set(page, "Progress", _progress);
        if (email is not null)
            Set(page, "Email", email);
        return page;
    }

    private (bool Success, string? Message) Outcome(string label) =>
        Assert.Single(_progress.Handles, handle => handle.Label == label).Outcome
        ?? throw new Xunit.Sdk.XunitException($"the activity '{label}' was never completed");

    // ---- part 1: an eligibility verdict outlives its audit and its notification ---------------------

    // A mailbox in Exchange Online, checked for the move back on-premises: eligible, or - Get-MigrationUser refused -
    // undetermined. Either is a verdict the service reached.
    private RenderableMigration CheckPage(string answer, RecordingEmail email, bool withAudit)
    {
        _exchange.Mailboxes[Address] = CloudMailbox(Address);
        if (answer == "undetermined")
            _exchange.Failures["Get-MigrationUser"] = Refused(Refusal);
        var page = Page(_exchange.Service(), email);
        if (!withAudit)
            Set(page, "Audit", null);
        Field("singleEmail").SetValue(page, Address);
        Field("singleTicketNumber").SetValue(page, "INC0001");
        Field("singleMigrationDirection").SetValue(page, MigrationDirection.ToOnPrem);
        return page;
    }

    private static void AssertTheVerdict(string answer, RenderableMigration page)
    {
        var result = Assert.IsType<MigrationEligibilityResult>(Field("singleResult").GetValue(page));
        if (answer == "eligible")
        {
            Assert.Equal(MigrationStatus.Eligible, result.Status);
            Assert.Empty(result.IneligibilityReasons);
        }
        else
        {
            Assert.Equal(MigrationStatus.Undetermined, result.Status);
            Assert.Equal([Undetermined], result.IneligibilityReasons);
        }
    }

    private static (bool, string?) CheckOutcome(string answer) =>
        answer == "eligible" ? (true, null) : (false, Undetermined);

    // The finding. Before, the audit's throw reached the check's catch: the verdict became "Could not be determined:
    // Object reference not set ...", and the catch's own audit then threw out of the handler.
    [Theory]
    [InlineData("eligible")]
    [InlineData("undetermined")]
    public async Task AnAuditThatThrows_LeavesTheVerdictStanding_AndTheNotificationStillGoes(string answer)
    {
        var email = _exchange.Email();
        var page = CheckPage(answer, email, withAudit: false);

        await Call(page, "CheckSingleEligibility");

        AssertTheVerdict(answer, page);
        Assert.Equal(["CheckMigrationEligibility_ToOnPrem"], email.Sent);
        Assert.Equal(CheckOutcome(answer), Outcome("Checking eligibility"));
    }

    // The finding, the notification's half. Before, a notification that threw replaced the verdict with "Could not be
    // determined: <the mail error>" and wrote a second audit row, status Error.
    [Theory]
    [InlineData("eligible")]
    [InlineData("undetermined")]
    public async Task ANotificationThatThrows_LeavesTheVerdictStanding_AndItsOneAuditRow(string answer)
    {
        var email = _exchange.Email(throws: true);
        var page = CheckPage(answer, email, withAudit: true);

        await Call(page, "CheckSingleEligibility");

        AssertTheVerdict(answer, page);
        var check = Assert.Single(_exchange.AuditEvents(), e => e.GetProperty("action").GetString() == "CheckMigrationEligibility");
        Assert.Equal(answer == "eligible" ? "Eligible" : "Undetermined", check.GetProperty("status").GetString());
        Assert.Equal(CheckOutcome(answer), Outcome("Checking eligibility"));
    }

    // The page catch's own audit is a guarded step too: a check that fails before the service returns, with an audit
    // that throws, is still shown as undetermined, and nothing escapes the handler.
    [Fact]
    public async Task ACheckThatFailsBeforeTheServiceReturns_WithAnAuditThatThrows_IsStillUndetermined()
    {
        var page = Page(_exchange.Service(connects: false));
        Set(page, "Audit", null);
        Field("singleEmail").SetValue(page, Address);
        Field("singleMigrationDirection").SetValue(page, MigrationDirection.ToOnPrem);

        await Call(page, "CheckSingleEligibility");

        var result = Assert.IsType<MigrationEligibilityResult>(Field("singleResult").GetValue(page));
        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Equal(["Could not be determined: the Exchange Online sign-in could not be completed"], result.IneligibilityReasons);
        Assert.Equal((false, "Could not be determined: the Exchange Online sign-in could not be completed"), Outcome("Checking eligibility"));
    }

    // ---- part 2: every write completes its activity with its outcome --------------------------------

    public static TheoryData<string, bool> Writes => new()
    {
        { "ExecuteUserAction", true },
        { "ExecuteUserAction", false },
        { "ExecuteBatchAction", true },
        { "ExecuteBatchAction", false },
        { "ExecuteBulkBatchAction", true },
        { "ExecuteBulkBatchAction", false },
        { "ExecuteBulkMailboxAction", true },
        { "ExecuteBulkMailboxAction", false },
        { "CreateSingleMigrationBatch", true },
        { "CreateSingleMigrationBatch", false },
        { "CreateMigrationBatch", true },
        { "CreateMigrationBatch", false },
    };

    // The finding. Each write completed its activity as succeeded on every path; a write that failed now completes it
    // not succeeded, with the failure the page shows, and one that succeeded completes it succeeded.
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task EveryWrite_CompletesItsActivityWithItsOutcome(string handler, bool fails)
    {
        var (label, shown) = await RunWrite(handler, fails);

        var outcome = Outcome(label);
        if (fails)
        {
            Assert.False(shown.Success);
            Assert.Equal((false, shown.Message), outcome);
        }
        else
        {
            Assert.True(shown.Success, shown.Message);
            Assert.Equal((true, null), outcome);
        }
    }

    // A create whose notification throws keeps the batch's result; before, the throw reached the handler's catch and the
    // page showed the mail error as the create's failure.
    [Theory]
    [InlineData("CreateSingleMigrationBatch")]
    [InlineData("CreateMigrationBatch")]
    public async Task ACreateWhoseNotificationThrows_KeepsItsResult(string handler)
    {
        var email = _exchange.Email(throws: true);

        var (label, shown) = await RunWrite(handler, fails: false, email);

        Assert.True(shown.Success, shown.Message);
        Assert.Equal((true, null), Outcome(label));
        Assert.Equal(["CreateMigrationBatch_ToCloud"], email.Sent);
    }

    // "Nothing to do" is an answer, not a failure (D2(a)): a bulk run with every selected row skipped succeeds.
    [Theory]
    [InlineData("ExecuteBulkBatchAction")]
    [InlineData("ExecuteBulkMailboxAction")]
    public async Task ABulkRunWithNothingToDo_Succeeds(string handler)
    {
        var service = _exchange.Service();
        var page = Page(service);
        string label;
        if (handler == "ExecuteBulkBatchAction")
        {
            Field("migrationBatches").SetValue(page, new List<MigrationBatchInfo>());
            Func<string, Task<PermissionResult>> stop = name => service.StopMigrationBatchAsync(name);
            await Call(page, handler, (IReadOnlyList<string>)[], (IReadOnlyList<MigrationBatchSkip>)[new(BatchName, "Completed")],
                stop, "StopMigrationBatch", "Migration_StopSelected", "Queued to stop", "INC0001");
            label = "StopMigrationBatch on 0 batch(es)"; // aud-a68: the run's own name; it was "Removing completed batches" whatever ran
        }
        else
        {
            Field("batchUsers").SetValue(page, new List<MigrationUserInfo> { new() { EmailAddress = Address, Status = "Completed" } });
            Func<string, Task<PermissionResult>> pause = email => service.StopMigrationUserAsync(email);
            await Call(page, handler, new MigrationUserActionPlan([], [new(Address, "Completed")]), pause,
                "StopMigrationUser", "Migration_PauseSelected", "Paused", "INC0001");
            label = "Applying the bulk mailbox action";
        }

        Assert.Equal((true, null), Outcome(label));
        Assert.True(Assert.IsType<PermissionResult>(Field("batchActionResult").GetValue(page)).Success);
    }

    private async Task<(string Label, PermissionResult Shown)> RunWrite(string handler, bool fails, RecordingEmail? email = null)
    {
        switch (handler)
        {
            case "ExecuteUserAction":
                {
                    if (fails)
                        _exchange.Failures["Stop-MigrationUser"] = Refused("the mailbox is not moving");
                    var service = _exchange.Service();
                    var page = Page(service, email);
                    Func<Task<PermissionResult>> stop = () => service.StopMigrationUserAsync(Address);
                    await Call(page, handler, stop, "StopMigrationUser", Address, "INC0001");
                    return ($"StopMigrationUser on {Address}", Shown(page, "batchActionResult"));
                }
            case "ExecuteBatchAction":
                {
                    if (fails)
                        _exchange.Failures["Stop-MigrationBatch"] = Refused("the batch is not moving");
                    var service = _exchange.Service();
                    var page = Page(service, email);
                    Func<Task<PermissionResult>> stop = () => service.StopMigrationBatchAsync(BatchName);
                    await Call(page, handler, stop, "StopMigrationBatch", BatchName, "INC0001");
                    return ($"StopMigrationBatch on {BatchName}", Shown(page, "batchActionResult"));
                }
            case "ExecuteBulkBatchAction":
                {
                    if (fails)
                        _exchange.Failures["Stop-MigrationBatch"] = Refused("the batch is not moving");
                    var service = _exchange.Service();
                    var page = Page(service, email);
                    Field("migrationBatches").SetValue(page, new List<MigrationBatchInfo>());
                    Func<string, Task<PermissionResult>> stop = name => service.StopMigrationBatchAsync(name);
                    await Call(page, handler, (IReadOnlyList<string>)[BatchName], (IReadOnlyList<MigrationBatchSkip>)[], stop,
                        "StopMigrationBatch", "Migration_StopSelected", "Queued to stop", "INC0001");
                    return ("StopMigrationBatch on 1 batch(es)", Shown(page, "batchActionResult")); // aud-a68: the run's own name
                }
            case "ExecuteBulkMailboxAction":
                {
                    if (fails)
                        _exchange.Failures["Stop-MigrationUser"] = Refused("the mailbox is not moving");
                    var service = _exchange.Service();
                    var page = Page(service, email);
                    Field("batchUsers").SetValue(page, new List<MigrationUserInfo> { new() { EmailAddress = Address, Status = "Syncing" } });
                    Func<string, Task<PermissionResult>> pause = address => service.StopMigrationUserAsync(address);
                    await Call(page, handler, new MigrationUserActionPlan([Address], []), pause,
                        "StopMigrationUser", "Migration_PauseSelected", "Paused", "INC0001");
                    return ("Applying the bulk mailbox action", Shown(page, "batchActionResult"));
                }
            case "CreateSingleMigrationBatch":
                {
                    _exchange.Protection = new Hashtable { ["*"] = fails ? "unavailable" : "clear" };
                    var page = Page(_exchange.Service(), email);
                    Field("singleResult").SetValue(page, new MigrationEligibilityResult { EmailAddress = Address, Status = MigrationStatus.Eligible });
                    Field("singleTicketNumber").SetValue(page, "INC0001");
                    Field("singleMigrationDirection").SetValue(page, MigrationDirection.ToCloud);
                    await Call(page, handler);
                    return ("Creating the migration batch", Shown(page, "singleCreateResult"));
                }
            case "CreateMigrationBatch":
                {
                    _exchange.Protection = new Hashtable { ["*"] = fails ? "unavailable" : "clear" };
                    var page = Page(_exchange.Service(), email);
                    Field("bulkResult").SetValue(page, new MigrationBatchResult
                    {
                        BatchName = BatchName,
                        Success = true,
                        Direction = MigrationDirection.ToCloud,
                        TotalUsers = 1,
                        EligibleUsers = 1,
                        EligibilityResults = [new MigrationEligibilityResult { EmailAddress = Address, Status = MigrationStatus.Eligible }],
                    });
                    Field("bulkTicketNumber").SetValue(page, "INC0001");
                    Field("migrationDirection").SetValue(page, MigrationDirection.ToCloud);
                    await Call(page, handler);
                    return ("Creating the migration batch", Shown(page, "createResult"));
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(handler), handler, null);
        }
    }

    private static PermissionResult Shown(RenderableMigration page, string field) =>
        Assert.IsType<PermissionResult>(Field(field).GetValue(page));
}
