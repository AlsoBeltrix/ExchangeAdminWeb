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
public sealed record ResolvedRecipient(
    string PrimarySmtpAddress,
    string? ExternalDirectoryObjectId,
    string? RecipientType,
    bool ExistsOnPrem);

public interface IIdentityResolver
{
    Task<string?> ResolveToObjectIdAsync(string identity);

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
}
