namespace ExchangeAdminWeb.Services.Storage;

/// <summary>
/// Lets a caching reader notice a config write made by ANOTHER process on the shared database
/// (docs/SharedConfigDb-Plan.md AC4, decision 2026-09-04). <see cref="SqliteConfigStore.Write"/>
/// bumps <see cref="ConfigChangeToken"/> inside every write transaction; a reader records the
/// token as of its own load and asks <see cref="HasChangedSince"/> on every cache hit.
///
/// Protocol for a reader (review finding scd-2): read the token BEFORE loading the value you
/// cache - <c>var t = watcher.CurrentToken(); load...; _loadedToken = t;</c>. A write that races
/// the load moves the stored token past <c>t</c>, so the next check reloads. Seeding the token
/// on the first poll instead would miss any change made between the load and that poll - for
/// a value that never expires (the extended-log level) until the next unrelated write.
///
/// <see cref="HasChangedSince"/> reads the database at most once per <see cref="Throttle"/> per
/// watcher, so a hot path (one check per log line) costs a clock read and a compare. One
/// watcher per caching reader; the readers' own TTLs still bound staleness when reads fail.
/// </summary>
public sealed class ConfigChangeWatcher
{
    /// <summary>Minimum interval between two database reads inside <see cref="HasChangedSince"/>.</summary>
    public static readonly TimeSpan Throttle = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The token value recorded when <see cref="CurrentToken"/> could not read the store. Every
    /// successful read differs from it, so a reader that loaded under this value reloads on
    /// its next check that reaches the store.
    /// </summary>
    public const long Unknown = long.MinValue;

    private readonly IConfigStore _store;
    private readonly ILogger? _logger;
    private readonly object _lock = new();

    private DateTime _lastReadAt = DateTime.MinValue;
    private long _lastSeen = Unknown;
    private bool _failureLogged;

    // Test seam for the throttle clock; production uses the real clock.
    internal Func<DateTime> UtcNow = () => DateTime.UtcNow;

    public ConfigChangeWatcher(IConfigStore store, ILogger? logger = null)
    {
        _store = store;
        _logger = logger;
    }

    /// <summary>
    /// The stored token right now, never throttled; <see cref="Unknown"/> when the read fails
    /// (logged once until the next success). Callers read it BEFORE loading the value they cache.
    /// A successful read also refreshes what <see cref="HasChangedSince"/> compares against, so a
    /// reader that just reloaded is not told "changed" again by an older throttled reading. It
    /// does NOT restart the throttle: the first check after a load still reaches the store, so a
    /// write that landed between the load and that check is seen at once.
    /// </summary>
    public long CurrentToken()
    {
        try
        {
            var token = _store.GetChangeToken();
            lock (_lock)
            {
                _lastSeen = token;
                _failureLogged = false;
            }
            return token;
        }
        catch (Exception ex)
        {
            LogOnce(ex);
            return Unknown;
        }
    }

    /// <summary>
    /// True when the stored token differs from <paramref name="loadedToken"/>. Reads the store at
    /// most once per <see cref="Throttle"/>; between reads it compares against the last value
    /// seen. A failed read returns false (stale-but-serving beats a hot loop of failures; the
    /// caller's TTL still bounds staleness) and logs once until the next success. An
    /// <see cref="Unknown"/> <paramref name="loadedToken"/> compares as changed against every
    /// successful read.
    /// </summary>
    public bool HasChangedSince(long loadedToken)
    {
        lock (_lock)
        {
            var now = UtcNow();
            if (now - _lastReadAt >= Throttle)
            {
                _lastReadAt = now;
                try
                {
                    _lastSeen = _store.GetChangeToken();
                    _failureLogged = false;
                }
                catch (Exception ex)
                {
                    LogOnce(ex);
                    return false;
                }
            }

            // Nothing has been read successfully yet (the first read failed): serve the cache.
            if (_lastSeen == Unknown)
                return false;

            return _lastSeen != loadedToken;
        }
    }

    private void LogOnce(Exception ex)
    {
        lock (_lock)
        {
            if (_failureLogged)
                return;
            _failureLogged = true;
        }

        _logger?.LogWarning(ex, "Config change token could not be read; cached config is served until the next successful check or its TTL");
    }
}
