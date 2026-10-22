using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class ConferenceRoomBulkProcessorTests : IDisposable
{
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
        public Func<string, RoomOperationResult> Result = email =>
            new RoomOperationResult { Email = email, Success = true, Message = "ok" };

        public Task<RoomOperationResult> SetRoomMetadataAndListAsync(string roomEmail, ValidatedMailbox? room, string city, string building, int capacity,
            string floor, string floorLabel, string displayDevice, string videoDevice, string countryOrRegion, string state, string timezone)
        {
            FinderCalls.Add(roomEmail);
            Rooms.Add(room);
            return Task.FromResult(Result(roomEmail));
        }

        public Task<RoomOperationResult> SetRoomTypeAsync(string roomEmail, ValidatedMailbox? room, RoomType roomType, string timezone,
            string site = "none", string? arbiter = null, bool removeExistingPermissions = false)
        {
            TypeCalls.Add(roomEmail);
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
        /// the gate allowed; it now refuses (no room to bind the write to), so an ordinary row is a
        /// resolved, unprotected room with a mailbox.
        /// </summary>
        public static readonly Guid RoomGuid = Guid.Parse("5b5b5b5b-0000-4000-8000-000000000001");

        public ProtectedPrincipalService.ResolutionStatus Status = ResolutionStatus.Resolved;
        public ProtectedPrincipalResult Verdict = ProtectedPrincipalResult.NotProtected();
        public int EvalCount;

        public FakePpService(IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
            ProtectedPrincipalRepository repo, DelineaService delinea)
            : base(env, config, moduleConfig, repo, delinea, NullLogger<ProtectedPrincipalService>.Instance)
        { }

        // Scripts the seam the gate actually calls - since aud-a2b the room gate resolves through
        // ResolveExchangeTargetAsync (Exchange first, AD must agree). Overriding
        // ResolveWithStatusAsync instead would let the real resolution run and fail closed, so
        // EvalCount would stay at zero and the allow cases would not be tested.
        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveExchangeTargetAsync(string identity)
        {
            EvalCount++;
            ResolvedDirectoryPrincipal? p = Status == ResolutionStatus.Resolved
                ? new ResolvedDirectoryPrincipal("AD", identity, identity, identity, identity, null, "guid", null, RoomGuid)
                : null;
            return Task.FromResult((p, Status));
        }

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target)
            => Task.FromResult(Verdict);
    }

    private sealed class Fixture
    {
        public required FakeRoomOps Rooms { get; init; }
        public required FakePpService Pp { get; init; }
        public required AuditService Audit { get; init; }
        public required EmailService Email { get; init; }
        public required SectionAccessService SectionAccess { get; init; }
        public required ModuleEnablementService Enablement { get; init; }
        public required ConferenceRoomBulkProcessor Processor { get; init; }
    }

    private Fixture CreateFixture(string[]? allowedGroups = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _tempDir,
            ["Email:AdminNotificationEmail"] = "", // email is a no-op sink; we assert via substitute
            ["Security:AllowedGroups:0"] = "AllUsers",
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
        // Seed section access so ConferenceRooms allows "AllUsers".
        new SectionAccessRepository(store).SaveAll(new Dictionary<string, string[]>
        {
            ["ConferenceRooms"] = allowedGroups ?? ["AllUsers"]
        });

        var pp = new FakePpService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea);
        // Real servicer service over the same section-access store. No ProtectedServicer row is
        // seeded, so it denies - which is what keeps these bulk assertions unchanged: a job has no
        // ClaimsPrincipal to service with in the first place.
        var servicers = new ProtectedPrincipalServicerService(sectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance);
        var gate = new ConferenceRoomProtectionGate(pp, servicers, NullLogger<ConferenceRoomProtectionGate>.Instance);
        // The usage sink is passed explicitly as null: the proxy generator binds constructors by
        // exact arity and does not fill in the optional third parameter.
        var audit = Substitute.ForPartsOf<AuditService>(jsonlLog, trace, null);
        var email = Substitute.ForPartsOf<EmailService>(config, NullLogger<EmailService>.Instance);
        var rooms = new FakeRoomOps();

        var enablement = new ModuleEnablementService(catalog, env, moduleConfig,
            TestConfigStore.CreateModuleEnablement(_tempDir), config, NullLogger<ModuleEnablementService>.Instance);
        // The fixture represents a normally running module. A fresh enablement store has no
        // row for Conference Rooms, and after A8 that reads as disabled - which is correct
        // behaviour and would otherwise make every existing test fail for the wrong reason.
        enablement.SaveEnablement(new Dictionary<string, bool>
        {
            [ConferenceRoomBulkProcessor.ModuleName] = true,
            // IsModuleEnabled cascades through DependsOn, and Conference Rooms depends on
            // ExchangeOnline - so the parent has to be on for the child to be effectively on.
            ["ExchangeOnline"] = true,
        });

        var processor = new ConferenceRoomBulkProcessor(rooms, gate, sectionAccess, enablement, trace, audit, email,
            NullLogger<ConferenceRoomBulkProcessor>.Instance);

        return new Fixture { Rooms = rooms, Pp = pp, Audit = audit, Email = email, SectionAccess = sectionAccess, Enablement = enablement, Processor = processor };
    }

    private static BulkJob FinderJob(string authRoles = "AllUsers", params string[] emails)
    {
        var rows = emails.Select(e => new FinderCsvRow { Email = e, Building = "B", Capacity = 1 }).ToList();
        return MakeJob(ConferenceRoomJobPayload.FinderJobType,
            new ConferenceRoomJobPayload { Kind = ConferenceRoomJobPayload.FinderJobType, FinderRows = rows }, authRoles);
    }

    private static BulkJob TypeJob(string authRoles = "AllUsers", params string[] emails)
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

    [Fact]
    public async Task Finder_HappyPath_AppliesRow_AuditsSuccess()
    {
        var f = CreateFixture();
        var job = FinderJob("AllUsers", "room1@x");

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Success, outcome.Status);
        Assert.Equal(["room1@x"], f.Rooms.FinderCalls);
        // Exactly one protection evaluation per row - the consolidation guarantee (no double-check).
        Assert.Equal(1, f.Pp.EvalCount);
        f.Audit.Received().LogConferenceRoomAction("jdoe", "10.0.0.9", "ConferenceRooms_SetMetadata_Bulk",
            "room1@x", true, "INC1", errorDetail: Arg.Any<string?>(), oldValues: Arg.Any<Dictionary<string, object?>?>(), newValues: Arg.Any<Dictionary<string, object?>?>(),
            extra: Arg.Any<Dictionary<string, object?>?>());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARow_IsWrittenToTheRoomTheGateResolvedForIt_AndTheAuditRecordsIt(bool finder)
    {
        // aud-a2d. The row's room operation is handed the gate's room, not row.Email, and the row's
        // audit keeps the typed target while recording the identity written.
        var f = CreateFixture();
        var job = finder ? FinderJob("AllUsers", "room1@x") : TypeJob("AllUsers", "room1@x");

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
        // no room to bind it to, so the row is refused and audited as a denial.
        var f = CreateFixture();
        f.Pp.Status = ProtectedPrincipalService.ResolutionStatus.NotFound;
        var job = FinderJob("AllUsers", "ghost@x");

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Failed, outcome.Status);
        Assert.Empty(f.Rooms.FinderCalls);
        f.Audit.Received().LogConferenceRoomAction("jdoe", "10.0.0.9", "ConferenceRooms_SetMetadata_Bulk",
            "ghost@x", false, "INC1", errorDetail: Arg.Any<string?>(), oldValues: Arg.Any<Dictionary<string, object?>?>(), newValues: Arg.Any<Dictionary<string, object?>?>());
    }

    [Fact]
    public async Task Type_HappyPath_AppliesRow()
    {
        var f = CreateFixture();
        var job = TypeJob("AllUsers", "room1@x");

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Success, outcome.Status);
        Assert.Equal(["room1@x"], f.Rooms.TypeCalls);
    }

    [Fact]
    public async Task Finder_ProtectedTarget_IsBlocked_NotApplied_AuditedDenial()
    {
        // GAP 3 fix: the Finder path now enforces the protected-principal gate.
        var f = CreateFixture();
        f.Pp.Status = ProtectedPrincipalService.ResolutionStatus.Resolved;
        f.Pp.Verdict = ProtectedPrincipalResult.Protected("matched", "Users");
        var job = FinderJob("AllUsers", "ceo@x");

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Failed, outcome.Status);
        Assert.Contains("protected principal", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(f.Rooms.FinderCalls); // never applied
        f.Audit.Received().LogConferenceRoomAction("jdoe", "10.0.0.9", "ConferenceRooms_SetMetadata_Bulk",
            "ceo@x", false, "INC1", errorDetail: Arg.Any<string?>(), oldValues: Arg.Any<Dictionary<string, object?>?>(), newValues: Arg.Any<Dictionary<string, object?>?>());
    }

    [Fact]
    public async Task Type_ProtectedTarget_IsBlocked_NotApplied()
    {
        var f = CreateFixture();
        f.Pp.Status = ProtectedPrincipalService.ResolutionStatus.Resolved;
        f.Pp.Verdict = ProtectedPrincipalResult.Protected("matched", "Users");
        var job = TypeJob("AllUsers", "ceo@x");

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Failed, outcome.Status);
        Assert.Empty(f.Rooms.TypeCalls);
    }

    [Theory]
    [InlineData(ProtectedPrincipalService.ResolutionStatus.Unavailable)]
    [InlineData(ProtectedPrincipalService.ResolutionStatus.Ambiguous)]
    public async Task ProtectionUnresolvable_FailsClosed_NotApplied(ProtectedPrincipalService.ResolutionStatus status)
    {
        var f = CreateFixture();
        f.Pp.Status = status;
        var job = FinderJob("AllUsers", "room1@x");

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Failed, outcome.Status);
        Assert.Empty(f.Rooms.FinderCalls);
    }

    [Fact]
    public async Task AuthSnapshotLacksAccess_FailsClosed_NotApplied()
    {
        var f = CreateFixture(allowedGroups: ["ConfRoomAdmins"]);
        var job = FinderJob("SomeOtherGroup", "room1@x"); // snapshot has no matching claim

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Failed, outcome.Status);
        Assert.Contains("authorization denied", outcome.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(f.Rooms.FinderCalls);
    }

    [Fact]
    public async Task PartialResult_MapsToPartialRowStatus()
    {
        var f = CreateFixture();
        f.Rooms.Result = email => new RoomOperationResult { Email = email, Success = false, Partial = true, Message = "half done" };
        var job = FinderJob("AllUsers", "room1@x");

        var outcome = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Partial, outcome.Status);
    }

    [Fact]
    public async Task CountRows_ReturnsPayloadRowCount()
    {
        var f = CreateFixture();
        Assert.Equal(3, f.Processor.CountRows(FinderJob("AllUsers", "a", "b", "c")));
        Assert.Equal(2, f.Processor.CountRows(TypeJob("AllUsers", "a", "b")));
    }

    [Fact]
    public async Task OnJobCompleted_SendsAdminNotification()
    {
        var f = CreateFixture();
        var job = FinderJob("AllUsers", "a", "b");
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
        var job = FinderJob("AllUsers", "a", "b");
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
        var job = FinderJob("AllUsers", "a", "b");
        job.Status = BulkJobStatus.Cancelled;
        job.TotalRows = 2; job.ProcessedRows = 1; job.SuccessCount = 1; job.PartialCount = 0; job.FailedCount = 0;

        await f.Processor.OnJobCompletedAsync(job);

        await f.Email.Received().SendAdminNotificationAsync("jdoe", "10.0.0.9", "ConferenceRooms_SetMetadata_Bulk",
            false, "INC1", Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task ADisabledModule_StopsTheJob_AndSaysSo()
    {
        // Audit finding A8. Each row re-checked the section's allowed groups and nothing else,
        // so a DISABLED Conference Rooms module kept changing rooms - queued jobs and every
        // subsequent row ran on, because live authorization rejects a disabled module and this
        // off-circuit path never asked. Comms-10k closed the same gap as aud-c2.
        var fx = CreateFixture();
        fx.Enablement.SaveEnablement(new Dictionary<string, bool>
        {
            [ConferenceRoomBulkProcessor.ModuleName] = false,
        });

        var job = FinderJob("AllUsers", "room1@contoso.com");
        var outcome = await fx.Processor.ProcessRowAsync(job, 0, CancellationToken.None);

        Assert.False(outcome.Status == BulkJobRowStatus.Success);
        Assert.Contains("disabled", outcome.Message, StringComparison.OrdinalIgnoreCase);

        // And nothing reached the directory.
        Assert.Empty(fx.Rooms.FinderCalls);
    }

    /// <summary>
    /// Review of aud-a8. The test above omits ExchangeOnline from the save, and SaveEnablement
    /// defaults an omitted module to its catalog default - which for ExchangeOnline is false. So
    /// it switched BOTH off at once and would have passed just as well against a parent-only
    /// check, or a child-only one that ignored the cascade. These two separate the cases.
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

        var outcome = await fx.Processor.ProcessRowAsync(
            FinderJob("AllUsers", "room1@contoso.com"), 0, CancellationToken.None);

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

        await fx.Processor.ProcessRowAsync(
            FinderJob("AllUsers", "room1@contoso.com"), 0, CancellationToken.None);

        Assert.NotEmpty(fx.Rooms.FinderCalls);
    }
}
