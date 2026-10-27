using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public sealed class PermissionValidatorTests : IDisposable
{
    // aud-a26t. Every directory a helper makes, and what it built over one that holds a task or a timer, so Dispose can
    // leave nothing behind in the temp directory: these directories used to be made by every validator and kept forever.
    private readonly List<string> _dirs = [];
    private readonly List<IDisposable> _owned = [];

    public void Dispose()
    {
        foreach (var owned in _owned)
            owned.Dispose();
        _owned.Clear();

        foreach (var dir in _dirs)
        {
            // The pooled connection would otherwise keep the database file open (the AttributeEditorHarness shape).
            SqliteConnection.ClearPool(new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(dir, "exchangeadmin.db"),
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString()));
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        _dirs.Clear();
    }

    /// <summary>A directory of this test's own under the temp directory, deleted by <see cref="Dispose"/>.</summary>
    private string NewTestDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    /// <summary>
    /// An Exchange resolver that answers "no such recipient" for every identity - the legacy
    /// list's entries then stay literals, which is what the tests written before aud-a2b relied
    /// on implicitly when they ran with no resolver at all.
    /// </summary>
    private static IIdentityResolver ExchangeKnowsNobody()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.ResolveRecipientAsync(Arg.Any<string>()).Returns((ResolvedRecipient?)null);
        return resolver;
    }

    /// <summary>
    /// aud-a2d: a mailbox Exchange reports, with the mailbox GUID a write is bound to. An allow now
    /// requires one, so the tests written before a2d that expect an allow give their target one.
    /// </summary>
    private static ResolvedRecipient MailboxRecipient(string address, Guid exchangeGuid) =>
        new(address, "ext-" + address, "UserMailbox", ExistsOnPrem: true, ExchangeGuid: exchangeGuid);

    private static readonly Guid AnyoneGuid = Guid.Parse("1a1a1a1a-0000-4000-8000-000000000001");
    private static readonly Guid OtherGuid = Guid.Parse("1a1a1a1a-0000-4000-8000-000000000002");

    /// <summary>Exchange knows <paramref name="address"/> as a mailbox and nobody else.</summary>
    private static IIdentityResolver ExchangeKnowsOnly(string address, Guid exchangeGuid)
    {
        var resolver = ExchangeKnowsNobody();
        resolver.ResolveRecipientAsync(address).Returns(MailboxRecipient(address, exchangeGuid));
        return resolver;
    }

    private PermissionValidator CreateValidator(
        string[]? excludedUsers = null,
        bool preventSelfGrant = true,
        string[]? appsettingsExcludedUsers = null,
        ExchangeAdminWeb.Services.Storage.IConfigStore? moduleConfigStore = null,
        IIdentityResolver? resolver = null,
        bool directoryReadCredential = false)
    {
        // One database per validator, every store on it, so nothing one test seeds reaches another (aud-a26q); the logs
        // under the same directory rather than one every validator and every run shared, all of it deleted (aud-a26t).
        var testDir = NewTestDir("eaw-test-");

        var configData = new Dictionary<string, string?>
        {
            ["Security:PreventSelfGrant"] = preventSelfGrant.ToString(),
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = Path.Combine(testDir, "logs")
        };

        // aud-a70 (6): a grant the self-grant check clears needs the protected-principal directory-read credential - its
        // Secret ID here, by the fallback the lookups read, and a stand-in fetch for it on the validator below.
        if (directoryReadCredential)
            configData["Security:ProtectedPrincipalDirectoryReadSecretId"] = "4242";

        // Retired appsettings source: seeded only by the guard test that proves a value
        // present ONLY under Security:ExcludedUsers is no longer read (retired 2026-07-28).
        if (appsettingsExcludedUsers is not null)
        {
            for (int i = 0; i < appsettingsExcludedUsers.Length; i++)
                configData[$"Security:ExcludedUsers:{i}"] = appsettingsExcludedUsers[i];
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var logger = Substitute.For<ILogger<PermissionValidator>>();
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        if (resolver is not null)
            serviceProvider.GetService(typeof(IIdentityResolver)).Returns(resolver);

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(testDir);
        var moduleConfigLogger = Substitute.For<ILogger<ModuleConfigService>>();
        var moduleConfigRepo = moduleConfigStore != null
            ? new ExchangeAdminWeb.Services.Storage.ModuleConfigRepository(moduleConfigStore)
            : TestConfigStore.CreateModuleConfig(testDir);
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, moduleConfigRepo, moduleConfigLogger);

        // Exclusions are read from the MailboxPermissions/ExcludedUsers module config
        // (the only source since the appsettings fallback was retired 2026-07-28).
        if (excludedUsers is not null)
        {
            moduleConfig.SaveModuleConfig("MailboxPermissions", new Dictionary<string, string>
            {
                ["ExcludedUsers"] = string.Join(",", excludedUsers)
            });
        }

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var delineaLogger = Substitute.For<ILogger<DelineaService>>();
        var extLogLogger = Substitute.For<ILogger<ExtendedLogService>>();
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(testDir), extLogLogger);
        _owned.Add(extLog);
        var jsonlLogger = Substitute.For<ILogger<JsonlLogService>>();
        var jsonlLog = new JsonlLogService(config, jsonlLogger);
        var operationTrace = new OperationTraceService(config, jsonlLog);
        var delineaService = new DelineaService(httpClientFactory, config, delineaLogger, extLog, operationTrace);
        var protectedPrincipalLogger = Substitute.For<ILogger<ProtectedPrincipalService>>();
        var protectedPrincipalService = new ProtectedPrincipalService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(testDir), delineaService, protectedPrincipalLogger);

        var enablementLogger = Substitute.For<ILogger<ModuleEnablementService>>();
        var enablement = new ModuleEnablementService(new ModuleCatalog(), env, moduleConfig, TestConfigStore.CreateModuleEnablement(testDir), config, enablementLogger);

        var exoPoolLogger = Substitute.For<ILogger<ExoConnectionPool>>();
        var exoPool = new ExoConnectionPool(config, moduleConfig, enablement, exoPoolLogger, operationTrace);
        _owned.Add(exoPool);

        // Real servicer over a store with no ProtectedServicer row, so it denies - these existing
        // assertions are about the PROTECTION decision and must keep passing unchanged.
        var servicers = new ProtectedPrincipalServicerService(
            new SectionAccessService(config, Substitute.For<ILogger<SectionAccessService>>(), env, new ModuleCatalog(),
                new ExchangeAdminWeb.Services.Storage.SectionAccessRepository(TestConfigStore.Create(testDir))),
            Substitute.For<ILogger<ProtectedPrincipalServicerService>>());

        // aud-a43: the self-grant check reads the directory; an empty stand-in forest unless a test gives one.
        return new PermissionValidator(config, moduleConfig, exoPool, protectedPrincipalService, servicers, logger, scopeFactory)
        {
            SelfGrantDirectoryRunspace = new SelfGrantDirectoryStandIn().Open,
            DirectoryReadSecretFetch = directoryReadCredential
                ? _ => Task.FromResult<(string username, string password, string domain)?>(("svc-dirread", "stand-in-secret", "DOMAIN"))
                : null,
        };
    }

    // --- Shared config database: cross-process cache freshness (SharedConfigDb-Plan AC4) ---

    private ExchangeAdminWeb.Services.Storage.IConfigStore NewStore() => TestConfigStore.Create(NewTestDir("eaw-test-"));

    [Fact]
    public void TheHelpers_KeepEveryDirectoryAndTheLogsTheirOwn_AndDisposeDeletesThem()
    {
        // aud-a26t. A validator's logs went to one directory under the temp directory that every validator and every run
        // shared, and no directory these helpers made was ever deleted.
        CreateValidator();
        _ = NewStore();
        CreateValidatorWithScriptedResolution();
        var dirs = _dirs.ToList();

        Assert.Equal(3, dirs.Count);
        Assert.True(Directory.Exists(Path.Combine(dirs[0], "logs", "ExchangeAdminWeb")), "the validator's logs are not under its own directory");
        Assert.True(Directory.Exists(Path.Combine(dirs[2], "ExchangeAdminWeb")), "the scripted validator's logs are not under its own directory");

        Dispose();

        Assert.All(dirs, dir => Assert.False(Directory.Exists(dir), $"{dir} was left behind"));
    }

    private static void SaveExclusionsAsOtherInstance(ExchangeAdminWeb.Services.Storage.IConfigStore store, params string[] users)
        => new ExchangeAdminWeb.Services.Storage.ModuleConfigRepository(store).SaveModule(
            "MailboxPermissions", new Dictionary<string, string> { ["ExcludedUsers"] = string.Join(",", users) });

    [Fact]
    public async Task PermissionValidator_ReloadsWhenTokenMoves()
    {
        var store = NewStore();
        var validator = CreateValidator(excludedUsers: new[] { "blocked@contoso.com" }, moduleConfigStore: store,
            resolver: ExchangeKnowsNobody());
        var now = DateTime.UtcNow;
        validator.ChangeWatcher.UtcNow = () => now;

        Assert.False(await validator.IsUserExcludedAsync("newly@contoso.com"));
        Assert.False(await validator.IsUserExcludedAsync("newly@contoso.com"));

        // "The other instance" saves the module config on the same database - no ConfigSaved
        // event reaches this validator, only the change token does.
        SaveExclusionsAsOtherInstance(store, "blocked@contoso.com", "newly@contoso.com");

        // Inside the 2 s throttle the cache still serves (the 30-minute lifetime is nowhere near).
        Assert.False(await validator.IsUserExcludedAsync("newly@contoso.com"));

        now = now.Add(ExchangeAdminWeb.Services.Storage.ConfigChangeWatcher.Throttle);
        Assert.True(await validator.IsUserExcludedAsync("newly@contoso.com"));
    }

    // scd-2: a write that lands between the load and the FIRST check must be seen by that check.
    [Fact]
    public async Task PermissionValidator_DetectsAChangeMadeBeforeItsFirstPoll()
    {
        var store = NewStore();
        var validator = CreateValidator(excludedUsers: new[] { "blocked@contoso.com" }, moduleConfigStore: store,
            resolver: ExchangeKnowsNobody());

        Assert.False(await validator.IsUserExcludedAsync("newly@contoso.com"));

        SaveExclusionsAsOtherInstance(store, "blocked@contoso.com", "newly@contoso.com");

        Assert.True(await validator.IsUserExcludedAsync("newly@contoso.com"));
    }

    // --- Self-grant validation ---
    // aud-a43: the synchronous, string-only check is gone. Its cases are the async check's logon-name string match, which
    // refuses before anything is looked up; the signed-in operator is a principal now, the logon name its Name claim.

    private static System.Security.Claims.ClaimsPrincipal SignedIn(string logonName) =>
        SelfGrantDirectoryStandIn.Principal(logonName, SelfGrantDirectoryStandIn.DomainSid + "-1104");

    [Fact]
    public async Task ValidateSelfGrant_SameUser_ReturnsError()
    {
        var validator = CreateValidator();
        var result = await validator.ValidateSelfGrantAsync(SignedIn(@"DOMAIN\jdoe"), "jdoe@company.com");
        Assert.NotNull(result);
        Assert.Contains("cannot grant permissions to yourself", result);
    }

    [Fact]
    public async Task ValidateSelfGrant_SameUser_ExactMatch_ReturnsError()
    {
        var validator = CreateValidator();
        var result = await validator.ValidateSelfGrantAsync(SignedIn("jdoe"), "jdoe");
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ValidateSelfGrant_DifferentUser_ReturnsNull()
    {
        // Past the string match the check finds the operator in the directory and asks Exchange: here the directory holds
        // the operator and nobody named jdoe, and Exchange names jdoe as a recipient of their own, so the grant is to someone
        // else. aud-a69: a grantee neither Exchange nor the directory holds is refused as a check that could not be completed.
        // aud-a70 (6): and every grant is refused without the directory-read credential, so this one has it.
        var forest = new SelfGrantDirectoryStandIn();
        var admin = forest.AddUser("admin", upn: "admin@company.com");
        var resolver = ExchangeKnowsNobody();
        resolver.ResolveRecipientAsync("jdoe@company.com").Returns(
            new ResolvedRecipient("jdoe@company.com", "ext-jdoe", "UserMailbox", ExistsOnPrem: false));
        var validator = CreateValidator(resolver: resolver, directoryReadCredential: true);
        validator.SelfGrantDirectoryRunspace = forest.Open;

        var result = await validator.ValidateSelfGrantAsync(
            SelfGrantDirectoryStandIn.Principal(@"DOMAIN\admin", admin.Sid), "jdoe@company.com");

        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateSelfGrant_Disabled_AllowsSelfGrant()
    {
        var validator = CreateValidator(preventSelfGrant: false);
        var result = await validator.ValidateSelfGrantAsync(SignedIn("jdoe"), "jdoe");
        Assert.Null(result);
    }

    [Theory]
    [InlineData(@"DOMAIN\jdoe", "jdoe@company.com")]
    [InlineData("jdoe@company.com", @"DOMAIN\jdoe")]
    [InlineData("jdoe", "jdoe@company.com")]
    [InlineData("jdoe@company.com", "jdoe")]
    public async Task ValidateSelfGrant_ExtractsUsername_AcrossFormats(string current, string affected)
    {
        var validator = CreateValidator();
        var result = await validator.ValidateSelfGrantAsync(SignedIn(current), affected);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task ValidateSelfGrant_CaseInsensitive()
    {
        var validator = CreateValidator();
        var result = await validator.ValidateSelfGrantAsync(SignedIn("JDoe"), "jdoe");
        Assert.NotNull(result);
    }

    // --- Fail-closed init state machine ---

    [Fact]
    public async Task ValidateTargetMailbox_ExclusionExchangeDoesNotKnow_KeepsLiteralMatch()
    {
        // Exchange answered and has no such recipient, so the entry stays a literal match. An
        // unrelated target is allowed; the literal entry is blocked. (Before aud-a2b this ran
        // with no resolver at all and passed only because the failed lookup was swallowed. Since
        // aud-a2d the unrelated target must be a mailbox Exchange knows, or nothing can be written.)
        var validator = CreateValidator(excludedUsers: new[] { "SomeGroupThatNeedsExpansion" },
            resolver: ExchangeKnowsOnly("anyone@company.com", AnyoneGuid));

        var resultUnrelated = await validator.ValidateTargetMailboxAsync("anyone@company.com");
        Assert.Null(resultUnrelated);

        var resultProtected = await validator.ValidateTargetMailboxAsync("SomeGroupThatNeedsExpansion");
        Assert.NotNull(resultProtected);
        Assert.Contains("protected", resultProtected, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateTargetMailbox_ExclusionConfigured_ExchangeCannotBeAsked_FailsClosed()
    {
        // aud-a2b hole 1. With no way to ask Exchange, the list cannot recognise another address
        // of an excluded mailbox - so it cannot say any target is clear. This used to load
        // "successfully" with string matching only and allow everything else.
        var validator = CreateValidator(excludedUsers: new[] { "vip@contoso.com" });

        var result = await validator.ValidateTargetMailboxAsync("anyone@company.com");

        Assert.NotNull(result);
    }

    // --- aud-a2b hole 1: the legacy excluded-user list ---

    private static ResolvedRecipient Vip => new("vip@contoso.com", "ext-vip", "UserMailbox", ExistsOnPrem: true);

    [Fact]
    public async Task LegacyList_ASecondaryAddressOfAnExcludedMailbox_IsRefused()
    {
        // The finding: vip@contoso.com is excluded, and the operator types a secondary address of
        // that same mailbox. Exchange names the recipient; its canonical address is excluded.
        var resolver = ExchangeKnowsNobody();
        resolver.ResolveRecipientAsync("vip@contoso.com").Returns(Vip);
        resolver.ResolveRecipientAsync("alias@contoso.com").Returns(Vip);
        var validator = CreateValidator(excludedUsers: new[] { "vip@contoso.com" }, resolver: resolver);

        var result = await validator.ValidateTargetMailboxAsync("alias@contoso.com", actingUser: null, moduleId: "MailboxPermissions");

        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task LegacyList_AnUnrelatedMailbox_IsAllowed()
    {
        // The opposite direction, so the refusal above cannot be passing by refusing everything.
        var resolver = ExchangeKnowsNobody();
        resolver.ResolveRecipientAsync("vip@contoso.com").Returns(Vip);
        resolver.ResolveRecipientAsync("other@contoso.com").Returns(MailboxRecipient("other@contoso.com", OtherGuid));
        var validator = CreateValidator(excludedUsers: new[] { "vip@contoso.com" }, resolver: resolver);

        var result = await validator.ValidateTargetMailboxAsync("other@contoso.com", actingUser: null, moduleId: "MailboxPermissions");

        Assert.Null(result.Error);
    }

    [Fact]
    public async Task LegacyList_AnExcludedEntryExchangeCouldNotResolve_FailsClosed()
    {
        // The load used to swallow this and accept a list with no ids as complete; every alias
        // then passed for the 30-minute cache lifetime.
        var resolver = ExchangeKnowsNobody();
        resolver.ResolveRecipientAsync("vip@contoso.com")
            .Returns<ResolvedRecipient?>(_ => throw new InvalidOperationException("EXO unreachable"));
        resolver.ResolveRecipientAsync("other@contoso.com")
            .Returns(new ResolvedRecipient("other@contoso.com", "ext-other", "UserMailbox", ExistsOnPrem: true));
        var validator = CreateValidator(excludedUsers: new[] { "vip@contoso.com" }, resolver: resolver);

        var result = await validator.ValidateTargetMailboxAsync("other@contoso.com", actingUser: null, moduleId: "MailboxPermissions");

        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task LegacyList_ATargetExchangeCouldNotResolve_FailsClosed()
    {
        // A failed lookup of the TARGET used to fall through to "not excluded".
        var resolver = ExchangeKnowsNobody();
        resolver.ResolveRecipientAsync("vip@contoso.com").Returns(Vip);
        resolver.ResolveRecipientAsync("alias@contoso.com")
            .Returns<ResolvedRecipient?>(_ => throw new InvalidOperationException("EXO unreachable"));
        var validator = CreateValidator(excludedUsers: new[] { "vip@contoso.com" }, resolver: resolver);

        var result = await validator.ValidateTargetMailboxAsync("alias@contoso.com", actingUser: null, moduleId: "MailboxPermissions");

        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task LegacyList_AFailedLoad_IsRetriedAfterAMinute_NotServedForTheCacheLifetime()
    {
        // Failing closed is the rule; failing closed for half an hour after Exchange recovers is
        // not. The first load fails, the next attempt inside the retry window is still refused
        // from the cached failure, and once the window passes the recovered lookup is used.
        var calls = 0;
        var resolver = ExchangeKnowsNobody();
        resolver.ResolveRecipientAsync("vip@contoso.com").Returns<ResolvedRecipient?>(_ =>
            ++calls == 1 ? throw new InvalidOperationException("EXO unreachable") : Vip);
        resolver.ResolveRecipientAsync("other@contoso.com").Returns(MailboxRecipient("other@contoso.com", OtherGuid));
        var validator = CreateValidator(excludedUsers: new[] { "vip@contoso.com" }, resolver: resolver);
        var now = DateTime.UtcNow;
        validator.UtcNow = () => now;
        validator.ChangeWatcher.UtcNow = () => now;

        Assert.NotNull((await validator.ValidateTargetMailboxAsync("other@contoso.com", null, "MailboxPermissions")).Error);

        now = now.AddSeconds(30);
        Assert.NotNull((await validator.ValidateTargetMailboxAsync("other@contoso.com", null, "MailboxPermissions")).Error);

        now = now.Add(PermissionValidator.FailedLoadRetry);
        Assert.Null((await validator.ValidateTargetMailboxAsync("other@contoso.com", null, "MailboxPermissions")).Error);
    }

    [Fact]
    public async Task ValidateTargetMailbox_EmptyExclusions_AllowsOperations()
    {
        // No exclusions configured at all - init succeeds with empty set. Since aud-a2d the target
        // is still put to Exchange once, for the mailbox the write is bound to.
        var validator = CreateValidator(excludedUsers: Array.Empty<string>(),
            resolver: ExchangeKnowsOnly("anyone@company.com", AnyoneGuid));

        var result = await validator.ValidateTargetMailboxAsync("anyone@company.com");
        Assert.Null(result);
    }

    [Fact]
    public async Task ValidateTargetMailbox_AppsettingsOnlyExclusion_IsNotProtected()
    {
        // Guards the retirement of the Security:ExcludedUsers appsettings fallback
        // (2026-07-28): a value present ONLY under appsettings, with the module config
        // empty, must NOT be treated as excluded. Restoring the fallback read fails this.
        var validator = CreateValidator(appsettingsExcludedUsers: new[] { "AppsettingsOnlyGroup" },
            resolver: ExchangeKnowsOnly("AppsettingsOnlyGroup", AnyoneGuid));

        var result = await validator.ValidateTargetMailboxAsync("AppsettingsOnlyGroup");
        Assert.Null(result);
    }

    [Fact]
    public async Task IsUserExcluded_ChecksExactAndUsername()
    {
        // This test only works if init succeeds. With no Exchange config,
        // any configured user that looks like a group will fail.
        // Use empty exclusions so init succeeds, then verify the method itself.
        var validator = CreateValidator(excludedUsers: Array.Empty<string>());

        var excluded = await validator.IsUserExcludedAsync("anyone@company.com");
        Assert.False(excluded);
    }

    // --- Retry after init failure ---

    [Fact]
    public async Task ValidateTargetMailbox_InitSucceeds_WhenExchangeKnowsNoEntry()
    {
        // When EXO is not configured, group expansion is skipped gracefully and the literal
        // entries are still present. Exchange answering "no such recipient" for every entry is a
        // complete load, so init succeeds and unrelated targets are allowed. (It used to run
        // with no resolver; since aud-a2b that refuses - see the FailsClosed test above.)
        var validator = CreateValidator(excludedUsers: new[] { "SomeGroup" },
            resolver: ExchangeKnowsOnly("user@company.com", AnyoneGuid));

        var firstResult = await validator.ValidateTargetMailboxAsync("user@company.com");
        Assert.Null(firstResult);

        // Second call should return the same (init cached as success)
        var secondResult = await validator.ValidateTargetMailboxAsync("user@company.com");
        Assert.Null(secondResult);
    }

    // --- Resolution outcomes: the operator-facing message must name the real cause ---

    [Fact]
    public async Task AddressOnlyConfig_StillAsksTheDirectory()
    {
        // Audit finding A2. With only address-form users configured, the validator skipped
        // resolution and compared the operator's typed address as a string. A mailbox has more
        // than one address, so the comparison answered a question nobody asked.
        var (validator, pp) = CreateValidatorWithScriptedResolution(addressOnlyConfig: true);

        await validator.ValidateTargetMailboxAsync("alias@contoso.com", actingUser: null, moduleId: "MailboxPermissions");

        Assert.Equal(1, pp.ResolveCalls);
    }

    [Fact]
    public async Task ASecondaryAliasOfAProtectedMailbox_IsRefused()
    {
        // The finding itself. vip@contoso.com is protected; the operator targets a SECONDARY
        // alias of that same mailbox. The directory resolves the alias to the protected
        // primary, so the check must deny - previously the alias never reached the directory
        // and passed as an unrelated address.
        var (validator, pp) = CreateValidatorWithScriptedResolution(addressOnlyConfig: true);
        pp.ResolveTo = new ResolvedDirectoryPrincipal(
            "Test", "VIP", "vip@contoso.com", "vip", "vip@contoso.com", "CN=vip,DC=contoso,DC=com", "guid", null);

        var result = await validator.ValidateTargetMailboxAsync(
            "alias@contoso.com", actingUser: null, moduleId: "MailboxPermissions");

        Assert.NotNull(result.Error);
        Assert.Contains("protected", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AConfiguredSecondaryAddress_StillProtects_AfterResolution()
    {
        // Regression caught reviewing aud-a2, and the mirror image of the bug that slice
        // fixed. The protected entry is itself a SECONDARY address: it resolves to a mailbox
        // whose UPN and primary SMTP are both different, and MatchesIdentity compares only the
        // resolved fields. Always resolving therefore DISCARDED the configured address and the
        // operation passed. Resolution must add the hidden identity, not remove the typed one.
        var (validator, pp) = CreateValidatorWithScriptedResolution(addressOnlyConfig: true);
        pp.ResolveTo = new ResolvedDirectoryPrincipal(
            "Test", "VIP", "someone.else@contoso.com", "vip", "primary@contoso.com",
            "CN=vip,DC=contoso,DC=com", "guid", null);

        var result = await validator.ValidateTargetMailboxAsync(
            "vip@contoso.com", actingUser: null, moduleId: "MailboxPermissions");

        Assert.NotNull(result.Error);
        Assert.Contains("protected", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AddressOnlyConfig_AllowsAnUnprotectedTarget()
    {
        // Review showed the three aud-a2 tests would all pass against an implementation that
        // called the resolver and then always denied. This is the case that forbids that: an
        // ordinary unprotected mailbox, address-only rules, directory answering normally.
        var (validator, pp) = CreateValidatorWithScriptedResolution(addressOnlyConfig: true);
        pp.ResolveTo = new ResolvedDirectoryPrincipal(
            "Test", "Nobody", "nobody@contoso.com", "nobody", "nobody@contoso.com",
            "CN=nobody,DC=contoso,DC=com", "guid", null, ExchangeGuid: AnyoneGuid);

        var result = await validator.ValidateTargetMailboxAsync(
            "nobody@contoso.com", actingUser: null, moduleId: "MailboxPermissions");

        Assert.Null(result.Error);
    }

    [Fact]
    public async Task AnAdMatchExchangeDoesNotAgreeWith_IsNotTheObjectChecked()
    {
        // aud-a2b hole 2 at the validator. The AD-first resolution answers with an unrelated,
        // unprotected account that shares the typed string; the Exchange-target resolution - the
        // object the write will actually hit - refuses because the two disagree. Checking the AD
        // account and handing the same string to Exchange is the bypass.
        var (validator, pp) = CreateValidatorWithScriptedResolution(addressOnlyConfig: true);
        pp.AdFirstStatus = ProtectedPrincipalService.ResolutionStatus.Resolved;
        pp.Status = ProtectedPrincipalService.ResolutionStatus.Ambiguous;
        pp.CheckResult = ProtectedPrincipalResult.NotProtected();

        var result = await validator.ValidateTargetMailboxAsync("vip", actingUser: null, moduleId: "MailboxPermissions");

        Assert.NotNull(result.Error);
        Assert.Equal(1, pp.ResolveCalls);
    }

    [Fact]
    public async Task AddressOnlyConfig_DeniesWhenTheDirectoryCannotAnswer()
    {
        // The deliberate cost of always resolving: an address-only configuration used to work
        // during a directory outage because it never asked. It now denies, by the same rule
        // A3 applies - an unanswerable question is not a negative answer. Pinned so the
        // trade-off cannot be reverted silently.
        var (validator, pp) = CreateValidatorWithScriptedResolution(addressOnlyConfig: true);
        pp.Status = ProtectedPrincipalService.ResolutionStatus.Unavailable;

        var result = await validator.ValidateTargetMailboxAsync(
            "anyone@contoso.com", actingUser: null, moduleId: "MailboxPermissions");

        Assert.NotNull(result.Error);
    }

    /// <summary>
    /// Scripts the resolution seam so the four outcomes can be driven without a directory. The
    /// gate calls ResolveExchangeTargetAsync since aud-a2b, so that is what is overridden; the
    /// AD-first ResolveWithExchangeFallbackAsync is scripted separately so a validator that went
    /// back to it would get an answer - the wrong one - rather than failing for another reason.
    /// </summary>
    private sealed class ScriptedPpService : ProtectedPrincipalService
    {
        public ProtectedPrincipalService.ResolutionStatus Status = ResolutionStatus.Resolved;

        /// <summary>
        /// Forces the central-config branch on. Without this the fixture's repository reports
        /// "not configured", ValidateTargetMailboxAsync skips the entire protection block, and
        /// tests of that block pass no matter what it contains - which is exactly what a
        /// non-vacuity probe caught here.
        /// </summary>
        public override bool HasCentralConfig => true;

        /// <summary>Set to script the fail-closed config-load error branch.</summary>
        public string? LoadError;

        /// <summary>Set to script the outcome of the protection check itself.</summary>
        public ProtectedPrincipalResult? CheckResult;

        public ScriptedPpService(IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
            ExchangeAdminWeb.Services.Storage.ProtectedPrincipalRepository repo, DelineaService delinea)
            : base(env, config, moduleConfig, repo, delinea, Substitute.For<ILogger<ProtectedPrincipalService>>())
        { }

        /// <summary>How many times the directory was asked. Audit finding A2 is about a path
        /// that never asked at all, so the count is the evidence.</summary>
        public int ResolveCalls;

        /// <summary>
        /// Set to script what the directory says an address belongs to - the alias-to-primary
        /// mapping A2 turns on. Null returns an identity principal, as before.
        /// </summary>
        public ResolvedDirectoryPrincipal? ResolveTo;

        /// <summary>The mailbox GUID the default scripted principal carries (aud-a2d: an allow needs one).</summary>
        public static readonly Guid DefaultMailboxGuid = Guid.Parse("1a1a1a1a-0000-4000-8000-0000000000ff");

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveExchangeTargetAsync(string identity)
        {
            ResolveCalls++;
            ResolvedDirectoryPrincipal? p = Status == ResolutionStatus.Resolved
                ? ResolveTo ?? new ResolvedDirectoryPrincipal("Test", identity, identity, "sam", identity, "CN=x,DC=y", "guid", null,
                    ExchangeGuid: DefaultMailboxGuid)
                : null;
            return Task.FromResult((p, Status));
        }

        /// <summary>What the AD-first resolution says: by default an unrelated, resolved account.</summary>
        public ResolutionStatus AdFirstStatus = ResolutionStatus.Resolved;

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithExchangeFallbackAsync(string identity)
        {
            ResolvedDirectoryPrincipal? p = AdFirstStatus == ResolutionStatus.Resolved
                ? new ResolvedDirectoryPrincipal("Test", "Unrelated", "unrelated@contoso.com", identity,
                    null, "CN=Unrelated,DC=y", "unrelated-guid", null)
                : null;
            return Task.FromResult((p, AdFirstStatus));
        }

        public override (ProtectedPrincipalConfig? config, string[] legacyExclusions, string? error) LoadEffectiveConfig()
            => LoadError is not null
                ? (null, Array.Empty<string>(), LoadError)
                : base.LoadEffectiveConfig();

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target)
            => CheckResult is not null
                ? Task.FromResult(CheckResult)
                : base.CheckAsync(target);
    }

    /// <summary>
    /// Builds a validator whose protected-principal config has a Group rule, which is what makes
    /// ValidateTargetMailboxAsync require full resolution and reach the scripted seam.
    /// </summary>
    /// <param name="excludedUsers">aud-a2d: a legacy list to run beside the central check.</param>
    /// <param name="resolver">aud-a2d: what Exchange answers the legacy list, when it asks.</param>
    /// <param name="servicerSid">aud-a2d: a Mailbox Permissions servicer group, so the serviced allow is reachable.</param>
    private (PermissionValidator validator, ScriptedPpService pp) CreateValidatorWithScriptedResolution(
        bool addressOnlyConfig = false,
        string[]? excludedUsers = null,
        IIdentityResolver? resolver = null,
        string? servicerSid = null)
    {
        var testDir = NewTestDir("eaw-res-");
        Directory.CreateDirectory(Path.Combine(testDir, "config"));

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = testDir
        }).Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(testDir);

        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(catalog, env, TestConfigStore.CreateModuleConfig(testDir), Substitute.For<ILogger<ModuleConfigService>>());
        if (excludedUsers is not null)
        {
            moduleConfig.SaveModuleConfig("MailboxPermissions", new Dictionary<string, string>
            {
                ["ExcludedUsers"] = string.Join(",", excludedUsers)
            });
        }

        File.WriteAllText(Path.Combine(testDir, "config", "protected-principals.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                ProtectedPrincipals = new
                {
                    // addressOnlyConfig is the A2 shape: every user entry is an address and
                    // there are no Group/OU/pattern rules. That combination used to skip
                    // directory resolution entirely.
                    Users = addressOnlyConfig ? new[] { "vip@contoso.com" } : Array.Empty<string>(),
                    Groups = addressOnlyConfig
                        ? Array.Empty<string>()
                        : new[] { "CN=VIPs,OU=Groups,DC=contoso,DC=com" },
                    OrganizationalUnits = Array.Empty<string>(),
                    SamAccountNamePatterns = Array.Empty<string>()
                }
            }));

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(testDir), Substitute.For<ILogger<ExtendedLogService>>());
        _owned.Add(extLog);
        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var trace = new OperationTraceService(config, jsonlLog);
        var delinea = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extLog, trace);

        var pp = new ScriptedPpService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(testDir), delinea);

        var enablement = new ModuleEnablementService(catalog, env, moduleConfig, TestConfigStore.CreateModuleEnablement(testDir), config, Substitute.For<ILogger<ModuleEnablementService>>());
        var exoPool = new ExoConnectionPool(config, moduleConfig, enablement, Substitute.For<ILogger<ExoConnectionPool>>(), trace);
        _owned.Add(exoPool);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(serviceProvider);
        if (resolver is not null)
            serviceProvider.GetService(typeof(IIdentityResolver)).Returns(resolver);

        // A servicer row is written to this validator's own store, as every other row here is (aud-a26q).
        var sectionAccessRepo = new ExchangeAdminWeb.Services.Storage.SectionAccessRepository(
            TestConfigStore.Create(testDir));
        if (servicerSid is not null)
        {
            sectionAccessRepo.SaveAll(new Dictionary<string, string[]>
            {
                [ProtectedPrincipalServicerService.SectionKeyFor("MailboxPermissions")] = [servicerSid],
                ["MailboxPermissions"] = ["S-1-5-21-1-2-3-500"],
            });
        }
        var servicers2 = new ProtectedPrincipalServicerService(
            new SectionAccessService(config, Substitute.For<ILogger<SectionAccessService>>(), env, new ModuleCatalog(),
                sectionAccessRepo),
            Substitute.For<ILogger<ProtectedPrincipalServicerService>>());
        var validator = new PermissionValidator(config, moduleConfig, exoPool, pp, servicers2,
            Substitute.For<ILogger<PermissionValidator>>(), scopeFactory);

        return (validator, pp);
    }

    [Fact]
    public async Task ValidateTargetMailbox_NotFoundInEitherDirectory_SaysCheckTheAddress()
    {
        // The reported defect: a target neither directory knows used to produce
        // "identity resolution is unavailable. Contact your administrator." That reads like an
        // outage and sent L1/L2 support chasing the wrong problem when the cause was a bad
        // address. Both directories answered here, so the message must say so.
        var (validator, pp) = CreateValidatorWithScriptedResolution();
        pp.Status = ProtectedPrincipalService.ResolutionStatus.NotFound;

        var result = await validator.ValidateTargetMailboxAsync("nosuchbox@company.com");

        Assert.NotNull(result);
        Assert.Contains("was not found in Active Directory or Exchange Online", result);
        Assert.Contains("nosuchbox@company.com", result);
        Assert.DoesNotContain("Contact your administrator", result);
    }

    [Fact]
    public async Task ValidateTargetMailbox_ResolutionUnavailable_StillDenies()
    {
        // A directory that could not be reached must keep denying, with the administrator
        // message. This is the fail-closed half of the change (Known Failure Class #3).
        var (validator, pp) = CreateValidatorWithScriptedResolution();
        pp.Status = ProtectedPrincipalService.ResolutionStatus.Unavailable;

        var result = await validator.ValidateTargetMailboxAsync("someone@company.com");

        Assert.NotNull(result);
        Assert.Contains("unavailable", result, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Contact your administrator", result);
    }

    [Fact]
    public async Task ValidateTargetMailbox_Ambiguous_SaysAmbiguous_NotUnavailable()
    {
        var (validator, pp) = CreateValidatorWithScriptedResolution();
        pp.Status = ProtectedPrincipalService.ResolutionStatus.Ambiguous;

        var result = await validator.ValidateTargetMailboxAsync("dupe@company.com");

        Assert.NotNull(result);
        Assert.Contains("ambiguous", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateTargetMailbox_Resolved_ProceedsToTheProtectionCheck()
    {
        // A resolved target must not be denied by the resolution branch at all. The configured
        // Group rule cannot be evaluated without a directory-read credential, so the protection
        // check fails closed - which proves the resolution branch was passed rather than skipped.
        var (validator, pp) = CreateValidatorWithScriptedResolution();
        pp.Status = ProtectedPrincipalService.ResolutionStatus.Resolved;

        var result = await validator.ValidateTargetMailboxAsync("user@company.com");

        Assert.NotNull(result);
        Assert.DoesNotContain("was not found", result);
        Assert.DoesNotContain("ambiguous", result, StringComparison.OrdinalIgnoreCase);
    }

    // --- Fail-closed denials after resolution succeeds ---
    //
    // These are the branches that decide whether a protected mailbox can be modified. They were
    // unreachable in a test until HasCentralConfig / LoadEffectiveConfig / CheckAsync became
    // virtual seams, so the whole deny path sat uncovered on the code that enforces it.

    [Fact]
    public async Task ValidateTargetMailbox_ConfigLoadFails_DeniesRatherThanAllowing()
    {
        // Known Failure Class #3, fail-closed authorization. A protection config that cannot be
        // READ says nothing about whether the target is protected - so the only safe answer is to
        // refuse. Allowing here would silently un-protect every principal the moment the config
        // store hiccups, which is the worst possible failure for this gate.
        var (validator, pp) = CreateValidatorWithScriptedResolution();
        pp.LoadError = "protected-principals.json is corrupt";

        var result = await validator.ValidateTargetMailboxAsync("vip@company.com");

        Assert.NotNull(result);
        Assert.Contains("Access denied", result);
        Assert.Contains("corrupt", result);
    }

    [Fact]
    public async Task ValidateTargetMailbox_ProtectionCheckFails_DeniesRatherThanAllowing()
    {
        // The other half of the same rule: the check RAN but could not reach the directory to
        // evaluate a Group or OU rule. An unevaluated rule is not a passed rule.
        var (validator, pp) = CreateValidatorWithScriptedResolution();
        pp.CheckResult = ProtectedPrincipalResult.Failed("directory unreachable");

        var result = await validator.ValidateTargetMailboxAsync("vip@company.com");

        Assert.NotNull(result);
        Assert.Contains("Access denied", result);
        Assert.Contains("directory unreachable", result);
    }

    [Fact]
    public async Task ValidateTargetMailbox_ProtectedPrincipal_IsRefused()
    {
        // The gate doing its actual job.
        var (validator, pp) = CreateValidatorWithScriptedResolution();
        pp.CheckResult = ProtectedPrincipalResult.Protected("Target is a protected principal.", "Group:VIPs");

        var result = await validator.ValidateTargetMailboxAsync("ceo@company.com");

        Assert.NotNull(result);
        Assert.Contains("Access denied", result);
        Assert.Contains("ceo@company.com", result);
        Assert.Contains("protected", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateTargetMailbox_NotProtected_IsAllowed()
    {
        // The permissive path, asserted so the deny tests above are not passing merely because
        // this gate denies everything.
        var (validator, pp) = CreateValidatorWithScriptedResolution();
        pp.CheckResult = ProtectedPrincipalResult.NotProtected();

        var result = await validator.ValidateTargetMailboxAsync("ordinary@company.com");

        Assert.Null(result);
    }

    // --- aud-a2d: the allow carries the mailbox the write is bound to, and there is no allow without one ---

    private static readonly Guid SharedGuid = Guid.Parse("2b2b2b2b-0000-4000-8000-000000000001");
    private const string SharedObjectGuid = "2c2c2c2c-0000-4000-8000-000000000001";

    private static ResolvedDirectoryPrincipal SharedMailbox(Guid? exchangeGuid) => new(
        "Test", "Shared", "shared@contoso.com", "shared", "shared@contoso.com",
        "CN=shared,DC=contoso,DC=com", SharedObjectGuid, null, ExchangeGuid: exchangeGuid);

    [Fact]
    public async Task TheAllowCarriesTheMailboxTheCheckResolved_NotTheStringTyped()
    {
        // The operator types an alias; the check resolves and proves the mailbox behind it. The
        // write must be handed that mailbox - its GUID and the AD object proven to own it - and
        // not the alias, which Exchange would resolve all over again.
        var (validator, pp) = CreateValidatorWithScriptedResolution(addressOnlyConfig: true);
        pp.ResolveTo = SharedMailbox(SharedGuid);

        var result = await validator.ValidateTargetMailboxAsync("alias@contoso.com", actingUser: null, moduleId: "MailboxPermissions");

        Assert.Null(result.Error);
        var mailbox = Assert.IsType<ValidatedMailbox>(result.Mailbox);
        Assert.Equal(SharedGuid, mailbox.ExchangeGuid);
        Assert.Equal(SharedGuid.ToString("D"), mailbox.Identity);
        Assert.Equal(SharedObjectGuid, mailbox.DirectoryObjectGuid);
    }

    [Fact]
    public async Task AResolvedTargetWithNoMailbox_IsRefused_RatherThanWrittenByItsName()
    {
        // ResolveExchangeTargetAsync's no-recipient branch: AD knows the object, Exchange has no
        // recipient, so the principal carries no mailbox GUID. Before aud-a2d this was allowed and
        // the write handed the string to Exchange to make of it what it would.
        var (validator, pp) = CreateValidatorWithScriptedResolution(addressOnlyConfig: true);
        pp.ResolveTo = SharedMailbox(exchangeGuid: null);

        var result = await validator.ValidateTargetMailboxAsync("shared@contoso.com", actingUser: null, moduleId: "MailboxPermissions");

        Assert.Equal(PermissionValidator.NoMailboxMessage("shared@contoso.com"), result.Error);
        Assert.Null(result.Mailbox);
    }

    [Fact]
    public async Task AProtectedTargetWithNoMailbox_IsStillRefusedAsProtected()
    {
        // The binding runs after the protection verdict, so a protected principal keeps the
        // refusal - and the warning - that names it as protected.
        var (validator, pp) = CreateValidatorWithScriptedResolution(addressOnlyConfig: true);
        pp.ResolveTo = SharedMailbox(exchangeGuid: null);
        pp.CheckResult = ProtectedPrincipalResult.Protected("Target is a protected principal.", "User:vip");

        var result = await validator.ValidateTargetMailboxAsync("shared@contoso.com", actingUser: null, moduleId: "MailboxPermissions");

        Assert.Contains("is protected", result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AServicedAllow_CarriesTheMailbox_AndWithoutOneIsRefused(bool hasMailbox)
    {
        const string servicerSid = "S-1-5-21-1-2-3-4001";
        var (validator, pp) = CreateValidatorWithScriptedResolution(addressOnlyConfig: true, servicerSid: servicerSid);
        pp.ResolveTo = SharedMailbox(hasMailbox ? SharedGuid : null);
        pp.CheckResult = ProtectedPrincipalResult.Protected("Target is a protected principal.", "User:vip");
        var servicer = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, @"CONTOSO\servicer"),
             new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, servicerSid)], "TestAuth"));

        var result = await validator.ValidateTargetMailboxAsync("shared@contoso.com", servicer, "MailboxPermissions");

        if (hasMailbox)
        {
            Assert.Null(result.Error);
            Assert.NotNull(result.ServicedNote);
            Assert.Equal(SharedGuid, result.Mailbox!.ExchangeGuid);
        }
        else
        {
            Assert.Equal(PermissionValidator.NoMailboxMessage("shared@contoso.com"), result.Error);
            Assert.Null(result.ServicedNote);
            Assert.Null(result.Mailbox);
        }
    }

    [Fact]
    public async Task NothingConfigured_TheTargetIsStillBoundToTheMailboxExchangeNames()
    {
        // No protection configured: nothing to check, but the write must still be bound to a
        // mailbox rather than to the string - so Exchange is asked once, here.
        var resolver = ExchangeKnowsOnly("anyone@company.com", AnyoneGuid);
        var validator = CreateValidator(excludedUsers: Array.Empty<string>(), resolver: resolver);

        var result = await validator.ValidateTargetMailboxAsync("anyone@company.com", actingUser: null, moduleId: "MailboxPermissions");

        Assert.Null(result.Error);
        Assert.Equal(AnyoneGuid, result.Mailbox!.ExchangeGuid);
        Assert.Null(result.Mailbox.DirectoryObjectGuid);
        await resolver.Received(1).ResolveRecipientAsync("anyone@company.com");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NothingConfigured_ATargetWithNoMailbox_IsRefused(bool recipientWithoutMailbox)
    {
        // No recipient at all, or a recipient Exchange reports no mailbox GUID for (a contact, a
        // distribution group): either way there is no mailbox to bind a write to.
        var resolver = ExchangeKnowsNobody();
        if (recipientWithoutMailbox)
        {
            resolver.ResolveRecipientAsync("team@company.com")
                .Returns(new ResolvedRecipient("team@company.com", "ext-team", "MailUniversalDistributionGroup", ExistsOnPrem: false));
        }
        var validator = CreateValidator(excludedUsers: Array.Empty<string>(), resolver: resolver);

        var result = await validator.ValidateTargetMailboxAsync("team@company.com", actingUser: null, moduleId: "MailboxPermissions");

        Assert.Equal(PermissionValidator.NoMailboxMessage("team@company.com"), result.Error);
        Assert.Null(result.Mailbox);
    }

    [Fact]
    public async Task NothingConfigured_ExchangeCannotBeAsked_IsRefused()
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.ResolveRecipientAsync(Arg.Any<string>())
            .Returns<ResolvedRecipient?>(_ => throw new InvalidOperationException("EXO unreachable"));
        var withResolver = CreateValidator(excludedUsers: Array.Empty<string>(), resolver: resolver);
        var withoutResolver = CreateValidator(excludedUsers: Array.Empty<string>());

        var unreachable = await withResolver.ValidateTargetMailboxAsync("anyone@company.com", actingUser: null, moduleId: "MailboxPermissions");
        var unregistered = await withoutResolver.ValidateTargetMailboxAsync("anyone@company.com", actingUser: null, moduleId: "MailboxPermissions");

        Assert.Contains("could not be identified", unreachable.Error, StringComparison.Ordinal);
        Assert.Null(unreachable.Mailbox);
        Assert.Contains("could not be identified", unregistered.Error, StringComparison.Ordinal);
        Assert.Null(unregistered.Mailbox);
    }

    [Fact]
    public async Task LegacyList_TheAllowCarriesTheMailboxTheListChecked_AskingExchangeOnce()
    {
        // The list put the target to Exchange to compare its canonical address; that answer is the
        // mailbox the write is bound to. Asking again could get a different one.
        var resolver = ExchangeKnowsNobody();
        resolver.ResolveRecipientAsync("vip@contoso.com").Returns(Vip);
        resolver.ResolveRecipientAsync("other@contoso.com").Returns(MailboxRecipient("other@contoso.com", OtherGuid));
        var validator = CreateValidator(excludedUsers: new[] { "vip@contoso.com" }, resolver: resolver);

        var result = await validator.ValidateTargetMailboxAsync("other@contoso.com", actingUser: null, moduleId: "MailboxPermissions");

        Assert.Null(result.Error);
        Assert.Equal(OtherGuid, result.Mailbox!.ExchangeGuid);
        await resolver.Received(1).ResolveRecipientAsync("other@contoso.com");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CentralAndLegacyLookupsNamingDifferentMailboxes_AreRefused(bool sameMailbox)
    {
        // Both checks ran on this target and each asked Exchange. If the answers name two
        // mailboxes, the two checks looked at different objects and there is no one mailbox the
        // write could be bound to. The same answer twice is the ordinary case and is allowed.
        var resolver = ExchangeKnowsNobody();
        resolver.ResolveRecipientAsync("legacy-vip@contoso.com").Returns(MailboxRecipient("legacy-vip@contoso.com", AnyoneGuid));
        resolver.ResolveRecipientAsync("alias@contoso.com")
            .Returns(MailboxRecipient(sameMailbox ? "shared@contoso.com" : "elsewhere@contoso.com", sameMailbox ? SharedGuid : OtherGuid));
        var (validator, pp) = CreateValidatorWithScriptedResolution(
            addressOnlyConfig: true, excludedUsers: ["legacy-vip@contoso.com"], resolver: resolver);
        pp.ResolveTo = SharedMailbox(SharedGuid);

        var result = await validator.ValidateTargetMailboxAsync("alias@contoso.com", actingUser: null, moduleId: "MailboxPermissions");

        if (sameMailbox)
        {
            Assert.Null(result.Error);
            Assert.Equal(SharedGuid, result.Mailbox!.ExchangeGuid);
        }
        else
        {
            Assert.Contains("two different mailboxes", result.Error, StringComparison.Ordinal);
            Assert.Null(result.Mailbox);
        }
    }
}
