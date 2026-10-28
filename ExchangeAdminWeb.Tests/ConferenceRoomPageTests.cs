using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.ConferenceRooms;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The Conference Rooms page's own decisions, driven for real: a real <see cref="Page"/> instance with its injected services
/// set through reflection (the <see cref="ConferenceRoomDirectoryAuditTests"/> idiom) and its handler invoked.
/// </summary>
/// <remarks>
/// The room service is the real one on <see cref="ExchangeWriteHarness"/>, whose stand-in Exchange records every call, and the
/// audit is the real one, read back from its file. Nothing here reaches Exchange, a directory or the network.
/// </remarks>
public sealed class ConferenceRoomPageTests : IDisposable
{
    private const string Operator = @"CONTOSO\operator";
    private const string OperatorIp = "10.0.0.5";

    private readonly ExchangeWriteHarness _h = new();
    private readonly ActivityProgressService _progress = new();

    public void Dispose()
    {
        _progress.Dispose();
        _h.Dispose();
    }

    // ---- aud-a2y (3): the page's refusal of a room type it cannot parse says nothing was written ----

    [Fact]
    public async Task ARoomTypeThatDoesNotParse_IsRefused_SayingNothingWasWritten()
    {
        // Refused after the ticket and the authorization check and before the protection gate or the service is reached, so
        // nothing was written - and the banner now says so, as every refusal the service decides before a write does.
        var page = BuildPage();
        SetField(page, "typeRoomEmail", "room1@contoso.test");
        SetField(page, "selectedRoomType", "Boardroom");
        SetField(page, "typeTicketNumber", "INC100");
        SetField(page, "typeTimezone", "GMT Standard Time");

        await (Task)Method("SetSingleRoomType").Invoke(page, null)!;

        var result = Assert.IsType<RoomOperationResult>(GetField(page, "result"));
        Assert.False(result.Success);
        Assert.Equal("Invalid room type: Boardroom. Nothing was written.", result.Message);
        Assert.Empty(_h.Calls);
    }

    [Theory]
    [InlineData("4")]
    [InlineData("Video,Restricted")]
    public async Task ARoomTypeGivenAsANumberOrACommaList_IsRefused_NotApplied(string typeName)
    {
        // kd-28. Enum.TryParse took "4" for CEO - which removes every existing calendar permission - and a comma list for the
        // value its names OR to. The single room is parsed by name, as the CSV paths are: refused before the protection gate
        // or the service, nothing sent to Exchange.
        var page = BuildPage();
        SetField(page, "typeRoomEmail", "room1@contoso.test");
        SetField(page, "selectedRoomType", typeName);
        SetField(page, "typeTicketNumber", "INC100");
        SetField(page, "typeTimezone", "GMT Standard Time");

        await (Task)Method("SetSingleRoomType").Invoke(page, null)!;

        var result = Assert.IsType<RoomOperationResult>(GetField(page, "result"));
        Assert.False(result.Success);
        Assert.Equal($"Invalid room type: {typeName}. Nothing was written.", result.Message);
        Assert.Empty(_h.Calls);
    }

    // ---- kd-29: the page's refusal of a type that is no name files a refusal row ----

    [Theory]
    [InlineData("Boardroom")]
    [InlineData("4")]
    public async Task ARoomTypeThatIsNoName_IsAudited_AsARefusal(string typeName)
    {
        // The banner said nothing was written and nothing recorded the refusal. One ConferenceRooms_SetType row now does, as
        // the page's other refusals do: Failed, the refusal as its error, and the aud-a28 fields - nothing was sent.
        var page = BuildPage();
        SetField(page, "typeRoomEmail", "room1@contoso.test");
        SetField(page, "selectedRoomType", typeName);
        SetField(page, "typeTicketNumber", "INC100");
        SetField(page, "typeTimezone", "GMT Standard Time");

        await (Task)Method("SetSingleRoomType").Invoke(page, null)!;

        var row = Assert.Single(_h.AuditRows(), r => r.GetProperty("action").GetString() == "ConferenceRooms_SetType");
        Assert.Equal("operator", row.GetProperty("user").GetString());
        Assert.Equal(OperatorIp, row.GetProperty("ip").GetString());
        Assert.Equal("room1@contoso.test", row.GetProperty("target").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal("INC100", row.GetProperty("ticket").GetString());
        Assert.Equal($"Invalid room type: {typeName}. Nothing was written.", row.GetProperty("error").GetString());
        Assert.Equal("Validation", row.GetProperty("refusedAt").GetString());
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
        Assert.Empty(_h.Calls);
    }

    // ---- kd-5: cancelling a bulk job writes the audit row removing one does ----
    // The jobs panel's Cancel stopped a queued or running job and recorded nothing, while Remove beside it audits. It now
    // writes RemoveJob's row - operator, IP, the job, its ticket, the job as rendered - with whether the cancel took effect:
    // refused when the job had already finished or was gone, failed when the store threw (and the throw still surfaces).

    private const string CancelAction = "ConferenceRooms_CancelBulkJob";
    private const string NothingCancelled = "The job was not cancelled: it had already finished, or it no longer exists.";

    private static readonly DateTime Submitted = new(2026, 10, 23, 9, 30, 0, DateTimeKind.Utc);

    private static BulkJob Job(string id, BulkJobStatus status, string module = ConferenceRoomBulkProcessor.ModuleName) => new()
    {
        Id = id,
        ModuleId = module,
        JobType = ConferenceRoomJobPayload.TypeJobType,
        Status = status,
        SubmittedBy = "jdoe",
        SubmittedIp = "10.0.0.9",
        Ticket = "INC4242",
        PayloadJson = "{}",
        SubmittedAtUtc = Submitted,
        TotalRows = 3,
    };

    // A real job service on a jobs database of its own; with migrated false the database has no tables, so every store
    // call throws - the failure a cancel has to survive.
    private (BulkJobService Jobs, BulkJobRepository Store) JobService(bool migrated = true)
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_h.Dir, $"jobs-{Guid.NewGuid():N}.db"));
        if (migrated)
            new JobStoreMigrator(factory).Migrate();
        var store = new BulkJobRepository(factory);
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        var jobs = new BulkJobService(scopes, store, new BulkJobProcessorRegistry([]), _h.Config, NullLogger<BulkJobService>.Instance);
        return (jobs, store);
    }

    // kd-27 made CancelJob async (it re-checks access first), so its outcome - a throw included - is the task it returns.
    private static Task Cancel(Page page, BulkJob job) => (Task)Method("CancelJob").Invoke(page, [job])!;

    private List<JsonElement> CancelRows() =>
        _h.AuditRows().Where(r => r.GetProperty("action").GetString() == CancelAction).ToList();

    // RemoveJob's row: the operator and their IP, the job as target, its ticket, and the job as it was rendered.
    private static void AssertTheRow(JsonElement row, BulkJob job, bool success, string? error)
    {
        Assert.Equal("operator", row.GetProperty("user").GetString());
        Assert.Equal(OperatorIp, row.GetProperty("ip").GetString());
        Assert.Equal("ConferenceRooms", row.GetProperty("category").GetString());
        Assert.Equal(job.Id, row.GetProperty("target").GetString());
        Assert.Equal("INC4242", row.GetProperty("ticket").GetString());
        Assert.Equal(success ? "Success" : "Failed", row.GetProperty("result").GetString());
        Assert.Equal(error, row.TryGetProperty("error", out var written) && written.ValueKind != JsonValueKind.Null ? written.GetString() : null);
        Assert.Equal(job.JobType, row.GetProperty("old_JobType").GetString());
        Assert.Equal(job.Status.ToString(), row.GetProperty("old_Status").GetString());
        Assert.Equal("jdoe", row.GetProperty("old_SubmittedBy").GetString());
        Assert.Equal(Submitted, row.GetProperty("old_SubmittedAtUtc").GetDateTime().ToUniversalTime());
        Assert.Equal(3, row.GetProperty("old_TotalRows").GetInt32());
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    public async Task CancellingAnActiveJob_IsAudited_AsDone(string state)
    {
        var (jobs, store) = JobService();
        var job = Job("job-1", state == "queued" ? BulkJobStatus.Queued : BulkJobStatus.Running);
        store.Insert(job);
        var page = BuildPage(jobs);

        await Cancel(page, job);

        var row = Assert.Single(CancelRows());
        AssertTheRow(row, job, success: true, error: null);
        Assert.False(row.TryGetProperty("refusedAt", out _));
        Assert.False(row.TryGetProperty("mutationAttempted", out _));
        var after = store.Get(job.Id)!;
        if (state == "queued")
            Assert.Equal(BulkJobStatus.Cancelled, after.Status);
        else
            Assert.True(after.CancelRequested);
    }

    [Theory]
    [InlineData("finished")]
    [InlineData("gone")]
    public async Task ACancelThatTakesNoEffect_IsAudited_AsRefused(string state)
    {
        // The row was rendered active; by the click the job had finished, or someone had removed it. Nothing is cancelled,
        // and the row says so rather than recording a cancel that did not happen.
        var (jobs, store) = JobService();
        var job = Job("job-2", BulkJobStatus.Running);
        if (state == "finished")
            store.Insert(Job("job-2", BulkJobStatus.Completed));
        var page = BuildPage(jobs);

        await Cancel(page, job);

        var row = Assert.Single(CancelRows());
        AssertTheRow(row, job, success: false, error: NothingCancelled);
        // kd-32. A refusal by the job's state, not a failed write: nothing was changed.
        Assert.Equal("JobState", row.GetProperty("refusedAt").GetString());
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
        if (state == "finished")
            Assert.Equal(BulkJobStatus.Completed, store.Get(job.Id)!.Status);
        else
            Assert.Null(store.Get(job.Id));
    }

    [Fact]
    public async Task ACancelTheStoreFails_IsAudited_AsFailed_AndStillThrows()
    {
        var (jobs, _) = JobService(migrated: false);
        var job = Job("job-3", BulkJobStatus.Queued);
        var page = BuildPage(jobs);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => Cancel(page, job));

        var row = Assert.Single(CancelRows());
        AssertTheRow(row, job, success: false, error: thrown.Message);
        // kd-32. A throw keeps no refusal fields: the store may have been reached.
        Assert.False(row.TryGetProperty("refusedAt", out _));
        Assert.False(row.TryGetProperty("mutationAttempted", out _));
    }

    // ---- kd-52: the four writes' access refusal is audited, once ------------------------------------
    // Each write handler re-checks access after its ticket check (ReauthorizeAsync); a refusal set the banner and recorded
    // nothing. It now writes one row - the handler's action and target, the refusal and the aud-a28 fields - the shape kd-27's
    // job controls file.

    [Theory]
    [InlineData("SetupSingleRoom", "ConferenceRooms_SetMetadata", "room1@contoso.test")]
    [InlineData("ApplyFinderCsv", "ConferenceRooms_SetMetadata", "(bulk)")]
    [InlineData("SetSingleRoomType", "ConferenceRooms_SetType", "room1@contoso.test")]
    [InlineData("ApplyTypeCsv", "ConferenceRooms_SetType", "(bulk)")]
    public async Task ARevokedOperatorsWrite_IsRefused_AndAuditedOnce(string handler, string action, string target)
    {
        var page = BuildPage(authorized: false);
        SetField(page, "roomEmail", "room1@contoso.test");
        SetField(page, "ticketNumber", "INC100");
        SetField(page, "csvFinderTicket", "INC100");
        SetField(page, "typeRoomEmail", "room1@contoso.test");
        SetField(page, "typeTicketNumber", "INC100");
        SetField(page, "selectedRoomType", "Standard");
        SetField(page, "csvTypeTicket", "INC100");

        await (Task)Method(handler).Invoke(page, null)!;

        var row = Assert.Single(_h.AuditRows());
        Assert.Equal(action, row.GetProperty("action").GetString());
        Assert.Equal("operator", row.GetProperty("user").GetString());
        Assert.Equal(OperatorIp, row.GetProperty("ip").GetString());
        Assert.Equal(target, row.GetProperty("target").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal("INC100", row.GetProperty("ticket").GetString());
        Assert.Equal("Authorization denied.", row.GetProperty("error").GetString());
        Assert.Equal("Authorization", row.GetProperty("refusedAt").GetString());
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal("Authorization denied.", Assert.IsType<RoomOperationResult>(GetField(page, "result")).Message);
        Assert.Empty(_h.Calls);
    }

    // ---- kd-27: the job controls re-check access at the click -------------------------------------
    // Cancel and Remove acted on whatever the operator could do when the page loaded. They now re-check it as the four write
    // handlers do (ReauthorizeAsync) before any store call; a refusal leaves the job as it was and is audited under the
    // control's own action, with the aud-a28 refusal fields.

    private const string RemoveAction = "ConferenceRooms_RemoveBulkJob";

    [Theory]
    [InlineData("cancel")]
    [InlineData("remove")]
    public async Task ARevokedOperator_NeitherCancelsNorRemovesAJob_AndTheRefusalIsAudited(string control)
    {
        var (jobs, store) = JobService();
        var job = Job("job-4", control == "cancel" ? BulkJobStatus.Queued : BulkJobStatus.Completed);
        store.Insert(job);
        var page = BuildPage(jobs, authorized: false);

        await (Task)Method(control == "cancel" ? "CancelJob" : "RemoveJob").Invoke(page, [job])!;

        var row = Assert.Single(_h.AuditRows(), r => r.GetProperty("action").GetString() == (control == "cancel" ? CancelAction : RemoveAction));
        AssertTheRow(row, job, success: false, error: "Authorization denied.");
        Assert.Equal("Authorization", row.GetProperty("refusedAt").GetString());
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(job.Status, store.Get(job.Id)!.Status);
        Assert.False(store.Get(job.Id)!.CancelRequested);
        Assert.Equal("Authorization denied.", Assert.IsType<RoomOperationResult>(GetField(page, "result")).Message);
    }

    [Fact]
    public async Task AnAllowedOperatorsRemove_RemovesTheJob_AndAuditsIt()
    {
        var (jobs, store) = JobService();
        var job = Job("job-5", BulkJobStatus.Completed);
        store.Insert(job);
        var page = BuildPage(jobs);

        await (Task)Method("RemoveJob").Invoke(page, [job])!;

        Assert.Null(store.Get(job.Id));
        var row = Assert.Single(_h.AuditRows(), r => r.GetProperty("action").GetString() == RemoveAction);
        AssertTheRow(row, job, success: true, error: null);
        Assert.False(row.TryGetProperty("refusedAt", out _));
        Assert.False(row.TryGetProperty("mutationAttempted", out _));
    }

    // ---- kd-78: a removal that did not happen says why ----------------------------------------------
    // RemoveJob filed a removal the store did not make as Failed with no reason and no refusal fields, and the banner said
    // nothing - the gap kd-54 closed on Admin Bulk Jobs. It now words the store's answer (kd-77) as that page does, in the
    // banner and the row, and files it as a refusal by the job's state that changed nothing. A removal that happens is audited
    // as before and leaves the banner as it was.

    private static Task Remove(Page page, BulkJob job) => (Task)Method("RemoveJob").Invoke(page, [job])!;

    private List<JsonElement> RemoveRows() =>
        _h.AuditRows().Where(r => r.GetProperty("action").GetString() == RemoveAction).ToList();

    [Fact]
    public async Task ARemovalThatHappens_IsAuditedAsDone_AndLeavesTheBannerAsItWas()
    {
        var (jobs, store) = JobService();
        var job = Job("job-6", BulkJobStatus.Completed);
        store.Insert(job);
        var page = BuildPage(jobs);
        var earlier = new RoomOperationResult { Success = true, Message = "An earlier operation's result." };
        SetField(page, "result", earlier);

        await Remove(page, job);

        Assert.Null(store.Get(job.Id));
        var row = Assert.Single(RemoveRows());
        AssertTheRow(row, job, success: true, error: null);
        Assert.False(row.TryGetProperty("refusedAt", out _));
        Assert.False(row.TryGetProperty("mutationAttempted", out _));
        Assert.Same(earlier, GetField(page, "result"));
    }

    [Theory]
    [InlineData("gone", "it no longer exists.")]
    [InlineData("running", "it has not finished.")]
    [InlineData("queued", "it has not finished.")]
    [InlineData("another module", "it belongs to another module.")]
    public async Task ARemovalThatDoesNotHappen_SaysWhy_AndIsAuditedAsARefusalThatChangedNothing(string state, string because)
    {
        // "gone" is the one this page meets: the row was rendered finished and by the click another administrator had removed
        // the job, or it had been pruned. The page lists only its own finished jobs and a finished job never runs again, so the
        // other three are driven directly - the store's other answers, each worded rather than read as one another.
        var (jobs, store) = JobService();
        var job = Job("job-7", state switch { "running" => BulkJobStatus.Running, "queued" => BulkJobStatus.Queued, _ => BulkJobStatus.Completed });
        if (state is "running" or "queued")
            store.Insert(job);
        else if (state == "another module")
            store.Insert(Job(job.Id, BulkJobStatus.Completed, module: "Comms10k"));
        var page = BuildPage(jobs);

        await Remove(page, job);

        var row = Assert.Single(RemoveRows());
        AssertTheRow(row, job, success: false, error: $"The job was not removed: {because}");
        Assert.Equal("JobState", row.GetProperty("refusedAt").GetString());
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
        var banner = Assert.IsType<RoomOperationResult>(GetField(page, "result"));
        Assert.False(banner.Success);
        Assert.Equal($"Could not remove the Room Type (bulk) job - {because}", banner.Message);
        // Nothing was changed: the job is still there as it was, or still absent.
        if (state == "gone")
            Assert.Null(store.Get(job.Id));
        else
            Assert.Equal(job.Status, store.Get(job.Id)!.Status);
    }

    // ---- the page ------------------------------------------------------------------------------

    private Page BuildPage(BulkJobService? jobs = null, bool authorized = true)
    {
        var page = new Page();
        if (jobs is not null)
            SetProperty(page, "BulkJobs", jobs);
        SetProperty(page, "RoomService", new ConferenceRoomService(_h.Pool, null!, _h.ModuleConfig, null!, _h.Trace, _h.Config,
            NullLogger<ConferenceRoomService>.Instance));
        SetProperty(page, "Audit", _h.Audit);
        SetProperty(page, "Email", Substitute.For<EmailService>(_h.Config, NullLogger<EmailService>.Instance));
        SetProperty(page, "ServiceNow", new ServiceNowService(_h.Config, NullLogger<ServiceNowService>.Instance, HttpClients()));
        SetProperty(page, "AuthStateProvider", Authentication());
        SetProperty(page, "AuthorizationService", Authorization(authorized));
        SetProperty(page, "OpTrace", _h.Trace);
        SetProperty(page, "Logger", NullLogger<Page>.Instance);
        SetProperty(page, "Progress", _progress);

        SetField(page, "currentUser", Operator);
        SetField(page, "clientIpAddress", OperatorIp);
        return page;
    }

    private static IHttpClientFactory HttpClients()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        return factory;
    }

    private static AuthenticationStateProvider Authentication()
    {
        var state = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, Operator)], "test")));
        var provider = Substitute.For<AuthenticationStateProvider>();
        provider.GetAuthenticationStateAsync().Returns(Task.FromResult(state));
        return provider;
    }

    // The page's policy answer for the operator: authorized, or - kd-27 - revoked since the page loaded.
    private static IAuthorizationService Authorization(bool authorized = true)
    {
        var service = Substitute.For<IAuthorizationService>();
        service.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(authorized ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return service;
    }

    private static MethodInfo Method(string name) =>
        typeof(Page).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static void SetProperty(Page page, string name, object value) =>
        typeof(Page).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static void SetField(Page page, string name, object? value) =>
        typeof(Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static object? GetField(Page page, string name) =>
        typeof(Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page);
}
