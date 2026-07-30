using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Services.ProtectedPrincipalService;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards ProtectedPrincipalService.ResolveWithExchangeFallbackAsync - the resolution entry point
/// the protection gates use.
///
/// Two properties are load-bearing and both are fail-closed
/// (docs/ProtectedPrincipalResolution-Plan.md):
///
/// 1. Only a NotFound from Active Directory may fall through to Exchange. Resolved, Ambiguous and
///    Unavailable must come back exactly as AD produced them, so nothing that denies today starts
///    allowing.
/// 2. An Exchange lookup that could not run must return Unavailable, never NotFound. Callers in
///    ConferenceRooms and GroupManagement allow on NotFound, so collapsing an EXO outage into
///    NotFound would un-protect a principal.
///
/// The third property is the reason the feature exists: a protected mailbox addressed by a
/// secondary SMTP alias misses AD entirely (protected rows are stored as primary addresses).
/// Exchange normalizes the alias to the canonical address, which is then re-resolved in AD so the
/// group / OU / pattern rules apply.
/// </summary>
public class ProtectedPrincipalExchangeFallbackTests : IDisposable
{
    private readonly string _tempDir;

    public ProtectedPrincipalExchangeFallbackTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ppef-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    /// <summary>
    /// Scripts the AD half of the resolution so the Exchange half can be exercised without a live
    /// directory. Records every identity it was asked for, which is how the "AD is not re-queried"
    /// and "re-queried with the canonical address" assertions are made.
    /// </summary>
    private sealed class ScriptedAdService : ProtectedPrincipalService
    {
        private readonly Dictionary<string, (ResolvedDirectoryPrincipal?, ResolutionStatus)> _script
            = new(StringComparer.OrdinalIgnoreCase);

        public readonly List<string> AdLookups = new();

        public ScriptedAdService(IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
            ProtectedPrincipalRepository repo, DelineaService delinea, IServiceScopeFactory? scopeFactory)
            : base(env, config, moduleConfig, repo, delinea, NullLogger<ProtectedPrincipalService>.Instance, scopeFactory)
        { }

        public ScriptedAdService AdReturns(string identity, ResolutionStatus status, ResolvedDirectoryPrincipal? principal = null)
        {
            _script[identity] = (principal, status);
            return this;
        }

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithStatusAsync(string identity)
        {
            AdLookups.Add(identity);
            return Task.FromResult(_script.TryGetValue(identity, out var scripted)
                ? scripted
                : ((ResolvedDirectoryPrincipal?)null, ResolutionStatus.NotFound));
        }
    }

    private static ResolvedDirectoryPrincipal AdPrincipal(string address, string? dn = "CN=x,OU=Users,DC=contoso,DC=com")
        => new("ProtectedPrincipalService-AD", address, address, address.Split('@')[0], address, dn, "guid", null);

    /// <param name="resolver">null registers no IIdentityResolver at all.</param>
    /// <param name="withScopeFactory">false constructs the service the way the direct-construction
    /// test files do, with no scope factory available.</param>
    private ScriptedAdService CreateService(IIdentityResolver? resolver, bool withScopeFactory = true)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = _tempDir,
        }).Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var moduleConfig = new ModuleConfigService(new Modules.ModuleCatalog(), env,
            TestConfigStore.CreateModuleConfig(_tempDir), NullLogger<ModuleConfigService>.Instance);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), NullLogger<ExtendedLogService>.Instance);
        var jsonlLog = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(config, jsonlLog);
        var delinea = new DelineaService(httpClientFactory, config, NullLogger<DelineaService>.Instance, extLog, trace);

        IServiceScopeFactory? scopeFactory = null;
        if (withScopeFactory)
        {
            var services = new ServiceCollection();
            if (resolver != null)
                services.AddSingleton(resolver);
            scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        }

        return new ScriptedAdService(env, config, moduleConfig,
            TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea, scopeFactory);
    }

    private static IIdentityResolver ResolverReturning(ResolvedRecipient? recipient)
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.ResolveRecipientAsync(Arg.Any<string>()).Returns(recipient);
        return resolver;
    }

    private static IIdentityResolver ResolverThrowing(Exception ex)
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.ResolveRecipientAsync(Arg.Any<string>()).Returns<ResolvedRecipient?>(_ => throw ex);
        return resolver;
    }

    // ---- property 1: only NotFound falls through to Exchange -----------------

    [Fact]
    public async Task Resolved_PassesThrough_AndDoesNotConsultExchange()
    {
        var resolver = ResolverReturning(new ResolvedRecipient("other@contoso.com", null, "UserMailbox", true));
        var service = CreateService(resolver);
        service.AdReturns("user@contoso.com", ResolutionStatus.Resolved, AdPrincipal("user@contoso.com"));

        var (principal, status) = await service.ResolveWithExchangeFallbackAsync("user@contoso.com");

        Assert.Equal(ResolutionStatus.Resolved, status);
        Assert.Equal("user@contoso.com", principal!.UserPrincipalName);
        await resolver.DidNotReceive().ResolveRecipientAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Ambiguous_PassesThrough_AndDoesNotConsultExchange()
    {
        // Ambiguous is a deliberate fail-closed denial in AD. Asking Exchange could turn it into a
        // single confident answer and defeat that.
        var resolver = ResolverReturning(new ResolvedRecipient("user@contoso.com", null, "UserMailbox", true));
        var service = CreateService(resolver);
        service.AdReturns("user@contoso.com", ResolutionStatus.Ambiguous);

        var (principal, status) = await service.ResolveWithExchangeFallbackAsync("user@contoso.com");

        Assert.Equal(ResolutionStatus.Ambiguous, status);
        Assert.Null(principal);
        await resolver.DidNotReceive().ResolveRecipientAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task AdUnavailable_StaysUnavailable_AndDoesNotConsultExchange()
    {
        // AD did not answer. Exchange answering instead would substitute a different directory's
        // view for the one the protection rules are written against.
        var resolver = ResolverReturning(new ResolvedRecipient("user@contoso.com", null, "UserMailbox", true));
        var service = CreateService(resolver);
        service.AdReturns("user@contoso.com", ResolutionStatus.Unavailable);

        var (_, status) = await service.ResolveWithExchangeFallbackAsync("user@contoso.com");

        Assert.Equal(ResolutionStatus.Unavailable, status);
        await resolver.DidNotReceive().ResolveRecipientAsync(Arg.Any<string>());
    }

    // ---- property 2: a failed Exchange lookup is never an absence -----------

    [Fact]
    public async Task ExchangeThrows_IsUnavailable_NotNotFound()
    {
        // THE fail-closed case. NotFound here would let an EXO outage un-protect an
        // alias-addressed principal in ConferenceRooms and GroupManagement, which allow on
        // NotFound.
        var service = CreateService(ResolverThrowing(new InvalidOperationException("Connecting to remote server failed")));
        service.AdReturns("ceo@contoso.com", ResolutionStatus.NotFound);

        var (principal, status) = await service.ResolveWithExchangeFallbackAsync("ceo@contoso.com");

        Assert.Equal(ResolutionStatus.Unavailable, status);
        Assert.Null(principal);
    }

    [Fact]
    public async Task NoScopeFactory_IsUnavailable_NotNotFound()
    {
        var service = CreateService(resolver: null, withScopeFactory: false);
        service.AdReturns("ceo@contoso.com", ResolutionStatus.NotFound);

        var (_, status) = await service.ResolveWithExchangeFallbackAsync("ceo@contoso.com");

        Assert.Equal(ResolutionStatus.Unavailable, status);
    }

    [Fact]
    public async Task NoIdentityResolverRegistered_IsUnavailable_NotNotFound()
    {
        var service = CreateService(resolver: null);
        service.AdReturns("ceo@contoso.com", ResolutionStatus.NotFound);

        var (_, status) = await service.ResolveWithExchangeFallbackAsync("ceo@contoso.com");

        Assert.Equal(ResolutionStatus.Unavailable, status);
    }

    // ---- confirmed absence ---------------------------------------------------

    [Fact]
    public async Task NeitherDirectoryHasTheRecipient_IsNotFound()
    {
        var service = CreateService(ResolverReturning(null));
        service.AdReturns("typo@contoso.com", ResolutionStatus.NotFound);

        var (principal, status) = await service.ResolveWithExchangeFallbackAsync("typo@contoso.com");

        Assert.Equal(ResolutionStatus.NotFound, status);
        Assert.Null(principal);
    }

    // ---- the alias case: what closes the bypass ------------------------------

    [Fact]
    public async Task AliasAddressed_ReResolvesCanonicalAddressInAd()
    {
        // The regression case from the plan: the CEO reached by a secondary alias. AD misses the
        // alias, Exchange returns the primary address, and the AD re-resolution produces the full
        // principal the protection rules match against.
        var service = CreateService(ResolverReturning(
            new ResolvedRecipient("vincent.roche@contoso.com", "ext-1", "UserMailbox", ExistsOnPrem: true)));
        service.AdReturns("VRoche@o365.contoso.com", ResolutionStatus.NotFound);
        service.AdReturns("vincent.roche@contoso.com", ResolutionStatus.Resolved, AdPrincipal("vincent.roche@contoso.com"));

        var (principal, status) = await service.ResolveWithExchangeFallbackAsync("VRoche@o365.contoso.com");

        Assert.Equal(ResolutionStatus.Resolved, status);
        Assert.Equal("vincent.roche@contoso.com", principal!.PrimarySmtpAddress);
        Assert.NotNull(principal.DistinguishedName); // group and OU rules can be evaluated
        Assert.Equal(
            new[] { "VRoche@o365.contoso.com", "vincent.roche@contoso.com" },
            service.AdLookups);
    }

    [Fact]
    public async Task AliasReResolution_AdUnavailable_IsUnavailable()
    {
        // AD went down between the two calls. The alias case must not degrade into an absence.
        var service = CreateService(ResolverReturning(
            new ResolvedRecipient("vincent.roche@contoso.com", null, "UserMailbox", ExistsOnPrem: true)));
        service.AdReturns("VRoche@o365.contoso.com", ResolutionStatus.NotFound);
        service.AdReturns("vincent.roche@contoso.com", ResolutionStatus.Unavailable);

        var (_, status) = await service.ResolveWithExchangeFallbackAsync("VRoche@o365.contoso.com");

        Assert.Equal(ResolutionStatus.Unavailable, status);
    }

    [Fact]
    public async Task AliasReResolution_AmbiguousInAd_StaysAmbiguous()
    {
        var service = CreateService(ResolverReturning(
            new ResolvedRecipient("shared@contoso.com", null, "UserMailbox", ExistsOnPrem: true)));
        service.AdReturns("alias@contoso.com", ResolutionStatus.NotFound);
        service.AdReturns("shared@contoso.com", ResolutionStatus.Ambiguous);

        var (_, status) = await service.ResolveWithExchangeFallbackAsync("alias@contoso.com");

        Assert.Equal(ResolutionStatus.Ambiguous, status);
    }

    [Fact]
    public async Task CanonicalAddressDifferingOnlyByCase_IsNotTreatedAsAnAlias()
    {
        // Exchange echoes the address back in its own casing. Re-resolving would be a pointless
        // second AD hit against an address AD has already said it does not have.
        var service = CreateService(ResolverReturning(
            new ResolvedRecipient("User@Contoso.com", null, "UserMailbox", ExistsOnPrem: true)));
        service.AdReturns("user@contoso.com", ResolutionStatus.NotFound);

        await service.ResolveWithExchangeFallbackAsync("user@contoso.com");

        Assert.Equal(new[] { "user@contoso.com" }, service.AdLookups);
    }

    // ---- cloud-only ----------------------------------------------------------

    [Fact]
    public async Task CloudOnlyRecipient_ResolvesWithNoDistinguishedName()
    {
        // Exchange knows the recipient under the address AD missed, so there is no on-prem object.
        // A null DN is the point: it is what makes the group and OU rules inapplicable rather than
        // skipped, and CheckAsync must not be handed a fabricated one.
        var service = CreateService(ResolverReturning(
            new ResolvedRecipient("jabil.support@contoso.com", "ext-9", "UserMailbox", ExistsOnPrem: false)));
        service.AdReturns("jabil.support@contoso.com", ResolutionStatus.NotFound);

        var (principal, status) = await service.ResolveWithExchangeFallbackAsync("jabil.support@contoso.com");

        Assert.Equal(ResolutionStatus.Resolved, status);
        Assert.Equal("jabil.support@contoso.com", principal!.PrimarySmtpAddress);
        Assert.Equal("jabil.support@contoso.com", principal.UserPrincipalName);
        Assert.Equal("ProtectedPrincipalService-EXO", principal.Source);
        Assert.Equal("ext-9", principal.EntraObjectId);
        Assert.Null(principal.DistinguishedName);
        Assert.Null(principal.SamAccountName);
    }

    [Fact]
    public async Task CloudOnlyRecipient_StillMatchesProtectedUserRows()
    {
        // The rule types a cloud-only principal CAN be protected by must actually fire. This is
        // the guarantee behind the Constitution's "protect by address, never by group membership".
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = new[] { "vip.cloud@contoso.com" },
                Groups = Array.Empty<string>(),
                OrganizationalUnits = Array.Empty<string>(),
                SamAccountNamePatterns = Array.Empty<string>()
            }
        });
        File.WriteAllText(Path.Combine(_tempDir, "config", "protected-principals.json"), json);

        var service = CreateService(ResolverReturning(
            new ResolvedRecipient("vip.cloud@contoso.com", "ext-vip", "UserMailbox", ExistsOnPrem: false)));
        service.AdReturns("vip.cloud@contoso.com", ResolutionStatus.NotFound);

        var (principal, status) = await service.ResolveWithExchangeFallbackAsync("vip.cloud@contoso.com");
        Assert.Equal(ResolutionStatus.Resolved, status);

        var verdict = await service.CheckAsync(principal!);

        Assert.True(verdict.IsProtected);
        Assert.False(verdict.CheckFailed);
        Assert.Contains("User:vip.cloud@contoso.com", verdict.MatchedRules);
    }

    [Fact]
    public async Task MailEnabledGroup_FlowsThroughCheckAsyncWithoutError()
    {
        // Get-Recipient returns groups, which Get-ADUser never could. The resolved principal has
        // to survive CheckAsync - MatchesIdentity compares strings and must not assume a user.
        var service = CreateService(ResolverReturning(
            new ResolvedRecipient("adspstaff@contoso.com", "ext-grp", "MailUniversalDistributionGroup", ExistsOnPrem: false)));
        service.AdReturns("adspstaff@contoso.com", ResolutionStatus.NotFound);

        var (principal, status) = await service.ResolveWithExchangeFallbackAsync("adspstaff@contoso.com");
        Assert.Equal(ResolutionStatus.Resolved, status);

        var verdict = await service.CheckAsync(principal!);

        Assert.False(verdict.IsProtected);
        Assert.False(verdict.CheckFailed);
    }

    // ---- the legacy entry point is untouched --------------------------------

    [Fact]
    public async Task LegacyResolveDirectoryPrincipalAsync_DoesNotUseTheFallback()
    {
        // Callers still on the legacy wrapper must keep exactly today's behavior until slice 4
        // switches them over.
        var resolver = ResolverReturning(new ResolvedRecipient("vincent.roche@contoso.com", null, "UserMailbox", true));
        var service = CreateService(resolver);
        service.AdReturns("VRoche@o365.contoso.com", ResolutionStatus.NotFound);

        var result = await service.ResolveDirectoryPrincipalAsync("VRoche@o365.contoso.com");

        Assert.Null(result);
        await resolver.DidNotReceive().ResolveRecipientAsync(Arg.Any<string>());
    }
}
