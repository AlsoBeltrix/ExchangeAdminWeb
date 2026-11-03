namespace ExchangeAdminWeb.Services;

/// <summary>One object read by DN, with the objectGUID the directory returned for it (null when unreadable).</summary>
internal sealed record DirectoryObjectGuid(string DistinguishedName, string? ObjectGuid);

/// <summary>
/// The one directory read the <c>ou_guid</c> comparison makes, separated from how it is performed so the comparison is
/// testable without a domain controller (the <see cref="IProtectedGroupDirectory"/> split).
/// </summary>
internal interface IProtectedOuDirectory
{
    /// <summary>
    /// The objects at <paramref name="dns"/>, read by DN on <paramref name="server"/> - a domain controller of the domain
    /// that holds them - each with its objectGUID. An object it does not hold is left out of the answer. Throws when the
    /// read fails; never reports "no rows" for a read that did not complete.
    /// </summary>
    IReadOnlyList<DirectoryObjectGuid> ReadObjectGuids(IReadOnlyList<string> dns, string server);
}

/// <summary>
/// Decides whether a target sits beneath a protected OU stored by objectGUID (<c>ou_guid</c>,
/// docs/ProtectedPrincipalIdentifiers-Plan.md C.2). Pure over <see cref="IProtectedOuDirectory"/>.
/// </summary>
/// <remarks>
/// The target's DN names every container above it. They are read by DN in the target's own domain - one read however many
/// OUs are protected - and their objectGUIDs compared with the stored ones, so an OU that was renamed or moved still
/// matches, where the legacy row's DN suffix compare stops matching the moment the OU's name changes. The target itself is
/// among the objects read, as the legacy rule matches a target whose DN is the OU's.
///
/// Fail closed: a domain that cannot be determined, a read that fails, an answer that leaves out a container or carries
/// no readable GUID - each marks the answer incomplete, and the caller refuses an incomplete answer that found no match.
/// </remarks>
internal static class ProtectedOuMembership
{
    internal static (IReadOnlyList<string> Matches, bool Incomplete) Evaluate(
        IReadOnlyList<ProtectedPrincipalIdEntry> protectedOus,
        string targetDn,
        IProtectedOuDirectory directory,
        ILogger logger)
    {
        var matches = new List<string>();
        if (protectedOus.Count == 0)
            return (matches, false);

        var server = GroupManagementService.ServerFromDn(targetDn);
        if (server is null)
        {
            logger.LogWarning(
                "Protected-OU check cannot determine the owning domain for target DN {TargetDn} - failing closed", targetDn);
            return (matches, true);
        }

        var containers = Containers(targetDn);
        if (containers.Count == 0)
            return (matches, false);

        IReadOnlyList<DirectoryObjectGuid> rows;
        try
        {
            rows = directory.ReadObjectGuids(containers, server);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Protected-OU check could not read the containers of {TargetDn} in {Server} - failing closed", targetDn, server);
            return (matches, true);
        }

        var incomplete = false;
        var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var guid = ProtectedPrincipalIdEntry.CanonicalGuid(row.ObjectGuid);
            if (guid is null)
                incomplete = true;
            else
                guids.Add(guid);
        }

        // Counted rather than matched by DN: the directory may write a DN with other casing or escaping than the target's
        // own, and a container it left out is an answer that cannot rule that container out.
        if (rows.Count < containers.Count)
            incomplete = true;

        if (incomplete)
            logger.LogWarning(
                "Protected-OU check read {Read} of the {Expected} containers of {TargetDn}, or one without a readable objectGUID - check incomplete",
                rows.Count, containers.Count, targetDn);

        foreach (var ou in protectedOus)
        {
            if (guids.Contains(ou.Identifier))
                matches.Add($"OU:{ou.Display}");
        }

        return (matches, incomplete);
    }

    /// <summary>
    /// The target's DN and every container above it, nearest first, up to but not including the domain's own naming
    /// context (<c>DC=</c> components): an OU is never the domain head, and the components above the head are not objects
    /// a domain controller of that domain returns. Escaped commas are not separators.
    /// </summary>
    internal static IReadOnlyList<string> Containers(string targetDn)
    {
        var dns = new List<string>();
        for (var dn = targetDn.Trim();
             !string.IsNullOrEmpty(dn) && !dn.StartsWith("DC=", StringComparison.OrdinalIgnoreCase);
             dn = EmergencyDisableService.ParentDnOf(dn))
            dns.Add(dn);
        return dns;
    }
}
