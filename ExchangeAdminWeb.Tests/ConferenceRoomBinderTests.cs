using System.Text.RegularExpressions;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ExchangeAdminWeb.Tests;

/// <summary>Exchange stand-ins for the Conference Rooms suites that drive the room binder.</summary>
internal static class RoomBinderFixtures
{
    /// <summary>Exchange knowing every identity as a directory-synced room mailbox with <paramref name="exchangeGuid"/>.</summary>
    public static IIdentityResolver ExchangeKnowsASyncedRoom(Guid exchangeGuid)
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.ResolveRecipientAsync(Arg.Any<string>()).Returns(call => new ResolvedRecipient(
            call.Arg<string>(), "ext-" + call.Arg<string>(), "UserMailbox", ExistsOnPrem: true, ExchangeGuid: exchangeGuid,
            RecipientTypeDetails: "RoomMailbox"));
        return resolver;
    }
}

/// <summary>
/// docs/ProtectedPrincipalIdentifiers-Plan.md D.2 (slice S4): <see cref="ConferenceRoomBinder"/> binds a Conference Rooms write
/// to the room Exchange names and, for a synced room, to the one account holding its on-premises SID - and runs no
/// protected-principal check, because protected principals do not apply to Conference Rooms (owner ruling 2026-11-03).
/// Driven through the real protected-principal service over the stand-in forest, with Graph and the secret store stood in;
/// nothing here reaches a directory, Exchange, Graph or the secret store.
/// </summary>
public sealed class ConferenceRoomBinderTests : IDisposable
{
    private const string Target = "room1@contoso.test";
    private static readonly Guid RoomMailbox = Guid.Parse("0f0f0f0f-2222-4333-8444-555555555555");
    private const string FabrikamSid = "S-1-5-21-444444444-555555555-666666666";

    private readonly ExchangeWriteHarness _h = new();
    private readonly SelfGrantDirectoryStandIn.Entry _room;
    private readonly LogLines<ProtectedPrincipalService> _serviceLog = new();

    public ConferenceRoomBinderTests()
    {
        _h.AddMailbox(Target, RoomMailbox, recipientTypeDetails: "RoomMailbox");
        // The room's account lives in the other domain under another UPN: the session's own domain holds nothing under the
        // typed address, so only the catalog read by the SID Graph reports finds it - binding by address finds nothing.
        _h.Forest.AddGroup("Fabrikam Domain Users", domainDn: SelfGrantDirectoryStandIn.OtherDomainDn, domainSid: FabrikamSid,
            rid: SelfGrantDirectoryStandIn.DomainUsersRid);
        _room = _h.Forest.AddUser("room1", upn: "room1@fabrikam.test", mail: Target,
            domainDn: SelfGrantDirectoryStandIn.OtherDomainDn, domainSid: FabrikamSid, proxies: [$"SMTP:{Target}"]);
        _h.GraphSids["ext-" + Target] = _room.Sid;
    }

    public void Dispose() => _h.Dispose();

    private static ResolvedRecipient Recipient(string type = "RoomMailbox", bool synced = true, Guid? mailbox = null) =>
        new(Target, "ext-" + Target, "UserMailbox", ExistsOnPrem: synced, ExchangeGuid: mailbox ?? RoomMailbox,
            RecipientTypeDetails: type);

    private static IIdentityResolver Exchange(ResolvedRecipient? answer)
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.ResolveRecipientAsync(Arg.Any<string>()).Returns((ResolvedRecipient?)null);
        resolver.ResolveRecipientAsync(Target).Returns(answer);
        return resolver;
    }

    /// <summary>The binder over the real protected-principal service the harness builds, with <paramref name="protection"/> saved.</summary>
    private ConferenceRoomBinder Binder(IIdentityResolver exchange, ProtectedPrincipalConfig? protection = null)
    {
        _h.ProtectedValidator(exchange, protection ?? new ProtectedPrincipalConfig(), protectedPrincipalsLogger: _serviceLog);
        return new ConferenceRoomBinder(exchange, _h.ProtectedPrincipals!, NullLogger<ConferenceRoomBinder>.Instance);
    }

    /// <summary>Runs the binder; returns the room the write was handed, or the refusal, and how many times the write ran.</summary>
    private static async Task<(ValidatedMailbox? room, RoomBindingRefusal? refusal, int writes)> Run(ConferenceRoomBinder binder)
    {
        var writes = 0;
        ValidatedMailbox? written = null;
        var refusal = await binder.BindThenRunAsync<RoomBindingRefusal?>(Target,
            onRefused: r => r,
            onBound: room =>
            {
                writes++;
                written = room;
                return Task.FromResult<RoomBindingRefusal?>(null);
            });
        return (written, refusal, writes);
    }

    private bool AnyProtectionCheckBegun =>
        _serviceLog.Information.Any(line => line.StartsWith(ProtectedPrincipalService.ProtectionCheckName, StringComparison.Ordinal));

    [Fact]
    public async Task ASyncedRoom_IsBoundBySid_AndTheWriteIsHandedThatAccountsObjectGuid()
    {
        var (room, refusal, writes) = await Run(Binder(Exchange(Recipient())));

        Assert.Null(refusal);
        Assert.Equal(1, writes);
        Assert.Equal(RoomMailbox, room!.ExchangeGuid);
        // aud-a2d: the Active Directory steps write the account found by the SID Graph reports - here in another domain,
        // under another UPN, which the address alone never finds.
        Assert.Equal(_room.Guid.ToString(), room.DirectoryObjectGuid);
        Assert.Contains(_h.Forest.Searches,
            read => read == $"Get-ADObject {SelfGrantDirectoryStandIn.GlobalCatalogEndpoint} (objectSid={_room.Sid})");
        Assert.Empty(_h.Forest.Errors);
    }

    [Fact]
    public async Task TheBinding_FetchesTheCredentialOnce_AndBeginsNoProtectionCheck()
    {
        var (_, refusal, _) = await Run(Binder(Exchange(Recipient())));

        Assert.Null(refusal);
        Assert.Equal(1, _h.DirectoryReadFetches);
        Assert.Equal(1, _h.GraphSidReads);
        // BeginCheck logs one "Protected-principal check for ..." line when it ends; the binder begins none.
        Assert.False(AnyProtectionCheckBegun, string.Join(Environment.NewLine, _serviceLog.Information));
    }

    [Fact]
    public async Task AProtectedRoom_IsWritten_TheBinderNeverConsultsTheRules()
    {
        // The room's account is in a protected group of its own domain, so the protected-principal check calls it protected
        // (asserted below, so the rule is known to bite). Conference Rooms does not ask: the room is written.
        var vip = _h.Forest.AddGroup("Fabrikam VIP Rooms", domainDn: SelfGrantDirectoryStandIn.OtherDomainDn,
            domainSid: FabrikamSid, members: [_room]);
        var binder = Binder(Exchange(Recipient()), new ProtectedPrincipalConfig { Groups = [vip.Dn] });

        var (room, refusal, writes) = await Run(binder);

        Assert.Null(refusal);
        Assert.Equal(1, writes);
        Assert.Equal(_room.Guid.ToString(), room!.DirectoryObjectGuid);
        Assert.False(AnyProtectionCheckBegun);

        var (bound, _) = await _h.ProtectedPrincipals!.BindSyncedRecipientAsync(Target, Recipient());
        Assert.True((await _h.ProtectedPrincipals.CheckAsync(bound!)).IsProtected, "the stand-in rule no longer protects the room");
    }

    [Fact]
    public async Task ACloudOnlyRoom_IsBound_WithNoDirectoryObject_AndNoDirectoryRead()
    {
        var (room, refusal, writes) = await Run(Binder(Exchange(Recipient(synced: false))));

        Assert.Null(refusal);
        Assert.Equal(1, writes);
        Assert.Equal(RoomMailbox, room!.ExchangeGuid);
        Assert.Null(room.DirectoryObjectGuid);
        Assert.Equal(0, _h.GraphSidReads);
        Assert.Equal(0, _h.DirectoryReadFetches);
    }

    [Theory]
    [InlineData("UserMailbox")]
    [InlineData("EquipmentMailbox")]
    [InlineData("MailUniversalDistributionGroup")]
    public async Task ARecipientThatIsNotARoom_IsRefused_InTheReadBackChecksWords_NothingWritten(string type)
    {
        // Cloud-only, with a mailbox GUID: without the type check it would bind.
        var (_, refusal, writes) = await Run(Binder(Exchange(Recipient(type, synced: false))));

        Assert.Equal(0, writes);
        Assert.Equal(
            $"'{Target}' is a {type}, not a room mailbox. Conference Room operations require RoomMailbox or RemoteRoomMailbox.",
            refusal!.Message);
    }

    [Fact]
    public async Task ARemoteRoomMailbox_IsARoom()
    {
        var (_, refusal, writes) = await Run(Binder(Exchange(Recipient("RemoteRoomMailbox"))));

        Assert.Null(refusal);
        Assert.Equal(1, writes);
    }

    [Fact]
    public async Task AnUnknownAddress_IsRefused_NothingWritten()
    {
        var (_, refusal, writes) = await Run(Binder(Exchange(null)));

        Assert.Equal(0, writes);
        Assert.Equal($"Room '{Target}' not found.", refusal!.Message);
    }

    [Fact]
    public async Task AnExchangeFailure_IsRefused_NothingWritten()
    {
        var exchange = Substitute.For<IIdentityResolver>();
        exchange.ResolveRecipientAsync(Arg.Any<string>()).ThrowsAsync(new InvalidOperationException("stand-in: Exchange is down"));

        var (_, refusal, writes) = await Run(Binder(exchange));

        Assert.Equal(0, writes);
        Assert.Equal("Exchange Online could not be asked. Nothing was written.", refusal!.Message);
    }

    [Fact]
    public async Task ARoomWithNoMailboxGuid_IsRefused_AsNoRoom_NothingWritten()
    {
        var (_, refusal, writes) = await Run(Binder(Exchange(Recipient(mailbox: Guid.Empty))));

        Assert.Equal(0, writes);
        Assert.Equal(ConferenceRoomBinder.NoRoom(Target).Message, refusal!.Message);
        Assert.Equal(0, _h.GraphSidReads);
    }

    [Fact]
    public async Task ASyncedRoomWhoseSidCannotBeRead_IsRefused_WithNoReadBySid_AndNoProtectionWording()
    {
        _h.GraphSids.Remove("ext-" + Target);

        var (_, refusal, writes) = await Run(Binder(Exchange(Recipient())));

        Assert.Equal(0, writes);
        Assert.Equal(ConferenceRoomBinder.AccountUnavailable, refusal!.Message);
        Assert.DoesNotContain("protect", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_h.Forest.Searches, read => read.Contains("(objectSid=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NoProtectionEntryPoint_IsEverReached_EvenForARoomTheRulesProtect()
    {
        // A counting stand-in over the real binding: every protection entry point of the service is counted, and the check
        // would call the room protected. The binder reaches none of them, and the room is written.
        var service = CountingService.Create(Path.Combine(_h.Dir, "counting"));

        var binder = new ConferenceRoomBinder(Exchange(Recipient()), service, NullLogger<ConferenceRoomBinder>.Instance);
        var (room, refusal, writes) = await Run(binder);

        Assert.Null(refusal);
        Assert.Equal(1, writes);
        Assert.Equal(CountingService.HolderGuid, room!.DirectoryObjectGuid);
        Assert.Equal(1, service.Bindings);
        Assert.Equal(0, service.ProtectionCalls);
    }

    /// <summary>
    /// The protected-principal service with its directory seams scripted - nothing under the typed string, one user object
    /// under the SID the source reports - and every protection entry point counted, answering "protected".
    /// </summary>
    private sealed class CountingService(
        IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
        ProtectedPrincipalRepository repo, DelineaService delinea)
        : ProtectedPrincipalService(env, config, moduleConfig, repo, delinea, NullLogger<ProtectedPrincipalService>.Instance)
    {
        public const string HolderGuid = "7a7a7a7a-0000-4000-8000-000000000007";
        private const string Sid = "S-1-5-21-1000000001-1000000002-1000000003-7007";

        public int ProtectionCalls;
        public int Bindings;

        public static CountingService Create(string dir)
        {
            Directory.CreateDirectory(dir);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delinea:SecretServerUrl"] = "https://fake.local",
                ["Audit:LogRoot"] = dir,
            }).Build();
            var env = Substitute.For<IWebHostEnvironment>();
            env.ContentRootPath.Returns(dir);
            var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(dir),
                NullLogger<ModuleConfigService>.Instance);
            var http = Substitute.For<IHttpClientFactory>();
            http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
            var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(dir), NullLogger<ExtendedLogService>.Instance);
            var delinea = new DelineaService(http, config, NullLogger<DelineaService>.Instance, extLog,
                new OperationTraceService(config, new JsonlLogService(config, NullLogger<JsonlLogService>.Instance)));
            return new CountingService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(dir), delinea)
            {
                OnPremisesSidSource = new ReportsSid(Sid),
            };
        }

        private sealed class ReportsSid(string sid) : IOnPremisesSidSource
        {
            public string Name => "Graph";

            public Task<OnPremisesSidAnswer> ReadAsync(string externalDirectoryObjectId) =>
                Task.FromResult(new OnPremisesSidAnswer(OnPremisesSidOutcome.Reported, sid));
        }

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> BindSyncedRecipientAsync(
            string identity, ResolvedRecipient recipient)
        {
            Bindings++;
            return base.BindSyncedRecipientAsync(identity, recipient);
        }

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithStatusAsync(string identity) =>
            Task.FromResult<(ResolvedDirectoryPrincipal?, ResolutionStatus)>((null, ResolutionStatus.NotFound));

        public override Task<(bool answered, IReadOnlyList<DirectorySidObject> found)> FindObjectsBySidAsync(string sid) =>
            Task.FromResult<(bool, IReadOnlyList<DirectorySidObject>)>((true,
                [new DirectorySidObject("CN=room1,OU=Rooms,DC=contoso,DC=test", HolderGuid, Sid, "user", "room1", null, null, "Room 1")]));

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target)
        {
            ProtectionCalls++;
            return Task.FromResult(ProtectedPrincipalResult.Protected("stand-in"));
        }

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveExchangeTargetAsync(string identity)
        {
            ProtectionCalls++;
            return base.ResolveExchangeTargetAsync(identity);
        }

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithExchangeFallbackAsync(string identity)
        {
            ProtectionCalls++;
            return base.ResolveWithExchangeFallbackAsync(identity);
        }

        public override ProtectedPrincipalResult CheckWriteTarget(ResolvedDirectoryPrincipal target)
        {
            ProtectionCalls++;
            return ProtectedPrincipalResult.Protected("stand-in");
        }

        public override (ProtectedPrincipalConfig? config, string[] legacyExclusions, string? error) LoadEffectiveConfig()
        {
            ProtectionCalls++;
            return base.LoadEffectiveConfig();
        }
    }

    /// <summary>A logger that keeps the Information lines it is given, as rendered.</summary>
    private sealed class LogLines<T> : ILogger<T>
    {
        private readonly List<string> _information = [];

        public IReadOnlyList<string> Information
        {
            get
            {
                lock (_information)
                    return _information.ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != LogLevel.Information)
                return;
            lock (_information)
                _information.Add(formatter(state, exception));
        }
    }
}

/// <summary>
/// docs/ProtectedPrincipalIdentifiers-Plan.md D.2 (slice S4): Conference Rooms no longer offers protected-principal servicing.
/// A servicer group already stored for it is dead config - left in the store, not shown on Module Configuration, and logged
/// once at startup.
/// </summary>
public sealed class ConferenceRoomRetiredServicerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"cr-retired-{Guid.NewGuid():N}");

    public ConferenceRoomRetiredServicerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private (ProtectedPrincipalServicerService servicers, Warnings log, SectionAccessRepository store) Servicers()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _dir,
        }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_dir);
        var store = new SectionAccessRepository(TestConfigStore.Create(_dir));
        var sectionAccess = new SectionAccessService(config, NullLogger<SectionAccessService>.Instance, env, new ModuleCatalog(), store);
        var log = new Warnings();
        return (new ProtectedPrincipalServicerService(sectionAccess, log), log, store);
    }

    [Fact]
    public void AConferenceRoomsServicerGroupInTheStore_IsLoggedOnce_AndLeftInTheStore()
    {
        var (servicers, log, store) = Servicers();
        const string group = "S-1-5-21-1000000001-1000000002-1000000003-1234";
        store.SaveAll(new Dictionary<string, string[]>
        {
            [ProtectedPrincipalServicerService.SectionKeyFor("ConferenceRooms")] = [group],
            [ProtectedPrincipalServicerService.SectionKeyFor("BlockedSenders")] = [group],
        });

        var logged = servicers.ReportRetiredGrants();

        Assert.Equal(["ConferenceRooms"], logged);
        var line = Assert.Single(log.Lines);
        Assert.Contains("ConferenceRooms runs no protected-principal check", line, StringComparison.Ordinal);
        Assert.Contains("ProtectedServicer:ConferenceRooms", line, StringComparison.Ordinal);
        // Left in the store, untouched.
        Assert.True(store.TryGetAll(out var stored));
        Assert.Equal([group], stored[ProtectedPrincipalServicerService.SectionKeyFor("ConferenceRooms")]);
    }

    [Fact]
    public void NoConferenceRoomsServicerGroup_LogsNothing()
    {
        var (servicers, log, _) = Servicers();

        Assert.Empty(servicers.ReportRetiredGrants());
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void Startup_ReportsTheRetiredGrants_Once()
    {
        var program = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Program.cs"));

        Assert.Single(Regex.Matches(program, @"\.ReportRetiredGrants\(\)"));
    }

    [Fact]
    public void ModuleConfiguration_NoLongerOffersConferenceRoomsServicing()
    {
        var page = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "ModuleConfig.razor"));
        var declaration = Regex.Match(page,
            @"ModulesWithProtectedPrincipalServicing\s*=\s*new\([^)]*\)\s*\{(?<members>[^}]*)\}");
        Assert.True(declaration.Success, "the opt-in list is no longer an explicit set literal - the tripwire is stale");

        Assert.DoesNotContain("\"ConferenceRooms\"", declaration.Groups["members"].Value, StringComparison.Ordinal);
        Assert.Contains("ConferenceRooms", ProtectedPrincipalServicerService.RetiredModuleIds);
    }

    private sealed class Warnings : ILogger<ProtectedPrincipalServicerService>
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines
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
            if (logLevel < LogLevel.Warning)
                return;
            lock (_lines)
                _lines.Add(formatter(state, exception));
        }
    }
}
