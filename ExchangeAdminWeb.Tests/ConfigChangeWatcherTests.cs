using ExchangeAdminWeb.Services.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/SharedConfigDb-Plan.md AC4: the watcher that lets a caching reader notice a config write
/// made by the other instance on the shared database.
/// </summary>
public class ConfigChangeWatcherTests
{
    private sealed class FakeStore : IConfigStore
    {
        public long Token;
        public int Reads;
        public bool Throw;

        public long GetChangeToken()
        {
            Reads++;
            if (Throw)
                throw new SqliteException("locked", 5);
            return Token;
        }

        public T Read<T>(Func<SqliteConnection, T> read) => throw new NotSupportedException();
        public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write) => throw new NotSupportedException();
        public void Write(Action<SqliteConnection, SqliteTransaction> write) => throw new NotSupportedException();
    }

    private static (ConfigChangeWatcher Watcher, FakeStore Store, ILogger Log, Func<DateTime> Clock) Create(long token = 5)
    {
        var store = new FakeStore { Token = token };
        var log = Substitute.For<ILogger>();
        var now = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);
        var watcher = new ConfigChangeWatcher(store, log) { UtcNow = () => now };
        return (watcher, store, log, () => now);
    }

    [Fact]
    public void CurrentToken_ReturnsStoredValue_Unthrottled()
    {
        var (watcher, store, _, _) = Create(token: 7);

        Assert.Equal(7, watcher.CurrentToken());
        store.Token = 8;
        Assert.Equal(8, watcher.CurrentToken());
        Assert.Equal(2, store.Reads);
    }

    [Fact]
    public void HasChangedSince_SameToken_False_MovedToken_True()
    {
        var store = new FakeStore { Token = 5 };
        var now = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);
        var watcher = new ConfigChangeWatcher(store) { UtcNow = () => now };

        Assert.False(watcher.HasChangedSince(5));

        store.Token = 6;
        now = now.Add(ConfigChangeWatcher.Throttle);
        Assert.True(watcher.HasChangedSince(5));
    }

    [Fact]
    public void HasChangedSince_Throttles()
    {
        var (watcher, store, _, _) = Create(token: 5);

        watcher.HasChangedSince(5);
        store.Token = 6;
        Assert.False(watcher.HasChangedSince(5), "within the throttle the watcher must not re-read");
        Assert.Equal(1, store.Reads);
    }

    [Fact]
    public void HasChangedSince_StoreThrows_ReturnsFalseAndLogsOnce()
    {
        var store = new FakeStore { Token = 5, Throw = true };
        var log = Substitute.For<ILogger>();
        var now = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);
        var watcher = new ConfigChangeWatcher(store, log) { UtcNow = () => now };

        Assert.False(watcher.HasChangedSince(5));
        now = now.Add(ConfigChangeWatcher.Throttle);
        Assert.False(watcher.HasChangedSince(5));
        Assert.Equal(2, store.Reads);

        log.ReceivedWithAnyArgs(1).Log(default, default, default(object)!, default, default!);
    }

    [Fact]
    public void HasChangedSince_UnknownLoadedToken_IsAlwaysChanged()
    {
        var (watcher, _, _, _) = Create(token: 0);

        Assert.True(watcher.HasChangedSince(ConfigChangeWatcher.Unknown));
    }

    [Fact]
    public void CurrentToken_StoreThrows_ReturnsUnknown()
    {
        var store = new FakeStore { Throw = true };
        var watcher = new ConfigChangeWatcher(store, Substitute.For<ILogger>());

        Assert.Equal(ConfigChangeWatcher.Unknown, watcher.CurrentToken());
    }

    // A reader that reloads after a change records the token CurrentToken returned. An older
    // throttled reading must not then report "changed" against that fresh token, or every call
    // for the next two seconds would reload again.
    [Fact]
    public void CurrentToken_RefreshesWhatHasChangedSinceComparesAgainst()
    {
        var (watcher, store, _, _) = Create(token: 5);

        Assert.False(watcher.HasChangedSince(5));
        store.Token = 6;
        var loaded = watcher.CurrentToken();

        Assert.Equal(6, loaded);
        Assert.False(watcher.HasChangedSince(loaded));
    }

    // scd-2: a write that lands between a reader's load and its first check must be seen by
    // that first check - CurrentToken does not restart the throttle.
    [Fact]
    public void FirstCheckAfterALoad_ReachesTheStore()
    {
        var (watcher, store, _, _) = Create(token: 5);

        var loaded = watcher.CurrentToken();
        store.Token = 6;

        Assert.True(watcher.HasChangedSince(loaded));
    }
}
