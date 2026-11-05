using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Services;

/// <summary>Which Module Configuration grants a Verify press covers: the store, the module, and - section access - its aliases.</summary>
/// <param name="Store"><see cref="VerifyIgnoreList.SectionAccessStore"/> or <see cref="VerifyIgnoreList.ModuleAdminsStore"/>.</param>
/// <param name="ModuleId">The module whose page this is.</param>
/// <param name="Aliases">Section access: the module's policy aliases and its servicer key. Module admins: unused.</param>
internal sealed record ModuleGroupVerifyScope(string Store, string ModuleId, IReadOnlyList<string> Aliases)
{
    /// <summary>The <c>verify_last</c> key of this pane: one line per store and module (plan C.1).</summary>
    public string LastVerifiedKey => $"{Store}:{ModuleId}";
}

/// <summary>What a Verify press of one Module Configuration pane read (plan B.1, B.3), from one read transaction.</summary>
internal sealed record ModuleGroupVerifyWorkset(ModuleGroupVerifyScope Scope, ModuleGroupVerifySnapshot Snapshot)
{
    public string Version => Snapshot.Version;

    /// <summary>The items this workset verifies.</summary>
    public IReadOnlyList<VerifyItem> Items => ModuleGroupVerifyAdapter.ItemsFrom(Scope.Store, Snapshot.Rows, Snapshot.DisplayNames, Version);

    public string? IgnoresRaw => Snapshot.IgnoresRaw;

    /// <summary>The ignore list; empty, with a Warning, when it does not parse.</summary>
    public IReadOnlyList<VerifyIgnoreEntry> Ignores(ILogger? logger) => VerifyIgnoreList.ParseOrEmpty(IgnoresRaw, logger);
}

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md A3/A4, B.2 (slice V3). The Verify adapter of Module Configuration's group grants: one class,
/// two stores - <see cref="VerifyIgnoreList.SectionAccessStore"/> (the Access tab's groups and the Protection servicer's, both
/// <c>section_access</c> rows, row key = policy alias) and <see cref="VerifyIgnoreList.ModuleAdminsStore"/> (<c>module_admins</c>,
/// row key = module id). The key shapes are the same; only the rule for a grant stored by name differs, on purpose.
/// </summary>
/// <remarks>
/// <para><b>Grants stored as a SID</b> are looked up by SID at the global catalog (the engine's batched read): none -> missing;
/// several -> several objects; not of class <c>group</c> -> wrong type; the stored display name, where one exists, compared
/// with the group's current sAMAccountName -> name changed; else verified. The display name is <c>DOMAIN\name</c> (or a bare
/// name); only its name half is compared, because a SID fixes its domain - a group of another domain has another SID. A grant
/// with no stored display name (a module admin whose SID has no section-access row) is never "name changed".</para>
/// <para><b>A value shaped like a SID but not a usable group SID</b> (a well-known SID, a domain SID, an SDDL alias) grants
/// nothing at any gate: wrong type, Remove only, never ignorable.</para>
/// <para><b>Grants stored by name</b> are not converted (decision 2026-11-03: they grant nothing until converted, and are never
/// ignorable). Each store keeps its own resolver, unchanged (plan B.2): section access through
/// <see cref="SectionAccessSidMigrationPlanner.Plan"/> over <see cref="ISectionAccessGroupDirectory"/> - the startup migration's
/// rule: <c>DOMAIN\Name</c> in that domain, a bare name in the host's own domain, never forest-wide; module admins through
/// <see cref="ProtectedPrincipalIdentifierResolver.ResolveModuleAdminGrants"/> - <c>db convert-identifiers</c>' rule:
/// <c>DOMAIN\name</c> in that domain, a bare name through the global catalog, forest-wide. Nothing is named or defaulted
/// (Invariant 7): both rules discover their domain at run time.</para>
/// <para><b>Ignores here change only what Verify reports</b> (plan B.4): section access, the servicer and module admins compare
/// stored SIDs with token claims, and no authorization path reads the ignore list.</para>
/// </remarks>
internal sealed class ModuleGroupVerifyAdapter : IVerifyStoreAdapter
{
    private readonly ISectionAccessGroupDirectory? _sectionAccessDirectory;

    /// <param name="store">The store this instance verifies.</param>
    /// <param name="sectionAccessDirectory">Section access: the SID migration's own directory, which resolves its names.</param>
    public ModuleGroupVerifyAdapter(string store, ISectionAccessGroupDirectory? sectionAccessDirectory = null)
    {
        if (store is not (VerifyIgnoreList.SectionAccessStore or VerifyIgnoreList.ModuleAdminsStore))
            throw new ArgumentException($"Not a Module Configuration group store: '{store}'", nameof(store));
        Store = store;
        _sectionAccessDirectory = sectionAccessDirectory;
    }

    public string Store { get; }

    /// <summary>The usable group SID <paramref name="value"/> holds, as parsed; null for a name or an unusable SID.</summary>
    internal static string? UsableSid(string value) =>
        SectionAccessGroupIdentity.Parse(value) is { Kind: StoredGroupValueKind.Sid, Sid: { } sid } ? sid : null;

    /// <summary>Whether <paramref name="value"/> is shaped like a SID but is not a usable group SID.</summary>
    internal static bool IsUnusableSid(string value) =>
        value.Trim().StartsWith("S-1-", StringComparison.OrdinalIgnoreCase) && UsableSid(value) is null;

    /// <summary>The items <paramref name="rows"/> hold, read at <paramref name="version"/>: every non-blank grant.</summary>
    public static IReadOnlyList<VerifyItem> ItemsFrom(
        string store, IReadOnlyList<GroupGrantRow> rows, IReadOnlyDictionary<string, string> displayNames, string version) =>
        rows.Where(r => !string.IsNullOrWhiteSpace(r.Value))
            .Select(r => new VerifyItem(store, r.RowKey, VerifyObjectKind.Group, UsableSid(r.Value),
                displayNames.TryGetValue(r.Value, out var name) ? name : string.Empty, r.Value, version))
            .ToList();

    public VerifyLookup LookupFor(VerifyItem item)
    {
        if (item.Identifier is { } sid)
            return new VerifyLookup(VerifyLookupKind.Sid, sid);

        return Store == VerifyIgnoreList.SectionAccessStore
            ? new VerifyLookup(VerifyLookupKind.ResolveOutsideDirectory)
            : new VerifyLookup(VerifyLookupKind.InDirectory);
    }

    public VerifyFinding Compare(VerifyItem item, VerifyLookupResult.Found result)
    {
        var found = result.Objects;
        if (found.Count == 0)
            return VerifyFinding.Missing();
        if (found.Count > 1)
            return VerifyFinding.Ambiguous(string.Join("; ", found.Select(o => o.DistinguishedName).Order(StringComparer.OrdinalIgnoreCase)));

        var only = found[0];
        if (!string.Equals(only.ObjectClass, "group", StringComparison.OrdinalIgnoreCase))
            return VerifyFinding.WrongClass(only.ObjectClass ?? "class not read");

        if (CurrentLabel(item.Label, only.SamAccountName) is { } current
            && !string.Equals(BareName(item.Label), BareName(current), StringComparison.OrdinalIgnoreCase))
            return VerifyFinding.LabelDrift(current);

        return VerifyFinding.Verified();
    }

    /// <summary>
    /// B.2: the display name the stored one should be today - its domain half kept (the SID fixes the domain), its name half the
    /// group's current sAMAccountName. Null when nothing is stored, or the sAMAccountName was not read: nothing to compare.
    /// </summary>
    internal static string? CurrentLabel(string stored, string? samAccountName)
    {
        var label = (stored ?? string.Empty).Trim();
        if (label.Length == 0 || string.IsNullOrWhiteSpace(samAccountName))
            return null;

        var slash = label.IndexOf('\\');
        return slash > 0 ? $"{label[..slash]}\\{samAccountName.Trim()}" : samAccountName.Trim();
    }

    private static string BareName(string label)
    {
        var trimmed = label.Trim();
        var slash = trimmed.IndexOf('\\');
        return slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
    }

    private static VerifyFinding UnusableSidFinding(string value) =>
        VerifyFinding.WrongClass($"well-known or unusable SID ({SectionAccessGroupIdentity.SidRejectionReason(value.Trim())})");

    /// <summary>
    /// A4, module admins: each grant stored by name resolved by <see cref="ProtectedPrincipalIdentifierResolver.ResolveModuleAdminGrants"/>
    /// - the rule <c>db convert-identifiers</c> uses, a bare name forest-wide - in the press's one directory session.
    /// </summary>
    public IReadOnlyList<VerifyFinding> ResolveInDirectory(IReadOnlyList<VerifyItem> items, IVerifyDirectory directory, CancellationToken cancellationToken)
    {
        var findings = new VerifyFinding?[items.Count];
        var toResolve = new List<int>();
        for (var i = 0; i < items.Count; i++)
        {
            if (IsUnusableSid(items[i].StoredValue))
                findings[i] = UnusableSidFinding(items[i].StoredValue);
            else
                toResolve.Add(i);
        }

        var resolved = ProtectedPrincipalIdentifierResolver.ResolveModuleAdminGrants(
            toResolve.Select(i => items[i].StoredValue).ToList(), directory, cancellationToken);
        for (var j = 0; j < toResolve.Count; j++)
            findings[toResolve[j]] = VerifyFinding.NotConverted(ProtectedPrincipalVerifyAdapter.ResolutionOf(resolved[j]));

        return findings.Select(f => f ?? VerifyFinding.Unreadable("not looked up")).ToList();
    }

    /// <summary>
    /// A3, section access: each grant stored by name resolved by <see cref="SectionAccessSidMigrationPlanner.Plan"/> over the
    /// migration's own <see cref="ISectionAccessGroupDirectory"/> - the startup migration's rule, a bare name in the host's own
    /// domain - one distinct value at a time, so one lookup that cannot run leaves only its own rows unanswered.
    /// </summary>
    public Task<IReadOnlyList<VerifyFinding>> ResolveOutsideDirectoryAsync(IReadOnlyList<VerifyItem> items, CancellationToken cancellationToken)
    {
        if (_sectionAccessDirectory is not { } directory)
            return Task.FromResult<IReadOnlyList<VerifyFinding>>(
                items.Select(_ => VerifyFinding.Unreadable("this store has no resolver outside the directory")).ToList());

        return Task.Run<IReadOnlyList<VerifyFinding>>(() =>
        {
            var byValue = new Dictionary<string, VerifyFinding>(StringComparer.OrdinalIgnoreCase);
            var findings = new List<VerifyFinding>(items.Count);
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!byValue.TryGetValue(item.StoredValue, out var finding))
                {
                    finding = IsUnusableSid(item.StoredValue) ? UnusableSidFinding(item.StoredValue) : ResolveSectionAccessName(item, directory);
                    byValue[item.StoredValue] = finding;
                }
                findings.Add(finding);
            }
            return findings;
        }, cancellationToken);
    }

    private static VerifyFinding ResolveSectionAccessName(VerifyItem item, ISectionAccessGroupDirectory directory)
    {
        RowMigrationResult row;
        try
        {
            row = SectionAccessSidMigrationPlanner.Plan([(item.RowKey, item.StoredValue)], directory).Rows[0];
        }
        catch (DirectoryUnavailableException ex)
        {
            return VerifyFinding.NotConverted(new VerifyResolution(VerifyResolutionKind.Unreadable, $"the directory could not be asked: {ex.Message}"));
        }

        if (row.Converted && row.Sid is { } sid)
            return VerifyFinding.NotConverted(new VerifyResolution(
                VerifyResolutionKind.Convertible, $"converts to {sid}{(row.DisplayName is { } name ? $" ({name})" : "")}",
                ProtectedPrincipalRepository.KindGroupSid, sid, row.DisplayName));

        return VerifyFinding.NotConverted(new VerifyResolution(
            row.Outcome == GroupResolutionOutcome.Ambiguous ? VerifyResolutionKind.Several : VerifyResolutionKind.None,
            row.Failure ?? "not found"));
    }

    // B.4: every ignore here is display-only, and only a grant stored as a usable SID can carry one.
    public bool CanIgnore(VerifyItem item, VerifyFinding finding) =>
        item.Identifier is not null
        && finding.Kind is VerifyFindingKind.Missing or VerifyFindingKind.LabelDrift or VerifyFindingKind.WrongClass or VerifyFindingKind.Ambiguous;

    public IReadOnlyList<VerifyAction> ActionsFor(VerifyItem item, VerifyFinding finding) => finding.Kind switch
    {
        VerifyFindingKind.LabelDrift => [VerifyAction.Rename, VerifyAction.Remove, VerifyAction.Ignore],
        VerifyFindingKind.Missing or VerifyFindingKind.Ambiguous => [VerifyAction.Replace, VerifyAction.Remove, VerifyAction.Ignore],
        // A value that is not a usable group SID grants nothing at any gate: Remove only (plan B.2).
        VerifyFindingKind.WrongClass => item.Identifier is null
            ? [VerifyAction.Remove]
            : [VerifyAction.Replace, VerifyAction.Remove, VerifyAction.Ignore],
        VerifyFindingKind.NotConverted => finding.Resolution?.Kind switch
        {
            VerifyResolutionKind.Convertible => [VerifyAction.Convert, VerifyAction.Remove],
            VerifyResolutionKind.None or VerifyResolutionKind.Several => [VerifyAction.Replace, VerifyAction.Remove],
            _ => [VerifyAction.Remove],
        },
        VerifyFindingKind.Ignored => [VerifyAction.Replace, VerifyAction.Remove],
        VerifyFindingKind.Unreadable => [VerifyAction.Remove],
        _ => [],
    };
}
