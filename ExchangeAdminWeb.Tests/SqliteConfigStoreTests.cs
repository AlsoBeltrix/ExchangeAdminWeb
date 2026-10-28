using ExchangeAdminWeb.Services.Storage;
using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Tests;

public class SqliteConfigStoreTests
{
    private static SqliteConfigStore CreateStore(TempDir temp)
    {
        var factory = new SqliteConnectionFactory(temp.DbPath);
        new ConfigStoreMigrator(factory).Migrate();
        return new SqliteConfigStore(factory);
    }

    [Fact]
    public void GetChangeToken_FreshDatabase_IsZero()
    {
        using var temp = new TempDir();
        var store = CreateStore(temp);

        Assert.Equal(0, store.GetChangeToken());
    }

    [Fact]
    public void Write_BumpsChangeToken()
    {
        using var temp = new TempDir();
        var store = CreateStore(temp);

        store.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO app_setting (key, value) VALUES ('a', '1');";
            command.ExecuteNonQuery();
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, store.GetChangeToken());

        store.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO app_setting (key, value) VALUES ('b', '2');";
            command.ExecuteNonQuery();
        }, TestContext.Current.CancellationToken);

        Assert.Equal(2, store.GetChangeToken());
    }

    [Fact]
    public void Write_RollsBackWriteAndTokenWhenCallbackThrows()
    {
        using var temp = new TempDir();
        var store = CreateStore(temp);

        // Seed one row and capture the token.
        store.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO app_setting (key, value) VALUES ('keep', 'yes');";
            command.ExecuteNonQuery();
        }, TestContext.Current.CancellationToken);
        var tokenBefore = store.GetChangeToken();

        // A throwing write must roll back BOTH its row and the token bump.
        Assert.Throws<InvalidOperationException>(() =>
            store.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "INSERT INTO app_setting (key, value) VALUES ('bad', 'no');";
                command.ExecuteNonQuery();
                throw new InvalidOperationException("boom");
            }, TestContext.Current.CancellationToken));

        Assert.Equal(tokenBefore, store.GetChangeToken());

        var rows = store.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM app_setting;";
            return Convert.ToInt64(command.ExecuteScalar());
        });
        Assert.Equal(1, rows); // only 'keep' survived
    }

    [Fact]
    public void Read_SeesDataWrittenByPriorWrite()
    {
        using var temp = new TempDir();
        var store = CreateStore(temp);

        store.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO app_setting (key, value) VALUES ('color', 'blue');";
            command.ExecuteNonQuery();
        }, TestContext.Current.CancellationToken);

        var value = store.Read(connection =>
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM app_setting WHERE key = 'color';";
            return Convert.ToString(command.ExecuteScalar());
        });

        Assert.Equal("blue", value);
    }

    [Fact]
    public void Factory_SupportsConcurrentShortLivedConnections()
    {
        using var temp = new TempDir();
        var store = CreateStore(temp);

        store.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO app_setting (key, value) VALUES ('shared', 'ok');";
            command.ExecuteNonQuery();
        }, TestContext.Current.CancellationToken);

        // Simulate the mixed Singleton/Scoped readers: many overlapping reads must all succeed
        // against their own short-lived connections (the factory must not hand out one shared
        // connection - Section 5B.1).
        Parallel.For(0, 32, _ =>
        {
            var value = store.Read(connection =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM app_setting WHERE key = 'shared';";
                return Convert.ToString(command.ExecuteScalar());
            });
            Assert.Equal("ok", value);
        });
    }

    [Fact]
    public void Write_HoldsTheWriteLockBeforeTheCallbackReads()
    {
        // aud-a33b and aud-a33c decide inside the write callback from what it reads there. That is
        // safe only if no other writer - the other instance on the shared database included - can
        // commit between the callback's first read and its writes, which needs the write lock taken
        // at BEGIN (IMMEDIATE) rather than at the first write (DEFERRED). Here another connection
        // tries to start a write while the callback has not yet touched the database.
        using var temp = new TempDir();
        var store = CreateStore(temp);

        var otherWriterWasRefused = store.Write((connection, transaction) =>
        {
            using var other = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = temp.DbPath }.ToString());
            other.Open();

            // Two commands, not one. With "PRAGMA busy_timeout = 0; BEGIN IMMEDIATE;" as a single
            // command this provider did not surface the BEGIN's SQLITE_BUSY: the call returned
            // normally with no transaction open, and the ROLLBACK below then failed instead.
            using (var noWait = other.CreateCommand())
            {
                noWait.CommandText = "PRAGMA busy_timeout = 0;";
                noWait.ExecuteNonQuery();
            }

            using var begin = other.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.CommandTimeout = 1;
            try
            {
                begin.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 5)
            {
                return true;
            }

            using var rollback = other.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
            return false;
        });

        Assert.True(otherWriterWasRefused,
            "Another connection began a write inside the store's write transaction: the transaction "
            + "is DEFERRED, so what a callback reads can be overtaken before it writes.");
    }
}
