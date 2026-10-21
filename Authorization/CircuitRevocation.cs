namespace ExchangeAdminWeb.Authorization;

/// <summary>Why revalidation ended an open circuit's session (aud-a29b).</summary>
public enum CircuitRevocationReason
{
    /// <summary>The account is no longer in a consulted group its token carries.</summary>
    MembershipChanged,

    /// <summary>The account is disabled in the directory.</summary>
    AccountDisabled,

    /// <summary>The check could not be completed, so it could not confirm access, so it failed closed.</summary>
    LookupFailed,

    /// <summary>The check a circuit waits on when it opens never reached a verdict (aud-a29c).</summary>
    PendingCheckTimedOut,
}

/// <summary>
/// Whether this circuit's access has been revoked by revalidation. One per circuit (scoped).
/// </summary>
/// <remarks>
/// Set once and never cleared: a revoked circuit stays revoked for its whole life, whatever
/// principal a reconnect offers it later. Read by the provider (which refuses to take an
/// authenticated principal back), by <see cref="CircuitRevocationGate"/> (which refuses every
/// inbound activity) and by the session-ended notice (which says why). The first reason wins, so
/// when the interval loop and the open check both find a circuit invalid it is audited once and the
/// operator's message does not change under them.
/// </remarks>
public sealed class CircuitRevocation
{
    private readonly object _sync = new();
    private CircuitRevocationReason? _reason;

    public bool IsRevoked
    {
        get
        {
            lock (_sync)
                return _reason is not null;
        }
    }

    public CircuitRevocationReason? Reason
    {
        get
        {
            lock (_sync)
                return _reason;
        }
    }

    /// <summary>Records the revocation; false when the circuit was already revoked.</summary>
    public bool TryRevoke(CircuitRevocationReason reason)
    {
        lock (_sync)
        {
            if (_reason is not null)
                return false;

            _reason = reason;
            return true;
        }
    }
}

/// <summary>
/// What an operator whose session was ended is told. Plain words, and no group is named: the
/// person reading it may be the one whose access was just removed.
/// </summary>
public static class SessionEndedMessages
{
    public const string Title = "This session has ended";

    /// <summary>
    /// The way back. Under Kerberos a reload presents the same group list until Windows renews its
    /// ticket, which is why the advice goes past "reload".
    /// </summary>
    public const string ReloadAdvice =
        "Reload the page to carry on with the access you have now. If this keeps happening when "
        + "your access should not have changed, sign out of Windows and sign back in so your group "
        + "membership is refreshed, or contact the IT Service Desk.";

    public static string For(CircuitRevocationReason reason) => reason switch
    {
        CircuitRevocationReason.MembershipChanged =>
            "Your access was re-checked against Active Directory, and your account is no longer in "
            + "a group that this session's access came from.",
        CircuitRevocationReason.AccountDisabled =>
            "Your access was re-checked against Active Directory, and your account is disabled.",
        CircuitRevocationReason.PendingCheckTimedOut =>
            "Your access could not be confirmed against Active Directory in time when this page "
            + "connected, so this session was ended as a precaution.",
        _ =>
            "Your access could not be re-checked against Active Directory just now, so this session "
            + "was ended as a precaution.",
    };
}
