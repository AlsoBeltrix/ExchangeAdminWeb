using System.Diagnostics;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a41: a connection from <see cref="SqliteConnectionFactory"/> waits <see cref="SqliteConnectionFactory.BusyTimeout"/>
/// for a lock another connection holds, once, and then fails SQLITE_BUSY. Without the provider's command timeout set, its
/// busy retry wrapped the factory's 5-second busy timeout and a held lock was waited out for about 35 seconds.
/// </summary>
/// <remarks>
/// Real temp files and a real lock: another unpooled connection's <c>BEGIN IMMEDIATE</c>, as the other instance or another
/// admin's save holds it. The first case waits the documented wait by design.
/// </remarks>
public sealed class SqliteBusyWaitTests
{
    private static readonly TimeSpan SecondWait = SqliteConnectionFactory.BusyTimeout * 2;

    private static SqliteConfigStore MigratedStore(string path)
    {
        var factory = new SqliteConnectionFactory(path);
        new ConfigStoreMigrator(factory).Migrate();
        return new SqliteConfigStore(factory);
    }

    private static SqliteConnection Unpooled(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void AWriteThatMeetsAHeldLock_FailsBusy_AfterTheDocumentedWait()
    {
        using var temp = new TempDir();
        var store = MigratedStore(temp.DbPath);
        using var holder = Unpooled(temp.DbPath);
        Execute(holder, "BEGIN IMMEDIATE;");

        var watch = Stopwatch.StartNew();
        var busy = Assert.Throws<SqliteException>(() => store.Write((_, _) => { }));
        watch.Stop();
        Execute(holder, "ROLLBACK;");

        Assert.True(ConfigStoreBusy.IsBusy(busy), $"not SQLITE_BUSY: {busy.SqliteErrorCode} {busy.Message}");
        Assert.True(watch.Elapsed >= SqliteConnectionFactory.BusyTimeout - TimeSpan.FromSeconds(0.5),
            $"the write gave up after {watch.Elapsed.TotalSeconds:F2} s, before the documented wait");
        Assert.True(watch.Elapsed < SecondWait - TimeSpan.FromSeconds(1),
            $"the write waited {watch.Elapsed.TotalSeconds:F2} s: more than one busy wait of {SqliteConnectionFactory.BusyTimeout.TotalSeconds:F0} s");
    }

    [Fact]
    public void AFactoryConnection_WaitsTheBusyTimeout_AndTheProviderRetriesNoLonger()
    {
        using var temp = new TempDir();
        using var connection = new SqliteConnectionFactory(temp.DbPath).Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA busy_timeout;";

        Assert.Equal((long)SqliteConnectionFactory.BusyTimeout.TotalMilliseconds, Convert.ToInt64(command.ExecuteScalar()));
        Assert.Equal((int)SqliteConnectionFactory.BusyTimeout.TotalSeconds, connection.DefaultTimeout);
        Assert.Equal(connection.DefaultTimeout, command.CommandTimeout);
    }

    [Fact]
    public void AFactorysPooledConnections_AreReleasedByClearingThePoolOfThePlainConnectionString()
    {
        // The command timeout is set on each connection, not in the connection string: the pool is keyed by the string,
        // and code that clears this file's pool rebuilds the plain one. With the timeout in the string, the clear missed
        // the factory's pool and the file stayed open (aud-a41: 207 test fixtures failed to delete their folder).
        using var temp = new TempDir();
        using (var connection = new SqliteConnectionFactory(temp.DbPath).Open())
            Execute(connection, "CREATE TABLE t (x INTEGER);");

        SqliteConnection.ClearPool(new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = temp.DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString()));

        File.Delete(temp.DbPath);
        Assert.False(File.Exists(temp.DbPath));
    }

    [Fact]
    public async Task ALockReleasedWithinTheWait_LetsTheWriteThrough()
    {
        using var temp = new TempDir();
        var store = MigratedStore(temp.DbPath);
        using var holder = Unpooled(temp.DbPath);
        Execute(holder, "BEGIN IMMEDIATE;");
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1));
            Execute(holder, "ROLLBACK;");
        }, TestContext.Current.CancellationToken);

        var watch = Stopwatch.StartNew();
        store.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO app_setting (key, value) VALUES ('busy-wait', 'written');";
            command.ExecuteNonQuery();
        });
        watch.Stop();
        await release;

        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(0.8), $"the write did not wait for the lock ({watch.Elapsed.TotalSeconds:F2} s)");
        Assert.True(watch.Elapsed < SqliteConnectionFactory.BusyTimeout, $"the write waited {watch.Elapsed.TotalSeconds:F2} s for a lock held 1 s");
        Assert.Equal("written", store.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM app_setting WHERE key = 'busy-wait';";
            return Convert.ToString(command.ExecuteScalar());
        }));
    }

    [Fact]
    public async Task ANewDatabaseFilesSwitchToWal_WaitsForALockAsLongAsAnyOtherStatement()
    {
        // SQLite fails the switch to WAL at once when another connection holds a lock - no busy_timeout - so the provider's
        // own retry is the only wait it gets. A command timeout below the busy wait cut it short (measured: 1.1 s at 1).
        using var temp = new TempDir();
        using (var create = Unpooled(temp.DbPath))
            Execute(create, "CREATE TABLE t (x INTEGER);");

        using var holder = Unpooled(temp.DbPath);
        Execute(holder, "BEGIN IMMEDIATE; INSERT INTO t VALUES (1);");
        var release = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            Execute(holder, "ROLLBACK;");
        }, TestContext.Current.CancellationToken);

        var watch = Stopwatch.StartNew();
        using var connection = new SqliteConnectionFactory(temp.DbPath).Open();
        watch.Stop();
        await release;

        using var mode = connection.CreateCommand();
        mode.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", Convert.ToString(mode.ExecuteScalar()));
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1.5), $"the open did not wait for the lock ({watch.Elapsed.TotalSeconds:F2} s)");
    }
}
