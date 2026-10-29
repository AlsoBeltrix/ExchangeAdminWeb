using System.Diagnostics;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-58 B1 (docs/CancellationDepth-Plan.md 4.3, 4.4; aud-a67 (12)): a config write takes a token, and a cancel before its
/// commit leaves nothing written - the transaction, the change-token bump included, rolls back whole - while a cancel during or
/// after the commit is not honoured. <see cref="SqliteConfigStore"/> looks at the token before the connection opens (C1),
/// before <c>BEGIN IMMEDIATE</c> (C2), as it returns (C3) and after the callback and the bump, before the commit (C4); the
/// interface's default, for the stand-ins, looks once and then writes.
/// </summary>
/// <remarks>
/// A real store over a temporary database; each outcome read back through a connection of its own. A lock another
/// connection holds, as the other instance or another admin's save holds it, is an unpooled connection's
/// <c>BEGIN IMMEDIATE</c>. Two cases wait the store's busy wait by design.
/// </remarks>
public sealed class SqliteConfigStoreCancellationTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly SqliteConfigStore _store;

    public SqliteConfigStoreCancellationTests()
    {
        var factory = new SqliteConnectionFactory(_temp.DbPath);
        new ConfigStoreMigrator(factory).Migrate();
        _store = new SqliteConfigStore(factory);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>The two writes: the one that returns a value, and the one that returns none.</summary>
    public static TheoryData<bool> BothWrites => [true, false];

    // C1 and C2: a token already cancelled - the page gave up before the save reached the store - writes nothing, moves no
    // change token, runs no callback, and does not wait for a lock another connection holds.
    [Theory]
    [MemberData(nameof(BothWrites))]
    public void AWriteWhoseTokenIsAlreadyCancelled_WritesNothing_AndWaitsForNoLock(bool returnsAValue)
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var ran = false;
        using var holder = Unpooled();
        Execute(holder, "BEGIN IMMEDIATE;");
        var clock = Stopwatch.StartNew();

        Assert.ThrowsAny<OperationCanceledException>(() => Write(returnsAValue, cancel.Token, (connection, transaction) =>
        {
            ran = true;
            Insert(connection, transaction, "a");
        }));
        clock.Stop();
        Execute(holder, "ROLLBACK;");

        Assert.False(ran);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"the write waited {clock.Elapsed.TotalSeconds:F1} s for a lock");
        Assert.Equal(0, Rows());
        Assert.Equal(0, _store.GetChangeToken());
    }

    // C3: cancelled while the write waits for a lock another connection holds, the lock then freed - the wait ends, and the
    // write ends there: a cancellation, its callback never run, nothing written.
    [Fact]
    public async Task AWriteCancelledDuringTheBusyWait_TheLockThenFreed_RunsNoCallback_AndWritesNothing()
    {
        using var cancel = new CancellationTokenSource();
        var ran = false;
        using var holder = Unpooled();
        Execute(holder, "BEGIN IMMEDIATE;");

        var write = Task.Run(() => _store.Write((connection, transaction) =>
        {
            ran = true;
            Insert(connection, transaction, "a");
        }, cancel.Token), TestContext.Current.CancellationToken);
        // Long enough for the write to have opened its connection and met the lock (a connection opens in milliseconds); far
        // inside the busy wait (SqliteConnectionFactory.BusyTimeout).
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.False(write.IsCompleted, "the write must be waiting for the lock");
        await cancel.CancelAsync();
        Execute(holder, "ROLLBACK;");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.False(ran);
        Assert.Equal(0, Rows());
        Assert.Equal(0, _store.GetChangeToken());
    }

    // C4: cancelled from inside the callback, its rows inserted - the write ends before its commit, and the transaction rolls
    // back whole: no row, no change token moved.
    [Theory]
    [MemberData(nameof(BothWrites))]
    public void AWriteCancelledInsideItsCallback_RollsBackWhole(bool returnsAValue)
    {
        using var cancel = new CancellationTokenSource();

        Assert.ThrowsAny<OperationCanceledException>(() => Write(returnsAValue, cancel.Token, (connection, transaction) =>
        {
            Insert(connection, transaction, "a");
            Insert(connection, transaction, "b");
            cancel.Cancel();
        }));

        Assert.Equal(0, Rows());
        Assert.Equal(0, _store.GetChangeToken());
    }

    // After the commit nothing looks at the token: cancelled once the write has committed, it returns its result, and its
    // rows and its change token are there. The store alone decides whether a write landed.
    [Fact]
    public void AWriteCancelledAfterItsCommit_ReturnsItsResult_ItsRowsThere()
    {
        using var cancel = new CancellationTokenSource();
        _store.AfterCommit = cancel.Cancel;

        var result = _store.Write((connection, transaction) =>
        {
            Insert(connection, transaction, "a");
            return "committed";
        }, cancel.Token);

        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal("committed", result);
        Assert.Equal(1, Rows());
        Assert.Equal(1, _store.GetChangeToken());
    }

    // The busy wait itself takes no token: cancelled while the write waits for a lock that is never freed, the wait runs out
    // and the write fails busy - which is what the save reports - not as a cancellation. Nothing written.
    [Fact]
    public async Task AWriteCancelledDuringABusyWaitThatNeverEnds_FailsBusy_NotAsACancel()
    {
        using var cancel = new CancellationTokenSource();
        using var holder = Unpooled();
        Execute(holder, "BEGIN IMMEDIATE;");
        Exception? outcome;
        try
        {
            var write = Task.Run(() => _store.Write((connection, transaction) => Insert(connection, transaction, "a"), cancel.Token),
                TestContext.Current.CancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            await cancel.CancelAsync();
            outcome = await Record.ExceptionAsync(() => write.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        }
        finally
        {
            Execute(holder, "ROLLBACK;");
        }

        var busy = Assert.IsType<SqliteException>(outcome);
        Assert.True(ConfigStoreBusy.IsBusy(busy), $"not SQLITE_BUSY: {busy.SqliteErrorCode} {busy.Message}");
        Assert.Equal(0, Rows());
    }

    // The interface's default, for a store that implements only the token-less writes - the tests' stand-ins: a token already
    // cancelled is refused before the write is asked for. And the one store the app runs, SqliteConfigStore, implements both
    // token-taking writes itself, so its four looks - not the default's one - are what the saves get.
    [Fact]
    public void TheDefaultRefusesACancelledToken_AndTheAppsStoreImplementsBothTokenWritesItself()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        IConfigStore standIn = new TokenLessStore();

        Assert.ThrowsAny<OperationCanceledException>(() => standIn.Write((_, _) => 1, cancel.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => standIn.Write((_, _) => { }, cancel.Token));
        Assert.Equal(0, ((TokenLessStore)standIn).Writes);

        var map = typeof(SqliteConfigStore).GetInterfaceMap(typeof(IConfigStore));
        var tokenWrites = Enumerable.Range(0, map.InterfaceMethods.Length)
            .Where(i => map.InterfaceMethods[i].GetParameters().Any(p => p.ParameterType == typeof(CancellationToken)))
            .Select(i => map.TargetMethods[i])
            .ToList();
        Assert.Equal(2, tokenWrites.Count);
        Assert.All(tokenWrites, method => Assert.Equal(typeof(SqliteConfigStore), method.DeclaringType));
    }

    /// <summary>A store with only the token-less writes, as the tests' stand-ins are: it counts the writes it is asked for.</summary>
    private sealed class TokenLessStore : IConfigStore
    {
        public int Writes { get; private set; }

        public long GetChangeToken() => 0;

        public T Read<T>(Func<SqliteConnection, T> read) => throw new NotSupportedException();

        public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write)
        {
            Writes++;
            return default!;
        }

        public void Write(Action<SqliteConnection, SqliteTransaction> write) => Writes++;
    }

    private void Write(bool returnsAValue, CancellationToken cancellationToken, Action<SqliteConnection, SqliteTransaction> body)
    {
        if (returnsAValue)
        {
            _store.Write((connection, transaction) =>
            {
                body(connection, transaction);
                return 1;
            }, cancellationToken);
        }
        else
        {
            _store.Write(body, cancellationToken);
        }
    }

    private static void Insert(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO app_setting (key, value) VALUES ($key, '1');";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    /// <summary>The rows written, read through a connection of its own.</summary>
    private long Rows()
    {
        using var connection = Unpooled();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM app_setting;";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private SqliteConnection Unpooled()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _temp.DbPath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
