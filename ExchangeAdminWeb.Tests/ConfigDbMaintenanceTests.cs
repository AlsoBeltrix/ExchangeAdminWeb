using System.Text;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Tests;

internal static class ConfigDbTestFiles
{
    public static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWriteCreate)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    public static void Exec(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static long Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    public static long CountRows(string path, string table)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly);
        return Scalar(connection, $"SELECT COUNT(*) FROM {table};");
    }

    public static string Integrity(string path)
    {
        using var connection = Open(path, SqliteOpenMode.ReadOnly);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();
        var lines = new List<string>();
        while (reader.Read())
            lines.Add(reader.GetString(0));
        return string.Join("\n", lines);
    }

    public static void CreateCorrupt(string path)
    {
        long indexRoot;
        long pageSize;
        using (var connection = Open(path))
        {
            Exec(connection, "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT NOT NULL);");
            Exec(connection, "CREATE INDEX ix_t_v ON t (v);");
            for (var i = 0; i < 40; i++)
                Exec(connection, $"INSERT INTO t (v) VALUES ('marker-{i:D4}');");
            indexRoot = Scalar(connection, "SELECT rootpage FROM sqlite_master WHERE name = 'ix_t_v';");
            pageSize = Scalar(connection, "PRAGMA page_size;");
        }

        var bytes = File.ReadAllBytes(path);
        var start = (int)((indexRoot - 1) * pageSize);
        var end = (int)Math.Min(bytes.Length, start + pageSize);
        var needle = Encoding.ASCII.GetBytes("marker");
        var replacement = Encoding.ASCII.GetBytes("MARKER");
        var replaced = 0;
        for (var i = start; i <= end - needle.Length; i++)
        {
            if (bytes.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                replacement.CopyTo(bytes, i);
                replaced++;
            }
        }

        if (replaced == 0)
            throw new InvalidOperationException("corruption helper found nothing to overwrite in the index page");
        File.WriteAllBytes(path, bytes);
    }
}

public class ConfigDbMaintenanceTests
{
    [Fact]
    public void Backup_OfWalDatabaseWithOpenUncommittedWriter_CopiesOnlyCommittedRowsAndPassesIntegrity()
    {
        using var temp = new TempDir();
        var source = temp.DbPath;
        var dest = Path.Combine(temp.Path, "backup.db");

        using var writer = ConfigDbTestFiles.Open(source);
        ConfigDbTestFiles.Exec(writer, "PRAGMA journal_mode=WAL;");
        ConfigDbTestFiles.Exec(writer, "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT);");
        for (var i = 0; i < 3; i++)
            ConfigDbTestFiles.Exec(writer, $"INSERT INTO t (v) VALUES ('committed-{i}');");

        using var open = writer.BeginTransaction();
        for (var i = 0; i < 2; i++)
            ConfigDbTestFiles.Exec(writer, $"INSERT INTO t (v) VALUES ('uncommitted-{i}');", open);

        var written = ConfigDbMaintenance.Backup(source, dest);

        Assert.Equal(Path.GetFullPath(dest), written);
        Assert.Equal("ok", ConfigDbTestFiles.Integrity(dest));
        Assert.Equal(3, ConfigDbTestFiles.CountRows(dest, "t"));
        open.Rollback();
    }

    [Fact]
    public void Backup_LeavesSourceBytesAndLastWriteTimeUnchanged()
    {
        // A WAL database whose last writer went away without checkpointing: the committed rows
        // live only in the -wal file. A read-write open would checkpoint them into the main file
        // on close; the backup must not.
        using var temp = new TempDir();
        var source = temp.DbPath;
        var dest = Path.Combine(temp.Path, "backup.db");

        var writer = ConfigDbTestFiles.Open(source);
        ConfigDbTestFiles.Exec(writer, "PRAGMA journal_mode=WAL;");
        ConfigDbTestFiles.Exec(writer, "PRAGMA wal_autocheckpoint=0;");
        ConfigDbTestFiles.Exec(writer, "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT);");
        for (var i = 0; i < 5; i++)
            ConfigDbTestFiles.Exec(writer, $"INSERT INTO t (v) VALUES ('row-{i}');");

        using (var reader = ConfigDbTestFiles.Open(source, SqliteOpenMode.ReadOnly))
        {
            Assert.Equal(5, ConfigDbTestFiles.Scalar(reader, "SELECT COUNT(*) FROM t;"));
            writer.Dispose();
        }

        Assert.True(File.Exists(source + "-wal") && new FileInfo(source + "-wal").Length > 0,
            "precondition: committed rows must still be in an un-checkpointed WAL");

        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, past);
        var before = File.ReadAllBytes(source);

        ConfigDbMaintenance.Backup(source, dest);

        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Equal(past, File.GetLastWriteTimeUtc(source));
        Assert.Equal(5, ConfigDbTestFiles.CountRows(dest, "t"));
    }

    [Fact]
    public void Backup_CopyHasNoSidecarsAndIsNotInWalMode()
    {
        using var temp = new TempDir();
        var source = temp.DbPath;
        var dest = Path.Combine(temp.Path, "backup.db");

        using (var writer = ConfigDbTestFiles.Open(source))
        {
            ConfigDbTestFiles.Exec(writer, "PRAGMA journal_mode=WAL;");
            ConfigDbTestFiles.Exec(writer, "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT);");
            ConfigDbTestFiles.Exec(writer, "INSERT INTO t (v) VALUES ('a');");

            ConfigDbMaintenance.Backup(source, dest);
        }

        Assert.False(File.Exists(dest + "-wal"), "the copy must not have a -wal sidecar");
        Assert.False(File.Exists(dest + "-shm"), "the copy must not have a -shm sidecar");

        var header = new byte[20];
        using (var stream = File.OpenRead(dest))
            stream.ReadExactly(header);
        Assert.Equal(1, header[18]);
        Assert.Equal(1, header[19]);
    }

    [Fact]
    public void Backup_OfCorruptSource_ThrowsAndLeavesNoDestination()
    {
        using var temp = new TempDir();
        var source = temp.DbPath;
        var dest = Path.Combine(temp.Path, "backup.db");
        ConfigDbTestFiles.CreateCorrupt(source);
        Assert.NotEqual("ok", ConfigDbTestFiles.Integrity(source));

        Assert.ThrowsAny<Exception>(() => ConfigDbMaintenance.Backup(source, dest));

        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void Backup_WhenDestinationAppearsAfterPrecheck_ThrowsAndLeavesItUntouched()
    {
        // cdt-1: a second run passes the precheck, then the first run's snapshot lands at dest.
        // The second run must fail without deleting or replacing that snapshot.
        using var temp = new TempDir();
        var source = temp.DbPath;
        var dest = Path.Combine(temp.Path, "backup.db");
        CreateSmallDatabase(source);
        var otherRunsBytes = Encoding.ASCII.GetBytes("another run's verified snapshot");

        Assert.Throws<ConfigDbMaintenanceException>(() =>
            ConfigDbMaintenance.Backup(source, dest, d => File.WriteAllBytes(d, otherRunsBytes)));

        Assert.True(File.Exists(dest), "the file another run wrote must survive");
        Assert.Equal(otherRunsBytes, File.ReadAllBytes(dest));
        Assert.Empty(TempLeftovers(temp.Path));
    }

    [Fact]
    public void Backup_OfCorruptSource_LeavesNoTempFile()
    {
        using var temp = new TempDir();
        var source = temp.DbPath;
        var dest = Path.Combine(temp.Path, "backup.db");
        ConfigDbTestFiles.CreateCorrupt(source);

        Assert.ThrowsAny<Exception>(() => ConfigDbMaintenance.Backup(source, dest));

        Assert.Empty(TempLeftovers(temp.Path));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void Backup_OnSuccess_AddsOnlyTheDestination()
    {
        using var temp = new TempDir();
        var source = temp.DbPath;
        var dest = Path.Combine(temp.Path, "backup.db");
        CreateSmallDatabase(source);
        var before = Directory.GetFileSystemEntries(temp.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);

        ConfigDbMaintenance.Backup(source, dest);

        var added = Directory.GetFileSystemEntries(temp.Path).Where(p => !before.Contains(p)).ToList();
        Assert.Equal([Path.GetFullPath(dest)], added);
    }

    private static void CreateSmallDatabase(string path)
    {
        using var connection = ConfigDbTestFiles.Open(path);
        ConfigDbTestFiles.Exec(connection, "CREATE TABLE t (id INTEGER PRIMARY KEY, v TEXT);");
        ConfigDbTestFiles.Exec(connection, "INSERT INTO t (v) VALUES ('a');");
    }

    private static string[] TempLeftovers(string directory) =>
        Directory.GetFileSystemEntries(directory, "*.tmp-*");

    [Fact]
    public void Backup_WhenSourceMissing_Throws()
    {
        using var temp = new TempDir();
        var dest = Path.Combine(temp.Path, "backup.db");

        Assert.Throws<ConfigDbMaintenanceException>(() => ConfigDbMaintenance.Backup(temp.DbPath, dest));
        Assert.False(File.Exists(temp.DbPath));
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public void Init_CreatesAnEmptyDatabaseThatPassesIntegrity()
    {
        using var temp = new TempDir();

        var created = ConfigDbMaintenance.Init(temp.DbPath);

        Assert.Equal(Path.GetFullPath(temp.DbPath), created);
        Assert.True(File.Exists(temp.DbPath));
        Assert.Equal("ok", ConfigDbTestFiles.Integrity(temp.DbPath));
    }

    [Fact]
    public void Init_RefusesAnExistingFile()
    {
        using var temp = new TempDir();
        File.WriteAllText(temp.DbPath, "not a database");

        Assert.Throws<ConfigDbMaintenanceException>(() => ConfigDbMaintenance.Init(temp.DbPath));
        Assert.Equal("not a database", File.ReadAllText(temp.DbPath));
    }

    // ---- aud-a67 item 2: the db mode waits the one busy wait, not the provider's retry around it ----------------

    [Fact]
    public void OpenReadOnly_TakesTheOneBusyWait_AsBothItsBusyTimeoutAndItsCommandTimeout()
    {
        // The defect: busy_timeout was set and the connection's command timeout was not, so the provider ran a busy
        // statement again, each run another full busy_timeout, until its own 30 seconds had passed (the aud-a41 shape).
        using var temp = new TempDir();
        ConfigDbMaintenance.Init(temp.DbPath);

        using var connection = ConfigDbMaintenance.OpenReadOnly(Path.GetFullPath(temp.DbPath));
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";

        Assert.Equal((long)SqliteConnectionFactory.BusyTimeout.TotalMilliseconds, Convert.ToInt64(command.ExecuteScalar()));
        Assert.Equal((int)SqliteConnectionFactory.BusyTimeout.TotalSeconds, connection.DefaultTimeout);
        Assert.Equal(connection.DefaultTimeout, command.CommandTimeout);
    }

    [Fact]
    public void Check_OfADatabaseHeldByAnotherConnection_GivesUp_WithoutTheProvidersThirtySecondRetry()
    {
        // Measured against the built app before this fix: db check sat for 30.5 s on a database another process held, where
        // docs/ConfigDbToolInApp-Plan.md documents 5; after it, 7.8 to 9.4 s - the open and the statement each wait once.
        // The ceiling here is loose on purpose (aud-a67 item 7): it fails the 30-second retry loop and nothing else.
        using var temp = new TempDir();
        using (var holder = HoldExclusively(temp.DbPath))
        {
            var waited = System.Diagnostics.Stopwatch.StartNew();
            // The provider's own busy failure, which the CLI's Execute turns into "error: ..." and exit 2.
            var refused = Assert.Throws<SqliteException>(() => ConfigDbMaintenance.Check(temp.DbPath));
            waited.Stop();

            Assert.True(ConfigStoreBusy.IsBusy(refused), $"not SQLITE_BUSY: {refused.SqliteErrorCode} {refused.Message}");
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(25),
                $"the check waited {waited.Elapsed.TotalSeconds:F1} s: the provider's retry loop, not the one busy wait");
            GC.KeepAlive(holder);
        }

        // Released, the same check answers.
        ConfigDbMaintenance.Check(temp.DbPath);
    }

    /// <summary>
    /// Holds <paramref name="path"/> so no other connection may even read it: a write transaction whose page cache is one
    /// page, so SQLite spills to the file and takes an EXCLUSIVE lock for the rest of the transaction. A plain
    /// BEGIN IMMEDIATE takes RESERVED, which a reader passes.
    /// </summary>
    private static SqliteConnection HoldExclusively(string path)
    {
        var connection = ConfigDbTestFiles.Open(path);
        ConfigDbTestFiles.Exec(connection, "PRAGMA cache_size=1;");
        ConfigDbTestFiles.Exec(connection, "CREATE TABLE IF NOT EXISTS held (k TEXT PRIMARY KEY, v TEXT);");
        ConfigDbTestFiles.Exec(connection, """
            WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i+1 FROM n WHERE i < 20000)
            INSERT INTO held SELECT 'k' || i, 'v' || i FROM n;
            """);
        ConfigDbTestFiles.Exec(connection, "BEGIN IMMEDIATE;");
        ConfigDbTestFiles.Exec(connection, "UPDATE held SET v = v || '-held';");
        return connection;
    }
}
