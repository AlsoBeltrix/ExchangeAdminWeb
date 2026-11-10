using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Modules;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>Why the runner refused a row of a job instead of handing it to the job's processor.</summary>
public enum JobRefusal
{
    /// <summary>The job carries no authorization snapshot, or one that cannot be read.</summary>
    SnapshotMissing,

    /// <summary>The job's snapshot was captured for a section other than its module's.</summary>
    SnapshotForAnotherSection,

    /// <summary>The job's module is disabled, or its enablement cannot be read.</summary>
    ModuleDisabled,

    /// <summary>No group that authorized the submitter is allowed for the section now, or section access cannot be read.</summary>
    AccessRemoved,

    /// <summary>The job's module is not in the catalog, or a store threw while it was read.</summary>
    Unverifiable,
}

/// <summary>
/// Whether a queued job may still run, asked by the runner before every row. An interface so a test
/// of the runner's own mechanics can run a processor that belongs to no catalog module.
/// </summary>
public interface IJobAuthorization
{
    /// <summary>Null when the job may run its next row; otherwise why it may not.</summary>
    JobRefusal? Check(BulkJob job);
}

/// <summary>
/// Re-checks a job against the live stores: its module must be enabled, and a group that authorized
/// the submitter when the job was queued (<see cref="JobAuthorizationSnapshot"/>) must still be
/// allowed for the module's main permission. Anything it cannot establish is a refusal.
/// </summary>
public sealed class JobAuthorization : IJobAuthorization
{
    private readonly ModuleCatalog _catalog;
    private readonly ModuleEnablementService _enablement;
    private readonly SectionAccessService _sectionAccess;
    private readonly ILogger<JobAuthorization> _logger;

    public JobAuthorization(
        ModuleCatalog catalog,
        ModuleEnablementService enablement,
        SectionAccessService sectionAccess,
        ILogger<JobAuthorization> logger)
    {
        _catalog = catalog;
        _enablement = enablement;
        _sectionAccess = sectionAccess;
        _logger = logger;
    }

    public JobRefusal? Check(BulkJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        // A module's jobs are authorized against its main permission.
        var section = _catalog.GetById(job.ModuleId)?.MainPermission.PolicyAlias;
        if (section is null)
            return JobRefusal.Unverifiable;

        var snapshot = JobAuthorizationSnapshot.FromJson(job.AuthSnapshotJson);
        if (snapshot is null)
            return JobRefusal.SnapshotMissing;
        if (!string.Equals(snapshot.Section, section, StringComparison.Ordinal))
            return JobRefusal.SnapshotForAnotherSection;

        try
        {
            if (!_enablement.IsModuleEnabled(job.ModuleId))
                return JobRefusal.ModuleDisabled;

            // Only a group captured when the job was queued may keep it running. A group allowed
            // since then is not a replacement for that decision.
            var groups = _sectionAccess.GetGroupsForSection(section)
                .Where(SectionAccessGroupIdentity.IsUsableGroupSid).ToArray();
            return snapshot.IsStillAuthorized(groups) ? null : JobRefusal.AccessRemoved;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Authorization of bulk job {Job} ({Module}) could not be verified", job.Id, job.ModuleId);
            return JobRefusal.Unverifiable;
        }
    }
}
