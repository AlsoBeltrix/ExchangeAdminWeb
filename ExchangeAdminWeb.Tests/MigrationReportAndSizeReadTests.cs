using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a23k. (1) <c>GetMigrationUserReportAsync</c> turned a failed fetch into report text ("Error
/// retrieving report: ..."): the page stored it, showed it as the report and completed its activity as succeeded, and the
/// bulk export stored it as the mailbox's report on a row marked Succeeded. (2) For a move back on-premises the cloud
/// mailbox size read gave 0 for a size it could not read - the main read failing, no row, a size that did not parse, or any
/// failure of the archive read - so the page showed an empty mailbox, or no size at all. aud-a55: an archive read that
/// answered with no row and no error still read as no archive.
/// </summary>
/// <remarks>
/// The real service over <see cref="MigrationStubExchange"/>, the page's own handler and compiled render tree, and the real
/// export processor over a real report store in a temporary folder. No Exchange.
/// </remarks>
public sealed class MigrationReportAndSizeReadTests : IDisposable
{
    private const string Address = "alice@contoso.test";
    private const string BatchName = "Wave 12";
    private const string Refusal = "the statistics service is unavailable";

    private readonly MigrationStubExchange _exchange = new();
    private readonly RecordingProgress _progress = new();

    public void Dispose() => _exchange.Dispose();

    private RenderableMigration Page()
    {
        var page = _exchange.Page(_exchange.Service());
        Set(page, "Progress", _progress);
        return page;
    }

    private void Fail(string step, string how) =>
        _exchange.Failures[step] = how == "throw" ? Refusal : Refused(Refusal);

    // ---- part 1: a failed report fetch is a failure, not a report ---------------------------------

    // The finding. Before: Text "Error retrieving report: ..." and no failure at all.
    [Theory]
    [InlineData("throw")]
    [InlineData("error")]
    public async Task AFetchThatFails_IsAFailedOutcome_NotReportText(string how)
    {
        Fail("Get-MigrationUserStatistics", how);

        var report = await _exchange.Service().GetMigrationUserReportAsync(Address);

        Assert.Null(report.Text);
        Assert.Contains(Refusal, report.Failure, StringComparison.Ordinal);
    }

    // Before, no statistics row was a null report, which the page stored and then showed nothing for.
    [Fact]
    public async Task NoStatistics_IsAFailedOutcome_NotAnEmptyReport()
    {
        _exchange.Behaviour["NoStatistics"] = true;

        var report = await _exchange.Service().GetMigrationUserReportAsync(Address);

        Assert.Null(report.Text);
        Assert.Equal($"Exchange returned no migration statistics for {Address}", report.Failure);
    }

    [Fact]
    public async Task AReport_IsItsText()
    {
        var report = await _exchange.Service().GetMigrationUserReportAsync(Address);

        Assert.Null(report.Failure);
        Assert.Contains("--- Move Report ---", report.Text, StringComparison.Ordinal);
        Assert.Contains("the move report", report.Text, StringComparison.Ordinal);
    }

    // Through the page: the failure is shown as a failure, outside the report body (which holds nothing), is not stored,
    // and the activity says the fetch failed.
    [Fact]
    public async Task AFailedFetch_IsShownAsAFailure_NotStored_AndItsActivityFails()
    {
        Fail("Get-MigrationUserStatistics", "error");
        var page = Page();
        Field("expandedBatch").SetValue(page, BatchName);

        await Call(page, "FetchUserReport", Address);

        Assert.Null(Field("userReport").GetValue(page));
        Assert.Contains(Refusal, Assert.IsType<string>(Field("reportFailure").GetValue(page)), StringComparison.Ordinal);
        Assert.Null(Field("reportFetchedAtUtc").GetValue(page));
        Assert.Null(new MigrationReportStore(_exchange.Environment).TryRead(BatchName, Address, DateTime.UtcNow));

        var rendered = page.RenderText();
        Assert.Contains("The report could not be retrieved: ", rendered, StringComparison.Ordinal);
        Assert.Contains(Refusal, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Error retrieving report", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Taken ", rendered, StringComparison.Ordinal);

        var (success, message) = Assert.Single(_progress.Handles).Outcome!.Value;
        Assert.False(success);
        Assert.StartsWith($"The report for {Address} could not be fetched: ", message, StringComparison.Ordinal);
        Assert.Contains(Refusal, message, StringComparison.Ordinal);
    }

    // A report fetched after a failure replaces it: the failure is gone, the report is shown and stored.
    [Fact]
    public async Task AReportFetchedAfterAFailure_ClearsIt()
    {
        Fail("Get-MigrationUserStatistics", "error");
        var page = Page();
        Field("expandedBatch").SetValue(page, BatchName);
        await Call(page, "FetchUserReport", Address);
        _exchange.Failures.Clear();

        await Call(page, "FetchUserReport", Address);

        Assert.Null(Field("reportFailure").GetValue(page));
        Assert.Contains("the move report", Assert.IsType<string>(Field("userReport").GetValue(page)), StringComparison.Ordinal);
        Assert.NotNull(new MigrationReportStore(_exchange.Environment).TryRead(BatchName, Address, DateTime.UtcNow));
        Assert.DoesNotContain("The report could not be retrieved", page.RenderText(), StringComparison.Ordinal);
    }

    // The bulk export, the fetch's other caller: a failed fetch is a Failed row naming why, and nothing is stored for the
    // zip to carry. Before, the error text was stored as the report and the row Succeeded.
    [Fact]
    public async Task TheExportJob_RecordsAFailedFetchAsAFailedRow_AndStoresNothing()
    {
        Fail("Get-MigrationUserStatistics", "error");
        var store = new MigrationReportStore(_exchange.Environment);

        var row = await Export(store);

        Assert.Equal(BulkJobRowStatus.Failed, row.Status);
        Assert.StartsWith("Report could not be fetched: ", row.Message, StringComparison.Ordinal);
        Assert.Contains(Refusal, row.Message, StringComparison.Ordinal);
        Assert.Null(store.TryRead(BatchName, Address, DateTime.UtcNow));
    }

    [Fact]
    public async Task TheExportJob_StoresAFetchedReport()
    {
        var store = new MigrationReportStore(_exchange.Environment);

        var row = await Export(store);

        Assert.Equal(BulkJobRowStatus.Success, row.Status);
        Assert.Contains("the move report", store.TryRead(BatchName, Address, DateTime.UtcNow)?.Text, StringComparison.Ordinal);
    }

    private Task<BulkJobRowOutcome> Export(MigrationReportStore store)
    {
        var processor = new MigrationReportExportProcessor(
            _exchange.Service(), store, _exchange.Audit, NullLogger<MigrationReportExportProcessor>.Instance);
        var job = new BulkJob
        {
            Id = "job-a23k",
            ModuleId = MigrationReportExportJobPayload.ModuleId,
            JobType = MigrationReportExportJobPayload.JobType,
            SubmittedBy = "CONTOSO\\operator",
            SubmittedIp = "127.0.0.1",
            SubmittedAtUtc = DateTime.UtcNow,
            PayloadJson = JsonSerializer.Serialize(new MigrationReportExportJobPayload { BatchName = BatchName, EmailAddresses = [Address] }),
        };
        return processor.ProcessRowAsync(job, 0, CancellationToken.None);
    }

    // ---- part 2: a cloud size that cannot be read is marked, never 0 --------------------------------

    private Task<MigrationEligibilityResult> CheckToOnPrem()
    {
        _exchange.Mailboxes[Address] = CloudMailbox(Address);
        return _exchange.Service().CheckMigrationEligibilityAsync(Address, MigrationDirection.ToOnPrem);
    }

    // The finding. Before, each of these read as a 0 GB mailbox; the verdict is unchanged, since no move back on-premises
    // is decided by size.
    [Theory]
    [InlineData("the statistics read throws", "Get-MailboxStatistics failed: " + Refusal)]
    [InlineData("the statistics read writes an error", "Get-MailboxStatistics failed: " + Refusal)]
    [InlineData("no statistics row", "Exchange returned no mailbox statistics")]
    [InlineData("a size that does not parse", "the reported TotalItemSize 'Unlimited' is not a size this app can read")]
    public async Task AMailboxSizeThatCannotBeRead_IsMarked_NeverZero(string how, string reason)
    {
        switch (how)
        {
            case "the statistics read throws": Fail("Get-MailboxStatistics", "throw"); break;
            case "the statistics read writes an error": Fail("Get-MailboxStatistics", "error"); break;
            case "no statistics row": _exchange.Behaviour["MailboxSize"] = null; break;
            default: _exchange.Behaviour["MailboxSize"] = "Unlimited"; break;
        }

        var result = await CheckToOnPrem();

        Assert.Equal(MigrationStatus.Eligible, result.Status);
        Assert.Equal(reason, result.MailboxSizeUnreadable);
    }

    [Theory]
    [InlineData("the archive read writes an error", "Get-MailboxStatistics -Archive failed: " + Refusal)]
    [InlineData("an archive size that does not parse", "the reported TotalItemSize 'Unlimited' is not a size this app can read")]
    public async Task AnArchiveSizeThatCannotBeRead_IsMarked_NotNoArchive(string how, string reason)
    {
        if (how == "the archive read writes an error")
            Fail("Get-MailboxStatistics -Archive", "error");
        else
            _exchange.Behaviour["ArchiveSize"] = "Unlimited";

        var result = await CheckToOnPrem();

        Assert.Null(result.MailboxSizeUnreadable);
        Assert.Equal(1.5, result.MailboxSizeGB);
        Assert.Equal(reason, result.ArchiveSizeUnreadable);
    }

    // Exchange's own "no such object" for the archive is no archive, as before: no marker, nothing to show.
    [Fact]
    public async Task NoArchive_IsNoArchive()
    {
        _exchange.Behaviour["NoArchive"] = true;

        var result = await CheckToOnPrem();

        Assert.Null(result.ArchiveSizeUnreadable);
        Assert.Equal(0, result.ArchiveSizeGB);
        Assert.Equal(1.5, result.MailboxSizeGB);
    }

    // aud-a55. In each shape Exchange's not-found is read from - its structure, never its text.
    [Theory]
    [InlineData("category")]
    [InlineData("type")]
    [InlineData("identifier")]
    public async Task NoArchive_IsNoArchive_ByEachNotFoundShape(string shape)
    {
        _exchange.NotFound = shape switch { "category" => NotFoundByCategory, "type" => NotFoundByType, _ => NotFoundByIdentifier };
        _exchange.Behaviour["NoArchive"] = true;

        var result = await CheckToOnPrem();

        Assert.Null(result.ArchiveSizeUnreadable);
        Assert.Equal(0, result.ArchiveSizeGB);
    }

    // aud-a55, the finding's third part. An archive read that answers with no row and no error is not Exchange saying
    // there is no archive, so it is not no archive: it is the marker, and the page says it could not be read. Before, the
    // read's not-found and an empty answer came back alike, and both were no archive.
    [Fact]
    public async Task AnArchiveReadWithNoRowAndNoError_IsMarked_NotNoArchive()
    {
        _exchange.Behaviour["ArchiveSize"] = null;

        var result = await CheckToOnPrem();

        Assert.Equal("Exchange returned no archive statistics", result.ArchiveSizeUnreadable);
        Assert.Null(result.MailboxSizeUnreadable);
        Assert.Equal(1.5, result.MailboxSizeGB);

        var page = Page();
        Field("singleResult").SetValue(page, result);
        Assert.Contains("could not be read (Exchange returned no archive statistics)", page.RenderText(), StringComparison.Ordinal);
    }

    // A size read that throws as a whole - here a cancellation, which neither read's own catch takes (the aud-a23g rule)
    // - marks both sizes, where the catch dropped it silently and the page showed no size.
    [Fact]
    public async Task ASizeReadThatFailsAsAWhole_MarksBothSizes()
    {
        _exchange.Behaviour["StatisticsCancelled"] = true;

        var result = await CheckToOnPrem();

        Assert.Equal(MigrationStatus.Eligible, result.Status);
        Assert.StartsWith("the size read failed: ", result.MailboxSizeUnreadable, StringComparison.Ordinal);
        Assert.Equal(result.MailboxSizeUnreadable, result.ArchiveSizeUnreadable);
    }

    // The page: a marked size reads "could not be read (<reason>)", and no total is given beside it.
    [Fact]
    public async Task APageWithAMarkedSize_SaysItCouldNotBeRead_AndGivesNoTotal()
    {
        _exchange.Behaviour["MailboxSize"] = "Unlimited";
        var page = Page();
        Field("singleEmail").SetValue(page, Address);
        Field("singleMigrationDirection").SetValue(page, MigrationDirection.ToOnPrem);
        _exchange.Mailboxes[Address] = CloudMailbox(Address);

        await Call(page, "CheckSingleEligibility");
        var rendered = page.RenderText();

        Assert.Contains("could not be read (the reported TotalItemSize 'Unlimited' is not a size this app can read)", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain($"{0.0:F2} GB", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Total:", rendered, StringComparison.Ordinal);
    }

    // The control: readable sizes and their total, as before.
    [Fact]
    public async Task APageWithReadableSizes_ShowsThemAndTheirTotal()
    {
        var page = Page();
        Field("singleEmail").SetValue(page, Address);
        Field("singleMigrationDirection").SetValue(page, MigrationDirection.ToOnPrem);
        _exchange.Mailboxes[Address] = CloudMailbox(Address);

        await Call(page, "CheckSingleEligibility");
        var rendered = page.RenderText();

        Assert.Contains($"{1.5:F2} GB", rendered, StringComparison.Ordinal);
        Assert.Contains("Total:", rendered, StringComparison.Ordinal);
        Assert.Contains($"{3.0:F2} GB", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be read", rendered, StringComparison.Ordinal);
    }

    // The bulk table: a marked size is said in its cell too.
    [Fact]
    public void TheBulkTable_SaysAMarkedSizeCouldNotBeRead()
    {
        var page = Page();
        Field("tabIndex").SetValue(page, 1);
        Field("migrationDirection").SetValue(page, MigrationDirection.ToOnPrem);
        Field("bulkResult").SetValue(page, new MigrationBatchResult
        {
            BatchName = BatchName,
            Success = true,
            Direction = MigrationDirection.ToOnPrem,
            TotalUsers = 1,
            EligibleUsers = 1,
            EligibilityResults =
            [
                new MigrationEligibilityResult
                {
                    EmailAddress = Address,
                    Status = MigrationStatus.Eligible,
                    MailboxSizeUnreadable = "Get-MailboxStatistics failed: " + Refusal,
                    ArchiveSizeUnreadable = "Get-MailboxStatistics failed: " + Refusal,
                },
            ],
        });

        var rendered = page.RenderText();

        Assert.Contains($"could not be read (Get-MailboxStatistics failed: {Refusal})", rendered, StringComparison.Ordinal);
        Assert.Contains($"archive could not be read (Get-MailboxStatistics failed: {Refusal})", rendered, StringComparison.Ordinal);
    }
}
