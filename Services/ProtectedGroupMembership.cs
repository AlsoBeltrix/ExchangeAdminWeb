using System.Security.Principal;

namespace ExchangeAdminWeb.Services;

/// <summary>The shape of one configured protected-group entry.</summary>
internal enum ProtectedGroupEntryKind
{
    /// <summary>A distinguished name. Read at that DN, in the domain its DC components name.</summary>
    DistinguishedName,

    /// <summary><c>DOMAIN\Name</c>. Looked up only in the domain the prefix names.</summary>
    DomainQualified,

    /// <summary>A canonical SID string. Looked up forest-wide.</summary>
    Sid,

    /// <summary>A bare name with no domain. Looked up forest-wide; more than one match refuses.</summary>
    Name,
}

/// <summary>
/// One configured protected-group entry, parsed by shape. Pure so the routing rule each shape
/// gets is testable without a directory.
/// </summary>
internal sealed record ProtectedGroupEntry(ProtectedGroupEntryKind Kind, string Value, string? Domain)
{
    /// <remarks>
    /// A backslash means two different things depending on shape. In <c>CONTOSO\Ops</c> it
    /// separates a domain; in a DN it escapes a character, and a DN always carries an <c>=</c>
    /// before its first backslash where a domain prefix never does - the discriminator
    /// <c>ADDirectorySearchService.NormalizeIdentity</c> already uses (review finding ppv-2).
    ///
    /// aud-a3b hole 3: the domain half used to be thrown away, so <c>B\Privileged</c> was
    /// resolved as <c>Privileged</c> in whatever domain the query happened to bind to. It is kept
    /// here and the evaluator routes on it.
    /// </remarks>
    internal static ProtectedGroupEntry Parse(string raw)
    {
        var v = (raw ?? string.Empty).Trim();
        var slash = v.IndexOf('\\');

        if (slash > 0 && slash < v.Length - 1 && !v.AsSpan(0, slash).Contains('='))
            return new(ProtectedGroupEntryKind.DomainQualified, v[(slash + 1)..].Trim(), v[..slash].Trim());

        if (v.Contains('='))
            return new(ProtectedGroupEntryKind.DistinguishedName, v, null);

        if (IsCanonicalSid(v))
            return new(ProtectedGroupEntryKind.Sid, v, null);

        return new(ProtectedGroupEntryKind.Name, v, null);
    }

    /// <summary>
    /// True only for a SID written in its own canonical form. The framework parser also accepts
    /// SDDL aliases (<c>"BA"</c> parses as BUILTIN\Administrators), so parse-success alone would
    /// let a two-letter group name be read as a SID; the exact round-trip refuses that.
    /// </summary>
    private static bool IsCanonicalSid(string value)
    {
        if (!value.StartsWith("S-", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            return string.Equals(new SecurityIdentifier(value).Value, value, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>The scope of a directory group, as Active Directory reports it.</summary>
internal enum DirectoryGroupScope
{
    DomainLocal,
    Global,
    Universal,
}

/// <summary>One group as the directory reported it. The SID is the identity compared.</summary>
internal sealed record DirectoryGroup(string DistinguishedName, string Sid, DirectoryGroupScope Scope);

/// <summary>
/// The target object, read in its own domain. <see cref="ObjectClass"/> decides whether the
/// object carries a primary group; <see cref="Sid"/> is null for objects that are not security
/// principals (a contact).
/// </summary>
internal sealed record DirectoryTarget(string? Sid, string? ObjectClass, int? PrimaryGroupId);

/// <summary>Which attribute a group lookup keys on.</summary>
internal enum GroupLookupKey
{
    DistinguishedName,
    Sid,
    SamAccountName,
    CommonName,
}

/// <summary>
/// The directory reads the protected-group membership evaluation needs, separated from how they
/// are performed so the evaluation - which decides what an answer MEANS - is testable without a
/// domain controller.
/// </summary>
/// <remarks>
/// Every member either returns the directory's answer or throws. None of them may report "no
/// rows" for a read that did not complete: the evaluator treats an empty answer as evidence.
///
/// A <c>server</c> is always the DNS name of the domain whose data is being asked for. A null
/// server on <see cref="FindGroups"/> means the whole forest, through a global catalog.
/// </remarks>
internal interface IProtectedGroupDirectory
{
    /// <summary>Base read of the target in its own domain.</summary>
    DirectoryTarget ReadTarget(string targetDn, string server);

    /// <summary>
    /// The DNS name of the forest domain a <c>DOMAIN\</c> prefix names. Throws when the forest
    /// has no such domain, or more than one.
    /// </summary>
    string ResolveDomainServer(string domain);

    /// <summary>Groups whose <paramref name="key"/> equals <paramref name="value"/>.</summary>
    IReadOnlyList<DirectoryGroup> FindGroups(GroupLookupKey key, string value, string? server);

    /// <summary>
    /// Every group of <paramref name="server"/>'s domain whose member chain, as that domain
    /// holds it, reaches any of <paramref name="memberDns"/>.
    /// </summary>
    IReadOnlyList<DirectoryGroup> GroupsContainingAny(IReadOnlyList<string> memberDns, string server);

    /// <summary>
    /// Whether the domain-local group at <paramref name="groupDn"/>, through the nesting its own
    /// domain holds, has any of <paramref name="memberDns"/> as a member.
    /// </summary>
    bool DomainLocalGroupContainsAny(string groupDn, IReadOnlyList<string> memberDns, string server);

    /// <summary>
    /// The pre-existing check: whether any of <paramref name="candidateDns"/> has
    /// <paramref name="groupDn"/> in its memberOf chain, as <paramref name="server"/> holds it.
    /// </summary>
    bool AnyIsInChainOf(IReadOnlyList<string> candidateDns, string groupDn, string server);
}

/// <summary>
/// Decides whether a target is a member of any configured protected group. Pure over
/// <see cref="IProtectedGroupDirectory"/>.
/// </summary>
/// <remarks>
/// aud-a3b. The previous check was one in-chain query per protected group, sent to the target's
/// domain, and it read "no rows" as "not a member". Three ways to get no rows while the answer is
/// yes:
///
/// 1. A domain-local group in ANOTHER domain. Its member links live only in its own domain, so
///    the target's domain cannot see them. Domain-local groups can only nest inside domain-local
///    groups of the same domain, so that domain holds every link of the outer chain - it is asked
///    directly, with the target and everything the target's domain knows it belongs to.
/// 2. The primary group. That membership is <c>primaryGroupID</c>, never a <c>member</c> link,
///    so no link query can see it. It is derived from the target's own SID and RID and added to
///    the set every query starts from.
/// 3. The resolver threw away the domain half of <c>DOMAIN\Name</c> and passed a DN through
///    without reading it. Entries are now bound to exactly one group by SID, in the domain they
///    name.
///
/// Fail closed throughout: a read that fails, a lookup that matches nothing or more than one
/// object, a domain that cannot be determined - each marks the answer incomplete, and the caller
/// refuses an incomplete answer that found no match. A match found elsewhere still protects.
/// </remarks>
internal static class ProtectedGroupMembership
{
    /// <summary>The evaluation's answer.</summary>
    /// <param name="Matches">One <c>Group:&lt;entry&gt;</c> rule per protected group matched.</param>
    /// <param name="Incomplete">True when some part of the question could not be answered.</param>
    internal sealed record Result(IReadOnlyList<string> Matches, bool Incomplete);

    private sealed record Seed(string DistinguishedName, string? Sid);

    internal static Result Evaluate(
        IReadOnlyList<string> protectedGroups,
        string targetDn,
        IProtectedGroupDirectory directory,
        ILogger logger)
    {
        var matches = new List<string>();
        var entries = protectedGroups.Where(g => !string.IsNullOrWhiteSpace(g)).ToList();
        if (entries.Count == 0)
            return new(matches, false);

        // The owning domain comes from the target's own DN (aud-a3). Without it nothing below
        // can be routed, and an unanswerable question is not a negative answer.
        var targetServer = GroupManagementService.ServerFromDn(targetDn);
        if (targetServer is null)
        {
            logger.LogWarning(
                "Protected-group check cannot determine the owning domain for target DN {TargetDn} - failing closed",
                targetDn);
            return new(matches, true);
        }

        List<Seed>? seeds;
        IReadOnlyList<DirectoryGroup> ancestors;
        try
        {
            seeds = ReadSeeds(targetDn, targetServer, directory, logger);
            if (seeds is null)
                return new(matches, true);

            ancestors = directory.GroupsContainingAny(
                seeds.Select(s => s.DistinguishedName).ToList(), targetServer);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Protected-group check could not read target {TargetDn} or its groups in {Server} - failing closed",
                targetDn, targetServer);
            return new(matches, true);
        }

        var memberSids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var sid in seeds.Select(s => s.Sid).Concat(ancestors.Select(a => a.Sid)))
        {
            if (!string.IsNullOrWhiteSpace(sid))
                memberSids.Add(sid);
        }

        var memberDns = seeds.Select(s => s.DistinguishedName)
            .Concat(ancestors.Select(a => a.DistinguishedName))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var seedDns = seeds.Select(s => s.DistinguishedName).ToList();

        var incomplete = false;
        foreach (var raw in entries)
        {
            try
            {
                var group = Resolve(ProtectedGroupEntry.Parse(raw), directory, out var problem);
                if (group is null)
                {
                    logger.LogWarning(
                        "Protected group {ProtectedGroup} could not be resolved to exactly one directory group ({Problem}) - check incomplete",
                        raw, problem);
                    incomplete = true;
                    continue;
                }

                // The target itself, its primary group, or a group its own domain links it into.
                if (memberSids.Contains(group.Sid))
                {
                    matches.Add($"Group:{raw}");
                    continue;
                }

                var groupServer = GroupManagementService.ServerFromDn(group.DistinguishedName);
                if (groupServer is null)
                {
                    logger.LogWarning(
                        "Protected group {ProtectedGroup} resolved to DN {GroupDn}, which names no domain - check incomplete",
                        raw, group.DistinguishedName);
                    incomplete = true;
                    continue;
                }

                if (group.Scope == DirectoryGroupScope.DomainLocal)
                {
                    // Hole 1. Asked in the GROUP's domain, never the target's: that is the only
                    // place its member links exist.
                    if (directory.DomainLocalGroupContainsAny(group.DistinguishedName, memberDns, groupServer))
                        matches.Add($"Group:{raw}");
                    continue;
                }

                // A global group of another domain cannot contain the target, and a group of the
                // target's own domain that is not among the ancestors is not linked to it. What
                // is left is a universal group of another domain, which keeps the query the
                // previous check ran (now with the primary group added) so no coverage it had is
                // lost. That query's own blind spot is aud-a3c.
                if (group.Scope == DirectoryGroupScope.Universal
                    && !string.Equals(groupServer, targetServer, StringComparison.OrdinalIgnoreCase)
                    && directory.AnyIsInChainOf(seedDns, group.DistinguishedName, targetServer))
                {
                    matches.Add($"Group:{raw}");
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to evaluate protected group {ProtectedGroup} during the membership check - check incomplete",
                    raw);
                incomplete = true;
            }
        }

        return new(matches, incomplete);
    }

    /// <summary>
    /// The objects every membership query starts from: the target, and for an account its
    /// primary group. Null when the target could not be read completely.
    /// </summary>
    private static List<Seed>? ReadSeeds(
        string targetDn, string targetServer, IProtectedGroupDirectory directory, ILogger logger)
    {
        var target = directory.ReadTarget(targetDn, targetServer);
        var seeds = new List<Seed> { new(targetDn, target.Sid) };

        if (!RequiresPrimaryGroup(target))
            return seeds;

        // Hole 2. An account always has a primary group; one that cannot be read is an
        // incomplete read, not an account without one.
        var primarySid = target.PrimaryGroupId is int rid && target.Sid is not null
            ? PrimaryGroupSid(target.Sid, rid)
            : null;
        if (primarySid is null)
        {
            logger.LogWarning(
                "Protected-group check could not read the primary group of {TargetDn} - failing closed",
                targetDn);
            return null;
        }

        var primary = directory.FindGroups(GroupLookupKey.Sid, primarySid, targetServer);
        if (primary.Count != 1)
        {
            logger.LogWarning(
                "Primary group {PrimaryGroupSid} of {TargetDn} resolved to {Count} groups - failing closed",
                primarySid, targetDn, primary.Count);
            return null;
        }

        seeds.Add(new(primary[0].DistinguishedName, primary[0].Sid));
        return seeds;
    }

    /// <summary>
    /// Binds one entry to exactly one group, in the directory its shape names. Null, with the
    /// reason in <paramref name="problem"/>, for no match or several.
    /// </summary>
    private static DirectoryGroup? Resolve(
        ProtectedGroupEntry entry, IProtectedGroupDirectory directory, out string? problem)
    {
        switch (entry.Kind)
        {
            case ProtectedGroupEntryKind.DistinguishedName:
                var dnServer = GroupManagementService.ServerFromDn(entry.Value);
                if (dnServer is null)
                {
                    problem = "the DN names no domain";
                    return null;
                }

                // Read, never passed through: a DN that no longer exists protects nobody, and the
                // check must say it cannot answer rather than answer for a group that is gone.
                return One(directory.FindGroups(GroupLookupKey.DistinguishedName, entry.Value, dnServer), out problem);

            case ProtectedGroupEntryKind.DomainQualified:
                // Throws when the forest has no such domain - which the caller records as an
                // incomplete check, never as a lookup in some other domain.
                var namedServer = directory.ResolveDomainServer(entry.Domain!);
                return ByName(entry.Value, namedServer, directory, out problem);

            case ProtectedGroupEntryKind.Sid:
                return One(directory.FindGroups(GroupLookupKey.Sid, entry.Value, null), out problem);

            default:
                // No domain named, so none is assumed: the forest is searched and a name two
                // domains share refuses rather than resolving to whichever answered first.
                return ByName(entry.Value, null, directory, out problem);
        }
    }

    /// <summary>
    /// sAMAccountName first, then the common name. An exact sAMAccountName match wins outright
    /// within one domain because it is unique there; the common name is only consulted when no
    /// group carries the name as its sAMAccountName.
    /// </summary>
    private static DirectoryGroup? ByName(
        string name, string? server, IProtectedGroupDirectory directory, out string? problem)
    {
        var bySam = directory.FindGroups(GroupLookupKey.SamAccountName, name, server);
        if (bySam.Count > 0)
            return One(bySam, out problem);

        return One(directory.FindGroups(GroupLookupKey.CommonName, name, server), out problem);
    }

    private static DirectoryGroup? One(IReadOnlyList<DirectoryGroup> groups, out string? problem)
    {
        if (groups.Count == 1)
        {
            problem = null;
            return groups[0];
        }

        problem = groups.Count == 0 ? "no such group" : $"{groups.Count} groups match";
        return null;
    }

    /// <summary>
    /// Whether this object carries a primary group: any security principal that is not a group.
    /// An object whose class could not be read is assumed to, so an unreadable class fails
    /// closed rather than skipping the primary group.
    /// </summary>
    internal static bool RequiresPrimaryGroup(DirectoryTarget target)
        => target.Sid is not null
           && !string.Equals(target.ObjectClass, "group", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The primary group's SID: the account's own domain SID with the primary group's RID.
    /// Null when the account SID is not a domain account SID.
    /// </summary>
    internal static string? PrimaryGroupSid(string accountSid, int primaryGroupRid)
    {
        if (primaryGroupRid <= 0)
            return null;

        try
        {
            var domain = new SecurityIdentifier(accountSid).AccountDomainSid;
            return domain is null ? null : $"{domain.Value}-{primaryGroupRid}";
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Maps the AD module's <c>GroupScope</c> rendering; null when unrecognised.</summary>
    internal static DirectoryGroupScope? ParseScope(string? groupScope) => groupScope?.Trim() switch
    {
        "DomainLocal" => DirectoryGroupScope.DomainLocal,
        "Global" => DirectoryGroupScope.Global,
        "Universal" => DirectoryGroupScope.Universal,
        _ => null,
    };

    // ----- LDAP filters, pure so their shapes are testable -----

    internal const string InChainRule = "1.2.840.113556.1.4.1941";

    /// <summary>The most DN terms one filter carries; longer lists are split across queries.</summary>
    internal const int MaxTermsPerFilter = 50;

    internal static string ExactDnFilter(string dn)
        => $"(distinguishedName={ProtectedPrincipalService.EscapeLdapFilter(dn)})";

    /// <summary>Groups whose member chain reaches any of the DNs.</summary>
    internal static string ContainsAnyFilter(IEnumerable<string> memberDns)
        => "(|" + string.Concat(memberDns.Select(dn =>
               $"(member:{InChainRule}:={ProtectedPrincipalService.EscapeLdapFilter(dn)})")) + ")";

    /// <summary>The group at <paramref name="groupDn"/>, only if its chain reaches any of the DNs.</summary>
    internal static string GroupContainsAnyFilter(string groupDn, IEnumerable<string> memberDns)
        => $"(&{ExactDnFilter(groupDn)}{ContainsAnyFilter(memberDns)})";

    /// <summary>Any of the candidates whose memberOf chain reaches the group.</summary>
    internal static string AnyInChainOfFilter(IEnumerable<string> candidateDns, string groupDn)
        => "(&(|" + string.Concat(candidateDns.Select(ExactDnFilter)) + ")"
           + $"(memberOf:{InChainRule}:={ProtectedPrincipalService.EscapeLdapFilter(groupDn)}))";

    /// <summary>The attribute filter for a group lookup.</summary>
    internal static string GroupLookupFilter(GroupLookupKey key, string value)
    {
        var escaped = ProtectedPrincipalService.EscapeLdapFilter(value);
        return key switch
        {
            GroupLookupKey.DistinguishedName => $"(distinguishedName={escaped})",
            GroupLookupKey.Sid => $"(objectSid={escaped})",
            GroupLookupKey.SamAccountName => $"(sAMAccountName={escaped})",
            _ => $"(|(cn={escaped})(name={escaped}))",
        };
    }

    /// <summary>
    /// The forest's crossRef for a domain, by NetBIOS or DNS name. <c>nETBIOSName=*</c> keeps it to
    /// DOMAIN crossRefs: the configuration and schema crossRefs carry the forest root's dnsRoot
    /// too, and would otherwise make the root domain's DNS name look ambiguous.
    /// </summary>
    internal static string DomainCrossRefFilter(string domain)
    {
        var escaped = ProtectedPrincipalService.EscapeLdapFilter(domain);
        return $"(&(objectClass=crossRef)(nETBIOSName=*)(|(nETBIOSName={escaped})(dnsRoot={escaped})))";
    }

    /// <summary>Splits a DN list so no single filter grows without bound.</summary>
    internal static IEnumerable<IReadOnlyList<string>> Batches(IReadOnlyList<string> dns)
    {
        for (var i = 0; i < dns.Count; i += MaxTermsPerFilter)
            yield return dns.Skip(i).Take(MaxTermsPerFilter).ToList();
    }
}
