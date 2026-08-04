using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Owns everything about where a Message Analysis detail export lives on disk: the directory, the
/// filename convention, jobId validation, and existence checks. One implementation shared by the
/// writer (MessageTraceDetailJobProcessor) and the reader (the Downloadable Reports page), so the
/// two cannot drift apart and orphan existing files
/// (docs/MessageTraceDownloadLink-Plan.md slice 1).
///
/// Retention: exports older than <see cref="RetentionDays"/> are deleted by
/// <see cref="PruneExpired"/>, called once at startup. <see cref="ExpiresAtUtc"/> states the same
/// window to the notification email and the reports page, so what the app promises and what it
/// enforces are the same number by construction. A missing file is an ordinary outcome, not an
/// error.
///
/// **Superseded design, recorded so it is not reinstated.** Until 2026-08-04 this was documented as
/// the job of a host scheduled task, per plan D1. That task was never created on any host -
/// measured, schtasks listed 266 tasks and none belonged to this app - so nothing enforced the
/// window, exports accumulated indefinitely, and past day 30 the reports page would show "Expired"
/// for a file still on disk. Owner ruled 2026-08-04: **"there are and will be no scheduled
/// tasks."** Retention moved in-process, alongside the bulk-job record prune that already runs
/// there.
///
/// <see cref="RetentionDays"/> stays a constant rather than a config key. Openreview F4's reasoning
/// still holds and now holds more strongly: one number is read by the pruner, the email and the
/// page, so a second knob could only create disagreement.
/// </summary>
public sealed class MessageTraceExportStore
{
    /// <summary>
    /// How long an export survives. Read by the pruner, the notification email and the reports
    /// page alike - see the type remarks for why this is one constant and not a setting.
    /// </summary>
    public const int RetentionDays = 30;

    /// <summary>
    /// Job IDs are assigned as GUID "N" (Services/Jobs/BulkJobModels.cs). Validating against that
    /// exact shape is a total whitelist, which closes path traversal at the parse step rather than
    /// by sanitising a hostile string - there is no "clean it up and carry on" branch to get wrong.
    /// </summary>
    private static readonly Regex JobIdPattern = new("^[0-9a-fA-F]{32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly IConfiguration _config;

    public MessageTraceExportStore(IConfiguration config) => _config = config;

    /// <summary>
    /// The export directory, under the required audit log root. Resolved through
    /// <see cref="AuditLogRoot.Require"/> only - never re-derived - so an unset Audit:LogRoot fails
    /// loudly here exactly as it does everywhere else. Does not create the directory.
    /// </summary>
    public string DirectoryPath =>
        Path.Combine(AuditLogRoot.Require(_config), "ExchangeAdminWeb", "MessageTraceExports");

    /// <summary>
    /// The export filename for a job. This format is load-bearing: it is what already exists on disk
    /// for every export written to date, so changing it orphans those files.
    /// </summary>
    public string FileNameFor(string jobId, DateTime submittedAtUtc)
    {
        RequireValidJobId(jobId);
        return $"MessageTraceDetail_{jobId}_{submittedAtUtc:yyyyMMdd-HHmmss}.csv";
    }

    /// <summary>
    /// The full path an export would occupy. Throws <see cref="ArgumentException"/> for a jobId that
    /// is not a GUID "N", and <see cref="InvalidOperationException"/> if the composed path escapes
    /// <see cref="DirectoryPath"/>. Says nothing about whether the file exists - see
    /// <see cref="TryResolve"/>.
    /// </summary>
    public string PathFor(string jobId, DateTime submittedAtUtc)
    {
        var dir = DirectoryPath;
        var candidate = Path.Combine(dir, FileNameFor(jobId, submittedAtUtc));

        // Belt and braces. The jobId whitelist should make this unreachable; this is the guard that
        // survives a future change to the ID format, and it fails loudly rather than quietly
        // resolving somewhere else.
        //
        // Confirmed unreachable today: with this branch disabled, all 19 store tests still pass,
        // because the whitelist rejects every traversal input before it gets here. That makes it
        // untested-by-construction rather than untested-by-omission - deliberate, and the reason it
        // must not be deleted as "dead code". Anything that loosens the jobId shape makes it live.
        var root = Path.GetFullPath(dir);
        var full = Path.GetFullPath(candidate);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Resolved export path escapes the export directory: {full}");

        return full;
    }

    /// <summary>
    /// True and the full path when the export file exists. False for a missing file - the normal,
    /// expected state once the host retention task has run. Does not throw for a missing file or a
    /// missing directory, and never creates the directory. An invalid jobId still throws: that is a
    /// caller bug, not an expired export, and must not be reported as one.
    /// </summary>
    public bool TryResolve(string jobId, DateTime submittedAtUtc, out string fullPath)
    {
        fullPath = PathFor(jobId, submittedAtUtc);
        return File.Exists(fullPath);
    }

    /// <summary>
    /// When the host retention task is expected to have removed this export. Descriptive only; see
    /// the type remarks. Used for the availability date in the notification email and the reports
    /// page column.
    /// </summary>
    public DateTime ExpiresAtUtc(DateTime submittedAtUtc) => submittedAtUtc.AddDays(RetentionDays);

    private static void RequireValidJobId(string jobId)
    {
        if (string.IsNullOrEmpty(jobId) || !JobIdPattern.IsMatch(jobId))
            throw new ArgumentException($"Job id is not a GUID \"N\" value: '{jobId}'", nameof(jobId));
    }

    /// <summary>
    /// Mirrors <see cref="FileNameFor"/>. Anchored at both ends so only files this app's export
    /// path wrote can ever match.
    ///
    /// This is the load-bearing guard in <see cref="PruneExpired"/>: the export directory sits
    /// INSIDE the audit log root, so a wildcard sweep there is one configuration mistake away from
    /// deleting audit data. Matching the exact convention means anything else in that directory -
    /// an operator's note, a half-written file, a future format - is left alone by construction.
    /// </summary>
    private static readonly Regex ExportFilePattern = new(
        @"^MessageTraceDetail_[0-9a-fA-F]{32}_\d{8}-\d{6}\.csv$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Deletes exports whose age exceeds <see cref="RetentionDays"/>. Returns how many were
    /// removed. Called once at startup from <c>Program.cs</c>; there is no timer, matching the
    /// bulk-job runner's rule that nothing in this app runs on a schedule.
    ///
    /// Never throws. Retention is housekeeping and must not be able to stop the app booting, so a
    /// missing directory, an unreadable one, or a locked individual file are all survivable. A
    /// per-file failure is counted and logged rather than aborting the sweep, so one locked file
    /// cannot leave the rest of an expired set on disk.
    /// </summary>
    /// <param name="nowUtc">Injected so the cutoff is testable without waiting 30 days.</param>
    public int PruneExpired(DateTime nowUtc, ILogger? logger = null)
    {
        string directory;
        try
        {
            directory = DirectoryPath;
        }
        catch (Exception ex)
        {
            // An unset Audit:LogRoot throws here. That is fatal elsewhere by design, but this path
            // must not be what surfaces it - the app has a louder, clearer failure for it already.
            logger?.LogWarning(ex, "Export retention skipped: the export directory could not be resolved");
            return 0;
        }

        if (!Directory.Exists(directory))
        {
            // The ordinary state before the first export is written. Not an error, and not
            // something to create.
            return 0;
        }

        var cutoff = nowUtc.AddDays(-RetentionDays);
        var deleted = 0;
        var failed = 0;

        try
        {
            foreach (var path in Directory.EnumerateFiles(directory))
            {
                if (!ExportFilePattern.IsMatch(Path.GetFileName(path)))
                    continue;

                try
                {
                    if (File.GetLastWriteTimeUtc(path) >= cutoff)
                        continue;

                    File.Delete(path);
                    deleted++;
                }
                catch (Exception ex)
                {
                    failed++;
                    logger?.LogWarning(ex, "Could not delete expired export {Path}", path);
                }
            }
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Export retention sweep of {Directory} failed", directory);
            return deleted;
        }

        if (deleted > 0 || failed > 0)
        {
            logger?.LogInformation(
                "Export retention: deleted {Deleted} export(s) older than {Days}d from {Directory}; {Failed} could not be deleted",
                deleted, RetentionDays, directory, failed);
        }

        return deleted;
    }
}
