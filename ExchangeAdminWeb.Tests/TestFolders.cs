namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-47. Removing a test's folder while a writer may still be finishing with something in it.
/// </summary>
internal static class TestFolders
{
    /// <summary>
    /// Deletes <paramref name="path"/> and everything in it, or answers false when something in it is
    /// still held, so the caller can try again.
    /// </summary>
    /// <remarks>
    /// Windows refuses to delete a held file in two ways: a sharing violation (an <see cref="IOException"/>)
    /// while another handle has it open, and access denied (an <see cref="UnauthorizedAccessException"/>)
    /// while it is delete-pending. The second is what a <c>JsonlFileAppender</c> lock file is for a moment
    /// after its writer moves it aside and deletes it, and a retry that caught only the first let it out and
    /// failed the test it was retrying for.
    /// </remarks>
    internal static bool TryDelete(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
