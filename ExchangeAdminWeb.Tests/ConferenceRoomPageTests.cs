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

    // ---- kd-5: cancelling a bulk job writes the audit row removing one does ----
    // The jobs panel's Cancel stopped a queued or running job and recorded nothing, while Remove beside it audits. It now
    // writes RemoveJob's row - operator, IP, the job, its ticket, the job as rendered - with whether the cancel took effect:
    // refused when the job had already finished or was gone, failed when the store threw (and the throw still surfaces).

    private const string CancelAction = "ConferenceRooms_CancelBulkJob";
    private const string NothingCancelled = "The job was not cancelled: it had already finished, or it no longer exists.";

    private static readonly DateTime Submitted = new(2026, 10, 23, 9, 30, 0, DateTimeKind.Utc);

    private static BulkJob Job(string id, BulkJobStatus status) => new()
    {
        Id = id,
        ModuleId = ConferenceRoomBulkProcessor.ModuleName,
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

    private static void Cancel(Page page, BulkJob job) => Method("CancelJob").Invoke(page, [job]);

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
    public void CancellingAnActiveJob_IsAudited_AsDone(string state)
    {
        var (jobs, store) = JobService();
        var job = Job("job-1", state == "queued" ? BulkJobStatus.Queued : BulkJobStatus.Running);
        store.Insert(job);
        var page = BuildPage(jobs);

        Cancel(page, job);

        AssertTheRow(Assert.Single(CancelRows()), job, success: true, error: null);
        var after = store.Get(job.Id)!;
        if (state == "queued")
            Assert.Equal(BulkJobStatus.Cancelled, after.Status);
        else
            Assert.True(after.CancelRequested);
    }

    [Theory]
    [InlineData("finished")]
    [InlineData("gone")]
    public void ACancelThatTakesNoEffect_IsAudited_AsRefused(string state)
    {
        // The row was rendered active; by the click the job had finished, or someone had removed it. Nothing is cancelled,
        // and the row says so rather than recording a cancel that did not happen.
        var (jobs, store) = JobService();
        var job = Job("job-2", BulkJobStatus.Running);
        if (state == "finished")
            store.Insert(Job("job-2", BulkJobStatus.Completed));
        var page = BuildPage(jobs);

        Cancel(page, job);

        AssertTheRow(Assert.Single(CancelRows()), job, success: false, error: NothingCancelled);
        if (state == "finished")
            Assert.Equal(BulkJobStatus.Completed, store.Get(job.Id)!.Status);
        else
            Assert.Null(store.Get(job.Id));
    }

    [Fact]
    public void ACancelTheStoreFails_IsAudited_AsFailed_AndStillThrows()
    {
        var (jobs, _) = JobService(migrated: false);
        var job = Job("job-3", BulkJobStatus.Queued);
        var page = BuildPage(jobs);

        var thrown = Assert.Throws<TargetInvocationException>(() => Cancel(page, job)).InnerException;

        Assert.NotNull(thrown);
        AssertTheRow(Assert.Single(CancelRows()), job, success: false, error: thrown.Message);
    }

    // ---- the page ------------------------------------------------------------------------------

    private Page BuildPage(BulkJobService? jobs = null)
    {
        var page = new Page();
        if (jobs is not null)
            SetProperty(page, "BulkJobs", jobs);
        SetProperty(page, "RoomService", new ConferenceRoomService(_h.Pool, null!, _h.ModuleConfig, null!, _h.Trace, _h.Config,
            NullLogger<ConferenceRoomService>.Instance, _h.ProtectedPrincipals, _h.Servicers));
        SetProperty(page, "Audit", _h.Audit);
        SetProperty(page, "Email", Substitute.For<EmailService>(_h.Config, NullLogger<EmailService>.Instance));
        SetProperty(page, "ServiceNow", new ServiceNowService(_h.Config, NullLogger<ServiceNowService>.Instance, HttpClients()));
        SetProperty(page, "AuthStateProvider", Authentication());
        SetProperty(page, "AuthorizationService", Authorization());
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

    private static IAuthorizationService Authorization()
    {
        var service = Substitute.For<IAuthorizationService>();
        service.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
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
