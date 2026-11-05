using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md B.1, B.2 (slice V1): the Verify engine and the protected-principal adapter over a
/// stand-in directory - every finding, the batching, unreadable never Verified or Missing, the forest check, and how ignores
/// apply. Nothing here reaches a directory.
/// </summary>
public sealed class DirectoryObjectVerifierTests
{
    private const string Domain = "DC=contoso,DC=com";
    private const string Version = "v1";
    private static string Sid(int rid) => $"S-1-5-21-1000000000-1000000000-1000000000-{rid}";
    private static string Guid(int n) => $"00000000-0000-0000-0000-{n:D12}";
    private static string Dn(string cn, string container = "OU=Groups") => $"CN={cn},{container},{Domain}";

    private static VerifyDirectoryObject User(int rid, string? upn, string? mail = null, string cls = "user") =>
        new($"CN=u{rid},OU=Staff,{Domain}", Sid(rid), Guid(rid), cls, upn, mail, $"u{rid}");

    private static VerifyDirectoryObject Group(int rid, string dn, string cls = "group") => new(dn, Sid(rid), Guid(rid), cls, null, null, $"g{rid}");

    private static VerifyDirectoryObject Ou(int n, string dn) => new(dn, null, Guid(n), "organizationalUnit", null, null, null);

    private static DirectoryObjectVerifier Engine(IVerifyDirectoryAccess access) =>
        new(access, [new ProtectedPrincipalVerifyAdapter()], NullLogger<DirectoryObjectVerifier>.Instance);

    private static Task<VerifyReport> Verify(StandInVerifyDirectory directory, ProtectedPrincipalData rules, params VerifyIgnoreEntry[] ignores) =>
        Engine(new StandInAccess(directory)).VerifyAsync(ProtectedPrincipalVerifyAdapter.ItemsFrom(rules, Version), ignores, CancellationToken.None);

    private static VerifyFinding FindingOf(VerifyReport report, string kind, string storedValue) =>
        Assert.Single(report.Results, r => r.Item.RowKey == kind && r.Item.StoredValue == storedValue).Finding;

    private static string Row(string id, string label) => ProtectedPrincipalIdEntry.Format(id, label);

    // ----- B.2: every finding of an identifier row -----

    [Fact]
    public async Task EveryFindingOfAnIdentifierRow_IsTheDirectorysAnswer_ComparedByClassAndCanonicalName()
    {
        var directory = new StandInVerifyDirectory();
        directory.Objects.AddRange(
        [
            User(1, "alice@contoso.com"),
            User(2, "robert@contoso.com"),
            User(3, upn: null, mail: null),
            User(4, upn: null, mail: "dee.new@contoso.com"),
            User(6, "pc01$@contoso.com", cls: "computer"),
            Group(10, Dn("Tier0")),
            User(11, "notagroup@contoso.com"),
            Group(12, Dn("Twin A")),
            Group(12, Dn("Twin B")),
            Ou(20, "OU=Tier0 Renamed," + Domain),
            Group(30, Dn("Target")) with { ObjectGuid = Guid(30) },
        ]);

        var rules = ProtectedPrincipalData.Empty with
        {
            UserIds =
            [
                Row(Sid(1), "alice@contoso.com"),
                Row(Sid(2), "bob@contoso.com"),
                Row(Sid(3), "svc-old"),
                Row(Sid(4), "dee@contoso.com"),
                Row(Sid(5), "gone@contoso.com"),
                Row(Sid(6), "pc01@contoso.com"),
                "not-a-sid|someone@contoso.com",
            ],
            GroupIds = [Row(Sid(10), Dn("Tier0")), Row(Sid(11), Dn("Was A Group")), Row(Sid(12), Dn("Twin A"))],
            OuIds = [Row(Guid(20), "OU=Tier0," + Domain)],
            GroupTargets = [ProtectedGroupTargetEntry.Format(Guid(30), Dn("Target"))],
        };

        var report = await Verify(directory, rules);

        Assert.Equal(VerifyFindingKind.Verified, FindingOf(report, "user_sid", Row(Sid(1), "alice@contoso.com")).Kind);
        var drift = FindingOf(report, "user_sid", Row(Sid(2), "bob@contoso.com"));
        Assert.Equal((VerifyFindingKind.LabelDrift, "robert@contoso.com"), (drift.Kind, drift.Detail));
        // A user with neither a UPN nor mail is never "name changed": its stored label stands (B.2).
        Assert.Equal(VerifyFindingKind.Verified, FindingOf(report, "user_sid", Row(Sid(3), "svc-old")).Kind);
        // Mail when there is no UPN, as the validator stores it.
        Assert.Equal("dee.new@contoso.com", FindingOf(report, "user_sid", Row(Sid(4), "dee@contoso.com")).Detail);
        Assert.Equal(VerifyFindingKind.Missing, FindingOf(report, "user_sid", Row(Sid(5), "gone@contoso.com")).Kind);
        Assert.Equal((VerifyFindingKind.WrongClass, "computer"),
            (FindingOf(report, "user_sid", Row(Sid(6), "pc01@contoso.com")).Kind, FindingOf(report, "user_sid", Row(Sid(6), "pc01@contoso.com")).Detail));
        Assert.Equal(VerifyFindingKind.Unreadable, FindingOf(report, "user_sid", "not-a-sid|someone@contoso.com").Kind);

        Assert.Equal(VerifyFindingKind.Verified, FindingOf(report, "group_sid", Row(Sid(10), Dn("Tier0"))).Kind);
        Assert.Equal(VerifyFindingKind.WrongClass, FindingOf(report, "group_sid", Row(Sid(11), Dn("Was A Group"))).Kind);
        var twins = FindingOf(report, "group_sid", Row(Sid(12), Dn("Twin A")));
        Assert.Equal(VerifyFindingKind.Ambiguous, twins.Kind);
        Assert.Contains(Dn("Twin B"), twins.Detail);

        var ou = FindingOf(report, "ou_guid", Row(Guid(20), "OU=Tier0," + Domain));
        Assert.Equal((VerifyFindingKind.LabelDrift, "OU=Tier0 Renamed," + Domain), (ou.Kind, ou.Detail));
        Assert.Equal(VerifyFindingKind.Verified, FindingOf(report, "group_target", ProtectedGroupTargetEntry.Format(Guid(30), Dn("Target"))).Kind);

        Assert.Equal(12, report.Total);
        Assert.Equal(4, report.Verified);
        Assert.Equal(1, report.Unreadable);
        Assert.Equal(7, report.Findings);
    }

    [Fact]
    public async Task ARowStoredByName_IsNotConverted_CarryingTheResolversAnswer_AndIsNeverIgnored()
    {
        var directory = new StandInVerifyDirectory();
        directory.Users["(userPrincipalName=alice@contoso.com)"] = [new($"CN=Alice,OU=Staff,{Domain}", Sid(1))];
        directory.Users["(sAMAccountName=ops)"] = [new($"CN=Ops1,OU=Staff,{Domain}", Sid(2)), new($"CN=Ops2,OU=Staff,{Domain}", Sid(3))];
        directory.ThrowsFor.Add(Dn("Broken"));
        var rules = ProtectedPrincipalData.Empty with
        {
            Users = ["alice@contoso.com", "ops"],
            Groups = [Dn("Gone"), Dn("Broken")],
        };
        // A hand-edited list cannot make a row stored by name ignorable (decision 2026-11-03).
        var ignore = new VerifyIgnoreEntry(VerifyIgnoreList.ProtectedPrincipalsStore, "group", Dn("Gone"), "NotConverted", "not found", "x", "t");

        var report = await Verify(directory, rules, ignore);

        var alice = FindingOf(report, "user", "alice@contoso.com");
        Assert.Equal(VerifyFindingKind.NotConverted, alice.Kind);
        Assert.Equal((VerifyResolutionKind.Convertible, "user_sid", Sid(1)), (alice.Resolution!.Kind, alice.Resolution.IdKind, alice.Resolution.Identifier));
        Assert.Equal(VerifyResolutionKind.Several, FindingOf(report, "user", "ops").Resolution!.Kind);
        var gone = FindingOf(report, "group", Dn("Gone"));
        Assert.Equal((VerifyFindingKind.NotConverted, VerifyResolutionKind.None), (gone.Kind, gone.Resolution!.Kind));
        Assert.Equal(VerifyResolutionKind.Unreadable, FindingOf(report, "group", Dn("Broken")).Resolution!.Kind);
        Assert.Equal(0, report.Ignored);
    }

    // ----- B.1: batching -----

    [Fact]
    public async Task IdentifierLookups_AreBatchedInFifties_AtTheCatalog_InOneSession()
    {
        var directory = new StandInVerifyDirectory();
        var userIds = new List<string>();
        for (var rid = 1000; rid < 1120; rid++)
        {
            directory.Objects.Add(User(rid, $"u{rid}@contoso.com"));
            userIds.Add(Row(Sid(rid), $"u{rid}@contoso.com"));
        }
        directory.Objects.AddRange([Ou(1, "OU=A," + Domain), Ou(2, "OU=B," + Domain), Ou(3, "OU=C," + Domain)]);
        var access = new StandInAccess(directory);
        var rules = ProtectedPrincipalData.Empty with
        {
            UserIds = [.. userIds],
            OuIds = [Row(Guid(1), "OU=A," + Domain), Row(Guid(2), "OU=B," + Domain), Row(Guid(3), "OU=C," + Domain)],
        };

        var report = await Engine(access).VerifyAsync(ProtectedPrincipalVerifyAdapter.ItemsFrom(rules, Version), CancellationToken.None);

        Assert.Equal([50, 50, 20], directory.SidBatches);
        Assert.Equal([3], directory.GuidBatches);
        Assert.Equal(1, access.Sessions);
        Assert.Equal(123, report.Verified);
        // The forest is asked about each stored DN's domain once, not once per row.
        Assert.Equal(1, directory.Calls.Count(c => c == "forest contoso.com"));
    }

    // ----- B.1 (R4): unreadable is never Verified or Missing -----

    [Fact]
    public async Task ABatchWhoseReadFails_IsUnreadable_ForEveryRowInIt_NeverVerifiedOrMissing()
    {
        var directory = new StandInVerifyDirectory { FailSidBatch = 1 };
        var rows = new List<string>();
        for (var rid = 2000; rid < 2120; rid++)
        {
            // Every other one exists: in the failed batch a "missing" verdict would be a lie either way.
            if (rid % 2 == 0)
                directory.Objects.Add(User(rid, $"u{rid}@contoso.com"));
            rows.Add(Row(Sid(rid), $"u{rid}@contoso.com"));
        }

        var report = await Verify(directory, ProtectedPrincipalData.Empty with { UserIds = [.. rows] });

        var failed = report.Results.Skip(50).Take(50).ToList();
        Assert.All(failed, r => Assert.Equal(VerifyFindingKind.Unreadable, r.Finding.Kind));
        Assert.All(failed, r => Assert.Contains("not operational", r.Finding.Detail));
        Assert.All(report.Results.Take(50).Concat(report.Results.Skip(100)),
            r => Assert.Contains(r.Finding.Kind, new[] { VerifyFindingKind.Verified, VerifyFindingKind.Missing }));
        Assert.Equal(50, report.Unreadable);
    }

    [Fact]
    public async Task ASessionThatCannotBeHad_LeavesEveryEntryUnreadable_WithTheReason()
    {
        var rules = ProtectedPrincipalData.Empty with
        {
            UserIds = [Row(Sid(1), "a@contoso.com")],
            Groups = [Dn("Legacy")],
            OuIds = [Row(Guid(1), "OU=A," + Domain)],
        };

        var report = await Engine(new StandInAccess(new StandInVerifyDirectory(), unavailable: "the credential could not be retrieved"))
            .VerifyAsync(ProtectedPrincipalVerifyAdapter.ItemsFrom(rules, Version), CancellationToken.None);

        Assert.All(report.Results, r => Assert.Equal((VerifyFindingKind.Unreadable, "the credential could not be retrieved"), (r.Finding.Kind, r.Finding.Detail)));
    }

    [Fact]
    public async Task TheProtectionServicesOwnSession_WithNoDirectoryReadCredential_VerifiesNothing_AndCallsNothingMissing()
    {
        using var h = new SettingsPageHarness();
        var rules = ProtectedPrincipalData.Empty with { GroupIds = [Row(Sid(10), Dn("Tier0"))], UserIds = [Row(Sid(1), "a@contoso.com")] };

        var report = await Engine(h.ProtectedPrincipals).VerifyAsync(ProtectedPrincipalVerifyAdapter.ItemsFrom(rules, Version), CancellationToken.None);

        Assert.All(report.Results, r => Assert.Equal(VerifyFindingKind.Unreadable, r.Finding.Kind));
        Assert.All(report.Results, r => Assert.Contains("not configured", r.Finding.Detail));
    }

    [Fact]
    public async Task AForestThatCannotBeAskedAboutADomain_MakesItsRowsUnreadable()
    {
        var directory = new StandInVerifyDirectory { ForestFailure = new InvalidOperationException("Could not read the forest's configuration naming context.") };
        // The object is NOT at the catalog: without the forest check this row would read as missing.
        var rules = ProtectedPrincipalData.Empty with { GroupIds = [Row(Sid(10), Dn("Tier0"))] };

        var report = await Verify(directory, rules);

        var finding = Assert.Single(report.Results).Finding;
        Assert.Equal(VerifyFindingKind.Unreadable, finding.Kind);
        Assert.Contains("configuration naming context", finding.Detail);
    }

    // ----- B.1 (F6): forest scope -----

    [Fact]
    public async Task ADnOfADomainOutsideTheForest_IsUnreadable_NeverMissing_AndItsRowStoredByNameIsNotLookedUp()
    {
        var directory = new StandInVerifyDirectory();
        const string foreign = "CN=Partners,OU=Groups,DC=fabrikam,DC=net";
        var rules = ProtectedPrincipalData.Empty with
        {
            GroupIds = [Row(Sid(40), foreign)],
            OuIds = [Row(Guid(41), "OU=Remote,DC=fabrikam,DC=net")],
            Groups = ["CN=Elsewhere,DC=fabrikam,DC=net"],
        };

        var report = await Verify(directory, rules);

        Assert.Equal((VerifyFindingKind.Unreadable, DirectoryObjectVerifier.NotInForest),
            (FindingOf(report, "group_sid", Row(Sid(40), foreign)).Kind, FindingOf(report, "group_sid", Row(Sid(40), foreign)).Detail));
        Assert.Equal(VerifyFindingKind.Unreadable, FindingOf(report, "ou_guid", Row(Guid(41), "OU=Remote,DC=fabrikam,DC=net")).Kind);
        var legacy = FindingOf(report, "group", "CN=Elsewhere,DC=fabrikam,DC=net");
        Assert.Equal((VerifyFindingKind.NotConverted, VerifyResolutionKind.Unreadable, DirectoryObjectVerifier.NotInForest),
            (legacy.Kind, legacy.Resolution!.Kind, legacy.Resolution.Detail));
        Assert.DoesNotContain(directory.Calls, c => c.StartsWith("FindGroups", StringComparison.Ordinal));
        Assert.Equal(0, report.Results.Count(r => r.Finding.Kind == VerifyFindingKind.Missing));
    }

    // ----- B.3: ignores -----

    [Fact]
    public async Task AnIgnore_TurnsOnlyTheFindingItMatches_Ignored_AndNeverAnUnreadableOne()
    {
        var directory = new StandInVerifyDirectory();
        directory.Objects.Add(Group(51, Dn("Renamed Again")));
        directory.FailGuids = true;
        var store = VerifyIgnoreList.ProtectedPrincipalsStore;
        var missing = new VerifyIgnoreEntry(store, "group_sid", Sid(50), "Missing", VerifyIgnoreList.NotFoundObserved, @"EXAMPLE\admin", "2026-11-04T00:00:00Z");
        var oldDrift = new VerifyIgnoreEntry(store, "group_sid", Sid(51), "LabelDrift", Dn("Renamed Once"), @"EXAMPLE\admin", "2026-11-04T00:00:00Z");
        var onUnreadable = new VerifyIgnoreEntry(store, "ou_guid", Guid(52), "Missing", VerifyIgnoreList.NotFoundObserved, @"EXAMPLE\admin", "2026-11-04T00:00:00Z");
        var rules = ProtectedPrincipalData.Empty with
        {
            GroupIds = [Row(Sid(50), Dn("Deleted")), Row(Sid(51), Dn("Original"))],
            OuIds = [Row(Guid(52), "OU=X," + Domain)],
        };

        var report = await Verify(directory, rules, missing, oldDrift, onUnreadable);

        var ignored = FindingOf(report, "group_sid", Row(Sid(50), Dn("Deleted")));
        Assert.Equal(VerifyFindingKind.Ignored, ignored.Kind);
        Assert.Equal(VerifyFindingKind.Missing, ignored.Underlying!.Kind);
        Assert.Same(missing, ignored.IgnoredBy);
        // Ignored under the name it had then; it has been renamed again, so it is reported again (B.3).
        Assert.Equal(VerifyFindingKind.LabelDrift, FindingOf(report, "group_sid", Row(Sid(51), Dn("Original"))).Kind);
        Assert.Equal(VerifyFindingKind.Unreadable, FindingOf(report, "ou_guid", Row(Guid(52), "OU=X," + Domain)).Kind);
        Assert.Equal([oldDrift, onUnreadable], report.StaleIgnores);
    }

    // ----- the filters -----

    [Fact]
    public void TheBatchedFilters_AreEqualityTermsOnTheIdentifier_TheGuidAsItsStoredBytes()
    {
        var guid = "0b5e3c2a-7d41-4f8e-9c6a-2e1f3d4b5a69";

        Assert.Equal(@"(|(objectGUID=\2a\3c\5e\0b\41\7d\8e\4f\9c\6a\2e\1f\3d\4b\5a\69))", VerifyFilters.AnyGuid([guid]));
        Assert.Equal($"(|(objectSid={Sid(1)})(objectSid={Sid(2)}))", VerifyFilters.AnySid([Sid(1), Sid(2)]));
    }

    // ----- stand-ins -----

    internal sealed class StandInAccess(IVerifyDirectory directory, string? unavailable = null) : IVerifyDirectoryAccess
    {
        public int Sessions { get; private set; }

        public Task<VerifyDirectoryRun<T>> RunAsync<T>(Func<IVerifyDirectory, T> reads, CancellationToken cancellationToken)
        {
            if (unavailable is not null)
                return Task.FromResult(VerifyDirectoryRun<T>.NotRun(unavailable));
            Sessions++;
            return Task.FromResult(VerifyDirectoryRun<T>.Ran(reads(directory)));
        }
    }

    /// <summary>A forest of one domain, contoso.com, answering from <see cref="Objects"/> and the resolver's tables.</summary>
    internal sealed class StandInVerifyDirectory : IVerifyDirectory
    {
        public List<VerifyDirectoryObject> Objects { get; } = [];
        public HashSet<string> ForestDomains { get; } = new(StringComparer.OrdinalIgnoreCase) { "contoso.com" };
        public Dictionary<string, List<DirectoryUserObject>> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ThrowsFor { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<int> SidBatches { get; } = [];
        public List<int> GuidBatches { get; } = [];
        public List<string> Calls { get; } = [];
        public int FailSidBatch { get; set; } = -1;
        public bool FailGuids { get; set; }
        public Exception? ForestFailure { get; set; }

        public string? ForestDomainServer(string dnsDomain)
        {
            Calls.Add("forest " + dnsDomain);
            if (ForestFailure is not null)
                throw ForestFailure;
            return ForestDomains.Contains(dnsDomain) ? dnsDomain : null;
        }

        public IReadOnlyList<VerifyDirectoryObject> FindBySids(IReadOnlyList<string> sids)
        {
            SidBatches.Add(sids.Count);
            if (FailSidBatch == SidBatches.Count - 1)
                throw new InvalidOperationException("The server is not operational.");
            return Objects.Where(o => sids.Contains(o.ObjectSid, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        public IReadOnlyList<VerifyDirectoryObject> FindByGuids(IReadOnlyList<string> guids)
        {
            GuidBatches.Add(guids.Count);
            if (FailGuids)
                throw new InvalidOperationException("The server is not operational.");
            return Objects.Where(o => guids.Contains(o.ObjectGuid, StringComparer.OrdinalIgnoreCase)).ToList();
        }

        public IReadOnlyList<VerifyDirectoryObject> ReadByDns(IReadOnlyList<string> dns, string server) =>
            Objects.Where(o => dns.Contains(o.DistinguishedName, StringComparer.OrdinalIgnoreCase)).ToList();

        public string ResolveDomainServer(string domain) =>
            string.Equals(domain, "CONTOSO", StringComparison.OrdinalIgnoreCase) ? "contoso.com" : throw new InvalidOperationException($"No domain '{domain}'.");

        public IReadOnlyList<DirectoryGroup> FindGroups(GroupLookupKey key, string value, string? server)
        {
            Calls.Add($"FindGroups {key} {value}");
            if (ThrowsFor.Contains(value))
                throw new InvalidOperationException("The server is not operational.");
            return Objects.Where(o => o.ObjectClass == "group" && key switch
            {
                GroupLookupKey.DistinguishedName => string.Equals(o.DistinguishedName, value, StringComparison.OrdinalIgnoreCase),
                GroupLookupKey.Sid => string.Equals(o.ObjectSid, value, StringComparison.OrdinalIgnoreCase),
                _ => string.Equals(o.SamAccountName, value, StringComparison.OrdinalIgnoreCase),
            })
                .Select(o => new DirectoryGroup(o.DistinguishedName, o.ObjectSid!, DirectoryGroupScope.Global))
                .ToList();
        }

        public IReadOnlyList<DirectoryObjectGuid> ReadObjectGuids(IReadOnlyList<string> dns, string server) =>
            Objects.Where(o => dns.Contains(o.DistinguishedName, StringComparer.OrdinalIgnoreCase))
                .Select(o => new DirectoryObjectGuid(o.DistinguishedName, o.ObjectGuid)).ToList();

        public IReadOnlyList<DirectoryUserObject> FindUsers(string ldapFilter, string? server)
        {
            Calls.Add("FindUsers " + ldapFilter);
            foreach (var (term, users) in Users)
            {
                if (ldapFilter.Contains(term, StringComparison.OrdinalIgnoreCase))
                    return users;
            }
            return [];
        }
    }
}
