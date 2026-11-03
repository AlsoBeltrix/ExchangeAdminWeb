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
/// <param name="Security">
/// kd-88. A security group, which a token carries. False for a distribution group - and for a group whose category could
/// not be read, so an unreadable category takes the path that reads the membership links rather than the one that trusts
/// the token.
/// </param>
internal sealed record DirectoryGroup(string DistinguishedName, string Sid, DirectoryGroupScope Scope, bool Security = true);

/// <summary>
/// The target object, read in its own domain. <see cref="ObjectClass"/> decides whether the
/// object carries a primary group; <see cref="Sid"/> is null for objects that are not security
/// principals (a contact).
/// </summary>
internal sealed record DirectoryTarget(string? Sid, string? ObjectClass, int? PrimaryGroupId);

/// <summary>
/// kd-88. One object's <c>memberOf</c> as the directory that answered holds it: the groups whose member links that directory
/// holds and that name the object directly. <see cref="Sid"/> is null for an object without one (a contact).
/// </summary>
internal sealed record DirectoryMemberOf(string DistinguishedName, string? Sid, IReadOnlyList<string> MemberOf);

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
/// server on <see cref="FindGroups"/> and <see cref="ReadMemberOf"/> means the whole forest, through a global catalog.
///
/// kd-88. Every read is anchored: a base read at a known DN, an equality search on an indexed key, or the one in-chain
/// question asked of a single named group (<see cref="DomainLocalGroupContainsAny"/>). The two members that searched a whole
/// domain, and the whole forest, for every group whose member chain reached the target - <c>GroupsContainingAny</c> and
/// <c>UniversalGroupsContainingAny</c> - are gone: on the dev forest the first ran into the domain controller's two-minute
/// timeout and every grant was refused.
/// </remarks>
internal interface IProtectedGroupDirectory
{
    /// <summary>Base read of the target in its own domain.</summary>
    DirectoryTarget ReadTarget(string targetDn, string server);

    /// <summary>
    /// kd-88. The account's <c>tokenGroups</c>: the SIDs of every security group a domain controller of its own domain
    /// computes for it - nested groups, universal groups across the forest through a global catalog, its own domain's
    /// domain-local groups and its primary group. A base-scope read at the account's DN, bound by its SID (kd-87's shape:
    /// the attribute is constructed only in a base-scope search). Throws when the read fails or finds no such account.
    /// </summary>
    IReadOnlyList<string> ReadTokenGroups(string accountDn, string accountSid, string server);

    /// <summary>
    /// kd-88. The <c>memberOf</c> of each object at <paramref name="dns"/>, as <paramref name="server"/> holds it: a domain
    /// controller of that domain for objects of that domain, or - null - a global catalog for objects of any domain, where it
    /// holds universal groups' links. A read of named objects by DN, never a search for whatever contains them. An object it
    /// does not hold is left out of the answer.
    /// </summary>
    IReadOnlyList<DirectoryMemberOf> ReadMemberOf(IReadOnlyList<string> dns, string? server);

    /// <summary>
    /// The DNS name of the forest domain a <c>DOMAIN\</c> prefix names. Throws when the forest
    /// has no such domain, or more than one.
    /// </summary>
    string ResolveDomainServer(string domain);

    /// <summary>Groups whose <paramref name="key"/> equals <paramref name="value"/>.</summary>
    IReadOnlyList<DirectoryGroup> FindGroups(GroupLookupKey key, string value, string? server);

    /// <summary>
    /// The DNs of every object in the forest whose objectSid is <paramref name="sid"/>, of any
    /// class, through a global catalog.
    /// </summary>
    IReadOnlyList<string> FindObjectDnsBySid(string sid);

    /// <summary>
    /// Whether the domain-local group at <paramref name="groupDn"/>, through the nesting its own
    /// domain holds, has any of <paramref name="memberDns"/> as a member. Anchored on that one group.
    /// </summary>
    bool DomainLocalGroupContainsAny(string groupDn, IReadOnlyList<string> memberDns, string server);
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
///    directly, with the target and every group already known to contain it.
/// 2. The primary group. That membership is <c>primaryGroupID</c>, never a <c>member</c> link,
///    so no link query can see it. A token carries it, and the token is checked to carry it; where
///    links are read, it is derived from the target's own SID and RID and the read starts from it too.
/// 3. The resolver threw away the domain half of <c>DOMAIN\Name</c> and passed a DN through
///    without reading it. Entries are now bound to exactly one group by SID, in the domain they
///    name.
///
/// And one the record did not list, aud-a3c: a universal group's member links are on its own
/// domain's DCs and the global catalogs, so the target's domain sees them only when the DC that
/// answered happens to be a GC. Universal membership is read where it is complete.
///
/// kd-88. The 2026-10-21 version found the target's groups by searching for EVERY group whose member chain reached the
/// target - across its whole domain, then across the forest at a global catalog. On the dev forest the first ran into the
/// domain controller's two-minute timeout and every grant was refused. The groups an account is in are now the directory's
/// own answer: its <c>tokenGroups</c>, one base-scope read in its own domain, which carries its primary group (hole 2,
/// checked), universal groups across the forest (aud-a3c) and its own domain's domain-local groups. A token does not carry
/// what is not security - a distribution group - nor a domain-local group of another domain (hole 1), nor anything at all
/// for a target without one: a group or a contact. Those, and only those, are read from the membership links: the target's
/// ancestry, one read of the named objects per level, in its own domain and at the global catalog, then the one anchored
/// question to a domain-local group's own domain. Nothing searches a domain or the forest for whatever contains the target.
///
/// Fail closed throughout: a read that fails, a lookup that matches nothing or more than one object, a domain that cannot be
/// determined, a token without the primary group - each marks the answer incomplete, and the caller refuses an incomplete
/// answer that found no match. A match found elsewhere still protects.
/// </remarks>
internal static class ProtectedGroupMembership
{
    /// <summary>The evaluation's answer.</summary>
    /// <param name="Matches">One <c>Group:&lt;entry&gt;</c> rule per protected group matched.</param>
    /// <param name="Incomplete">True when some part of the question could not be answered.</param>
    internal sealed record Result(IReadOnlyList<string> Matches, bool Incomplete)
    {
        /// <summary>The entries matched, exactly as they were passed in.</summary>
        public IReadOnlyList<string> MatchedEntries => Matches.Select(m => m["Group:".Length..]).ToList();
    }

    /// <summary>
    /// The target as the evaluation knows it before reading any link: its SID, and for an account its token and its primary
    /// group's SID. <see cref="HasToken"/> is false for a target without one (a group or a contact).
    /// </summary>
    private sealed record TargetMembership(string TargetDn, HashSet<string> Sids, bool HasToken, string? PrimaryGroupSid);

    /// <summary>Every DN the target's membership links reach - the target and the start of the read included - and their SIDs.</summary>
    private sealed record Ancestry(IReadOnlyList<string> Dns, HashSet<string> Sids);

    /// <summary>Levels of nesting the ancestry read follows before it calls the answer incomplete.</summary>
    internal const int MaxNestingLevels = 64;

    /// <summary>Groups the ancestry read collects before it calls the answer incomplete.</summary>
    internal const int MaxAncestors = 10_000;

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

        TargetMembership? membership;
        try
        {
            membership = ReadMembership(targetDn, targetServer, directory, logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Protected-group check could not read target {TargetDn} or its groups in {Server} - failing closed",
                targetDn, targetServer);
            return new(matches, true);
        }

        if (membership is null)
            return new(matches, true);

        var incomplete = false;
        var unanswered = new List<(string Raw, DirectoryGroup Group, string Server)>();
        foreach (var raw in entries)
        {
            try
            {
                // docs/ProtectedPrincipalIdentifiers-Plan.md C.2. A SID entry - a group_sid row, or a legacy row stored as a
                // SID - that the target is, or that its token carries, is a match without reading the group: the SID is the
                // identity compared below, and the token is the directory's own answer. Only a SID the token does not carry
                // is resolved, for the paths that read membership links.
                var entry = ProtectedGroupEntry.Parse(raw);
                if (entry.Kind == ProtectedGroupEntryKind.Sid && membership.Sids.Contains(entry.Value))
                {
                    matches.Add($"Group:{raw}");
                    continue;
                }

                var group = Resolve(entry, directory, out var problem);
                if (group is null)
                {
                    logger.LogWarning(
                        "Protected group {ProtectedGroup} could not be resolved to exactly one directory group ({Problem}) - check incomplete",
                        raw, problem);
                    incomplete = true;
                    continue;
                }

                // The target itself, or a group its token carries.
                if (membership.Sids.Contains(group.Sid))
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

                // A security group the token is complete for, and does not carry: not a member. Every other group - one a
                // token cannot carry - is asked of the membership links below.
                if (TokenAnswers(membership, group, groupServer, targetServer))
                    continue;

                unanswered.Add((raw, group, groupServer));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to evaluate protected group {ProtectedGroup} during the membership check - check incomplete",
                    raw);
                incomplete = true;
            }
        }

        if (unanswered.Count == 0)
            return new(matches, incomplete);

        Ancestry ancestry;
        try
        {
            ancestry = ReadAncestry(membership, targetServer, directory);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Protected-group check could not read the groups {TargetDn} is in from their member links - check incomplete",
                targetDn);
            return new(matches, true);
        }

        foreach (var (raw, group, groupServer) in unanswered)
        {
            try
            {
                // A group the target's own domain or the global catalog links it into.
                if (ancestry.Sids.Contains(group.Sid))
                {
                    matches.Add($"Group:{raw}");
                    continue;
                }

                // Hole 1. A domain-local group is asked in the GROUP's domain, never the target's: that is the only place its
                // member links exist. Anchored on the group; its inner end is the target or a group the read above found.
                if (group.Scope == DirectoryGroupScope.DomainLocal
                    && directory.DomainLocalGroupContainsAny(group.DistinguishedName, ancestry.Dns, groupServer))
                {
                    matches.Add($"Group:{raw}");
                }

                // Anything else not among the ancestors does not contain the target: every universal ancestor came from the
                // global catalog (aud-a3c), every global one from the target's own domain - the only domain whose global
                // groups can hold it.
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
    /// Whether the target's token settles <paramref name="group"/>: a security group, and not a domain-local group of another
    /// domain. A token computed in the target's own domain carries every such group the target is in - its own domain's
    /// global and domain-local groups, every universal group of the forest, its primary group - so one it does not carry does
    /// not contain the target. A global group of another domain cannot contain it at all.
    /// </summary>
    private static bool TokenAnswers(TargetMembership membership, DirectoryGroup group, string groupServer, string targetServer)
        => membership.HasToken
           && group.Security
           && !(group.Scope == DirectoryGroupScope.DomainLocal && !SameDomain(groupServer, targetServer));

    /// <summary>
    /// The target, and for an account its token. Null when the target could not be read completely: an account whose primary
    /// group cannot be derived, or whose token does not carry it - a token computed without it is not the directory's answer.
    /// </summary>
    private static TargetMembership? ReadMembership(
        string targetDn, string targetServer, IProtectedGroupDirectory directory, ILogger logger)
    {
        var target = directory.ReadTarget(targetDn, targetServer);
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(target.Sid))
            sids.Add(target.Sid);

        if (!RequiresPrimaryGroup(target))
            return new(targetDn, sids, HasToken: false, PrimaryGroupSid: null);

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

        var token = directory.ReadTokenGroups(targetDn, target.Sid!, targetServer);
        if (!token.Contains(primarySid, StringComparer.OrdinalIgnoreCase))
        {
            // Every account's token carries its primary group. One that does not was not computed - the attribute withheld
            // from the reader, or a domain controller that could not finish it - and an empty answer is not "in nothing".
            logger.LogWarning(
                "The directory's token-groups answer for {TargetDn} does not carry its primary group {PrimaryGroupSid} - failing closed",
                targetDn, primarySid);
            return null;
        }

        sids.UnionWith(token);
        return new(targetDn, sids, HasToken: true, primarySid);
    }

    /// <summary>
    /// The groups the target's membership links reach, read level by level from the places that hold the links: a domain
    /// controller of the target's own domain for that domain's objects, and a global catalog for every object, which holds
    /// universal groups' links. Each level is one read of the objects found so far, by name - never a search of a domain for
    /// whatever contains them. Throws when a read fails, when an object asked for is not in the answer, or when the nesting
    /// runs past <see cref="MaxNestingLevels"/> or <see cref="MaxAncestors"/>.
    /// </summary>
    private static Ancestry ReadAncestry(TargetMembership membership, string targetServer, IProtectedGroupDirectory directory)
    {
        var start = new List<string> { membership.TargetDn };
        if (membership.PrimaryGroupSid is { } primarySid)
        {
            // Hole 2 for the links: no member link holds an account in its primary group, so the read starts from it too.
            var primary = directory.FindGroups(GroupLookupKey.Sid, primarySid, targetServer);
            if (primary.Count != 1)
                throw new InvalidOperationException($"Primary group {primarySid} resolved to {primary.Count} groups; expected exactly one.");
            start.Add(primary[0].DistinguishedName);
        }

        var known = new HashSet<string>(start, StringComparer.OrdinalIgnoreCase);
        var dns = known.ToList();
        var sids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var frontier = dns.ToList();

        for (var level = 0; frontier.Count > 0; level++)
        {
            if (level >= MaxNestingLevels || known.Count > MaxAncestors)
                throw new InvalidOperationException(
                    $"The groups holding the target nest more than {MaxNestingLevels} levels or {MaxAncestors} groups deep.");

            var next = new List<string>();
            var ownDomain = frontier
                .Where(dn => SameDomain(GroupManagementService.ServerFromDn(dn), targetServer))
                .ToList();

            foreach (var (asked, server) in new[] { (ownDomain, (string?)targetServer), (frontier, (string?)null) })
            {
                if (asked.Count == 0)
                    continue;

                var answered = new Dictionary<string, DirectoryMemberOf>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in directory.ReadMemberOf(asked, server))
                    answered[row.DistinguishedName] = row;

                foreach (var dn in asked)
                {
                    // Every object asked for is known to exist - the target, or a group a link named. One missing from the
                    // answer was not read, and its links are not "none".
                    if (!answered.TryGetValue(dn, out var row))
                        throw new InvalidOperationException(
                            $"{server ?? "The global catalog"} did not return {dn}, which the read asked for.");

                    if (!string.IsNullOrWhiteSpace(row.Sid))
                        sids.Add(row.Sid);

                    foreach (var parent in row.MemberOf)
                    {
                        if (!string.IsNullOrWhiteSpace(parent) && known.Add(parent))
                        {
                            dns.Add(parent);
                            next.Add(parent);
                        }
                    }
                }
            }

            frontier = next;
        }

        return new(dns, sids);
    }

    private static bool SameDomain(string? a, string? b)
        => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The problem <see cref="Resolve"/> reports for an entry no group matches.</summary>
    internal const string NoSuchGroup = "no such group";

    /// <summary>The problem <see cref="Resolve"/> reports for a DN with no DC components.</summary>
    internal const string DnNamesNoDomain = "the DN names no domain";

    /// <summary>
    /// Binds one entry to exactly one group, in the directory its shape names. Null, with the
    /// reason in <paramref name="problem"/>, for no match or several. Also the identifier migration's group lookup
    /// (docs/ProtectedPrincipalIdentifiers-Plan.md B.4), so a legacy row converts to the group the check binds it to.
    /// </summary>
    internal static DirectoryGroup? Resolve(
        ProtectedGroupEntry entry, IProtectedGroupDirectory directory, out string? problem)
    {
        switch (entry.Kind)
        {
            case ProtectedGroupEntryKind.DistinguishedName:
                var dnServer = GroupManagementService.ServerFromDn(entry.Value);
                if (dnServer is null)
                {
                    problem = DnNamesNoDomain;
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

        problem = groups.Count == 0 ? NoSuchGroup : $"{groups.Count} groups match";
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

    /// <summary>
    /// kd-88. Whether the AD module's <c>GroupCategory</c> rendering names a security group. Anything else - Distribution, or
    /// a category that could not be read - is not, and takes the path that reads the membership links.
    /// </summary>
    internal static bool IsSecurityCategory(string? groupCategory)
        => string.Equals(groupCategory?.Trim(), "Security", StringComparison.Ordinal);

    // ----- LDAP filters, pure so their shapes are testable -----

    internal const string InChainRule = "1.2.840.113556.1.4.1941";

    /// <summary>The most DN terms one filter carries; longer lists are split across queries.</summary>
    internal const int MaxTermsPerFilter = 50;

    internal static string ExactDnFilter(string dn)
        => $"(distinguishedName={ProtectedPrincipalService.EscapeLdapFilter(dn)})";

    /// <summary>kd-88. The named objects, by DN: what the ancestry read asks for at each level.</summary>
    internal static string AnyDnFilter(IEnumerable<string> dns)
        => "(|" + string.Concat(dns.Select(ExactDnFilter)) + ")";

    /// <summary>
    /// The group at <paramref name="groupDn"/>, only if its chain reaches any of the DNs. Anchored on that one group: the only
    /// in-chain filter this check sends (kd-88). The domain-wide form - every group whose chain reaches a DN - is gone.
    /// </summary>
    internal static string GroupContainsAnyFilter(string groupDn, IEnumerable<string> memberDns)
        => $"(&{ExactDnFilter(groupDn)}(|" + string.Concat(memberDns.Select(dn =>
               $"(member:{InChainRule}:={ProtectedPrincipalService.EscapeLdapFilter(dn)})")) + "))";

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
