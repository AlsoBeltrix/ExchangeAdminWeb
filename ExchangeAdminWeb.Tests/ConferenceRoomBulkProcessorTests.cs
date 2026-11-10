using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class ConferenceRoomBulkProcessorTests : IDisposable
{
    /// <summary>The group the fixture's section allows and a job's snapshot carries, unless a test says otherwise.</summary>
    private const string Group = "S-1-5-21-100-200-300-4001";
    private const string OtherGroup = "S-1-5-21-100-200-300-4002";
    private const string Disabled = "Conference Rooms is currently disabled.";
    private const string Denied = "Authorization denied.";
    private const string DeniedAudit = "Authorization denied (captured snapshot lacks access).";

    private readonly string _tempDir;

    public ConferenceRoomBulkProcessorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"crbp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    // A fake room-ops seam that records calls - and, since aud-a2d, the room each was bound to -
    // and returns a scripted result.
    private sealed class FakeRoomOps : IConferenceRoomBulkOperations
    {
        public readonly List<string> FinderCalls = new();
        public readonly List<string> TypeCalls = new();
        public readonly List<ValidatedMailbox?> Rooms = new();
        // kd-28. The type each Room Type row was applied as.
        public readonly List<RoomType> TypesApplied = new();
        public Func<string, RoomOperationResult> Result = email =>
            new RoomOperationResult { Email = email, Success = true, Message = "ok" };

        // kd-58 A9. The token each Room Finder row was handed.
        public readonly List<CancellationToken> FinderTokens = new();

        public Task<RoomOperationResult> SetRoomMetadataAndListAsync(string roomEmail, ValidatedMailbox? room, string city, string building, int capacity,
            string floor, string floorLabel, string displayDevice, string videoDevice, string countryOrRegion, string state, string timezone,
            CancellationToken cancellationToken)
        {
            FinderCalls.Add(roomEmail);
            Rooms.Add(room);
            FinderTokens.Add(cancellationToken);
            return Task.FromResult(Result(roomEmail));
        }

        public Task<RoomOperationResult> SetRoomTypeAsync(string roomEmail, ValidatedMailbox? room, RoomType roomType, string timezone,
            string site = "none", string? arbiter = null, bool removeExistingPermissions = false)
        {
            TypeCalls.Add(roomEmail);
            TypesApplied.Add(roomType);
            Rooms.Add(room);
            return Task.FromResult(Result(roomEmail));
        }
    }

    // A fake PP service overriding the two virtual seam methods to a scripted verdict.
    // EvalCount records how many times the resolve seam is hit - the anti-double-check probe:
    // exactly one protection evaluation must occur per processed row.
    private sealed class FakePpService : ProtectedPrincipalService
    {
        /// <summary>
        /// The mailbox GUID a resolved row carries. aud-a2d: the default used to be NotFound, which
        /// the old gate allowed; it now refuses (no room to bind the write to), so an ordinary row is a
        /// resolved, unprotected room with a mailbox.
        /// </summary>
        public static readonly Guid RoomGuid = Guid.Parse("5b5b5b5b-0000-4000-8000-000000000001");

        public ProtectedPrincipalService.ResolutionStatus Status = ResolutionStatus.Resolved;
        public ProtectedPrincipalResult Verdict = ProtectedPrincipalResult.NotProtected();
        public int EvalCount;
        public int CheckCount;

        /// <summary>Whether Exchange knows the row's address as a synced room mailbox; false is "no such recipient".</summary>
        public bool ExchangeKnows = true;

        public FakePpService(IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
            ProtectedPrincipalRepository repo, DelineaService delinea)
            : base(env, config, moduleConfig, repo, delinea, NullLogger<ProtectedPrincipalService>.Instance)
        { }

        /// <summary>Exchange, as the room binder asks it: every row a synced room mailbox, unless told it knows none.</summary>
        public IIdentityResolver Exchange()
        {
            var resolver = Substitute.For<IIdentityResolver>();
            resolver.ResolveRecipientAsync(Arg.Any<string>()).Returns(call => ExchangeKnows
                ? new ResolvedRecipient(call.Arg<string>(), "ext-room", "UserMailbox", ExistsOnPrem: true, ExchangeGuid: RoomGuid,
                    RecipientTypeDetails: "RoomMailbox")
                : null);
            return resolver;
        }

        // Scripts the seam the room binder calls for a synced room (docs/ProtectedPrincipalIdentifiers-Plan.md D.2): the
        // shared SID binder. EvalCount records how many times it is reached - one binding per row.
        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> BindSyncedRecipientAsync(
            string identity, ResolvedRecipient recipient)
        {
            EvalCount++;
            ResolvedDirectoryPrincipal? p = Status == ResolutionStatus.Resolved
                ? new ResolvedDirectoryPrincipal("AD", identity, identity, identity, identity, null, "guid", null, RoomGuid)
                : null;
            return Task.FromResult((p, Status));
        }

        // Never reached: protected principals do not apply to Conference Rooms (owner ruling 2026-11-03). Counted so a
        // test can say so.
        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target)
        {
            CheckCount++;
            return Task.FromResult(Verdict);
        }
    }

    private sealed class Fixture
    {
        public required FakeRoomOps Rooms { get; init; }
        public required FakePpService Pp { get; init; }
        public required AuditService Audit { get; init; }
        public required EmailService Email { get; init; }
        public required SectionAccessService SectionAccess { get; init; }
        public required ModuleEnablementService Enablement { get; init; }
        public required JobAuthorization Authorization { get; init; }
        public required ConferenceRoomBulkProcessor Processor { get; init; }

        /// <summary>One row as the runner runs it: its authorization check, then the processor's refusal or its work.</summary>
        public async Task<BulkJobRowOutcome> RowAsync(BulkJob job, int row) =>
            Authorization.Check(job) is { } refusal
                ? Processor.RefuseRow(job, row, refusal)
                : await Processor.ProcessRowAsync(job, row, CancellationToken.None);
    }

    private Fixture CreateFixture(string[]? allowedGroups = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _tempDir,
            ["Email:AdminNotificationEmail"] = "", // email is a no-op sink; we assert via substitute
            ["Security:AllowedGroups:0"] = Group,
            ["Delinea:SecretServerUrl"] = "https://fake.local", // DelineaService ctor requires it; unused (PP is faked)
        }).Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var catalog = new Modules.ModuleCatalog();
        var store = TestConfigStore.Create(_tempDir);
        var moduleConfig = new ModuleConfigService(catalog, env, TestConfigStore.CreateModuleConfig(_tempDir), NullLogger<ModuleConfigService>.Instance);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), NullLogger<ExtendedLogService>.Instance);
        var jsonlLog = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(config, jsonlLog);
        var delinea = new DelineaService(httpClientFactory, config, NullLogger<DelineaService>.Instance, extLog, trace);

        var sectionAccess = new SectionAccessService(config, NullLogger<SectionAccessService>.Instance, env, catalog, new SectionAccessRepository(store));
        // Seed section access so ConferenceRooms allows the fixture's group.
        new SectionAccessRepository(store).SaveAll(new Dictionary<string, string[]>
        {
            ["ConferenceRooms"] = allowedGroups ?? [Group]
        });

        var pp = new FakePpService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea);
        var binder = new ConferenceRoomBinder(pp.Exchange(), pp, NullLogger<ConferenceRoomBinder>.Instance);
        // The usage sink is passed explicitly as null: the proxy generator binds constructors by
        // exact arity and does not fill in the optional third parameter.
        var audit = Substitute.ForPartsOf<AuditService>(jsonlLog, trace, null);
        var email = Substitute.ForPartsOf<EmailService>(config, NullLogger<EmailService>.Instance);
        var rooms = new FakeRoomOps();

        var enablement = new ModuleEnablementService(catalog, env, moduleConfig,
            TestConfigStore.CreateModuleEnablement(_tempDir), config, NullLogger<ModuleEnablementService>.Instance);
        // The fixture represents a normally running module. A fresh enablement store has no
        // row for Conference Rooms, which reads as disabled.
        enablement.SaveEnablement(new Dictionary<string, bool>
        {
            [ConferenceRoomBulkProcessor.ModuleName] = true,
            // IsModuleEnabled cascades through DependsOn, and Conference Rooms depends on
            // ExchangeOnline - so the parent has to be on for the child to be effectively on.
            ["ExchangeOnline"] = true,
        });

        var processor = new ConferenceRoomBulkProcessor(rooms, binder, trace, audit, email,
            NullLogger<ConferenceRoomBulkProcessor>.Instance, CancelRequests);

        return new Fixture
        {
            Rooms = rooms,
            Pp = pp,
            Audit = audit,
            Email = email,
            SectionAccess = sectionAccess,
            Enablement = enablement,
            Authorization = new JobAuthorization(catalog, enablement, sectionAccess, NullLogger<JobAuthorization>.Instance),
            Processor = processor,
        };
    }

    private static BulkJob FinderJob(string authRoles = Group, params string[] emails)
    {
        var rows = emails.Select(e => new FinderCsvRow { Email = e, Building = "B", Capacity = 1 }).ToList();
        return MakeJob(ConferenceRoomJobPayload.FinderJobType,
            new ConferenceRoomJobPayload { Kind = ConferenceRoomJobPayload.FinderJobType, FinderRows = rows }, authRoles);
    }

    private static BulkJob TypeJob(string authRoles = Group, params string[] emails)
    {
        var rows = emails.Select(e => new TypeCsvRow { Email = e, Type = "Standard" }).ToList();
        return MakeJob(ConferenceRoomJobPayload.TypeJobType,
            new ConferenceRoomJobPayload { Kind = ConferenceRoomJobPayload.TypeJobType, TypeRows = rows }, authRoles);
    }

    private static BulkJob MakeJob(string jobType, ConferenceRoomJobPayload payload, string authRoles)
    {
        var groups = authRoles.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var snap = new JobAuthorizationSnapshot { Section = "ConferenceRooms", RoleClaims = groups, AuthorizedGroups = groups };
        return new BulkJob
        {
            Id = "j1",
            ModuleId = "ConferenceRooms",
            JobType = jobType,
            Status = BulkJobStatus.Running,
            SubmittedBy = "jdoe",
            SubmittedIp = "10.0.0.9",
            Ticket = "INC1",
            AuthSnapshotJson = snap.ToJson(),
            PayloadJson = JsonSerializer.Serialize(payload),
            SubmittedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>kd-58 A9. The job store's cancel flag, as the processor reads it: no cancel requested unless a test says so.</summary>
    private IBulkJobCancelRequests CancelRequests { get; } = Substitute.For<IBulkJobCancelRequests>();

    // kd-58 A9. A Room Finder row hands the job's token to the room service - its steps before Set-Place take it.
    [Fact]
    public async Task AFinderRow_HandsTheJobsTokenToTheRoomService()
    {
        var f = CreateFixture();
        using var job = new CancellationTokenSource();

        await f.Processor.ProcessRowAsync(FinderJob(Group, "room1@x"), 0, job.Token);

        Assert.Equal(job.Token, Assert.Single(f.Rooms.FinderTokens));
    }

    // kd-58 A9, kd-57's rule at the processor: a cancellation the job did not ask for - the room service ends a row so while the
    // job's token is live - is a row that failed, in its own words, as before; only a cancel of the job's own token goes on
    // to the runner as one.
    [Fact]
    public async Task AFinderRowsCancellation_TheJobDidNotAskFor_IsARowThatFailed()
    {
        var f = CreateFixture();
        f.Rooms.Result = _ => throw new OperationCanceledException("the sign-in was abandoned");
        using var job = new CancellationTokenSource();

        var outcome = await f.Processor.ProcessRowAsync(FinderJob(Group, "room1@x"), 0, job.Token);

        Assert.Equal(BulkJobRowStatus.Failed, outcome.Status);
        Assert.Equal("the sign-in was abandoned", outcome.Message);
        f.Audit.Received().LogConferenceRoomAction("jdoe", "10.0.0.9", "ConferenceRooms_SetMetadata_Bulk",
            "room1@x", false, "INC1", errorDetail: "the sign-in was abandoned", oldValues: Arg.Any<Dictionary<string, object?>?>(),
            newValues: Arg.Any<Dictionary<string, object?>?>(), extra: Arg.Is<Dictionary<string, object?>?>(x => x == null || !x.ContainsKey("result")));
    }

    [Fact]
    public async Task Finder_HappyPath_AppliesRow_AuditsSuccess()
    {
        var f = CreateFixture();
        var job = FinderJob(Group, "room1@x");

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Success, outcome.Status);
        Assert.Equal(["room1@x"], f.Rooms.FinderCalls);
        // Exactly one binding per row - the consolidation guarantee - and no protection check.
        Assert.Equal(1, f.Pp.EvalCount);
        Assert.Equal(0, f.Pp.CheckCount);
        f.Audit.Received().LogConferenceRoomAction("jdoe", "10.0.0.9", "ConferenceRooms_SetMetadata_Bulk",
            "room1@x", true, "INC1", errorDetail: Arg.Any<string?>(), oldValues: Arg.Any<Dictionary<string, object?>?>(), newValues: Arg.Any<Dictionary<string, object?>?>(),
            extra: Arg.Any<Dictionary<string, object?>?>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARow_IsWrittenToTheRoomTheBinderBoundForIt_AndTheAuditRecordsIt(bool finder)
    {
        // aud-a2d. The row's room operation is handed the binder's room, not row.Email, and the row's
        // audit keeps the typed target while recording the identity written.
        var f = CreateFixture();
        var job = finder ? FinderJob(Group, "room1@x") : TypeJob(Group, "room1@x");

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Success, outcome.Status);
        Assert.Equal(FakePpService.RoomGuid, Assert.Single(f.Rooms.Rooms)!.ExchangeGuid);
        f.Audit.Received().LogConferenceRoomAction("jdoe", "10.0.0.9",
            finder ? "ConferenceRooms_SetMetadata_Bulk" : "ConferenceRooms_SetType_Bulk",
            "room1@x", true, "INC1", errorDetail: Arg.Any<string?>(), oldValues: Arg.Any<Dictionary<string, object?>?>(),
            newValues: Arg.Any<Dictionary<string, object?>?>(),
            extra: Arg.Is<Dictionary<string, object?>?>(x =>
                x != null && Equals(x[ValidatedMailbox.AuditKey], FakePpService.RoomGuid.ToString("D"))));
    }

    [Fact]
    public async Task ARowNeitherDirectoryKnows_HasNoRoomToBindTo_IsDeniedAndNotApplied()
    {
        // NotFound used to reach the write (the old fixture default); with nothing resolved there is
        // no room to bind it to, so the row is refused and audited as a refusal.
        var f = CreateFixture();
        f.Pp.ExchangeKnows = false;
        var job = FinderJob(Group, "ghost@x");

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Failed, outcome.Status);
        Assert.Equal("Room 'ghost@x' not found.", outcome.Message);
        Assert.Empty(f.Rooms.FinderCalls);
        f.Audit.Received().LogConferenceRoomAction("jdoe", "10.0.0.9", "ConferenceRooms_SetMetadata_Bulk",
            "ghost@x", false, "INC1", errorDetail: Arg.Any<string?>(), oldValues: Arg.Any<Dictionary<string, object?>?>(), newValues: Arg.Any<Dictionary<string, object?>?>());
    }

    [Fact]
    public async Task Type_HappyPath_AppliesRow()
    {
        var f = CreateFixture();
        var job = TypeJob(Group, "room1@x");

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Success, outcome.Status);
        Assert.Equal(["room1@x"], f.Rooms.TypeCalls);
    }

    [Fact]
    public async Task ATypeRowWhoseTypeDoesNotParse_IsRefused_SayingNothingWasWritten()
    {
        // aud-a2y (3). Refused before the service is called, so its message - on the row and in its audit - ends "Nothing
        // was written.", as every refusal the service decides before a write does (aud-a2w).
        const string refusal = "Invalid type: Boardroom. Nothing was written.";
        var f = CreateFixture();
        var job = MakeJob(ConferenceRoomJobPayload.TypeJobType, new ConferenceRoomJobPayload
        {
            Kind = ConferenceRoomJobPayload.TypeJobType,
            TypeRows = [new TypeCsvRow { Email = "room1@x", Type = "Boardroom" }],
        }, Group);

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Failed, outcome.Status);
        Assert.Equal(refusal, outcome.Message);
        Assert.Empty(f.Rooms.TypeCalls);
        f.Audit.Received(1).LogConferenceRoomAction("jdoe", "10.0.0.9", "ConferenceRooms_SetType_Bulk",
            "room1@x", false, "INC1", errorDetail: refusal, oldValues: Arg.Any<Dictionary<string, object?>?>(),
            newValues: Arg.Any<Dictionary<string, object?>?>(), extra: Arg.Any<Dictionary<string, object?>?>());
    }

    [Theory]
    [InlineData("4")]
    [InlineData("9")]
    [InlineData("Video,Restricted")]
    public async Task ATypeRowGivenANumberOrACommaList_IsRefused_NotApplied(string type)
    {
        // kd-28. Enum.TryParse took each of these: "4" as CEO, which removes every existing calendar permission; "9" as an
        // undefined type, written to CustomAttribute9 as "RoomType:9"; "Video,Restricted" as Exception. A type is its name.
        var refusal = $"Invalid type: {type}. Nothing was written.";
        var f = CreateFixture();

        var outcome = await f.Processor.ProcessRowAsync(TypeRowJob(type), 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Failed, outcome.Status);
        Assert.Equal(refusal, outcome.Message);
        Assert.Empty(f.Rooms.TypeCalls);
        f.Audit.Received(1).LogConferenceRoomAction("jdoe", "10.0.0.9", "ConferenceRooms_SetType_Bulk",
            "room1@x", false, "INC1", errorDetail: refusal, oldValues: Arg.Any<Dictionary<string, object?>?>(),
            newValues: Arg.Any<Dictionary<string, object?>?>(), extra: Arg.Any<Dictionary<string, object?>?>());
    }

    [Fact]
    public async Task ATypeRowGivenAName_IsApplied_CaseIgnoredAsBefore()
    {
        var f = CreateFixture();

        var outcome = await f.Processor.ProcessRowAsync(TypeRowJob("ceo"), 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Success, outcome.Status);
        Assert.Equal([RoomType.CEO], f.Rooms.TypesApplied);
    }

    private static BulkJob TypeRowJob(string type) => MakeJob(ConferenceRoomJobPayload.TypeJobType, new ConferenceRoomJobPayload
    {
        Kind = ConferenceRoomJobPayload.TypeJobType,
        TypeRows = [new TypeCsvRow { Email = "room1@x", Type = type }],
    }, Group);

    [Fact]
    public async Task Finder_AProtectedRoom_IsApplied_WithNoUser_AndNoCheckRuns()
    {
        // docs/ProtectedPrincipalIdentifiers-Plan.md S4: protected principals do not apply to Conference Rooms (owner ruling
        // 2026-11-03). A bulk row runs under no operator, and needs none: a room the rules would call protected is written
        // like any other, and the check is never asked.
        var f = CreateFixture();
        f.Pp.Verdict = ProtectedPrincipalResult.Protected("matched", "Users");
        var job = FinderJob(Group, "ceo@x");

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Success, outcome.Status);
        Assert.Equal(["ceo@x"], f.Rooms.FinderCalls);
        Assert.Equal(0, f.Pp.CheckCount);
        f.Audit.Received().LogConferenceRoomAction("jdoe", "10.0.0.9", "ConferenceRooms_SetMetadata_Bulk",
            "ceo@x", true, "INC1", errorDetail: Arg.Any<string?>(), oldValues: Arg.Any<Dictionary<string, object?>?>(), newValues: Arg.Any<Dictionary<string, object?>?>(),
            extra: Arg.Is<Dictionary<string, object?>?>(x => x != null && !x.ContainsKey("protectedPrincipalServiced")));
    }

    [Fact]
    public async Task Type_AProtectedRoom_IsApplied_WithNoUser_AndNoCheckRuns()
    {
        var f = CreateFixture();
        f.Pp.Verdict = ProtectedPrincipalResult.Protected("matched", "Users");
        var job = TypeJob(Group, "ceo@x");

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Success, outcome.Status);
        Assert.Equal(["ceo@x"], f.Rooms.TypeCalls);
        Assert.Equal(0, f.Pp.CheckCount);
    }

    [Theory]
    [InlineData(ProtectedPrincipalService.ResolutionStatus.Unavailable)]
    [InlineData(ProtectedPrincipalService.ResolutionStatus.Ambiguous)]
    public async Task ARoomWhoseAccountCannotBeBound_IsRefused_NotApplied(ProtectedPrincipalService.ResolutionStatus status)
    {
        var f = CreateFixture();
        f.Pp.Status = status;
        var job = FinderJob(Group, "room1@x");

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Failed, outcome.Status);
        Assert.Empty(f.Rooms.FinderCalls);
    }

    [Fact]
    public async Task AuthSnapshotLacksAccess_FailsClosed_NotApplied()
    {
        var f = CreateFixture(allowedGroups: [OtherGroup]);
        var job = FinderJob(Group, "room1@x"); // the snapshot's group is not one the section allows

        var outcome = await f.RowAsync(job, 0);

        Assert.Equal(BulkJobRowStatus.Failed, outcome.Status);
        Assert.Equal(Denied, outcome.Message);
        Assert.Empty(f.Rooms.FinderCalls);
        AssertRefusalAudited(f, "room1@x", "INC1", DeniedAudit);
    }

    // The ticket is said before the access, the page's order; a disabled module is said before either.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARefusedRowOfAJobWithNoTicket_SaysTheModuleIsOff_OrElseTheTicket(bool disabled)
    {
        var f = CreateFixture(allowedGroups: [OtherGroup]);
        if (disabled)
            f.Enablement.SaveEnablement(new Dictionary<string, bool> { [ConferenceRoomBulkProcessor.ModuleName] = false, ["ExchangeOnline"] = true });
        var job = WithTicket(FinderJob(Group, "room1@x"), null);

        var outcome = await f.RowAsync(job, 0);

        Assert.Equal(BulkJobRowStatus.Failed, outcome.Status);
        Assert.Equal(disabled ? Disabled : $"Ticket validation failed: {TicketPresence.RequiredMessage}. Nothing was written.", outcome.Message);
        Assert.Empty(f.Rooms.FinderCalls);
        AssertRefusalAudited(f, "room1@x", "", outcome.Message!);
    }

    private static void AssertRefusalAudited(Fixture f, string target, string ticket, string error) =>
        f.Audit.Received(1).LogConferenceRoomAction("jdoe", "10.0.0.9", "ConferenceRooms_SetMetadata_Bulk",
            target, false, ticket, errorDetail: error, oldValues: Arg.Any<Dictionary<string, object?>?>(), newValues: Arg.Any<Dictionary<string, object?>?>(),
            extra: Arg.Any<Dictionary<string, object?>?>());

    private static BulkJob WithTicket(BulkJob job, string? ticket, string? id = null, string? snapshot = null) => new()
    {
        Id = id ?? job.Id,
        ModuleId = job.ModuleId,
        JobType = job.JobType,
        Status = BulkJobStatus.Queued,
        SubmittedBy = job.SubmittedBy,
        SubmittedIp = job.SubmittedIp,
        Ticket = ticket,
        AuthSnapshotJson = snapshot ?? job.AuthSnapshotJson,
        PayloadJson = job.PayloadJson,
        SubmittedAtUtc = job.SubmittedAtUtc,
    };

    // -------------------------------------------------------------------------
    // Through the real runner, over the real authorization check and its stores
    // -------------------------------------------------------------------------

    private (BulkJobService Runner, BulkJobRepository Repository) Runner(Fixture f)
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_tempDir, $"jobs-{Guid.NewGuid():N}.db"));
        new JobStoreMigrator(factory).Migrate();
        var repository = new BulkJobRepository(factory);
        var provider = new ServiceCollection().AddScoped(_ => f.Processor).BuildServiceProvider();
        var runner = new BulkJobService(provider.GetRequiredService<IServiceScopeFactory>(), repository,
            new BulkJobProcessorRegistry([new(ConferenceRoomBulkProcessor.ModuleName, typeof(ConferenceRoomBulkProcessor))]),
            f.Authorization, new ConfigurationBuilder().Build(), NullLogger<BulkJobService>.Instance);
        return (runner, repository);
    }

    private void BreakStore(bool enablement)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(_tempDir, "exchangeadmin.db"),
            Pooling = false,
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = enablement ? "DROP TABLE module_enablement;" : "DROP TABLE section_access;";
        command.ExecuteNonQuery();
    }

    [Theory]
    [InlineData("disabled", Disabled, Disabled)]
    [InlineData("access-removed", Denied, DeniedAudit)]
    [InlineData("unreadable-enablement", Disabled, Disabled)]
    [InlineData("unreadable-sections", Denied, DeniedAudit)]
    public async Task Runner_AJobThatMayNoLongerRunAfterItsFirstRow_WritesNoMoreRooms(string mode, string reason, string audited)
    {
        var f = CreateFixture();
        var (runner, repository) = Runner(f);
        var job = WithTicket(FinderJob(Group, "room1@x", "room2@x", "room3@x"), "INC1", Guid.NewGuid().ToString("N"));
        repository.Insert(job);
        var taken = false;
        runner.JobChanged += id =>
        {
            if (taken || repository.Get(id)!.ProcessedRows != 1)
                return;
            taken = true;
            if (mode == "disabled")
                f.Enablement.SaveEnablement(new Dictionary<string, bool> { [ConferenceRoomBulkProcessor.ModuleName] = false, ["ExchangeOnline"] = true });
            else if (mode == "access-removed")
                new SectionAccessRepository(TestConfigStore.Create(_tempDir)).SaveAll(new Dictionary<string, string[]> { ["ConferenceRooms"] = [OtherGroup] });
            else
                BreakStore(enablement: mode == "unreadable-enablement");
        };

        await runner.DrainQueueAsync(TestContext.Current.CancellationToken);

        var done = repository.Get(job.Id)!;
        Assert.True(taken);
        Assert.Equal(["room1@x"], f.Rooms.FinderCalls);
        Assert.Equal((BulkJobStatus.Completed, 3, 1, 2), (done.Status, done.ProcessedRows, done.SuccessCount, done.FailedCount));
        Assert.Equal([("room2@x", reason), ("room3@x", reason)], runner.GetRows(job.Id).Skip(1).Select(row => (row.Target, row.Message!)));
        AssertRefusalAudited(f, "room2@x", "INC1", audited);
        AssertRefusalAudited(f, "room3@x", "INC1", audited);
        await f.Email.Received(1).SendAdminNotificationAsync("jdoe", "10.0.0.9", "ConferenceRooms_SetMetadata_Bulk", false, "INC1",
            Arg.Is<IReadOnlyDictionary<string, string>>(d => d["Result"] == "1 succeeded, 0 partial, 2 failed of 3 (Completed)."),
            "1 succeeded, 0 partial, 2 failed of 3 (Completed).");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{not-json")]
    [InlineData("{\"Section\":\"Comms10k\",\"RoleClaims\":[\"S-1-5-21-100-200-300-4001\"],\"AuthorizedGroups\":[\"S-1-5-21-100-200-300-4001\"]}")]
    public async Task Runner_AJobWithNoUsableSnapshot_IsRefusedOnEveryRow(string? snapshot)
    {
        var f = CreateFixture();
        var (runner, repository) = Runner(f);
        var queued = WithTicket(FinderJob(Group, "room1@x", "room2@x"), "INC1", Guid.NewGuid().ToString("N"));
        var job = new BulkJob
        {
            Id = queued.Id,
            ModuleId = queued.ModuleId,
            JobType = queued.JobType,
            Status = BulkJobStatus.Queued,
            SubmittedBy = queued.SubmittedBy,
            SubmittedIp = queued.SubmittedIp,
            Ticket = queued.Ticket,
            AuthSnapshotJson = snapshot,
            PayloadJson = queued.PayloadJson,
            SubmittedAtUtc = queued.SubmittedAtUtc,
        };
        repository.Insert(job);

        await runner.DrainQueueAsync(TestContext.Current.CancellationToken);

        var done = repository.Get(job.Id)!;
        Assert.Empty(f.Rooms.FinderCalls);
        Assert.Equal((BulkJobStatus.Completed, 2, 0, 2), (done.Status, done.ProcessedRows, done.SuccessCount, done.FailedCount));
        Assert.All(runner.GetRows(job.Id), row => Assert.Equal(Denied, row.Message));
        AssertRefusalAudited(f, "room1@x", "INC1", DeniedAudit);
        AssertRefusalAudited(f, "room2@x", "INC1", DeniedAudit);
    }

    [Fact]
    public async Task Runner_AnAllowedJob_WritesEveryRoom()
    {
        var f = CreateFixture();
        var (runner, repository) = Runner(f);
        var job = WithTicket(FinderJob(Group, "room1@x", "room2@x", "room3@x"), "INC1", Guid.NewGuid().ToString("N"));
        repository.Insert(job);

        await runner.DrainQueueAsync(TestContext.Current.CancellationToken);

        var done = repository.Get(job.Id)!;
        Assert.Equal(["room1@x", "room2@x", "room3@x"], f.Rooms.FinderCalls);
        Assert.Equal((BulkJobStatus.Completed, 3, 3, 0), (done.Status, done.ProcessedRows, done.SuccessCount, done.FailedCount));
        await f.Email.Received(1).SendAdminNotificationAsync("jdoe", "10.0.0.9", "ConferenceRooms_SetMetadata_Bulk", true, "INC1",
            Arg.Any<IReadOnlyDictionary<string, string>>(), null);
    }

    [Fact]
    public async Task PartialResult_MapsToPartialRowStatus()
    {
        var f = CreateFixture();
        f.Rooms.Result = email => new RoomOperationResult { Email = email, Success = false, Partial = true, Message = "half done" };
        var job = FinderJob(Group, "room1@x");

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Partial, outcome.Status);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARowsMessage_CarriesAMembershipTheReadBackConfirmed_WhenTheResultHasOne(bool confirmed)
    {
        // aud-a2o. A job shows each row's message and not its steps, so a row whose room-list membership the
        // on-prem read-back confirmed - not one this row is known to have written (aud-a2n) - read as any applied
        // add. The detail the page shows on that step, and the audit records, now follows the row's message.
        const string Done = "Room metadata and room list configured.";
        const string Detail = "Membership confirmed present after a stand-in write error: nothing recorded as written.";
        var f = CreateFixture();
        f.Rooms.Result = email => new RoomOperationResult
        {
            Email = email,
            Success = true,
            Message = Done,
            DirectoryMembershipConfirmed = confirmed ? Detail : null,
        };

        var outcome = await f.Processor.ProcessRowAsync(FinderJob(Group, "room1@x"), 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Success, outcome.Status);
        Assert.StartsWith(Done, outcome.Message, StringComparison.Ordinal);
        if (confirmed)
            Assert.Contains(Detail, outcome.Message, StringComparison.Ordinal);
        else
            Assert.Equal(Done, outcome.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARowsMessage_CarriesCityStateAndCountryTheReadBackConfirmed_WhenTheResultHasThem(bool alsoAMembership)
    {
        // aud-a2q. The same for City/State/Country the read-back found in place after Set-ADUser reported an error: the
        // detail follows the row's message - before a confirmed membership's, the order the steps ran in.
        const string Done = "Room metadata and room list configured.";
        const string Attributes = "City/State/Country confirmed in place after a stand-in write error: nothing recorded as written.";
        const string Membership = "Membership confirmed present after a stand-in write error: nothing recorded as written.";
        var f = CreateFixture();
        f.Rooms.Result = email => new RoomOperationResult
        {
            Email = email,
            Success = true,
            Message = Done,
            DirectoryAttributesConfirmed = Attributes,
            DirectoryMembershipConfirmed = alsoAMembership ? Membership : null,
        };

        var outcome = await f.Processor.ProcessRowAsync(FinderJob(Group, "room1@x"), 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Success, outcome.Status);
        Assert.Equal(alsoAMembership ? $"{Done} {Attributes} {Membership}" : $"{Done} {Attributes}", outcome.Message);
    }

    [Fact]
    public async Task CountRows_ReturnsPayloadRowCount()
    {
        var f = CreateFixture();
        Assert.Equal(3, f.Processor.CountRows(FinderJob(Group, "a", "b", "c")));
        Assert.Equal(2, f.Processor.CountRows(TypeJob(Group, "a", "b")));
    }

    [Fact]
    public async Task OnJobCompleted_SendsAdminNotification()
    {
        var f = CreateFixture();
        var job = FinderJob(Group, "a", "b");
        job.Status = BulkJobStatus.Completed;
        job.TotalRows = 2; job.SuccessCount = 2; job.ProcessedRows = 2;

        await f.Processor.OnJobCompletedAsync(job);

        await f.Email.Received().SendAdminNotificationAsync("jdoe", "10.0.0.9", "ConferenceRooms_SetMetadata_Bulk",
            true, "INC1", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task OnJobCompleted_AllPartialRows_NotifiesAsNotSuccess()
    {
        // A job with no failures but partial rows is NOT a success (matches the live page, which
        // reports bulk success only when every row fully succeeds).
        var f = CreateFixture();
        var job = FinderJob(Group, "a", "b");
        job.Status = BulkJobStatus.Completed;
        job.TotalRows = 2; job.ProcessedRows = 2; job.SuccessCount = 0; job.PartialCount = 2; job.FailedCount = 0;

        await f.Processor.OnJobCompletedAsync(job);

        await f.Email.Received().SendAdminNotificationAsync("jdoe", "10.0.0.9", "ConferenceRooms_SetMetadata_Bulk",
            false, "INC1", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task OnJobCompleted_CancelledJob_NotifiesAsNotSuccess()
    {
        var f = CreateFixture();
        var job = FinderJob(Group, "a", "b");
        job.Status = BulkJobStatus.Cancelled;
        job.TotalRows = 2; job.ProcessedRows = 1; job.SuccessCount = 1; job.PartialCount = 0; job.FailedCount = 0;

        await f.Processor.OnJobCompletedAsync(job);

        await f.Email.Received().SendAdminNotificationAsync("jdoe", "10.0.0.9", "ConferenceRooms_SetMetadata_Bulk",
            false, "INC1", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ADisabledModule_StopsTheJob_AndSaysSo()
    {
        // Live authorization rejects a disabled module, and so does the runner's check: a queued
        // job, and every row after the module is switched off, is refused.
        var fx = CreateFixture();
        fx.Enablement.SaveEnablement(new Dictionary<string, bool>
        {
            [ConferenceRoomBulkProcessor.ModuleName] = false,
        });

        var job = FinderJob(Group, "room1@contoso.com");
        var outcome = await fx.RowAsync(job, 0);

        Assert.Equal(BulkJobRowStatus.Failed, outcome.Status);
        Assert.Equal(Disabled, outcome.Message);
        AssertRefusalAudited(fx, "room1@contoso.com", "INC1", Disabled);

        // And nothing reached the directory.
        Assert.Empty(fx.Rooms.FinderCalls);
    }

    /// <summary>
    /// The test above omits ExchangeOnline from the save, and SaveEnablement defaults an omitted
    /// module to its catalog default - which for ExchangeOnline is false. So it switches BOTH off
    /// at once and would pass just as well against a parent-only check, or a child-only one that
    /// ignored the cascade. These two separate the cases.
    /// </summary>
    [Theory]
    [InlineData(false, true, "the child alone being off must stop the job")]
    [InlineData(true, false, "the parent alone being off must stop the job, because enablement cascades")]
    public async Task EitherTheChildOrItsParentBeingOff_StopsTheJob(bool child, bool parent, string because)
    {
        var fx = CreateFixture();
        fx.Enablement.SaveEnablement(new Dictionary<string, bool>
        {
            [ConferenceRoomBulkProcessor.ModuleName] = child,
            ["ExchangeOnline"] = parent,
        });

        var outcome = await fx.RowAsync(FinderJob(Group, "room1@contoso.com"), 0);

        Assert.False(outcome.Status == BulkJobRowStatus.Success, because);
        Assert.Contains("disabled", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fx.Rooms.FinderCalls);
    }

    /// <summary>
    /// The other side of the same coin: with both on, the row must proceed. Without this the
    /// pair above is satisfied by a check that refuses everything.
    /// </summary>
    [Fact]
    public async Task WithBothOn_TheRowStillReachesTheDirectory()
    {
        var fx = CreateFixture();
        fx.Enablement.SaveEnablement(new Dictionary<string, bool>
        {
            [ConferenceRoomBulkProcessor.ModuleName] = true,
            ["ExchangeOnline"] = true,
        });

        await fx.RowAsync(FinderJob(Group, "room1@contoso.com"), 0);

        Assert.NotEmpty(fx.Rooms.FinderCalls);
    }
}
