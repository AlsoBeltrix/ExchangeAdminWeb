using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace ExchangeAdminWeb.Authorization;

/// <summary>
/// Refuses every inbound circuit activity once revalidation has revoked the circuit, and starts the
/// check that runs when a circuit opens (aud-a29b).
/// </summary>
/// <remarks>
/// <para>
/// Replacing the principal is not enough on its own in this app. The router is static and pages are
/// interactive islands, so nothing re-evaluates a page's <c>[Authorize]</c> when the state changes;
/// a page that authorized once keeps its cached user; and a reconnect would hand the stale
/// principal back. Dropping every inbound activity - events, JS interop, navigation, root-component
/// updates - is what makes the fix hold on every page however the page was written. The
/// session-ended notice is the one thing left that visibly changes.
/// </para>
/// <para>
/// Dropped, not thrown: the notice already explains, and a throw per keystroke would fill the log.
/// The first refusal is logged.
/// </para>
/// <para>
/// <see cref="Order"/> 1 runs this after handlers at the default 0, so <c>ClientInfoCircuitHandler</c>
/// has captured the circuit's IP before the open check can audit. The open check itself runs on the
/// thread pool and is not awaited: the circuit's first render does not wait for the directory.
/// </para>
/// </remarks>
public sealed class CircuitRevocationGate : CircuitHandler
{
    private readonly CircuitRevocation _revocation;
    private readonly AuthenticationStateProvider _authenticationState;
    private readonly ILogger<CircuitRevocationGate> _logger;
    private int _refusalLogged;

    public CircuitRevocationGate(
        CircuitRevocation revocation,
        AuthenticationStateProvider authenticationState,
        ILogger<CircuitRevocationGate> logger)
    {
        _revocation = revocation;
        _authenticationState = authenticationState;
        _logger = logger;
    }

    /// <summary>The circuit-open check, exposed so a test can await it. It never throws.</summary>
    internal Task OpenCheck { get; private set; } = Task.CompletedTask;

    public override int Order => 1;

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_authenticationState is MembershipRevalidatingAuthenticationStateProvider provider)
            OpenCheck = Task.Run(() => provider.CheckAtCircuitOpenAsync(), CancellationToken.None);

        return base.OnCircuitOpenedAsync(circuit, cancellationToken);
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return context =>
        {
            if (!_revocation.IsRevoked)
                return next(context);

            if (Interlocked.Exchange(ref _refusalLogged, 1) == 0)
                _logger.LogWarning("Refusing inbound activity on a circuit whose access was revoked by revalidation");

            return Task.CompletedTask;
        };
    }
}
