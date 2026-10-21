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
/// Kerberos a reload presents the same group SIDs as before.
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
    /// The check run once when a circuit opens; started by <see cref="CircuitRevocationGate"/>.
    /// Never throws. Stops quietly once the circuit is disposed - there is no session left to end.
    /// </summary>
    internal async Task CheckAtCircuitOpenAsync()
    {
        var cancellationToken = _disposed.Token;
        ClaimsPrincipal? user = null;
        try
        {
            user = (await GetAuthenticationStateAsync().ConfigureAwait(false)).User;
            if (user.Identity?.IsAuthenticated != true)
                return;

            if (!await ValidateAsync(user, TriggerCircuitOpened, cancellationToken).ConfigureAwait(false))
                SetAuthenticationState(Anonymous);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The circuit-open access check failed; ending the session");
            Revoke(user, MembershipRevalidationResult.LookupFailed($"The access check failed: {ex.Message}"), TriggerCircuitOpened);
            SetAuthenticationState(Anonymous);
        }
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
