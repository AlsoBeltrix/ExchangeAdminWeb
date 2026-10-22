using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a23h. <c>CheckMigrationEligibilityAsync</c> read <c>Get-MigrationUser</c> and <c>Get-Mailbox</c>
/// under <c>-ErrorAction Ignore</c>, so a read that failed - a throw included - read as "no migration in progress" or
/// "no cloud mailbox", and a mailbox could be called eligible when Exchange could not be asked.
/// <c>ApproveMigrationUserAsync</c> ran <c>Set-MigrationBatch</c> twice and <c>Complete-MigrationBatch</c> the same way,
/// so none of them could fail, and the approval reported "initiated completion" whether or not the batch was completed.
/// </summary>
/// <remarks>
/// Behaviour, not text: the real service over <see cref="MigrationStubExchange"/>, and the page's own handlers and
/// render tree. No Exchange.
/// </remarks>
public sealed class MigrationEligibilityAndApproveTests : IDisposable
{
    private const string Address = "alice@contoso.test";
    private const string Other = "bob@contoso.test";
    private const string Refusal = "the account lacks the Mail Recipients role";

    private readonly MigrationStubExchange _exchange = new();

    public void Dispose() => _exchange.Dispose();

    private void Fail(string step, string how, string message)
    {
        if (how == "throw")
            _exchange.Failures[step] = message;
        else
            _exchange.Failures[step] = Refused(message);
    }

    private static string[] ApprovalSteps(bool ownBatch) => ownBatch
        ?
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
        ]
        :
        [
            $"Get-MigrationBatch {Address}",
            $"Set-MigrationUser -ApproveSkippedItems {Address}",
            $"Set-MigrationUser -CompleteAfter {Address}",
            $"Set-MoveRequest -SkippedItemApprovalTime {Address}",
            $"Resume-MoveRequest {Address}",
        ];

    // ---- eligibility: a read that fails ----------------------------------------------------------

    // The finding. A mailbox in Exchange Online, being checked for the move back on-premises: before, a failed
    // Get-MigrationUser read as no migration in progress and the mailbox was ELIGIBLE; a failed Get-Mailbox read as no
    // cloud mailbox. Each failure the two ways a cmdlet can: a terminating error, and an error it writes.
    [Theory]
    [InlineData("Get-MigrationUser", "throw")]
    [InlineData("Get-MigrationUser", "error")]
    [InlineData("Get-Mailbox", "throw")]
    [InlineData("Get-Mailbox", "error")]
    public async Task AnEligibilityReadThatFails_CouldNotBeDetermined_NeverAGuess(string read, string how)
    {
        _exchange.Mailboxes[Address] = CloudMailbox(Address);
        Fail(read, how, Refusal);

        var result = await _exchange.Service().CheckMigrationEligibilityAsync(Address, MigrationDirection.ToOnPrem);

        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Contains($"Could not be determined: {read} failed: {Refusal}", result.IneligibilityReasons);
        Assert.DoesNotContain(result.IneligibilityReasons, reason => reason.Contains("The running command stopped", StringComparison.Ordinal));
        Assert.DoesNotContain("Migration already in progress", result.IneligibilityReasons);
        Assert.DoesNotContain(result.IneligibilityReasons, reason => reason.StartsWith("Not a cloud mailbox", StringComparison.Ordinal));
    }

    // A session without the cmdlet - a role not granted, a module not loaded. PowerShell files that under ObjectNotFound
    // too, and it is an answer about the session, never about the mailbox.
    [Fact]
    public async Task ASessionWithoutTheCmdlet_CouldNotBeDetermined()
    {
        _exchange.Mailboxes[Address] = CloudMailbox(Address);
        _exchange.Missing.Add("Get-MigrationUser");

        var result = await _exchange.Service().CheckMigrationEligibilityAsync(Address, MigrationDirection.ToOnPrem);

        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Contains(result.IneligibilityReasons, reason => reason.StartsWith("Could not be determined: Get-MigrationUser failed: ", StringComparison.Ordinal));
    }

    // Only structure is read. A refusal whose message SAYS the object "couldn't be found", and a throw whose text names
    // Exchange's not-found type, are failures like any other.
    [Theory]
    [InlineData("a written error whose message says it")]
    [InlineData("a throw whose text names the type")]
    public async Task TextThatSaysNotFound_IsNotReadAsAnAnswer(string form)
    {
        _exchange.Mailboxes[Address] = CloudMailbox(Address);
        if (form == "a throw whose text names the type")
            _exchange.Failures["Get-MigrationUser"] = $"|Microsoft.Exchange.Configuration.Tasks.ManagementObjectNotFoundException|The operation couldn't be performed because object '{Address}' couldn't be found.";
        else
            _exchange.Failures["Get-MigrationUser"] = Refused($"The operation couldn't be performed because object '{Address}' couldn't be found.");

        var result = await _exchange.Service().CheckMigrationEligibilityAsync(Address, MigrationDirection.ToOnPrem);

        Assert.Equal(MigrationStatus.Undetermined, result.Status);
    }

    // An answer that was already in stays: a migration in progress makes the mailbox ineligible whatever the next read
    // says, and the failed read is listed beside it.
    [Fact]
    public async Task AReadThatFailsAfterAnIneligibleAnswer_StaysIneligible_WithBothReasons()
    {
        _exchange.MigrationUsers[Address] = Row(("Identity", Address));
        Fail("Get-Mailbox", "error", Refusal);

        var result = await _exchange.Service().CheckMigrationEligibilityAsync(Address, MigrationDirection.ToOnPrem);

        Assert.Equal(MigrationStatus.Ineligible, result.Status);
        Assert.Equal(["Migration already in progress", $"Could not be determined: Get-Mailbox failed: {Refusal}"], result.IneligibilityReasons);
    }

    // ---- eligibility: Exchange's own "no such object" keeps its meaning ----------------------------

    [Theory]
    [InlineData("by its category")]
    [InlineData("by its exception's type")]
    [InlineData("by its identifier")]
    public async Task ExchangesNotFound_KeepsItsMeaning(string shape)
    {
        _exchange.NotFound = shape switch
        {
            "by its category" => NotFoundByCategory,
            "by its exception's type" => NotFoundByType,
            _ => NotFoundByIdentifier,
        };
        _exchange.Mailboxes[Address] = CloudMailbox(Address);
        var service = _exchange.Service();

        // No migration user: not "in progress". A cloud mailbox: eligible to move back.
        var inCloud = await service.CheckMigrationEligibilityAsync(Address, MigrationDirection.ToOnPrem);
        Assert.Equal(MigrationStatus.Eligible, inCloud.Status);
        Assert.Empty(inCloud.IneligibilityReasons);

        // No mailbox in Exchange Online: "not a cloud mailbox", as it always read.
        var onPremises = await service.CheckMigrationEligibilityAsync(Other, MigrationDirection.ToOnPrem);
        Assert.Equal(MigrationStatus.Ineligible, onPremises.Status);
        Assert.Equal(["Not a cloud mailbox (must be in Exchange Online to migrate back to on-premises)"], onPremises.IneligibilityReasons);
    }

    [Fact]
    public async Task AMigrationUserThatExists_IsStillAMigrationInProgress()
    {
        _exchange.MigrationUsers[Address] = Row(("Identity", Address));
        _exchange.Mailboxes[Address] = CloudMailbox(Address);

        var result = await _exchange.Service().CheckMigrationEligibilityAsync(Address, MigrationDirection.ToOnPrem);

        Assert.Equal(MigrationStatus.Ineligible, result.Status);
        Assert.Equal(["Migration already in progress"], result.IneligibilityReasons);
    }

    // The recogniser on its own, over exceptions PowerShell itself raised under -ErrorAction Stop - each as the call
    // throws it, and inside the wrapper the base class's Invoke throws.
    [Theory]
    [InlineData("Write-Error -Message 'no such object' -Category ObjectNotFound", true)]
    [InlineData("$PSCmdlet.WriteError($ByType)", true)]
    [InlineData("$PSCmdlet.WriteError($ByIdentifier)", true)]
    [InlineData("$PSCmdlet.ThrowTerminatingError($ByCategory)", true)]
    [InlineData("Write-Error -Message 'The operation could not be performed because the object could not be found'", false)]
    [InlineData("throw '|Microsoft.Exchange.Configuration.Tasks.ManagementObjectNotFoundException|no such object'", false)]
    [InlineData("Get-CmdletThisSessionDoesNotHave", false)]
    public void IsNotFound_ReadsTheRecordsStructure_NeverItsText(string body, bool notFound)
    {
        var thrown = Raise(body);

        Assert.Equal(notFound, MigrationService.IsNotFound(thrown));
        Assert.Equal(notFound, MigrationService.IsNotFound(new InvalidOperationException(thrown.Message, thrown)));
    }

    private static Exception Raise(string body)
    {
        using var runspace = RunspaceFactory.CreateRunspace();
        runspace.Open();
        runspace.SessionStateProxy.SetVariable("ByType", NotFoundByType(Address));
        runspace.SessionStateProxy.SetVariable("ByIdentifier", NotFoundByIdentifier(Address));
        runspace.SessionStateProxy.SetVariable("ByCategory", NotFoundByCategory(Address));
        using (var define = PowerShell.Create())
        {
            define.Runspace = runspace;
            define.AddScript($"function Get-Probe {{ [CmdletBinding()] param() {body} }}").Invoke();
        }

        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        ps.AddCommand("Get-Probe").AddParameter("ErrorAction", "Stop");
        return Assert.ThrowsAny<Exception>(() => ps.Invoke());
    }

    // ---- eligibility: the page ------------------------------------------------------------------

    // The page's own check: an undetermined answer says so, names the reason, and offers no batch.
    [Fact]
    public async Task AnUndeterminedAnswer_IsShownAsSuch_AndOffersNoBatch()
    {
        _exchange.Mailboxes[Address] = CloudMailbox(Address);
        Fail("Get-MigrationUser", "error", Refusal);
        var page = _exchange.Page(_exchange.Service());
        Field("singleEmail").SetValue(page, Address);
        Field("singleTicketNumber").SetValue(page, "INC0001");
        Field("singleMigrationDirection").SetValue(page, MigrationDirection.ToOnPrem);

        await Call(page, "CheckSingleEligibility");
        var rendered = page.RenderText();

        Assert.Contains($"Whether {Address} can migrate to On-Premises could not be determined.", rendered, StringComparison.Ordinal);
        Assert.Contains($"Could not be determined: Get-MigrationUser failed: {Refusal}", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("IS NOT eligible", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("IS eligible", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Create Migration Batch", rendered, StringComparison.Ordinal);

        var check = Assert.Single(_exchange.AuditEvents(), e => e.GetProperty("action").GetString() == "CheckMigrationEligibility");
        Assert.Equal("Undetermined", check.GetProperty("status").GetString());
        Assert.Contains("Could not be determined", check.GetProperty("reasons").GetString(), StringComparison.Ordinal);
    }

    // Blocked at the handler too, not only by the hidden card: creation from an undetermined answer does nothing. The
    // control is an Eligible answer, which goes through to the service.
    [Theory]
    [InlineData(MigrationStatus.Undetermined, false)]
    [InlineData(MigrationStatus.Eligible, true)]
    public async Task CreatingABatchFromTheAnswer_GoesThroughOnlyWhenEligible(MigrationStatus status, bool goesThrough)
    {
        var page = _exchange.Page(_exchange.Service());
        Field("singleResult").SetValue(page, new MigrationEligibilityResult { EmailAddress = Address, Status = status });
        Field("singleTicketNumber").SetValue(page, "INC0001");

        await Call(page, "CreateSingleMigrationBatch");

        Assert.Equal(goesThrough, Field("singleCreateResult").GetValue(page) != null);
        Assert.DoesNotContain(_exchange.CallList(), call => call.StartsWith("New-MigrationBatch", StringComparison.Ordinal));
    }

    // A throw before the service returns - here the connection cannot be opened - reaches the page's own catch. It too
    // is "could not be determined", not "IS NOT eligible".
    [Fact]
    public async Task ACheckThatFailsBeforeTheServiceReturns_CouldNotBeDetermined()
    {
        var page = _exchange.Page(_exchange.Service(connects: false));
        Field("singleEmail").SetValue(page, Address);
        Field("singleMigrationDirection").SetValue(page, MigrationDirection.ToOnPrem);

        await Call(page, "CheckSingleEligibility");

        var result = Assert.IsType<MigrationEligibilityResult>(Field("singleResult").GetValue(page));
        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Equal(["Could not be determined: the Exchange Online sign-in could not be completed"], result.IneligibilityReasons);
        Assert.DoesNotContain("IS NOT eligible", page.RenderText(), StringComparison.Ordinal);
    }

    // The bulk check: an undetermined row is counted apart and named, and only the eligible one is offered.
    [Fact]
    public async Task ABulkCheck_CountsAnUndeterminedRowApart()
    {
        _exchange.Mailboxes[Address] = CloudMailbox(Address);
        _exchange.Mailboxes[Other] = CloudMailbox(Other);
        _exchange.Failures[$"Get-MigrationUser {Address}"] = Refused(Refusal);
        var service = _exchange.Service();
        using var csv = new MemoryStream(Encoding.UTF8.GetBytes($"EmailAddress\n{Address}\n{Other}\n"));

        var bulk = await service.CheckBulkMigrationEligibilityAsync(csv, MigrationDirection.ToOnPrem);

        Assert.Equal((2, 1, 0, 1), (bulk.TotalUsers, bulk.EligibleUsers, bulk.IneligibleUsers, bulk.UndeterminedUsers));
        Assert.Equal(MigrationStatus.Undetermined, bulk.EligibilityResults.Single(r => r.EmailAddress == Address).Status);

        var page = _exchange.Page(service);
        Field("tabIndex").SetValue(page, 1);
        Field("migrationDirection").SetValue(page, MigrationDirection.ToOnPrem);
        Field("bulkResult").SetValue(page, bulk);
        var rendered = page.RenderText();

        Assert.Contains("Could not be determined:", rendered, StringComparison.Ordinal);
        Assert.Contains("Undetermined", rendered, StringComparison.Ordinal);
        Assert.Contains($"Could not be determined: Get-MigrationUser failed: {Refusal}", rendered, StringComparison.Ordinal);
        Assert.Contains("1 user(s) eligible to migrate", rendered, StringComparison.Ordinal);
    }

    // ---- approve ---------------------------------------------------------------------------------

    // A single-user batch is named for its mailbox, so the batch steps act on it: every step once, in order, then the
    // batch read back. aud-a23n: what the read-back shows is the Detail, said as what was read.
    [Fact]
    public async Task ACleanApproval_OfAMailboxInItsOwnBatch_RunsEveryStep_AndReadsTheBatchBack()
    {
        _exchange.Batches[Address] = Batch(Address);

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Approved skipped items and initiated completion for {Address}.", result.Message);
        Assert.Equal(
            $"Exchange accepted Complete-MigrationBatch for its migration batch {Address}; read back, the batch reports status Synced with a completion time set.",
            result.Detail);
        Assert.Equal(ApprovalSteps(ownBatch: true), _exchange.CallList());
    }

    // The finding: a Complete-MigrationBatch that fails is the row's failure, named, with what had already been applied,
    // and the page's own action records it as a failed approval. Before, it was swallowed and the approval said
    // "initiated completion".
    [Theory]
    [InlineData("throw")]
    [InlineData("error")]
    public async Task ACompleteMigrationBatchThatFails_IsAFailedAuditedRow_NamingIt(string how)
    {
        const string message = "the batch has mailboxes that failed and cannot be completed";
        _exchange.Batches[Address] = Batch(Address);
        Fail("Complete-MigrationBatch", how, message);
        var service = _exchange.Service();
        var page = _exchange.Page(service);
        Func<Task<PermissionResult>> approve = () => service.ApproveMigrationUserAsync(Address);

        await Call(page, "ExecuteUserAction", approve, "ApproveMigrationUser", Address, "INC0001");

        var result = Assert.IsType<PermissionResult>(Field("batchActionResult").GetValue(page));
        Assert.False(result.Success);
        Assert.Equal(
            $"Complete-MigrationBatch failed for {Address}: {message}. Already applied: Set-MigrationUser -ApproveSkippedItems, "
            + "Set-MigrationUser -CompleteAfter, Set-MigrationBatch -ApproveSkippedItems, Set-MigrationBatch -CompleteAfter, "
            + "Set-MoveRequest -SkippedItemApprovalTime, Resume-MoveRequest.",
            result.Message);
        Assert.Equal(ApprovalSteps(ownBatch: true)[..^1], _exchange.CallList());

        var audited = Assert.Single(_exchange.AuditEvents(), e => e.GetProperty("action").GetString() == "ApproveMigrationUser");
        Assert.Equal("Failed", audited.GetProperty("result").GetString());
        Assert.Equal(Address, audited.GetProperty("target").GetString());
        Assert.StartsWith("Complete-MigrationBatch failed for ", audited.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    // A write that fails stops the approval there: it is named, and nothing after it runs.
    [Fact]
    public async Task ASetMigrationBatchThatFails_IsNamed_AndNothingAfterItRuns()
    {
        _exchange.Batches[Address] = Batch(Address);
        Fail("Set-MigrationBatch -CompleteAfter", "error", "the batch is completed and cannot be changed");

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.False(result.Success);
        Assert.Equal(
            $"Set-MigrationBatch -CompleteAfter failed for {Address}: the batch is completed and cannot be changed. Already applied: "
            + "Set-MigrationUser -ApproveSkippedItems, Set-MigrationUser -CompleteAfter, Set-MigrationBatch -ApproveSkippedItems.",
            result.Message);
        Assert.Equal(ApprovalSteps(ownBatch: true)[..5], _exchange.CallList());
    }

    // A mailbox in a batch of many has no batch named for it: the batch steps do not apply, so none runs - before, each
    // failed unseen under Ignore - and the result says no batch was completed.
    [Fact]
    public async Task AMailboxNotInABatchOfItsOwn_IsApprovedWithoutTouchingABatch_AndSaysSo()
    {
        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Approved skipped items and initiated completion for {Address}.", result.Message);
        Assert.Equal($"{Address} is not in a migration batch of its own, so no batch was completed; only this mailbox was told to complete.", result.Detail);
        Assert.Equal(ApprovalSteps(ownBatch: false), _exchange.CallList());
    }

    // Whether the batch exists is read before any write; a read that fails changes nothing.
    [Theory]
    [InlineData("throw")]
    [InlineData("error")]
    public async Task AnExistenceReadThatFails_ChangesNothing(string how)
    {
        Fail("Get-MigrationBatch", how, Refusal);

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.False(result.Success);
        Assert.Equal($"Could not tell whether {Address} is in a migration batch of its own: {Refusal}. Nothing was changed.", result.Message);
        Assert.Equal([$"Get-MigrationBatch {Address}"], _exchange.CallList());
    }

    // aud-a23n. A read-back that fails after Complete-MigrationBatch was accepted is not a failed approval: the completion
    // was accepted, and reporting it failed would invite a second run of writes already made. The Detail says the state
    // since was not confirmed, and why, in the error's own words.
    [Fact]
    public async Task AReadBackThatFails_IsStillASuccess_SayingItsStateWasNotConfirmed()
    {
        _exchange.Batches[Address] = Batch(Address);
        _exchange.Behaviour["ReadBackRefused"] = Refused("the batch is being updated");

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.True(result.Success, result.Message);
        Assert.Equal($"Approved skipped items and initiated completion for {Address}.", result.Message);
        Assert.Equal(
            $"Exchange accepted Complete-MigrationBatch for its migration batch {Address}; reading the batch back failed (the batch is being updated), so its state since was not confirmed.",
            result.Detail);
        Assert.Equal(ApprovalSteps(ownBatch: true), _exchange.CallList());
    }

    // aud-a23n. Every write reported success and the batch reads back with no completion time: the schedule is not
    // confirmed, and the Detail says so - on a success, never as a failure.
    [Fact]
    public async Task AWriteThatChangesNothing_IsStillASuccess_SayingTheScheduleWasNotConfirmed()
    {
        _exchange.Batches[Address] = Batch(Address);
        _exchange.Behaviour["SetBatchChangesNothing"] = true;

        var result = await _exchange.Service().ApproveMigrationUserAsync(Address);

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            $"Exchange accepted Complete-MigrationBatch for its migration batch {Address}; read back, the batch reports status Synced and no completion time, so the read-back did not confirm the schedule.",
            result.Detail);
        Assert.Equal(ApprovalSteps(ownBatch: true), _exchange.CallList());
    }
}
