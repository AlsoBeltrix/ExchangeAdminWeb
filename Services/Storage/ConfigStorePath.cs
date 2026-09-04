namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// Resolves where the config database lives (docs/SharedConfigDb-Plan.md, AC1). Two cases:
///
/// - <c>ConfigStore:Path</c> is blank or absent: today's single-instance default,
///   <c>&lt;ContentRoot&gt;\config\exchangeadmin.db</c>, created on first start if missing.
/// - <c>ConfigStore:Path</c> is set: an absolute local file path shared by both instances on
///   this server (decision 2026-09-04). The file MUST already exist - it is opened without
///   create, so a mistyped key, a moved file or a deleted file stops the app instead of
///   starting it on a brand-new empty database seeded at defaults that would serve with no
///   protected principals and no section access while looking healthy (review finding scd-1).
///
/// A UNC path is refused: SQLite's WAL locking is only sound on a local file system, and the
/// two instances live on one server. A relative path is refused because it would resolve
/// against whichever working directory the host happens to have.
/// </summary>
public static class ConfigStorePath
{
    public const string Key = "ConfigStore:Path";

    /// <summary>The resolved file path and whether startup must refuse when it does not exist.</summary>
    public readonly record struct Resolution(string Path, bool MustExist);

    /// <summary>
    /// Returns the default path (must not exist) when <paramref name="configured"/> is blank,
    /// the configured path (must exist) when it is an absolute local path, and throws
    /// <see cref="InvalidOperationException"/> naming the key for a UNC or relative value.
    /// </summary>
    public static Resolution Resolve(string? configured, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return new Resolution(System.IO.Path.Combine(contentRoot, "config", "exchangeadmin.db"), MustExist: false);

        var value = configured.Trim();

        if (value.StartsWith(@"\\", StringComparison.Ordinal) || value.StartsWith("//", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{Key} is a network (UNC) path: '{value}'. The shared config database must be a local " +
                "file on this server - SQLite's locking is not reliable over a network share.");
        }

        if (!System.IO.Path.IsPathRooted(value) || System.IO.Path.GetPathRoot(value) is null or "" or @"\" or "/")
        {
            throw new InvalidOperationException(
                $"{Key} must be an absolute local file path (for example D:\\inetpub\\ExchangeAdminWebShared\\config\\exchangeadmin.db); got '{value}'.");
        }

        return new Resolution(value, MustExist: true);
    }
}
