using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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

    // ---- aud-a45: a logging provider that throws does not change what the watcher answers ----------------------

    private const string CurrentTokenReadFails = "CurrentToken's read fails";
    private const string CheckReadFails = "HasChangedSince's read fails";
    private const string CheckReadFailsAfterASuccess = "a read fails after a success";
    private const string LoadedUnderUnknown = "a value loaded under Unknown, the store readable again";

    /// <summary>
    /// The aud-a28q tripwire's throwing-logger theory, for the watcher: its report of an unreadable token ran unguarded,
    /// so a provider that threw took its exception out of CurrentToken and HasChangedSince - into every caching reader's
    /// cache hit and load - instead of the answer a working provider gets.
    /// </summary>
    [Theory]
    [InlineData(CurrentTokenReadFails)]
    [InlineData(CheckReadFails)]
    [InlineData(CheckReadFailsAfterASuccess)]
    [InlineData(LoadedUnderUnknown)]
    public void TheWatcher_GivenALoggerThatThrows_StillAnswers(string cause)
    {
        var store = new FakeStore { Token = 5 };
        var log = new ThrowingLogger<ConfigChangeWatcherTests>();
        var now = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);
        var watcher = new ConfigChangeWatcher(store, log) { UtcNow = () => now };

        switch (cause)
        {
            case CurrentTokenReadFails:
                store.Throw = true;
                Assert.Equal(ConfigChangeWatcher.Unknown, Answer(watcher.CurrentToken));
                Assert.Equal(1, log.Attempts);
                break;

            case CheckReadFails:
                store.Throw = true;
                Assert.False(Answer(() => watcher.HasChangedSince(5)));
                Assert.Equal(1, log.Attempts);
                break;

            case CheckReadFailsAfterASuccess:
                store.Throw = true;
                Assert.False(Answer(() => watcher.HasChangedSince(5)));
                store.Throw = false;
                now = now.Add(ConfigChangeWatcher.Throttle);
                Assert.False(Answer(() => watcher.HasChangedSince(5)));
                store.Throw = true;
                now = now.Add(ConfigChangeWatcher.Throttle);
                Assert.False(Answer(() => watcher.HasChangedSince(5)));
                Assert.Equal(2, log.Attempts);
                break;

            case LoadedUnderUnknown:
                // The fail-closed half of caching, kept: a value loaded while the token could not be read reloads at the
                // first check that reaches the store.
                store.Throw = true;
                var loaded = Answer(watcher.CurrentToken);
                store.Throw = false;
                Assert.True(Answer(() => watcher.HasChangedSince(loaded)));
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(cause), cause, null);
        }
    }

    /// <summary>
    /// A caching reader over a real config store, whose own logger - the one it hands its watcher - throws: the usage
    /// telemetry switch, read on the audit path and by the Home page.
    /// </summary>
    [Theory]
    [InlineData("the cache hit")]
    [InlineData("the load")]
    public void AReader_GivenALoggerThatThrows_AndAnUnreadableToken_StillAnswers(string path)
    {
        var dir = Path.Combine(Path.GetTempPath(), "eaw-a45-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new TokenSwitchStore(TestConfigStore.Create(dir));
            var env = Substitute.For<IWebHostEnvironment>();
            env.ContentRootPath.Returns(dir);
            var moduleConfig = new ModuleConfigService(
                new ModuleCatalog(), env, new ModuleConfigRepository(store), NullLogger<ModuleConfigService>.Instance);
            var logger = new ThrowingLogger<UsageTelemetryService>();
            var usage = new UsageTelemetryService(
                new UsageEventRepository(new SqliteConnectionFactory(Path.Combine(dir, "usage.db"))),
                moduleConfig, new ModuleCatalog(), logger);

            if (path == "the cache hit")
                Assert.True(usage.Enabled(), "the switch did not load while its token could be read");
            store.TokenUnreadable = true;

            Assert.True(Answer(usage.Enabled));
            Assert.Equal(1, logger.Attempts);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* best effort */ }
        }
    }

    /// <summary>The call's answer, asserting it returned one rather than throwing.</summary>
    private static T Answer<T>(Func<T> call)
    {
        T answer = default!;
        Assert.Null(Record.Exception(() => answer = call()));
        return answer;
    }

    /// <summary>A real store whose change token the test can make unreadable; reads and writes pass through.</summary>
    private sealed class TokenSwitchStore(IConfigStore inner) : IConfigStore
    {
        public bool TokenUnreadable { get; set; }

        public long GetChangeToken() =>
            TokenUnreadable ? throw new SqliteException("the change token cannot be read", 5) : inner.GetChangeToken();

        public T Read<T>(Func<SqliteConnection, T> read) => inner.Read(read);

        public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write) => inner.Write(write);

        public void Write(Action<SqliteConnection, SqliteTransaction> write) => inner.Write(write);
    }

    /// <summary>Counts every report and throws once it has, as a failing provider can (aud-a28u).</summary>
    private sealed class ThrowingLogger<T> : ILogger<T>
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException("the logging provider failed");
        }
    }
}
