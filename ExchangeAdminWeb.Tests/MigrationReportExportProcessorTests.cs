using System.Text.Json;
using ExchangeAdminWeb.Services.Jobs;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Source-level guards for the bulk report export's per-row work (R31).
/// </summary>
/// <remarks>
/// THESE ARE TRIPWIRES FOR THE RULES, NOT A HARNESS FOR THE PROCESSOR. Running a row needs a live
/// MigrationService and a real Exchange session, which this repo has no substitute for at the
/// MigrationService level - the seam other processors use (IMessageTraceDetailSource) does not
/// exist here. What these prove is that the decisions R31 turns on are still in the code: reuse
/// before fetch, a failed row that does not abort the batch, and an audit failure that cannot
/// turn a fetched report into a reported failure.
///
/// The payload round-trip below is real behaviour, not a tripwire: a queued job is only an
/// inspectable record if it survives serialization.
/// </remarks>
public class MigrationReportExportProcessorTests
{
    private static string ReadProcessor() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "Services", "Jobs", "MigrationReportExportProcessor.cs"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Services", "Jobs")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Services/Jobs from the test base directory.");
    }

    [Fact]
    public void ThePayloadSurvivesBeingQueued()
    {
        // A queued job is a real, inspectable record only if the payload round-trips. The BATCH
        // NAME is in it deliberately: a report is identified by batch plus mailbox, and a batch
        // removed and recreated under the same name is a different migration - so the job records
        // which batch it was submitted against rather than resolving "the open batch" on wake.
        var payload = new MigrationReportExportJobPayload
        {
            BatchName = "Wave1-Finance",
            EmailAddresses = ["a@x.com", "b@x.com"],
        };

        var back = JsonSerializer.Deserialize<MigrationReportExportJobPayload>(
            JsonSerializer.Serialize(payload));

        Assert.NotNull(back);
        Assert.Equal("Wave1-Finance", back!.BatchName);
        Assert.Equal(["a@x.com", "b@x.com"], back.EmailAddresses);
    }

    [Fact]
    public void AHeldReportIsReusedBeforeAnythingIsFetched()
    {
        // R31c, and it is the difference between an export costing twenty minutes per mailbox and
        // one costing twenty minutes per mailbox NOT already read. On a batch the operator has
        // been working through, most are already held.
        var source = ReadProcessor();

        var read = source.IndexOf("_reports.TryRead(", StringComparison.Ordinal);
        var fetch = source.IndexOf("GetMigrationUserReportAsync", StringComparison.Ordinal);

        Assert.True(read > 0, "the processor must consult the store");
        Assert.True(fetch > read, "the store read must come before the fetch, not after it");

        // And the reuse path returns rather than falling through into the fetch.
        Assert.Contains("Status = BulkJobRowStatus.Success", source[read..fetch], StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedMailboxIsOneFailedRowAndNotABlanketFailure()
    {
        // R31b and Known Failure Class 2. Three of six succeeding means three reports are
        // available and three are named; it must never read as "the export failed".
        var source = ReadProcessor();

        Assert.Contains("Status = BulkJobRowStatus.Failed", source, StringComparison.Ordinal);

        // Returned, not thrown. The runner would record a throw the same way, but returning is
        // what makes the message the operator reads ours rather than an exception string.
        var catchAt = source.IndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(catchAt > 0);
        Assert.DoesNotContain("throw;", source[catchAt..], StringComparison.Ordinal);
    }

    [Fact]
    public void AnAuditFailureDoesNotTurnAFetchedReportIntoAFailedRow()
    {
        // The report was fetched and stored. Reporting the row as Failed because the audit write
        // failed would send the operator chasing a mailbox that is actually ready - the same rule
        // the bulk executors follow, where audit warnings are warnings.
        var source = ReadProcessor();

        var auditCatch = source.IndexOf("catch (Exception auditEx)", StringComparison.Ordinal);
        Assert.True(auditCatch > 0, "the audit write must be in its own try");

        var afterAudit = source[auditCatch..];
        var nextReturn = afterAudit.IndexOf("return new BulkJobRowOutcome", StringComparison.Ordinal);
        Assert.True(nextReturn > 0);
        Assert.Contains("BulkJobRowStatus.Success", afterAudit[..(nextReturn + 200)], StringComparison.Ordinal);
    }

    [Fact]
    public void TheExportProducesNoArtifactOfItsOwn()
    {
        // R31e: the export writes nothing new - the zip is assembled later from the same store the
        // dialog reads, so the file an operator downloads and the text they were looking at cannot
        // disagree. A processor that wrote its own copy would reintroduce exactly that gap.
        var source = ReadProcessor();

        Assert.Contains("_reports.Write(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ZipArchive", source, StringComparison.Ordinal);
        Assert.DoesNotContain("File.WriteAllText", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ItIsRegisteredUnderTheModuleTheJobNames()
    {
        // The runner resolves a processor by module id. A mismatch here means the job queues and
        // never runs, which looks like a hang rather than a wiring error.
        Assert.Equal(MigrationReportExportJobPayload.ModuleId, MigrationReportExportProcessor.ModuleName);
        Assert.Equal("Migration", MigrationReportExportProcessor.ModuleName);
    }
}
