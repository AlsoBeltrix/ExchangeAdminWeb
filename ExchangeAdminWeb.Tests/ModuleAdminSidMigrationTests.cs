using System.Diagnostics;
using System.Security.Claims;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/ProtectedPrincipalIdentifiers-Plan.md S5 (B.6): the pass that replaces each module-admin grant stored by name with the
/// SID of the one group it names, at startup and on Module Configuration's Resolve now. The lookup's decisions are proven over a
/// fake directory; the attribute match and the one credential fetch through the real service over the stand-in forest; the
/// store's end state, and what then authorises, against a real SQLite store.
/// </summary>
public sealed class ModuleAdminSidMigrationTests : IDisposable
{
    private const string ModuleA = "MailboxPermissions";
    private const string ModuleB = "CalendarPermissions";
    private const string ContosoOpsSid = "S-1-5-21-1000000000-1000000000-1000000000-3001";
    private const string FabrikamOpsSid = "S-1-5-21-2000000000-2000000000-2000000000-3001";
    private const string ContosoOpsDn = "CN=Ops,OU=Groups,DC=contoso,DC=com";
    private const string FabrikamOpsDn = "CN=Ops,OU=Groups,DC=fabrikam,DC=com";
    private const string HelpdeskSid = "S-1-5-21-1000000000-1000000000-1000000000-3002";
    private const string HelpdeskDn = "CN=Helpdesk,OU=Groups,DC=contoso,DC=com";
    private const string PickedSid = "S-1-5-21-1000000000-1000000000-1000000000-3999";

    private readonly string _tempDir;
    private readonly ModuleAdminRepository _repository;
    private readonly ModuleAdminService _admins;
    private readonly AppSettingRepository _settings;
    private readonly List<(string Actor, ModuleAdminMigrationResult Result)> _audits = [];

    public ModuleAdminSidMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ppi-s5-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
        var store = TestConfigStore.Create(_tempDir);
        _repository = new ModuleAdminRepository(store);
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);
        _admins = new ModuleAdminService(env, _repository, NullLogger<ModuleAdminService>.Instance);
        _settings = new AppSettingRepository(store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // ----- the lookup: one search per grant, in the directory its shape names, exactly one group converts -----

    [Fact]
    public void ADomainQualifiedGrant_ResolvesOnlyInItsDomain_NotToASameNamedGroupOfAnotherDomain()
    {
        var directory = StandardDirectory();

        var resolved = ProtectedPrincipalIdentifierResolver.ResolveModuleAdminGrants(
            ["FABRIKAM\\Ops", "CONTOSO\\Ops", "Ops"], directory, CancellationToken.None);

        Assert.Equal(FabrikamOpsSid, resolved[0].Identifier);
        Assert.Equal(ContosoOpsSid, resolved[1].Identifier);
        // A bare name is the forest's, and two domains carry it: ambiguous, never whichever answered first.
        Assert.Null(resolved[2].Identifier);
        Assert.Equal(ProtectedPrincipalLeftReason.Ambiguous, resolved[2].Reason);
        Assert.Contains("fabrikam.com|Ops", directory.Calls);
        Assert.Contains("contoso.com|Ops", directory.Calls);
        Assert.Contains("(catalog)|Ops", directory.Calls);
    }

    [Fact]
    public void ZeroManyUnavailableAndWellKnown_AreLeft_WithTheirReason()
    {
        var directory = StandardDirectory();
        directory.Groups[("(catalog)", "Administrators")] = [new("CN=Administrators,CN=Builtin,DC=contoso,DC=com", "S-1-5-32-544", DirectoryGroupScope.DomainLocal)];
        directory.Throws["Flaky"] = new InvalidOperationException("Unable to contact the server.");

        var resolved = ProtectedPrincipalIdentifierResolver.ResolveModuleAdminGrants(
            ["Nobody", "Ops", "Flaky", "Administrators", "BUILTIN\\Administrators", "S-1-5-32-544", "NOSUCH\\Ops"],
            directory, CancellationToken.None);

        Assert.All(resolved, r => Assert.Null(r.Identifier));
        Assert.Equal(
            [ProtectedPrincipalLeftReason.NotFound, ProtectedPrincipalLeftReason.Ambiguous, ProtectedPrincipalLeftReason.DirectoryUnavailable,
             ProtectedPrincipalLeftReason.WellKnownSid, ProtectedPrincipalLeftReason.WellKnownSid, ProtectedPrincipalLeftReason.WellKnownSid,
             ProtectedPrincipalLeftReason.DirectoryUnavailable],
            resolved.Select(r => r.Reason!.Value));
        Assert.Equal("not found", resolved[0].Detail);
        Assert.Equal($"2 groups match: {ContosoOpsDn}; {FabrikamOpsDn}", resolved[1].Detail);
        Assert.StartsWith("directory unavailable", resolved[2].Detail);
        // A BUILTIN prefix and a well-known SID are refused without a lookup.
        Assert.DoesNotContain(directory.Calls, c => c.EndsWith("|S-1-5-32-544", StringComparison.Ordinal));
        Assert.DoesNotContain(directory.Calls, c => c.StartsWith("domain BUILTIN", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSearch_MatchesTheSamAccountName_TheCommonName_AndTheDisplayName_GroupsOnly()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var group = forest.Add(new SelfGrantDirectoryStandIn.Entry
        {
            Dn = "CN=Operations Team,OU=Groups," + SelfGrantDirectoryStandIn.DomainDn,
            Class = "group",
            Sam = "ops-sam",
            DisplayName = "Operations (display)",
            Sid = SelfGrantDirectoryStandIn.DomainSid + "-7001",
        });
        // A user carrying the same name is never the match: groups only.
        forest.AddUser("ops-user", nickname: null);
        var service = Service(forest, () => { });

        var resolved = await ((IModuleAdminGrantResolver)service).ResolveModuleAdminGrantsAsync(
            ["ops-sam", "Operations Team", "Operations (display)", "ops-user"], CancellationToken.None);

        Assert.Equal([group.Sid, group.Sid, group.Sid, null], resolved.Select(r => r.Identifier));
        Assert.Equal(ProtectedPrincipalLeftReason.NotFound, resolved[3].Reason);
        Assert.Empty(forest.Errors);
    }

    [Fact]
    public async Task OnePass_FetchesTheDirectoryReadCredentialOnce_ForEveryGrant()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var groups = Enumerable.Range(1, 5).Select(i => forest.AddGroup($"Admins{i}")).ToList();
        var fetches = 0;
        var service = Service(forest, () => Interlocked.Increment(ref fetches));

        var resolved = await ((IModuleAdminGrantResolver)service).ResolveModuleAdminGrantsAsync(
            [.. groups.Select(g => g.Sam!)], CancellationToken.None);

        Assert.Equal(1, fetches);
        Assert.Equal(groups.Select(g => g.Sid), resolved.Select(r => r.Identifier));
    }

    // ----- the pass, against a real store -----

    [Fact]
    public async Task AConvertedGrant_AuthorisesAtOnce_AndTheAuditListsIt()
    {
        _admins.SetModuleAdmins(ModuleA, ["CONTOSO\\Ops", PickedSid]);
        var member = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.GroupSid, ContosoOpsSid)], "test"));
        Assert.False(_admins.IsModuleAdmin(ModuleA, member));

        var result = await Migration(new ResolverOver(StandardDirectory())).RunAsync("CONTOSO\\admin", "10.0.0.1", CancellationToken.None);

        Assert.Equal(ProtectedPrincipalMigrationStatus.Migrated, result.Status);
        Assert.True(_admins.IsModuleAdmin(ModuleA, member));
        Assert.Equal([ContosoOpsSid, PickedSid], _repository.GetForModule(ModuleA).Order(StringComparer.Ordinal));
        var audit = Assert.Single(_audits);
        Assert.Equal("CONTOSO\\admin", audit.Actor);
        Assert.Equal([$"{ModuleA}: CONTOSO\\Ops -> {ContosoOpsSid}"], (string[])ModuleAdminSidMigration.AuditDetail(audit.Result)["converted"]!);
    }

    [Fact]
    public async Task GrantsThatDoNotResolveToExactlyOneGroup_AreListedWithTheirReason_AndNothingIsWritten()
    {
        _admins.SetModuleAdmins(ModuleA, ["Nobody", "Ops"]);
        _admins.SetModuleAdmins(ModuleB, ["Flaky"]);
        var directory = StandardDirectory();
        directory.Throws["Flaky"] = new InvalidOperationException("Unable to contact the server.");
        var before = _repository.GetAll();

        var result = await Migration(new ResolverOver(directory)).RunAsync(ModuleAdminSidMigration.SystemActor, "", CancellationToken.None);

        Assert.Equal(ProtectedPrincipalMigrationStatus.RowsLeft, result.Status);
        Assert.Empty(result.Converted);
        Assert.Equal(before, _repository.GetAll());
        Assert.True(_admins.TryGetUnusableGrants(out var unusable));
        Assert.Equal(["Nobody", "Ops"], unusable[ModuleA].Order(StringComparer.Ordinal));
        Assert.Equal(["Flaky"], unusable[ModuleB]);

        var flags = Migration(new ResolverOver(directory)).ReadFlags();
        Assert.Equal("not found", flags.For(ModuleA, "Nobody")!.Detail);
        Assert.StartsWith("2 groups match: ", flags.For(ModuleA, "Ops")!.Detail);
        Assert.Equal(ProtectedPrincipalLeftReason.DirectoryUnavailable, flags.For(ModuleB, "Flaky")!.Reason);
        Assert.Contains("not found", ExchangeAdminWeb.Components.Pages.ModuleConfig.NotConvertedAdminReason(flags.For(ModuleA, "Nobody")));
        Assert.Contains("2 groups match", ExchangeAdminWeb.Components.Pages.ModuleConfig.NotConvertedAdminReason(flags.For(ModuleA, "Ops")));
        Assert.Contains("could not be asked", ExchangeAdminWeb.Components.Pages.ModuleConfig.NotConvertedAdminReason(flags.For(ModuleB, "Flaky")));
        Assert.Contains("grants nothing", ExchangeAdminWeb.Components.Pages.ModuleConfig.NotConvertedAdminTip(null, null));
        Assert.Equal(2 + 1, ((string[])ModuleAdminSidMigration.AuditDetail(Assert.Single(_audits).Result)["left"]!).Length);
    }

    [Fact]
    public async Task ADirectoryThatCannotBeConsulted_WritesNothing_AndFlagsEveryGrant()
    {
        _admins.SetModuleAdmins(ModuleA, ["CONTOSO\\Ops", "Helpdesk"]);
        var resolver = new ResolverOver(StandardDirectory()) { AllUnavailable = "directory unavailable: the directory-read credential could not be retrieved" };

        var result = await Migration(resolver).RunAsync(ModuleAdminSidMigration.SystemActor, "", CancellationToken.None);

        Assert.Equal(ProtectedPrincipalMigrationStatus.DirectoryUnavailable, result.Status);
        Assert.Equal(["CONTOSO\\Ops", "Helpdesk"], _repository.GetForModule(ModuleA).Order(StringComparer.Ordinal));
        Assert.Equal(2, result.Left.Count);
    }

    [Fact]
    public async Task OneValueHeldByTwoModules_IsLookedUpOnce_AndConvertedInBoth()
    {
        _admins.SetModuleAdmins(ModuleA, ["Helpdesk"]);
        _admins.SetModuleAdmins(ModuleB, ["Helpdesk"]);
        var directory = StandardDirectory();

        await Migration(new ResolverOver(directory)).RunAsync(ModuleAdminSidMigration.SystemActor, "", CancellationToken.None);

        Assert.Single(directory.Calls, c => c == "(catalog)|Helpdesk");
        Assert.Equal([HelpdeskSid], _repository.GetForModule(ModuleA));
        Assert.Equal([HelpdeskSid], _repository.GetForModule(ModuleB));
    }

    [Fact]
    public async Task ASecondPass_IsANoOp()
    {
        _admins.SetModuleAdmins(ModuleA, ["Helpdesk"]);
        var resolver = new ResolverOver(StandardDirectory());
        var migration = Migration(resolver);

        await migration.RunAsync(ModuleAdminSidMigration.SystemActor, "", CancellationToken.None);
        var second = await migration.RunAsync(ModuleAdminSidMigration.SystemActor, "", CancellationToken.None);

        Assert.Equal(ProtectedPrincipalMigrationStatus.AlreadyMigrated, second.Status);
        Assert.Equal(1, resolver.Calls);
        Assert.Equal([HelpdeskSid], _repository.GetForModule(ModuleA));
        Assert.Single(_audits); // the startup actor files nothing for a pass with nothing to do
    }

    [Fact]
    public async Task AGrantChangedBetweenTheReadAndTheWrite_IsNotConverted()
    {
        _admins.SetModuleAdmins(ModuleA, ["Helpdesk", "CONTOSO\\Ops"]);
        // Another admin - or the other instance - removes the Helpdesk grant while the pass is looking it up.
        var resolver = new ResolverOver(StandardDirectory())
        {
            During = () => _admins.ApplyAdminChanges(ModuleA, [], ["Helpdesk"]),
        };

        var result = await Migration(resolver).RunAsync(ModuleAdminSidMigration.SystemActor, "", CancellationToken.None);

        Assert.Equal(ProtectedPrincipalMigrationStatus.SkippedChangedRows, result.Status);
        Assert.Equal([ContosoOpsSid], _repository.GetForModule(ModuleA));
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("Helpdesk", skipped.Name);
    }

    [Fact]
    public async Task AGrantWhoseSidTheModuleAlreadyHolds_IsRemoved_LeavingTheGrantHeldOnce()
    {
        _admins.SetModuleAdmins(ModuleA, ["Helpdesk", HelpdeskSid]);

        var result = await Migration(new ResolverOver(StandardDirectory())).RunAsync(ModuleAdminSidMigration.SystemActor, "", CancellationToken.None);

        Assert.Equal(ProtectedPrincipalMigrationStatus.Migrated, result.Status);
        Assert.Equal([HelpdeskSid], _repository.GetForModule(ModuleA));
    }

    [Fact]
    public async Task TheStartupPass_DoesNotDelayStartup_WhenTheDirectoryHangs()
    {
        _admins.SetModuleAdmins(ModuleA, ["Helpdesk"]);
        var release = new TaskCompletionSource();
        // Released by a timer as well, so a pass that blocked its caller ends the test with a failure rather than a hang.
        _ = Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ContinueWith(_ => release.TrySetResult(), TaskScheduler.Default);
        var migration = Migration(new ResolverOver(StandardDirectory()) { Hang = release.Task });

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
        Assert.Equal([HelpdeskSid], _repository.GetForModule(ModuleA));
    }

    // ----- harness -----

    private ModuleAdminSidMigration Migration(IModuleAdminGrantResolver resolver) =>
        new(_admins, resolver, _settings, (actor, _, result) => _audits.Add((actor, result)), NullLogger.Instance);

    private static FakeGroupDirectory StandardDirectory()
    {
        var directory = new FakeGroupDirectory();
        directory.Domains["CONTOSO"] = "contoso.com";
        directory.Domains["FABRIKAM"] = "fabrikam.com";
        var contosoOps = new DirectoryGroup(ContosoOpsDn, ContosoOpsSid, DirectoryGroupScope.Global);
        var fabrikamOps = new DirectoryGroup(FabrikamOpsDn, FabrikamOpsSid, DirectoryGroupScope.Global);
        directory.Groups[("contoso.com", "Ops")] = [contosoOps];
        directory.Groups[("fabrikam.com", "Ops")] = [fabrikamOps];
        directory.Groups[("(catalog)", "Ops")] = [contosoOps, fabrikamOps];
        directory.Groups[("(catalog)", "Helpdesk")] = [new(HelpdeskDn, HelpdeskSid, DirectoryGroupScope.Global)];
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

    /// <summary>The production resolver's shape over a fake directory: the pure lookup, with seams for the pass's tests.</summary>
    private sealed class ResolverOver(FakeGroupDirectory directory) : IModuleAdminGrantResolver
    {
        public int Calls { get; private set; }
        public Action? During { get; init; }
        public Task? Hang { get; init; }
        public string? AllUnavailable { get; init; }

        public async Task<IReadOnlyList<ProtectedPrincipalRowResolution>> ResolveModuleAdminGrantsAsync(
            IReadOnlyList<string> grants, CancellationToken cancellationToken)
        {
            Calls++;
            if (Hang is not null)
                await Hang;
            During?.Invoke();
            if (AllUnavailable is not null)
                return grants.Select(g => ProtectedPrincipalRowResolution.Left(
                    new ProtectedPrincipalLegacyRow(ProtectedPrincipalRepository.KindGroup, g),
                    ProtectedPrincipalLeftReason.DirectoryUnavailable, AllUnavailable)).ToList();
            return ProtectedPrincipalIdentifierResolver.ResolveModuleAdminGrants(grants, directory, cancellationToken);
        }
    }

    /// <summary>Groups keyed by (server or "(catalog)", name); every call recorded as "server|name" or "domain X".</summary>
    private sealed class FakeGroupDirectory : IProtectedGroupDirectory
    {
        public Dictionary<(string Server, string Name), List<DirectoryGroup>> Groups { get; } = [];
        public Dictionary<string, string> Domains { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Exception> Throws { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = [];

        public string ResolveDomainServer(string domain)
        {
            Calls.Add("domain " + domain);
            return Domains.TryGetValue(domain, out var server) ? server : throw new InvalidOperationException($"No domain '{domain}'.");
        }

        public IReadOnlyList<DirectoryGroup> FindGroups(GroupLookupKey key, string value, string? server)
        {
            Assert.Equal(GroupLookupKey.AnyGroupName, key);
            Calls.Add($"{server ?? "(catalog)"}|{value}");
            if (Throws.TryGetValue(value, out var ex))
                throw ex;
            return Groups.TryGetValue((server ?? "(catalog)", value), out var groups) ? groups : [];
        }

        public DirectoryTarget ReadTarget(string targetDn, string server) => throw new NotSupportedException();
        public IReadOnlyList<string> ReadTokenGroups(string accountDn, string accountSid, string server) => throw new NotSupportedException();
        public IReadOnlyList<DirectoryMemberOf> ReadMemberOf(IReadOnlyList<string> dns, string? server) => throw new NotSupportedException();
        public IReadOnlyList<string> FindObjectDnsBySid(string sid) => throw new NotSupportedException();
        public bool DomainLocalGroupContainsAny(string groupDn, IReadOnlyList<string> memberDns, string server) => throw new NotSupportedException();
    }
}
