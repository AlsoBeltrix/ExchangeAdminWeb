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
    /// <summary>
    /// aud-a67 item 7. What a wait must stay under to show it is not the provider's retry loop: that loop ran the
    /// statement again, each run another full busy timeout, until the command timeout had passed - about 30 to 35
    /// seconds, measured. The ceiling used to be one second under two busy waits (9 s), which is a timing rather than a
    /// signal: on a loaded host - several agents building and testing at once - a 5-second wait plus scheduling has
    /// crossed it repeatedly with nothing wrong. The shape of the wait is pinned structurally instead, by
    /// <see cref="AFactoryConnection_WaitsTheBusyTimeout_AndTheProviderRetriesNoLonger"/>, which reads back
    /// <c>busy_timeout</c> and <c>DefaultTimeout</c> and cannot drift with load.
    /// </summary>
    private static readonly TimeSpan NotTheRetryLoop = TimeSpan.FromSeconds(25);

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
        var busy = Assert.Throws<SqliteException>(() => store.Write((_, _) => { }, TestContext.Current.CancellationToken));
        watch.Stop();
        Execute(holder, "ROLLBACK;");

        Assert.True(ConfigStoreBusy.IsBusy(busy), $"not SQLITE_BUSY: {busy.SqliteErrorCode} {busy.Message}");
        Assert.True(watch.Elapsed >= SqliteConnectionFactory.BusyTimeout - TimeSpan.FromSeconds(0.5),
            $"the write gave up after {watch.Elapsed.TotalSeconds:F2} s, before the documented wait");
        Assert.True(watch.Elapsed < NotTheRetryLoop,
            $"the write waited {watch.Elapsed.TotalSeconds:F2} s: the provider's retry loop, not one busy wait of {SqliteConnectionFactory.BusyTimeout.TotalSeconds:F0} s");
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
        }, TestContext.Current.CancellationToken);
        watch.Stop();
        await release;

        // The floor stays: it is what proves the write WAITED for the lock rather than finding it free, and a loaded
        // host only ever pushes a wait up, never below the hold.
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(0.8), $"the write did not wait for the lock ({watch.Elapsed.TotalSeconds:F2} s)");

        // cons-2, the aud-a67 (7) class. The ceiling was one busy wait (5 s) for a lock held 1 s, which is a timing
        // rather than a signal: the write has to be scheduled, and on a host running several builds and test runs at
        // once that can take seconds with nothing wrong. What the assertion is for is the provider's retry loop -
        // measured at 30 to 35 s - and 25 s still fails that and nothing else. What the write did, rather than how
        // long it took, is pinned below: the row it wrote is read back.
        Assert.True(watch.Elapsed < NotTheRetryLoop, $"the write waited {watch.Elapsed.TotalSeconds:F2} s for a lock held 1 s: the provider's retry loop");
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
