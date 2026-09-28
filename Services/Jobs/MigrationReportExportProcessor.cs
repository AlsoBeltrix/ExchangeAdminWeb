using System.Text.Json;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// The per-mailbox work behind a bulk migration report export (R31 of
/// docs/MigrationInterfaceRedesign-Plan.md).
/// </summary>
/// <remarks>
/// <para>
/// One row per ticked mailbox. Each row either reuses a report the store already holds (R31c) or
/// fetches one and stores it. <b>It writes nothing new</b> (R31e): the zip is assembled later from
/// the same store the dialog reads, so the file an operator downloads and the text they were
/// looking at cannot disagree. That is why this processor produces no artifact of its own.
/// </para>
/// <para>
/// <b>Read-only, so no ticket and no protected-principal gate</b> (R31d). Fetching a report
/// changes nothing. It runs at <c>MigrationCheck</c> - the module's main permission, the one that
/// already lets an operator read the batch list, and the level the single Report button has always
/// run at. Requiring <c>MigrationManage</c> would be a quiet privilege escalation of the page's
/// read surface. It is still audited as a read of migration data.
/// </para>
/// <para>
/// <b>R31b: a partial result is still a result.</b> Three of six succeeding means three reports
/// are available and the three that failed are named. A row that fails is recorded as Failed and
/// the job continues - the runner aggregates, and nothing here may turn one bad mailbox into a
/// blanket failure (Known Failure Class 2).
/// </para>
/// </remarks>
public sealed class MigrationReportExportProcessor : IBulkJobProcessor
{
    private readonly MigrationService _migration;
    private readonly MigrationReportStore _reports;
    private readonly AuditService _audit;
    private readonly ILogger<MigrationReportExportProcessor> _logger;

    public MigrationReportExportProcessor(
        MigrationService migration,
        MigrationReportStore reports,
        AuditService audit,
        ILogger<MigrationReportExportProcessor> logger)
    {
        _migration = migration;
        _reports = reports;
        _audit = audit;
        _logger = logger;
    }

    /// <summary>Registration key, matching the other processors' static ModuleName convention.</summary>
    public const string ModuleName = MigrationReportExportJobPayload.ModuleId;

    public string ModuleId => ModuleName;

    public int CountRows(BulkJob job) => Parse(job).EmailAddresses.Count;

    public async Task<BulkJobRowOutcome> ProcessRowAsync(
        BulkJob job, int rowIndex, CancellationToken cancellationToken)
    {
        var payload = Parse(job);
        var email = payload.EmailAddresses[rowIndex];

        // R31c. Anything already fetched under R24a is reused rather than pulled again. This is
        // the difference between an export that costs twenty minutes per mailbox and one that
        // costs twenty minutes per mailbox NOT already read - on a batch the operator has been
        // working through, most of them.
        var held = _reports.TryRead(payload.BatchName, email, DateTime.UtcNow);
        if (held != null)
        {
            return new BulkJobRowOutcome
            {
                Target = email,
                Status = BulkJobRowStatus.Success,
                Message = $"Reused the report taken {held.FetchedAtUtc:yyyy-MM-dd HH:mm} UTC.",
            };
        }

        // The runner checks cancellation between rows; checking here too means a cancel landing
        // during a twenty-minute fetch is honoured before the NEXT one starts rather than after.
        cancellationToken.ThrowIfCancellationRequested();

        var takenAtUtc = DateTime.UtcNow;
        try
        {
            var text = await _migration.GetMigrationUserReportAsync(email);

            _reports.Write(payload.BatchName, email, text, takenAtUtc);

            try
            {
                _audit.LogMigrationAction(
                    job.SubmittedBy, job.SubmittedIp, "ExportUserReport", email,
                    success: true, ticketNumber: job.Ticket);
            }
            catch (Exception auditEx)
            {
                // An audit failure is a warning, never an operation failure: the report was
                // fetched and stored, and reporting the row as Failed would send the operator
                // chasing a mailbox that is actually ready.
                _logger.LogWarning(auditEx, "Audit failed for exported report of {Email}", email);
            }

            return new BulkJobRowOutcome
            {
                Target = email,
                Status = BulkJobRowStatus.Success,
                Message = "Report fetched.",
            };
        }
        catch (Exception ex)
        {
            // Failed, not thrown. A throw is caught by the runner and recorded the same way, but
            // returning makes the message the operator reads ours rather than an exception string.
            _logger.LogWarning(ex, "Report fetch failed for {Email}", email);

            return new BulkJobRowOutcome
            {
                Target = email,
                Status = BulkJobRowStatus.Failed,
                Message = $"Report could not be fetched: {ex.Message}",
            };
        }
    }

    private static MigrationReportExportJobPayload Parse(BulkJob job) =>
        JsonSerializer.Deserialize<MigrationReportExportJobPayload>(job.PayloadJson)
        ?? throw new InvalidOperationException(
            $"Migration report export job {job.Id} carries an unreadable payload.");
}
