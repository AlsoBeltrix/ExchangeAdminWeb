using System.Collections.Concurrent;

namespace ExchangeAdminWeb.Services;

/// <summary>One page request that is being served right now.</summary>
/// <param name="Id">Identifies this request so it can be ended exactly.</param>
/// <param name="Route">The path being built, as the operator would recognise it.</param>
/// <param name="StartedUtc">When the server began. Elapsed time is derived, never accumulated.</param>
public sealed record PageLoad(string Id, string Route, DateTime StartedUtc);

/// <summary>
/// Which page requests are in flight, per operator. The status frame reads this to say what the
/// app is doing right now.
/// </summary>
/// <remarks>
/// WHY THIS EXISTS, because it replaces four failed attempts at the same job and the reason
/// matters more than the mechanism.
///
/// The status frame first reported navigation from the BROWSER: a click started a "Loading X"
/// readout and something was supposed to come along and stop it. Four stop mechanisms were
/// shipped - two Blazor events, a DOM MutationObserver, and a timeout - and every one of them
/// failed on the real deployment, leaving the frame counting upwards on a page that had already
/// arrived. The faults were all the same shape: the browser had LATCHED a claim and the
/// retraction never came. A design that must be told to stop lying can always be caught lying.
///
/// This holds no latch. It records a request while the server is serving it and forgets it when
/// the request ends, in a finally. "Is a page loading" is answered by asking, not by remembering
/// - so there is nothing to strand, no timer, no cap, and no inference about what the browser is
/// doing.
///
/// Keyed by operator because an enhanced-navigation fetch is an ordinary HTTP request and
/// carries no circuit id; the user's identity is the only thing that links the request to the
/// circuit that should show it. <see cref="ClientInfoService.StoreForUser"/> solves the same
/// cross-scope problem the same way, and is the precedent.
///
/// Singleton: requests and circuits are different scopes and neither outlives the other.
/// </remarks>
public sealed class PageLoadTracker
{
    // Keyed by operator, then by request id. A ConcurrentDictionary per user rather than a lock:
    // every page request in the app writes here, including concurrent ones from several tabs.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, PageLoad>> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Raised with the operator's name whenever their set of in-flight page requests changes.
    /// Subscribers are on Blazor circuits and MUST marshal onto the renderer.
    /// </summary>
    public event Action<string>? Changed;

    /// <summary>
    /// Records that a page request has begun. Dispose the result when it ends - from a finally,
    /// so a request that throws does not leave the frame claiming to still be loading.
    /// </summary>
    public IDisposable Begin(string user, string route)
    {
        if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(route))
            return NullScope.Instance;

        var load = new PageLoad(Guid.NewGuid().ToString("N"), route.Trim(), DateTime.UtcNow);
        var forUser = _inFlight.GetOrAdd(user, _ => new ConcurrentDictionary<string, PageLoad>(StringComparer.Ordinal));
        forUser[load.Id] = load;

        Raise(user);
        return new Scope(this, user, load.Id);
    }

    /// <summary>What this operator has in flight right now, oldest first. Never null.</summary>
    public IReadOnlyList<PageLoad> InFlightFor(string user)
    {
        if (string.IsNullOrWhiteSpace(user) || !_inFlight.TryGetValue(user, out var forUser))
            return [];

        return forUser.Values.OrderBy(l => l.StartedUtc).ToList();
    }

    private void End(string user, string id)
    {
        if (!_inFlight.TryGetValue(user, out var forUser) || !forUser.TryRemove(id, out _))
            return;

        // Drop the empty bucket so an operator who has gone home stops occupying a key. The
        // race with a concurrent Begin is benign: GetOrAdd puts a fresh bucket back.
        if (forUser.IsEmpty)
            _inFlight.TryRemove(user, out _);

        Raise(user);
    }

    // A subscriber that throws must not fail the page request it was only reporting on. The
    // request is the operator's actual work; the status frame is commentary.
    private void Raise(string user)
    {
        try
        {
            Changed?.Invoke(user);
        }
        catch
        {
            // Intentionally swallowed, as above.
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly PageLoadTracker _owner;
        private readonly string _user;
        private readonly string _id;
        private bool _ended;

        internal Scope(PageLoadTracker owner, string user, string id)
        {
            _owner = owner;
            _user = user;
            _id = id;
        }

        public void Dispose()
        {
            if (_ended)
                return;

            _ended = true;
            _owner.End(_user, _id);
        }
    }

    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
