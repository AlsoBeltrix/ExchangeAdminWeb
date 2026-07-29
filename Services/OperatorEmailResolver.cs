using System.Security.Principal;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Resolves the signed-in operator's own mailbox address from Active Directory, keyed by the
/// Windows SID the Negotiate scheme puts in the PrimarySid claim.
/// </summary>
/// <remarks>
/// <para>
/// Why this exists: the app authenticates with Negotiate only, and a Kerberos/NTLM token
/// carries no mail claim, no email claim, and no UPN claim -- so reading the address from
/// claims yields "" on every request. The address has to be looked up.
/// </para>
/// <para>
/// The lookup key is the SID, never the account name. A SID is immutable, unambiguous, and
/// domain-qualified; a name is none of those, and resolving a name through the wildcard
/// autocomplete search can return a same-named account from another trusted domain -- which
/// would mail mail-flow data to the wrong person. See
/// <c>docs/OperatorEmailResolution-Plan.md</c> ("Why the SID") and the precedent this follows,
/// <c>SelfServiceGroupService.ResolveCallerDn</c>.
/// </para>
/// <para>
/// Fail-soft throughout: every failure returns null. Callers branch on null only -- an empty
/// or whitespace string is never returned. A directory hiccup must not break a page whose only
/// dependency on this is a pre-filled text box.
/// </para>
/// </remarks>
public class OperatorEmailResolver
{
    private readonly IOperatorDirectory _directory;
    private readonly ILogger<OperatorEmailResolver> _logger;

    public OperatorEmailResolver(IOperatorDirectory directory, ILogger<OperatorEmailResolver> logger)
    {
        _directory = directory;
        _logger = logger;
    }

    /// <summary>
    /// Resolve the operator's address: <c>mail</c> if set, else <c>userPrincipalName</c>, else
    /// null. Runs the blocking directory call off the calling thread.
    /// </summary>
    /// <param name="primarySid">The value of the caller's PrimarySid claim. Anything that is
    /// not a well-formed SID -- null, blank, a DOMAIN\user string, a UPN -- returns null
    /// without touching the directory.</param>
    public virtual async Task<string?> ResolveAsync(string? primarySid)
    {
        if (!IsSecurityIdentifier(primarySid))
        {
            // Not an error worth a stack trace: an absent claim is a legitimate state that the
            // caller handles (the pre-fill is simply empty). The distinction matters when an
            // operator reports a blank box -- this line separates "no claim" from "AD down".
            _logger.LogWarning(
                "Operator email resolution skipped: the caller has no usable PrimarySid claim. " +
                "The address will be left empty.");
            return null;
        }

        try
        {
            // The directory call takes a process-wide lock that can wait up to 30 seconds, so
            // it must not run on the Blazor renderer thread -- it would freeze the circuit.
            // Same treatment ADIdentityAutocomplete gives the sibling search call.
            var user = await Task.Run(() => _directory.FindUserBySid(primarySid!));

            if (user is null)
            {
                _logger.LogWarning("Operator email resolution found no directory user for the signed-in principal.");
                return null;
            }

            // mail first, then UPN (owner: "UPN is the same as email here"). Whitespace never
            // wins over a real value, and the address is never synthesized from the account
            // name plus a domain -- that fails silently by mailing a plausible wrong address.
            return Trimmed(user.Email) ?? Trimmed(user.UserPrincipalName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Operator email resolution failed; the address will be left empty.");
            return null;
        }
    }

    /// <summary>
    /// Null for null, blank, or whitespace; otherwise the trimmed value. Keeps the "never
    /// return an empty string" contract in one place.
    /// </summary>
    private static string? Trimmed(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    /// <summary>
    /// True only for a well-formed Windows SID. This is the gate that keeps an account name
    /// from ever re-entering the identity path.
    /// </summary>
    private static bool IsSecurityIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            _ = new SecurityIdentifier(value.Trim());
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
