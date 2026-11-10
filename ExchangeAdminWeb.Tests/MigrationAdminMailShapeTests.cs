using System.Collections;
using System.Text;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Components.Forms;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The administrators' mail for each Migration action carries rows named for what they hold - a mailbox, a batch,
/// a CSV file, a count - and none of the mailbox-permission rows. Each test runs the page's own handler over
/// <see cref="MigrationStubExchange"/> and reads the notification the page asked the mail service for.
/// </summary>
public sealed class MigrationAdminMailShapeTests : IDisposable
{
    private const string Address = "first.mailbox@contoso.test";
    private const string Ticket = "INC0001";
    private const string BatchName = "Batch-A";

    private readonly MigrationStubExchange _exchange = new();
    private readonly RecordingEmail _email;

    public MigrationAdminMailShapeTests()
    {
        _email = _exchange.Email();
        _exchange.Protection = new Hashtable { ["*"] = "clear" };
        _exchange.Mailboxes[Address] = CloudMailbox(Address);
        _exchange.Batches[BatchName] = Batch(BatchName);
    }

    public void Dispose() => _exchange.Dispose();

    [Fact]
    public async Task TheSingleCheck_NamesTheMailboxAndItsEligibility()
    {
        var page = Page();
        Field("singleEmail").SetValue(page, Address);
        Field("singleTicketNumber").SetValue(page, Ticket);
        Field("singleMigrationDirection").SetValue(page, MigrationDirection.ToOnPrem);

        await Call(page, "CheckSingleEligibility");

        var verdict = Assert.IsType<MigrationEligibilityResult>(Field("singleResult").GetValue(page));
        var mail = Assert.Single(_email.Mails);
        Assert.Equal("CheckMigrationEligibility_ToOnPrem", mail.Action);
        Assert.Equal(verdict.Status == MigrationStatus.Eligible, mail.Success);
        Assert.Equal(Ticket, mail.Ticket);
        Assert.Equal([$"Mailbox={Address}", $"Eligibility={verdict.Status}"], mail.Rows);
    }

    [Fact]
    public async Task TheSingleCreate_NamesTheBatchTheMailboxAndTheOptions()
    {
        var page = Page();
        Field("singleResult").SetValue(page, new MigrationEligibilityResult { EmailAddress = Address, Status = MigrationStatus.Eligible });
        Field("singleTicketNumber").SetValue(page, Ticket);
        Field("singleMigrationDirection").SetValue(page, MigrationDirection.ToCloud);
        Field("singleBatchName").SetValue(page, BatchName);
        Field("singleAutoStart").SetValue(page, false);
        Field("singleAutoComplete").SetValue(page, true);

        await Call(page, "CreateSingleMigrationBatch");

        var mail = Assert.Single(_email.Mails);
        Assert.Equal(("CreateMigrationBatch_ToCloud", true, Ticket, (string?)null), (mail.Action, mail.Success, mail.Ticket, mail.Error));
        Assert.Equal([$"Batch={BatchName}", $"Mailbox={Address}", "Options=AutoStart:False AutoComplete:True"], mail.Rows);
    }

    [Fact]
    public async Task TheBulkCheck_NamesTheCsvFileAndTheCount()
    {
        var page = Page();
        Field("csvFile").SetValue(page, Csv("wave-a.csv", Address));
        Field("bulkTicketNumber").SetValue(page, Ticket);
        Field("migrationDirection").SetValue(page, MigrationDirection.ToOnPrem);

        await Call(page, "CheckBulkEligibility");

        var checkedRows = Assert.IsType<MigrationBatchResult>(Field("bulkResult").GetValue(page));
        var mail = Assert.Single(_email.Mails);
        Assert.Equal(("BulkMigrationEligibilityCheck_ToOnPrem", true, Ticket, (string?)null), (mail.Action, mail.Success, mail.Ticket, mail.Error));
        Assert.Equal(["CSV File=wave-a.csv", $"Eligible={checkedRows.EligibleUsers}/{checkedRows.TotalUsers} eligible"], mail.Rows);
    }

    [Fact]
    public async Task TheBulkCreate_NamesTheBatchTheMailboxCountAndTheOptions()
    {
        var page = Page();
        Field("bulkResult").SetValue(page, new MigrationBatchResult
        {
            BatchName = "wave",
            Success = true,
            TotalUsers = 1,
            EligibleUsers = 1,
            EligibilityResults = [new MigrationEligibilityResult { EmailAddress = Address, Status = MigrationStatus.Eligible }],
        });
        Field("bulkTicketNumber").SetValue(page, Ticket);
        Field("migrationDirection").SetValue(page, MigrationDirection.ToCloud);
        Field("bulkBatchName").SetValue(page, BatchName);
        Field("bulkAutoStart").SetValue(page, false);
        Field("bulkAutoComplete").SetValue(page, true);

        await Call(page, "CreateMigrationBatch");

        var mail = Assert.Single(_email.Mails);
        Assert.Equal(("CreateMigrationBatch_ToCloud", true, Ticket, (string?)null), (mail.Action, mail.Success, mail.Ticket, mail.Error));
        Assert.Equal([$"Batch={BatchName}", "Mailboxes=1 users", "Options=AutoStart:False AutoComplete:True"], mail.Rows);
    }

    [Fact]
    public async Task ABatchAction_NamesTheBatch_AndCarriesItsFailure()
    {
        var page = Page();
        Func<Task<PermissionResult>> stop = () => Task.FromResult(PermissionResult.Fail("Exchange refused it"));

        await Call(page, "ExecuteBatchAction", stop, "StopMigrationBatch", BatchName, Ticket);

        var mail = Assert.Single(_email.Mails);
        Assert.Equal(("Migration_StopMigrationBatch", false, Ticket, "Exchange refused it"), (mail.Action, mail.Success, mail.Ticket, mail.Error));
        Assert.Equal([$"Batch={BatchName}"], mail.Rows);
    }

    [Fact]
    public async Task AMailboxAction_NamesTheMailbox()
    {
        var page = Page();
        Func<Task<PermissionResult>> stop = () => Task.FromResult(PermissionResult.Ok());

        await Call(page, "ExecuteUserAction", stop, "StopMigrationUser", Address, Ticket);

        var mail = Assert.Single(_email.Mails);
        Assert.Equal(("Migration_StopMigrationUser", true, Ticket, (string?)null), (mail.Action, mail.Success, mail.Ticket, mail.Error));
        Assert.Equal([$"Mailbox={Address}"], mail.Rows);
    }

    [Fact]
    public async Task ABulkBatchAction_SaysHowManyBatchesSucceeded()
    {
        var page = Page();
        Field("migrationBatches").SetValue(page, new List<MigrationBatchInfo>());
        Func<string, Task<PermissionResult>> stop = _ => Task.FromResult(PermissionResult.Ok());

        await Call(page, "ExecuteBulkBatchAction", (IReadOnlyList<string>)[BatchName], (IReadOnlyList<MigrationBatchSkip>)[], stop,
            "StopMigrationBatch", "Migration_StopSelected", "Queued stop of", Ticket);

        var mail = Assert.Single(_email.Mails);
        Assert.Equal(("Migration_StopSelected", true, Ticket, (string?)null), (mail.Action, mail.Success, mail.Ticket, mail.Error));
        Assert.Equal(["Succeeded=1 batch(es)"], mail.Rows);
    }

    [Fact]
    public async Task ABulkMailboxAction_SaysHowManyMailboxesSucceeded()
    {
        var page = Page();
        var loaded = new List<MigrationUserInfo> { new() { EmailAddress = Address, Status = "Syncing" } };
        Field("batchUsers").SetValue(page, loaded);
        var plan = MigrationUserActionPlanner.Plan(loaded, [Address], MigrationUserAction.Pause);
        Assert.Equal([Address], plan.Eligible);
        Func<string, Task<PermissionResult>> pause = _ => Task.FromResult(PermissionResult.Ok());

        await Call(page, "ExecuteBulkMailboxAction", plan, pause, "StopMigrationUser", "Migration_PauseMailboxes", "Queued pause of", Ticket);

        var mail = Assert.Single(_email.Mails);
        Assert.Equal(("Migration_PauseMailboxes", true, Ticket, (string?)null), (mail.Action, mail.Success, mail.Ticket, mail.Error));
        Assert.Equal(["Succeeded=1 mailbox(es)"], mail.Rows);
    }

    private RenderableMigration Page()
    {
        var page = _exchange.Page(_exchange.Service());
        Set(page, "Email", _email);
        Set(page, "Progress", new RecordingProgress());
        return page;
    }

    private static IBrowserFile Csv(string name, params string[] addresses)
    {
        var file = Substitute.For<IBrowserFile>();
        file.Name.Returns(name);
        file.OpenReadStream(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes("EmailAddress\n" + string.Join("\n", addresses) + "\n")));
        return file;
    }
}
