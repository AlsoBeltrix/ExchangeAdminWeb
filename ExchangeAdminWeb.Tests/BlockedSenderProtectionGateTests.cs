using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The unblock protection decision (docs/ProtectedPrincipalGapFix-Plan.md GAP A).
/// </summary>
/// <remarks>
/// These replace the source-scanning tripwires the first cut of this fix shipped with. Those
/// asserted that a gate call APPEARED in the razor handler and preceded the write, which review
/// correctly called weak: they would pass if the gate were called on the wrong variable, or if its
/// result were discarded. What matters is the decision, so the decision moved into a service and
/// is asserted directly here. A narrow placement test remains in
/// <see cref="PageAuthorizationRecheckTests"/> for the handler wiring only.
///
/// Every branch below is a case where getting it wrong either lets a protected principal be
/// unblocked, or blocks an address the module exists to clear.
/// </remarks>
public class BlockedSenderProtectionGateTests
{
    private const string Address = "spammer@contoso.com";

    /// <summary>
    /// Scripts resolution and the protection check without a directory. Mirrors the seam
    /// <see cref="PermissionValidatorTests"/> uses - the same virtual members exist for the same
    /// reason.
    /// </summary>
    private sealed class ScriptedPpService : ProtectedPrincipalService
    {
        public ProtectedPrincipalService.ResolutionStatus Status = ResolutionStatus.Resolved;
        public bool ResolveThrows;
        public bool ReturnNullOnResolved;
        public string? LoadError;
        public ProtectedPrincipalResult? CheckResult;
        public string? LastResolvedIdentity;

        public override bool HasCentralConfig => true;

        public ScriptedPpService(IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
            ExchangeAdminWeb.Services.Storage.ProtectedPrincipalRepository repo, DelineaService delinea)
            : base(env, config, moduleConfig, repo, delinea, Substitute.For<ILogger<ProtectedPrincipalService>>())
        { }

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithExchangeFallbackAsync(string identity)
        {
            LastResolvedIdentity = identity;

            if (ResolveThrows)
                throw new InvalidOperationException("directory blew up");

            ResolvedDirectoryPrincipal? p = Status == ResolutionStatus.Resolved && !ReturnNullOnResolved
                ? new ResolvedDirectoryPrincipal("Test", identity, identity, "sam", identity, "CN=x,DC=y", "guid", null)
                : null;

            return Task.FromResult((p, Status));
        }

        /// <summary>Set false to model a deployment with no protection rules at all.</summary>
        public bool HasRules = true;

        public override (ProtectedPrincipalConfig? config, string[] legacyExclusions, string? error) LoadEffectiveConfig()
        {
            if (LoadError is not null)
                return (null, Array.Empty<string>(), LoadError);

            // Rules present by default. The gate skips resolution entirely when BOTH the central
            // config and the legacy list are empty, so a fake returning an empty config would make
            // every resolution and protection test below vacuous - they would pass without the
            // resolver ever being called.
            var config = HasRules
                ? new ProtectedPrincipalConfig { Users = ["someone@contoso.com"] }
                : new ProtectedPrincipalConfig();

            return (config, LegacyExclusions, null);
        }

        /// <summary>The legacy MailboxPermissions/ExcludedUsers list.</summary>
        public string[] LegacyExclusions = Array.Empty<string>();

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target)
            => Task.FromResult(CheckResult ?? ProtectedPrincipalResult.NotProtected());
    }

    private static (BlockedSenderProtectionGate gate, ScriptedPpService pp) Create()
    {
        var testDir = Path.Combine(Path.GetTempPath(), "eaw-bsgate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = testDir
        }).Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(testDir);

        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env,
            TestConfigStore.CreateModuleConfig(testDir), Substitute.For<ILogger<ModuleConfigService>>());

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(testDir), Substitute.For<ILogger<ExtendedLogService>>());
        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var trace = new OperationTraceService(config, jsonlLog);
        var delinea = new DelineaService(httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extLog, trace);

        var pp = new ScriptedPpService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(testDir), delinea);
        var gate = new BlockedSenderProtectionGate(pp, Substitute.For<ILogger<BlockedSenderProtectionGate>>());

        return (gate, pp);
    }

    // ---- The case the whole fix exists for --------------------------------------------------

    [Fact]
    public async Task AProtectedPrincipalIsRefused()
    {
        var (gate, pp) = Create();
        pp.CheckResult = ProtectedPrincipalResult.Protected("protected", "User:ceo@contoso.com");

        var decision = await gate.EvaluateAsync("ceo@contoso.com");

        Assert.True(decision.Denied);
        Assert.Equal(BlockedSenderProtectionGate.ProtectedMessage, decision.Reason);
    }

    [Fact]
    public async Task AnOrdinaryAddressIsAllowed()
    {
        // The permissive path, asserted so the denial tests are not passing merely because the
        // gate denies everything.
        var (gate, _) = Create();

        var decision = await gate.EvaluateAsync(Address);

        Assert.False(decision.Denied);
        Assert.Null(decision.Reason);
    }

    // ---- Resolution always goes through Exchange ---------------------------------------------

    [Fact]
    public async Task ResolutionUsesTheExchangeFallback_NotAnAdOnlyLookup()
    {
        // The defect review caught in the first cut: routing through
        // PermissionValidator.ValidateTargetMailboxAsync skips Exchange resolution entirely when
        // the protection config holds only address-form user rows, so an alias-addressed protected
        // principal is compared literally and never normalized to its primary address. This gate
        // must resolve through Exchange unconditionally - the scripted resolver records that the
        // fallback overload was the one called.
        var (gate, pp) = Create();

        await gate.EvaluateAsync("alias@contoso.com");

        Assert.Equal("alias@contoso.com", pp.LastResolvedIdentity);
    }

    // ---- Status policy, one test per outcome -------------------------------------------------

    [Fact]
    public async Task AnUnresolvableAddressIsStillCheckedAgainstProtectedRows()
    {
        // NotFound means no DIRECTORY object exists - not that no protected ROW names the address.
        // Protected user rows are literal strings matched against UPN and SMTP address, so an
        // address someone deliberately protected must still be refused even when it resolves to
        // nothing. Review caught this: the module allowed it while MFA Reset checked it, for no
        // principled reason.
        var (gate, pp) = Create();
        pp.Status = ProtectedPrincipalService.ResolutionStatus.NotFound;
        pp.CheckResult = ProtectedPrincipalResult.Protected("protected", "User:ghost@contoso.com");

        var decision = await gate.EvaluateAsync("ghost@contoso.com");

        Assert.True(decision.Denied);
        Assert.Contains("directory-unresolved", decision.AuditDetail);
    }

    [Fact]
    public async Task AnUnresolvableAddressIsALLOWED_BecauseThereIsNothingToProtect()
    {
        // Deliberately different from the mailbox gate, which denies NotFound. A blocked sender is
        // routinely external, decommissioned or otherwise unresolvable - frequently the reason it
        // was blocked - and denying those would make the module unable to clear the entries it
        // exists to clear. NotFound from the fallback resolver means BOTH directories affirmatively
        // answered "no such recipient", so no principal exists to be protected.
        var (gate, pp) = Create();
        pp.Status = ProtectedPrincipalService.ResolutionStatus.NotFound;

        var decision = await gate.EvaluateAsync("long-gone@partner.example");

        Assert.False(decision.Denied);
    }

    [Fact]
    public async Task AnUnavailableDirectoryIsRefused()
    {
        // Known Failure Class #3: a directory that did not answer is not evidence of absence.
        var (gate, pp) = Create();
        pp.Status = ProtectedPrincipalService.ResolutionStatus.Unavailable;

        var decision = await gate.EvaluateAsync(Address);

        Assert.True(decision.Denied);
        Assert.Equal(BlockedSenderProtectionGate.UnavailableMessage, decision.Reason);
    }

    [Fact]
    public async Task AnAmbiguousAddressIsRefused()
    {
        var (gate, pp) = Create();
        pp.Status = ProtectedPrincipalService.ResolutionStatus.Ambiguous;

        var decision = await gate.EvaluateAsync(Address);

        Assert.True(decision.Denied);
        Assert.Equal(BlockedSenderProtectionGate.AmbiguousMessage, decision.Reason);
    }

    // ---- Fail-closed on everything that is not an answer -------------------------------------

    [Fact]
    public async Task AConfigThatCannotBeReadRefuses()
    {
        // An unreadable protection config says nothing about whether this address is protected.
        var (gate, pp) = Create();
        pp.LoadError = "protected-principals store is corrupt";

        var decision = await gate.EvaluateAsync(Address);

        Assert.True(decision.Denied);
        Assert.Contains("corrupt", decision.Reason);
    }

    [Fact]
    public async Task AFailedProtectionCheckRefuses()
    {
        // The check ran but could not evaluate a rule. An unevaluated rule is not a passed rule.
        var (gate, pp) = Create();
        pp.CheckResult = ProtectedPrincipalResult.Failed("group membership unavailable");

        var decision = await gate.EvaluateAsync(Address);

        Assert.True(decision.Denied);
        Assert.Contains("group membership unavailable", decision.Reason);
    }

    [Fact]
    public async Task AThrowingResolverRefuses()
    {
        var (gate, pp) = Create();
        pp.ResolveThrows = true;

        var decision = await gate.EvaluateAsync(Address);

        Assert.True(decision.Denied);
        Assert.Equal(BlockedSenderProtectionGate.UnavailableMessage, decision.Reason);
    }

    [Fact]
    public async Task ResolvedWithNoPrincipalRefuses()
    {
        // Not a documented resolver state. An unexpected shape must never become an allow.
        var (gate, pp) = Create();
        pp.ReturnNullOnResolved = true;

        var decision = await gate.EvaluateAsync(Address);

        Assert.True(decision.Denied);
    }

    // ---- The legacy exclusion list is protection data too -------------------------------------

    [Fact]
    public async Task LegacyExclusionsAreStillEvaluated_WhenThereIsNoCentralConfig()
    {
        // Review finding: an earlier version returned Allow as soon as HasCentralConfig was false,
        // which skipped the legacy MailboxPermissions/ExcludedUsers list entirely. That list is
        // live protection data, and "nothing configured centrally" is precisely the deployment
        // where it is the ONLY protection - so the short-circuit un-protected exactly the
        // installations relying on it.
        var (gate, pp) = Create();
        pp.HasRules = false;
        pp.LegacyExclusions = ["legacy-protected@contoso.com"];
        pp.CheckResult = ProtectedPrincipalResult.Protected("protected", "Legacy:legacy-protected@contoso.com");

        var decision = await gate.EvaluateAsync("legacy-protected@contoso.com");

        Assert.True(decision.Denied);
    }

    [Fact]
    public async Task NoRulesAnywhereSkipsResolutionEntirely()
    {
        // The one legitimate short-circuit: both sources read successfully and both are empty, so
        // no directory round-trip can change the answer.
        var (gate, pp) = Create();
        pp.HasRules = false;
        pp.LegacyExclusions = Array.Empty<string>();

        var decision = await gate.EvaluateAsync(Address);

        Assert.False(decision.Denied);
        Assert.Null(pp.LastResolvedIdentity);
    }

    // ---- Audit detail names the rule; the banner does not -------------------------------------

    [Fact]
    public async Task ADenialAuditsTheMatchedRule_WhileTheBannerStaysGeneric()
    {
        // docs/BlockedSenders.md promises the Event Log detail names the protection rule. The
        // operator-facing banner deliberately does not: a reviewer needs to know WHY, whoever
        // typed the address does not need the rule set enumerated back at them.
        var (gate, pp) = Create();
        pp.CheckResult = ProtectedPrincipalResult.Protected("protected", "Group:VIPs", "User:ceo@contoso.com");

        var decision = await gate.EvaluateAsync("ceo@contoso.com");

        Assert.Equal(BlockedSenderProtectionGate.ProtectedMessage, decision.Reason);
        Assert.Contains("Group:VIPs", decision.AuditDetail);
        Assert.Contains("User:ceo@contoso.com", decision.AuditDetail);
        Assert.DoesNotContain("Group:VIPs", decision.Reason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ABlankAddressIsRefused(string address)
    {
        var (gate, _) = Create();

        Assert.True((await gate.EvaluateAsync(address)).Denied);
    }
}
