using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-47. The folder-delete retry treats a held file as "not yet", whichever way Windows refuses it.
/// </summary>
/// <remarks>
/// The stand-in is a lock-named file held by this test: delete-pending through its own handle, which is
/// what the appender's moved-aside lock file is for a moment, or simply open with no sharing.
/// </remarks>
public sealed class TestFoldersTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "ExchangeAdminWebTests", Guid.NewGuid().ToString("N"));

    public TestFoldersTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch
        {
            // Best effort; a handle this class held is closed by now.
        }
    }

    private string LockFile() =>
        Path.Combine(_folder, "exchangeadmin_20261023_extended.jsonl.lock." + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AFolderHoldingADeletePendingLockFile_IsNotYetDeletable_AndIsDeletedOnceItIsReleased()
    {
        using (HoldDeletePending(LockFile()))
        {
            // The stand-in reproduces the failure itself, so a change in how Windows answers would show here
            // rather than make the next assertion vacuous.
            Assert.Throws<UnauthorizedAccessException>(() => Directory.Delete(_folder, recursive: true));
            Assert.False(TestFolders.TryDelete(_folder));
            Assert.True(Directory.Exists(_folder));
        }

        Assert.True(SpinWait.SpinUntil(() => TestFolders.TryDelete(_folder), TimeSpan.FromSeconds(30)));
        Assert.False(Directory.Exists(_folder));
    }

    [Fact]
    public void AFolderHoldingAnOpenLockFile_IsNotYetDeletable_AndIsDeletedOnceItIsReleased()
    {
        using (new FileStream(LockFile(), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => Directory.Delete(_folder, recursive: true));
            Assert.False(TestFolders.TryDelete(_folder));
        }

        Assert.True(SpinWait.SpinUntil(() => TestFolders.TryDelete(_folder), TimeSpan.FromSeconds(30)));
        Assert.False(Directory.Exists(_folder));
    }

    // ---- the delete-pending stand-in ------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        [MarshalAs(UnmanagedType.U1)]
        public bool DeleteFile;
    }

    private const int FileDispositionInfoClass = 4;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file, int informationClass, ref FileDispositionInfo information, int size);

    /// <summary>
    /// A new file at <paramref name="path"/>, marked delete-pending at once through its own handle - the classic
    /// delete disposition, not the POSIX one a plain File.Delete uses - and kept so until the stream is disposed.
    /// DeleteOnClose is only what gives the handle the delete access the disposition needs.
    /// </summary>
    private static FileStream HoldDeletePending(string path)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.DeleteOnClose);
        var information = new FileDispositionInfo { DeleteFile = true };
        if (!SetFileInformationByHandle(stream.SafeFileHandle, FileDispositionInfoClass, ref information, Marshal.SizeOf<FileDispositionInfo>()))
        {
            var error = Marshal.GetLastWin32Error();
            stream.Dispose();
            throw new IOException($"SetFileInformationByHandle failed with error {error}.");
        }

        return stream;
    }
}
