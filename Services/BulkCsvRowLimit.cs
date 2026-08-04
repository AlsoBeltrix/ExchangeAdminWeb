using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// The cap on how many rows one bulk permission CSV may carry.
/// </summary>
/// <remarks>
/// A safety control, not a performance tuning knob: each row is a permission grant against a real
/// mailbox, so the cap bounds the blast radius of one mistaken upload. It is duplicated verbatim
/// in <see cref="MailboxPermissionService"/> and <see cref="CalendarPermissionService"/>, where it
/// sits inside an async CSV-reading method that no test could reach - both services were at 0%
/// coverage.
///
/// The reading loop breaks once it has read one row PAST the cap, then rejects. That is
/// deliberate: it detects "too many" without materializing an unbounded file into memory. The
/// consequence is the boundary this class pins - exactly <see cref="MaxRows"/> rows is ACCEPTED,
/// and rejection needs at least one more.
/// </remarks>
public static class BulkCsvRowLimit
{
    /// <summary>The largest accepted row count.</summary>
    public const int MaxRows = 200;

    /// <summary>How many rows to read before stopping - one past the cap, so the cap can be
    /// detected as exceeded without reading the whole file.</summary>
    public const int ReadCeiling = MaxRows + 1;

    public const string RejectionMessage =
        "CSV exceeds 200 row limit. Please split into smaller files.";

    /// <summary>
    /// Whether the reader should stop after the row count reached so far.
    /// </summary>
    public static bool ShouldStopReading(int rowsReadSoFar) => rowsReadSoFar > MaxRows;

    /// <summary>
    /// Whether a file of this size must be rejected outright.
    /// </summary>
    /// <remarks>
    /// Note the asymmetry with a plain <c>&gt;=</c>: a file of exactly <see cref="MaxRows"/> rows
    /// is valid and processed. Tightening this to reject at 200 would silently break a working
    /// 200-row upload, which is why the boundary is pinned by test rather than left to whoever
    /// next reads the condition.
    /// </remarks>
    public static bool Exceeds(int rowCount) => rowCount > MaxRows;

    /// <summary>
    /// The result returned for an oversized file: every row counted as failed, nothing applied.
    /// </summary>
    public static BulkOperationResult Rejected(int rowCount) => new()
    {
        TotalRows = rowCount,
        FailedCount = rowCount,
        Errors = [RejectionMessage]
    };
}
