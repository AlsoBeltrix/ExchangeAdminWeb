using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// What a Verify press of the protected principals read (docs/DirectoryObjectVerify-Plan.md B.1, B.3): the rows, their
/// version, and the raw ignore list, from one read transaction.
/// </summary>
internal sealed record ProtectedPrincipalVerifyWorkset(ProtectedPrincipalData Rules, string Version, string? IgnoresRaw)
{
    /// <summary>The items this workset verifies.</summary>
    public IReadOnlyList<VerifyItem> Items => ProtectedPrincipalVerifyAdapter.ItemsFrom(Rules, Version);

    /// <summary>The ignore list; empty, with a Warning, when it does not parse.</summary>
    public IReadOnlyList<VerifyIgnoreEntry> Ignores(ILogger? logger) => VerifyIgnoreList.ParseOrEmpty(IgnoresRaw, logger);
}

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md A1/A2, B.2. The Verify adapter of the Admin Settings protected principals: identifier rows
/// (<c>user_sid</c>, <c>group_sid</c>, <c>ou_guid</c>, <c>group_target</c>) looked up by identifier at the global catalog and
/// compared by class and canonical name; legacy rows with no identifier companion resolved by the conversion's own resolver
/// and reported not converted. <c>sam_pattern</c> rows are patterns, not objects, and are not verified.
/// </summary>
/// <remarks>
/// <para><b>Canonical names (B.2)</b>, as the validator stores them on add (<see cref="ProtectedPrincipalEntryValidator.CanonicalValue"/>):
/// a user's userPrincipalName, else its mail, else the stored label unchanged - a user with neither is never "name changed";
/// a group, OU or group target's distinguishedName. Compared ignoring case.</para>
/// <para><b>Classes.</b> A user row names a user object (<c>user</c> or <c>inetOrgPerson</c>; a computer is not one); a group
/// row and a target a <c>group</c>; an OU row an <c>organizationalUnit</c>.</para>
/// <para><b>Rows stored by name (A2)</b> are never ignorable (decision 2026-11-03: an unconverted row fails closed until it is
/// converted): Convert when the resolver finds exactly one object, Replace when it finds none or several, Remove always. Their
/// DNs get the forest check too: a DN of a domain the forest lacks is "could not verify", never "not found".</para>
/// </remarks>
internal sealed class ProtectedPrincipalVerifyAdapter : IVerifyStoreAdapter
{
    public string Store => VerifyIgnoreList.ProtectedPrincipalsStore;

    private static readonly HashSet<string> IdentifierKinds = new(StringComparer.Ordinal)
    {
        ProtectedPrincipalRepository.KindUserSid,
        ProtectedPrincipalRepository.KindGroupSid,
        ProtectedPrincipalRepository.KindOuGuid,
        ProtectedPrincipalRepository.KindGroupTarget,
    };

    /// <summary>Why an identifier row whose identifier does not parse is unreadable.</summary>
    internal const string IdentifierUnreadable = "its stored identifier cannot be read - remove it and pick it again from the search";

    /// <summary>
    /// The items <paramref name="rules"/> hold, read at <paramref name="version"/>: every identifier row and target, and every
    /// legacy user, group and OU row no identifier row pairs with. A companion is not an item: its identifier row is.
    /// </summary>
    public static IReadOnlyList<VerifyItem> ItemsFrom(ProtectedPrincipalData rules, string version)
    {
        var store = VerifyIgnoreList.ProtectedPrincipalsStore;
        var items = new List<VerifyItem>();

        void Ids(string kind, VerifyObjectKind objectKind, string[] rows, Func<string?, string?> canonical)
        {
            foreach (var row in rows.Where(r => !string.IsNullOrWhiteSpace(r)))
            {
                var entry = ProtectedPrincipalIdEntry.Parse(row);
                items.Add(new VerifyItem(store, kind, objectKind, canonical(entry?.Identifier), entry?.Label ?? string.Empty, row, version));
            }
        }

        void Legacy(string kind, VerifyObjectKind objectKind, string[] legacy, string[] ids)
        {
            foreach (var value in ProtectedPrincipalIdEntry.Unpaired(legacy.Where(v => !string.IsNullOrWhiteSpace(v)), ids))
                items.Add(new VerifyItem(store, kind, objectKind, null, value.Trim(), value, version));
        }

        Ids(ProtectedPrincipalRepository.KindUserSid, VerifyObjectKind.User, rules.UserIds, ProtectedPrincipalIdEntry.CanonicalSid);
        Ids(ProtectedPrincipalRepository.KindGroupSid, VerifyObjectKind.Group, rules.GroupIds, ProtectedPrincipalIdEntry.CanonicalSid);
        Ids(ProtectedPrincipalRepository.KindOuGuid, VerifyObjectKind.OrganizationalUnit, rules.OuIds, ProtectedPrincipalIdEntry.CanonicalGuid);

        foreach (var row in rules.GroupTargets.Where(r => !string.IsNullOrWhiteSpace(r)))
        {
            var target = ProtectedGroupTargetEntry.Parse(row);
            items.Add(new VerifyItem(store, ProtectedPrincipalRepository.KindGroupTarget, VerifyObjectKind.GroupTarget,
                ProtectedPrincipalIdEntry.CanonicalGuid(target.ObjectGuid), target.DistinguishedName ?? string.Empty, row, version));
        }

        Legacy(ProtectedPrincipalRepository.KindUser, VerifyObjectKind.User, rules.Users, rules.UserIds);
        Legacy(ProtectedPrincipalRepository.KindGroup, VerifyObjectKind.Group, rules.Groups, rules.GroupIds);
        Legacy(ProtectedPrincipalRepository.KindOu, VerifyObjectKind.OrganizationalUnit, rules.OrganizationalUnits, rules.OuIds);
        return items;
    }

    private static bool IsDn(string? value) => value is { } v && v.Contains('=') && v.Contains(',');

    public VerifyLookup LookupFor(VerifyItem item)
    {
        if (!IdentifierKinds.Contains(item.RowKey))
            return new VerifyLookup(VerifyLookupKind.InDirectory);

        var storedDn = IsDn(item.Label) ? item.Label : null;
        if (item.RowKey == ProtectedPrincipalRepository.KindGroupTarget && item.Identifier is null)
        {
            // A hand-repaired target carrying only its DN is still enforced by that DN, so it is read by that DN.
            return storedDn is not null
                ? new VerifyLookup(VerifyLookupKind.Dn, storedDn, storedDn)
                : VerifyLookup.NotLookedUp(IdentifierUnreadable);
        }

        if (item.Identifier is null)
            return VerifyLookup.NotLookedUp(IdentifierUnreadable);

        return item.RowKey is ProtectedPrincipalRepository.KindOuGuid or ProtectedPrincipalRepository.KindGroupTarget
            ? new VerifyLookup(VerifyLookupKind.Guid, item.Identifier, storedDn)
            : new VerifyLookup(VerifyLookupKind.Sid, item.Identifier, storedDn);
    }

    public VerifyFinding Compare(VerifyItem item, VerifyLookupResult.Found result)
    {
        var found = result.Objects;
        if (found.Count == 0)
            return VerifyFinding.Missing();
        if (found.Count > 1)
            return VerifyFinding.Ambiguous(string.Join("; ", found.Select(o => o.DistinguishedName).Order(StringComparer.OrdinalIgnoreCase)));

        var only = found[0];
        if (!IsClass(item.Kind, only.ObjectClass))
            return VerifyFinding.WrongClass(only.ObjectClass ?? "class not read");

        var canonical = CanonicalLabel(item.Kind, only);
        if (canonical is not null && !string.Equals(canonical.Trim(), item.Label.Trim(), StringComparison.OrdinalIgnoreCase))
            return VerifyFinding.LabelDrift(canonical.Trim());

        return VerifyFinding.Verified();
    }

    /// <summary>B.2: the name the validator would store for <paramref name="found"/> today; null for a user with neither UPN nor mail.</summary>
    internal static string? CanonicalLabel(VerifyObjectKind kind, VerifyDirectoryObject found) => kind switch
    {
        VerifyObjectKind.User => !string.IsNullOrWhiteSpace(found.UserPrincipalName) ? found.UserPrincipalName
            : !string.IsNullOrWhiteSpace(found.Mail) ? found.Mail
            : null,
        _ => found.DistinguishedName,
    };

    private static bool IsClass(VerifyObjectKind kind, string? objectClass) => kind switch
    {
        VerifyObjectKind.User => ProtectedPrincipalService.IsUserObject(objectClass),
        VerifyObjectKind.Group or VerifyObjectKind.GroupTarget => string.Equals(objectClass, "group", StringComparison.OrdinalIgnoreCase),
        VerifyObjectKind.OrganizationalUnit => string.Equals(objectClass, "organizationalUnit", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>
    /// A2: each row stored by name, resolved by <see cref="ProtectedPrincipalIdentifierResolver.Resolve"/> - the rules
    /// <c>db convert-identifiers</c> uses - in the press's session, after the forest check on a DN-shaped value.
    /// </summary>
    public IReadOnlyList<VerifyFinding> ResolveInDirectory(IReadOnlyList<VerifyItem> items, IVerifyDirectory directory, CancellationToken cancellationToken)
    {
        var findings = new VerifyFinding?[items.Count];
        var toResolve = new List<(int Index, ProtectedPrincipalLegacyRow Row)>();
        var domains = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var value = item.StoredValue.Trim();
            if (IsDn(value) && ADDirectorySearchService.DnsDomainFromDn(value) is { } domain)
            {
                if (!domains.TryGetValue(domain, out var problem))
                {
                    try
                    {
                        problem = directory.ForestDomainServer(domain) is null ? DirectoryObjectVerifier.NotInForest : null;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        problem = $"the forest could not be asked about domain '{domain}': {ex.Message}";
                    }
                    domains[domain] = problem;
                }

                if (problem is not null)
                {
                    findings[i] = VerifyFinding.NotConverted(new VerifyResolution(VerifyResolutionKind.Unreadable, problem));
                    continue;
                }
            }

            toResolve.Add((i, new ProtectedPrincipalLegacyRow(item.RowKey, item.StoredValue)));
        }

        var resolved = ProtectedPrincipalIdentifierResolver.Resolve(toResolve.Select(r => r.Row).ToList(), directory, cancellationToken);
        for (var j = 0; j < toResolve.Count; j++)
            findings[toResolve[j].Index] = VerifyFinding.NotConverted(ResolutionOf(resolved[j]));

        return findings.Select(f => f ?? VerifyFinding.Unreadable("not looked up")).ToList();
    }

    /// <summary>The resolver's answer as a resolution: one object convertible, none, several, or a lookup that could not run.</summary>
    internal static VerifyResolution ResolutionOf(ProtectedPrincipalRowResolution r)
    {
        if (r.Identifier is { } identifier)
            return new VerifyResolution(VerifyResolutionKind.Convertible, $"converts to {identifier}", r.IdKind, identifier);

        return r.Reason switch
        {
            ProtectedPrincipalLeftReason.Ambiguous => new VerifyResolution(VerifyResolutionKind.Several, r.Detail ?? "more than one object matches"),
            ProtectedPrincipalLeftReason.DirectoryUnavailable => new VerifyResolution(VerifyResolutionKind.Unreadable, r.Detail ?? "the directory could not be read"),
            // Not found, a well-known SID, or a shape the conversion does not look up: no one object to convert to.
            _ => new VerifyResolution(VerifyResolutionKind.None, r.Detail ?? "not found"),
        };
    }

    public bool CanIgnore(VerifyItem item, VerifyFinding finding) =>
        IdentifierKinds.Contains(item.RowKey)
        && finding.Kind is VerifyFindingKind.Missing or VerifyFindingKind.LabelDrift or VerifyFindingKind.WrongClass or VerifyFindingKind.Ambiguous;

    public IReadOnlyList<VerifyAction> ActionsFor(VerifyItem item, VerifyFinding finding) => finding.Kind switch
    {
        VerifyFindingKind.LabelDrift => [VerifyAction.Rename, VerifyAction.Remove, VerifyAction.Ignore],
        VerifyFindingKind.Missing or VerifyFindingKind.WrongClass or VerifyFindingKind.Ambiguous =>
            [VerifyAction.Replace, VerifyAction.Remove, VerifyAction.Ignore],
        VerifyFindingKind.NotConverted => finding.Resolution?.Kind switch
        {
            VerifyResolutionKind.Convertible => [VerifyAction.Convert, VerifyAction.Remove],
            VerifyResolutionKind.None or VerifyResolutionKind.Several => [VerifyAction.Replace, VerifyAction.Remove],
            _ => [VerifyAction.Remove],
        },
        // An ignore is undone by removing the entry or picking its replacement (exec summary).
        VerifyFindingKind.Ignored => [VerifyAction.Replace, VerifyAction.Remove],
        VerifyFindingKind.Unreadable => [VerifyAction.Remove],
        _ => [],
    };
}
