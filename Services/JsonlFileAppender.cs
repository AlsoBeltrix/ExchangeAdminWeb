using System.Diagnostics;
using System.Text;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Appends one JSON record to a JSONL file as one line, or not at all, with every other writer of that file kept out
/// while it does - in this process or another (aud-a28z part 3, aud-a47). The JSONL audit and trace writer and the
/// extended log's consumer (aud-a46) both write through it.
/// </summary>
/// <remarks>
/// aud-a47. Dev and prod share one log root by default, so two instances append to the same daily files, and the
/// writers' own locks were each one instance's monitor. A writer could then write at an offset that was stale by the
/// time it wrote - over the other instance's row - or take a failed line back past a row the other had just reported
/// Written. The lock here is the file system's: a lock file beside the log, opened exclusively. Whoever can write the
/// log folder can take it, and nobody else can hold it. The file system refuses a second exclusive handle whichever
/// process or account asks, and the OS closes a dead process's handle, and so releases its lock. A named mutex was
/// considered and rejected: its default DACL shuts out an instance running as another account, a DACL broad enough to
/// fix that lets any local user hold the predictable name and stall every audit write, and it serialises only writers
/// on one host that name the root the same way (the aud-a47 record).
/// </remarks>
internal static class JsonlFileAppender
{
    /// <summary>
    /// How long a writer waits for another writer's lock before it drops its record. A write holds the lock for a few
    /// milliseconds; the bound is what a hung holder costs each write.
    /// </summary>
    internal static readonly TimeSpan DefaultLockWait = TimeSpan.FromSeconds(2);

    // The two refusals that mean another handle holds the lock file (HRESULT low words).
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int LongestPauseMilliseconds = 16;

    /// <summary>The lock file for <paramref name="path"/>: beside it, never a log - no reader opens or lists it.</summary>
    internal static string LockPathFor(string path) => path + ".lock";

    /// <summary>
    /// Appends <paramref name="json"/> to <paramref name="path"/> as one line, under the file's lock, or throws having
    /// written nothing that stays. The lock is taken first, before anything looks at the file; <paramref name="rotateIfFull"/>
    /// (the caller's size check and rotation), the offset, the repair, the write and any take-back all run under it.
    /// </summary>
    /// <remarks>
    /// The callbacks are the caller's reports and must not throw: <paramref name="repaired"/> after a write that first
    /// ended a cut tail, <paramref name="truncationFailed"/> when taking a failed line back failed too.
    /// </remarks>
    internal static void Append(
        string path,
        string json,
        TimeSpan lockWait,
        Action rotateIfFull,
        Action<Stream, byte[]> writeLineBytes,
        Action repaired,
        Action<Exception> truncationFailed)
    {
        // aud-a47. The lock before anything else: an offset, a size or a last byte read without it can be stale by the
        // time it is used.
        using var held = Lock(path, lockWait);
        rotateIfFull();

        // aud-a28z part 3, moved here from the JSONL writer. One record, one line, or none. The line - the bytes
        // StreamWriter.WriteLine wrote: UTF-8 and the platform's newline - goes in one write on an unbuffered stream, so
        // nothing is held back that a later flush could append after a truncation. A write that fails part-way is taken
        // back to the length the file had before it, best effort. And a file that ends mid-line - a truncation that
        // failed, or a writer that died - is ended first, so this record starts on its own line; the half line stays,
        // one line the event log drops, and the caller reports the repair.
        using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1);
        var start = file.Seek(0, SeekOrigin.End);
        var repair = start > 0 && !EndsWithNewline(file);
        var line = Encoding.UTF8.GetBytes(repair ? Environment.NewLine + json + Environment.NewLine : json + Environment.NewLine);

        try
        {
            writeLineBytes(file, line);
        }
        catch (Exception)
        {
            TruncateTo(file, start, truncationFailed);
            throw;
        }

        if (repair)
            repaired();
    }

    /// <summary>
    /// Takes <paramref name="path"/>'s lock: an exclusive handle on its lock file, deleted when the handle closes. A held
    /// lock is retried, pausing 1 ms and doubling to 16 ms, until <paramref name="wait"/> has passed; then
    /// <see cref="JsonlLockTimeoutException"/>. Any other failure to open it propagates at once.
    /// </summary>
    internal static IDisposable Lock(string path, TimeSpan wait)
    {
        var lockPath = LockPathFor(path);
        var clock = Stopwatch.StartNew();
        var pause = 1;
        while (true)
        {
            Exception refusal;
            try
            {
                return File.OpenHandle(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, FileOptions.DeleteOnClose);
            }
            catch (IOException ex) when (IsHeldElsewhere(ex))
            {
                refusal = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                // What a contender meets while the holder's close is deleting the file. A folder that refuses the lock for
                // good meets it too, and costs each write its full wait (the aud-a47 record's known gaps).
                refusal = ex;
            }

            // A live writer that hangs holding the lock costs this wait and no more: the record is dropped, its report
            // naming the lock, and never written without it.
            if (clock.Elapsed >= wait)
                throw new JsonlLockTimeoutException(lockPath, wait, refusal);

            Thread.Sleep(pause);
            pause = Math.Min(pause * 2, LongestPauseMilliseconds);
        }
    }

    private static bool IsHeldElsewhere(IOException ex) =>
        (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    // The last byte read back; the stream is left at the end, where the line is written.
    private static bool EndsWithNewline(FileStream file)
    {
        file.Seek(-1, SeekOrigin.End);
        return file.ReadByte() == '\n';
    }

    // Best effort: a truncation that fails is reported, and the next write ends the half line before its own.
    private static void TruncateTo(FileStream file, long length, Action<Exception> truncationFailed)
    {
        try
        {
            file.SetLength(length);
        }
        catch (Exception ex)
        {
            truncationFailed(ex);
        }
    }
}

/// <summary>
/// A JSONL writer did not get its file's lock within its wait (aud-a47). The record is dropped; the writer's recovery
/// path reports it with this exception, which names the lock file, the wait, and - inside - the last refusal.
/// </summary>
internal sealed class JsonlLockTimeoutException(string lockPath, TimeSpan wait, Exception innerException)
    : IOException(
        $"Another writer held the lock {lockPath} for longer than {wait.TotalMilliseconds:0} ms; the record was not written, so that it is never written at an offset read without the lock",
        innerException)
{
    public string LockPath { get; } = lockPath;
}
