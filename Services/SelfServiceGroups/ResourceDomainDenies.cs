namespace ExchangeAdminWeb.Services.SelfServiceGroups;

/// <summary>
/// Decides which of a group's Deny trustees apply to the caller when the caller's token cannot
/// say, by reading the caller's memberships from the directory. aud-a4b finding 7.
/// </summary>
/// <remarks>
/// The self-service DACL gate evaluates the group's ACL against the caller's token as the web
/// server received it. That token carries the domain-local groups of the WEB SERVER's domain; the
/// group being written may live in another, whose domain-local groups are then missing - so a
/// Deny one of them carries was skipped while an individual Allow granted, and the module's
/// privileged credential performed the write.
///
/// The membership question is the one <see cref="ProtectedGroupMembership.Evaluate"/> already
/// answers for the protected-principal check: it asks a domain-local group in its own domain,
/// reads universal ancestry from the global catalog and counts the primary group. It is reused,
/// not re-derived.
///
/// What is found can only make a Deny apply. It never satisfies an Allow: the finding is the Deny
/// that was missed, and widening grants on a directory computation is not needed to close it.
/// </remarks>
internal static class ResourceDomainDenies
{
    /// <summary>
    /// Whether the caller may write the group's membership: the DACL against the caller's token,
    /// with every Deny the token could not decide settled from the directory first.
    /// </summary>
    /// <remarks>
    /// Fail closed: a trustee that cannot be resolved cleanly, a caller who cannot be found, a read
    /// that fails or an incomplete membership answer all refuse.
    /// </remarks>
    internal static bool CallerMayWriteMembers(
        IReadOnlyCollection<GroupMembershipAce.Entry> entries,
        string callerSid,
        IReadOnlyCollection<string> callerGroupSids,
        IReadOnlyCollection<string> denyOnlySids,
        string groupDn,
        IProtectedGroupDirectory directory,
        ILogger logger)
    {
        var undecided = GroupMembershipAce.DenyTrusteesTheTokenCannotDecide(entries, callerSid, callerGroupSids, denyOnlySids);
        if (undecided.Count == 0)
            return GroupMembershipAce.GrantsMemberWrite(entries, callerSid, callerGroupSids, denyOnlySids);

        var applicable = ApplicableToCaller(undecided, callerSid, groupDn, directory, logger);
        if (applicable is null)
            return false;

        return GroupMembershipAce.GrantsMemberWrite(
            entries, callerSid, callerGroupSids, denyOnlySids.Concat(applicable).ToList());
    }

    /// <summary>
    /// The subset of <paramref name="denySids"/> the caller is a member of, or null when that
    /// cannot be established.
    /// </summary>
    internal static IReadOnlyList<string>? ApplicableToCaller(
        IReadOnlyList<string> denySids, string callerSid, string groupDn, IProtectedGroupDirectory directory, ILogger logger)
    {
        if (denySids.Count == 0)
            return [];

        var groupServer = GroupManagementService.ServerFromDn(groupDn);
        if (groupServer is null)
        {
            logger.LogWarning("Self-service DACL check cannot determine the domain of {GroupDn} - refusing", groupDn);
            return null;
        }

        try
        {
            var candidates = new List<(string Sid, string Dn)>();
            foreach (var sid in denySids)
            {
                // A BUILTIN alias exists in every domain under the same SID; the one this DACL
                // means is the one in the group's own domain. An account SID is unique in the
                // forest.
                var builtin = sid.StartsWith("S-1-5-32-", StringComparison.OrdinalIgnoreCase);
                var groups = directory.FindGroups(GroupLookupKey.Sid, sid, builtin ? groupServer : null);

                // Not a group: another account (the caller's own SID is in the token, so this one
                // is somebody else's), or a principal no longer in this forest. Neither can be in
                // the caller's token for a resource here.
                if (groups.Count == 0)
                    continue;

                if (groups.Count > 1)
                {
                    logger.LogWarning("Deny trustee {Sid} resolved to {Count} groups - refusing", sid, groups.Count);
                    return null;
                }

                candidates.Add((sid, groups[0].DistinguishedName));
            }

            if (candidates.Count == 0)
                return [];

            var callerDns = directory.FindObjectDnsBySid(callerSid);
            if (callerDns.Count != 1)
            {
                logger.LogWarning("Caller {Sid} resolved to {Count} directory objects - refusing", callerSid, callerDns.Count);
                return null;
            }

            var result = ProtectedGroupMembership.Evaluate(
                candidates.Select(c => c.Dn).ToList(), callerDns[0], directory, logger);
            if (result.Incomplete)
                return null;

            var matched = new HashSet<string>(result.MatchedEntries, StringComparer.OrdinalIgnoreCase);
            return candidates.Where(c => matched.Contains(c.Dn)).Select(c => c.Sid).ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Self-service DACL check could not settle the Deny trustees of {GroupDn} - refusing", groupDn);
            return null;
        }
    }
}
