using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Looks up BitLocker recovery keys in the archive and, when requested, live Active Directory.
/// </summary>
/// <remarks>
/// The archive is the default source: scheduled exports keep normal searches
/// fast and avoid repeated live directory scans. Live Active Directory is an
/// explicit fallback for machines encrypted since the latest export.
///
/// An unreachable selected source is a failure rather than an empty result: on
/// a recovery call, "no key exists" and "I could not look" must not look alike.
/// </remarks>
public sealed class BitLockerRecoveryService
{
    private const string ModuleId = "BitLockerRecovery";
    private const string ArchivePathKey = "ArchiveDatabasePath";
    private const string SearchLimitKey = "SearchResultLimit";
    private const int DefaultSearchLimit = 50;
    private const int MaxSearchLimit = 500;

    private readonly ModuleConfigService _moduleConfig;
    private readonly IBitLockerLiveDirectorySearch _liveDirectory;
    private readonly ILogger<BitLockerRecoveryService> _logger;

    public BitLockerRecoveryService(
        ModuleConfigService moduleConfig,
        IBitLockerLiveDirectorySearch liveDirectory,
        ILogger<BitLockerRecoveryService> logger)
    {
        _moduleConfig = moduleConfig;
        _liveDirectory = liveDirectory;
        _logger = logger;
    }

    /// <summary>
    /// Searches by computer name substring, the way operators search.
    /// </summary>
    public async Task<BitLockerSearchResult> SearchByComputerNameAsync(
        string computerName,
        bool includeLiveAd = false)
    {
        if (string.IsNullOrWhiteSpace(computerName))
        {
            return BitLockerSearchResult.Fail("Enter a computer name to search for.");
        }

        var term = computerName.Trim();
        var limit = ResolveSearchLimit();
        var archive = await SearchArchiveByComputerNameAsync(term, limit);
        if (!archive.Success)
        {
            return archive;
        }

        if (!includeLiveAd)
        {
            return archive;
        }

        var live = await _liveDirectory.SearchByComputerNameAsync(term, limit);
        if (!live.Success)
        {
            return WithLiveDirectoryWarning(archive, live.Error);
        }

        return MergeResults(live, archive, limit);
    }

    /// <summary>
    /// Searches by recovery-screen identifier.
    /// </summary>
    /// <remarks>
    /// The recovery screen may give the caller a full key ID GUID, a short key
    /// ID prefix, or a 48-digit recovery password.
    /// </remarks>
    public async Task<BitLockerSearchResult> SearchByKeyIdAsync(
        string keyId,
        bool includeLiveAd = false)
    {
        var identifier = BitLockerRecoveryIdentifierParser.Parse(keyId);
        if (identifier is null)
        {
            return BitLockerSearchResult.Fail(
                "Enter a recovery key ID or 48-digit recovery password to search for.");
        }

        var limit = ResolveSearchLimit();
        var archive = await SearchArchiveByRecoveryIdentifierAsync(identifier.Value, limit);
        if (!archive.Success)
        {
            return archive;
        }

        if (!includeLiveAd)
        {
            return archive;
        }

        var live = await _liveDirectory.SearchByRecoveryIdentifierAsync(identifier.Value, limit);
        if (!live.Success)
        {
            return WithLiveDirectoryWarning(archive, live.Error);
        }

        return MergeResults(live, archive, limit);
    }

    private Task<BitLockerSearchResult> SearchArchiveByComputerNameAsync(string computerName, int limit)
    {
        // ESCAPE keeps a literal % or _ in a machine name from acting as a
        // wildcard.
        var escaped = computerName
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

        const string sql = """
            SELECT short_computer_name, recovery_password, key_guid, volume_guid,
                   created_utc, first_seen_source, last_seen_in_ad_utc
            FROM recovery_keys
            WHERE short_computer_name LIKE $pattern ESCAPE '\'
            ORDER BY short_computer_name, created_utc
            LIMIT $limit;
            """;

        return RunArchiveQueryAsync(sql, limit, command =>
        {
            command.Parameters.AddWithValue("$pattern", $"%{escaped}%");
        });
    }

    private Task<BitLockerSearchResult> SearchArchiveByRecoveryIdentifierAsync(
        BitLockerRecoveryIdentifier identifier,
        int limit)
    {
        if (identifier.Kind == BitLockerRecoveryIdentifierKind.RecoveryPassword)
        {
            const string passwordSql = """
                SELECT short_computer_name, recovery_password, key_guid, volume_guid,
                       created_utc, first_seen_source, last_seen_in_ad_utc
                FROM recovery_keys
                WHERE recovery_password = $password
                ORDER BY short_computer_name
                LIMIT $limit;
                """;

            return RunArchiveQueryAsync(passwordSql, limit, command =>
            {
                command.Parameters.AddWithValue("$password", identifier.Value);
            });
        }

        const string keyIdSql = """
            SELECT short_computer_name, recovery_password, key_guid, volume_guid,
                   created_utc, first_seen_source, last_seen_in_ad_utc
            FROM recovery_keys
            WHERE key_guid COLLATE NOCASE LIKE $keyIdPattern ESCAPE '\'
            ORDER BY short_computer_name
            LIMIT $limit;
            """;

        return RunArchiveQueryAsync(keyIdSql, limit, command =>
        {
            command.Parameters.AddWithValue(
                "$keyIdPattern",
                $"{EscapeLikePattern(identifier.Value)}%");
        });
    }

    private async Task<BitLockerSearchResult> RunArchiveQueryAsync(
        string sql,
        int limit,
        Action<SqliteCommand> bindParameters)
    {
        // Fail closed on configuration problems. A misconfigured module that
        // silently returned nothing would be indistinguishable from a machine
        // with no key on file.
        if (_moduleConfig.IsModuleCorrupt(ModuleId))
        {
            _logger.LogWarning("BitLockerRecovery configuration is corrupt; refusing to search.");
            return BitLockerSearchResult.Fail(
                "Module configuration is unreadable. Ask an administrator to check the BitLocker Recovery module config.");
        }

        var archivePath = _moduleConfig.GetValue(ModuleId, ArchivePathKey);
        if (string.IsNullOrWhiteSpace(archivePath))
        {
            return BitLockerSearchResult.Fail(
                "The BitLocker key archive is not configured. Set Archive Database Path in Module Config.");
        }

        // SQLite's WAL mode needs shared memory that SMB does not provide, so a
        // reachable share is worse than an unreachable one: reads can succeed
        // intermittently and return torn data while the export writes. Reject
        // it as configuration rather than letting it half-work.
        if (archivePath.StartsWith(@"\\", StringComparison.Ordinal) ||
            archivePath.StartsWith("//", StringComparison.Ordinal))
        {
            _logger.LogWarning("BitLockerRecovery is configured with a UNC archive path; refusing to open it.");
            return BitLockerSearchResult.Fail(
                "The BitLocker key archive is configured on a network share, which cannot be read reliably. " +
                "Ask an administrator to point Archive Database Path at a local copy.");
        }

        if (!File.Exists(archivePath))
        {
            _logger.LogWarning("BitLocker archive not found at the configured path.");
            return BitLockerSearchResult.Fail(
                "The BitLocker key archive is not reachable, so this search could not run. " +
                "An empty result would not mean the key is missing. Ask an administrator to check the archive path.");
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = archivePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            };

            await using var connection = new SqliteConnection(builder.ToString());
            await connection.OpenAsync();

            await using (var pragma = connection.CreateCommand())
            {
                // Wait out the scheduled export's write rather than failing.
                pragma.CommandText = "PRAGMA busy_timeout = 5000;";
                await pragma.ExecuteNonQueryAsync();
            }

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            bindParameters(command);
            command.Parameters.AddWithValue("$limit", limit + 1);

            var rows = new List<BitLockerRecoveryKey>();
            var truncated = false;
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (rows.Count >= limit)
                {
                    truncated = true;
                    break;
                }

                rows.Add(ReadArchiveRow(reader) with { RowId = rows.Count });
            }

            return BitLockerSearchResult.Ok(rows, truncated, limit);
        }
        catch (Exception ex)
        {
            // The message may name paths or schema; the operator gets a safe
            // sentence and the detail goes to the log.
            _logger.LogError(ex, "BitLocker archive query failed.");
            return BitLockerSearchResult.Fail(
                "The BitLocker key archive could not be read, so this search did not run. " +
                "An empty result would not mean the key is missing.");
        }
    }

    private BitLockerSearchResult MergeResults(
        BitLockerLiveDirectorySearchResult live,
        BitLockerSearchResult archive,
        int limit)
    {
        var rows = new List<BitLockerRecoveryKey>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncatedByMerge = false;

        foreach (var row in live.Keys.Concat(archive.Keys))
        {
            var key = $"{row.ComputerName}|{row.RecoveryPassword}";
            if (!seen.Add(key))
            {
                continue;
            }

            if (rows.Count >= limit)
            {
                truncatedByMerge = true;
                break;
            }

            rows.Add(row with { RowId = rows.Count });
        }

        return BitLockerSearchResult.Ok(
            rows,
            live.Truncated || archive.Truncated || truncatedByMerge,
            limit,
            live.Warnings.Concat(archive.Warnings).ToArray());
    }

    private static BitLockerSearchResult WithLiveDirectoryWarning(
        BitLockerSearchResult archive,
        string? liveError)
    {
        var warnings = archive.Warnings.Concat(
            [liveError ?? "Active Directory could not be searched. Archive results are shown."])
            .ToArray();

        return archive with { Warnings = warnings };
    }

    private int ResolveSearchLimit()
    {
        var configured = _moduleConfig.GetValue(ModuleId, SearchLimitKey);
        if (!int.TryParse(configured, out var limit) || limit <= 0)
        {
            return DefaultSearchLimit;
        }

        // A negative or zero LIMIT means "unlimited" to SQLite, and an
        // unbounded value would pull the whole archive into this process.
        return Math.Min(limit, MaxSearchLimit);
    }

    private static BitLockerRecoveryKey ReadArchiveRow(DbDataReader reader) => new()
    {
        RowId = 0,
        ComputerName = reader.GetString(0),
        RecoveryPassword = reader.GetString(1),
        KeyId = reader.IsDBNull(2) ? null : reader.GetString(2),
        VolumeId = reader.IsDBNull(3) ? null : reader.GetString(3),
        CreatedUtc = ParseUtc(reader.IsDBNull(4) ? null : reader.GetString(4)),
        FirstSeenSource = reader.GetString(5),
        LastSeenInAdUtc = ParseUtc(reader.IsDBNull(6) ? null : reader.GetString(6)),
        ResultSource = BitLockerRecoveryKeySource.Archive,
    };

    private static DateTime? ParseUtc(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return DateTime.TryParse(
            value,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var parsed)
            ? parsed
            : null;
    }

    private static string EscapeLikePattern(string value) => value
        .Replace("\\", "\\\\")
        .Replace("%", "\\%")
        .Replace("_", "\\_");
}

public enum BitLockerRecoveryKeySource
{
    ActiveDirectory,
    Archive,
}

/// <summary>One recovery key as shown to an operator.</summary>
public sealed record BitLockerRecoveryKey
{
    /// <summary>
    /// Position of this row within its result set, used by the UI to track
    /// which rows have been revealed.
    /// </summary>
    /// <remarks>
    /// It exists because no natural key is reliably unique here: historical
    /// rows carry no key ID, and two keys for the same machine can share a
    /// created timestamp or have none. Keying reveal state on those would let
    /// one audited reveal disclose several keys at once. The recovery password
    /// is never used as an identifier.
    /// </remarks>
    public required int RowId { get; init; }

    public required string ComputerName { get; init; }
    public required string RecoveryPassword { get; init; }
    public string? KeyId { get; init; }
    public string? VolumeId { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public required string FirstSeenSource { get; init; }
    public DateTime? LastSeenInAdUtc { get; init; }
    public required BitLockerRecoveryKeySource ResultSource { get; init; }

    public bool FoundInLiveDirectory => ResultSource == BitLockerRecoveryKeySource.ActiveDirectory;

    public bool EverSeenInDirectory => FoundInLiveDirectory || LastSeenInAdUtc.HasValue;

    public string StatusLabel => FoundInLiveDirectory
        ? "Live AD"
        : LastSeenInAdUtc.HasValue
            ? "Archive"
            : "Archive only";
}

/// <summary>Outcome of a search, including why it could not run.</summary>
public sealed record BitLockerSearchResult
{
    public required bool Success { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<BitLockerRecoveryKey> Keys { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public bool IsPartial => Warnings.Count > 0;

    /// <summary>Results were capped, so more keys may exist.</summary>
    public bool Truncated { get; init; }

    public int Limit { get; init; }

    public static BitLockerSearchResult Ok(
        IReadOnlyList<BitLockerRecoveryKey> keys,
        bool truncated,
        int limit,
        IReadOnlyList<string>? warnings = null) =>
        new()
        {
            Success = true,
            Keys = keys,
            Truncated = truncated,
            Limit = limit,
            Warnings = warnings ?? [],
        };

    public static BitLockerSearchResult Fail(string error) =>
        new() { Success = false, Error = error };
}
