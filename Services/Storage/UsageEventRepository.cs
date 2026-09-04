using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// One anonymous usage row. There is deliberately no actor, IP, target or ticket field:
/// the audit log (<see cref="ExchangeAdminWeb.Services.AuditService"/>) stays the record of
/// who did what, and this store answers only "what gets used" (docs/UsageTelemetry-Plan.md).
/// <paramref name="Session"/> is a throwaway per-circuit id, not an identity.
/// </summary>
public sealed record UsageEvent(
    DateTime TsUtc,
    string? Session,
    string Kind,
    string? Module,
    string? Category,
    string? Detail,
    bool? Success);

/// <summary>
/// Per-module aggregate. <paramref name="Module"/> is the grouping key: a catalog module id
/// where one is known, otherwise the raw audit category of an action that maps to no module
/// (reported by name in the Usage view). Rows belonging to neither - the home and
/// access-denied opens - are not aggregated here.
/// </summary>
public sealed record ModuleUsage(string Module, int Opens, int Actions, int FailedActions, int DistinctSessions);

/// <summary>Per-theme aggregate: how many sessions ended the range on that theme.</summary>
public sealed record ThemeUsage(string ThemeId, int Sessions);

/// <summary>Whole-range session shape: how many visits, how broad, how many did nothing.</summary>
public sealed record SessionUsage(int Sessions, double AvgModulesPerSession, int SessionsWithNoAction);

/// <summary>
/// Thin hand-written repository for the anonymous usage-telemetry database
/// (docs/UsageTelemetry-Plan.md, AC1/AC2). Mirrors the Services/Storage/*Repository pattern:
/// Microsoft.Data.Sqlite, no EF, a short-lived connection per operation from its OWN
/// <see cref="SqliteConnectionFactory"/> pointed at config/exchangeadmin-usage.db.
///
/// It is NOT the config store and shares nothing with it: the config database is promoted and
/// backed up, this one is disposable environment-local telemetry that must never be copied
/// between instances (review finding ute-1). There is no migrator and no schema version - the
/// table is created idempotently on first use, so reverting the build simply stops writing here
/// and a stale file on disk is harmless.
/// </summary>
public sealed class UsageEventRepository
{
    private const string CreateSchemaSql =
        """
        CREATE TABLE IF NOT EXISTS usage_event (
            id       INTEGER PRIMARY KEY,
            ts       TEXT    NOT NULL,
            session  TEXT    COLLATE NOCASE,
            kind     TEXT    NOT NULL COLLATE NOCASE,
            module   TEXT    COLLATE NOCASE,
            category TEXT    COLLATE NOCASE,
            detail   TEXT,
            success  INTEGER
        );
        CREATE INDEX IF NOT EXISTS ix_usage_event_ts ON usage_event(ts);
        CREATE INDEX IF NOT EXISTS ix_usage_event_module_kind ON usage_event(module, kind);
        """;

    private readonly SqliteConnectionFactory _factory;
    private readonly object _schemaLock = new();
    private bool _schemaReady;

    public UsageEventRepository(SqliteConnectionFactory usageDb) => _factory = usageDb;

    /// <summary>The resolved absolute path of the usage database file.</summary>
    public string DatabasePath => _factory.DatabasePath;

    // -------------------------------------------------------------------------
    // Writes
    // -------------------------------------------------------------------------

    /// <summary>
    /// Appends one row. Throws on a database fault - every caller reaches this through
    /// <c>UsageTelemetryService.Enqueue</c>, which owns the try/catch, so a telemetry
    /// failure can never surface to the operation that triggered it.
    /// </summary>
    public void Insert(UsageEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        using var connection = OpenReady();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO usage_event (ts, session, kind, module, category, detail, success)
            VALUES ($ts, $session, $kind, $module, $category, $detail, $success);
            """;
        command.Parameters.AddWithValue("$ts", Iso(e.TsUtc));
        command.Parameters.AddWithValue("$session", (object?)e.Session ?? DBNull.Value);
        command.Parameters.AddWithValue("$kind", e.Kind);
        command.Parameters.AddWithValue("$module", (object?)e.Module ?? DBNull.Value);
        command.Parameters.AddWithValue("$category", (object?)e.Category ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", (object?)e.Detail ?? DBNull.Value);
        command.Parameters.AddWithValue("$success", e.Success is null ? DBNull.Value : (e.Success.Value ? 1 : 0));
        command.ExecuteNonQuery();
    }

    /// <summary>Deletes every row stamped before <paramref name="cutoffUtc"/>; returns how many went.</summary>
    public int PruneOlderThan(DateTime cutoffUtc)
    {
        using var connection = OpenReady();
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM usage_event WHERE ts < $cutoff;";
        command.Parameters.AddWithValue("$cutoff", Iso(cutoffUtc));
        return command.ExecuteNonQuery();
    }

    // -------------------------------------------------------------------------
    // Aggregates. Every range is half-open [fromUtc, toUtc): ts is stored as a round-trip
    // ISO-8601 UTC string, which sorts lexically, so text comparison is chronological.
    // -------------------------------------------------------------------------

    /// <summary>
    /// Opens, actions, failed actions and distinct sessions per module over the range.
    /// Grouped by the module id, falling back to the raw audit category for actions that map
    /// to no module; rows with neither (home / access-denied opens) are excluded.
    /// </summary>
    public IReadOnlyList<ModuleUsage> ModuleSummary(DateTime fromUtc, DateTime toUtc)
    {
        using var connection = OpenReady();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT COALESCE(module, category)          AS grp,
                   SUM(CASE WHEN kind = 'open'   THEN 1 ELSE 0 END)                       AS opens,
                   SUM(CASE WHEN kind = 'action' THEN 1 ELSE 0 END)                       AS actions,
                   SUM(CASE WHEN kind = 'action' AND success = 0 THEN 1 ELSE 0 END)       AS failed,
                   COUNT(DISTINCT session)                                                AS sessions
            FROM usage_event
            WHERE ts >= $from AND ts < $to
              AND COALESCE(module, category) IS NOT NULL
              AND COALESCE(module, category) <> ''
            GROUP BY grp
            ORDER BY grp;
            """;
        BindRange(command, fromUtc, toUtc);

        var rows = new List<ModuleUsage>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new ModuleUsage(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4)));
        }
        return rows;
    }

    /// <summary>
    /// Per theme id, the number of distinct sessions whose LATEST theme row in the range names
    /// it. Counting every theme row instead would double-count anyone who toggled themes.
    /// </summary>
    public IReadOnlyList<ThemeUsage> ThemeSummary(DateTime fromUtc, DateTime toUtc)
    {
        using var connection = OpenReady();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT theme, COUNT(*) AS sessions
            FROM (
                SELECT detail AS theme,
                       ROW_NUMBER() OVER (PARTITION BY session ORDER BY ts DESC, id DESC) AS rn
                FROM usage_event
                WHERE kind = 'theme' AND ts >= $from AND ts < $to
                  AND session IS NOT NULL AND detail IS NOT NULL
            )
            WHERE rn = 1
            GROUP BY theme
            ORDER BY sessions DESC, theme;
            """;
        BindRange(command, fromUtc, toUtc);

        var rows = new List<ThemeUsage>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
            rows.Add(new ThemeUsage(reader.GetString(0), reader.GetInt32(1)));
        return rows;
    }

    /// <summary>
    /// Whole-range visit shape. "Acted" is an EXACT join on the session column (review finding
    /// ute-3), never a time window: an action row with no session (a background job) must not
    /// make some unrelated browsing session look active.
    /// </summary>
    public SessionUsage SessionSummary(DateTime fromUtc, DateTime toUtc)
    {
        using var connection = OpenReady();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            WITH s AS (
                SELECT DISTINCT session
                FROM usage_event
                WHERE ts >= $from AND ts < $to AND session IS NOT NULL
            )
            SELECT
                (SELECT COUNT(*) FROM s),
                (SELECT COALESCE(AVG(m), 0.0) FROM (
                    SELECT (SELECT COUNT(DISTINCT e.module)
                            FROM usage_event e
                            WHERE e.session = s.session AND e.kind = 'open'
                              AND e.module IS NOT NULL
                              AND e.ts >= $from AND e.ts < $to) AS m
                    FROM s)),
                (SELECT COUNT(*) FROM s WHERE NOT EXISTS (
                    SELECT 1 FROM usage_event a
                    WHERE a.kind = 'action' AND a.session = s.session
                      AND a.ts >= $from AND a.ts < $to));
            """;
        BindRange(command, fromUtc, toUtc);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return new SessionUsage(0, 0, 0);
        return new SessionUsage(reader.GetInt32(0), reader.GetDouble(1), reader.GetInt32(2));
    }

    // -------------------------------------------------------------------------
    // Schema
    // -------------------------------------------------------------------------

    /// <summary>
    /// Opens a connection with the table guaranteed to exist. The create runs at most once per
    /// process (double-checked under a lock) and is itself idempotent, so a second repository
    /// over the same file - or a second process - is harmless.
    /// </summary>
    private SqliteConnection OpenReady()
    {
        var connection = _factory.Open();
        try
        {
            EnsureSchema(connection);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        return connection;
    }

    private void EnsureSchema(SqliteConnection connection)
    {
        if (_schemaReady)
            return;
        lock (_schemaLock)
        {
            if (_schemaReady)
                return;
            using var command = connection.CreateCommand();
            command.CommandText = CreateSchemaSql;
            command.ExecuteNonQuery();
            _schemaReady = true;
        }
    }

    private static void BindRange(SqliteCommand command, DateTime fromUtc, DateTime toUtc)
    {
        command.Parameters.AddWithValue("$from", Iso(fromUtc));
        command.Parameters.AddWithValue("$to", Iso(toUtc));
    }

    private static string Iso(DateTime value) => value.ToUniversalTime().ToString("O");
}
