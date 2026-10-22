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
/// The one accepted form is an ASCII drive letter, a colon, then <c>\</c> or <c>/</c>, and it is
/// returned with every <c>/</c> turned into <c>\</c> (cdt-35). A network path is refused: SQLite's
/// WAL locking is only sound on a local file system, and the two instances live on one server.
/// Windows reads any two leading separators, in any mix of <c>\</c> and <c>/</c>, as a UNC or
/// device path, and <c>\??\UNC\</c> is the NT namespace's name for a share, so none of them
/// passes. A relative, drive-relative (<c>D:file.db</c>) or driveless rooted path is refused
/// because it would resolve against whichever working directory the host happens to have.
/// </summary>
public static class ConfigStorePath
{
    public const string Key = "ConfigStore:Path";

    /// <summary>The resolved file path and whether startup must refuse when it does not exist.</summary>
    public readonly record struct Resolution(string Path, bool MustExist);

    /// <summary>
    /// Returns the default path (must not exist) when <paramref name="configured"/> is blank,
    /// the configured path with <c>\</c> separators (must exist) when it is a drive-letter local
    /// path, and throws <see cref="InvalidOperationException"/> naming the key for anything else.
    /// </summary>
    public static Resolution Resolve(string? configured, string contentRoot)
    {
        if (string.IsNullOrWhiteSpace(configured))
            return new Resolution(System.IO.Path.Combine(contentRoot, "config", "exchangeadmin.db"), MustExist: false);

        var value = configured.Trim();

        // TRIPWIRE (cdt-30, cdt-35): tools/SqliteConfigBackup.psm1 holds the ops scripts' copy of this
        // rule (marked TRIPWIRE there too) and applies it to the ConfigStore:Path the deploy,
        // promotion, cutover and installer scripts read before they back up or check the
        // database, so they act on exactly the file this method opens, spelled the same way. The two
        // copies are identical: the trim; any two leading separators refused as a network path; then
        // only an ASCII letter, a colon and \ or / accepted; / turned into \. Change one, change the
        // other: tests/ps/SqliteConfigBackup.Tests.ps1 pins these lines and holds the module to them,
        // and ExchangeAdminWeb.Tests/ConfigStorePathTests.cs pins the module's lines and holds this
        // method to them.
        if (value.Length >= 2 && IsSeparator(value[0]) && IsSeparator(value[1]))
        {
            throw new InvalidOperationException(
                $"{Key} is a network (UNC) path: '{value}'. The shared config database must be a local " +
                "file on this server - SQLite's locking is not reliable over a network share.");
        }

        // A drive letter and a separator, not merely rooted or "fully qualified": "D:exchangeadmin.db"
        // names a drive but resolves against that drive's current directory, so two instances could
        // open two different files while both believe they share one (review finding scdi-2), and
        // Path.IsPathFullyQualified also passes \??\UNC\server\share, a network share (cdt-35).
        if (!IsDriveLetterPath(value))
        {
            throw new InvalidOperationException(
                $"{Key} must be an absolute local file path (a drive letter, a colon, then \\ or /, for example D:\\inetpub\\ExchangeAdminWebShared\\config\\exchangeadmin.db); got '{value}'.");
        }

        return new Resolution(value.Replace('/', '\\'), MustExist: true);
    }

    private static bool IsSeparator(char c) => c == '\\' || c == '/';

    private static bool IsDriveLetterPath(string value) =>
        value.Length >= 3 && char.IsAsciiLetter(value[0]) && value[1] == ':' && IsSeparator(value[2]);
}
