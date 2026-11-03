using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
            ProtectedPrincipalRepository repo, DelineaService delinea, IServiceScopeFactory? scopeFactory,
            ILogger<ProtectedPrincipalService>? logger = null)
            : base(env, config, moduleConfig, repo, delinea, logger ?? NullLogger<ProtectedPrincipalService>.Instance, scopeFactory)
        {
            OnPremisesSidSource = new ScriptedSidSource(this);
        }

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

        // docs/ProtectedPrincipalIdentifiers-Plan.md C.2. What Graph reports as each recipient's on-premises SID, by
        // ExternalDirectoryObjectId - null for "answered, none reported"; absent for "could not be asked".
        public readonly Dictionary<string, string?> GraphSids = new(StringComparer.OrdinalIgnoreCase);

        public readonly List<string> GraphReads = new();

        private readonly Dictionary<string, DirectorySidObject[]> _sidScript = new(StringComparer.Ordinal);

        public readonly List<string> SidLookups = new();

        public bool SidLookupFails;

        public ScriptedAdService GraphReports(string externalDirectoryObjectId, string? sid)
        {
            GraphSids[externalDirectoryObjectId] = sid;
            return this;
        }

        /// <summary>The objects the global catalog holds under <paramref name="sid"/>, each carrying it.</summary>
        public ScriptedAdService SidHeldBy(string sid, params ResolvedDirectoryPrincipal[] holders) =>
            SidReturns(sid, holders.Select(h => SidObject(h, sid)).ToArray());

        public ScriptedAdService SidReturns(string sid, params DirectorySidObject[] returned)
        {
            _sidScript[sid] = returned;
            return this;
        }

        public override Task<(bool answered, IReadOnlyList<DirectorySidObject> found)> FindObjectsBySidAsync(string sid)
        {
            SidLookups.Add(sid);
            if (SidLookupFails)
                return Task.FromResult((false, (IReadOnlyList<DirectorySidObject>)Array.Empty<DirectorySidObject>()));

            return Task.FromResult((true, (IReadOnlyList<DirectorySidObject>)(
                _sidScript.TryGetValue(sid, out var found) ? found : Array.Empty<DirectorySidObject>())));
        }
    }

    /// <summary>The service's SID source over <see cref="ScriptedAdService.GraphSids"/>.</summary>
    private sealed class ScriptedSidSource(ScriptedAdService service) : IOnPremisesSidSource
    {
        public string Name => "Graph";

        public Task<OnPremisesSidAnswer> ReadAsync(string externalDirectoryObjectId)
        {
            service.GraphReads.Add(externalDirectoryObjectId);
            if (!service.GraphSids.TryGetValue(externalDirectoryObjectId, out var sid))
                return Task.FromResult(OnPremisesSidAnswer.NotAnswered("not scripted"));
            return Task.FromResult(sid is null
                ? new OnPremisesSidAnswer(OnPremisesSidOutcome.NoneReported)
                : new OnPremisesSidAnswer(OnPremisesSidOutcome.Reported, sid));
        }
    }

    private static DirectorySidObject SidObject(ResolvedDirectoryPrincipal p, string sid, string objectClass = "user") =>
        new(p.DistinguishedName, p.ObjectGuid, sid, objectClass, p.SamAccountName, p.UserPrincipalName, p.PrimarySmtpAddress, p.DisplayName);

    private static ResolvedDirectoryPrincipal AdPrincipal(string address, string? dn = "CN=x,OU=Users,DC=contoso,DC=com")
        => new("ProtectedPrincipalService-AD", address, address, address.Split('@')[0], address, dn, "guid", null);

    /// <param name="resolver">null registers no IIdentityResolver at all.</param>
    /// <param name="withScopeFactory">false constructs the service the way the direct-construction
    /// test files do, with no scope factory available.</param>
    private ScriptedAdService CreateService(IIdentityResolver? resolver, bool withScopeFactory = true,
        ILogger<ProtectedPrincipalService>? logger = null)
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
            TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea, scopeFactory, logger);
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
    public async Task AliasReResolution_CanonicalAddressUnknownToAd_IsUnavailable_NotNotFound()
    {
        // aud-a2b. Exchange says the recipient is synced and names its canonical address, and AD
        // holds no object under it: the directories disagree. Passing AD's NotFound through let
        // the gates that allow on NotFound (Conference Rooms, Group Management) proceed against a
        // synced recipient whose group and OU rules were never evaluated.
        var service = CreateService(ResolverReturning(
            new ResolvedRecipient("vip@contoso.com", "ext-vip", "UserMailbox", ExistsOnPrem: true)));
        service.AdReturns("vip.alias@contoso.com", ResolutionStatus.NotFound);
        service.AdReturns("vip@contoso.com", ResolutionStatus.NotFound);

        var (principal, status) = await service.ResolveWithExchangeFallbackAsync("vip.alias@contoso.com");

        Assert.Equal(ResolutionStatus.Unavailable, status);
        Assert.Null(principal);
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
    public async Task CloudOnlyRecipient_AddressedByAlias_StillResolves()
    {
        // Review finding ppv-1. A cloud-only mailbox can carry secondary aliases, so "is an alias"
        // and "is cloud-only" are independent. Branching on address equality first sent this
        // intersection down the alias path, where re-querying AD necessarily returned NotFound -
        // and the gates that allow on NotFound then let the operation through. Protection must not
        // depend on which of a principal's addresses the operator typed.
        var service = CreateService(ResolverReturning(
            new ResolvedRecipient("vip.cloud@contoso.com", "ext-vip", "UserMailbox", ExistsOnPrem: false)));
        service.AdReturns("vip.alias@contoso.com", ResolutionStatus.NotFound);
        service.AdReturns("vip.cloud@contoso.com", ResolutionStatus.NotFound);

        var (principal, status) = await service.ResolveWithExchangeFallbackAsync("vip.alias@contoso.com");

        Assert.Equal(ResolutionStatus.Resolved, status);
        Assert.NotNull(principal);
        // The CANONICAL address, not the alias that was typed - that is what the protected user
        // rows are stored as and matched against.
        Assert.Equal("vip.cloud@contoso.com", principal!.PrimarySmtpAddress);
        Assert.Equal("vip.cloud@contoso.com", principal.UserPrincipalName);
        Assert.Null(principal.DistinguishedName);
    }

    [Fact]
    public async Task CloudOnlyRecipient_AddressedByAlias_IsProtectedByTheUserRow()
    {
        // The end-to-end consequence of ppv-1: the same principal must be protected whether it is
        // addressed by its primary address or by an alias.
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
        // K.3: the user row is compared by its user_sid row - here, a cloud-only target carrying no SID, by that row's label.
        service.SaveConfig(new ProtectedPrincipalConfig { Users = ["vip.cloud@contoso.com"], UserIds = [ProtectedPrincipalIdEntry.Format("S-1-5-21-1000-2000-3000-1199", "vip.cloud@contoso.com")] });
        service.AdReturns("vip.alias@contoso.com", ResolutionStatus.NotFound);

        var (principal, status) = await service.ResolveWithExchangeFallbackAsync("vip.alias@contoso.com");
        Assert.Equal(ResolutionStatus.Resolved, status);

        var verdict = await service.CheckAsync(principal!);

        Assert.True(verdict.IsProtected);
        Assert.Contains("User:vip.cloud@contoso.com", verdict.MatchedRules);
    }

    [Fact]
    public async Task ExchangeSaysSyncedButAdMissedIt_IsUnavailable_NotCloudOnly()
    {
        // Adjacent case the ppv-1 restructure had to define: Exchange reports the recipient as
        // directory-synced, yet AD missed it under that same address. The directories disagree, so
        // nothing is established. Treating it as cloud-only would hand back a null-DN principal
        // and silently skip the group and OU rules a synced object IS subject to - an
        // un-protecting guess. Fail closed instead.
        var service = CreateService(ResolverReturning(
            new ResolvedRecipient("synced@contoso.com", null, "UserMailbox", ExistsOnPrem: true)));
        service.AdReturns("synced@contoso.com", ResolutionStatus.NotFound);

        var (principal, status) = await service.ResolveWithExchangeFallbackAsync("synced@contoso.com");

        Assert.Equal(ResolutionStatus.Unavailable, status);
        Assert.Null(principal);
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
        // K.3: the user row is compared by its user_sid row - here, a cloud-only target carrying no SID, by that row's label.
        service.SaveConfig(new ProtectedPrincipalConfig { Users = ["vip.cloud@contoso.com"], UserIds = [ProtectedPrincipalIdEntry.Format("S-1-5-21-1000-2000-3000-1199", "vip.cloud@contoso.com")] });
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

    // ---- aud-a2b hole 2: ResolveExchangeTargetAsync --------------------------
    // For a write Exchange performs with the typed string. Exchange is asked first and AD must
    // agree with it; the object checked must be the object Exchange will write. Since
    // docs/ProtectedPrincipalIdentifiers-Plan.md C.2 a synced recipient is bound to the one account
    // holding the on-premises SID Graph reports for it.

    private static ResolvedDirectoryPrincipal AdObject(string upn, string sam, string? mail, string guid, string dn)
        => new("ProtectedPrincipalService-AD", sam, upn, sam, mail, dn, guid, null);

    private static readonly ResolvedDirectoryPrincipal UnrelatedAccount =
        AdObject("svc-vip@contoso.com", "vip", null, "guid-unrelated", "CN=svc-vip,OU=Service,DC=contoso,DC=com");

    private static readonly ResolvedDirectoryPrincipal VipMailboxUser =
        AdObject("vip.person@contoso.com", "vperson", "vip.person@contoso.com", "guid-vip", "CN=VIP Person,OU=Execs,DC=contoso,DC=com");

    private const string VipSid = "S-1-5-21-1000-2000-3000-1101";
    private static readonly Guid VipMailboxGuid = Guid.Parse("6f1d7c9e-2b4a-4e0f-9a3d-5c8b1e7f2a40");

    private static ResolvedRecipient SyncedVipRecipient()
        => new("vip.person@contoso.com", "ext-vip", "UserMailbox", ExistsOnPrem: true, ExchangeGuid: VipMailboxGuid);

    /// <summary>The protected mailbox's own AD object: its <c>mail</c> is empty and its UPN differs from the primary address.</summary>
    private static readonly ResolvedDirectoryPrincipal VipBackingObject =
        AdObject("vperson@corp.contoso.com", "vperson", null, "guid-vip", "CN=VIP Person,OU=Execs,DC=contoso,DC=com");

    /// <summary>Not a recipient: an admin account whose <c>mail</c> points at the VIP's address,
    /// which AD does not require to be unique.</summary>
    private static readonly ResolvedDirectoryPrincipal AddressNamesake =
        AdObject("a-vperson@corp.contoso.com", "a-vperson", "vip.person@contoso.com", "guid-namesake",
            "CN=a-vperson,OU=Admins,DC=contoso,DC=com");

    /// <summary>The VIP as Graph and the catalog know it: Graph reports VipSid, the catalog holds <paramref name="holder"/> under it.</summary>
    private ScriptedAdService VipService(ResolvedDirectoryPrincipal? holder = null)
    {
        var service = CreateService(ResolverReturning(SyncedVipRecipient()));
        service.GraphReports("ext-vip", VipSid).SidHeldBy(VipSid, holder ?? VipMailboxUser);
        return service;
    }

    [Fact]
    public async Task ExchangeTarget_AnAdAccountSharingTheString_DoesNotStandInForTheMailbox()
    {
        // The finding. "vip" is the sAMAccountName of an unrelated, non-mail AD account and the
        // Exchange alias of the protected VIP mailbox. AD alone answers with the account; the
        // write would land on the mailbox. The two directories name different objects.
        var service = VipService();
        service.AdReturns("vip", ResolutionStatus.Resolved, UnrelatedAccount);

        var (principal, status) = await service.ResolveExchangeTargetAsync("vip");

        Assert.Equal(ResolutionStatus.Ambiguous, status);
        Assert.Null(principal);

        // The AD-first resolution, for contrast, accepts the unrelated account - which is the
        // bypass this entry point exists to close for Exchange-written targets.
        var (adFirst, adFirstStatus) = await service.ResolveWithExchangeFallbackAsync("vip");
        Assert.Equal(ResolutionStatus.Resolved, adFirstStatus);
        Assert.Equal("guid-unrelated", adFirst!.ObjectGuid);
    }

    [Fact]
    public async Task ExchangeTarget_AgreeingDirectories_ResolveToTheMailboxUser()
    {
        // The ordinary case: the typed UPN, AD's reading of it and the account holding the SID are one object.
        var service = VipService();
        service.AdReturns("vip.person@contoso.com", ResolutionStatus.Resolved, VipMailboxUser);

        var (principal, status) = await service.ResolveExchangeTargetAsync("vip.person@contoso.com");

        Assert.Equal(ResolutionStatus.Resolved, status);
        Assert.Equal("guid-vip", principal!.ObjectGuid);
        Assert.Equal(VipSid, principal.ObjectSid);
        Assert.Equal(VipMailboxGuid, principal.ExchangeGuid);
        Assert.Equal(new[] { "ext-vip" }, service.GraphReads);
        Assert.Equal(new[] { VipSid }, service.SidLookups);
    }

    [Fact]
    public async Task ExchangeTarget_AnAliasAdDoesNotKnow_ResolvesToTheObjectExchangeWillWrite()
    {
        var service = VipService();
        service.AdReturns("vip.alias@contoso.com", ResolutionStatus.NotFound);

        var (principal, status) = await service.ResolveExchangeTargetAsync("vip.alias@contoso.com");

        Assert.Equal(ResolutionStatus.Resolved, status);
        Assert.Equal("guid-vip", principal!.ObjectGuid);
        // Bound by SID: the canonical address is never re-read in AD.
        Assert.Equal(new[] { "vip.alias@contoso.com" }, service.AdLookups);
    }

    [Fact]
    public async Task ExchangeTarget_ACloudOnlyRecipientWithAnAdNamesake_IsAmbiguous()
    {
        // Exchange will write a cloud-only mailbox; AD reads the same string as an on-premises
        // object. Two objects - checking either alone checks the wrong one half the time.
        var service = CreateService(ResolverReturning(
            new ResolvedRecipient("vip.cloud@contoso.com", "ext-cloud", "UserMailbox", ExistsOnPrem: false)));
        service.AdReturns("vip", ResolutionStatus.Resolved, UnrelatedAccount);

        var (_, status) = await service.ResolveExchangeTargetAsync("vip");

        Assert.Equal(ResolutionStatus.Ambiguous, status);
    }

    [Fact]
    public async Task ExchangeTarget_ACloudOnlyRecipient_ResolvesToItsCanonicalAddress_AndAsksNoSidSource()
    {
        var service = CreateService(ResolverReturning(
            new ResolvedRecipient("vip.cloud@contoso.com", "ext-cloud", "UserMailbox", ExistsOnPrem: false)));
        service.AdReturns("vip.cloud.alias@contoso.com", ResolutionStatus.NotFound);

        var (principal, status) = await service.ResolveExchangeTargetAsync("vip.cloud.alias@contoso.com");

        Assert.Equal(ResolutionStatus.Resolved, status);
        Assert.Equal("vip.cloud@contoso.com", principal!.PrimarySmtpAddress);
        Assert.Null(principal.DistinguishedName);
        Assert.Empty(service.GraphReads);
        Assert.Empty(service.SidLookups);
    }

    [Fact]
    public async Task ExchangeTarget_ExchangeCannotBeAsked_IsUnavailable_EvenWhenAdAnswers()
    {
        // The AD answer alone is exactly what this entry point refuses to trust.
        var service = CreateService(ResolverThrowing(new InvalidOperationException("Connecting to remote server failed")));
        service.AdReturns("vip", ResolutionStatus.Resolved, UnrelatedAccount);

        var (principal, status) = await service.ResolveExchangeTargetAsync("vip");

        Assert.Equal(ResolutionStatus.Unavailable, status);
        Assert.Null(principal);
    }

    [Fact]
    public async Task ExchangeTarget_NoSuchRecipient_FallsBackToTheAdObject()
    {
        // An Exchange write cannot land on a recipient Exchange does not have, so what AD resolved
        // is the only object the string names.
        var service = CreateService(ResolverReturning(null));
        service.AdReturns("vperson", ResolutionStatus.Resolved, VipMailboxUser);

        var (principal, status) = await service.ResolveExchangeTargetAsync("vperson");

        Assert.Equal(ResolutionStatus.Resolved, status);
        Assert.Equal("guid-vip", principal!.ObjectGuid);
    }

    [Fact]
    public async Task ExchangeTarget_NeitherDirectory_IsNotFound()
    {
        var service = CreateService(ResolverReturning(null));
        service.AdReturns("typo@contoso.com", ResolutionStatus.NotFound);

        var (_, status) = await service.ResolveExchangeTargetAsync("typo@contoso.com");

        Assert.Equal(ResolutionStatus.NotFound, status);
    }

    [Theory]
    [InlineData(ResolutionStatus.Ambiguous)]
    [InlineData(ResolutionStatus.Unavailable)]
    public async Task ExchangeTarget_AdThatCannotAnswerCleanly_Refuses(ResolutionStatus adStatus)
    {
        var service = VipService();
        service.AdReturns("vip", adStatus);

        var (_, status) = await service.ResolveExchangeTargetAsync("vip");

        Assert.Equal(adStatus, status);
    }

    [Fact]
    public void IsSameDirectoryObject_ComparesGuidsFirst_ThenDns_AndNeverAssumes()
    {
        var a = AdObject("a@x", "a", null, "g1", "CN=A,DC=x");
        Assert.True(IsSameDirectoryObject(a, a with { DistinguishedName = "CN=Moved,DC=x" }));
        Assert.False(IsSameDirectoryObject(a, a with { ObjectGuid = "g2" }));
        Assert.True(IsSameDirectoryObject(a with { ObjectGuid = null }, a with { ObjectGuid = null }));
        Assert.False(IsSameDirectoryObject(
            a with { ObjectGuid = null, DistinguishedName = null },
            a with { ObjectGuid = null, DistinguishedName = null }));
    }

    // ---- C.2: a synced recipient is bound by its on-premises SID ------------------
    // aud-a2c found the AD object by the recipient's canonical address through mail / UPN; an
    // account with no mailbox can carry that address, and was then the object checked while
    // Exchange wrote the mailbox. The mailbox-GUID proof that closed it is retired: the SID Graph
    // reports names the one account the mailbox is synced from.

    [Fact]
    public async Task ExchangeTarget_AnotherAccountCarryingTheAddress_IsNeverTheObjectChecked_TheSidsAccountIs()
    {
        // The aud-a2c scenario. AD's only object under the canonical address is the admin account
        // carrying it in mail; the mailbox's own account has no mail and another UPN. Bound by
        // address, the admin account is checked; bound by SID, the mailbox's own account is.
        var service = VipService(holder: VipBackingObject);
        service.AdReturns("vip.alias@contoso.com", ResolutionStatus.NotFound);
        service.AdReturns("vip.person@contoso.com", ResolutionStatus.Resolved, AddressNamesake);

        var (principal, status) = await service.ResolveExchangeTargetAsync("vip.alias@contoso.com");

        Assert.Equal(ResolutionStatus.Resolved, status);
        Assert.Equal("guid-vip", principal!.ObjectGuid);
        Assert.Equal(VipBackingObject.DistinguishedName, principal.DistinguishedName);
        Assert.Equal(VipSid, principal.ObjectSid);
        Assert.Equal(VipMailboxGuid, principal.ExchangeGuid);
    }

    [Fact]
    public async Task ExchangeTarget_TypedAsTheAddressAnotherAccountCarries_IsAmbiguous()
    {
        // The same namesake, typed as the address it carries: AD reads the typed string as the
        // admin account, the SID names the mailbox's account - two objects (aud-a2b, unchanged).
        var service = VipService(holder: VipBackingObject);
        service.AdReturns("vip.person@contoso.com", ResolutionStatus.Resolved, AddressNamesake);

        var (principal, status) = await service.ResolveExchangeTargetAsync("vip.person@contoso.com");

        Assert.Equal(ResolutionStatus.Ambiguous, status);
        Assert.Null(principal);
    }

    [Fact]
    public async Task ExchangeTarget_NoObjectHoldsTheSid_IsUnavailable()
    {
        var service = CreateService(ResolverReturning(SyncedVipRecipient()));
        service.GraphReports("ext-vip", VipSid);
        service.AdReturns("vip.person@contoso.com", ResolutionStatus.Resolved, VipMailboxUser);

        var (principal, status) = await service.ResolveExchangeTargetAsync("vip.person@contoso.com");

        Assert.Equal(ResolutionStatus.Unavailable, status);
        Assert.Null(principal);
        Assert.Equal(new[] { VipSid }, service.SidLookups);
    }

    [Fact]
    public async Task ExchangeTarget_TwoObjectsHoldTheSid_IsAmbiguous()
    {
        var service = CreateService(ResolverReturning(SyncedVipRecipient()));
        service.GraphReports("ext-vip", VipSid).SidHeldBy(VipSid, VipMailboxUser, AddressNamesake);
        service.AdReturns("vip.alias@contoso.com", ResolutionStatus.NotFound);

        var (principal, status) = await service.ResolveExchangeTargetAsync("vip.alias@contoso.com");

        Assert.Equal(ResolutionStatus.Ambiguous, status);
        Assert.Null(principal);
    }

    [Theory]
    [InlineData("computer")]
    [InlineData("group")]
    [InlineData("contact")]
    public async Task ExchangeTarget_TheOneObjectHoldingTheSidIsNotAUser_IsAmbiguous(string objectClass)
    {
        var service = CreateService(ResolverReturning(SyncedVipRecipient()));
        service.GraphReports("ext-vip", VipSid).SidReturns(VipSid, SidObject(VipMailboxUser, VipSid, objectClass));
        service.AdReturns("vip.alias@contoso.com", ResolutionStatus.NotFound);

        var (principal, status) = await service.ResolveExchangeTargetAsync("vip.alias@contoso.com");

        Assert.Equal(ResolutionStatus.Ambiguous, status);
        Assert.Null(principal);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("inetOrgPerson")]
    public async Task ExchangeTarget_AUserObjectHoldingTheSid_Resolves(string objectClass)
    {
        var service = CreateService(ResolverReturning(SyncedVipRecipient()));
        service.GraphReports("ext-vip", VipSid).SidReturns(VipSid, SidObject(VipMailboxUser, VipSid, objectClass));
        service.AdReturns("vip.alias@contoso.com", ResolutionStatus.NotFound);

        var (_, status) = await service.ResolveExchangeTargetAsync("vip.alias@contoso.com");

        Assert.Equal(ResolutionStatus.Resolved, status);
    }

    [Fact]
    public async Task ExchangeTarget_TheReadBySidCannotRun_IsUnavailable()
    {
        var service = VipService();
        service.AdReturns("vip.alias@contoso.com", ResolutionStatus.NotFound);
        service.SidLookupFails = true;

        var (principal, status) = await service.ResolveExchangeTargetAsync("vip.alias@contoso.com");

        Assert.Equal(ResolutionStatus.Unavailable, status);
        Assert.Null(principal);
    }

    [Fact]
    public async Task ExchangeTarget_TheSidSourceUnanswered_IsUnavailable_NotTheAddressFallback()
    {
        // C.2 step 6: Graph could not be asked (no setting, an error, a throttle). The canonical
        // address names the mailbox's account in AD, and that is still not used: "could not ask"
        // is never "no SID".
        var service = CreateService(ResolverReturning(SyncedVipRecipient()));
        service.AdReturns("vip.alias@contoso.com", ResolutionStatus.NotFound);
        service.AdReturns("vip.person@contoso.com", ResolutionStatus.Resolved, VipMailboxUser);

        var (principal, status) = await service.ResolveExchangeTargetAsync("vip.alias@contoso.com");

        Assert.Equal(ResolutionStatus.Unavailable, status);
        Assert.Null(principal);
        Assert.Equal(new[] { "ext-vip" }, service.GraphReads);
        Assert.Equal(new[] { "vip.alias@contoso.com" }, service.AdLookups);
        Assert.Empty(service.SidLookups);
    }

    [Fact]
    public async Task ExchangeTarget_AServiceWithNoSidSource_IsUnavailable()
    {
        var service = CreateService(ResolverReturning(SyncedVipRecipient()));
        service.OnPremisesSidSource = null;
        service.AdReturns("vip.person@contoso.com", ResolutionStatus.Resolved, VipMailboxUser);

        var (_, status) = await service.ResolveExchangeTargetAsync("vip.person@contoso.com");

        Assert.Equal(ResolutionStatus.Unavailable, status);
    }

    [Fact]
    public async Task ExchangeTarget_GraphAnsweredWithNoSid_BindsByAddress_WithItsOneMatchRule_AndSaysSo()
    {
        // C.2 step 5. Graph answered and reports no on-premises SID: the canonical address, one match.
        var logger = new CapturingLogger();
        var service = CreateService(ResolverReturning(SyncedVipRecipient()), logger: logger);
        service.GraphReports("ext-vip", null);
        service.AdReturns("vip.alias@contoso.com", ResolutionStatus.NotFound);
        service.AdReturns("vip.person@contoso.com", ResolutionStatus.Resolved, VipMailboxUser);

        var (principal, status) = await service.ResolveExchangeTargetAsync("vip.alias@contoso.com");

        Assert.Equal(ResolutionStatus.Resolved, status);
        Assert.Equal("guid-vip", principal!.ObjectGuid);
        Assert.Equal(VipMailboxGuid, principal.ExchangeGuid);
        Assert.Empty(service.SidLookups);
        Assert.Contains("no on-premises SID reported for vip.alias@contoso.com; bound by address", logger.Messages);

        // Its one-match rule: two objects under the address refuse; none is the directories disagreeing.
        service.AdReturns("vip.person@contoso.com", ResolutionStatus.Ambiguous);
        Assert.Equal(ResolutionStatus.Ambiguous, (await service.ResolveExchangeTargetAsync("vip.alias@contoso.com")).status);
        service.AdReturns("vip.person@contoso.com", ResolutionStatus.NotFound);
        Assert.Equal(ResolutionStatus.Unavailable, (await service.ResolveExchangeTargetAsync("vip.alias@contoso.com")).status);
    }

    [Fact]
    public async Task ExchangeTarget_TheBoundMailbox_IsCheckedAsItself()
    {
        // End to end: the principal bound by SID reaches CheckAsync with its DN and objectGUID intact, so
        // a mailbox protected by objectGUID is still protected when reached by an alias.
        var json = System.Text.Json.JsonSerializer.Serialize(new
        {
            ProtectedPrincipals = new
            {
                Users = new[] { "guid-vip" },
                Groups = Array.Empty<string>(),
                OrganizationalUnits = Array.Empty<string>(),
                SamAccountNamePatterns = Array.Empty<string>()
            }
        });
        File.WriteAllText(Path.Combine(_tempDir, "config", "protected-principals.json"), json);

        var service = VipService();
        // K.3: compared by the user_sid row db convert-identifiers wrote for it - the bound principal carries its SID.
        service.SaveConfig(new ProtectedPrincipalConfig { Users = ["guid-vip"], UserIds = [ProtectedPrincipalIdEntry.Format(VipSid, "guid-vip")] });
        service.AdReturns("vip.alias@contoso.com", ResolutionStatus.NotFound);

        var (principal, status) = await service.ResolveExchangeTargetAsync("vip.alias@contoso.com");
        Assert.Equal(ResolutionStatus.Resolved, status);

        var verdict = await service.CheckAsync(principal!);

        Assert.True(verdict.IsProtected);
        Assert.Contains("User:guid-vip", verdict.MatchedRules);
    }

    [Fact]
    public async Task ExchangeTarget_ACloudOnlyRecipient_CarriesExchangesOwnMailboxGuid()
    {
        var cloudGuid = Guid.Parse("0b7f3a52-91c4-4d8e-8f21-6a5e3c9d7b10");
        var service = CreateService(ResolverReturning(
            new ResolvedRecipient("vip.cloud@contoso.com", "ext-cloud", "UserMailbox", ExistsOnPrem: false, ExchangeGuid: cloudGuid)));
        service.AdReturns("vip.cloud.alias@contoso.com", ResolutionStatus.NotFound);

        var (principal, status) = await service.ResolveExchangeTargetAsync("vip.cloud.alias@contoso.com");

        Assert.Equal(ResolutionStatus.Resolved, status);
        Assert.Equal(cloudGuid, principal!.ExchangeGuid);
        Assert.Empty(service.SidLookups);
    }

    [Fact]
    public void SelectSidObject_AnObjectWithoutItsDnOrGuid_OrUnderAnotherSid_IsUnavailable()
    {
        var recipient = SyncedVipRecipient();
        Assert.Equal(ResolutionStatus.Unavailable, SelectSidObject(VipSid, recipient,
            [SidObject(VipMailboxUser with { ObjectGuid = null }, VipSid)]).status);
        Assert.Equal(ResolutionStatus.Unavailable, SelectSidObject(VipSid, recipient,
            [SidObject(VipMailboxUser with { DistinguishedName = null }, VipSid)]).status);
        Assert.Equal(ResolutionStatus.Unavailable, SelectSidObject(VipSid, recipient,
            [SidObject(VipMailboxUser, "S-1-5-21-1000-2000-3000-1102")]).status);
        Assert.Equal(ResolutionStatus.Resolved, SelectSidObject(VipSid, recipient, [SidObject(VipMailboxUser, VipSid)]).status);
    }

    // ---- C.2: the AD-first entry point's alias branch -------------------------------

    [Fact]
    public async Task Fallback_AnAliasOfASyncedMailbox_IsBoundBySid_WhenGraphReportsOne()
    {
        var service = VipService(holder: VipBackingObject);
        service.AdReturns("vip.alias@contoso.com", ResolutionStatus.NotFound);
        service.AdReturns("vip.person@contoso.com", ResolutionStatus.Resolved, AddressNamesake);

        var (principal, status) = await service.ResolveWithExchangeFallbackAsync("vip.alias@contoso.com");

        Assert.Equal(ResolutionStatus.Resolved, status);
        Assert.Equal("guid-vip", principal!.ObjectGuid);
        Assert.Equal(VipSid, principal.ObjectSid);
        // The mailbox GUID stays ResolveExchangeTargetAsync's alone (aud-a2d).
        Assert.Null(principal.ExchangeGuid);
        Assert.Equal(new[] { "vip.alias@contoso.com" }, service.AdLookups);
    }

    [Fact]
    public async Task Fallback_AnAliasOfASyncedMailbox_WithGraphUnanswered_ReResolvesTheAddressAsBefore()
    {
        var service = CreateService(ResolverReturning(SyncedVipRecipient()));
        service.AdReturns("vip.alias@contoso.com", ResolutionStatus.NotFound);
        service.AdReturns("vip.person@contoso.com", ResolutionStatus.Resolved, VipMailboxUser);

        var (principal, status) = await service.ResolveWithExchangeFallbackAsync("vip.alias@contoso.com");

        Assert.Equal(ResolutionStatus.Resolved, status);
        Assert.Equal("guid-vip", principal!.ObjectGuid);
        Assert.Equal(new[] { "vip.alias@contoso.com", "vip.person@contoso.com" }, service.AdLookups);
    }

    /// <summary>Every message the service logged, rendered.</summary>
    private sealed class CapturingLogger : ILogger<ProtectedPrincipalService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
                Messages.Add(formatter(state, exception));
        }
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
