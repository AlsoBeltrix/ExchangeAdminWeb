using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;
using ModuleConfigPage = ExchangeAdminWeb.Components.Pages.ModuleConfig;
using StandInAccess = ExchangeAdminWeb.Tests.DirectoryObjectVerifierTests.StandInAccess;
using StandInVerifyDirectory = ExchangeAdminWeb.Tests.DirectoryObjectVerifierTests.StandInVerifyDirectory;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md A6-A8, B.1, B.2, C.3 (slice V4): the config-pane adapter over stand-in directories - the
/// lockdown OU by Emergency Disable's own rule (an OU only), the search bases by a read of the DN (any object), the Conference
/// Rooms addresses by their recipient type - and the A6 read's not-found translation through the real
/// <see cref="PowerShellProtectedGroupDirectory"/> over a stand-in cmdlet. Nothing here reaches a directory or Exchange.
/// </summary>
public sealed class ModuleConfigVerifyEngineTests
{
    private const string OuDn = "OU=Lockdown,DC=contoso,DC=com";
    private const string ContainerDn = "CN=Users,DC=contoso,DC=com";
    private const string DomainRoot = "DC=contoso,DC=com";
    private const string GoneDn = "OU=Gone,DC=contoso,DC=com";
    private const string ForeignDn = "OU=Elsewhere,DC=fabrikam,DC=org";

    /// <summary>The V1 stand-in forest, answering the A6 read from its objects' classes as Get-ADOrganizationalUnit would.</summary>
    private sealed class ConfigDirectory(StandInVerifyDirectory inner) : IVerifyDirectory
    {
        public List<string> OuReads { get; } = [];
        public string? ForestDomainServer(string dnsDomain) => inner.ForestDomainServer(dnsDomain);
        public IReadOnlyList<VerifyDirectoryObject> FindBySids(IReadOnlyList<string> sids) => inner.FindBySids(sids);
        public IReadOnlyList<VerifyDirectoryObject> FindByGuids(IReadOnlyList<string> guids) => inner.FindByGuids(guids);
        // As a directory matches distinguishedName: by the name, not the spacing it was typed with; it answers its own form.
        public IReadOnlyList<VerifyDirectoryObject> ReadByDns(IReadOnlyList<string> dns, string server) =>
            inner.Objects.Where(o => dns.Any(d => string.Equals(
                ModuleConfigVerifyAdapter.NormalizeDn(d), ModuleConfigVerifyAdapter.NormalizeDn(o.DistinguishedName), StringComparison.OrdinalIgnoreCase))).ToList();
        public string ResolveDomainServer(string domain) => inner.ResolveDomainServer(domain);
        public IReadOnlyList<DirectoryGroup> FindGroups(GroupLookupKey key, string value, string? server) => inner.FindGroups(key, value, server);
        public IReadOnlyList<DirectoryObjectGuid> ReadObjectGuids(IReadOnlyList<string> dns, string server) => inner.ReadObjectGuids(dns, server);
        public IReadOnlyList<DirectoryUserObject> FindUsers(string ldapFilter, string? server) => inner.FindUsers(ldapFilter, server);

        public bool OrganizationalUnitExists(string dn, string server)
        {
            OuReads.Add($"{dn} @{server}");
            return inner.Objects.Any(o => string.Equals(o.DistinguishedName, dn, StringComparison.OrdinalIgnoreCase)
                && o.ObjectClass == "organizationalUnit");
        }
    }

    private static VerifyDirectoryObject At(string dn, string cls) => new(dn, null, null, cls, null, null, null);

    private static ConfigDirectory Forest() => new(new StandInVerifyDirectory
    {
        Objects = { At(OuDn, "organizationalUnit"), At(ContainerDn, "container"), At(DomainRoot, "domainDNS") },
    });

    private sealed class StandInRecipients : IRecipientGroupLookup
    {
        public Dictionary<string, VerifyLookupResult> Answers { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<IReadOnlyList<VerifyLookupResult>> LookUpAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<VerifyLookupResult>>(addresses.Select(a => Answers.GetValueOrDefault(a) ?? VerifyLookupResult.NotFound).ToList());
    }

    private static async Task<Dictionary<(string Key, string Entry), VerifyFinding>> VerifyAsync(
        string moduleId, Dictionary<string, string> values, IVerifyDirectory? directory = null, IRecipientGroupLookup? recipients = null)
    {
        var engine = new DirectoryObjectVerifier(new StandInAccess(directory ?? Forest()), [new ModuleConfigVerifyAdapter(recipients)],
            NullLogger<DirectoryObjectVerifier>.Instance);
        var report = await engine.VerifyAsync(ModuleConfigVerifyAdapter.ItemsFrom(moduleId, values, "v"), CancellationToken.None);
        return report.Results.ToDictionary(r => (r.Item.RowKey, r.Item.StoredValue), r => r.Finding);
    }

    private static Dictionary<string, string> Values(string key, string value) =>
        new(StringComparer.OrdinalIgnoreCase) { [key] = value };

    // ----- A6 vs A7: the lockdown OU is an OU only; a search base is any object -----

    [Fact]
    public async Task AContainerAtTheLockdownOuDn_IsMissing_WhileTheSameDnAsASearchBase_Verifies()
    {
        var lockdown = await VerifyAsync("EmergencyDisable", Values("LockdownOuDn", ContainerDn));
        var searchBase = await VerifyAsync("BitLockerRecovery", Values("ActiveDirectorySearchBase", ContainerDn));

        // B.2: missing, not wrong type - Get-ADOrganizationalUnit answers a container as no such OU, and the module refuses it
        // exactly as it refuses a missing one.
        Assert.Equal(VerifyFindingKind.Missing, Assert.Single(lockdown).Value.Kind);
        Assert.Equal(VerifyFindingKind.Verified, Assert.Single(searchBase).Value.Kind);
    }

    [Fact]
    public async Task TheLockdownOu_IsReadInItsOwnDomain_AnOuVerifies_NothingIsMissing_AForeignDomainIsUnreadable()
    {
        var directory = Forest();

        var ou = await VerifyAsync("EmergencyDisable", Values("LockdownOuDn", $" {OuDn} "), directory);
        var gone = await VerifyAsync("EmergencyDisable", Values("LockdownOuDn", GoneDn), directory);
        var foreign = await VerifyAsync("EmergencyDisable", Values("LockdownOuDn", ForeignDn), directory);

        Assert.Equal(VerifyFindingKind.Verified, Assert.Single(ou).Value.Kind);
        Assert.Equal(VerifyFindingKind.Missing, Assert.Single(gone).Value.Kind);
        var unreadable = Assert.Single(foreign).Value;
        Assert.Equal((VerifyFindingKind.Unreadable, DirectoryObjectVerifier.NotInForest), (unreadable.Kind, unreadable.Detail));
        Assert.Equal([$"{OuDn} @contoso.com", $"{GoneDn} @contoso.com"], directory.OuReads);
    }

    [Fact]
    public async Task EachSemicolonSearchBase_IsItsOwnItem_AnyObjectVerifies_AndTypedSpacingIsNotAMissingObject()
    {
        var findings = await VerifyAsync("ADAttributeEditor",
            Values("DefaultSearchBase", $"{OuDn};{GoneDn}; {DomainRoot} ;CN=Users, DC=contoso, DC=com;{ForeignDn}"));

        Assert.Equal(VerifyFindingKind.Verified, findings[("DefaultSearchBase", OuDn)].Kind);
        Assert.Equal(VerifyFindingKind.Verified, findings[("DefaultSearchBase", DomainRoot)].Kind);
        Assert.Equal(VerifyFindingKind.Verified, findings[("DefaultSearchBase", "CN=Users, DC=contoso, DC=com")].Kind);
        Assert.Equal(VerifyFindingKind.Missing, findings[("DefaultSearchBase", GoneDn)].Kind);
        Assert.Equal(VerifyFindingKind.Unreadable, findings[("DefaultSearchBase", ForeignDn)].Kind);
    }

    [Fact]
    public async Task AReadThatFails_IsUnreadable_NeverMissing()
    {
        var inner = new StandInVerifyDirectory { ForestFailure = new InvalidOperationException("The server is not operational.") };

        var findings = await VerifyAsync("BitLockerRecovery", Values("ActiveDirectorySearchBase", OuDn), new ConfigDirectory(inner));

        Assert.Equal(VerifyFindingKind.Unreadable, Assert.Single(findings).Value.Kind);
    }

    // ----- A8: one recipient of a group type -----

    [Fact]
    public async Task AConferenceRoomsAddress_IsVerifiedOnlyAsAGroup()
    {
        static VerifyLookupResult Recipient(string type) =>
            new VerifyLookupResult.Found([new VerifyDirectoryObject("CN=x", null, null, type, null, "x@contoso.com", null)]);

        var recipients = new StandInRecipients
        {
            Answers =
            {
                ["group@contoso.com"] = Recipient("MailUniversalSecurityGroup"),
                ["dynamic@contoso.com"] = Recipient("DynamicDistributionGroup"),
                ["person@contoso.com"] = Recipient("UserMailbox"),
                ["broken@contoso.com"] = new VerifyLookupResult.Unreadable("Get-Recipient failed: busy"),
                ["twice@contoso.com"] = new VerifyLookupResult.Found([new("CN=a", null, null, "MailUniversalDistributionGroup", null, null, null),
                    new("CN=b", null, null, "MailUniversalDistributionGroup", null, null, null)]),
            },
        };
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DefaultArbiterGroup"] = "group@contoso.com",
            ["ExecConfCoordinatorsGroup"] = "dynamic@contoso.com",
            ["ConfExecAdminsGroup"] = "person@contoso.com",
            ["ConfExecVPsGroup"] = "nobody@contoso.com",
            ["ConfAdminsGroup"] = "broken@contoso.com",
            ["ConfCEOGroup"] = "twice@contoso.com",
            ["ConfExceptionGroup"] = "",
        };

        var findings = await VerifyAsync("ConferenceRooms", values, recipients: recipients);

        Assert.Equal(6, findings.Count); // a blank setting is not an entry
        Assert.Equal(VerifyFindingKind.Verified, findings[("DefaultArbiterGroup", "group@contoso.com")].Kind);
        Assert.Equal(VerifyFindingKind.Verified, findings[("ExecConfCoordinatorsGroup", "dynamic@contoso.com")].Kind);
        var wrong = findings[("ConfExecAdminsGroup", "person@contoso.com")];
        Assert.Equal((VerifyFindingKind.WrongClass, "UserMailbox"), (wrong.Kind, wrong.Detail));
        Assert.Equal(VerifyFindingKind.Missing, findings[("ConfExecVPsGroup", "nobody@contoso.com")].Kind);
        Assert.Equal(VerifyFindingKind.Unreadable, findings[("ConfAdminsGroup", "broken@contoso.com")].Kind);
        Assert.Equal(VerifyFindingKind.Ambiguous, findings[("ConfCEOGroup", "twice@contoso.com")].Kind);
    }

    // ----- the actions each finding offers; ignores display-only -----

    [Fact]
    public void EveryFinding_OffersItsActions_AndUnreadableIsNeverIgnorable()
    {
        var adapter = new ModuleConfigVerifyAdapter();
        var item = new VerifyItem(VerifyIgnoreList.ConfigStore, "DefaultSearchBase", VerifyObjectKind.SearchBase, null, "", GoneDn, "v");

        foreach (var finding in new[] { VerifyFinding.Missing(), VerifyFinding.Ambiguous("a; b"), VerifyFinding.WrongClass("UserMailbox") })
        {
            Assert.Equal([VerifyAction.Replace, VerifyAction.Remove, VerifyAction.Ignore], adapter.ActionsFor(item, finding));
            Assert.True(adapter.CanIgnore(item, finding));
        }
        Assert.Equal([VerifyAction.Remove], adapter.ActionsFor(item, VerifyFinding.Unreadable("busy")));
        Assert.False(adapter.CanIgnore(item, VerifyFinding.Unreadable("busy")));
        Assert.Empty(adapter.ActionsFor(item, VerifyFinding.Verified()));
    }

    // ----- R5: the reach disclosure on A6 and BitLocker, and nowhere it does not apply -----

    [Fact]
    public void TheReachDisclosure_IsInEveryHoverOfTheLockdownOuAndTheBitLockerSearchBase_AndNotTheOthers()
    {
        static string TipOf(string key, VerifyFinding finding) => ModuleConfigPage.ConfigVerifyBadgeParts(new VerifyItemResult(
            new VerifyItem(VerifyIgnoreList.ConfigStore, key, VerifyObjectKind.SearchBase, null, "", OuDn, "v"), finding), "2026-11-05 10:00").Tip;

        foreach (var finding in new[] { VerifyFinding.Verified(), VerifyFinding.Missing(), VerifyFinding.Unreadable("busy") })
        {
            Assert.Contains(ModuleConfigVerifyAdapter.ReachDisclosure, TipOf("LockdownOuDn", finding), StringComparison.Ordinal);
            Assert.Contains(ModuleConfigVerifyAdapter.ReachDisclosure, TipOf("ActiveDirectorySearchBase", finding), StringComparison.Ordinal);
            Assert.DoesNotContain(ModuleConfigVerifyAdapter.ReachDisclosure, TipOf("DefaultSearchBase", finding), StringComparison.Ordinal);
            Assert.DoesNotContain(ModuleConfigVerifyAdapter.ReachDisclosure, TipOf("ExcludedADGroups", finding), StringComparison.Ordinal);
        }
        Assert.Contains("does not test that the module's own account", ModuleConfigVerifyAdapter.ReachDisclosure, StringComparison.Ordinal);
        Assert.Contains(ModuleConfigVerifyAdapter.ReachDisclosure, ModuleConfigPage.ConfigVerifyPaneNote("EmergencyDisable"), StringComparison.Ordinal);
        Assert.Contains(ModuleConfigVerifyAdapter.ReachDisclosure, ModuleConfigPage.ConfigVerifyPaneNote("BitLockerRecovery"), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryBadgeAndAction_CarriesItsHelp_AndEveryVerifiedSettingHasAFieldTip()
    {
        foreach (var setting in ModuleConfigVerifyAdapter.Settings)
        {
            var item = new VerifyItem(VerifyIgnoreList.ConfigStore, setting.Key, setting.Kind, null, "", "x", "v");
            foreach (var finding in new[]
                     {
                         VerifyFinding.Verified(), VerifyFinding.Missing(), VerifyFinding.Ambiguous("a; b"), VerifyFinding.WrongClass("UserMailbox"),
                         VerifyFinding.Unreadable("busy"),
                         VerifyFinding.Ignored(VerifyFinding.Missing(), new VerifyIgnoreEntry(VerifyIgnoreList.ConfigStore, setting.Key, "x", "Missing", "not found", "a", "t")),
                     })
            {
                var (badge, css, tip) = ModuleConfigPage.ConfigVerifyBadgeParts(new VerifyItemResult(item, finding), "2026-11-05 10:00");
                Assert.False(string.IsNullOrWhiteSpace(badge) || string.IsNullOrWhiteSpace(css), $"{setting.Key} {finding.Kind}");
                Assert.True(tip.Length > 40, $"{setting.Key} {finding.Kind} has no real hover help");
            }
            foreach (var action in new[] { VerifyAction.Replace, VerifyAction.Remove, VerifyAction.Ignore })
                Assert.Contains("audited", ModuleConfigPage.ConfigVerifyActionTip(action, new VerifyItemResult(item, VerifyFinding.Missing())), StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(ModuleConfigPage.ConfigVerifyFieldTip(setting)));
            Assert.False(string.IsNullOrWhiteSpace(ModuleConfigPage.ConfigVerifyPaneNote(setting.ModuleId)));
        }
    }

    // ----- C.3: the field types, the stored values and their parsing unchanged -----

    [Fact]
    public void TheVerifiedSettings_CarryTheirFieldTypes_AndEachKeyBelongsToOneModule()
    {
        var catalog = new ModuleCatalog();
        ConfigFieldType TypeOf(string module, string key) =>
            Assert.Single(catalog.GetById(module)!.ConfigFields, f => f.Key == key).FieldType;

        Assert.Equal(ConfigFieldType.AdGroup, TypeOf("Migration", "ExcludedADGroups"));
        Assert.Equal(',', Assert.Single(catalog.GetById("Migration")!.ConfigFields, f => f.Key == "ExcludedADGroups").ListSeparator);
        Assert.Equal(ConfigFieldType.OU, TypeOf("EmergencyDisable", "LockdownOuDn"));
        Assert.Equal(ConfigFieldType.SearchBase, TypeOf("ADAttributeEditor", "DefaultSearchBase"));
        Assert.Equal(';', Assert.Single(catalog.GetById("ADAttributeEditor")!.ConfigFields, f => f.Key == "DefaultSearchBase").ListSeparator);
        Assert.Equal(ConfigFieldType.SearchBase, TypeOf("BitLockerRecovery", "ActiveDirectorySearchBase"));
        foreach (var setting in ModuleConfigVerifyAdapter.SettingsOf("ConferenceRooms"))
            Assert.Equal(ConfigFieldType.RecipientGroup, TypeOf("ConferenceRooms", setting.Key));
        Assert.Equal(8, ModuleConfigVerifyAdapter.SettingsOf("ConferenceRooms").Count);

        // The ignore's row key is the setting key: no key may belong to two modules, and each is declared by its module.
        Assert.Equal(ModuleConfigVerifyAdapter.Settings.Count, ModuleConfigVerifyAdapter.Settings.Select(s => s.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (var setting in ModuleConfigVerifyAdapter.Settings)
        {
            var field = Assert.Single(catalog.GetById(setting.ModuleId)!.ConfigFields, f => f.Key == setting.Key);
            Assert.Equal(setting.Separator, field.ListSeparator);
            Assert.DoesNotContain(catalog.GetAll().Where(m => m.Id != setting.ModuleId).SelectMany(m => m.ConfigFields), f => f.Key == setting.Key);
        }
    }

    // ----- A6, the real read: the AD module's not-found is a confirmed absence, any other failure throws -----

    private const string StandInOu = """
        [CmdletBinding()] param([string] $Identity, [string] $Server, $Credential)
        $null = $Reads.Add("$Identity @$Server")
        if ($Refused.Contains($Identity)) {
            $PSCmdlet.WriteError([System.Management.Automation.ErrorRecord]::new([System.InvalidOperationException]::new('Unable to contact the server.'), 'ActiveDirectoryServer:0,Microsoft.ActiveDirectory.Management.Commands.GetADOrganizationalUnit', [System.Management.Automation.ErrorCategory]::ResourceUnavailable, $Identity)); return
        }
        if ($Ous.Contains($Identity)) { return [pscustomobject]@{ DistinguishedName = $Identity; ObjectClass = 'organizationalUnit' } }
        $PSCmdlet.WriteError([System.Management.Automation.ErrorRecord]::new([System.InvalidOperationException]::new("Cannot find an object with identity: '$Identity' under: 'DC=contoso,DC=com'."), 'ActiveDirectoryCmdlet:Microsoft.ActiveDirectory.Management.ADIdentityNotFoundException,Microsoft.ActiveDirectory.Management.Commands.GetADOrganizationalUnit', [System.Management.Automation.ErrorCategory]::ObjectNotFound, $Identity))
        """;

    [Fact]
    public void TheLockdownOuRead_TranslatesOnlyTheNotFound_AsAConfirmedAbsence()
    {
        var reads = new ArrayList();
        var state = InitialSessionState.CreateDefault2();
        state.Variables.Add(new SessionStateVariableEntry("Reads", reads, null));
        state.Variables.Add(new SessionStateVariableEntry("Ous", new ArrayList { OuDn }, null));
        state.Variables.Add(new SessionStateVariableEntry("Refused", new ArrayList { GoneDn }, null));
        state.Commands.Add(new SessionStateFunctionEntry("Get-ADOrganizationalUnit", StandInOu));
        using var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;
        var directory = new PowerShellProtectedGroupDirectory(ps, new PSCredential("svc-verify", new SecureString()));

        Assert.True(directory.OrganizationalUnitExists(OuDn, "contoso.com"));
        Assert.False(directory.OrganizationalUnitExists(ContainerDn, "contoso.com")); // a container is not an OU: the AD module's not-found
        var refused = Assert.ThrowsAny<Exception>(() => directory.OrganizationalUnitExists(GoneDn, "contoso.com"));
        Assert.Contains("Unable to contact the server", refused.Message, StringComparison.Ordinal);
        Assert.Equal([$"{OuDn} @contoso.com", $"{ContainerDn} @contoso.com", $"{GoneDn} @contoso.com"], reads.Cast<string>());
    }
}
