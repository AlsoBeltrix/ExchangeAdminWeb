using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using StandInAccess = ExchangeAdminWeb.Tests.DirectoryObjectVerifierTests.StandInAccess;
using StandInVerifyDirectory = ExchangeAdminWeb.Tests.DirectoryObjectVerifierTests.StandInVerifyDirectory;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md A3/A4, B.2 (slice V3): the Module Configuration group adapter over stand-in directories -
/// every finding on the Access tab (access and Protection servicer groups) and the Module Admins tab, each store's own rule for a
/// grant stored by name (a bare name in the host's own domain for section access, forest-wide for module admins, and never the
/// other way round), and that a grant stored by name is never ignorable. Nothing here reaches a directory.
/// </summary>
public sealed class ModuleGroupVerifyEngineTests
{
    private const string Domain = "DC=contoso,DC=com";
    private const string Version = "v1";
    private const string Alias = "BlockedSenders";
    private const string Servicer = "ProtectedServicer:BlockedSenders";
    private const string Module = "BlockedSenders";
    private static string Sid(int rid) => $"S-1-5-21-1000000000-1000000000-1000000000-{rid}";

    private static VerifyDirectoryObject Group(int rid, string sam, string cls = "group") =>
        new($"CN={sam},OU=Groups,{Domain}", Sid(rid), null, cls, null, null, sam);

    private static DirectoryObjectVerifier Engine(IVerifyDirectoryAccess access, ISectionAccessGroupDirectory? sectionAccess = null) =>
        new(access,
        [
            new ModuleGroupVerifyAdapter(VerifyIgnoreList.SectionAccessStore, sectionAccess),
            new ModuleGroupVerifyAdapter(VerifyIgnoreList.ModuleAdminsStore),
        ], NullLogger<DirectoryObjectVerifier>.Instance);

    private static IReadOnlyList<VerifyItem> Items(string store, IEnumerable<GroupGrantRow> rows, Dictionary<string, string>? names = null) =>
        ModuleGroupVerifyAdapter.ItemsFrom(store, rows.ToList(), names ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), Version);

    private static VerifyFinding FindingOf(VerifyReport report, string rowKey, string value) =>
        Assert.Single(report.Results, r => r.Item.RowKey == rowKey && r.Item.StoredValue == value).Finding;

    private static StandInVerifyDirectory Forest() => new()
    {
        Objects =
        {
            Group(10, "g10"),
            Group(11, "Renamed"),
            Group(13, "alice", cls: "user"),
            Group(14, "TwinA"),
            Group(14, "TwinB"),
        },
    };

    // ----- A3: every finding on the Access tab, the servicer's rows included -----

    [Fact]
    public async Task AccessTab_EveryFinding_IsTheDirectorysAnswer_AndANameIsResolvedByTheSidMigrationsOwnRule()
    {
        var sectionAccess = new StandInSectionAccessGroups();
        sectionAccess.Groups["Ops"] = [new DirectoryGroupMatch(Sid(20), @"CONTOSO\Ops")];
        sectionAccess.Groups["Twins"] = [new DirectoryGroupMatch(Sid(21), @"CONTOSO\Twins"), new DirectoryGroupMatch(Sid(22), @"FABRIKAM\Twins")];
        sectionAccess.Unavailable.Add("Broken");
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Sid(10)] = @"CONTOSO\g10",
            [Sid(11)] = @"CONTOSO\Old Name",
        };
        var rows = new[]
        {
            new GroupGrantRow(Alias, Sid(10)), new GroupGrantRow(Alias, Sid(11)), new GroupGrantRow(Alias, Sid(12)),
            new GroupGrantRow(Alias, Sid(13)), new GroupGrantRow(Alias, Sid(14)), new GroupGrantRow(Alias, "S-1-1-0"),
            new GroupGrantRow(Alias, "Ops"), new GroupGrantRow(Alias, "Nobody"), new GroupGrantRow(Alias, "Twins"),
            new GroupGrantRow(Alias, "Broken"), new GroupGrantRow(Servicer, Sid(10)), new GroupGrantRow(Servicer, Sid(12)),
        };

        var report = await Engine(new StandInAccess(Forest()), sectionAccess)
            .VerifyAsync(Items(VerifyIgnoreList.SectionAccessStore, rows, names), CancellationToken.None);

        Assert.Equal(VerifyFindingKind.Verified, FindingOf(report, Alias, Sid(10)).Kind);
        var drift = FindingOf(report, Alias, Sid(11));
        Assert.Equal((VerifyFindingKind.LabelDrift, @"CONTOSO\Renamed"), (drift.Kind, drift.Detail));
        Assert.Equal(VerifyFindingKind.Missing, FindingOf(report, Alias, Sid(12)).Kind);
        Assert.Equal((VerifyFindingKind.WrongClass, "user"), (FindingOf(report, Alias, Sid(13)).Kind, FindingOf(report, Alias, Sid(13)).Detail));
        Assert.Equal(VerifyFindingKind.Ambiguous, FindingOf(report, Alias, Sid(14)).Kind);
        var wellKnown = FindingOf(report, Alias, "S-1-1-0");
        Assert.Equal(VerifyFindingKind.WrongClass, wellKnown.Kind);
        Assert.StartsWith("well-known or unusable SID", wellKnown.Detail);

        var ops = FindingOf(report, Alias, "Ops");
        Assert.Equal((VerifyFindingKind.NotConverted, VerifyResolutionKind.Convertible, Sid(20), @"CONTOSO\Ops"),
            (ops.Kind, ops.Resolution!.Kind, ops.Resolution.Identifier, ops.Resolution.Label));
        Assert.Equal(VerifyResolutionKind.None, FindingOf(report, Alias, "Nobody").Resolution!.Kind);
        Assert.Equal(VerifyResolutionKind.Several, FindingOf(report, Alias, "Twins").Resolution!.Kind);
        Assert.Equal(VerifyResolutionKind.Unreadable, FindingOf(report, Alias, "Broken").Resolution!.Kind);

        // The Protection servicer's rows are the same store's, under its own key.
        Assert.Equal(VerifyFindingKind.Verified, FindingOf(report, Servicer, Sid(10)).Kind);
        Assert.Equal(VerifyFindingKind.Missing, FindingOf(report, Servicer, Sid(12)).Kind);
    }

    // ----- A4: every finding on the Module Admins tab -----

    [Fact]
    public async Task ModuleAdminsTab_EveryFinding_IsTheDirectorysAnswer_AndANameIsResolvedByTheConversionsOwnRule()
    {
        var forest = Forest();
        forest.Objects.Add(Group(30, "Ops"));
        forest.Objects.Add(Group(31, "Twin") with { DistinguishedName = $"CN=Twin,OU=A,{Domain}" });
        forest.Objects.Add(Group(32, "Twin") with { DistinguishedName = $"CN=Twin,OU=B,{Domain}" });
        forest.ThrowsFor.Add("Broken");
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [Sid(10)] = "g10", [Sid(11)] = "Old Name" };
        var rows = new[] { Sid(10), Sid(11), Sid(12), Sid(13), Sid(14), "S-1-5-32-544", "Ops", @"CONTOSO\Ops", "Gone", "Twin", "Broken" }
            .Select(v => new GroupGrantRow(Module, v));

        var report = await Engine(new StandInAccess(forest)).VerifyAsync(Items(VerifyIgnoreList.ModuleAdminsStore, rows, names), CancellationToken.None);

        Assert.Equal(VerifyFindingKind.Verified, FindingOf(report, Module, Sid(10)).Kind);
        var drift = FindingOf(report, Module, Sid(11));
        Assert.Equal((VerifyFindingKind.LabelDrift, "Renamed"), (drift.Kind, drift.Detail));
        Assert.Equal(VerifyFindingKind.Missing, FindingOf(report, Module, Sid(12)).Kind);
        Assert.Equal(VerifyFindingKind.WrongClass, FindingOf(report, Module, Sid(13)).Kind);
        Assert.Equal(VerifyFindingKind.Ambiguous, FindingOf(report, Module, Sid(14)).Kind);
        Assert.Equal(VerifyFindingKind.WrongClass, FindingOf(report, Module, "S-1-5-32-544").Kind);

        var bare = FindingOf(report, Module, "Ops");
        Assert.Equal((VerifyResolutionKind.Convertible, Sid(30)), (bare.Resolution!.Kind, bare.Resolution.Identifier));
        Assert.Equal(VerifyResolutionKind.Convertible, FindingOf(report, Module, @"CONTOSO\Ops").Resolution!.Kind);
        Assert.Equal(VerifyResolutionKind.None, FindingOf(report, Module, "Gone").Resolution!.Kind);
        Assert.Equal(VerifyResolutionKind.Several, FindingOf(report, Module, "Twin").Resolution!.Kind);
        Assert.Equal(VerifyResolutionKind.Unreadable, FindingOf(report, Module, "Broken").Resolution!.Kind);
    }

    [Fact]
    public async Task ASessionThatCannotBeHad_LeavesEveryGrantUnreadable_NeverMissing()
    {
        var rows = new[] { new GroupGrantRow(Module, Sid(10)), new GroupGrantRow(Module, Sid(12)), new GroupGrantRow(Module, "Ops") };

        var report = await Engine(new StandInAccess(Forest(), unavailable: "no credential"))
            .VerifyAsync(Items(VerifyIgnoreList.ModuleAdminsStore, rows), CancellationToken.None);

        Assert.All(report.Results, r => Assert.Equal(VerifyFindingKind.Unreadable, r.Finding.Kind));
    }

    // ----- B.2: a bare name - the host's domain for section access, forest-wide for module admins -----

    [Fact]
    public async Task ABareName_ResolvesInTheHostsDomain_ForSectionAccess_AndForestWide_ForModuleAdmins_NeverTheOtherWayRound()
    {
        // "Ops" exists elsewhere in the forest (the global catalog holds it) but not in this server's own domain.
        var forest = new RecordingDirectory(Forest());
        forest.Inner.Objects.Add(Group(30, "Ops"));
        var sectionAccess = new StandInSectionAccessGroups();
        sectionAccess.Groups[@"CONTOSO\Ops"] = [new DirectoryGroupMatch(Sid(30), @"CONTOSO\Ops")];
        var engine = Engine(new StandInAccess(forest), sectionAccess);

        var access = await engine.VerifyAsync(
            Items(VerifyIgnoreList.SectionAccessStore, [new GroupGrantRow(Alias, "Ops"), new GroupGrantRow(Alias, @"CONTOSO\Ops")]), CancellationToken.None);

        // Section access: the bare name is asked of the migration's directory with no domain - its own domain - and not found;
        // the qualified one in the domain it names. The forest-wide catalog is never asked.
        Assert.Equal([("Ops", (string?)null), ("Ops", "CONTOSO")], sectionAccess.Asked);
        Assert.Equal(VerifyResolutionKind.None, FindingOf(access, Alias, "Ops").Resolution!.Kind);
        Assert.Equal(VerifyResolutionKind.Convertible, FindingOf(access, Alias, @"CONTOSO\Ops").Resolution!.Kind);
        Assert.Empty(forest.GroupLookups);

        var admins = await engine.VerifyAsync(Items(VerifyIgnoreList.ModuleAdminsStore, [new GroupGrantRow(Module, "Ops")]), CancellationToken.None);

        // Module admins: the bare name through the global catalog (no server), found; the section-access directory not asked again.
        Assert.Equal([(GroupLookupKey.AnyGroupName, "Ops", (string?)null)], forest.GroupLookups);
        Assert.Equal((VerifyResolutionKind.Convertible, Sid(30)),
            (FindingOf(admins, Module, "Ops").Resolution!.Kind, FindingOf(admins, Module, "Ops").Resolution!.Identifier));
        Assert.Equal(2, sectionAccess.Asked.Count);
    }

    // ----- B.4: a grant stored by name is never ignorable; a SID grant's ignore is display-only -----

    [Theory]
    [InlineData(VerifyIgnoreList.SectionAccessStore, Alias)]
    [InlineData(VerifyIgnoreList.ModuleAdminsStore, Module)]
    public async Task AGrantStoredByName_IsNeverIgnored_NorOffersIgnore_WhileASidGrantsIgnoreApplies(string store, string rowKey)
    {
        var sectionAccess = new StandInSectionAccessGroups();
        var forest = Forest();
        var ignores = new[]
        {
            new VerifyIgnoreEntry(store, rowKey, "Nobody", "NotConverted", "no group with that name exists in this domain", "x", "t"),
            new VerifyIgnoreEntry(store, rowKey, "Nobody", "NotConverted", "not found", "x", "t"),
            new VerifyIgnoreEntry(store, rowKey, Sid(12), "Missing", VerifyIgnoreList.NotFoundObserved, "x", "t"),
        };
        var items = Items(store, [new GroupGrantRow(rowKey, "Nobody"), new GroupGrantRow(rowKey, Sid(12))]);

        var report = await Engine(new StandInAccess(forest), sectionAccess).VerifyAsync(items, ignores, CancellationToken.None);

        var adapter = new ModuleGroupVerifyAdapter(store);
        var byName = Assert.Single(report.Results, r => r.Item.StoredValue == "Nobody");
        Assert.Equal(VerifyFindingKind.NotConverted, byName.Finding.Kind);
        Assert.DoesNotContain(VerifyAction.Ignore, adapter.ActionsFor(byName.Item, byName.Finding));
        foreach (var kind in Enum.GetValues<VerifyResolutionKind>())
            Assert.DoesNotContain(VerifyAction.Ignore, adapter.ActionsFor(byName.Item, VerifyFinding.NotConverted(new VerifyResolution(kind, "x"))));
        Assert.Equal(VerifyFindingKind.Ignored, Assert.Single(report.Results, r => r.Item.StoredValue == Sid(12)).Finding.Kind);
        // A value that is not a usable group SID: Remove only.
        var wellKnown = Items(store, [new GroupGrantRow(rowKey, "S-1-1-0")])[0];
        Assert.Equal([VerifyAction.Remove], adapter.ActionsFor(wellKnown, VerifyFinding.WrongClass("well-known or unusable SID")));
        Assert.False(adapter.CanIgnore(wellKnown, VerifyFinding.WrongClass("well-known or unusable SID")));
    }

    // ----- stand-ins -----

    /// <summary>The section-access SID migration's directory: answers by the stored name and records each (name, domain) asked.</summary>
    internal sealed class StandInSectionAccessGroups : ISectionAccessGroupDirectory
    {
        public Dictionary<string, List<DirectoryGroupMatch>> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Unavailable { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(string Name, string? Domain)> Asked { get; } = [];

        public IReadOnlyList<DirectoryGroupMatch> FindGroupsByName(string name, string? netBiosDomain)
        {
            Asked.Add((name, netBiosDomain));
            if (Unavailable.Contains(name))
                throw new DirectoryUnavailableException($"lookup of '{name}' failed");
            var key = netBiosDomain is null ? name : $"{netBiosDomain}\\{name}";
            return Groups.TryGetValue(key, out var found) ? found : [];
        }
    }

    // The stand-in forest, recording every group lookup with the server it went to (null: the global catalog, forest-wide).
    private sealed class RecordingDirectory(StandInVerifyDirectory inner) : IVerifyDirectory
    {
        public StandInVerifyDirectory Inner { get; } = inner;
        public List<(GroupLookupKey Key, string Value, string? Server)> GroupLookups { get; } = [];

        public string? ForestDomainServer(string dnsDomain) => Inner.ForestDomainServer(dnsDomain);
        public IReadOnlyList<VerifyDirectoryObject> FindBySids(IReadOnlyList<string> sids) => Inner.FindBySids(sids);
        public IReadOnlyList<VerifyDirectoryObject> FindByGuids(IReadOnlyList<string> guids) => Inner.FindByGuids(guids);
        public IReadOnlyList<VerifyDirectoryObject> ReadByDns(IReadOnlyList<string> dns, string server) => Inner.ReadByDns(dns, server);
        public string ResolveDomainServer(string domain) => Inner.ResolveDomainServer(domain);

        public IReadOnlyList<DirectoryGroup> FindGroups(GroupLookupKey key, string value, string? server)
        {
            GroupLookups.Add((key, value, server));
            return Inner.FindGroups(key, value, server);
        }

        public IReadOnlyList<DirectoryObjectGuid> ReadObjectGuids(IReadOnlyList<string> dns, string server) => Inner.ReadObjectGuids(dns, server);
        public IReadOnlyList<DirectoryUserObject> FindUsers(string ldapFilter, string? server) => Inner.FindUsers(ldapFilter, server);
    }
}
