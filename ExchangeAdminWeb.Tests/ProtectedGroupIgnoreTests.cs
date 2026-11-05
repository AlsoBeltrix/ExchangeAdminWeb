using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md B.4 (slice V2), at the evaluator: an entry the caller names as ignored when absent is
/// still looked up on every evaluation, and only the directory's definite "no such group" is skipped. Everything else - a group
/// the lookup finds on any path, a read that fails, several matches - is answered exactly as without the ignore.
/// </summary>
public class ProtectedGroupIgnoreEvaluatorTests
{
    private const string DomainA = "a.example";
    private const string DomainB = "b.example";
    private const string SidA = "S-1-5-21-100-200-300";
    private const string SidB = "S-1-5-21-400-500-600";

    private const string Tess = "CN=Tess,OU=People,DC=a,DC=example";
    private const string Uma = "CN=Uma,OU=People,DC=a,DC=example";
    private const string DomainUsersA = "CN=Domain Users,CN=Users,DC=a,DC=example";
    private const string StaffA = "CN=Staff,OU=Groups,DC=a,DC=example";
    private const string ProtectedVipsB = "CN=Protected VIPs,OU=Groups,DC=b,DC=example";
    private const string ExecList = "CN=Exec List,OU=Groups,DC=a,DC=example";

    private const string StaffSid = SidA + "-2001";
    private const string VipsSid = SidB + "-3001";
    private const string ExecListSid = SidA + "-2040";
    private const string GoneSid = SidA + "-9999";

    /// <summary>
    /// Tess is in A\Staff (global: her token carries it), which is in B's domain-local Protected VIPs (another domain's
    /// domain-local group: no token carries it), and in A's universal distribution list Exec List (no token carries it).
    /// </summary>
    private static FakeForest Forest()
    {
        var f = new FakeForest().Domain("A", DomainA).Domain("B", DomainB);
        f.Account(Tess, $"{SidA}-1105", primaryGroupRid: 513);
        f.Account(Uma, $"{SidA}-1106", primaryGroupRid: 513);
        f.Group(DomainUsersA, $"{SidA}-513", DirectoryGroupScope.Global, "Domain Users");
        f.Group(StaffA, StaffSid, DirectoryGroupScope.Global, "Staff", Tess);
        f.Group(ProtectedVipsB, VipsSid, DirectoryGroupScope.DomainLocal, "Protected VIPs", StaffA);
        f.DistributionGroup(ExecList, ExecListSid, DirectoryGroupScope.Universal, "Exec List", Tess);
        return f;
    }

    private static ProtectedGroupMembership.Result Evaluate(FakeForest forest, string targetDn, IReadOnlySet<string>? ignored, params string[] entries)
        => ProtectedGroupMembership.Evaluate(entries, targetDn, forest, NullLogger.Instance, ignored);

    private static HashSet<string> Ignoring(params string[] sids) => new(sids, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AnIgnoredSid_TheDirectoryConfirmsNoGroupCarries_IsSkipped_NotIncomplete_AndStillLookedUp()
    {
        var forest = Forest();

        var result = Evaluate(forest, Uma, Ignoring(GoneSid), GoneSid);

        Assert.False(result.Incomplete);
        Assert.Empty(result.Problems);
        Assert.Empty(result.Matches);
        Assert.Equal(1, result.Ignored);
        // The ignore never removes a read: the SID was asked of the forest on this very evaluation.
        Assert.Contains($"FindGroups Sid {GoneSid} forest", forest.Calls);
    }

    [Theory]
    [InlineData("token", Tess, StaffSid)]                     // (a) a security group the target's token carries
    [InlineData("distribution", Tess, ExecListSid)]           // (b) a distribution group, found by member links
    [InlineData("other-domain-local", Tess, VipsSid)]         // (c) a domain-local group of another domain
    [InlineData("no-token", StaffA, VipsSid)]                 // (d) a target with no token: a group
    public void ARestoredGroup_ProtectsAgain_WithTheIgnorePresent_ExactlyAsWithout(string path, string target, string sid)
    {
        var without = Evaluate(Forest(), target, null, sid);
        var forest = Forest();

        var with = Evaluate(forest, target, Ignoring(sid), sid);

        Assert.Equal(["Group:" + sid], without.Matches);
        Assert.Equal(without.Matches, with.Matches);
        Assert.False(with.Incomplete, path);
        Assert.Equal(0, with.Ignored);
    }

    [Theory]
    [InlineData("FindGroups")]                   // the lookup itself fails
    [InlineData("DomainLocalGroupContainsAny")]  // the group's own domain cannot be read
    [InlineData("ReadMemberOf")]                 // the member links cannot be read
    public void AGroupThatCannotBeRead_StaysIncomplete_WithTheIgnorePresent(string failOn)
    {
        var forest = Forest();
        forest.FailOn = failOn;

        var result = Evaluate(forest, Uma, Ignoring(VipsSid), VipsSid);

        Assert.True(result.Incomplete);
        Assert.Empty(result.Matches);
        Assert.Equal(0, result.Ignored);
    }

    [Fact]
    public void SeveralGroupsAnsweringForAnIgnoredSid_StayIncomplete()
    {
        // The ignore suppresses zero only: two answers are no answer, with or without it.
        var forest = Forest();
        const string twin = "CN=Twin,OU=Groups,DC=b,DC=example";
        forest.Group(twin, StaffSid, DirectoryGroupScope.Global, "Twin");

        var result = Evaluate(forest, Uma, Ignoring(StaffSid), StaffSid);

        Assert.True(result.Incomplete);
        Assert.Equal("2 groups match", Assert.Single(result.Problems).Problem);
        Assert.Equal(0, result.Ignored);
    }

    [Fact]
    public void AnEntryThatIsNotASid_IsNeverSkipped_EvenWhenNamed()
    {
        // Only a SID entry - a group_sid row - can be ignored; a DN the self-grant check just read stays "cannot tell".
        const string gone = "CN=Gone,OU=Groups,DC=a,DC=example";

        var result = Evaluate(Forest(), Uma, Ignoring(gone), gone);

        Assert.True(result.Incomplete);
        Assert.Equal(ProtectedGroupMembership.NoSuchGroup, Assert.Single(result.Problems).Problem);
    }

    [Fact]
    public void AnIgnoreForAnotherSid_DoesNotSkipThisOne()
    {
        var result = Evaluate(Forest(), Uma, Ignoring(SidA + "-9998"), GoneSid);

        Assert.True(result.Incomplete);
        Assert.Equal(0, result.Ignored);
    }
}

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md B.4 (slice V2), through the protected-principal check over the stand-in forest: the
/// ignore list read with the rules, matched on the keys Verify writes, changes one answer - a <c>group_sid</c> row's confirmed
/// zero - and nothing else.
/// </summary>
public sealed class ProtectedGroupIgnoreEnforcementTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"dov-v2-test-{Guid.NewGuid():N}");
    private readonly CapturingLogger _log = new();
    private const string GoneLabel = "CN=Tier0 Gone,OU=Groups," + SelfGrantDirectoryStandIn.DomainDn;
    private const string GoneSid = SelfGrantDirectoryStandIn.DomainSid + "-9001";

    public ProtectedGroupIgnoreEnforcementTests() => Directory.CreateDirectory(Path.Combine(_tempDir, "config"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static VerifyIgnoreEntry Ignore(string identifier, string rowKey = ProtectedPrincipalRepository.KindGroupSid,
        string finding = "Missing", string observed = VerifyIgnoreList.NotFoundObserved, string store = VerifyIgnoreList.ProtectedPrincipalsStore) =>
        new(store, rowKey, identifier, finding, observed, "CONTOSO\\admin", "2026-11-05T00:00:00.0000000Z");

    private static ProtectedPrincipalConfig GoneGroup => new()
    {
        Groups = [GoneLabel],
        GroupIds = [ProtectedPrincipalIdEntry.Format(GoneSid, GoneLabel)],
    };

    private void SeedIgnoresRaw(ProtectedPrincipalService service, string raw)
    {
        TestConfigStore.CreateAppSettings(_tempDir).Set(VerifyIgnoreList.SettingKey, raw);
        service.InvalidateCache();
    }

    private void SeedIgnores(ProtectedPrincipalService service, params VerifyIgnoreEntry[] entries) =>
        SeedIgnoresRaw(service, VerifyIgnoreList.Serialize(entries));

    [Fact]
    public async Task AnIgnoredMissingGroup_StopsRefusing_IsStillLookedUp_AndIsCountedOnTheTimingLine()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var target = forest.AddUser("carol", upn: "carol@contoso.test");
        var service = Service(forest);
        service.SaveConfig(GoneGroup);
        SeedIgnores(service, Ignore(GoneSid));

        var result = await service.CheckAsync(Principal(target));

        Assert.False(result.CheckFailed, result.Reason);
        Assert.False(result.IsProtected);
        // Looked up all the same: the group read by its SID ran on this check.
        Assert.Contains(forest.Searches, s => s.StartsWith("Get-ADGroup", StringComparison.Ordinal) && s.Contains(GoneSid, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(_log.Lines, l => l.Level == LogLevel.Debug && l.Text.Contains(GoneSid, StringComparison.Ordinal) && l.Text.Contains("ignored", StringComparison.Ordinal));
        var timing = Assert.Single(_log.Lines, l => l.Level == LogLevel.Information && l.Text.StartsWith("Protected-principal check for", StringComparison.Ordinal));
        Assert.Matches(@"^Protected-principal check for carol@contoso\.test took \d+ ms \(\d+ directory reads, 1 credential fetches, 1 ignored missing groups\)$", timing.Text);
    }

    [Fact]
    public async Task ANonIgnoredMissingGroup_StillRefuses_NamingIt_AndPointsAtVerify()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var target = forest.AddUser("carol", upn: "carol@contoso.test");
        var service = Service(forest);
        service.SaveConfig(GoneGroup);

        var result = await service.CheckAsync(Principal(target));

        Assert.True(result.CheckFailed, result.Reason);
        Assert.Contains($"protected group '{GoneLabel}': no such group - run Verify on Admin Settings", result.Reason, StringComparison.Ordinal);
        // Nothing ignored: the timing line keeps its kd-88 shape.
        var timing = Assert.Single(_log.Lines, l => l.Level == LogLevel.Information && l.Text.StartsWith("Protected-principal check for", StringComparison.Ordinal));
        Assert.Matches(@"^Protected-principal check for carol@contoso\.test took \d+ ms \(\d+ directory reads, 1 credential fetches\)$", timing.Text);
    }

    [Fact]
    public async Task ARestoredGroup_ProtectsAgain_WithTheIgnorePresent()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var target = forest.AddUser("carol", upn: "carol@contoso.test");
        var restored = forest.AddGroup("Tier0 Gone", rid: 9001, members: target);
        Assert.Equal(GoneSid, restored.Sid);
        var service = Service(forest);
        service.SaveConfig(GoneGroup);
        SeedIgnores(service, Ignore(GoneSid));

        var result = await service.CheckAsync(Principal(target));

        Assert.False(result.CheckFailed, result.Reason);
        Assert.True(result.IsProtected);
        Assert.Contains($"Group:{GoneLabel}", result.MatchedRules);
    }

    [Fact]
    public async Task AnIgnoredGroupWhoseLookupFails_StillRefuses()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var target = forest.AddUser("carol", upn: "carol@contoso.test");
        forest.Failures["Get-ADGroup"] = "The server is not operational.";
        var service = Service(forest);
        service.SaveConfig(GoneGroup);
        SeedIgnores(service, Ignore(GoneSid));

        var result = await service.CheckAsync(Principal(target));

        Assert.True(result.CheckFailed, result.Reason);
        Assert.Contains($"protected group '{GoneLabel}'", result.Reason, StringComparison.Ordinal);
    }

    private static readonly (string Why, VerifyIgnoreEntry Ignore)[] OtherRows =
    [
        ("another row key", Ignore(GoneSid, rowKey: ProtectedPrincipalRepository.KindUserSid)),
        ("another identifier", Ignore(SelfGrantDirectoryStandIn.DomainSid + "-9002")),
        ("another finding", Ignore(GoneSid, finding: "LabelDrift")),
        ("another observation", Ignore(GoneSid, observed: "CN=Something Else")),
        ("another store", Ignore(GoneSid, store: "ModuleConfiguration")),
    ];

    public static TheoryData<int> NotThisRow => new(Enumerable.Range(0, OtherRows.Length));

    [Theory]
    [MemberData(nameof(NotThisRow))]
    public async Task AnIgnoreForADifferentRowIdentifierOrFinding_DoesNotSuppress(int row)
    {
        var (why, ignore) = OtherRows[row];
        var forest = new SelfGrantDirectoryStandIn();
        var target = forest.AddUser("carol", upn: "carol@contoso.test");
        var service = Service(forest);
        service.SaveConfig(GoneGroup);
        SeedIgnores(service, ignore);

        var result = await service.CheckAsync(Principal(target));

        Assert.True(result.CheckFailed, why);
        Assert.Contains($"protected group '{GoneLabel}': no such group", result.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProtectedPrincipalRepository.KindGroup)]
    [InlineData(ProtectedPrincipalRepository.KindGroupSid)]
    public async Task ARowStoredByName_IsNeverIgnoredAtEnforcement_AndStillFailsClosedNotConverted(string rowKey)
    {
        // An unconverted row fails closed until converted (decision 2026-11-03): an ignore shaped for it - by its text, under
        // either kind - is not consulted, and the refusal still names the conversion.
        const string name = @"CONTOSO\Tier0";
        var forest = new SelfGrantDirectoryStandIn();
        var target = forest.AddUser("carol", upn: "carol@contoso.test");
        var service = Service(forest);
        service.SaveConfig(new ProtectedPrincipalConfig { Groups = [name] });
        SeedIgnores(service, Ignore(name, rowKey: rowKey));

        var result = await service.CheckAsync(Principal(target));

        Assert.True(result.CheckFailed, result.Reason);
        Assert.Contains($"protected group '{name}': not converted - run db convert-identifiers", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALegacyRowStoredAsTheSameSid_KeepsRefusing_ThoughTheGroupSidRowIsIgnored()
    {
        // A legacy Group row stored as a SID is not a group_sid row: Verify cannot ignore it, so neither can the check.
        var forest = new SelfGrantDirectoryStandIn();
        var target = forest.AddUser("carol", upn: "carol@contoso.test");
        var service = Service(forest);
        service.SaveConfig(new ProtectedPrincipalConfig
        {
            Groups = [GoneLabel, GoneSid],
            GroupIds = [ProtectedPrincipalIdEntry.Format(GoneSid, GoneLabel)],
        });
        SeedIgnores(service, Ignore(GoneSid), Ignore(GoneSid, rowKey: ProtectedPrincipalRepository.KindGroup));

        var result = await service.CheckAsync(Principal(target));

        Assert.True(result.CheckFailed, result.Reason);
        Assert.Contains("no such group", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACorruptIgnoreList_IgnoresNothing_AndIsWarnedOnce()
    {
        // A list holding the matching entry beside one incomplete entry does not parse: nothing in it is honoured.
        var forest = new SelfGrantDirectoryStandIn();
        var target = forest.AddUser("carol", upn: "carol@contoso.test");
        var service = Service(forest);
        service.SaveConfig(GoneGroup);
        var valid = VerifyIgnoreList.Serialize([Ignore(GoneSid)]);
        SeedIgnoresRaw(service, valid[..^1] + ",{\"store\":\"ProtectedPrincipals\"}]");

        var first = await service.CheckAsync(Principal(target));
        var second = await service.CheckAsync(Principal(target));

        Assert.True(first.CheckFailed, first.Reason);
        Assert.Contains($"protected group '{GoneLabel}': no such group", first.Reason, StringComparison.Ordinal);
        Assert.True(second.CheckFailed, second.Reason);
        Assert.Single(_log.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains(VerifyIgnoreList.SettingKey, StringComparison.Ordinal));
    }

    [Fact]
    public void IgnoredMissingGroups_UsesTheKeysVerifyWrites_AndNothingElse()
    {
        var cfg = GoneGroup;

        Assert.Equal([GoneSid], ProtectedPrincipalService.IgnoredMissingGroups(cfg, [Ignore(GoneSid)]));
        Assert.Equal([GoneSid], ProtectedPrincipalService.IgnoredMissingGroups(cfg, [Ignore(GoneSid.ToLowerInvariant())]));
        Assert.Empty(ProtectedPrincipalService.IgnoredMissingGroups(cfg, []));
        foreach (var (why, other) in OtherRows)
            Assert.True(ProtectedPrincipalService.IgnoredMissingGroups(cfg, [other]).Count == 0, why);
    }

    [Fact]
    public void TheIgnoresAreThoseReadWithTheseRules_NeverAnotherSets()
    {
        var service = Service(new SelfGrantDirectoryStandIn());
        service.SaveConfig(GoneGroup);
        SeedIgnores(service, Ignore(GoneSid));

        var (cfg, _, error) = service.LoadEffectiveConfig();

        Assert.Null(error);
        Assert.Equal([Ignore(GoneSid)], service.IgnoresReadWith(cfg));
        // A rule set this service did not load - a stand-in, or rules a different read returned - is handed no ignores.
        Assert.Empty(service.IgnoresReadWith(GoneGroup));
        Assert.Empty(service.IgnoresReadWith(null));
    }

    private static ResolvedDirectoryPrincipal Principal(SelfGrantDirectoryStandIn.Entry target) =>
        new("Test", "carol", target.Upn!, target.Sam, target.Upn, target.Dn, null, null, ObjectSid: target.Sid);

    private ProtectedPrincipalService Service(SelfGrantDirectoryStandIn forest)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = _tempDir,
            ["Security:ProtectedPrincipalDirectoryReadSecretId"] = "4242",
        }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);
        var moduleConfig = new ModuleConfigService(
            new ExchangeAdminWeb.Modules.ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(_tempDir), NullLogger<ModuleConfigService>.Instance);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(configuration, env, TestConfigStore.CreateAppSettings(_tempDir), NullLogger<ExtendedLogService>.Instance);
        var trace = new OperationTraceService(configuration, new JsonlLogService(configuration, NullLogger<JsonlLogService>.Instance));
        var delinea = new DelineaService(httpClientFactory, configuration, NullLogger<DelineaService>.Instance, extLog, trace);
        return new ProtectedPrincipalService(
            env, configuration, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea, _log)
        {
            DirectoryRunspace = forest.Open,
            DirectoryReadSecretFetch = _ =>
                Task.FromResult<(string username, string password, string domain)?>(("svc-read", "pw", "CONTOSO")),
        };
    }

    private sealed class CapturingLogger : ILogger<ProtectedPrincipalService>
    {
        private readonly List<(LogLevel Level, string Text)> _lines = [];

        public IReadOnlyList<(LogLevel Level, string Text)> Lines
        {
            get
            {
                lock (_lines)
                    return _lines.ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
                _lines.Add((logLevel, formatter(state, exception)));
        }
    }
}
