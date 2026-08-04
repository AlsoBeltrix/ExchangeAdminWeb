using System.Security.Principal;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Authorization;

/// <summary>
/// What shape a stored <c>section_access.group_value</c> has, and therefore how it must be
/// resolved to a SID.
/// </summary>
public enum StoredGroupValueKind
{
    /// <summary>Already a usable group SID. Passes through the migration untouched.</summary>
    Sid,

    /// <summary><c>NETBIOS\Name</c>. Must be resolved against THAT domain, not the local one.</summary>
    DomainQualified,

    /// <summary>A bare group name, resolved against the app's own domain.</summary>
    BareName,

    /// <summary>
    /// Cannot be resolved by any rule here. Never silently dropped - the caller reports it and
    /// halts, because losing a row silently removes an access grant with no audit trail.
    /// </summary>
    Unusable
}

/// <summary>
/// One stored value, split into the parts a resolver needs. <see cref="Sid"/> is set only for
/// <see cref="StoredGroupValueKind.Sid"/>; <see cref="NetBiosDomain"/> only for
/// <see cref="StoredGroupValueKind.DomainQualified"/>; <see cref="RejectionReason"/> only for
/// <see cref="StoredGroupValueKind.Unusable"/>.
/// </summary>
public sealed record StoredGroupValue(
    StoredGroupValueKind Kind,
    string Raw,
    string? Sid,
    string? NetBiosDomain,
    string? Name,
    string? RejectionReason);

/// <summary>
/// What a directory lookup for one stored value produced. <see cref="Ambiguous"/> is a distinct
/// state rather than "pick the first": two groups answering to one name is exactly the collision
/// this work removes, so guessing between them would re-introduce it at migration time.
/// </summary>
public enum GroupResolutionOutcome
{
    Resolved,
    NotFound,
    Ambiguous
}

/// <summary>
/// The pure core of section-access group identity: deciding whether a SID is usable as an
/// authorization subject, splitting a stored value into resolvable parts, and building the
/// directory filter that finds the group behind it.
///
/// Everything here is static and directory-free so it is testable on any host, including CI where
/// no Active Directory exists. The directory call itself lives with the migration; this file owns
/// every decision that call depends on. Same split, and the same reason, as
/// <c>ADDirectorySearchService.ClassifyOutcome</c>.
///
/// See docs/SectionAccessSidStorage-Plan.md.
/// </summary>
public static class SectionAccessGroupIdentity
{
    /// <summary>
    /// True when <paramref name="value"/> is a SID this app will accept as a section-access
    /// subject. See <see cref="SidRejectionReason"/> for what is refused and why.
    /// </summary>
    public static bool IsUsableGroupSid(string? value) => SidRejectionReason(value) is null;

    /// <summary>
    /// Why a SID is not usable as a section-access subject, or null when it is.
    /// </summary>
    /// <remarks>
    /// Four refusals, each closing a distinct hole:
    ///
    /// 1. <b>Not a SID at all.</b> The framework parser is the authority; no hand-rolled prefix
    ///    check.
    /// 2. <b>An SDDL alias.</b> <c>new SecurityIdentifier("BA")</c> SUCCEEDS and yields
    ///    BUILTIN\Administrators, so parse-success alone is not sufficient - the stored string
    ///    would then authorize a different principal than it names. Requiring an exact round-trip
    ///    to the canonical form rejects them. (A padded SID never reaches this check - the parser
    ///    itself refuses leading or trailing whitespace.) This trap is already
    ///    documented at <c>SelfServiceGroupService.IsSecurityIdentifier</c>; the rule is repeated
    ///    rather than shared because that one governs a USER identity for a bound
    ///    <c>-Identity</c> lookup, and this one governs an authorization subject with two extra
    ///    conditions that would be wrong there.
    /// 3. <b>Not a domain account SID.</b> <c>IsAccountSid()</c> is false for exactly the SIDs the
    ///    plan's Non-Goals refuse - <c>S-1-1-0</c> (Everyone), <c>S-1-5-32-*</c> (BUILTIN\*),
    ///    <c>S-1-5-11</c> (Authenticated Users), <c>S-1-5-18</c> (SYSTEM). They are unambiguous but
    ///    grant far more than an admin choosing "a group" intends. Verified against the framework
    ///    rather than assumed, so no blocklist has to be kept current.
    /// 4. <b>The domain SID itself, with no RID.</b> <c>S-1-5-21-a-b-c</c> parses, round-trips, and
    ///    reports <c>IsAccountSid()</c> true, but names a domain rather than a group. It is caught
    ///    by comparing against its own <c>AccountDomainSid</c>.
    ///
    /// A foreign domain's group SID is deliberately accepted: <c>winroot\Enterprise Admins</c> is a
    /// real cross-domain grant in this deployment and must survive the migration (plan Non-Goals).
    /// </remarks>
    public static string? SidRejectionReason(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "empty";

        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(value);
        }
        catch (ArgumentException)
        {
            return "not a valid SID";
        }

        if (!string.Equals(sid.Value, value, StringComparison.OrdinalIgnoreCase))
            return "not a canonical SID string (SDDL aliases such as 'BA' are not accepted)";

        if (!sid.IsAccountSid())
            return "a well-known SID, which grants more than a specific group";

        var domainSid = sid.AccountDomainSid;
        if (domainSid is not null && string.Equals(domainSid.Value, sid.Value, StringComparison.OrdinalIgnoreCase))
            return "a domain SID, not a group SID";

        return null;
    }

    /// <summary>
    /// Splits a stored group value into the parts a resolver needs.
    /// </summary>
    /// <remarks>
    /// The backslash is read as a NetBIOS domain separator, which is safe for THIS store and is
    /// not a general rule: a backslash also escapes a comma inside a distinguished name, and
    /// splitting on it there corrupts the DN (review finding ppv-2, which cost a valid group being
    /// refused as nonexistent). Section access has never stored a DN - verified 2026-08-03, 0 of 58
    /// prod rows contain '=' - and the admin page offers only group names. A DN-shaped value is
    /// therefore reported <see cref="StoredGroupValueKind.Unusable"/> rather than guessed at, so an
    /// unexpected shape halts loudly instead of being silently mangled into a different group.
    /// </remarks>
    public static StoredGroupValue Parse(string? raw)
    {
        var original = raw ?? string.Empty;

        if (string.IsNullOrWhiteSpace(original))
            return Unusable(original, "the value is empty");

        var v = original.Trim();

        // A group could in principle be NAMED "S-1-..."; treating that as a malformed SID rather
        // than a name is the safe way round, because the migration then halts and reports it
        // instead of resolving to something the operator did not intend.
        if (v.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            var reason = SidRejectionReason(v);
            return reason is null
                ? new StoredGroupValue(StoredGroupValueKind.Sid, original, v, null, null, null)
                : Unusable(original, $"looks like a SID but is {reason}");
        }

        var slash = v.IndexOf('\\');
        if (slash < 0)
            return new StoredGroupValue(StoredGroupValueKind.BareName, original, null, null, v, null);

        var domain = v[..slash];
        var name = v[(slash + 1)..];

        if (domain.Contains('='))
            return Unusable(original, "looks like a distinguished name, which this store does not hold");

        if (domain.Length == 0 || name.Length == 0)
            return Unusable(original, "has an empty domain or group name around the backslash");

        if (name.Contains('\\'))
            return Unusable(original, "contains more than one backslash");

        return new StoredGroupValue(StoredGroupValueKind.DomainQualified, original, null, domain, name, null);
    }

    /// <summary>
    /// The exact-match LDAP filter that finds the group behind a stored name.
    /// </summary>
    /// <remarks>
    /// All three of <c>sAMAccountName</c>, <c>cn</c> and <c>name</c> are queried because stored
    /// values are not consistently any one of them. The proof is in this deployment's own data:
    /// <c>$KOO300-S3AMUVVBVMI1</c> is a sAMAccountName whose <c>cn</c> is <c>Employees-All</c>, so a
    /// <c>cn</c>-only or <c>name</c>-only query returns nothing for it and a <c>sAMAccountName</c>-only
    /// query returns nothing for a row stored under its common name.
    ///
    /// <c>displayName</c> is deliberately absent: it is not unique in AD, so including it would
    /// manufacture the ambiguity this work exists to remove.
    ///
    /// Exact, never wildcard. An existence check that substring-matches would let <c>IAM</c> also
    /// find <c>IAM-Readers</c>; the same trap <c>ADDirectorySearchService.ValidateExists</c> and
    /// <c>FindUserBySid</c> each document.
    /// </remarks>
    public static string BuildGroupLookupFilter(string name)
    {
        var escaped = ProtectedPrincipalService.EscapeLdapFilter(name.Trim());
        return $"(|(sAMAccountName={escaped})(cn={escaped})(name={escaped}))";
    }

    /// <summary>
    /// The name to SHOW for a group: <c>DOMAIN\Name</c> where the domain is known, the bare name
    /// otherwise.
    /// </summary>
    /// <remarks>
    /// Display only - no authorization path reads it, and a stale one is cosmetic.
    ///
    /// Qualified because a bare name is ambiguous to a reader in exactly the way the stored value
    /// no longer is: this deployment authenticates three domains, and "ExchangeWebAdmins" does not
    /// say which one an entry grants. Storing SIDs removed that ambiguity from the data; showing a
    /// bare name puts it back in front of the operator, who is the one deciding whether a grant is
    /// correct.
    ///
    /// <c>DOMAIN\Name</c> rather than a UPN or mail address, verified against this directory:
    /// **no** security group carries a <c>userPrincipalName</c> (that attribute is for users), and
    /// only 5 of 8 sampled groups have <c>mail</c> - <c>ExchangeWebAdmins</c>, the admin group
    /// itself, has neither. A scheme that renders blank for the most privileged entry is not a
    /// display scheme.
    /// </remarks>
    public static string QualifiedDisplayName(string? netBiosDomain, string? name)
    {
        var bare = (name ?? string.Empty).Trim();

        if (string.IsNullOrEmpty(bare))
            return string.Empty;

        // Already qualified - do not double up. Values reach here from several places (the picker,
        // the migration, the store) and not all of them know whether the domain was prepended.
        if (bare.Contains('\\'))
            return bare;

        var domain = (netBiosDomain ?? string.Empty).Trim().TrimEnd('\\');

        return string.IsNullOrEmpty(domain) ? bare : $"{domain}\\{bare}";
    }

    /// <summary>
    /// Turns a match count into an outcome. Pure so the resolver's decision is provable without a
    /// directory: exactly one match resolves, zero is an affirmative absence, and two or more is
    /// ambiguous and must never be narrowed by picking one.
    /// </summary>
    public static GroupResolutionOutcome ClassifyMatchCount(int count) => count switch
    {
        1 => GroupResolutionOutcome.Resolved,
        <= 0 => GroupResolutionOutcome.NotFound,
        _ => GroupResolutionOutcome.Ambiguous
    };

    private static StoredGroupValue Unusable(string raw, string reason)
        => new(StoredGroupValueKind.Unusable, raw, null, null, null, reason);
}
