using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

/// <summary>One user-class object a lookup found: its DN and objectSid (null when it carries none).</summary>
internal sealed record DirectoryUserObject(string DistinguishedName, string? Sid);

/// <summary>
/// The directory reads the identifier migration makes (docs/ProtectedPrincipalIdentifiers-Plan.md B.4): the group check's
/// reads for groups, the <c>ou_guid</c> comparison's read for OUs, and one equality search for users.
/// </summary>
/// <remarks>
/// Every member returns the directory's answer or throws; none reports "no rows" for a read that did not complete, because
/// the resolver reads an empty answer as "not found".
/// </remarks>
internal interface IProtectedPrincipalResolveDirectory : IProtectedGroupDirectory, IProtectedOuDirectory
{
    /// <summary>
    /// The user-class objects <paramref name="ldapFilter"/> matches on <paramref name="server"/>: a domain controller of that
    /// domain, or - null - a global catalog for the whole forest.
    /// </summary>
    IReadOnlyList<DirectoryUserObject> FindUsers(string ldapFilter, string? server);
}

/// <summary>One legacy row the migration reads: its kind (<c>user</c>, <c>group</c> or <c>ou</c>) and its value as stored.</summary>
public sealed record ProtectedPrincipalLegacyRow(string Kind, string Value);

/// <summary>Why a legacy row was not converted. The row is left exactly as it was and enforced as before.</summary>
public enum ProtectedPrincipalLeftReason
{
    /// <summary>The directory answered and holds no object matching the value.</summary>
    NotFound,

    /// <summary>More than one object matches the value, so it names no one object.</summary>
    Ambiguous,

    /// <summary>The value resolves to a built-in or well-known SID, which names no one domain group.</summary>
    WellKnownSid,

    /// <summary>The lookup could not run, or its answer could not be read. The next pass tries again.</summary>
    DirectoryUnavailable,

    /// <summary>The value has a shape the pass does not look up; re-pick the entry from the search.</summary>
    Unsupported,
}

/// <summary>
/// One row's lookup: the identifier it converts to, or why it does not. Exactly one of <see cref="Identifier"/> and
/// <see cref="Reason"/> is set.
/// </summary>
public sealed record ProtectedPrincipalRowResolution(
    ProtectedPrincipalLegacyRow Row,
    string? Identifier,
    ProtectedPrincipalLeftReason? Reason,
    string? Detail)
{
    /// <summary>The identifier kind this row converts into: <c>user_sid</c>, <c>group_sid</c> or <c>ou_guid</c>.</summary>
    public string IdKind => ProtectedPrincipalIdentifierResolver.IdKindOf(Row.Kind);

    internal static ProtectedPrincipalRowResolution Converted(ProtectedPrincipalLegacyRow row, string identifier)
        => new(row, identifier, null, null);

    internal static ProtectedPrincipalRowResolution Left(
        ProtectedPrincipalLegacyRow row, ProtectedPrincipalLeftReason reason, string detail)
        => new(row, null, reason, detail);
}

/// <summary>
/// Resolves legacy protected-principal rows to one directory object each (docs/ProtectedPrincipalIdentifiers-Plan.md B.4).
/// Pure over <see cref="IProtectedPrincipalResolveDirectory"/>, so the exactly-one rule and the reason each refusal is
/// recorded with are testable without a domain controller.
/// </summary>
/// <remarks>
/// <para>Each row is looked up once, in the directory its shape names:</para>
/// <para>- A user: an address (UPN or mail) and a bare sAMAccountName through the global catalog, so a user of any domain of
/// the forest is found and a name two domains share is ambiguous rather than whichever answered first; <c>DOMAIN\name</c>
/// in the domain the prefix names; a DN by a read of that DN in its own domain. Always <c>objectCategory=person</c> and
/// <c>objectClass=user</c>, so a computer account - also of class user - is never the match.</para>
/// <para>- A group: <see cref="ProtectedGroupMembership.Resolve"/>, the lookup the protection check itself binds the row
/// with, so the conversion names the group the check was already enforcing.</para>
/// <para>- An OU: a read of the DN in its own domain for the objectGUID.</para>
/// <para>Exactly one match converts. Zero, several, a well-known SID or a lookup that could not run leaves the row - it is
/// enforced by name exactly as before (decision D1) - and records why. A lookup that throws is recorded as
/// <see cref="ProtectedPrincipalLeftReason.DirectoryUnavailable"/> for that row alone; the others still resolve.</para>
/// </remarks>
internal static class ProtectedPrincipalIdentifierResolver
{
    internal const string PersonFilter = "(objectCategory=person)(objectClass=user)";

    /// <summary>The identifier kind a legacy kind converts into.</summary>
    internal static string IdKindOf(string legacyKind) => legacyKind switch
    {
        ProtectedPrincipalRepository.KindUser => ProtectedPrincipalRepository.KindUserSid,
        ProtectedPrincipalRepository.KindGroup => ProtectedPrincipalRepository.KindGroupSid,
        ProtectedPrincipalRepository.KindOu => ProtectedPrincipalRepository.KindOuGuid,
        _ => throw new ArgumentOutOfRangeException(nameof(legacyKind), legacyKind, "Not a legacy principal kind."),
    };

    internal static IReadOnlyList<ProtectedPrincipalRowResolution> Resolve(
        IReadOnlyList<ProtectedPrincipalLegacyRow> rows, IProtectedPrincipalResolveDirectory directory, CancellationToken cancellationToken)
        => ResolveEach(rows, row => row.Kind switch
        {
            ProtectedPrincipalRepository.KindUser => ResolveUser(row, directory),
            ProtectedPrincipalRepository.KindGroup => ResolveGroup(row, directory),
            ProtectedPrincipalRepository.KindOu => ResolveOu(row, directory),
            _ => ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.Unsupported, "not a user, group or OU row"),
        }, cancellationToken);

    /// <summary>
    /// docs/ProtectedPrincipalIdentifiers-Plan.md B.6. Resolves module-admin grants stored by name - each value once - to the
    /// one group each names. Every returned row is of kind <c>group</c>, its value the grant as stored.
    /// </summary>
    /// <remarks>
    /// <para>The way section access resolved its names (docs/SectionAccessSidStorage-Plan.md, Migration step 2), with the
    /// display name added because the old module-admin picker stored either the sAMAccountName or the display name:</para>
    /// <para>- <c>DOMAIN\name</c> in the domain the prefix names, never the forest: a same-named group of another domain is
    /// not this grant. A <c>BUILTIN\</c> or <c>NT AUTHORITY\</c> prefix names a well-known group and is refused without a
    /// lookup.</para>
    /// <para>- A bare name through the global catalog, so a group of any domain is found and a name two domains share is
    /// ambiguous rather than whichever answered first.</para>
    /// <para>- One search, <see cref="GroupLookupKey.AnyGroupName"/>: <c>sAMAccountName</c>, <c>cn</c>, <c>name</c> and
    /// <c>displayName</c> together, groups only.</para>
    /// <para>Exactly one group converts, unless its SID is well-known or not a domain group's. A value already shaped like a
    /// SID but unusable (a well-known SID, an SDDL alias) is refused without a lookup.</para>
    /// </remarks>
    internal static IReadOnlyList<ProtectedPrincipalRowResolution> ResolveModuleAdminGrants(
        IReadOnlyList<string> grants, IProtectedGroupDirectory directory, CancellationToken cancellationToken)
        => ResolveEach(
            grants.Select(g => new ProtectedPrincipalLegacyRow(ProtectedPrincipalRepository.KindGroup, g)).ToList(),
            row => ResolveGrantName(row, directory),
            cancellationToken);

    private static readonly string[] WellKnownPrefixes = ["BUILTIN", "NT AUTHORITY"];

    private static ProtectedPrincipalRowResolution ResolveGrantName(ProtectedPrincipalLegacyRow row, IProtectedGroupDirectory directory)
    {
        var parsed = SectionAccessGroupIdentity.Parse(row.Value);
        string? server = null;
        switch (parsed.Kind)
        {
            case StoredGroupValueKind.Sid:
                // Already a usable SID: nothing to convert. Not reached from the pass, which reads only unusable grants.
                return ProtectedPrincipalRowResolution.Converted(row, parsed.Sid!);

            case StoredGroupValueKind.Unusable:
                return (row.Value ?? string.Empty).Trim().StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)
                    ? ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.WellKnownSid, "well-known SID refused")
                    : ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.Unsupported, $"not looked up ({parsed.RejectionReason}); remove it and pick the group from the search");

            case StoredGroupValueKind.DomainQualified:
                if (WellKnownPrefixes.Contains(parsed.NetBiosDomain!.Trim(), StringComparer.OrdinalIgnoreCase))
                    return ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.WellKnownSid, "well-known group refused");
                // Throws when the forest has no such domain - recorded as a lookup that could not run, never as a search of
                // some other domain.
                server = directory.ResolveDomainServer(parsed.NetBiosDomain!.Trim());
                break;
        }

        var groups = directory.FindGroups(GroupLookupKey.AnyGroupName, parsed.Name!.Trim(), server)
            .DistinctBy(g => g.DistinguishedName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (groups.Count == 0)
            return ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.NotFound, "not found");
        if (groups.Count > 1)
            return ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.Ambiguous,
                $"{groups.Count} groups match: " + string.Join("; ", groups.Select(g => g.DistinguishedName)));

        var sid = ProtectedPrincipalIdEntry.CanonicalSid(groups[0].Sid);
        if (sid is null)
            return ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.DirectoryUnavailable, "directory unavailable: the group's SID could not be read");

        return SectionAccessGroupIdentity.SidRejectionReason(sid) is not null
            ? ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.WellKnownSid, "well-known SID refused")
            : ProtectedPrincipalRowResolution.Converted(row, sid);
    }

    private static IReadOnlyList<ProtectedPrincipalRowResolution> ResolveEach(
        IReadOnlyList<ProtectedPrincipalLegacyRow> rows, Func<ProtectedPrincipalLegacyRow, ProtectedPrincipalRowResolution> resolve,
        CancellationToken cancellationToken)
    {
        var resolved = new List<ProtectedPrincipalRowResolution>(rows.Count);
        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                resolved.Add(resolve(row));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A read that failed proves nothing about the object: the row is left, enforced as before, and the next pass
                // asks again. Never "not found".
                resolved.Add(ProtectedPrincipalRowResolution.Left(
                    row, ProtectedPrincipalLeftReason.DirectoryUnavailable, $"directory unavailable: {ex.Message}"));
            }
        }
        return resolved;
    }

    /// <summary>The filter and server a user row is looked up with; null filter, with the reason, for a shape not looked up.</summary>
    internal static (string? Filter, string? Server, string? NetBiosDomain, string? Problem) UserLookup(string value)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length == 0)
            return (null, null, null, "the entry is blank");

        var slash = v.IndexOf('\\');
        if (slash > 0 && slash < v.Length - 1 && !v.AsSpan(0, slash).Contains('='))
        {
            // DOMAIN\name, in the domain the prefix names.
            var sam = ProtectedPrincipalService.EscapeLdapFilter(v[(slash + 1)..].Trim());
            return ($"(&{PersonFilter}(sAMAccountName={sam}))", null, v[..slash].Trim(), null);
        }

        if (v.Contains('='))
        {
            var server = GroupManagementService.ServerFromDn(v);
            return server is null
                ? (null, null, null, ProtectedGroupMembership.DnNamesNoDomain)
                : ($"(&{PersonFilter}{ProtectedGroupMembership.ExactDnFilter(v)})", server, null, null);
        }

        if (Guid.TryParse(v, out _))
            return (null, null, null, "an object ID is not looked up by the pass; re-pick the user from the search");

        var escaped = ProtectedPrincipalService.EscapeLdapFilter(v);
        return v.Contains('@')
            ? ($"(&{PersonFilter}(|(userPrincipalName={escaped})(mail={escaped})))", null, null, null)
            : ($"(&{PersonFilter}(sAMAccountName={escaped}))", null, null, null);
    }

    private static ProtectedPrincipalRowResolution ResolveUser(ProtectedPrincipalLegacyRow row, IProtectedPrincipalResolveDirectory directory)
    {
        var (filter, server, netBiosDomain, problem) = UserLookup(row.Value);
        if (filter is null)
            return ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.Unsupported, problem!);

        // Throws when the forest has no such domain - recorded as a lookup that could not run, never as a search of some
        // other domain.
        if (netBiosDomain is not null)
            server = directory.ResolveDomainServer(netBiosDomain);

        var users = directory.FindUsers(filter, server)
            .DistinctBy(u => u.DistinguishedName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (users.Count == 0)
            return ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.NotFound, "not found");
        if (users.Count > 1)
            return ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.Ambiguous, $"matches {users.Count} objects");

        var sid = ProtectedPrincipalIdEntry.CanonicalSid(users[0].Sid);
        return sid is null
            ? ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.DirectoryUnavailable, "directory unavailable: the user's SID could not be read")
            : ProtectedPrincipalRowResolution.Converted(row, sid);
    }

    private static ProtectedPrincipalRowResolution ResolveGroup(ProtectedPrincipalLegacyRow row, IProtectedPrincipalResolveDirectory directory)
    {
        var entry = ProtectedGroupEntry.Parse(row.Value);
        if (entry.Value.Length == 0)
            return ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.Unsupported, "the entry is blank");

        if (entry.Kind == ProtectedGroupEntryKind.Sid && SectionAccessGroupIdentity.SidRejectionReason(entry.Value) is not null)
            return ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.WellKnownSid, "well-known SID refused");

        var group = ProtectedGroupMembership.Resolve(entry, directory, out var problem);
        if (group is null)
        {
            return problem switch
            {
                ProtectedGroupMembership.NoSuchGroup => ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.NotFound, "not found"),
                ProtectedGroupMembership.DnNamesNoDomain => ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.Unsupported, problem),
                _ => ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.Ambiguous, problem ?? "more than one group matches"),
            };
        }

        var sid = ProtectedPrincipalIdEntry.CanonicalSid(group.Sid);
        if (sid is null)
            return ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.DirectoryUnavailable, "directory unavailable: the group's SID could not be read");

        // BUILTIN\Administrators read by its DN is as well-known as its SID typed in: the validator's rule (plan A.3).
        return SectionAccessGroupIdentity.SidRejectionReason(sid) is not null
            ? ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.WellKnownSid, "well-known SID refused")
            : ProtectedPrincipalRowResolution.Converted(row, sid);
    }

    private static ProtectedPrincipalRowResolution ResolveOu(ProtectedPrincipalLegacyRow row, IProtectedPrincipalResolveDirectory directory)
    {
        var dn = (row.Value ?? string.Empty).Trim();
        if (!dn.Contains('='))
            return ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.Unsupported, "not a distinguished name; re-pick the OU from the search");

        var server = GroupManagementService.ServerFromDn(dn);
        if (server is null)
            return ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.Unsupported, ProtectedGroupMembership.DnNamesNoDomain);

        var found = directory.ReadObjectGuids([dn], server)
            .Where(o => string.Equals(o.DistinguishedName, dn, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (found.Count == 0)
            return ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.NotFound, "not found");
        if (found.Count > 1)
            return ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.Ambiguous, $"matches {found.Count} objects");

        var guid = ProtectedPrincipalIdEntry.CanonicalGuid(found[0].ObjectGuid);
        return guid is null
            ? ProtectedPrincipalRowResolution.Left(row, ProtectedPrincipalLeftReason.DirectoryUnavailable, "directory unavailable: the OU's object GUID could not be read")
            : ProtectedPrincipalRowResolution.Converted(row, guid);
    }
}
