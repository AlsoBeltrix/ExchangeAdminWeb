using System.Text;

namespace ExchangeAdminWeb.Services.SelfServiceGroups;

/// <summary>
/// Pure, injection-safe construction of the AD ownership reverse-lookup filter (plan task 1, codex
/// F11). Kept separate from the live PowerShell query so the escaping and immutable-id validation -
/// the security-critical parts - are unit-testable without AD. The lookup finds groups the caller
/// owns via <c>managedBy</c> or the Exchange multi-owner <c>msExchCoManagedByLink</c>, keyed on the
/// caller's DISTINGUISHED NAME (both attributes hold DN-valued links).
///
/// All string values are escaped for the PowerShell AD provider's -LDAPFilter (RFC 4515 filter
/// escaping) before interpolation. No PowerShell string interpolation of raw identity input is ever
/// emitted (F11); callers pass the returned filter to Get-ADGroup -LDAPFilter as a bound value.
/// </summary>
public static class AdOwnershipFilter
{
    /// <summary>
    /// Escapes a value for use inside an RFC 4515 LDAP search filter. Escapes the five special
    /// characters ( ) * \ NUL as \XX hex, per the spec, so a DN carrying parentheses or other
    /// metacharacters cannot alter the filter's structure.
    /// </summary>
    public static string EscapeLdapFilterValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\5c"); break;
                case '*': sb.Append("\\2a"); break;
                case '(': sb.Append("\\28"); break;
                case ')': sb.Append("\\29"); break;
                case '\0': sb.Append("\\00"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Builds the LDAP filter selecting groups where the given caller DN is the managedBy owner OR a
    /// listed co-manager. The DN is LDAP-escaped; the result is a complete, structurally-fixed
    /// filter safe to pass to Get-ADGroup -LDAPFilter.
    /// </summary>
    /// <param name="callerDistinguishedName">The caller's resolved, non-empty DN.</param>
    public static string BuildOwnedGroupsFilter(string callerDistinguishedName)
    {
        if (string.IsNullOrWhiteSpace(callerDistinguishedName))
            throw new ArgumentException("Caller distinguished name is required.", nameof(callerDistinguishedName));

        var dn = EscapeLdapFilterValue(callerDistinguishedName);
        // objectCategory=group bounds the result to groups; the OR covers single-owner (managedBy)
        // and Exchange multi-owner (msExchCoManagedByLink) linkage.
        return $"(&(objectCategory=group)(|(managedBy={dn})(msExchCoManagedByLink={dn})))";
    }
}
