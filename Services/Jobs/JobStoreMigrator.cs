using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// Creates and evolves the SEPARATE operational jobs database schema (config/exchangeadmin-jobs.db),
/// distinct from the config database. Job state is environment-local, high-churn, and MUST NEVER be
/// promoted dev→prod or mixed into the config store (owner, 2026-07-02; docs/BulkJobRunner-Plan.md).
/// It reuses <see cref="SqliteConnectionFactory"/> (path-parameterized, WAL + busy timeout) but keeps
/// its own <c>PRAGMA user_version</c> cursor so the two databases evolve independently.
///
/// Same discipline as <see cref="ConfigStoreMigrator"/>: ordered, idempotent steps applied in order,
/// each in its own transaction; never edit or reorder an existing step — append only.
/// </summary>
public sealed class JobStoreMigrator
{
    private readonly SqliteConnectionFactory _factory;

    private static readonly string[] Migrations =
    [
        // v1 — initial jobs schema.
        //
        // status/module_id/job_type are COLLATE NOCASE for the same reason the config store uses it
        // (OrdinalIgnoreCase comparisons in the service layer). Row results cascade-delete with their
        // job so pruning a job cleans up its rows in one step.
        """
        CREATE TABLE IF NOT EXISTS bulk_job (
            id                   TEXT PRIMARY KEY COLLATE NOCASE,
            module_id            TEXT NOT NULL COLLATE NOCASE,
            job_type             TEXT NOT NULL COLLATE NOCASE,
            status               TEXT NOT NULL COLLATE NOCASE,
            submitted_by         TEXT NOT NULL,
            submitted_by_display TEXT,
            submitted_ip         TEXT NOT NULL,
            ticket               TEXT,
            auth_snapshot_json   TEXT,
            payload_json         TEXT NOT NULL,
            submitted_at         TEXT NOT NULL,
            started_at           TEXT,
            finished_at          TEXT,
            heartbeat_at         TEXT,
            total_rows           INTEGER NOT NULL DEFAULT 0,
            processed_rows       INTEGER NOT NULL DEFAULT 0,
            success_count        INTEGER NOT NULL DEFAULT 0,
            partial_count        INTEGER NOT NULL DEFAULT 0,
            failed_count         INTEGER NOT NULL DEFAULT 0,
            cancel_requested     INTEGER NOT NULL DEFAULT 0,
            message              TEXT
        );

        CREATE INDEX IF NOT EXISTS ix_bulk_job_status ON bulk_job (status);
        CREATE INDEX IF NOT EXISTS ix_bulk_job_submitted_at ON bulk_job (submitted_at);

        CREATE TABLE IF NOT EXISTS bulk_job_row (
            job_id      TEXT NOT NULL COLLATE NOCASE,
            row_index   INTEGER NOT NULL,
            target      TEXT NOT NULL,
            status      TEXT NOT NULL COLLATE NOCASE,
            message     TEXT,
            recorded_at TEXT NOT NULL,
            PRIMARY KEY (job_id, row_index),
            FOREIGN KEY (job_id) REFERENCES bulk_job (id) ON DELETE CASCADE
        );
        """,
    ];

    /// <summary>The schema version this build expects (the count of migration steps).</summary>
    public static int TargetVersion => Migrations.Length;

    public JobStoreMigrator(SqliteConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// Brings the jobs database up to <see cref="TargetVersion"/>. Idempotent and safe on every
    /// startup. Returns the version afterward. Fails fast if the DB was migrated by a newer build.
    /// </summary>
    public int Migrate()
    {
        using var connection = _factory.Open();

        var current = GetUserVersion(connection);

        if (current > Migrations.Length)
        {
            throw new InvalidOperationException(
                $"Jobs database schema version {current} is newer than this build supports " +
                $"(max {Migrations.Length}). Deploy a build at or above the schema version, or " +
                "delete the environment-local jobs database (it is never promoted and holds no config).");
        }

        for (var version = current; version < Migrations.Length; version++)
        {
            using var transaction = connection.BeginTransaction();

            using (var step = connection.CreateCommand())
            {
                step.Transaction = transaction;
                step.CommandText = Migrations[version];
                step.ExecuteNonQuery();
            }

            using (var bump = connection.CreateCommand())
            {
                bump.Transaction = transaction;
                bump.CommandText = $"PRAGMA user_version = {version + 1};";
                bump.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return GetUserVersion(connection);
    }

    private static int GetUserVersion(Microsoft.Data.Sqlite.SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
