using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;

namespace ExchangeAdminWeb.Authorization;

/// <summary>
/// Holds a new circuit until the check that runs when it opens has confirmed its access, then
/// refuses every inbound circuit activity once revalidation has revoked it (aud-a29b, aud-a29c).
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
/// <b>A new circuit is pending until its open check reaches a verdict (aud-a29c).</b> Under Kerberos
/// a reload presents the same stale groups, so a check that ran in the background let whatever was
/// clicked before the directory answered run unchecked, on every reload. Two holds, each enough on
/// its own in ASP.NET Core 10 (the order is traced in the aud-a29c record):
/// <see cref="OnCircuitOpenedAsync"/> awaits the verdict, and the framework adds the circuit's root
/// components - the page's circuit-side initialization and first render - only after every handler's
/// <c>OnCircuitOpenedAsync</c> has returned; and every inbound activity that reaches the gate while
/// the circuit is pending waits for the same verdict, the root-component add included (the
/// framework runs it inside the inbound pipeline). A verdict that does not confirm access drops what
/// waited and throws from <see cref="OnCircuitOpenedAsync"/>, so the circuit is never initialized.
/// </para>
/// <para>
/// The wait is the lookup's own: the open check ends within
/// <see cref="CircuitRevalidationOptions.LookupTimeout"/>, revoking on expiry. A hold that outlives
/// <see cref="HoldTimeout"/>, twice that, means the check itself is stuck, and revokes with
/// <see cref="CircuitRevocationReason.PendingCheckTimedOut"/>. With revalidation switched off, or
/// without the revalidating provider, nothing is held and nothing is revoked here.
/// </para>
/// <para>
/// <see cref="Order"/> 1 runs this after handlers at the default 0, so <c>ClientInfoCircuitHandler</c>
/// has captured the circuit's IP before the open check can audit.
/// </para>
/// </remarks>
public sealed class CircuitRevocationGate : CircuitHandler
{
    private static readonly Task<bool> Confirmed = Task.FromResult(true);

    private readonly CircuitRevocation _revocation;
    private readonly AuthenticationStateProvider _authenticationState;
    private readonly CircuitRevalidationOptions _options;
    private readonly ILogger<CircuitRevocationGate> _logger;
    private readonly object _sync = new();

    // The open check's verdict, once started: true when it confirmed access. Null until then - a
    // circuit is pending from the moment it exists, not from when someone thinks to start the check.
    private Task<bool>? _verdict;
    private int _refusalLogged;

    public CircuitRevocationGate(
        CircuitRevocation revocation,
        AuthenticationStateProvider authenticationState,
        CircuitRevalidationOptions options,
        ILogger<CircuitRevocationGate> logger)
    {
        _revocation = revocation;
        _authenticationState = authenticationState;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// The circuit-open check's verdict, exposed so a test can await it: true when access was
    /// confirmed, false when it was not. Never faults.
    /// </summary>
    internal Task<bool> OpenCheck
    {
        get
        {
            lock (_sync)
                return _verdict ?? Confirmed;
        }
    }

    /// <summary>
    /// How long anything waits for the open check before the gate stops trusting it to answer.
    /// Twice the lookup timeout, because the check enforces the lookup timeout itself; this is only
    /// reached when the check is stuck outside its lookup.
    /// </summary>
    internal TimeSpan HoldTimeout => _options.LookupTimeout * 2;

    public override int Order => 1;

    /// <summary>
    /// Waits for the open check, and refuses to let the circuit initialize without its verdict.
    /// </summary>
    /// <remarks>
    /// The framework awaits this before it adds the circuit's root components
    /// (<c>CircuitHost.UpdateRootComponents</c>), so no page in this circuit initializes or renders
    /// before the directory has answered. The throw is what stops a circuit that was not confirmed:
    /// the framework logs it and terminates the circuit, and the browser shows the app's error banner
    /// with its Reload link. The session-ended notice cannot show there, because it is itself one of
    /// the root components that are never added.
    /// </remarks>
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (!await AwaitVerdictAsync(cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "This circuit's access was not confirmed when it opened, so it is not initialized (aud-a29c).");
        }
    }

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next)
    {
        ArgumentNullException.ThrowIfNull(next);
        return context =>
        {
            if (_revocation.IsRevoked)
                return Refuse();

            // Confirmed and not revoked since: straight through, with no wait and no allocation.
            if (Volatile.Read(ref _verdict) is { IsCompletedSuccessfully: true, Result: true })
                return next(context);

            return HoldAsync(context, next);
        };
    }

    // An activity that reached a pending circuit. It runs only on a verdict that confirmed access,
    // and only if nothing has revoked the circuit in the meantime. Activities held together are
    // released together when the verdict arrives; they are not re-ordered on purpose, but nothing
    // here promises their arrival order either (see the aud-a29c record).
    private async Task HoldAsync(CircuitInboundActivityContext context, Func<CircuitInboundActivityContext, Task> next)
    {
        if (!await AwaitVerdictAsync(CancellationToken.None).ConfigureAwait(false) || _revocation.IsRevoked)
        {
            await Refuse().ConfigureAwait(false);
            return;
        }

        await next(context).ConfigureAwait(false);
    }

    // True only when the open check confirmed access. A revoked circuit answers at once. A check that
    // has not answered within HoldTimeout is treated as one that never will: the circuit is revoked
    // (audited once, by the provider) and the caller is told no.
    private async Task<bool> AwaitVerdictAsync(CancellationToken cancellationToken)
    {
        if (_revocation.IsRevoked)
            return false;

        var verdict = StartOpenCheck();
        try
        {
            return await verdict.WaitAsync(HoldTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            if (_authenticationState is MembershipRevalidatingAuthenticationStateProvider provider)
                provider.RevokeUnconfirmedOpenCheck(HoldTimeout);

            return false;
        }
    }

    // Starts the open check exactly once, from whichever gets here first: OnCircuitOpenedAsync, or an
    // inbound activity. The framework rebuilds the inbound pipeline with this gate in it before it
    // calls the handlers' OnCircuitOpenedAsync, so an activity can arrive first, and it must not find
    // a circuit that is neither checked nor being checked. Off-switch or no revalidating provider:
    // there is nothing to wait for.
    private Task<bool> StartOpenCheck()
    {
        lock (_sync)
        {
            if (_verdict is null)
            {
                _verdict = _options.Enabled && _authenticationState is MembershipRevalidatingAuthenticationStateProvider provider
                    ? Task.Run(() => provider.CheckAtCircuitOpenAsync(), CancellationToken.None)
                    : Confirmed;
            }

            return _verdict;
        }
    }

    private Task Refuse()
    {
        if (Interlocked.Exchange(ref _refusalLogged, 1) == 0)
            _logger.LogWarning("Refusing inbound activity on a circuit whose access was revoked by revalidation");

        return Task.CompletedTask;
    }
}
