namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Disposable temp directory for storage tests, with a convenience path for the config DB.
/// </summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "ExchangeAdminWebTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Path to a config database file inside this temp directory.</summary>
    public string DbPath => System.IO.Path.Combine(Path, "exchangeadmin.db");

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch
        {
            // Best-effort cleanup; WAL/SHM handles may briefly linger on Windows.
        }
    }
}
