namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// Shared helper for the one-time JSON/text → SQLite import each Phase B store performs.
/// After a legacy file's contents are imported into the DB, the file is renamed aside so it
/// can never be re-read or re-imported (import-once-then-archive, SqliteConfigStore-Plan §4).
/// Archiving rather than deleting preserves a manual rollback path until the first prod cycle
/// proves the importer.
/// </summary>
public static class LegacyConfigImport
{
    /// <summary>
    /// Renames <paramref name="path"/> to <c>{path}.imported-{timestamp}</c>. No-op if the file
    /// does not exist. Never throws on a failed archive — the import itself has already
    /// committed to the DB, and a left-behind legacy file is harmless because the DB row now
    /// takes precedence (the importer only runs when the DB has no value).
    /// </summary>
    public static void ArchiveFile(string path, ILogger? logger = null)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var archived = $"{path}.imported-{DateTime.UtcNow:yyyyMMddHHmmss}";
            File.Move(path, archived, overwrite: true);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to archive imported legacy config file {Path}", path);
        }
    }
}
