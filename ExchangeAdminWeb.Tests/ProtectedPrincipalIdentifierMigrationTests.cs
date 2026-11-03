using System.Diagnostics;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/ProtectedPrincipalIdentifiers-Plan.md S2: the pass that gives each legacy user, group and OU row its identifier row,
/// at startup and on Resolve now. The resolver's decisions are proven over a fake directory; the store's end state against a
/// real SQLite store; the one credential fetch and the fail-closed legacy Group row through the real service over the
/// stand-in forest.
/// </summary>
public class ProtectedPrincipalIdentifierMigrationTests : IDisposable
{
    private const string AliceSid = "S-1-5-21-1000000000-1000000000-1000000000-1105";
    private const string BobSid = "S-1-5-21-1000000000-1000000000-1000000000-1106";
    private const string Alice = "alice@contoso.com";
    private const string GroupSid = "S-1-5-21-1000000000-1000000000-1000000000-2001";
    private const string GroupDn = "CN=Tier0 Admins,OU=Groups,DC=contoso,DC=com";
    private const string OuGuid = "0b5e3c2a-7d41-4f8e-9c6a-2e1f3d4b5a69";
    private const string OuDn = "OU=Tier0,DC=contoso,DC=com";

    private readonly string _tempDir;
    private readonly ProtectedPrincipalRepository _repository;
    private readonly AppSettingRepository _settings;
    private readonly List<(string Actor, ProtectedPrincipalMigrationResult Result)> _audits = [];

    public ProtectedPrincipalIdentifierMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ppi-s2-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
        var store = TestConfigStore.Create(_tempDir);
        _repository = new ProtectedPrincipalRepository(store);
        _settings = new AppSettingRepository(store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ----- the resolver: one lookup per row, exactly one match converts -----

    [Fact]
    public void EachKind_ConvertsOnExactlyOneMatch()
    {
        var directory = new FakeDirectory();
        directory.Users["(userPrincipalName=alice@contoso.com)"] = [new("CN=Alice,OU=Staff,DC=contoso,DC=com", AliceSid)];
        directory.Users["(sAMAccountName=bob)|contoso.com"] = [new("CN=Bob,OU=Staff,DC=contoso,DC=com", BobSid)];
        directory.Domains["CONTOSO"] = "contoso.com";
        directory.Groups[(GroupLookupKey.DistinguishedName, GroupDn)] = [new(GroupDn, GroupSid, DirectoryGroupScope.Global)];
        directory.Ous[OuDn] = OuGuid;

        var resolved = ProtectedPrincipalIdentifierResolver.Resolve(
        [
            new(ProtectedPrincipalRepository.KindUser, Alice),
            new(ProtectedPrincipalRepository.KindUser, @"CONTOSO\bob"),
            new(ProtectedPrincipalRepository.KindGroup, GroupDn),
            new(ProtectedPrincipalRepository.KindOu, OuDn),
        ], directory, CancellationToken.None);

        Assert.Equal([AliceSid, BobSid, GroupSid, OuGuid], resolved.Select(r => r.Identifier));
        Assert.Equal(
            [ProtectedPrincipalRepository.KindUserSid, ProtectedPrincipalRepository.KindUserSid,
             ProtectedPrincipalRepository.KindGroupSid, ProtectedPrincipalRepository.KindOuGuid],
            resolved.Select(r => r.IdKind));
        // DOMAIN\name is asked of the domain it names, never the forest.
        Assert.Contains("FindUsers contoso.com", directory.Calls);
        // A user is always a person: a computer account, also of class user, is never the match.
        Assert.All(directory.Calls.Where(c => c.StartsWith("filter ", StringComparison.Ordinal)),
            c => Assert.Contains(ProtectedPrincipalIdentifierResolver.PersonFilter, c));
    }

    [Fact]
    public void ZeroManyAndUnavailable_LeaveTheRow_WithTheReason()
    {
        var directory = new FakeDirectory();
        directory.Users["(userPrincipalName=two@contoso.com)"] =
            [new("CN=A,DC=contoso,DC=com", AliceSid), new("CN=B,DC=contoso,DC=com", BobSid)];
        directory.Groups[(GroupLookupKey.SamAccountName, "Ops")] =
            [new("CN=Ops,DC=contoso,DC=com", GroupSid, DirectoryGroupScope.Global),
             new("CN=Ops,DC=fabrikam,DC=com", BobSid, DirectoryGroupScope.Global)];
        directory.Throws[OuDn] = new InvalidOperationException("Unable to contact the server.");

        var resolved = ProtectedPrincipalIdentifierResolver.Resolve(
        [
            new(ProtectedPrincipalRepository.KindUser, "nobody@contoso.com"),
            new(ProtectedPrincipalRepository.KindUser, "two@contoso.com"),
            new(ProtectedPrincipalRepository.KindGroup, "Ops"),
            new(ProtectedPrincipalRepository.KindGroup, "S-1-5-32-544"),
            new(ProtectedPrincipalRepository.KindOu, OuDn),
            new(ProtectedPrincipalRepository.KindOu, "OU=Missing,DC=contoso,DC=com"),
        ], directory, CancellationToken.None);

        Assert.All(resolved, r => Assert.Null(r.Identifier));
        Assert.Equal(
            [ProtectedPrincipalLeftReason.NotFound, ProtectedPrincipalLeftReason.Ambiguous, ProtectedPrincipalLeftReason.Ambiguous,
             ProtectedPrincipalLeftReason.WellKnownSid, ProtectedPrincipalLeftReason.DirectoryUnavailable, ProtectedPrincipalLeftReason.NotFound],
            resolved.Select(r => r.Reason!.Value));
        Assert.Equal("matches 2 objects", resolved[1].Detail);
        Assert.Contains("Unable to contact the server", resolved[4].Detail);
    }

    // ----- the pass against a real store -----

    [Fact]
    public async Task APass_WritesEachIdentifierRowBesideItsLegacyRow_AndRecordsTheRowsItLeft()
    {
        SeedLegacy(users: [Alice, "nobody@contoso.com"], groups: [GroupDn], ous: [OuDn]);
        var migration = Migration(new ResolverOver(StandardDirectory()));

        var result = await migration.RunAsync("CONTOSO\\admin", "10.0.0.1", CancellationToken.None);

        Assert.Equal(ProtectedPrincipalMigrationStatus.RowsLeft, result.Status);
        var data = Rules();
        // The legacy rows are untouched: an older build sharing the database keeps enforcing them.
        Assert.Equal([Alice, "nobody@contoso.com"], data.Users.Order());
        Assert.Equal([GroupDn], data.Groups);
        Assert.Equal([OuDn], data.OrganizationalUnits);
        Assert.Equal([ProtectedPrincipalIdEntry.Format(AliceSid, Alice)], data.UserIds);
        Assert.Equal([ProtectedPrincipalIdEntry.Format(GroupSid, GroupDn)], data.GroupIds);
        Assert.Equal([ProtectedPrincipalIdEntry.Format(OuGuid, OuDn)], data.OuIds);

        var flags = migration.ReadFlags();
        Assert.Equal(ProtectedPrincipalLeftReason.NotFound, flags.For(ProtectedPrincipalRepository.KindUser, "nobody@contoso.com")?.Reason);
        Assert.Null(flags.For(ProtectedPrincipalRepository.KindUser, Alice));

        var (actor, audited) = Assert.Single(_audits);
        Assert.Equal("CONTOSO\\admin", actor);
        var detail = ProtectedPrincipalIdentifierMigration.AuditDetail(audited);
        Assert.Contains($"user: {Alice} -> {AliceSid}", (string[])detail["converted"]!);
        Assert.Contains("user: nobody@contoso.com (not found)", (string[])detail["left"]!);
    }

    [Fact]
    public async Task ASecondPass_IsANoOp()
    {
        SeedLegacy(users: [Alice], groups: [GroupDn], ous: [OuDn]);
        var resolver = new ResolverOver(StandardDirectory());
        var migration = Migration(resolver);

        Assert.Equal(ProtectedPrincipalMigrationStatus.Migrated, (await migration.RunAsync(ProtectedPrincipalIdentifierMigration.SystemActor, "", CancellationToken.None)).Status);
        var afterFirst = Rules();

        var second = await migration.RunAsync(ProtectedPrincipalIdentifierMigration.SystemActor, "", CancellationToken.None);

        Assert.Equal(ProtectedPrincipalMigrationStatus.AlreadyMigrated, second.Status);
        Assert.Equal(1, resolver.Calls);
        Assert.Equivalent(afterFirst, Rules());
        Assert.Single(_audits); // the startup actor's no-op pass files nothing
    }

    [Fact]
    public async Task ARowChangedBetweenTheReadAndTheWrite_IsNotConverted_AndNothingIsHalfWritten()
    {
        SeedLegacy(users: [Alice], groups: [GroupDn], ous: []);
        var resolver = new ResolverOver(StandardDirectory())
        {
            // Inside the lookups, with no lock held: the other instance's admin removes the group row.
            During = () => _repository.Replace(s => s.Data with { Groups = [] }),
        };
        var migration = Migration(resolver);

        var result = await migration.RunAsync(ProtectedPrincipalIdentifierMigration.SystemActor, "", CancellationToken.None);

        Assert.Equal(ProtectedPrincipalMigrationStatus.SkippedChangedRows, result.Status);
        var data = Rules();
        Assert.Empty(data.Groups);
        Assert.Empty(data.GroupIds); // no identifier row for a row that is gone
        Assert.Equal([ProtectedPrincipalIdEntry.Format(AliceSid, Alice)], data.UserIds); // the row nobody touched converted
        Assert.Equal(GroupDn, Assert.Single(result.Skipped).LegacyValue);
    }

    [Fact]
    public async Task AnOlderBuildsWholeStoreSave_ThenAPass_RestoresTheIdentifierRows()
    {
        // The page's add: identifier row and companion.
        var page = ProtectedPrincipalData.Empty with
        {
            Users = [Alice],
            Groups = [GroupDn],
            OrganizationalUnits = [OuDn],
            UserIds = [ProtectedPrincipalIdEntry.Format(AliceSid, Alice)],
            GroupIds = [ProtectedPrincipalIdEntry.Format(GroupSid, GroupDn)],
            OuIds = [ProtectedPrincipalIdEntry.Format(OuGuid, OuDn)],
        };
        _repository.Save(page);

        // An older build saves: it deletes every row and writes back only the kinds it knows.
        _repository.Save(ProtectedPrincipalData.Empty with { Users = [Alice], Groups = [GroupDn], OrganizationalUnits = [OuDn] });
        Assert.Empty(Rules().UserIds);

        var result = await Migration(new ResolverOver(StandardDirectory())).RunAsync(ProtectedPrincipalIdentifierMigration.SystemActor, "", CancellationToken.None);

        Assert.Equal(ProtectedPrincipalMigrationStatus.Migrated, result.Status);
        var data = Rules();
        Assert.Equal(page.UserIds, data.UserIds);
        Assert.Equal(page.GroupIds, data.GroupIds);
        Assert.Equal(page.OuIds, data.OuIds);
    }

    [Fact]
    public async Task ADirectoryThatCannotBeConsulted_WritesNothing_AndFlagsEveryRow()
    {
        SeedLegacy(users: [Alice], groups: [GroupDn], ous: []);
        var resolver = new ResolverOver(StandardDirectory()) { AllUnavailable = "the directory-read credential could not be retrieved" };

        var result = await Migration(resolver).RunAsync(ProtectedPrincipalIdentifierMigration.SystemActor, "", CancellationToken.None);

        Assert.Equal(ProtectedPrincipalMigrationStatus.DirectoryUnavailable, result.Status);
        Assert.Empty(Rules().UserIds);
        Assert.Empty(Rules().GroupIds);
        Assert.Equal(2, result.Left.Count);
    }

    [Fact]
    public async Task TheStartupPass_DoesNotDelayStartup_WhenTheDirectoryHangs()
    {
        SeedLegacy(users: [Alice], groups: [], ous: []);
        var release = new TaskCompletionSource();
        // Released by a timer as well, so a pass that blocked its caller ends the test with a failure rather than a hang.
        _ = Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ContinueWith(_ => release.TrySetResult(), TaskScheduler.Default);
        var resolver = new ResolverOver(StandardDirectory()) { Hang = release.Task };
        var migration = Migration(resolver);

        var clock = Stopwatch.StartNew();
        var pass = migration.StartAtStartup();
        clock.Stop();

        try
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), $"StartAtStartup took {clock.Elapsed}");
            Assert.False(pass.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await pass.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        Assert.Single(Rules().UserIds);
    }

    // ----- through the real service, over the stand-in forest -----

    [Fact]
    public async Task OnePass_FetchesTheDirectoryReadCredentialOnce_ForEveryRow()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var users = Enumerable.Range(1, 4).Select(i => forest.AddUser($"user{i}", upn: $"user{i}@contoso.test")).ToList();
        var group = forest.AddGroup("Tier0");
        var ou = forest.Add(new SelfGrantDirectoryStandIn.Entry { Dn = "OU=Tier0," + SelfGrantDirectoryStandIn.DomainDn, Class = "organizationalUnit" });
        var fetches = 0;
        var service = Service(forest, () => Interlocked.Increment(ref fetches));

        IReadOnlyList<ProtectedPrincipalLegacyRow> rows =
        [
            .. users.Select(u => new ProtectedPrincipalLegacyRow(ProtectedPrincipalRepository.KindUser, u.Upn!)),
            new(ProtectedPrincipalRepository.KindGroup, group.Dn),
            new(ProtectedPrincipalRepository.KindOu, ou.Dn),
        ];
        var resolved = await ((IProtectedPrincipalLegacyResolver)service).ResolveLegacyRowsAsync(rows, CancellationToken.None);

        Assert.Equal(1, fetches);
        Assert.Equal([.. users.Select(u => u.Sid), group.Sid, ou.Guid.ToString("D")], resolved.Select(r => r.Identifier));
        Assert.Empty(forest.Errors);
    }

    [Fact]
    public async Task AnUnmigratedGroupRowNamingNoGroup_ProtectsNobody_BeforeAndAfterAPass_AndIsLeftWithItsReason()
    {
        // sweep-c item 17: a row the directory says names no group made every check with no other match incomplete - every
        // change in every module refused. Nobody can be in a group that does not exist: the check goes on without it, and the
        // pass still leaves it, NotFound, for the page to flag.
        var forest = new SelfGrantDirectoryStandIn();
        var target = forest.AddUser("carol", upn: "carol@contoso.test");
        var gone = "CN=Decommissioned,OU=Groups," + SelfGrantDirectoryStandIn.DomainDn;
        var service = Service(forest, () => { });
        service.SaveConfig(new ProtectedPrincipalConfig { Groups = [gone] });
        var principal = new ResolvedDirectoryPrincipal("Test", "carol", target.Upn!, target.Sam, target.Upn, target.Dn, null, null, ObjectSid: target.Sid);

        var before = await service.CheckAsync(principal);

        var pass = await new ProtectedPrincipalIdentifierMigration(
                TestConfigStore.CreateProtectedPrincipal(_tempDir), service, null, null, NullLogger.Instance)
            .RunAsync(ProtectedPrincipalIdentifierMigration.SystemActor, "", CancellationToken.None);
        var after = await service.CheckAsync(principal);

        Assert.False(before.CheckFailed, before.Reason);
        Assert.False(before.IsProtected);
        Assert.Equal(ProtectedPrincipalLeftReason.NotFound, Assert.Single(pass.Left).Reason);
        Assert.False(after.CheckFailed, after.Reason);
        Assert.False(after.IsProtected);
    }

    [Fact]
    public async Task AGroupRowWhoseLookupFails_StillFailsTheCheckClosed_NamingTheRow()
    {
        // The other half of sweep-c item 17: a read that fails is no answer, so the check still refuses - and the refusal now
        // names the row, where it said only "expansion errors".
        var forest = new SelfGrantDirectoryStandIn();
        var target = forest.AddUser("carol", upn: "carol@contoso.test");
        var unreadable = "CN=Unreadable,OU=Groups," + SelfGrantDirectoryStandIn.DomainDn;
        forest.Failures["Get-ADGroup"] = "The server is not operational.";
        var service = Service(forest, () => { });
        service.SaveConfig(new ProtectedPrincipalConfig { Groups = [unreadable] });
        var principal = new ResolvedDirectoryPrincipal("Test", "carol", target.Upn!, target.Sam, target.Upn, target.Dn, null, null, ObjectSid: target.Sid);

        var result = await service.CheckAsync(principal);

        Assert.True(result.CheckFailed, result.Reason);
        Assert.Contains($"protected group '{unreadable}'", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("expansion errors", result.Reason, StringComparison.Ordinal);
    }

    // ----- harness -----

    private void SeedLegacy(string[] users, string[] groups, string[] ous) =>
        _repository.Save(ProtectedPrincipalData.Empty with { Users = users, Groups = groups, OrganizationalUnits = ous });

    private ProtectedPrincipalData Rules()
    {
        Assert.True(_repository.TryRead(out var data, out _));
        return data;
    }

    private ProtectedPrincipalIdentifierMigration Migration(IProtectedPrincipalLegacyResolver resolver) =>
        new(_repository, resolver, _settings, (actor, _, result) => _audits.Add((actor, result)), NullLogger.Instance);

    private static FakeDirectory StandardDirectory()
    {
        var directory = new FakeDirectory();
        directory.Users["(userPrincipalName=alice@contoso.com)"] = [new("CN=Alice,OU=Staff,DC=contoso,DC=com", AliceSid)];
        directory.Groups[(GroupLookupKey.DistinguishedName, GroupDn)] = [new(GroupDn, GroupSid, DirectoryGroupScope.Global)];
        directory.Ous[OuDn] = OuGuid;
        return directory;
    }

    private ProtectedPrincipalService Service(SelfGrantDirectoryStandIn forest, Action onFetch)
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
            new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(_tempDir), NullLogger<ModuleConfigService>.Instance);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(configuration, env, TestConfigStore.CreateAppSettings(_tempDir), NullLogger<ExtendedLogService>.Instance);
        var trace = new OperationTraceService(configuration, new JsonlLogService(configuration, NullLogger<JsonlLogService>.Instance));
        var delinea = new DelineaService(httpClientFactory, configuration, NullLogger<DelineaService>.Instance, extLog, trace);
        return new ProtectedPrincipalService(
            env, configuration, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea,
            NullLogger<ProtectedPrincipalService>.Instance)
        {
            DirectoryRunspace = forest.Open,
            DirectoryReadSecretFetch = _ =>
            {
                onFetch();
                return Task.FromResult<(string username, string password, string domain)?>(("svc-read", "pw", "CONTOSO"));
            },
        };
    }

    /// <summary>The production resolver's shape over a fake directory: the pure resolver, with seams for the pass's tests.</summary>
    private sealed class ResolverOver(FakeDirectory directory) : IProtectedPrincipalLegacyResolver
    {
        public int Calls { get; private set; }
        public Action? During { get; init; }
        public Task? Hang { get; init; }
        public string? AllUnavailable { get; init; }

        public async Task<IReadOnlyList<ProtectedPrincipalRowResolution>> ResolveLegacyRowsAsync(
            IReadOnlyList<ProtectedPrincipalLegacyRow> rows, CancellationToken cancellationToken)
        {
            Calls++;
            if (Hang is not null)
                await Hang;
            During?.Invoke();
            if (AllUnavailable is not null)
                return rows.Select(r => ProtectedPrincipalRowResolution.Left(r, ProtectedPrincipalLeftReason.DirectoryUnavailable, AllUnavailable)).ToList();
            return ProtectedPrincipalIdentifierResolver.Resolve(rows, directory, cancellationToken);
        }
    }

    private sealed class FakeDirectory : IProtectedPrincipalResolveDirectory
    {
        /// <summary>Keyed by the inner term of the filter, plus "|server" when a server is named.</summary>
        public Dictionary<string, List<DirectoryUserObject>> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(GroupLookupKey, string), List<DirectoryGroup>> Groups { get; } = [];
        public Dictionary<string, string> Ous { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Domains { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Exception> Throws { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = [];

        public IReadOnlyList<DirectoryUserObject> FindUsers(string ldapFilter, string? server)
        {
            Calls.Add("filter " + ldapFilter);
            Calls.Add("FindUsers " + (server ?? "(catalog)"));
            foreach (var (key, users) in Users)
            {
                var parts = key.Split('|');
                if (ldapFilter.Contains(parts[0], StringComparison.OrdinalIgnoreCase)
                    && (parts.Length == 1 ? server is null : string.Equals(parts[1], server, StringComparison.OrdinalIgnoreCase)))
                    return users;
            }
            return [];
        }

        public string ResolveDomainServer(string domain) =>
            Domains.TryGetValue(domain, out var server) ? server : throw new InvalidOperationException($"No domain '{domain}'.");

        public IReadOnlyList<DirectoryGroup> FindGroups(GroupLookupKey key, string value, string? server) =>
            Groups.TryGetValue((key, value), out var groups) ? groups : [];

        public IReadOnlyList<DirectoryObjectGuid> ReadObjectGuids(IReadOnlyList<string> dns, string server)
        {
            if (Throws.TryGetValue(dns[0], out var ex))
                throw ex;
            return dns.Where(Ous.ContainsKey).Select(dn => new DirectoryObjectGuid(dn, Ous[dn])).ToList();
        }

        public DirectoryTarget ReadTarget(string targetDn, string server) => throw new NotSupportedException();
        public IReadOnlyList<string> ReadTokenGroups(string accountDn, string accountSid, string server) => throw new NotSupportedException();
        public IReadOnlyList<DirectoryMemberOf> ReadMemberOf(IReadOnlyList<string> dns, string? server) => throw new NotSupportedException();
        public IReadOnlyList<string> FindObjectDnsBySid(string sid) => throw new NotSupportedException();
        public bool DomainLocalGroupContainsAny(string groupDn, IReadOnlyList<string> memberDns, string server) => throw new NotSupportedException();
    }
}
