namespace ExchangeAdminWeb.Services;

/// <summary>
/// A log file this instance cannot rotate (aud-a65): which log, which file, since when its rotation has failed, how large it
/// has grown and its size limit. The writer still appends every entry to it (aud-a56), so it grows past its limit until the
/// rotation succeeds. The Admin Event Log shows it.
/// </summary>
public sealed record LogRotationFailure(string Log, string Path, DateTime SinceUtc, long SizeBytes, long LimitBytes)
{
    /// <summary>How far past its limit the file has grown.</summary>
    public long OverBytes => Math.Max(0, SizeBytes - LimitBytes);
}

/// <summary>
/// One writer's failed-rotation streak for one file, and when to report it (aud-a65). aud-a56 reported a streak once, at its
/// first failed write, and never again while it lasted, so a file another program held for days grew without a second word
/// until the volume filled. A streak is reported at its first failure, then whenever the file has grown by another whole
/// limit since the last report, or <see cref="ReportEvery"/> has passed since it, whichever comes first.
/// </summary>
/// <remarks>
/// Changed only by its writer: under the JSONL writer's lock, or on the extended log's one consumer. <see cref="State"/> is
/// replaced whole on each failure, so a reader on another thread - the event log's page - sees one failure's state.
/// </remarks>
internal sealed class RotationFailureStreak
{
    /// <summary>The longest a streak goes unreported while it lasts.</summary>
    internal static readonly TimeSpan ReportEvery = TimeSpan.FromHours(1);

    private volatile LogRotationFailure _state;
    private DateTime _lastReportUtc;
    private long _lastReportSize;

    internal RotationFailureStreak(string log, string path, long limitBytes, DateTime sinceUtc) =>
        _state = new LogRotationFailure(log, path, sinceUtc, 0, limitBytes);

    /// <summary>The streak as it stands.</summary>
    internal LogRotationFailure State => _state;

    /// <summary>How many times the streak has been reported.</summary>
    internal int Reports { get; private set; }

    /// <summary>
    /// Records one failed rotation of the file, now <paramref name="sizeBytes"/> long - null when its size could not be read,
    /// and the last one stands - and answers whether to report it.
    /// </summary>
    internal bool Failed(long? sizeBytes, DateTime nowUtc)
    {
        var state = _state;
        if (sizeBytes is { } size)
            _state = state = state with { SizeBytes = size };

        var grown = state.SizeBytes - _lastReportSize >= state.LimitBytes;
        var due = nowUtc - _lastReportUtc >= ReportEvery;
        if (Reports > 0 && !grown && !due)
            return false;

        Reports++;
        _lastReportUtc = nowUtc;
        _lastReportSize = state.SizeBytes;
        return true;
    }

    /// <summary>The length of the file at <paramref name="path"/>, or null when it cannot be read. Never throws.</summary>
    internal static long? SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
