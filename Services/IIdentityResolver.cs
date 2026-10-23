namespace ExchangeAdminWeb.Services;

/// <summary>
/// A recipient as Exchange Online resolved it. Returned only when Exchange affirmatively
/// answered; a lookup that could not run throws instead (see
/// <see cref="IIdentityResolver.ResolveRecipientAsync"/>).
/// </summary>
/// <param name="PrimarySmtpAddress">
/// The canonical primary SMTP address. Exchange normalizes any secondary alias to this, which
/// is what lets the protected-principal check re-resolve an alias-addressed target against its
/// real identity.
/// </param>
/// <param name="ExistsOnPrem">
/// True when Exchange reports the recipient as backed by a synced on-premises directory object;
/// false when it reports it cloud-only. Never a guess: a recipient whose sync state Exchange did
/// not report makes the lookup throw (aud-a2b hole 3). "Undetermined" used to read as false,
/// which sent a synced recipient down the cloud-only branch and skipped its group and OU rules.
/// </param>
/// <param name="ExchangeGuid">
/// The mailbox GUID Exchange reports, or null when it reports none (groups, contacts and mail
/// users without a mailbox report all zeros, which is read as none). For a synced mailbox this is
/// the value its on-premises object holds in <c>msExchMailboxGuid</c>, and it is what the
/// protected-principal check requires that object to prove before it is accepted (aud-a2c). An
/// address can be carried by any AD object; this cannot be, by accident.
/// </param>
public sealed record ResolvedRecipient(
    string PrimarySmtpAddress,
    string? ExternalDirectoryObjectId,
    string? RecipientType,
    bool ExistsOnPrem,
    Guid? ExchangeGuid = null);

/// <summary>One member of an Exchange Online group, as Exchange reported it (aud-a52).</summary>
/// <param name="PrimarySmtpAddress">The member's primary address; null when Exchange reported none.</param>
/// <param name="ExternalDirectoryObjectId">The member's directory object id; null when Exchange reported none.</param>
/// <param name="IsDirSynced">Exchange's directory-synced flag for the member; null when it reported none.</param>
public sealed record ExchangeGroupMember(
    string? PrimarySmtpAddress,
    string? ExternalDirectoryObjectId,
    string? RecipientType,
    string? RecipientTypeDetails,
    bool? IsDirSynced)
{
    /// <summary>Every kind of group Exchange reports carries "Group" in its RecipientType.</summary>
    public bool IsGroup => RecipientType?.Contains("Group", StringComparison.OrdinalIgnoreCase) == true;
}

/// <remarks>
/// There is deliberately no object-id-only lookup. <c>ResolveToObjectIdAsync</c> answered a lookup that could not run as
/// null, and the self-grant check took that null for "no such recipient" and cleared the grant (aud-a28y). Every caller asks
/// <see cref="ResolveRecipientAsync"/>, which throws instead.
/// </remarks>
public interface IIdentityResolver
{
    /// <summary>
    /// Resolves an identity through Exchange Online.
    /// </summary>
    /// <returns>
    /// The recipient, or <c>null</c> when Exchange affirmatively reported no such recipient.
    /// </returns>
    /// <exception cref="Exception">
    /// Thrown when the lookup could not be performed at all (EXO unreachable, not configured,
    /// credential failure). Callers MUST distinguish this from a <c>null</c> return: a failed
    /// lookup is not evidence of absence, and treating it as such would let an EXO outage
    /// un-protect a principal. See docs/ProtectedPrincipalResolution-Plan.md, Design.
    /// </exception>
    Task<ResolvedRecipient?> ResolveRecipientAsync(string identity);

    /// <summary>
    /// The members of the Exchange Online group <paramref name="group"/>, read through every cloud group nested in it; a
    /// nested group synced from on-premises is listed and not read - its membership is the directory's (aud-a52).
    /// </summary>
    /// <returns>Every member Exchange reported, nested groups' members included; never null.</returns>
    /// <exception cref="Exception">
    /// Thrown when the membership could not be read in full: Exchange Online unreachable, unreadable or not configured, the
    /// group not found, a group of a kind the read does not expand, a nested group without an address or a sync state, or
    /// more groups than it reads. Never an empty list standing in for a read that did not complete: the self-grant check
    /// takes the answer for evidence.
    /// </exception>
    Task<IReadOnlyList<ExchangeGroupMember>> ReadGroupMembersAsync(string group);
}
