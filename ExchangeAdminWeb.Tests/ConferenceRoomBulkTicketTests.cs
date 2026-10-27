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

/// <summary>
/// kd-23. The Conference Rooms bulk processor is the durable, off-circuit writer, and it trusted the
/// ticket captured on the job. The page has refused a blank ticket since kd-7, but a job submitted
/// before that - while ServiceNow was dormant and passed one - can still be queued or persisted. Each
/// row of such a job is now refused before the authorization re-check, the protection gate and any
/// write, with its own Failed audit row saying nothing was attempted.
/// </summary>
/// <remarks>
/// The real processor, over the real protection gate and a fake room-operations seam that records
/// every write it is asked for; the protected-principal service is the fake
/// <c>ConferenceRoomBulkProcessorTests</c> uses, counting its evaluations. The audit service is the
/// real one over a temp log root, read back as rows.
/// </remarks>
public sealed class ConferenceRoomBulkTicketTests : IDisposable
{
    private readonly TempDir _temp = new();

    public ConferenceRoomBulkTicketTests() =>
        Directory.CreateDirectory(Path.Combine(_temp.Path, "config"));

    public void Dispose() => _temp.Dispose();

    public static TheoryData<bool, string?> JobsWithNoTicket()
    {
        var data = new TheoryData<bool, string?>();
        foreach (var finder in new[] { true, false })
        {
            data.Add(finder, null);
            data.Add(finder, "");
            data.Add(finder, "   ");
            data.Add(finder, "\u0001"); // kd-24: a control-only ticket is no ticket either
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(JobsWithNoTicket))]
    public async Task EachRowOfAJobWithNoTicket_IsRefusedAndAudited_BeforeAnythingElseRuns(bool finder, string? ticket)
    {
        var f = CreateFixture();
        var job = Job(finder, ticket, "room1@x", "room2@x");

        // Per row, as the runner calls it: one refusal never stands in for another (Known Failure Class 2).
        var first = await f.Processor.ProcessRowAsync(job, 0, CancellationToken.None);
        var second = await f.Processor.ProcessRowAsync(job, 1, CancellationToken.None);

        const string refusal = "Ticket validation failed: Ticket number is required. Nothing was written.";
        Assert.Equal(BulkJobRowStatus.Failed, first.Status);
        Assert.Equal(BulkJobRowStatus.Failed, second.Status);
        Assert.Equal(refusal, first.Message);
        Assert.Equal(["room1@x", "room2@x"], new[] { first.Target, second.Target });

        // Nothing past the ticket: no protection evaluation, no room write.
        Assert.Equal(0, f.Pp.EvalCount);
        Assert.Empty(f.Rooms.FinderCalls);
        Assert.Empty(f.Rooms.TypeCalls);

        var rows = AuditRows();
        Assert.Equal(2, rows.Count);
        Assert.Equal(["room1@x", "room2@x"], rows.Select(r => r.GetProperty("target").GetString()));
        foreach (var row in rows)
        {
            Assert.Equal(finder ? "ConferenceRooms_SetMetadata_Bulk" : "ConferenceRooms_SetType_Bulk", row.GetProperty("action").GetString());
            Assert.Equal("Failed", row.GetProperty("result").GetString());
            Assert.Equal(refusal, row.GetProperty("error").GetString());
            Assert.Equal("TicketValidation", row.GetProperty("refusedAt").GetString());
            Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
            Assert.Equal("jdoe", row.GetProperty("user").GetString());

            // A blank ticket is recorded as none; a control-only one as it was captured, which is
            // what a reader of the row needs to see.
            if (string.IsNullOrWhiteSpace(ticket))
                Assert.False(row.TryGetProperty("ticket", out var recorded) && recorded.ValueKind != JsonValueKind.Null);
            else
                Assert.Equal(ticket, row.GetProperty("ticket").GetString());
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ARowOfAJobWithATicket_IsWritten(bool finder)
    {
        // The control: the same fixture with the ticket the page captured, and the row is written
        // through the gate - so the refusals above are the ticket check, not a fixture that writes
        // nothing.
        var f = CreateFixture();

        var outcome = await f.Processor.ProcessRowAsync(Job(finder, "INC1", "room1@x"), 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Success, outcome.Status);
        Assert.Equal(1, f.Pp.EvalCount);
        Assert.Single(finder ? f.Rooms.FinderCalls : f.Rooms.TypeCalls);
        Assert.DoesNotContain(AuditRows(), row => row.TryGetProperty("refusedAt", out _));
    }

    // ---- the fixture (the shape ConferenceRoomBulkProcessorTests builds) -----------------------

    private sealed class FakeRoomOps : IConferenceRoomBulkOperations
    {
        public readonly List<string> FinderCalls = new();
        public readonly List<string> TypeCalls = new();

        public Task<RoomOperationResult> SetRoomMetadataAndListAsync(string roomEmail, ValidatedMailbox? room, string city, string building, int capacity,
            string floor, string floorLabel, string displayDevice, string videoDevice, string countryOrRegion, string state, string timezone)
        {
            FinderCalls.Add(roomEmail);
            return Task.FromResult(new RoomOperationResult { Email = roomEmail, Success = true, Message = "ok" });
        }

        public Task<RoomOperationResult> SetRoomTypeAsync(string roomEmail, ValidatedMailbox? room, RoomType roomType, string timezone,
            string site = "none", string? arbiter = null, bool removeExistingPermissions = false)
        {
            TypeCalls.Add(roomEmail);
            return Task.FromResult(new RoomOperationResult { Email = roomEmail, Success = true, Message = "ok" });
        }
    }

    /// <summary>A resolved, unprotected room with a mailbox for every row, counting the evaluations.</summary>
    private sealed class FakePpService(IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
        ProtectedPrincipalRepository repo, DelineaService delinea)
        : ProtectedPrincipalService(env, config, moduleConfig, repo, delinea, NullLogger<ProtectedPrincipalService>.Instance)
    {
        public int EvalCount;

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveExchangeTargetAsync(string identity)
        {
            EvalCount++;
            return Task.FromResult<(ResolvedDirectoryPrincipal?, ResolutionStatus)>((
                new ResolvedDirectoryPrincipal("AD", identity, identity, identity, identity, null, "guid", null,
                    Guid.Parse("5b5b5b5b-0000-4000-8000-000000000001")),
                ResolutionStatus.Resolved));
        }

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target) =>
            Task.FromResult(ProtectedPrincipalResult.NotProtected());
    }

    private sealed record Fixture(FakeRoomOps Rooms, FakePpService Pp, ConferenceRoomBulkProcessor Processor);

    private Fixture CreateFixture()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _temp.Path,
            ["Email:AdminNotificationEmail"] = "",
            ["Delinea:SecretServerUrl"] = "https://fake.local",
        }).Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_temp.Path);
        var catalog = new Modules.ModuleCatalog();
        var store = TestConfigStore.Create(_temp.Path);
        var moduleConfig = new ModuleConfigService(catalog, env, TestConfigStore.CreateModuleConfig(_temp.Path), NullLogger<ModuleConfigService>.Instance);

        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var extendedLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_temp.Path), NullLogger<ExtendedLogService>.Instance);
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(config, log);
        var delinea = new DelineaService(http, config, NullLogger<DelineaService>.Instance, extendedLog, trace, _ => ("fixture-client", "fixture-key"));

        var sectionAccess = new SectionAccessService(config, NullLogger<SectionAccessService>.Instance, env, catalog, new SectionAccessRepository(store));
        new SectionAccessRepository(store).SaveAll(new Dictionary<string, string[]> { ["ConferenceRooms"] = ["AllUsers"] });

        var pp = new FakePpService(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_temp.Path), delinea);
        var servicers = new ProtectedPrincipalServicerService(sectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance);
        var gate = new ConferenceRoomProtectionGate(pp, servicers, NullLogger<ConferenceRoomProtectionGate>.Instance);

        var enablement = new ModuleEnablementService(catalog, env, moduleConfig,
            TestConfigStore.CreateModuleEnablement(_temp.Path), config, NullLogger<ModuleEnablementService>.Instance);
        enablement.SaveEnablement(new Dictionary<string, bool>
        {
            [ConferenceRoomBulkProcessor.ModuleName] = true,
            ["ExchangeOnline"] = true,
        });

        var rooms = new FakeRoomOps();
        var processor = new ConferenceRoomBulkProcessor(rooms, gate, sectionAccess, enablement, trace,
            new AuditService(log, trace), Substitute.For<EmailService>(config, NullLogger<EmailService>.Instance),
            NullLogger<ConferenceRoomBulkProcessor>.Instance);
        return new Fixture(rooms, pp, processor);
    }

    private static BulkJob Job(bool finder, string? ticket, params string[] emails)
    {
        var payload = finder
            ? new ConferenceRoomJobPayload
            {
                Kind = ConferenceRoomJobPayload.FinderJobType,
                FinderRows = emails.Select(e => new FinderCsvRow { Email = e, Building = "B", Capacity = 1 }).ToList(),
            }
            : new ConferenceRoomJobPayload
            {
                Kind = ConferenceRoomJobPayload.TypeJobType,
                TypeRows = emails.Select(e => new TypeCsvRow { Email = e, Type = "Standard" }).ToList(),
            };
        var snapshot = new JobAuthorizationSnapshot { Section = "ConferenceRooms", RoleClaims = ["AllUsers"], AuthorizedGroups = ["AllUsers"] };
        return new BulkJob
        {
            Id = "j1",
            ModuleId = "ConferenceRooms",
            JobType = payload.Kind,
            Status = BulkJobStatus.Running,
            SubmittedBy = "jdoe",
            SubmittedIp = "10.0.0.9",
            Ticket = ticket,
            AuthSnapshotJson = snapshot.ToJson(),
            PayloadJson = JsonSerializer.Serialize(payload),
            SubmittedAtUtc = DateTime.UtcNow,
        };
    }

    private List<JsonElement> AuditRows()
    {
        var folder = Path.Combine(_temp.Path, "ExchangeAdminWeb");
        if (!Directory.Exists(folder))
            return [];

        return Directory.GetFiles(folder, "exchangeadmin_*.jsonl")
            .Where(file => !Path.GetFileNameWithoutExtension(file).EndsWith("_trace", StringComparison.OrdinalIgnoreCase))
            .SelectMany(File.ReadAllLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(row => row.TryGetProperty("eventType", out var type) && type.GetString() == "audit")
            .ToList();
    }
}
