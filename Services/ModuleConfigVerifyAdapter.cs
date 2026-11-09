using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Services;

/// <summary>Which module rule a text setting is verified by (docs/DirectoryObjectVerify-Plan.md A5-A8, B.2).</summary>
internal enum ConfigVerifyRule
{
    /// <summary>A5: the Migration module's own per-domain Get-ADGroup lookup.</summary>
    ExcludedGroup,

    /// <summary>A6: Get-ADOrganizationalUnit, the Emergency Disable module's own precondition - an OU only.</summary>
    OrganizationalUnit,

    /// <summary>A7: a base read of the DN in its own domain - any object a search can start at.</summary>
    SearchBase,

    /// <summary>A8: Exchange's Get-Recipient through the Conference Rooms module's session - one recipient of a group type.</summary>
    RecipientGroup,
}

/// <summary>
/// One Module Configuration setting Verify checks (docs/DirectoryObjectVerify-Plan.md A5-A8): its module, its key, the rule,
/// and - for a list - the character the module splits it at. <see cref="ReachDisclosed"/>: the module reads the object with its
/// own account (and BitLocker against its own server), so a verified answer proves existence and type, not that reach (R5).
/// <see cref="WarnBeforeLastEntry"/>: an empty value widens what the module does, so removing the last entry is confirmed.
/// </summary>
internal sealed record ConfigVerifySetting(
    string ModuleId, string Key, ConfigVerifyRule Rule, char? Separator = null, bool ReachDisclosed = false, bool WarnBeforeLastEntry = false)
{
    public VerifyObjectKind Kind => Rule switch
    {
        ConfigVerifyRule.ExcludedGroup => VerifyObjectKind.Group,
        ConfigVerifyRule.OrganizationalUnit => VerifyObjectKind.OrganizationalUnit,
        ConfigVerifyRule.SearchBase => VerifyObjectKind.SearchBase,
        _ => VerifyObjectKind.RecipientGroup,
    };

    /// <summary>
    /// The entries <paramref name="raw"/> holds, split as the module splits it: a list at its separator with blank entries
    /// dropped and each trimmed (MigrationService's ExcludedADGroups, ADAttributeEditorService's DefaultSearchBase); a single
    /// value trimmed, none when blank. Distinct, ignoring case: one entry stored twice is one entry.
    /// </summary>
    public IReadOnlyList<string> EntriesOf(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return [];
        return Separator is { } separator
            ? raw.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : [raw.Trim()];
    }

    /// <summary>
    /// The value with <paramref name="entry"/> removed (C.2, R3-1), by the module's own parsing: a list keeps every other
    /// segment exactly as stored, in order; a single value, or a list left with no entry, is empty.
    /// </summary>
    public string WithoutEntry(string? raw, string entry)
    {
        if (Separator is not { } separator)
            return "";
        var kept = (raw ?? "").Split(separator)
            .Where(segment => !string.Equals(segment.Trim(), entry.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        return kept.All(string.IsNullOrWhiteSpace) ? "" : string.Join(separator, kept);
    }
}

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md C.2 (slice V4). The Verify lookup of the Conference Rooms group addresses (A8): one
/// <c>Get-Recipient</c> per address through that module's Exchange session, each answered found (its recipients; none is a
/// confirmed not-found) or unreadable, in order.
/// </summary>
internal interface IRecipientGroupLookup
{
    Task<IReadOnlyList<VerifyLookupResult>> LookUpAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken);
}

/// <summary>
/// The production <see cref="IRecipientGroupLookup"/>: a scope per press, and in it the scoped Conference Rooms service's own
/// pooled Exchange read (<see cref="ConferenceRoomService.LookUpRecipientsForVerifyAsync"/>).
/// </summary>
internal sealed class ConferenceRoomRecipientGroupLookup(IServiceScopeFactory scopes) : IRecipientGroupLookup
{
    public async Task<IReadOnlyList<VerifyLookupResult>> LookUpAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ConferenceRoomService>().LookUpRecipientsForVerifyAsync(addresses, cancellationToken);
    }
}

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md A5-A8, B.2, C.2, C.3 (slice V4). The Verify adapter of the Module Configuration settings
/// that hold a directory object as text: one store (<see cref="VerifyIgnoreList.ConfigStore"/>), one item per entry of a list
/// and one per single value (row key = the setting key, stored value = the entry), and each setting checked by the rule its
/// own module applies when it uses the value - so Verify never calls wrong a value the module accepts.
/// </summary>
/// <remarks>
/// <para><b>A5, Migration's excluded groups</b>: each comma entry through <see cref="MigrationService.TryResolveExcludedGroup"/>
/// in each of the forest's domains (asked as that module asks): one group -> verified; none in any domain -> missing; groups in
/// several domains -> several objects (the module excludes the members of each); a domain that could not be asked ->
/// unreadable.</para>
/// <para><b>A6, Emergency Disable's lockdown OU</b>: <c>Get-ADOrganizationalUnit</c> in the DN's own domain: an OU -> verified;
/// the AD module's not-found -> missing, and that is its answer for a container that is not an OU too, which the module refuses
/// as it refuses a missing OU (plan B.2: missing, not wrong type).</para>
/// <para><b>A7, the search bases</b> (AD Attribute Editor's semicolon list, BitLocker Recovery's single DN): a read of the DN in
/// its own domain: any object -> verified (a search can start at an OU, a container or a domain root); none -> missing.</para>
/// <para><b>A8, Conference Rooms' eight group addresses</b>: <c>Get-Recipient</c> through that module's Exchange session: one
/// recipient of a group type -> verified; Exchange's not-found -> missing; another recipient type -> wrong type; several ->
/// several objects. Conference Rooms runs no protected-principal check (Known Failure Class 3) and Verify adds none: it reads.</para>
/// <para><b>Every DN's domain is placed in the host's own forest</b> first (the engine's forest check, F6); nothing is named or
/// defaulted (Invariant 7). An ignore here changes only what Verify reports: no module reads the list.</para>
/// </remarks>
internal sealed class ModuleConfigVerifyAdapter : IVerifyStoreAdapter
{
    /// <summary>The settings Verify checks, by module. Each key belongs to one module, so the setting key is the ignore's row key.</summary>
    public static IReadOnlyList<ConfigVerifySetting> Settings { get; } =
    [
        new("Migration", "ExcludedADGroups", ConfigVerifyRule.ExcludedGroup, Separator: ','),
        new("EmergencyDisable", "LockdownOuDn", ConfigVerifyRule.OrganizationalUnit, ReachDisclosed: true),
        new("ADAttributeEditor", "DefaultSearchBase", ConfigVerifyRule.SearchBase, Separator: ';', WarnBeforeLastEntry: true),
        new("BitLockerRecovery", "ActiveDirectorySearchBase", ConfigVerifyRule.SearchBase, ReachDisclosed: true),
        new("ConferenceRooms", "DefaultArbiterGroup", ConfigVerifyRule.RecipientGroup),
        new("ConferenceRooms", "ExecConfCoordinatorsGroup", ConfigVerifyRule.RecipientGroup),
        new("ConferenceRooms", "ConfExecAdminsGroup", ConfigVerifyRule.RecipientGroup),
        new("ConferenceRooms", "ConfExecVPsGroup", ConfigVerifyRule.RecipientGroup),
        new("ConferenceRooms", "ConfAdminsGroup", ConfigVerifyRule.RecipientGroup),
        new("ConferenceRooms", "ConfCEOGroup", ConfigVerifyRule.RecipientGroup),
        new("ConferenceRooms", "ConfExceptionGroup", ConfigVerifyRule.RecipientGroup),
        new("ConferenceRooms", "ADGTAdminsGroup", ConfigVerifyRule.RecipientGroup),
    ];

    /// <summary>The Exchange recipient types that are groups (A8): mail-enabled distribution and security groups, and dynamic ones.</summary>
    internal static readonly IReadOnlySet<string> GroupRecipientTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "MailUniversalDistributionGroup", "MailUniversalSecurityGroup", "MailNonUniversalGroup", "DynamicDistributionGroup",
    };

    /// <summary>The A6 / BitLocker reach disclosure (R5), in every hover of those settings' findings.</summary>
    internal const string ReachDisclosure =
        "Verify does not test that the module's own account (or, for BitLocker, its configured server) can reach it.";

    private readonly IRecipientGroupLookup? _recipients;

    public ModuleConfigVerifyAdapter(IRecipientGroupLookup? recipients = null) => _recipients = recipients;

    public string Store => VerifyIgnoreList.ConfigStore;

    /// <summary>The settings of <paramref name="moduleId"/> Verify checks; none for a module without any.</summary>
    public static IReadOnlyList<ConfigVerifySetting> SettingsOf(string moduleId) =>
        Settings.Where(s => string.Equals(s.ModuleId, moduleId, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>The verified setting under <paramref name="key"/> (any module: each key belongs to one), or null.</summary>
    public static ConfigVerifySetting? SettingFor(string key) =>
        Settings.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The items of <paramref name="moduleId"/>'s verified settings, from <paramref name="values"/> read at <paramref name="version"/>.</summary>
    public static IReadOnlyList<VerifyItem> ItemsFrom(string moduleId, IReadOnlyDictionary<string, string> values, string version) =>
        SettingsOf(moduleId)
            .SelectMany(s => s.EntriesOf(values.GetValueOrDefault(s.Key))
                .Select(entry => new VerifyItem(VerifyIgnoreList.ConfigStore, s.Key, s.Kind, null, string.Empty, entry, version)))
            .ToList();

    public VerifyLookup LookupFor(VerifyItem item)
    {
        if (SettingFor(item.RowKey) is not { } setting)
            return VerifyLookup.NotLookedUp("this setting is not one Verify checks");

        var entry = item.StoredValue.Trim();
        return setting.Rule switch
        {
            ConfigVerifyRule.ExcludedGroup => new VerifyLookup(VerifyLookupKind.InDirectory, entry),
            ConfigVerifyRule.OrganizationalUnit or ConfigVerifyRule.SearchBase => new VerifyLookup(VerifyLookupKind.InDirectory, entry, StoredDn: entry),
            _ => new VerifyLookup(VerifyLookupKind.OutsideDirectory, entry),
        };
    }

    /// <summary>A8: a completed Get-Recipient answer (<see cref="LookUpOutsideDirectoryAsync"/>).</summary>
    public VerifyFinding Compare(VerifyItem item, VerifyLookupResult.Found result)
    {
        var found = result.Objects;
        if (found.Count == 0)
            return VerifyFinding.Missing();
        if (found.Count > 1)
            return VerifyFinding.Ambiguous(string.Join("; ", found.Select(o => o.DistinguishedName).Order(StringComparer.OrdinalIgnoreCase)));

        var only = found[0];
        return only.ObjectClass is { } type && GroupRecipientTypes.Contains(type)
            ? VerifyFinding.Verified()
            : VerifyFinding.WrongClass(only.ObjectClass ?? "recipient type not read");
    }

    /// <summary>A5, A6, A7, in the press's one directory session. One entry's failure leaves only that entry unreadable.</summary>
    public IReadOnlyList<VerifyFinding> ResolveInDirectory(IReadOnlyList<VerifyItem> items, IVerifyDirectory directory, CancellationToken cancellationToken)
    {
        var findings = new VerifyFinding[items.Count];
        IReadOnlyList<string>? domains = null;
        string? domainsProblem = null;
        var servers = new Dictionary<string, (string? Server, string? Problem)>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[i];
            var entry = item.StoredValue.Trim();
            try
            {
                findings[i] = SettingFor(item.RowKey)?.Rule switch
                {
                    ConfigVerifyRule.ExcludedGroup => ExcludedGroupFinding(directory, entry, ref domains, ref domainsProblem),
                    ConfigVerifyRule.OrganizationalUnit => ServerFor(directory, entry, servers) is { Server: { } server }
                        ? directory.OrganizationalUnitExists(entry, server) ? VerifyFinding.Verified() : VerifyFinding.Missing()
                        : VerifyFinding.Unreadable(ServerFor(directory, entry, servers).Problem ?? DirectoryObjectVerifier.NotInForest),
                    ConfigVerifyRule.SearchBase => ServerFor(directory, entry, servers) is { Server: { } baseServer }
                        ? SearchBaseFinding(directory.ReadByDns([entry], baseServer), entry)
                        : VerifyFinding.Unreadable(ServerFor(directory, entry, servers).Problem ?? DirectoryObjectVerifier.NotInForest),
                    _ => VerifyFinding.Unreadable("this setting has no lookup in the directory session"),
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                findings[i] = VerifyFinding.Unreadable($"the directory read failed: {ex.Message}");
            }
        }

        return findings;
    }

    private static VerifyFinding ExcludedGroupFinding(
        IVerifyDirectory directory, string entry, ref IReadOnlyList<string>? domains, ref string? domainsProblem)
    {
        if (domains is null && domainsProblem is null)
        {
            try
            {
                domains = directory.ForestDomainNames();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                domainsProblem = $"the forest's domains could not be read: {ex.Message}";
            }
        }

        if (domains is null)
            return VerifyFinding.Unreadable(domainsProblem ?? "the forest's domains could not be read");

        return directory.ResolveExcludedGroup(entry, domains) switch
        {
            MigrationService.ExcludedGroupResolution.Found { Groups.Count: 1 } => VerifyFinding.Verified(),
            MigrationService.ExcludedGroupResolution.Found several => VerifyFinding.Ambiguous(
                string.Join("; ", several.Groups.Select(g => $"{g.Domain}: {g.Dn}"))),
            MigrationService.ExcludedGroupResolution.NotFound => VerifyFinding.Missing(),
            MigrationService.ExcludedGroupResolution.Unreadable unreadable => VerifyFinding.Unreadable(unreadable.Reason),
            _ => VerifyFinding.Unreadable("the lookup gave no answer"),
        };
    }

    // The DN's own domain, placed in the host's forest (F6): the engine has already refused a DN outside it, so this answers for
    // the domains it let through, asked once each.
    private static (string? Server, string? Problem) ServerFor(
        IVerifyDirectory directory, string dn, Dictionary<string, (string? Server, string? Problem)> servers)
    {
        var domain = ADDirectorySearchService.DnsDomainFromDn(dn);
        if (domain is null)
            return (null, ProtectedGroupMembership.DnNamesNoDomain);
        if (servers.TryGetValue(domain, out var known))
            return known;

        (string? Server, string? Problem) answer;
        try
        {
            var server = directory.ForestDomainServer(domain);
            answer = (server, server is null ? DirectoryObjectVerifier.NotInForest : null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            answer = (null, $"the forest could not be asked about domain '{domain}': {ex.Message}");
        }
        servers[domain] = answer;
        return answer;
    }

    // A7: any object at the DN - compared without the spaces a DN may be typed with around its separators - is a search base.
    private static VerifyFinding SearchBaseFinding(IReadOnlyList<VerifyDirectoryObject> found, string dn) =>
        found.Any(o => string.Equals(NormalizeDn(o.DistinguishedName), NormalizeDn(dn), StringComparison.OrdinalIgnoreCase))
            ? VerifyFinding.Verified()
            : VerifyFinding.Missing();

    private static readonly Regex DnSeparatorSpace = new(@"\s*(?<!\\)([,=])\s*", RegexOptions.CultureInvariant);

    internal static string NormalizeDn(string dn) => DnSeparatorSpace.Replace(dn.Trim(), "$1");

    /// <summary>A8: the Conference Rooms module's Exchange session, one Get-Recipient per address; none configured - unreadable.</summary>
    public async Task<IReadOnlyList<VerifyLookupResult>> LookUpOutsideDirectoryAsync(IReadOnlyList<VerifyItem> items, CancellationToken cancellationToken)
    {
        if (_recipients is null)
            return items.Select(_ => (VerifyLookupResult)new VerifyLookupResult.Unreadable("Exchange cannot be asked from here")).ToList();
        return await _recipients.LookUpAsync(items.Select(i => i.StoredValue.Trim()).ToList(), cancellationToken);
    }

    // Display-only everywhere here (plan C.2): no module reads the ignore list.
    public bool CanIgnore(VerifyItem item, VerifyFinding finding) =>
        finding.Kind is VerifyFindingKind.Missing or VerifyFindingKind.Ambiguous or VerifyFindingKind.WrongClass;

    /// <summary>
    /// C.2: Replace is "edit the text" (the field is opened for editing - with the OU browser for a DN - and the pane's ordinary
    /// Save writes it); Remove takes the one entry out by the module's own parsing; Ignore is display-only. Unreadable: Remove only.
    /// </summary>
    public IReadOnlyList<VerifyAction> ActionsFor(VerifyItem item, VerifyFinding finding) => finding.Kind switch
    {
        VerifyFindingKind.Missing or VerifyFindingKind.Ambiguous or VerifyFindingKind.WrongClass =>
            [VerifyAction.Replace, VerifyAction.Remove, VerifyAction.Ignore],
        VerifyFindingKind.Ignored => [VerifyAction.Replace, VerifyAction.Remove],
        VerifyFindingKind.Unreadable => [VerifyAction.Remove],
        _ => [],
    };
}
