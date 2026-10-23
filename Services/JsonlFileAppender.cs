using System.Diagnostics;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
/// aud-a51. The lock is the open handle, never the file. It was opened DeleteOnClose, which asks for delete access, so
/// in a folder that grants create, read and write but not delete, a lock file already there refused every writer every
/// time: each record waited out the bound and was dropped as if another writer held it. It is opened without that now,
/// and after release the file is removed where the folder allows it and otherwise left, empty and harmless.
/// </remarks>
internal static class JsonlFileAppender
{
    /// <summary>
    /// How long a writer waits for another writer's lock before it drops its record. A write holds the lock for a few
    /// milliseconds; the bound is what a hung holder costs each write.
    /// </summary>
    internal static readonly TimeSpan DefaultLockWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long an access refusal at the lock file is retried before it is the folder's refusal (aud-a51). A writer that
    /// opens the lock file while a third party deletes it - an instance on the aud-a47 build deletes it on close - is
    /// refused access for a moment; the longest run measured was 5.1 ms, under six contending threads. A refusal that
    /// outlasts this is the folder's, and costs each write this long, never the bound.
    /// </summary>
    internal static readonly TimeSpan DefaultAccessRefusalGrace = TimeSpan.FromMilliseconds(100);

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
    /// The callbacks are the caller's reports and must not throw: <paramref name="rotationFailed"/> when the rotation threw
    /// and the line goes to the file as it is (aud-a56), <paramref name="repaired"/> after a write that first ended a cut
    /// tail, <paramref name="truncationFailed"/> when taking a failed line back failed too.
    /// <paramref name="lockWait"/> bounds the wait for another writer's lock, and <paramref name="accessRefusalGrace"/> the
    /// retry of a refusal (aud-a51).
    /// </remarks>
    internal static void Append(
        string path,
        string json,
        TimeSpan lockWait,
        TimeSpan accessRefusalGrace,
        Action rotateIfFull,
        Action<Exception> rotationFailed,
        Action<Stream, byte[]> writeLineBytes,
        Action repaired,
        Action<Exception> truncationFailed)
    {
        // aud-a56. The folder before the lock, whose file lives in it, on every write: it was created once, so a folder
        // removed while the app ran refused every row until a restart.
        EnsureFolder(path);

        // aud-a47. The lock before anything else: an offset, a size or a last byte read without it can be stale by the
        // time it is used.
        using var held = Lock(path, lockWait, accessRefusalGrace);

        // aud-a56. A rotation that fails does not fail the write: the line goes to the file as it is, past its limit, and
        // the next write tries the rotation again. It threw out of here, and every row to a full file was dropped until a
        // rotation succeeded - a folder without delete, another program holding the file without delete sharing.
        try
        {
            rotateIfFull();
        }
        catch (Exception ex)
        {
            rotationFailed(ex);
        }

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
    /// Takes <paramref name="path"/>'s lock: an exclusive handle on its lock file. A sharing or lock violation is another
    /// writer's handle, retried - pausing 1 ms and doubling to 16 ms - until <paramref name="wait"/> has passed; then
    /// <see cref="JsonlLockTimeoutException"/>. An access refusal is retried for <paramref name="accessRefusalGrace"/>, then
    /// <see cref="JsonlLockRefusedException"/>, which any other IO failure to open it is at once (aud-a51).
    /// </summary>
    internal static IDisposable Lock(string path, TimeSpan wait, TimeSpan accessRefusalGrace)
    {
        var lockPath = LockPathFor(path);
        var clock = Stopwatch.StartNew();
        var pause = 1;
        TimeSpan? refusedSince = null;
        while (true)
        {
            try
            {
                return new HeldLock(lockPath, File.OpenHandle(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException ex) when (IsHeldElsewhere(ex))
            {
                refusedSince = null;

                // A live writer that hangs holding the lock costs this wait and no more: the record is dropped, its report
                // naming the lock, and never written without it.
                if (clock.Elapsed >= wait)
                    throw new JsonlLockTimeoutException(lockPath, wait, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                // aud-a51. Not contention: the folder refuses this account the lock file, or a third party is deleting
                // it. This was waited for to the bound and reported as another writer's lock. It gets the grace now,
                // counted from the first refusal of an unbroken run - a sharing violation ends a run - and is then the
                // folder's refusal, reported as such.
                refusedSince ??= clock.Elapsed;
                if (clock.Elapsed - refusedSince.Value >= accessRefusalGrace)
                    throw new JsonlLockRefusedException(lockPath, ex);
            }
            catch (IOException ex)
            {
                // A missing folder, a device error: no wait helps. The record is dropped now, the reason inside.
                throw new JsonlLockRefusedException(lockPath, ex);
            }

            Thread.Sleep(pause);
            pause = Math.Min(pause * 2, LongestPauseMilliseconds);
        }
    }

    private static bool IsHeldElsewhere(IOException ex) =>
        (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    // The lock is this handle, not the file (aud-a51): closing it releases the lock, whatever then becomes of the file.
    private sealed class HeldLock(string lockPath, SafeFileHandle handle) : IDisposable
    {
        public void Dispose()
        {
            handle.Dispose();
            RemoveIfFree(lockPath);
        }
    }

    // Best effort, once the handle is closed (aud-a51): the file is moved out of the lock's name, then deleted. A plain
    // delete leaves the name delete-pending for a moment, and a writer that opens it then is refused access, which reads
    // as the folder refusing the lock - 576 refusals in 3 s under six contending threads, against none for the move. A
    // move of a file another writer holds is refused with a sharing violation, so it never removes a held lock.
    private static void RemoveIfFree(string lockPath)
    {
        var released = lockPath + "." + Guid.NewGuid().ToString("N");
        try
        {
            File.Move(lockPath, released);
        }
        catch (Exception)
        {
            // Another writer holds it by now, or the folder does not allow deleting: it stays, and the next writer
            // opens it.
            return;
        }

        try
        {
            File.Delete(released);
        }
        catch (Exception)
        {
            // Out of the lock's name already, so no lock; and a name no log or glob matches.
        }
    }

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

    // aud-a56. A folder that cannot be created is the folder refusing this writer its lock file, reported at once as the
    // aud-a51 refusal is, the cause inside.
    private static void EnsureFolder(string path)
    {
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder))
            return;

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new JsonlLockRefusedException(LockPathFor(path), ex);
        }
    }

    /// <summary>
    /// Rotates the full <paramref name="activePath"/> to place 1 (aud-a56). The rotated files below the first free place
    /// each move one place on first; the oldest, at place <paramref name="places"/>, is deleted only when every place is
    /// taken; and no move overwrites a file. Runs under the file's lock.
    /// </summary>
    /// <remarks>
    /// The writers' rotation deleted the oldest file and moved every other one on before it moved the full file. When that
    /// last move failed - the full file held open without delete sharing - each retry deleted one more rotated file, until
    /// none was left. A rotation that fails at its last move now leaves place 1 free, so a retry moves only the full file
    /// and deletes nothing.
    /// </remarks>
    internal static void Rotate(string activePath, int places, Func<int, string> rotatedPath)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(places, 1);

        var free = 1;
        while (free < places && File.Exists(rotatedPath(free)))
            free++;

        if (File.Exists(rotatedPath(free)))
            File.Delete(rotatedPath(free));

        for (var place = free - 1; place >= 1; place--)
            File.Move(rotatedPath(place), rotatedPath(place + 1));

        File.Move(activePath, rotatedPath(1));
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

/// <summary>
/// The log folder refused a JSONL writer its lock file (aud-a51): this account may not create, read or write it, or the
/// open failed for a reason other than another writer's handle. The record is dropped at once; the writer's recovery
/// path reports it with this exception, which names the lock file and the rights it needs, the refusal inside.
/// </summary>
internal sealed class JsonlLockRefusedException(string lockPath, Exception innerException)
    : IOException(
        $"The log folder refused the lock file {lockPath}: {innerException.Message} The record was not written. The account this instance runs as must be able to create files in the log folder and to read and write the files there",
        innerException)
{
    public string LockPath { get; } = lockPath;
}
