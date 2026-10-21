using System.Security.Claims;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;

namespace ExchangeAdminWeb.Authorization;

/// <summary>
/// The circuit's authentication state, re-validated against the directory while the circuit is
/// open (aud-a29b, option 1 of the record).
/// </summary>
/// <remarks>
/// <para>
/// Replaces the framework's <c>ServerAuthenticationStateProvider</c>, which keeps the principal a
/// circuit opened with for as long as it lives. The framework loop calls
/// <see cref="ValidateAuthenticationStateAsync"/> every <see cref="CircuitRevalidationOptions.Interval"/>,
/// and <see cref="CircuitRevocationGate"/> adds one check when a circuit opens, because under
/// Kerberos a reload presents the same group SIDs as before - and holds the new circuit until that
/// check has answered (aud-a29c).
/// </para>
/// <para>
/// On an invalid verdict the order is fixed: the circuit's <see cref="CircuitRevocation"/> is set,
/// then the audit line is written, then the principal is replaced with an anonymous one. The
/// revocation comes first so nothing that can fail afterwards - the audit included - can leave the
/// circuit unrevoked.
/// </para>
/// <para>
/// Re-implements <see cref="IHostEnvironmentAuthenticationStateProvider"/>. The framework hands a
/// circuit its principal through that interface, and not only when it opens: a SignalR reconnect
/// (<c>ComponentHub.ConnectCircuit</c> -&gt; <c>CircuitHost.SetCircuitUser</c>) sets the new
/// connection's principal, which under Kerberos carries the same stale groups. Once revoked, the
/// circuit is only ever given an anonymous one.
/// </para>
/// </remarks>
public sealed class MembershipRevalidatingAuthenticationStateProvider
    : RevalidatingServerAuthenticationStateProvider, IHostEnvironmentAuthenticationStateProvider
{
    internal const string AuditAction = "SessionRevalidation";
    internal const string AuditCategory = "Authentication";
    internal const string TriggerInterval = "Interval";
    internal const string TriggerCircuitOpened = "CircuitOpened";
    internal const string TriggerStartup = "Startup";
    internal const string ReasonDisabled = "Disabled";
    internal const string SystemActor = "System";
    internal const string SystemAddress = "localhost";

    private static readonly Task<AuthenticationState> Anonymous =
        Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));

    private readonly CircuitMembershipRevalidator _revalidator;
    private readonly CircuitRevocation _revocation;
    private readonly CircuitRevalidationOptions _options;
    private readonly AuditService _audit;
    private readonly ClientInfoService _clientInfo;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposed = new();

    public MembershipRevalidatingAuthenticationStateProvider(
        ILoggerFactory loggerFactory,
        CircuitMembershipRevalidator revalidator,
        CircuitRevocation revocation,
        CircuitRevalidationOptions options,
        AuditService audit,
        ClientInfoService clientInfo)
        : base(loggerFactory)
    {
        _revalidator = revalidator;
        _revocation = revocation;
        _options = options;
        _audit = audit;
        _clientInfo = clientInfo;
        _logger = loggerFactory.CreateLogger<MembershipRevalidatingAuthenticationStateProvider>();
    }

    protected override TimeSpan RevalidationInterval => _options.Interval;

    protected override Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
        => ValidateAsync(authenticationState.User, TriggerInterval, cancellationToken);

    // The framework's way in - circuit creation, server rendering and, the reason this exists, a
    // reconnect. The framework's own sign-out calls the base method directly and is unaffected.
    void IHostEnvironmentAuthenticationStateProvider.SetAuthenticationState(Task<AuthenticationState> authenticationStateTask)
        => SetAuthenticationState(_revocation.IsRevoked ? Anonymous : authenticationStateTask);

    /// <summary>
    /// True when <paramref name="user"/> may keep its access. False revokes the circuit first, so a
    /// caller that only signs out cannot leave it usable.
    /// </summary>
    /// <remarks>
    /// Every failure, including one this code did not anticipate, is a revocation. The framework
    /// loop would sign the circuit out on a throw too, but without setting the revocation - and so
    /// without the gate, the reconnect refusal or the audit line.
    /// </remarks>
    internal async Task<bool> ValidateAsync(ClaimsPrincipal user, string trigger, CancellationToken cancellationToken)
    {
        // The operator's off-switch. The one place both the interval loop and the open check pass
        // through, so off means no directory read and no revocation on either path. Startup has
        // already logged and audited that it is off (ReportIfDisabled).
        if (!_options.Enabled)
            return true;

        if (_revocation.IsRevoked)
            return false;

        MembershipRevalidationResult result;
        try
        {
            result = await _revalidator.RevalidateAsync(user, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw;
        }
        catch (Exception ex)
        {
            result = MembershipRevalidationResult.LookupFailed($"The access re-check failed unexpectedly: {ex.Message}");
        }

        if (result.IsValid)
            return true;

        Revoke(user, result, trigger);
        return false;
    }

    /// <summary>
    /// The check run once when a circuit opens; started by <see cref="CircuitRevocationGate"/>, which
    /// holds the circuit until this answers. True when access was confirmed, or there was nothing to
    /// confirm (an unauthenticated circuit); false when it revoked the circuit, or the circuit closed
    /// first. Never throws. Stops quietly once the circuit is disposed - there is no session left to
    /// end, and nothing it held may run.
    /// </summary>
    internal async Task<bool> CheckAtCircuitOpenAsync()
    {
        var cancellationToken = _disposed.Token;
        ClaimsPrincipal? user = null;
        try
        {
            user = (await GetAuthenticationStateAsync().ConfigureAwait(false)).User;
            if (user.Identity?.IsAuthenticated != true)
                return true;

            if (await ValidateAsync(user, TriggerCircuitOpened, cancellationToken).ConfigureAwait(false))
                return true;

            SetAuthenticationState(Anonymous);
            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The circuit-open access check failed; ending the session");
            Revoke(user, MembershipRevalidationResult.LookupFailed($"The access check failed: {ex.Message}"), TriggerCircuitOpened);
            SetAuthenticationState(Anonymous);
            return false;
        }
    }

    /// <summary>
    /// The gate's backstop (aud-a29c): the open check did not answer within the gate's hold, so the
    /// circuit's access was never confirmed. Revokes with
    /// <see cref="CircuitRevocationReason.PendingCheckTimedOut"/>, audits once, signs the circuit out.
    /// </summary>
    /// <remarks>
    /// The account is read only if the state is already there: the stuck check may be waiting on that
    /// very state, and the audit line then names "Unknown" rather than block.
    /// </remarks>
    internal void RevokeUnconfirmedOpenCheck(TimeSpan waited)
    {
        ClaimsPrincipal? user = null;
        try
        {
            var state = GetAuthenticationStateAsync();
            if (state.IsCompletedSuccessfully)
                user = state.Result.User;
        }
        catch (InvalidOperationException)
        {
            // No state was ever set: nothing to name.
        }

        Revoke(
            user,
            new MembershipRevalidationResult(
                CircuitRevocationReason.PendingCheckTimedOut,
                Array.Empty<string>(),
                $"The access check that runs when a circuit opens did not finish within {waited.TotalSeconds:0.#} seconds."),
            TriggerCircuitOpened);
        SetAuthenticationState(Anonymous);
    }

    /// <summary>
    /// When the off-switch is set, says so once at startup: one warning line and one audit line.
    /// True when it reported.
    /// </summary>
    /// <remarks>
    /// Revocation that is switched off must not look like revocation that found nothing. The audit
    /// line uses this provider's own action and category with reason <c>Disabled</c>, so anyone
    /// reading the trail for ended sessions also finds the period in which none could be ended. It
    /// is filed Failed so its explanation is kept; the actor is <c>System</c>, as no operator caused
    /// it. An audit failure is logged and does not stop startup.
    /// </remarks>
    internal static bool ReportIfDisabled(CircuitRevalidationOptions options, AuditService audit, ILogger logger)
    {
        if (options.Enabled)
            return false;

        logger.LogWarning(
            "Revocation revalidation is disabled by configuration ({Key} = false): an operator removed "
            + "from an AD group, or disabled, keeps their access on any tab already open until it is reloaded",
            CircuitRevalidationSettings.EnabledKey);

        try
        {
            audit.LogModuleAction(
                SystemActor, SystemAddress, AuditAction, AuditCategory, CircuitRevalidationSettings.EnabledKey,
                success: false,
                errorDetail: "Revocation revalidation is disabled by configuration. An operator removed from "
                    + "an AD group, or disabled, keeps their access on any tab already open until it is reloaded.",
                extra: new Dictionary<string, object?>
                {
                    ["reason"] = ReasonDisabled,
                    ["trigger"] = TriggerStartup,
                });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit failed for the disabled revocation revalidation");
        }

        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _disposed.Cancel();

        base.Dispose(disposing);
    }

    /// <summary>
    /// Revokes the circuit and writes its one audit line. A second call for the same circuit does
    /// neither.
    /// </summary>
    /// <remarks>
    /// The line names the operator, the reason, what triggered the check and, for a membership
    /// change, the removed group SIDs - SIDs only, the convention <c>LogSettingsChange</c> already
    /// follows; never a group name. An audit failure is logged and does not undo the revocation.
    /// </remarks>
    private void Revoke(ClaimsPrincipal? user, MembershipRevalidationResult result, string trigger)
    {
        if (!_revocation.TryRevoke(result.Reason ?? CircuitRevocationReason.LookupFailed))
            return;

        var account = user?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(account))
            account = "Unknown";

        _logger.LogWarning(
            "Session for {User} ended by access revalidation ({Trigger}): {Reason}. {Detail}",
            account, trigger, result.Reason, result.Detail);

        try
        {
            var extra = new Dictionary<string, object?>
            {
                ["reason"] = (result.Reason ?? CircuitRevocationReason.LookupFailed).ToString(),
                ["trigger"] = trigger,
            };

            if (result.RemovedGroupSids.Count > 0)
                extra["removedGroupSids"] = result.RemovedGroupSids.ToArray();

            _audit.LogModuleAction(
                account, _clientInfo.IpAddress, AuditAction, AuditCategory, account,
                success: false, errorDetail: result.Detail, extra: extra);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit failed for a session ended by access revalidation");
        }
    }
}
