using System.Diagnostics;
using System.Text;

namespace ExchangeAdminWeb.Services;

internal static class JsonlFileAppender
{
    internal static readonly TimeSpan DefaultLockWait = TimeSpan.FromSeconds(2);

    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;
    private const int LongestPauseMilliseconds = 16;

    internal static string LockPathFor(string path) => path + ".lock";

    internal static void Append(
        string path,
        string json,
        TimeSpan lockWait,
        Action rotateIfFull,
        Action<Stream, byte[]> writeLineBytes,
        Action repaired,
        Action<Exception> truncationFailed)
    {
        using var held = Lock(path, lockWait);
        rotateIfFull();

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
                refusal = ex;
            }

            if (clock.Elapsed >= wait)
                throw new JsonlLockTimeoutException(lockPath, wait, refusal);

            Thread.Sleep(pause);
            pause = Math.Min(pause * 2, LongestPauseMilliseconds);
        }
    }

    private static bool IsHeldElsewhere(IOException ex) =>
        (ex.HResult & 0xFFFF) is ErrorSharingViolation or ErrorLockViolation;

    private static bool EndsWithNewline(FileStream file)
    {
        file.Seek(-1, SeekOrigin.End);
        return file.ReadByte() == '\n';
    }

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

internal sealed class JsonlLockTimeoutException(string lockPath, TimeSpan wait, Exception innerException)
    : IOException(
        $"Another writer held the lock {lockPath} for longer than {wait.TotalMilliseconds:0} ms; the record was not written, so that it is never written at an offset read without the lock",
        innerException)
{
    public string LockPath { get; } = lockPath;
}
