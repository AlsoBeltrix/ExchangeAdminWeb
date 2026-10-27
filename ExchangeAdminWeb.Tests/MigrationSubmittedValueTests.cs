using System.Collections;
using System.Text;
using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Components.Forms;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using Harness = ExchangeAdminWeb.Tests.SubmittedValueHarness;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-72 on Mailbox Migrations: the single and bulk eligibility checks read the address, the CSV, the direction and the
/// ticket after their yield, and the two creates read the direction, the ticket, the batch name and the auto-start and
/// auto-complete choices after the authorization re-check and their yield - into the check or the batch, its audit rows
/// and the notice. The direction select and the ticket box are not disabled while a create runs. Each handler now reads
/// what it acts on and records above its first await, and a second click while the page is busy is ignored.
/// </summary>
/// <remarks>
/// The compiled page over <see cref="MigrationStubExchange"/>'s real service and stub cmdlets - no Exchange, no
/// directory - with a recording mail service. <see cref="SubmittedValueHarness.HeldContinuations"/> holds each handler at
/// its yield while the test changes the form.
/// </remarks>
public sealed class MigrationSubmittedValueTests : IDisposable
{
    private const string AddressA = "first.mailbox@contoso.test";
    private const string AddressB = "second.mailbox@contoso.test";
    private const string Ticket = "INC0001";

    private readonly MigrationStubExchange _exchange = new();
    private readonly RecordingEmail _email;

    public MigrationSubmittedValueTests()
    {
        _email = _exchange.Email();
        // Nobody is protected, so a create goes through to the stub's New-MigrationBatch (unset, the check fails closed).
        _exchange.Protection = new Hashtable { ["*"] = "clear" };
        _exchange.Mailboxes[AddressA] = CloudMailbox(AddressA);
        _exchange.Mailboxes[AddressB] = CloudMailbox(AddressB);
    }

    public void Dispose() => _exchange.Dispose();

    [Fact]
    public void AChangeLandingInTheSingleChecksYield_LeavesTheCheckAndItsRecordsOnWhatWasSubmitted()
    {
        var page = PageFor();
        Field("singleEmail").SetValue(page, AddressA);
        Field("singleTicketNumber").SetValue(page, Ticket);
        Field("singleMigrationDirection").SetValue(page, MigrationDirection.ToOnPrem);

        Harness.RunWithChangeWhileSuspended(() => Call(page, "CheckSingleEligibility"), () =>
        {
            Field("singleEmail").SetValue(page, AddressB);
            Field("singleTicketNumber").SetValue(page, "INC9999");
            Field("singleMigrationDirection").SetValue(page, MigrationDirection.ToCloud);
        });

        Assert.Equal(AddressA, Assert.IsType<MigrationEligibilityResult>(Field("singleResult").GetValue(page)).EmailAddress);
        var row = Assert.Single(_exchange.AuditEvents(), e => Text(e, "action") == "CheckMigrationEligibility");
        Assert.Equal(AddressA, Text(row, "target"));
        Assert.Equal(Ticket, Text(row, "ticket"));
        Assert.Equal(["CheckMigrationEligibility_ToOnPrem"], _email.Sent);
    }

    [Fact]
    public void AChangeLandingInTheSingleCreatesYield_CreatesAndRecordsTheBatchSubmitted()
    {
        var page = PageFor();
        Field("singleResult").SetValue(page, new MigrationEligibilityResult { EmailAddress = AddressA, Status = MigrationStatus.Eligible });
        Field("singleTicketNumber").SetValue(page, Ticket);
        Field("singleMigrationDirection").SetValue(page, MigrationDirection.ToCloud);
        Field("singleBatchName").SetValue(page, "Batch-A");
        // The auto-complete the batch is submitted with re-reads it from the stub's batches once it is created.
        _exchange.Batches["Batch-A"] = Batch("Batch-A");
        Field("singleAutoStart").SetValue(page, false);
        Field("singleAutoComplete").SetValue(page, true);

        Harness.RunWithChangeWhileSuspended(() => Call(page, "CreateSingleMigrationBatch"), () =>
        {
            Field("singleTicketNumber").SetValue(page, "INC9999");
            Field("singleMigrationDirection").SetValue(page, MigrationDirection.ToOnPrem);
            Field("singleBatchName").SetValue(page, "Batch-B");
            Field("singleAutoStart").SetValue(page, true);
            Field("singleAutoComplete").SetValue(page, false);
        });

        AssertTheBatchRecorded("Batch-A", autoStart: false, autoComplete: true);
    }

    [Fact]
    public void AChangeLandingInTheBulkChecksYield_LeavesTheCheckAndItsRecordsOnWhatWasSubmitted()
    {
        var page = PageFor();
        Field("csvFile").SetValue(page, Csv("wave-a.csv", AddressA));
        Field("bulkTicketNumber").SetValue(page, Ticket);
        Field("migrationDirection").SetValue(page, MigrationDirection.ToOnPrem);

        Harness.RunWithChangeWhileSuspended(() => Call(page, "CheckBulkEligibility"), () =>
        {
            Field("csvFile").SetValue(page, Csv("wave-b.csv", AddressB));
            Field("bulkTicketNumber").SetValue(page, "INC9999");
            Field("migrationDirection").SetValue(page, MigrationDirection.ToCloud);
        });

        var row = Assert.Single(_exchange.AuditEvents(), e => Text(e, "action")?.StartsWith("BulkEligibilityCheck_", StringComparison.Ordinal) == true);
        Assert.Equal("BulkEligibilityCheck_ToOnPrem", Text(row, "action"));
        Assert.Equal("wave-a.csv", Text(row, "target"));
        Assert.Equal(Ticket, Text(row, "ticket"));
        var checkedRow = Assert.Single(Assert.IsType<MigrationBatchResult>(Field("bulkResult").GetValue(page)).EligibilityResults);
        Assert.Equal(AddressA, checkedRow.EmailAddress);
        Assert.Equal(["BulkMigrationEligibilityCheck_ToOnPrem"], _email.Sent);
    }

    [Fact]
    public void AChangeLandingInTheBulkCreatesYield_CreatesAndRecordsTheBatchSubmitted()
    {
        var page = PageFor();
        Field("bulkResult").SetValue(page, new MigrationBatchResult
        {
            BatchName = "wave",
            Success = true,
            TotalUsers = 1,
            EligibleUsers = 1,
            EligibilityResults = [new MigrationEligibilityResult { EmailAddress = AddressA, Status = MigrationStatus.Eligible }],
        });
        Field("bulkTicketNumber").SetValue(page, Ticket);
        Field("migrationDirection").SetValue(page, MigrationDirection.ToCloud);
        Field("bulkBatchName").SetValue(page, "Batch-A");
        // The auto-complete the batch is submitted with re-reads it from the stub's batches once it is created.
        _exchange.Batches["Batch-A"] = Batch("Batch-A");
        Field("bulkAutoStart").SetValue(page, false);
        Field("bulkAutoComplete").SetValue(page, true);

        Harness.RunWithChangeWhileSuspended(() => Call(page, "CreateMigrationBatch"), () =>
        {
            Field("bulkTicketNumber").SetValue(page, "INC9999");
            Field("migrationDirection").SetValue(page, MigrationDirection.ToOnPrem);
            Field("bulkBatchName").SetValue(page, "Batch-B");
            Field("bulkAutoStart").SetValue(page, true);
            Field("bulkAutoComplete").SetValue(page, false);
        });

        AssertTheBatchRecorded("Batch-A", autoStart: false, autoComplete: true);
    }

    [Theory]
    [InlineData("CheckSingleEligibility")]
    [InlineData("CreateSingleMigrationBatch")]
    [InlineData("CheckBulkEligibility")]
    [InlineData("CreateMigrationBatch")]
    public void ASecondClickWhileTheHandlerRuns_IsIgnored_NotRunBesideIt(string handler)
    {
        var page = PageFor();
        Field("singleEmail").SetValue(page, AddressA);
        Field("singleTicketNumber").SetValue(page, Ticket);
        Field("singleResult").SetValue(page, new MigrationEligibilityResult { EmailAddress = AddressA, Status = MigrationStatus.Eligible });
        Field("csvFile").SetValue(page, Csv("wave-a.csv", AddressA));
        Field("bulkTicketNumber").SetValue(page, Ticket);
        Field("bulkResult").SetValue(page, new MigrationBatchResult
        {
            BatchName = "wave",
            Success = true,
            TotalUsers = 1,
            EligibleUsers = 1,
            EligibilityResults = [new MigrationEligibilityResult { EmailAddress = AddressA, Status = MigrationStatus.Eligible }],
        });

        Harness.RunWithChangeWhileSuspended(() => Call(page, handler), () =>
        {
            var second = Call(page, handler);
            Assert.True(second.IsCompleted, $"a second click during {handler} started a second run");
        });

        Assert.Single(_email.Sent);
    }

    private RenderableMigration PageFor()
    {
        var page = _exchange.Page(_exchange.Service());
        Set(page, "Email", _email);
        Set(page, "Progress", new RecordingProgress());
        return page;
    }

    private void AssertTheBatchRecorded(string batchName, bool autoStart, bool autoComplete)
    {
        var row = Assert.Single(_exchange.AuditEvents(), e => Text(e, "category") == "MigrationBatch");
        Assert.Equal("CreateMigrationBatch_ToCloud", Text(row, "action"));
        Assert.True(Text(row, "result") == "Success", $"the create did not succeed: {row}");
        Assert.Equal(Ticket, Text(row, "ticket"));
        Assert.Equal(batchName, Text(row, "batchName"));
        Assert.Equal(autoStart, row.GetProperty("autoStart").GetBoolean());
        Assert.Equal(autoComplete, row.GetProperty("autoComplete").GetBoolean());
        Assert.Equal(["CreateMigrationBatch_ToCloud"], _email.Sent);
    }

    private static IBrowserFile Csv(string name, params string[] addresses)
    {
        var file = Substitute.For<IBrowserFile>();
        file.Name.Returns(name);
        file.OpenReadStream(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes("EmailAddress\n" + string.Join("\n", addresses) + "\n")));
        return file;
    }

    private static string? Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
