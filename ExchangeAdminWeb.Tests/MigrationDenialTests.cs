using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a23q. A failed authorization on a single batch or mailbox action, or on either create, returned with
/// nothing shown and no audit row - the bulk executors audit a "_Denied" row - and its activity ended "Nothing was done." /
/// "Nothing was created."; a create's guard return showed nothing either. The Constitution: every denied mutating attempt is
/// audited, and UI hiding is not security, so a direct event invocation must be refused visibly. aud-a55: the two bulk
/// executors' "no list loaded" guard returns showed nothing either. aud-a68: the two bulk executors' own denials audited a
/// row with no mutationAttempted, and said only "Authorization denied.".
/// </summary>
/// <remarks>
/// The page's own handlers over <see cref="MigrationStubExchange"/>, its authorization stand-in denying one policy, its
/// progress channel recording how each activity ended, and the audit file read back. No Exchange.
/// </remarks>
public sealed class MigrationDenialTests : IDisposable
{
    private const string Address = "alice@contoso.test";
    private const string Batch = "Wave_12";
    private const string Ticket = "INC0042";

    private readonly MigrationStubExchange _exchange = new();
    private readonly RecordingProgress _progress = new();

    public void Dispose() => _exchange.Dispose();

    private RenderableMigration Page(string? deny)
    {
        var page = _exchange.Page(_exchange.Service(), deny);
        Set(page, "Progress", _progress);
        return page;
    }

    private (bool Success, string? Message)? Outcome() => Assert.Single(_progress.Handles).Outcome;

    private JsonElement OneDeniedRow(string action)
    {
        var row = Assert.Single(_exchange.AuditEvents());
        Assert.Equal($"{action}_Denied", row.GetProperty("action").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
        return row;
    }

    // ---- the finding: a single action ---------------------------------------------------------

    // Before: no banner, no audit row, and the activity ended "Nothing was done." - a refusal nobody could see afterwards.
    [Theory]
    [InlineData("ExecuteBatchAction", "CompleteMigrationBatch", Batch)]
    [InlineData("ExecuteUserAction", "ApproveMigrationUser", Address)]
    public async Task ADeniedAction_IsShown_Audited_AndNothingRuns(string handler, string auditAction, string target)
    {
        var page = Page(deny: "MigrationManage");
        var ran = false;
        Func<Task<PermissionResult>> action = () => { ran = true; return Task.FromResult(PermissionResult.Ok()); };

        await Call(page, handler, action, auditAction, target, Ticket);

        var expected = $"Not authorized to {auditAction} on {target}; nothing was done.";
        var shown = Assert.IsType<PermissionResult>(Field("batchActionResult").GetValue(page));
        Assert.False(shown.Success);
        Assert.Equal(expected, shown.Message);
        Assert.False(ran);
        var row = OneDeniedRow(auditAction);
        Assert.Equal(target, row.GetProperty("target").GetString());
        Assert.False(row.TryGetProperty("targets", out _)); // kd-17: a single action's target is the one it names; no list
        Assert.Equal(Ticket, row.GetProperty("ticket").GetString());
        Assert.Equal("MigrationManage authorization denied", row.GetProperty("error").GetString());
        Assert.Equal((false, expected), Outcome());

        Field("tabIndex").SetValue(page, 2);
        Assert.Contains(expected, page.RenderText(), StringComparison.Ordinal);
    }

    // The control: authorized, the action runs and is audited as before, and no denial is written.
    [Fact]
    public async Task AnAuthorizedAction_RunsAsBefore()
    {
        var page = Page(deny: null);
        var ran = false;
        Func<Task<PermissionResult>> action = () => { ran = true; return Task.FromResult(PermissionResult.Fail("Exchange refused it")); };

        await Call(page, "ExecuteUserAction", action, "StopMigrationUser", Address, Ticket);

        Assert.True(ran);
        var row = Assert.Single(_exchange.AuditEvents());
        Assert.Equal("StopMigrationUser", row.GetProperty("action").GetString());
        Assert.Equal((false, "Exchange refused it"), Outcome());
    }

    // A denial whose audit write throws is still shown and still ends the activity; nothing escapes the handler. The audit
    // service's methods are not virtual and its writer swallows its own failures (aud-a28q), so a throwing audit is stood in
    // for by none at all, as aud-a23m's tests do.
    [Fact]
    public async Task ADenialWhoseAuditWriteThrows_IsStillShown()
    {
        var page = Page(deny: "MigrationManage");
        Set(page, "Audit", null);
        Func<Task<PermissionResult>> action = () => Task.FromResult(PermissionResult.Ok());

        await Call(page, "ExecuteBatchAction", action, "StopMigrationBatch", Batch, Ticket);

        var expected = $"Not authorized to StopMigrationBatch on {Batch}; nothing was done.";
        Assert.Equal(expected, Assert.IsType<PermissionResult>(Field("batchActionResult").GetValue(page)).Message);
        Assert.Equal((false, expected), Outcome());
    }

    // ---- the finding: either create ------------------------------------------------------------

    // Before: nothing shown, no row, "Nothing was created.".
    [Theory]
    [InlineData(MigrationDirection.ToCloud)]
    [InlineData(MigrationDirection.ToOnPrem)]
    public async Task ADeniedSingleCreate_IsShown_Audited_AndNothingIsCreated(MigrationDirection direction)
    {
        var page = Page(deny: "MigrationCreate");
        Field("singleResult").SetValue(page, new MigrationEligibilityResult { EmailAddress = Address, Status = MigrationStatus.Eligible });
        Field("singleTicketNumber").SetValue(page, Ticket);
        Field("singleMigrationDirection").SetValue(page, direction);

        await Call(page, "CreateSingleMigrationBatch");

        const string expected = "Not authorized to create a migration batch; nothing was done.";
        var shown = Assert.IsType<PermissionResult>(Field("singleCreateResult").GetValue(page));
        Assert.Equal((false, expected), (shown.Success, shown.Message));
        var row = OneDeniedRow($"CreateMigrationBatch_{direction}");
        Assert.Equal(Address, row.GetProperty("target").GetString());
        Assert.False(row.TryGetProperty("targets", out _)); // kd-17: unchanged for a create
        Assert.Equal(Ticket, row.GetProperty("ticket").GetString());
        Assert.Equal("MigrationCreate authorization denied", row.GetProperty("error").GetString());
        Assert.DoesNotContain(_exchange.CallList(), call => call.StartsWith("New-MigrationBatch", StringComparison.Ordinal));
        Assert.Equal((false, expected), Outcome());
        Assert.Contains(expected, page.RenderText(), StringComparison.Ordinal);
    }

    // The target is the batch name typed, or "(bulk batch)" when none was.
    [Theory]
    [InlineData(Batch, Batch)]
    [InlineData("  ", "(bulk batch)")]
    public async Task ADeniedBulkCreate_IsShown_Audited_AndNothingIsCreated(string typed, string target)
    {
        var page = Page(deny: "MigrationCreate");
        Field("bulkResult").SetValue(page, OneEligibleRow());
        Field("bulkTicketNumber").SetValue(page, Ticket);
        Field("bulkBatchName").SetValue(page, typed);
        Field("tabIndex").SetValue(page, 1);

        await Call(page, "CreateMigrationBatch");

        const string expected = "Not authorized to create a migration batch; nothing was done.";
        var shown = Assert.IsType<PermissionResult>(Field("createResult").GetValue(page));
        Assert.Equal((false, expected), (shown.Success, shown.Message));
        var row = OneDeniedRow("CreateMigrationBatch_ToCloud");
        Assert.Equal(target, row.GetProperty("target").GetString());
        Assert.Equal("MigrationCreate authorization denied", row.GetProperty("error").GetString());
        Assert.DoesNotContain(_exchange.CallList(), call => call.StartsWith("New-MigrationBatch", StringComparison.Ordinal));
        Assert.Equal((false, expected), Outcome());
        Assert.Contains(expected, page.RenderText(), StringComparison.Ordinal);
    }

    // ---- a guard return says why nothing was created -------------------------------------------

    // Not a denial: nothing was attempted, so nothing is audited (the bulk executors' "nothing to do" rule), but the
    // operator is told which guard stopped it and the activity ends with that, where it said nothing and "Nothing was
    // created.".
    [Theory]
    [InlineData("no check", "No eligibility check has been run for this mailbox; nothing was created.")]
    [InlineData("ineligible", "alice@contoso.test's eligibility check reads Ineligible, and only an eligible mailbox can be migrated; nothing was created.")]
    [InlineData("undetermined", "alice@contoso.test's eligibility check reads Undetermined, and only an eligible mailbox can be migrated; nothing was created.")]
    [InlineData("no ticket", "A ticket number is required; nothing was created.")]
    public async Task ASingleCreateGuard_SaysWhyNothingWasCreated(string how, string expected)
    {
        var page = Page(deny: null);
        if (how != "no check")
        {
            var status = how switch { "ineligible" => MigrationStatus.Ineligible, "undetermined" => MigrationStatus.Undetermined, _ => MigrationStatus.Eligible };
            Field("singleResult").SetValue(page, new MigrationEligibilityResult { EmailAddress = Address, Status = status });
        }
        Field("singleTicketNumber").SetValue(page, how == "no ticket" ? " " : Ticket);

        await Call(page, "CreateSingleMigrationBatch");

        var shown = Assert.IsType<PermissionResult>(Field("singleCreateResult").GetValue(page));
        Assert.Equal((false, expected), (shown.Success, shown.Message));
        Assert.Empty(_exchange.AuditEvents());
        Assert.DoesNotContain(_exchange.CallList(), call => call.StartsWith("New-MigrationBatch", StringComparison.Ordinal));
        Assert.Equal((false, expected), Outcome());
    }

    [Theory]
    [InlineData("no check", "No eligibility check of a file has been run; nothing was created.")]
    [InlineData("none eligible", "No mailbox in the checked file is eligible; nothing was created.")]
    [InlineData("no ticket", "A ticket number is required; nothing was created.")]
    public async Task ABulkCreateGuard_SaysWhyNothingWasCreated(string how, string expected)
    {
        var page = Page(deny: null);
        if (how != "no check")
            Field("bulkResult").SetValue(page, how == "none eligible" ? NoEligibleRow() : OneEligibleRow());
        Field("bulkTicketNumber").SetValue(page, how == "no ticket" ? "" : Ticket);

        await Call(page, "CreateMigrationBatch");

        var shown = Assert.IsType<PermissionResult>(Field("createResult").GetValue(page));
        Assert.Equal((false, expected), (shown.Success, shown.Message));
        Assert.Empty(_exchange.AuditEvents());
        Assert.DoesNotContain(_exchange.CallList(), call => call.StartsWith("New-MigrationBatch", StringComparison.Ordinal));
        Assert.Equal((false, expected), Outcome());
    }

    // ---- aud-a55: a bulk executor with no list loaded says so -------------------------------------

    // Not a denial and nothing attempted, so nothing is audited - but the operator is told why nothing happened and the
    // activity ends with that, where both executors returned with no banner and an activity that read "Nothing was done.".
    [Theory]
    [InlineData("ExecuteBulkBatchAction", "No migration batch list is loaded; nothing was done.")]
    [InlineData("ExecuteBulkMailboxAction", "No mailbox list is loaded; nothing was done.")]
    public async Task ABulkExecutorWithNoListLoaded_SaysSo_AndNothingRuns(string handler, string expected)
    {
        var page = Page(deny: null);
        var ran = false;
        Func<string, Task<PermissionResult>> action = _ => { ran = true; return Task.FromResult(PermissionResult.Ok()); };

        if (handler == "ExecuteBulkBatchAction")
        {
            await Call(page, handler, (IReadOnlyList<string>)[Batch], (IReadOnlyList<MigrationBatchSkip>)[], action,
                "StopMigrationBatch", "Migration_StopSelected", "Queued to stop", Ticket);
        }
        else
        {
            await Call(page, handler, new MigrationUserActionPlan([Address], []), action,
                "StopMigrationUser", "Migration_PauseSelected", "Queued pause of", Ticket);
        }

        var shown = Assert.IsType<PermissionResult>(Field("batchActionResult").GetValue(page));
        Assert.Equal((false, expected), (shown.Success, shown.Message));
        Assert.False(ran);
        Assert.Empty(_exchange.AuditEvents());
        Assert.Equal((false, expected), Outcome());

        Field("tabIndex").SetValue(page, 2);
        Assert.Contains(expected, page.RenderText(), StringComparison.Ordinal);
    }

    // ---- aud-a68 (7): a bulk executor's denial, through RecordDenial -------------------------------

    // Before: the "_Denied" row had no mutationAttempted - the single actions' and the creates' denials, through
    // RecordDenial, carry false - and the banner and the activity said only "Authorization denied.". kd-17: and its target
    // was the run's notification action ("Migration_StopSelected"), so the row could not say what was denied; it is the
    // run's scope in the confirm bar's words, with the names it would have acted on in targets.
    [Theory]
    [InlineData("ExecuteBulkBatchAction", "StopMigrationBatch", "Migration_StopSelected", "StopMigrationBatch on 1 batch(es)", "1 batch(es)", Batch)]
    [InlineData("ExecuteBulkMailboxAction", "StopMigrationUser", "Migration_PauseMailboxes", "StopMigrationUser on 1 mailbox(es)", "1 mailbox(es)", Address)]
    public async Task ADeniedBulkAction_IsShown_AuditedAsNotAttempted_AndNothingRuns(string handler, string auditAction, string notificationAction, string described, string target, string attempted)
    {
        var page = Page(deny: "MigrationManage");
        var ran = false;
        Func<string, Task<PermissionResult>> action = _ => { ran = true; return Task.FromResult(PermissionResult.Ok()); };

        if (handler == "ExecuteBulkBatchAction")
            await Call(page, handler, (IReadOnlyList<string>)[Batch], (IReadOnlyList<MigrationBatchSkip>)[], action, auditAction, notificationAction, "Queued stop of", Ticket);
        else
            await Call(page, handler, new MigrationUserActionPlan([Address], []), action, auditAction, notificationAction, "Queued pause of", Ticket);

        var expected = $"Not authorized to {described}; nothing was done.";
        var shown = Assert.IsType<PermissionResult>(Field("batchActionResult").GetValue(page));
        Assert.Equal((false, expected), (shown.Success, shown.Message));
        Assert.False(ran);
        var row = OneDeniedRow(auditAction);
        var deniedTarget = row.GetProperty("target").GetString();
        Assert.DoesNotMatch(@"^Migration_", deniedTarget ?? "");
        Assert.Equal(target, deniedTarget);
        Assert.Equal([attempted], row.GetProperty("targets").EnumerateArray().Select(name => name.GetString()));
        Assert.Equal(Ticket, row.GetProperty("ticket").GetString());
        Assert.Equal("MigrationManage authorization denied", row.GetProperty("error").GetString());
        Assert.Equal((false, expected), Outcome());

        Field("tabIndex").SetValue(page, 2);
        Assert.Contains(expected, page.RenderText(), StringComparison.Ordinal);
    }

    private static MigrationBatchResult OneEligibleRow() => new()
    {
        BatchName = "2026-10-22_Move",
        Success = true,
        TotalUsers = 1,
        EligibleUsers = 1,
        EligibilityResults = [new MigrationEligibilityResult { EmailAddress = Address, Status = MigrationStatus.Eligible }],
    };

    private static MigrationBatchResult NoEligibleRow() => new()
    {
        BatchName = "2026-10-22_Move",
        Success = true,
        TotalUsers = 1,
        IneligibleUsers = 1,
        EligibilityResults = [new MigrationEligibilityResult { EmailAddress = Address, Status = MigrationStatus.Ineligible }],
    };
}
