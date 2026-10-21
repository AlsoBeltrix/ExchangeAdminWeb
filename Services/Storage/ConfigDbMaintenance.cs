using System.Globalization;
using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Services.Storage;

/// <summary>An expected failure of a db-mode operation; the CLI reports its message and exits 2.</summary>
public sealed class ConfigDbMaintenanceException : Exception
{
    public ConfigDbMaintenanceException(string message)
        : base(message)
    {
    }

    public ConfigDbMaintenanceException(string message, Exception inner)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The operations behind the app's <c>db</c> command-line mode (docs/ConfigDbToolInApp-Plan.md
/// section 3.1): the verified backup, integrity check and active-jobs query the ops scripts used to
/// run through sqlite3.exe. No console I/O, no DI, no configuration.
/// </summary>
/// <remarks>
/// Connections are raw, unpooled and opened with explicit modes. <see cref="SqliteConnectionFactory"/>
/// is deliberately not used: it creates directories and switches the file to WAL, and neither may
/// happen to a database being backed up or to the copy. Unpooled so a dispose really releases the
/// file, which the partial-copy cleanup depends on.
/// </remarks>
public static class ConfigDbMaintenance
{
    /// <summary>
    /// The exact query tools/JobStateWarning.psm1 ran through sqlite3.exe, so the five columns and
    /// their order (status, kind, submitted by, ticket, processed/total) are unchanged for its parser.
    /// </summary>
    public const string ActiveJobsQuery =
        "SELECT status, job_type, submitted_by, COALESCE(ticket,''), processed_rows || '/' || total_rows " +
        "FROM bulk_job WHERE status IN ('Running','Queued') ORDER BY submitted_at;";

    private const int IntegrityLinesInMessage = 10;

    /// <summary>
    /// Writes a consistent copy of <paramref name="sourcePath"/> to <paramref name="destPath"/> with
    /// <c>VACUUM INTO</c> and integrity-checks the copy. Returns the full destination path. Throws on
    /// any failure, and on a failure after the copy started no file is left at the destination: a
    /// file there must only ever mean a verified snapshot.
    /// </summary>
    public static string Backup(string sourcePath, string destPath)
    {
        var source = Path.GetFullPath(sourcePath);
        var dest = Path.GetFullPath(destPath);

        if (!File.Exists(source))
            throw new ConfigDbMaintenanceException($"Source database not found: {source}");

        // A stale -wal or -journal beside a missing destination is refused too: the new copy
        // could otherwise be opened together with a journal that belongs to some other file.
        foreach (var existing in SidecarSet(dest))
        {
            if (File.Exists(existing) || Directory.Exists(existing))
                throw new ConfigDbMaintenanceException($"Destination already exists, refusing to overwrite: {existing}");
        }

        try
        {
            // Read-only, so closing this connection can never checkpoint a live WAL into the
            // source file. VACUUM INTO takes no parameter, hence the quoted literal.
            using (var connection = OpenReadOnly(source))
            {
                using var vacuum = connection.CreateCommand();
                vacuum.CommandText = "VACUUM INTO '" + dest.Replace("'", "''", StringComparison.Ordinal) + "';";
                vacuum.ExecuteNonQuery();
            }

            var integrity = RunIntegrityCheck(dest);
            if (!IsOk(integrity))
            {
                throw new ConfigDbMaintenanceException(
                    $"Backup copy failed integrity check: {Summarize(integrity)}. The source at {source} may be corrupt.");
            }

            return dest;
        }
        catch
        {
            DeletePartial(dest);
            throw;
        }
    }

    /// <summary>Runs <c>PRAGMA integrity_check</c> read-only; throws unless the result is exactly "ok".</summary>
    public static void Check(string dbPath)
    {
        var path = Path.GetFullPath(dbPath);
        if (!File.Exists(path))
            throw new ConfigDbMaintenanceException($"Database not found: {path}");

        var integrity = RunIntegrityCheck(path);
        if (!IsOk(integrity))
            throw new ConfigDbMaintenanceException($"Integrity check failed on {path}: {Summarize(integrity)}");
    }

    /// <summary>
    /// Running and Queued bulk jobs as pipe-delimited lines, no header, NULL as empty - the shape
    /// sqlite3.exe printed. Reads everything before returning, so a failure prints no partial list.
    /// </summary>
    public static IReadOnlyList<string> ActiveJobs(string jobsDbPath)
    {
        var path = Path.GetFullPath(jobsDbPath);
        if (!File.Exists(path))
            throw new ConfigDbMaintenanceException($"Jobs database not found: {path}");

        var rows = new List<string>();
        using var connection = OpenReadOnly(path);
        using var command = connection.CreateCommand();
        command.CommandText = ActiveJobsQuery;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var fields = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
            {
                fields[i] = reader.IsDBNull(i)
                    ? string.Empty
                    : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty;
            }

            rows.Add(string.Join('|', fields));
        }

        return rows;
    }

    /// <summary>Creates an empty database file and returns its full path; refuses an existing one.</summary>
    public static string Init(string dbPath)
    {
        var path = Path.GetFullPath(dbPath);
        if (File.Exists(path) || Directory.Exists(path))
            throw new ConfigDbMaintenanceException($"File already exists, refusing to initialise: {path}");

        using var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadWriteCreate));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        command.ExecuteScalar();
        return path;
    }

    private static SqliteConnection OpenReadOnly(string path)
    {
        var connection = new SqliteConnection(ConnectionString(path, SqliteOpenMode.ReadOnly));
        try
        {
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = "PRAGMA busy_timeout=5000;";
            pragma.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static string ConnectionString(string path, SqliteOpenMode mode) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString();

    private static List<string> RunIntegrityCheck(string path)
    {
        var lines = new List<string>();
        using var connection = OpenReadOnly(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
            lines.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        return lines;
    }

    private static bool IsOk(List<string> integrity) =>
        integrity.Count == 1 && string.Equals(integrity[0].Trim(), "ok", StringComparison.Ordinal);

    private static string Summarize(List<string> integrity)
    {
        if (integrity.Count == 0)
            return "(no result)";
        var shown = string.Join("; ", integrity.Take(IntegrityLinesInMessage));
        return integrity.Count > IntegrityLinesInMessage
            ? $"{shown}; ... ({integrity.Count} lines)"
            : shown;
    }

    private static string[] SidecarSet(string dest) =>
        [dest, dest + "-journal", dest + "-wal", dest + "-shm"];

    private static void DeletePartial(string dest)
    {
        foreach (var path in SidecarSet(dest))
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // Best effort; the operation is already failing with its own message.
            }
            catch (UnauthorizedAccessException)
            {
                // As above.
            }
        }
    }
}
