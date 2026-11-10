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
/// aud-a2aa: an ambiguous binding is told as what it is. The old gate said "matches multiple AD users" for every
/// <c>Ambiguous</c> status, which names the wrong thing for most of the ways the binding reaches it - Exchange Online and
/// Active Directory naming different objects (aud-a2b), or two objects of any class carrying the room's on-premises SID.
/// Since docs/ProtectedPrincipalIdentifiers-Plan.md S4 the message is <see cref="ConferenceRoomBinder"/>'s, and it carries
/// no protection wording: Conference Rooms runs no protected-principal check (owner ruling 2026-11-03).
/// </summary>
/// <remarks>
/// The shapes are driven through the REAL <see cref="ProtectedPrincipalService.BindSyncedRecipientAsync"/>, with only its
/// directory seams scripted - the Active Directory lookup, the read by SID and the SID source - so each test reaches
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

        /// <summary>What the global catalog holds under the SID the source reports.</summary>
        public IReadOnlyList<DirectorySidObject> SidHolders { get; set; } = [];

        public ScriptedAdService AdReturns(string identity, ResolutionStatus status, ResolvedDirectoryPrincipal? principal = null)
        {
            _script[identity] = (principal, status);
            return this;
        }

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithStatusAsync(string identity) =>
            Task.FromResult(_script.TryGetValue(identity, out var scripted) ? scripted : ((ResolvedDirectoryPrincipal?)null, ResolutionStatus.NotFound));

        public override Task<(bool answered, IReadOnlyList<DirectorySidObject> found)> FindObjectsBySidAsync(string sid) =>
            Task.FromResult((true, SidHolders));
    }

    /// <summary>A SID source that answers, and reports <paramref name="sid"/> - none when it is null.</summary>
    private sealed class SidSource(string? sid) : IOnPremisesSidSource
    {
        public string Name => "Graph";

        public Task<OnPremisesSidAnswer> ReadAsync(string externalDirectoryObjectId) =>
            Task.FromResult(sid is null
                ? new OnPremisesSidAnswer(OnPremisesSidOutcome.NoneReported)
                : new OnPremisesSidAnswer(OnPremisesSidOutcome.Reported, sid));

        public Task<OnPremisesSidAnswer> ReadByUserPrincipalNameAsync(string userPrincipalName) =>
            Task.FromResult(OnPremisesSidAnswer.NotAnswered("not scripted"));
    }

    private const string RoomSid = "S-1-5-21-1000000001-1000000002-1000000003-5001";
    private static readonly Guid RoomMailbox = Guid.Parse("22222222-2222-4222-8222-222222222222");

    private static ResolvedRecipient SyncedRoom(string primary) =>
        new(primary, "ext-" + primary, "UserMailbox", ExistsOnPrem: true, ExchangeGuid: RoomMailbox, RecipientTypeDetails: "RoomMailbox");

    private static ResolvedDirectoryPrincipal Group(string name, string objectGuid) =>
        new("Test-AD", name, string.Empty, name, null, $"CN={name},OU=Groups,DC=contoso,DC=test", objectGuid, null);

    private static ResolvedDirectoryPrincipal User(string address, string objectGuid) =>
        new("Test-AD", address, address, address.Split('@')[0], address, $"CN={address},OU=Users,DC=contoso,DC=test", objectGuid, null);

    private static DirectorySidObject SidHolder(string name, string objectClass) =>
        new($"CN={name},OU=Rooms,DC=contoso,DC=test", Guid.NewGuid().ToString(), RoomSid, objectClass, name, null, null, name);

    private (ScriptedAdService resolution, IIdentityResolver exchange) Resolution(ResolvedRecipient? exchangeAnswer)
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

        return (new ScriptedAdService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea, scopeFactory),
            resolver);
    }

    /// <summary>Runs the binder and returns the operator-facing refusal, or null when it bound the room.</summary>
    private static async Task<string?> RefusalFrom(ScriptedAdService resolution, IIdentityResolver exchange) =>
        await new ConferenceRoomBinder(exchange, resolution, NullLogger<ConferenceRoomBinder>.Instance)
            .BindThenRunAsync(Typed, onRefused: refusal => refusal.Message, onBound: _ => Task.FromResult<string?>(null));

    [Fact]
    public async Task TwoObjectsCarryingTheRoomsSid_AreNotReportedAsMultipleAdUsers()
    {
        // A room account and a group both carry the SID Graph reports for the room: the forest is not answering as one, and
        // one of the two is not a user at all.
        var (resolution, exchange) = Resolution(SyncedRoom("room1@contoso.test"));
        resolution.OnPremisesSidSource = new SidSource(RoomSid);
        resolution.SidHolders = [SidHolder("Room 1", "user"), SidHolder("Room 1 Owners", "group")];

        var refusal = await RefusalFrom(resolution, exchange);

        Assert.Equal(ConferenceRoomBinder.AmbiguousMessage, refusal);
        Assert.DoesNotContain("AD users", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AUserAndAGroup_AreNotReportedAsMultipleAdUsers()
    {
        // aud-a2b's agreement check: Exchange resolves the typed string to a synced room whose canonical address Active
        // Directory holds as a GROUP, while the typed string itself reads as a USER. Two objects of different classes, and
        // the old text named the class of only one of them.
        var (resolution, exchange) = Resolution(SyncedRoom("room1@contoso.test"));
        resolution
            .AdReturns(Typed, ProtectedPrincipalService.ResolutionStatus.Resolved, User("someone@contoso.test", "33333333-3333-4333-8333-333333333333"))
            .AdReturns("room1@contoso.test", ProtectedPrincipalService.ResolutionStatus.Resolved, Group("Room 1 Owners", "44444444-4444-4444-8444-444444444444"));
        // docs/ProtectedPrincipalIdentifiers-Plan.md C.2 step 5: Graph answers with no on-premises SID, so the room is bound by
        // its canonical address, as this collision needs.
        resolution.OnPremisesSidSource = new SidSource(null);

        var refusal = await RefusalFrom(resolution, exchange);

        Assert.Equal(ConferenceRoomBinder.AmbiguousMessage, refusal);
        Assert.DoesNotContain("AD users", refusal, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheMessage_NamesNeitherAClassNorADirectory_AndSaysWhatToLookAt()
    {
        // The wording itself, pinned once: no object class, no single directory, an instruction the operator can act on -
        // and no protection language, since Conference Rooms runs no protection check.
        Assert.DoesNotContain("users", ConferenceRoomBinder.AmbiguousMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("more than one directory object", ConferenceRoomBinder.AmbiguousMessage, StringComparison.Ordinal);
        Assert.Contains("Check the address.", ConferenceRoomBinder.AmbiguousMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("protect", ConferenceRoomBinder.AmbiguousMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AnUnavailableBinding_SaysTheAccountCouldNotBeIdentified_WithNoProtectionWording()
    {
        // The sibling status: not an ambiguity, so it must not borrow its words - and it is not a protection check, so it
        // no longer says "Protection check unavailable."
        var (resolution, exchange) = Resolution(SyncedRoom("room1@contoso.test"));
        resolution.AdReturns(Typed, ProtectedPrincipalService.ResolutionStatus.Unavailable);

        var refusal = await RefusalFrom(resolution, exchange);

        Assert.Equal(ConferenceRoomBinder.AccountUnavailable, refusal);
        Assert.DoesNotContain("protect", refusal, StringComparison.OrdinalIgnoreCase);
    }
}
