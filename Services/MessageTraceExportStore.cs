using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Owns everything about where a Message Analysis detail export lives on disk: the directory, the
/// filename convention, jobId validation, and existence checks. One implementation shared by the
/// writer (MessageTraceDetailJobProcessor) and the reader (the Downloadable Reports page), so the
/// two cannot drift apart and orphan existing files
/// (docs/MessageTraceDownloadLink-Plan.md slice 1).
///
/// Retention note: this app NEVER deletes export files. A scheduled task on the host removes files
/// older than <see cref="RetentionDays"/> from the audit log root. <see cref="ExpiresAtUtc"/> only
/// mirrors that external policy so the notification email can state an availability date - it
/// enforces nothing, and a missing file is an ordinary outcome, not an error. Deliberately a
/// constant and not a config key: because the value is descriptive, a configurable one would be a
/// second source of retention truth that could silently disagree with the scheduled task (plan D1,
/// openreview F4). If the host task's window changes, change this constant in the same commit.
/// </summary>
public sealed class MessageTraceExportStore
{
    /// <summary>Mirrors the host scheduled task's deletion window. Descriptive only - see the type remarks.</summary>
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
}
