using System.Runtime.CompilerServices;
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
/// <para>
/// kd-90. Everything the revalidation path reads of a principal - its account SID and groups for
/// the check, its name for the warning and the audit line - comes from a copy of its claims taken
/// when the principal is handed to the circuit (or first read by it), never from the identity
/// again: the token behind a <c>WindowsIdentity</c> is disposed with the request that carried it.
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
    internal const string TriggerPageRequest = "PageRequest";
    internal const string ReasonDisabled = "Disabled";
    internal const string SystemActor = "System";
    internal const string SystemAddress = "localhost";

    /// <summary>
    /// What the off-switch means for an operator whose access was removed, said in the startup warning
    /// and the audit line alike (aud-a29h). Not "until it is reloaded": with revalidation off nothing
    /// consults the directory at all, and under Kerberos a reload brings back the ticket's groups.
    /// </summary>
    internal const string DisabledConsequence =
        "Nothing re-checks an operator's group membership against the directory - not an open tab, not a "
        + "reload, not a page request. An operator removed from an AD group, or disabled, keeps the access "
        + "their Windows sign-in carries: under Kerberos every reload and page request brings back the same "
        + "groups until their ticket is renewed or expires, and a tab already open keeps its access until it "
        + "is closed or the app restarts.";

    private static readonly Task<AuthenticationState> Anonymous =
        Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));

    private readonly CircuitMembershipRevalidator _revalidator;
    private readonly CircuitRevocation _revocation;
    private readonly CircuitRevalidationOptions _options;
    private readonly AuditService _audit;
    private readonly ClientInfoService _clientInfo;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _disposed = new();

    // The grants this circuit was confirmed through (aud-a29d): captured by the first check (the
    // circuit-open check) and added to by every valid check after it. Only ever grows - a grant
    // leaves this set only with the circuit - so a grant removed from the store cannot quietly drop
    // out of what is compared.
    private readonly object _grantsSync = new();
    private readonly HashSet<AuthorizationGrant> _grants = new();

    // kd-90. What the revalidation path reads of a principal: a copy of its claims - its name, its account SID, its groups -
    // as plain strings, taken once, the first time this circuit is handed the principal or reads it, and read in its place
    // ever after. The framework disposes a WindowsIdentity's token with the request that carried it and the circuit outlives
    // that request, so a later read of the identity itself throws ObjectDisposedException (dev app-20261030.log 09:45:36: in
    // Revoke, past the revocation, before its warning and its audit line, so the operator was signed out silently). Keyed by
    // the principal: a reconnect's new principal is copied when it is handed over, and a principal no longer held is
    // collected with its copy.
    private readonly ConditionalWeakTable<ClaimsPrincipal, ClaimsPrincipal> _copies = new();

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

    /// <summary>A copy of the grants this circuit was confirmed through.</summary>
    internal IReadOnlySet<AuthorizationGrant> CapturedGrants
    {
        get
        {
            lock (_grantsSync)
                return new HashSet<AuthorizationGrant>(_grants);
        }
    }

    protected override Task<bool> ValidateAuthenticationStateAsync(
        AuthenticationState authenticationState, CancellationToken cancellationToken)
        => ValidateAsync(authenticationState.User, TriggerInterval, cancellationToken);

    // The framework's way in - circuit creation, server rendering and, the reason this exists, a
    // reconnect. The framework's own sign-out calls the base method directly and is unaffected.
    // kd-90: the principal is copied here, inside the request that carries its token, before anything needs to read it.
    void IHostEnvironmentAuthenticationStateProvider.SetAuthenticationState(Task<AuthenticationState> authenticationStateTask)
    {
        if (!_revocation.IsRevoked && authenticationStateTask.IsCompletedSuccessfully)
            _ = Copy(authenticationStateTask.Result.User, out _);

        SetAuthenticationState(_revocation.IsRevoked ? Anonymous : authenticationStateTask);
    }

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

        // kd-90. The copy from here on, never the live identity: the check reads its SID and groups, the revocation its name.
        // A principal that could not be read even the first time is not one whose access can be confirmed.
        var copy = Copy(user, out var unreadable);
        if (copy is null)
        {
            Revoke(user, MembershipRevalidationResult.LookupFailed(
                $"The signed-in principal could not be read, so its access cannot be re-checked: {unreadable}"), trigger);
            return false;
        }

        MembershipRevalidationResult result;
        try
        {
            result = await _revalidator.RevalidateAsync(copy, CapturedGrants, cancellationToken).ConfigureAwait(false);
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
        {
            // What this check confirmed joins what the circuit is held to; nothing is taken out, so
            // a grant first seen now is watched from now on (aud-a29d).
            lock (_grantsSync)
                _grants.UnionWith(result.Grants);

            return true;
        }

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
            // kd-90: whether it is authenticated, from the copy too; one that cannot be read is checked, and refused there.
            if (Copy(user, out _) is { } copy && copy.Identity?.IsAuthenticated != true)
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
            "Revocation revalidation is disabled by configuration ({Key} = false). " + DisabledConsequence,
            CircuitRevalidationSettings.EnabledKey);

        try
        {
            audit.LogModuleAction(
                SystemActor, SystemAddress, AuditAction, AuditCategory, CircuitRevalidationSettings.EnabledKey,
                success: false,
                errorDetail: "Revocation revalidation is disabled by configuration. " + DisabledConsequence,
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

        // kd-90. The name from the principal's copy, never its live identity. Read from a WindowsIdentity whose token was
        // disposed with its request it threw here - past the revocation, so the circuit was signed out with neither the
        // warning nor the audit line below.
        var account = AccountOf(user);

        _logger.LogWarning(
            "Session for {User} ended by access revalidation ({Trigger}): {Reason}. {Detail}",
            account, trigger, result.Reason, result.Detail);

        WriteAuditLine(_audit, _logger, account, _clientInfo.IpAddress, result, trigger);
    }

    // kd-90. The account a revocation names: the name in the principal's copy, or "Unknown" when there is none - never the
    // live identity and never a throw, so the warning and the audit line after it are always written.
    private string AccountOf(ClaimsPrincipal? user)
    {
        string? account = null;
        try
        {
            account = Copy(user, out _)?.Identity?.Name;
        }
        catch (Exception)
        {
            // Copy itself does not throw; this keeps the two lines after the revocation unconditional whatever a principal does.
        }

        return string.IsNullOrWhiteSpace(account) ? "Unknown" : account;
    }

    // kd-90. The principal's copy, taken the first time this circuit sees the principal and kept beside it; null, with why,
    // when it cannot be read even then.
    private ClaimsPrincipal? Copy(ClaimsPrincipal? user, out string? unreadable)
    {
        unreadable = null;
        if (user is null)
            return null;

        try
        {
            return _copies.GetValue(user, Detached);
        }
        catch (Exception ex)
        {
            unreadable = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// kd-90. A principal of plain claims copied from <paramref name="user"/>: each identity's claims, authentication type,
    /// name and role claim types, read now and nothing behind them - no token, no handle - so reading it can never throw.
    /// </summary>
    internal static ClaimsPrincipal Detached(ClaimsPrincipal user) =>
        new(user.Identities
            .Select(identity => new ClaimsIdentity(
                identity.Claims
                    .Select(claim => new Claim(claim.Type, claim.Value, claim.ValueType, claim.Issuer, claim.OriginalIssuer))
                    .ToList(),
                identity.AuthenticationType, identity.NameClaimType, identity.RoleClaimType))
            .ToList());

    /// <summary>
    /// The one audit line for access revalidation refusing an operator - a circuit it ended, or a page
    /// request it refused (aud-a29g). One writer, so the trail reads the same whichever refused.
    /// </summary>
    /// <remarks>
    /// Names the operator, the reason, what triggered the check and, for a membership change, the
    /// removed group SIDs - SIDs only, never a group name. A refused page request also names the page
    /// it asked for (<paramref name="requestPath"/>, aud-a29i); a circuit has no request and passes
    /// none. An audit failure is logged and never undoes the refusal it records.
    /// </remarks>
    internal static void WriteAuditLine(
        AuditService audit, ILogger logger, string account, string ipAddress, MembershipRevalidationResult result, string trigger,
        string? requestPath = null)
    {
        try
        {
            var extra = new Dictionary<string, object?>
            {
                ["reason"] = (result.Reason ?? CircuitRevocationReason.LookupFailed).ToString(),
                ["trigger"] = trigger,
            };

            if (result.RemovedGroupSids.Count > 0)
                extra["removedGroupSids"] = result.RemovedGroupSids.ToArray();

            if (requestPath is not null)
                extra["path"] = requestPath;

            audit.LogModuleAction(
                account, ipAddress, AuditAction, AuditCategory, account,
                success: false, errorDetail: result.Detail, extra: extra);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit failed for an access revalidation refusal ({Trigger})", trigger);
        }
    }
}
