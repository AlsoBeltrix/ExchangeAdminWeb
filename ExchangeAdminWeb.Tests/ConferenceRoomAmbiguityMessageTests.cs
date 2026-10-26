using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a2aa: an ambiguous resolution is told as what it is. The gate said "matches multiple AD users" for every
/// <c>Ambiguous</c> status, which names the wrong thing for most of the ways the resolution reaches it since aud-a2b and
/// aud-a2c - Exchange Online and Active Directory naming different objects, or two directory objects of any class holding
/// the mailbox's GUID.
/// </summary>
/// <remarks>
/// The two shapes are driven through the REAL <see cref="ProtectedPrincipalService.ResolveExchangeTargetAsync"/>, with only
/// its two directory seams scripted - the Active Directory lookup and the Exchange resolver - so each test reaches
/// <c>Ambiguous</c> the way the live code reaches it, by a collision that involves no "AD users" at all.
/// </remarks>
public sealed class ConferenceRoomAmbiguityMessageTests : IDisposable
{
    private const string Typed = "boardroom";
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"cr-amb-{Guid.NewGuid():N}");

    public ConferenceRoomAmbiguityMessageTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>The real resolution with its Active Directory half scripted, as the aud-a2b and aud-a2c tests script it.</summary>
    private sealed class ScriptedAdService(
        IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
        ProtectedPrincipalRepository repo, DelineaService delinea, IServiceScopeFactory scopeFactory)
        : ProtectedPrincipalService(env, config, moduleConfig, repo, delinea, NullLogger<ProtectedPrincipalService>.Instance, scopeFactory)
    {
        private readonly Dictionary<string, (ResolvedDirectoryPrincipal?, ResolutionStatus)> _script = new(StringComparer.OrdinalIgnoreCase);

        public ScriptedAdService AdReturns(string identity, ResolutionStatus status, ResolvedDirectoryPrincipal? principal = null)
        {
            _script[identity] = (principal, status);
            return this;
        }

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithStatusAsync(string identity) =>
            Task.FromResult(_script.TryGetValue(identity, out var scripted) ? scripted : ((ResolvedDirectoryPrincipal?)null, ResolutionStatus.NotFound));
    }

    private static ResolvedDirectoryPrincipal Group(string name, string objectGuid) =>
        new("Test-AD", name, string.Empty, name, null, $"CN={name},OU=Groups,DC=contoso,DC=test", objectGuid, null);

    private static ResolvedDirectoryPrincipal User(string address, string objectGuid) =>
        new("Test-AD", address, address, address.Split('@')[0], address, $"CN={address},OU=Users,DC=contoso,DC=test", objectGuid, null);

    private ScriptedAdService Resolution(ResolvedRecipient? exchangeAnswer)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = _tempDir,
        }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env,
            TestConfigStore.CreateModuleConfig(_tempDir), NullLogger<ModuleConfigService>.Instance);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), NullLogger<ExtendedLogService>.Instance);
        var jsonl = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var delinea = new DelineaService(httpClientFactory, config, NullLogger<DelineaService>.Instance, extLog,
            new OperationTraceService(config, jsonl));

        var resolver = Substitute.For<IIdentityResolver>();
        resolver.ResolveRecipientAsync(Arg.Any<string>()).Returns(exchangeAnswer);
        var services = new ServiceCollection();
        services.AddSingleton(resolver);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new ScriptedAdService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea, scopeFactory);
    }

    private ConferenceRoomProtectionGate GateOver(ProtectedPrincipalService resolution)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _tempDir,
        }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);
        var servicers = new ProtectedPrincipalServicerService(
            new SectionAccessService(config, NullLogger<SectionAccessService>.Instance, env, new ModuleCatalog(),
                new SectionAccessRepository(TestConfigStore.Create(_tempDir))),
            NullLogger<ProtectedPrincipalServicerService>.Instance);
        return new ConferenceRoomProtectionGate(resolution, servicers, NullLogger<ConferenceRoomProtectionGate>.Instance);
    }

    /// <summary>Runs the gate and returns the operator-facing denial, or null when it allowed.</summary>
    private static async Task<string?> DenialFrom(ConferenceRoomProtectionGate gate) =>
        await gate.GuardThenRunAsync(Typed, onDenied: denial => denial.Message, onAllowed: _ => Task.FromResult<string?>(null));

    [Fact]
    public async Task TwoGroups_AreNotReportedAsMultipleAdUsers()
    {
        // aud-a2b's cloud-only branch, with groups on both sides: Exchange Online holds the name as a cloud distribution
        // group and Active Directory holds an on-premises group of the same name, so the string names two objects. Not one
        // user is involved.
        var resolution = Resolution(new ResolvedRecipient("rooms@contoso.test", "ext-rooms", "MailUniversalDistributionGroup", ExistsOnPrem: false))
            .AdReturns(Typed, ProtectedPrincipalService.ResolutionStatus.Resolved, Group("Boardroom Rooms", "11111111-1111-4111-8111-111111111111"));

        var denial = await DenialFrom(GateOver(resolution));

        Assert.Equal(ConferenceRoomProtectionGate.AmbiguousMessage, denial);
        Assert.DoesNotContain("AD users", denial, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AUserAndAGroup_AreNotReportedAsMultipleAdUsers()
    {
        // aud-a2b's agreement check: Exchange resolves the typed string to a synced mailbox whose canonical address Active
        // Directory holds as a GROUP, while the typed string itself reads as a USER. Two objects of different classes, and
        // the old text named the class of only one of them.
        var resolution = Resolution(new ResolvedRecipient("room1@contoso.test", null, "UserMailbox", ExistsOnPrem: true,
                ExchangeGuid: Guid.Parse("22222222-2222-4222-8222-222222222222")))
            .AdReturns(Typed, ProtectedPrincipalService.ResolutionStatus.Resolved, User("someone@contoso.test", "33333333-3333-4333-8333-333333333333"))
            .AdReturns("room1@contoso.test", ProtectedPrincipalService.ResolutionStatus.Resolved, Group("Room 1 Owners", "44444444-4444-4444-8444-444444444444"));

        var denial = await DenialFrom(GateOver(resolution));

        Assert.Equal(ConferenceRoomProtectionGate.AmbiguousMessage, denial);
        Assert.DoesNotContain("AD users", denial, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheMessage_NamesNeitherAClassNorADirectory_AndSaysWhatToLookAt()
    {
        // The wording itself, pinned once: no object class, no single directory, and an instruction the operator can act on.
        Assert.DoesNotContain("users", ConferenceRoomProtectionGate.AmbiguousMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("more than one directory object", ConferenceRoomProtectionGate.AmbiguousMessage, StringComparison.Ordinal);
        Assert.Contains("Check the address.", ConferenceRoomProtectionGate.AmbiguousMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnavailableResolution_StillSaysTheCheckCouldNotBeMade()
    {
        // The sibling status is untouched: it is not an ambiguity and must not borrow its words.
        var resolution = Resolution(exchangeAnswer: null)
            .AdReturns(Typed, ProtectedPrincipalService.ResolutionStatus.Unavailable);

        var denial = await DenialFrom(GateOver(resolution));

        Assert.Equal("Protection check unavailable.", denial);
    }
}
