using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.AdminBulkJobs;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-26: Admin Bulk Jobs audits a cancel with what the cancel did - cancelled, not cancelled because the job had already
/// finished or gone, refused, or failed - where it recorded Success after any cancel. kd-53: under the category of the
/// module whose job it is. kd-54: and a removal that did not happen says why.
/// </summary>
/// <remarks>
/// The real page, its injected services set through reflection (<see cref="ConferenceRoomPageTests"/>' idiom), its
/// <c>CancelJob</c> invoked, over a real <see cref="BulkJobService"/> on a jobs database of its own and the real audit read
/// back from its file. Nothing here reaches a directory, Exchange or the network.
/// </remarks>
public sealed class AdminBulkJobsCancelAuditTests : IDisposable
{
    private const string Operator = @"CONTOSO\admin";
    private const string OperatorIp = "10.0.0.7";
    private const string Action = "AdminBulkJobs_CancelJob";
    private const string Module = "Comms10k";
    private const string NotCancelled = "The job was not cancelled: it had already finished, or it no longer exists.";

    private static readonly DateTime Submitted = new(2026, 10, 23, 8, 15, 0, DateTimeKind.Utc);

    private readonly ExchangeWriteHarness _h = new();
    private readonly ActivityProgressService _progress = new();

    public void Dispose()
    {
        _progress.Dispose();
        _h.Dispose();
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    public async Task ACancelThatTakesEffect_IsAuditedAsDone(string state)
    {
        var (jobs, store) = JobService();
        var job = Job(state == "queued" ? BulkJobStatus.Queued : BulkJobStatus.Running);
        store.Insert(job);
        var page = BuildPage(jobs);

        await Cancel(page, job);

        var row = TheRow(job, success: true, error: null);
        Assert.False(row.TryGetProperty("refusedAt", out _));
        Assert.False(row.TryGetProperty("mutationAttempted", out _));
        Assert.Equal($"Cancellation requested for {Module} job T.", GetField(page, "actionMessage"));
        if (state == "queued")
            Assert.Equal(BulkJobStatus.Cancelled, store.Get(job.Id)!.Status);
        else
            Assert.True(store.Get(job.Id)!.CancelRequested);
    }

    [Theory]
    [InlineData("finished")]
    [InlineData("gone")]
    public async Task ACancelThatTakesNoEffect_IsAuditedAsNotCancelled_NotAsDone(string state)
    {
        // The row was rendered active; by the click the job had finished, or been removed. Nothing was cancelled, and the row
        // and the banner say so - the row said Success and the banner "Cancellation requested".
        var (jobs, store) = JobService();
        var job = Job(BulkJobStatus.Running);
        if (state == "finished")
            store.Insert(Job(BulkJobStatus.Completed));
        var page = BuildPage(jobs);

        await Cancel(page, job);

        var row = TheRow(job, success: false, error: NotCancelled);
        // kd-32. A refusal by the job's state, not a failed write: nothing was changed.
        Assert.Equal("JobState", row.GetProperty("refusedAt").GetString());
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal($"{Module} job T was not cancelled - it had already finished, or it no longer exists.", GetField(page, "actionMessage"));
        if (state == "finished")
            Assert.Equal(BulkJobStatus.Completed, store.Get(job.Id)!.Status);
        else
            Assert.Null(store.Get(job.Id));
    }

    [Fact]
    public async Task ARevokedOperatorsCancel_IsAuditedAsRefused_AndTheJobIsUntouched()
    {
        var (jobs, store) = JobService();
        var job = Job(BulkJobStatus.Queued);
        store.Insert(job);
        var page = BuildPage(jobs, authorized: false);

        await Cancel(page, job);

        var row = TheRow(job, success: false, error: "Authorization denied.");
        Assert.Equal("Authorization", row.GetProperty("refusedAt").GetString());
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(BulkJobStatus.Queued, store.Get(job.Id)!.Status);
        Assert.Equal("Your access to bulk job administration has been removed. Reload the page.", GetField(page, "actionMessage"));
    }

    [Fact]
    public async Task AJobCarryingNoModule_IsFiledUnderThisPagesOwnCategory_NotABlankOne()
    {
        // kd-53. The category comes from the job; a job with none would leave the row uncategorised, which no filter and no
        // compliance query can reach.
        var (jobs, store) = JobService();
        var job = Job(BulkJobStatus.Queued, moduleId: "");
        store.Insert(job);
        var page = BuildPage(jobs);

        await Cancel(page, job);

        var row = Assert.Single(_h.AuditRows(), r => r.GetProperty("action").GetString() == Action);
        Assert.Equal("AdminBulkJobs", row.GetProperty("category").GetString());
        Assert.Equal("Success", row.GetProperty("result").GetString());
    }

    [Fact]
    public async Task ACancelTheStoreFails_IsAuditedAsFailed_AndStillThrows()
    {
        var (jobs, _) = JobService(migrated: false);
        var job = Job(BulkJobStatus.Queued);
        var page = BuildPage(jobs);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => Cancel(page, job));

        var row = TheRow(job, success: false, error: thrown.Message);
        Assert.False(row.TryGetProperty("refusedAt", out _));
        Assert.False(row.TryGetProperty("mutationAttempted", out _));
    }

    // ---- kd-54: a removal that did not happen says why --------------------------------------------
    // RemoveJob filed Failed with no error and no refusal fields when DeleteJob answered false, and the banner guessed "it
    // may still be running". DeleteJob refuses a job that is gone and one that has not finished; the row now names which.

    private const string RemoveAction = "AdminBulkJobs_RemoveJob";

    private static Task Remove(Page page, BulkJob job) =>
        (Task)typeof(Page).GetMethod("RemoveJob", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [job])!;

    [Fact]
    public async Task ARemovalThatHappens_IsAuditedAsDone()
    {
        var (jobs, store) = JobService();
        var job = Job(BulkJobStatus.Completed);
        store.Insert(job);
        var page = BuildPage(jobs);

        await Remove(page, job);

        Assert.Null(store.Get(job.Id));
        var row = TheRow(job, success: true, error: null, action: RemoveAction);
        Assert.False(row.TryGetProperty("refusedAt", out _));
        Assert.False(row.TryGetProperty("mutationAttempted", out _));
        Assert.Equal($"Removed {Module} job T.", GetField(page, "actionMessage"));
    }

    [Theory]
    [InlineData("running", "it has not finished.")]
    [InlineData("queued", "it has not finished.")]
    [InlineData("gone", "it no longer exists.")]
    public async Task ARemovalThatDoesNotHappen_IsAuditedWithItsReason(string state, string because)
    {
        var (jobs, store) = JobService();
        var job = Job(state == "running" ? BulkJobStatus.Running : BulkJobStatus.Queued);
        if (state != "gone")
            store.Insert(job);
        var page = BuildPage(jobs);

        await Remove(page, job);

        var row = TheRow(job, success: false, error: $"The job was not removed: {because}", action: RemoveAction);
        Assert.Equal("JobState", row.GetProperty("refusedAt").GetString());
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal($"Could not remove {Module} job T - {because}", GetField(page, "actionMessage"));
        if (state == "gone")
            Assert.Null(store.Get(job.Id));
        else
            Assert.Equal(job.Status, store.Get(job.Id)!.Status);
    }

    [Fact]
    public async Task ARemovalTheStoreFails_IsAuditedAsFailed_AndStillThrows()
    {
        // kd-62. The removal and its reason are one call now, and it is wrapped: a store that throws leaves the row it
        // always owed - a mutation was attempted - instead of ending the handler with no record and no banner.
        var (jobs, _) = JobService(migrated: false);
        var job = Job(BulkJobStatus.Completed);
        var page = BuildPage(jobs);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => Remove(page, job));

        var row = TheRow(job, success: false, error: thrown.Message, action: RemoveAction);
        Assert.False(row.TryGetProperty("refusedAt", out _));
        Assert.False(row.TryGetProperty("mutationAttempted", out _));
    }

    // ---- the row -------------------------------------------------------------------------------

    // The one cancel row, as the page always wrote it - operator, IP, module/job, ticket, the job as rendered - with the
    // outcome and its reason.
    private JsonElement TheRow(BulkJob job, bool success, string? error, string action = Action)
    {
        var row = Assert.Single(_h.AuditRows(), r => r.GetProperty("action").GetString() == action);
        // kd-53. Filed under the job's own module: this fixture's job is a Comms-10k one, and every row went under
        // ConferenceRooms whatever module the job belonged to.
        Assert.Equal(Module, row.GetProperty("category").GetString());
        Assert.Equal("admin", row.GetProperty("user").GetString());
        Assert.Equal(OperatorIp, row.GetProperty("ip").GetString());
        Assert.Equal($"{Module}/{job.Id}", row.GetProperty("target").GetString());
        Assert.Equal("INC777", row.GetProperty("ticket").GetString());
        Assert.Equal(success ? "Success" : "Failed", row.GetProperty("result").GetString());
        Assert.Equal(error, row.TryGetProperty("error", out var written) && written.ValueKind != JsonValueKind.Null ? written.GetString() : null);
        Assert.Equal(Module, row.GetProperty("old_ModuleId").GetString());
        Assert.Equal(job.Status.ToString(), row.GetProperty("old_Status").GetString());
        Assert.Equal(3, row.GetProperty("old_TotalRows").GetInt32());
        return row;
    }

    // ---- the page and its job store --------------------------------------------------------------

    private static BulkJob Job(BulkJobStatus status, string moduleId = Module) => new()
    {
        Id = "job-1",
        ModuleId = moduleId,
        JobType = "T",
        Status = status,
        SubmittedBy = "jdoe",
        SubmittedIp = "10.0.0.9",
        Ticket = "INC777",
        PayloadJson = "{}",
        SubmittedAtUtc = Submitted,
        TotalRows = 3,
    };

    // With migrated false the database has no tables, so every store call throws.
    private (BulkJobService Jobs, BulkJobRepository Store) JobService(bool migrated = true)
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_h.Dir, $"jobs-{Guid.NewGuid():N}.db"));
        if (migrated)
            new JobStoreMigrator(factory).Migrate();
        var store = new BulkJobRepository(factory);
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return (new BulkJobService(scopes, store, new BulkJobProcessorRegistry([]), _h.Config, NullLogger<BulkJobService>.Instance), store);
    }

    private Page BuildPage(BulkJobService jobs, bool authorized = true)
    {
        var page = new Page();
        SetProperty(page, "BulkJobs", jobs);
        SetProperty(page, "Audit", _h.Audit);
        SetProperty(page, "AuthStateProvider", Authentication());
        SetProperty(page, "AuthorizationService", Authorization(authorized));
        SetProperty(page, "Logger", NullLogger<Page>.Instance);
        SetProperty(page, "Progress", _progress);
        SetField(page, "currentUser", Operator);
        SetField(page, "clientIpAddress", OperatorIp);
        return page;
    }

    private static Task Cancel(Page page, BulkJob job) =>
        (Task)typeof(Page).GetMethod("CancelJob", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [job])!;

    private static AuthenticationStateProvider Authentication()
    {
        var state = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, Operator)], "test")));
        var provider = Substitute.For<AuthenticationStateProvider>();
        provider.GetAuthenticationStateAsync().Returns(Task.FromResult(state));
        return provider;
    }

    private static IAuthorizationService Authorization(bool authorized)
    {
        var service = Substitute.For<IAuthorizationService>();
        service.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(authorized ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return service;
    }

    private static void SetProperty(Page page, string name, object value) =>
        typeof(Page).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static void SetField(Page page, string name, object? value) =>
        typeof(Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static object? GetField(Page page, string name) =>
        typeof(Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page);
}
