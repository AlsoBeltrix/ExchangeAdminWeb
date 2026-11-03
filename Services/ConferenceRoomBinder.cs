namespace ExchangeAdminWeb.Services;

/// <summary>
/// Why a Conference Rooms write was not bound to a room: the operator-facing <see cref="Message"/> and the
/// <see cref="AuditDetail"/> the caller records under its OWN action label (single-room Finder vs Type vs the bulk
/// <c>_Bulk</c> actions).
/// </summary>
public sealed record RoomBindingRefusal(string Message, string AuditDetail);

/// <summary>
/// Binds every Conference Rooms room-mutating write (single-room Finder and Type on the page, and each bulk row in
/// <see cref="Jobs.ConferenceRoomBulkProcessor"/>) to the room mailbox Exchange names, and - for a synced room - to the one
/// Active Directory account that room is synced from (aud-a2d). It is not a protection check: protected principals do not
/// apply to Conference Rooms (owner ruling 2026-11-03, <c>.agents/decisions.md</c>; Constitution, the module's scoped
/// exemption), so it reads no protection rules, consults no servicer grant and has no operator to evaluate.
/// </summary>
/// <remarks>
/// Check-then-run (Known Failure Class 1): the write is reachable only through the <c>onBound</c> delegate, invoked after
/// the binding is complete, so a refusal never reaches a side effect. docs/ProtectedPrincipalIdentifiers-Plan.md D.2:
/// <list type="number">
/// <item>Exchange is asked once (<see cref="IIdentityResolver.ResolveRecipientAsync"/>). It could not be asked, or knows no
/// such recipient: refused.</item>
/// <item>A recipient that is not a RoomMailbox / RemoteRoomMailbox is refused, in the words the service's read-back check
/// uses (that check stays); one with no mailbox GUID is refused as no room.</item>
/// <item>Synced: the account is bound through the shared SID binder
/// (<see cref="ProtectedPrincipalService.BindSyncedRecipientAsync"/>) - one directory-read credential fetch - and the typed
/// string must not name another Active Directory object (aud-a2b, a correctness rule: Exchange is handed the mailbox GUID
/// and Active Directory the objectGUID of what must be one room). Zero, many or unavailable: refused.</item>
/// <item>Cloud-only: the mailbox, with no Active Directory object; the Active Directory steps refuse without one.</item>
/// </list>
/// </remarks>
public sealed class ConferenceRoomBinder
{
    private readonly IIdentityResolver _exchange;
    private readonly ProtectedPrincipalService _directory;
    private readonly ILogger<ConferenceRoomBinder> _logger;

    public ConferenceRoomBinder(IIdentityResolver exchange, ProtectedPrincipalService directory, ILogger<ConferenceRoomBinder> logger)
    {
        _exchange = exchange;
        _directory = directory;
        _logger = logger;
    }

    /// <summary>
    /// Binds <paramref name="identity"/> to its room exactly once, then runs exactly one of the two delegates:
    /// <paramref name="onRefused"/> when there is no room to bind the write to, otherwise <paramref name="onBound"/> with the
    /// room. The write must live inside <paramref name="onBound"/> and be written to the room it is handed, never to the
    /// typed string, which is for the caller's messages and audit target only.
    /// </summary>
    public async Task<TResult> BindThenRunAsync<TResult>(
        string identity,
        Func<RoomBindingRefusal, TResult> onRefused,
        Func<ValidatedMailbox, Task<TResult>> onBound)
    {
        var (room, refusal) = await BindAsync(identity);
        if (refusal is not null)
            return onRefused(refusal);
        // Unreachable by construction - BindAsync returns a room or a refusal - and refused anyway, so nothing could be
        // handed a write with no room to bind it to.
        if (room is null)
            return onRefused(NoRoom(identity));
        return await onBound(room);
    }

    internal static RoomBindingRefusal NoRoom(string identity) => new(
        $"'{identity}' did not resolve to a room mailbox Exchange can identify, so the change cannot be bound to one. Check the address.",
        $"No mailbox identity for '{identity}' - blocked");

    /// <summary>What an Exchange lookup that could not be made is told as.</summary>
    internal const string ExchangeNotAsked = "Exchange Online could not be asked. Nothing was written.";

    /// <summary>The room's Active Directory account could not be read - no credential, a failed read, no such account.</summary>
    internal const string AccountUnavailable =
        "The room's Active Directory account could not be identified, so the change cannot be bound to it. Nothing was written.";

    /// <summary>
    /// aud-a2aa. What an <c>Ambiguous</c> binding is told as: Exchange Online and Active Directory naming different objects
    /// for the one string (aud-a2b), two objects carrying the room's on-premises SID, or the one carrying it not a user
    /// object. So the text names neither a class nor a directory, and says what to look at; the application log carries
    /// the specific reason.
    /// </summary>
    internal const string AmbiguousMessage =
        "Identity is ambiguous - it matches more than one directory object, or Exchange Online and Active Directory do "
        + "not name the same one. Check the address.";

    internal static string NotFound(string identity) => $"Room '{identity}' not found.";

    /// <summary>The wording of <c>ConferenceRoomService.ValidateRoomRecipientAsync</c>, whose read-back check stays.</summary>
    internal static string NotARoom(string identity, string? recipientTypeDetails) =>
        $"'{identity}' is a {(string.IsNullOrWhiteSpace(recipientTypeDetails) ? "recipient of no reported type" : recipientTypeDetails)}, not a room mailbox. Conference Room operations require RoomMailbox or RemoteRoomMailbox.";

    private static readonly HashSet<string> RoomRecipientTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "RoomMailbox", "RemoteRoomMailbox",
    };

    private static RoomBindingRefusal Refused(string message) => new(message, $"{message} - blocked");

    private async Task<(ValidatedMailbox? room, RoomBindingRefusal? refusal)> BindAsync(string identity)
    {
        ResolvedRecipient? recipient;
        try
        {
            recipient = await _exchange.ResolveRecipientAsync(identity);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Exchange lookup failed for room {Identity} - nothing written", identity);
            return (null, Refused(ExchangeNotAsked));
        }

        if (recipient is null)
            return (null, Refused(NotFound(identity)));

        if (!RoomRecipientTypes.Contains(recipient.RecipientTypeDetails ?? ""))
            return (null, Refused(NotARoom(identity, recipient.RecipientTypeDetails)));

        if (recipient.ExchangeGuid is not Guid guid || guid == Guid.Empty)
        {
            _logger.LogWarning("Refusing room write on {Identity} - Exchange reports no mailbox GUID for it", identity);
            return (null, NoRoom(identity));
        }

        if (!recipient.ExistsOnPrem)
            return (ValidatedMailbox.From(recipient), null);

        ResolvedDirectoryPrincipal? bound;
        ProtectedPrincipalService.ResolutionStatus status;
        try
        {
            (bound, status) = await _directory.BindSyncedRecipientAsync(identity, recipient);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Binding the Active Directory account of room {Identity} failed - nothing written", identity);
            return (null, Refused(AccountUnavailable));
        }

        if (status == ProtectedPrincipalService.ResolutionStatus.Ambiguous)
            return (null, Refused(AmbiguousMessage));
        if (status != ProtectedPrincipalService.ResolutionStatus.Resolved || bound is null)
            return (null, Refused(AccountUnavailable));

        // aud-a2d: the mailbox Exchange named, and the objectGUID of the account bound by its SID. The binder carries the
        // recipient's mailbox GUID; a principal that lost it, or carries another, is no room.
        var room = ValidatedMailbox.From(bound);
        if (room is null || room.ExchangeGuid != guid)
        {
            _logger.LogWarning("Refusing room write on {Identity} - the bound account does not carry the room's mailbox", identity);
            return (null, NoRoom(identity));
        }
        return (room, null);
    }
}
