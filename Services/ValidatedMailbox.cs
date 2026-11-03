namespace ExchangeAdminWeb.Services;

/// <summary>
/// The mailbox a protected-principal check validated, carried from the check to the write so the two
/// cannot name different objects (aud-a2d).
/// </summary>
/// <remarks>
/// aud-a2b made the check resolve the operator's string the way Exchange will, and the check binds a
/// synced mailbox to the one account in the forest holding the on-premises SID Microsoft Graph reports
/// for it (docs/ProtectedPrincipalIdentifiers-Plan.md C.2). The writes then handed Exchange the same string again, to be resolved again by
/// Exchange's own rules - so a change in the directory between the check and the write, or any rule
/// the two resolutions do not share, put the change on a mailbox nothing had checked.
///
/// This carries the answer instead of the question: Exchange's own mailbox GUID for the recipient the
/// check resolved and, when the check bound one, the Active Directory objectGUID of the account it
/// bound - the object found by SID. No operator types
/// either. There is no public constructor: an instance comes only from a check's result, and a
/// result without a usable GUID gives null, which every write refuses (<see cref="Refusal"/>). A
/// write never falls back to the typed string.
/// </remarks>
public sealed record ValidatedMailbox
{
    /// <summary>The <c>extra</c> key under which an audit row records the identity written.</summary>
    public const string AuditKey = "writtenIdentity";

    /// <summary>The <c>extra</c> key for the Active Directory object written, when one was.</summary>
    public const string DirectoryAuditKey = "writtenDirectoryObject";

    private ValidatedMailbox(Guid exchangeGuid, string? directoryObjectGuid)
    {
        ExchangeGuid = exchangeGuid;
        DirectoryObjectGuid = directoryObjectGuid;
    }

    /// <summary>Exchange's mailbox GUID: the mailbox every Exchange write is bound to.</summary>
    public Guid ExchangeGuid { get; }

    /// <summary>
    /// The objectGUID of the Active Directory account the check bound this mailbox to - the one object
    /// holding the on-premises SID Graph reports for it (C.2), or, where Graph reports none, the one
    /// object under its canonical address - or null when the check bound none: a cloud-only mailbox, or
    /// a check that did not consult Active Directory. The
    /// identity for writes Active Directory performs, never re-found from the typed string.
    /// </summary>
    public string? DirectoryObjectGuid { get; }

    /// <summary>
    /// The <c>-Identity</c> every Exchange write passes: the mailbox GUID in "D" form. Exchange
    /// Online and on-premises Exchange both accept a mailbox GUID as a mailbox identity - an
    /// assumption recorded in aud-a2d, which fails closed (the cmdlet reports the
    /// identity "couldn't be found") rather than open.
    /// </summary>
    public string Identity => ExchangeGuid.ToString("D");

    /// <summary>
    /// The mailbox a resolved principal names, or null when it names none. Only
    /// <see cref="ProtectedPrincipalService.ResolveExchangeTargetAsync"/> sets
    /// <see cref="ResolvedDirectoryPrincipal.ExchangeGuid"/>, so only its result can produce one.
    /// </summary>
    public static ValidatedMailbox? From(ResolvedDirectoryPrincipal? principal) =>
        principal?.ExchangeGuid is Guid guid && guid != Guid.Empty
            ? new ValidatedMailbox(guid,
                string.IsNullOrWhiteSpace(principal.ObjectGuid) ? null : principal.ObjectGuid)
            : null;

    /// <summary>
    /// The mailbox an Exchange recipient names, or null when it has none. No Active Directory
    /// object was proven on this path, so <see cref="DirectoryObjectGuid"/> is null.
    /// </summary>
    public static ValidatedMailbox? From(ResolvedRecipient? recipient) =>
        recipient?.ExchangeGuid is Guid guid && guid != Guid.Empty
            ? new ValidatedMailbox(guid, null)
            : null;

    /// <summary>
    /// What every write returns when it is handed no validated mailbox. Nothing has been written
    /// when this is returned.
    /// </summary>
    public static string Refusal(string typed) =>
        $"Refused: no mailbox identity was validated for '{typed}', so nothing was written. "
        + "A change is written only to the mailbox the protection check resolved, never to the "
        + "address as typed.";

    /// <summary>
    /// <paramref name="extra"/> with the identity written added, created when absent. The row's
    /// <c>target</c> stays the operator's typed string; this records what the write was bound to.
    /// A null <paramref name="mailbox"/> leaves <paramref name="extra"/> as it is.
    /// </summary>
    /// <param name="writtenDirectoryObject">
    /// The Active Directory object the operation reports it actually wrote (Conference Rooms Room
    /// Finder: <c>RoomOperationResult.WrittenDirectoryObject</c>), or null when it wrote none -
    /// recorded under <see cref="DirectoryAuditKey"/> only when given. It was a flag the caller set
    /// for the kind of operation, so every Room Finder row named an object whether or not Active
    /// Directory was written (aud-a2g). An Exchange-only write passes nothing.
    /// </param>
    public static Dictionary<string, object?>? WithWrittenIdentity(
        Dictionary<string, object?>? extra, ValidatedMailbox? mailbox, string? writtenDirectoryObject = null)
    {
        if (mailbox is null)
            return extra;

        extra ??= new Dictionary<string, object?>();
        extra[AuditKey] = mailbox.Identity;
        if (writtenDirectoryObject is not null)
            extra[DirectoryAuditKey] = writtenDirectoryObject;
        return extra;
    }
}
