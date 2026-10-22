using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

/// <summary>How a migration attempt ended. Every value leaves the app running.</summary>
public enum SectionAccessMigrationStatus
{
    /// <summary>
    /// Every row was already a SID, or every row this run read had been converted by another
    /// writer - normally the other instance starting alongside this one - by the time it held the
    /// write lock. Nothing to do.
    /// </summary>
    AlreadyMigrated,

    /// <summary>Rows were converted and written.</summary>
    Migrated,

    /// <summary>
    /// One or more rows could not be resolved. NOTHING was written; the store still holds names
    /// and authorization is unchanged.
    /// </summary>
    Halted,

    /// <summary>
    /// The directory could not be consulted. Nothing was written; the next start retries.
    /// </summary>
    DirectoryUnavailable,

    /// <summary>The store could not be read or written. Nothing was changed.</summary>
    StoreUnavailable,

    /// <summary>
    /// Another writer - normally an administrator saving through the other instance - changed or
    /// removed rows after this run read them. Those rows were left exactly as that change left
    /// them, and none was written back; every row still as read was converted. The log names
    /// each one. A row still holding a name is converted on the next start (aud-a33b).
    /// </summary>
    SkippedChangedRows
}

/// <summary>
/// Converts stored section-access group names to SIDs, once, at startup.
/// </summary>
/// <remarks>
/// <para><b>Never blocks boot and never half-writes.</b> Every failure mode returns a status and
/// leaves the store exactly as it was. That is not defensiveness for its own sake: this is the
/// table that decides who can reach every module, it runs before anyone can log in to fix
/// anything, and it depends on Active Directory - which the rest of startup does not. An app that
/// refused to start on an AD blip would be a worse outage than the ambiguity being fixed.</para>
///
/// <para><b>Never writes over another writer (aud-a33b).</b> The directory lookups run with no
/// lock held - I/O never runs under the write lock - and dev and prod share this database, so
/// the other instance can change grants meanwhile. The write therefore re-checks every row
/// under the lock and converts only those still exactly as read; a row that changed is that
/// writer's, and is left alone and reported rather than restored. All-or-nothing still governs
/// RESOLUTION - one row that cannot be resolved writes nothing - but a concurrent change is not
/// a resolution failure, so the rows nobody touched are still converted.</para>
///
/// <para><b>Idempotent and re-runnable.</b> A row already holding a SID is left alone, so a
/// deferred run picks up where the last one stopped, and a successful run makes every later one a
/// no-op. The migration is therefore safe to leave wired to every start rather than gated behind a
/// one-shot marker - a marker would also have to be rolled back on failure, adding a second thing
/// that can be wrong.</para>
///
/// See docs/SectionAccessSidStorage-Plan.md.
/// </remarks>
public sealed class SectionAccessSidMigration
{
    private readonly SectionAccessRepository _repository;
    private readonly ISectionAccessGroupDirectory _directory;
    private readonly ILogger<SectionAccessSidMigration> _logger;

    public SectionAccessSidMigration(
        SectionAccessRepository repository,
        ISectionAccessGroupDirectory directory,
        ILogger<SectionAccessSidMigration> logger)
    {
        _repository = repository;
        _directory = directory;
        _logger = logger;
    }

    /// <summary>
    /// Runs the migration. Does not throw: the caller is startup, and every failure here is one
    /// the app can run without.
    /// </summary>
    public SectionAccessMigrationStatus Run()
    {
        IReadOnlyList<(string PolicyAlias, string GroupValue)> rows;
        try
        {
            rows = _repository.GetAllRows();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not read section access for the SID migration; nothing was changed");
            return SectionAccessMigrationStatus.StoreUnavailable;
        }

        if (rows.Count == 0)
        {
            // A store with no rows is a valid state (nothing configured yet), not a failure.
            return SectionAccessMigrationStatus.AlreadyMigrated;
        }

        SectionAccessMigrationPlan plan;
        try
        {
            plan = SectionAccessSidMigrationPlanner.Plan(rows, _directory);
        }
        catch (DirectoryUnavailableException ex)
        {
            // Deliberately a warning, not an error: this is transient and self-healing. The store
            // is untouched and the next start tries again.
            _logger.LogWarning(
                "Section-access SID migration deferred - Active Directory could not be consulted ({Reason}). "
                + "Access is unaffected; it will be retried on the next start.", ex.Message);
            return SectionAccessMigrationStatus.DirectoryUnavailable;
        }

        if (plan.AlreadyMigrated)
            return SectionAccessMigrationStatus.AlreadyMigrated;

        if (!plan.ShouldWrite)
        {
            // LogError, because unlike the case above this will not fix itself - a human must
            // correct the data.
            _logger.LogError("{Report}", SectionAccessSidMigrationPlanner.DescribeFailures(plan.Failures));
            return SectionAccessMigrationStatus.Halted;
        }

        // Only rows whose stored value differs from the SID are conversions: names, and a SID
        // stored with stray whitespace, which the old wholesale rewrite also trimmed. A row already
        // holding its SID is not touched at all, so if somebody removes it meanwhile it stays
        // removed.
        var conversions = plan.Rows
            .Where(r => !string.Equals(r.OriginalValue, r.Sid, StringComparison.Ordinal))
            .Select(r => new SidConversion(r.PolicyAlias, r.OriginalValue, r.Sid!, r.DisplayName))
            .ToList();

        SidConversionOutcome outcome;
        try
        {
            outcome = _repository.ConvertToSids(conversions);
        }
        catch (Exception ex)
        {
            // The write is one transaction, so a throw means it rolled back whole. The store still
            // holds names and the next start retries.
            _logger.LogError(ex, "Section-access SID migration could not be written; no rows were changed");
            return SectionAccessMigrationStatus.StoreUnavailable;
        }

        if (outcome.Skipped.Count > 0)
        {
            // A warning, not an error: on a shared database this is a legitimate interleaving and
            // the newer data won, which is the outcome wanted. It is logged so an administrator
            // can see their change was kept, and which rows are not yet converted.
            _logger.LogWarning("{Report}", DescribeSkipped(outcome));
            return SectionAccessMigrationStatus.SkippedChangedRows;
        }

        if (outcome.Converted == 0)
        {
            // Both instances started together and the other converted first. Reporting the rows as
            // "changed" would be true of the table and false of anyone's intent.
            _logger.LogInformation(
                "Section-access SID migration: every row this run read had already been converted by another writer");
            return SectionAccessMigrationStatus.AlreadyMigrated;
        }

        _logger.LogInformation(
            "Section-access SID migration complete: {Count} row(s) now stored as SIDs", outcome.Converted);
        return SectionAccessMigrationStatus.Migrated;
    }

    /// <summary>The operator-facing account of rows the write left alone.</summary>
    internal static string DescribeSkipped(SidConversionOutcome outcome)
    {
        var lines = outcome.Skipped
            .Select(s => $"  {s.PolicyAlias} / '{s.Value}'")
            .Distinct(StringComparer.OrdinalIgnoreCase);

        return $"Section-access SID migration: {outcome.Skipped.Count} row(s) changed after this run read them "
            + "- normally an administrator saving through the other instance - and were left exactly as that "
            + $"change left them; none was written back. {outcome.Converted} row(s) still as read were converted. "
            + "A row still holding a group name is converted on the next start. Left alone:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, lines);
    }
}
