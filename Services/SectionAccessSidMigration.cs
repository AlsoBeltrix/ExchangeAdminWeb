using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

/// <summary>How a migration attempt ended. Every value leaves the app running.</summary>
public enum SectionAccessMigrationStatus
{
    /// <summary>Every row was already a SID. Nothing to do.</summary>
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
    StoreUnavailable
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

        try
        {
            _repository.ReplaceAllWithSids(
                plan.Rows.Select(r => (r.PolicyAlias, r.Sid!, r.DisplayName)).ToList());
        }
        catch (Exception ex)
        {
            // The write is one transaction, so a throw means it rolled back whole. The store still
            // holds names and the next start retries.
            _logger.LogError(ex, "Section-access SID migration could not be written; no rows were changed");
            return SectionAccessMigrationStatus.StoreUnavailable;
        }

        _logger.LogInformation(
            "Section-access SID migration complete: {Count} row(s) now stored as SIDs", plan.Rows.Count);
        return SectionAccessMigrationStatus.Migrated;
    }
}
