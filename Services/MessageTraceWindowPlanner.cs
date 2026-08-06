namespace ExchangeAdminWeb.Services;

/// <summary>
/// One cloud trace query's date window.
/// </summary>
public readonly record struct MessageTraceWindow(DateTime Start, DateTime End);

/// <summary>
/// Splits a requested date range into windows Exchange Online will actually accept, and decides
/// what the retention boundary allows.
/// </summary>
/// <remarks>
/// Pure and static: no EXO, no clock of its own. This exists because the rules it encodes were
/// wrong in the app until they were measured against the live tenant 2026-08-06, and a rule that
/// can only be verified by running a trace is a rule that gets guessed at.
///
/// **The two limits are independent, and conflating them is what produced commit 72b8047.**
///
/// - RETENTION: Exchange Online keeps 90 days. A start date older than that is gone and no
///   chunking recovers it.
/// - SPAN: a single <c>Get-MessageTraceV2</c> call accepts at most 10 days between StartDate and
///   EndDate, at ANY offset inside retention. Measured: 8/9/10-day windows return rows, an 11-day
///   window returns a 400 - "The interval between StartDate and EndDate can't be longer than 10
///   days" - and a 10-day window 80 days back returns rows just as one ending today does.
///
/// So a 90-day search is legal and is served by nine sequential 10-day calls. Boundary duplication
/// was measured, not assumed: adjacent windows sharing an endpoint returned 433 rows with 433
/// distinct MessageTraceIds, zero overlap.
/// </remarks>
public static class MessageTraceWindowPlanner
{
    /// <summary>Days of trace data Exchange Online retains. Measured: 90 accepted, 91 refused.</summary>
    public const int RetentionDays = 90;

    /// <summary>Maximum days between StartDate and EndDate in one call. Measured: 10 accepted, 11 refused.</summary>
    public const int MaxWindowDays = 10;

    /// <summary>The window length requested: exactly the service maximum.</summary>
    /// <remarks>
    /// A review raised DST here - that <c>AddDays(-10)</c> on a local value preserves the wall
    /// clock and so spans 10 days and one hour across a fall-back boundary, exceeding the service
    /// limit twice a year. **Measured on this host (Eastern Standard Time), that is not true**:
    /// across 2026-11-01, <c>AddDays(-10)</c> and <c>- TimeSpan.FromDays(10)</c> both yield exactly
    /// 240 hours. <see cref="DateTime"/> arithmetic on an Unspecified or Local kind performs no
    /// timezone conversion; only <see cref="DateTimeOffset"/> or an explicit
    /// <see cref="TimeZoneInfo"/> conversion applies an offset, and this path does neither.
    ///
    /// The TimeSpan form is kept anyway because it states the intent - a bound on ELAPSED time -
    /// and stays correct if a caller ever starts passing offset-aware values.
    /// <c>MessageTraceWindowPlannerTests</c> pins the property across a real DST boundary so the
    /// question does not need re-deriving.
    /// </remarks>
    public static readonly TimeSpan MaxWindow = TimeSpan.FromDays(MaxWindowDays);

    /// <summary>
    /// Why a range cannot be traced, or null when it can.
    /// </summary>
    /// <remarks>
    /// The AGE check is the one that did not exist before and is checked FIRST: the old rule
    /// bounded only the width of the window, so a one-day search 180 days ago passed validation and
    /// failed at Exchange with a raw cmdlet error. Telling an operator the range is too wide when
    /// the real problem is that the data is gone sends them to shrink a window that will never
    /// work.
    ///
    /// <paramref name="today"/> is injected so the boundary is testable without waiting for the
    /// calendar.
    /// </remarks>
    public static string? ValidateRange(DateTime start, DateTime end, DateTime today)
    {
        if (end < start)
            return "End date must be after start date.";
        if (start > today)
            return "Start date cannot be in the future.";
        if (end > today)
            return "End date cannot be in the future.";
        if ((today.Date - start.Date).TotalDays > RetentionDays)
            return $"Exchange Online keeps only {RetentionDays} days of message trace data. Choose a start date within the last {RetentionDays} days.";

        return null;
    }

    /// <summary>
    /// The sequence of windows covering <paramref name="start"/>..<paramref name="end"/>, none
    /// wider than <see cref="MaxWindowDays"/>.
    /// </summary>
    /// <remarks>
    /// Windows are contiguous and share endpoints - window N ends exactly where window N+1 begins.
    /// That is deliberate and measured: the service returns no duplicate rows across such a
    /// boundary, so nudging the boundary to "avoid overlap" would instead open a GAP and silently
    /// drop the messages inside it. Do not add a fudge factor here.
    ///
    /// Emitted NEWEST FIRST. The caller applies a result cap, and a cap consumed by the oldest
    /// windows would drop the recent messages an operator is most likely looking for.
    /// </remarks>
    public static IReadOnlyList<MessageTraceWindow> Split(DateTime start, DateTime end)
    {
        if (end <= start)
            return [new MessageTraceWindow(start, end)];

        var windows = new List<MessageTraceWindow>();
        var cursorEnd = end;

        while (cursorEnd > start)
        {
            // A TimeSpan subtraction, stating the bound as ELAPSED time. Equivalent to
            // AddDays here - measured, including across a DST boundary - see MaxWindow.
            var cursorStart = cursorEnd - MaxWindow;
            if (cursorStart < start)
                cursorStart = start;

            windows.Add(new MessageTraceWindow(cursorStart, cursorEnd));
            cursorEnd = cursorStart;
        }

        return windows;
    }
}
