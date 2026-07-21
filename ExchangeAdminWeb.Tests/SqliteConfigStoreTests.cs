using ExchangeAdminWeb.Services.Storage;

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
        });

        Assert.Equal(1, store.GetChangeToken());

        store.Write((connection, transaction) =>
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO app_setting (key, value) VALUES ('b', '2');";
            command.ExecuteNonQuery();
        });

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
        });
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
            }));

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
        });

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
        });

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
}
