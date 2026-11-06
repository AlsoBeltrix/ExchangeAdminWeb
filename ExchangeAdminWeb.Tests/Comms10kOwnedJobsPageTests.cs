using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.Comms10k;

namespace ExchangeAdminWeb.Tests;

// Compiled component/handler evidence only. These frames are not a browser or a rendering host.
#pragma warning disable BL0006
public sealed partial class Comms10kRefusalTests
{
    private const string C5Owner = "EXAMPLE\\operator";
    private const string C5GroupSid = "S-1-5-21-100-200-300-4444";

    [Fact]
    public async Task C5PageScopesListsAndDetailsToFreshIdentityAndConstants()
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = C5Page(fixture, progress);
        foreach (var job in new[]
        {
            C5Job("own-active"), C5Job("own-finished", BulkJobStatus.Completed),
            C5Job("other-owner", owner: "OTHER\\operator"),
            C5Job("other-module", module: "ConferenceRooms"), C5Job("other-type", type: "OtherCommsJob"),
        })
        {
            repository.Insert(job);
            C5Row(repository, job.Id, 0, BulkJobRowStatus.Success, $"recorded-{job.Id}");
        }
        SetField(page, "currentUser", "OTHER\\operator");
        await InvokePage(page, "RefreshOwnedJobs");
        Assert.Equal(["own-active"], C5Field<IReadOnlyList<BulkJob>>(page, "activeOwnedJobs").Select(j => j.Id));
        Assert.Equal(["own-finished"], C5Field<IReadOnlyList<BulkJob>>(page, "finishedOwnedJobs").Select(j => j.Id));
        foreach (var inaccessible in new[] { "other-owner", "other-module", "other-type", "missing" })
        {
            await C5Invoke(page, "ToggleJobDetails", inaccessible);
            Assert.Null(C5Field<BulkJob?>(page, "detailsJob"));
            Assert.Empty(C5Field<IReadOnlyList<BulkJobRow>>(page, "detailsRows"));
            Assert.Equal("Job unavailable or this action is no longer allowed.", C5Field<string>(page, "jobsActionMessage"));
        }
        await C5Invoke(page, "ToggleJobDetails", "own-finished");
        Assert.Equal("own-finished", C5Field<BulkJob>(page, "detailsJob").Id);
        Assert.Equal("recorded-own-finished", Assert.Single(C5Field<IReadOnlyList<BulkJobRow>>(page, "detailsRows")).Message);
    }

    [Theory]
    [InlineData("RefreshOwnedJobs", false)]
    [InlineData("LoadOwnedJobsAsync", false)]
    [InlineData("ToggleJobDetails", false)]
    [InlineData("CancelOwnedJob", false)]
    [InlineData("RemoveOwnedJob", false)]
    [InlineData("RefreshOwnedJobs", true)]
    [InlineData("LoadOwnedJobsAsync", true)]
    [InlineData("ToggleJobDetails", true)]
    [InlineData("CancelOwnedJob", true)]
    [InlineData("RemoveOwnedJob", true)]
    public async Task C5LivePolicyIncludingEnablementDeniesDirectHandlersAndClearsCaches(string handler, bool disableModule)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        using var policy = new C5LivePolicy(fixture);
        var (page, repository) = C5Page(fixture, progress);
        SetPageProperty(page, "AuthorizationService", policy.Authorization);
        repository.Insert(C5Job("own-active"));
        repository.Insert(C5Job("own-finished", BulkJobStatus.Completed));
        C5Row(repository, "own-finished", 1, BulkJobRowStatus.Partial, "Unknown write completion.");
        await InvokePage(page, "RefreshOwnedJobs");
        await C5Invoke(page, "ToggleJobDetails", "own-finished");
        Assert.Single(C5Field<IReadOnlyList<BulkJob>>(page, "activeOwnedJobs"));
        Assert.NotNull(C5Field<BulkJob?>(page, "detailsJob"));
        policy.Revoke(disableModule);

        await C5Invoke(page, handler, handler == "CancelOwnedJob" ? ["own-active"]
            : handler is "ToggleJobDetails" or "RemoveOwnedJob" ? ["own-finished"] : []);

        Assert.False(C5Field<bool>(page, "jobsAllowed"));
        Assert.Empty(C5Field<IReadOnlyList<BulkJob>>(page, "activeOwnedJobs"));
        Assert.Empty(C5Field<IReadOnlyList<BulkJob>>(page, "finishedOwnedJobs"));
        Assert.Null(C5Field<BulkJob?>(page, "detailsJob"));
        Assert.Empty(C5Field<IReadOnlyList<BulkJobRow>>(page, "detailsRows"));
        Assert.Contains("denied", C5Field<string>(page, "jobsLoadError"), StringComparison.Ordinal);
        Assert.False(repository.IsCancelRequested("own-active"));
        Assert.Equal(BulkJobStatus.Queued, repository.Get("own-active")!.Status);
        Assert.NotNull(repository.Get("own-finished"));
        if (handler is "CancelOwnedJob" or "RemoveOwnedJob")
        {
            var audit = Assert.Single(fixture.C5ControlAudits());
            Assert.Equal("Failed", audit.GetProperty("result").GetString());
            Assert.Equal(C5Owner, audit.GetProperty("Actor").GetString());
            Assert.False(audit.TryGetProperty("SubmittedBy", out _));
        }
    }

    [Theory]
    [InlineData("unauthenticated")]
    [InlineData("blank")]
    [InlineData("spaces")]
    [InlineData("display-only")]
    public async Task C5MissingTrustedIdentityCannotUseCachedOwnershipOrEnqueue(string identity)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = C5Page(fixture, progress);
        repository.Insert(C5Job("mine"));
        var name = identity switch { "blank" => "", "spaces" => "  ", _ => C5Owner };
        var claims = identity == "display-only" ? new[] { new Claim("displayName", C5Owner) } : [new Claim(ClaimTypes.Name, name)];
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, identity == "unauthenticated" ? null : "test"));
        SetPageProperty(page, "AuthStateProvider", C5Authentication(principal));
        await InvokePage(page, "RefreshOwnedJobs");
        Assert.Empty(C5Field<IReadOnlyList<BulkJob>>(page, "activeOwnedJobs"));
        SetField(page, "resolveResult", CompleteResolution());
        SetField(page, "confirmation", new Comms10kConfirmation([Address], "members.csv", "INC123", CompleteResolution().ResolvedMembers));
        await InvokeReplace(page);
        Assert.Equal(["mine"], repository.GetActive().Select(j => j.Id));
        Assert.Null(C5Field<string?>(page, "submittedNotice"));
        Assert.Equal("Authorization denied.", C5Field<Comms10kUpdateResult>(page, "result").Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C5DirectControlsUsePersistedScopeAndAuditWithoutForeignMetadata(bool delete)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = C5Page(fixture, progress);
        var status = delete ? BulkJobStatus.Completed : BulkJobStatus.Queued;
        var own = C5Job("own", status);
        repository.Insert(own);
        foreach (var foreign in new[] { C5Job("owner", status, "OTHER\\operator"),
            C5Job("module", status, module: "ConferenceRooms"), C5Job("type", status, type: "OtherCommsJob") })
            repository.Insert(foreign);
        SetField(page, "currentUser", "OTHER\\operator");
        SetField(page, "detailsJob", own);
        SetField(page, "detailsRows", new[] { new BulkJobRow { JobId = "own", RowIndex = 1, Target = "members.csv", Status = BulkJobRowStatus.Partial } });
        var method = delete ? "RemoveOwnedJob" : "CancelOwnedJob";
        foreach (var id in new[] { "owner", "module", "type", "missing" })
        {
            await C5Invoke(page, method, id);
            Assert.False(C5Field<bool>(page, "jobsActionSucceeded"));
            Assert.Equal("Job unavailable or this action is no longer allowed.", C5Field<string>(page, "jobsActionMessage"));
            if (id != "missing")
            {
                Assert.NotNull(repository.Get(id));
                Assert.Equal(status, repository.Get(id)!.Status);
                Assert.False(repository.IsCancelRequested(id));
            }
        }
        await C5Invoke(page, method, "own");
        Assert.True(C5Field<bool>(page, "jobsActionSucceeded"));
        Assert.Contains(delete ? "deleted" : "Cancellation requested", C5Field<string>(page, "jobsActionMessage"), StringComparison.Ordinal);
        Assert.Equal(delete, repository.Get("own") is null);
        if (delete)
        {
            Assert.Null(C5Field<BulkJob?>(page, "detailsJob"));
            Assert.Empty(C5Field<IReadOnlyList<BulkJobRow>>(page, "detailsRows"));
        }
        var audits = fixture.C5ControlAudits();
        Assert.Equal(5, audits.Count);
        foreach (var audit in audits)
        {
            Assert.Equal(C5Owner, audit.GetProperty("Actor").GetString());
            Assert.Equal("192.0.2.7", audit.GetProperty("ip").GetString());
            Assert.Equal(delete ? "Comms10k_DeleteJob" : "Comms10k_CancelJob", audit.GetProperty("action").GetString());
            Assert.Equal(audit.GetProperty("target").GetString(), audit.GetProperty("RequestedJobId").GetString());
            if (audit.GetProperty("result").GetString() == "Failed")
            {
                Assert.False(audit.TryGetProperty("ticket", out _));
                Assert.False(audit.TryGetProperty("SubmittedBy", out _));
            }
            else
            {
                Assert.Equal("INC123", audit.GetProperty("ticket").GetString());
                Assert.Equal(C5Owner, audit.GetProperty("SubmittedBy").GetString());
                Assert.Equal(status.ToString(), audit.GetProperty("JobStatus").GetString());
            }
        }
        Assert.Equal(0, fixture.C5NotificationCount);
    }

    [Fact]
    public async Task C5AuditFailureCannotReverseAnAlreadyDeletedRecord()
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = C5Page(fixture, progress);
        repository.Insert(C5Job("own", BulkJobStatus.Completed));
        SetPageProperty(page, "Audit", new AuditService(null!, null!));
        await C5Invoke(page, "RemoveOwnedJob", "own");
        Assert.Null(repository.Get("own"));
        Assert.True(C5Field<bool>(page, "jobsActionSucceeded"));
        Assert.Contains("record deleted", C5Field<string>(page, "jobsActionMessage"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task C5SubmissionUsesCurrentFullNameAndAnInformationalNotice()
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = C5Page(fixture, progress);
        SetField(page, "currentUser", "stale-SAM");
        SetField(page, "resolveResult", CompleteResolution());
        SetField(page, "confirmation", new Comms10kConfirmation([Address], "members.csv", "INC123", CompleteResolution().ResolvedMembers));
        await InvokeReplace(page);
        var job = Assert.Single(repository.GetActive());
        Assert.Equal(C5Owner, job.SubmittedBy);
        Assert.Null(C5Field<Comms10kUpdateResult?>(page, "result"));
        Assert.Contains(job.Id, C5Field<string>(page, "submittedNotice"), StringComparison.Ordinal);
        Assert.Equal(job.Id, Assert.Single(C5Field<IReadOnlyList<BulkJob>>(page, "activeOwnedJobs")).Id);
        var text = C5RenderText(page);
        Assert.Contains("Submitted.", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was changed.", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("refused", "Nothing was written. Fix the file.", "failed stages")]
    [InlineData("partial", "Partly applied. Acknowledged adds: 37. Final membership: 37.", "partial or unconfirmed")]
    [InlineData("unknown", "Could not confirm. Members written: UNKNOWN. Final membership: UNKNOWN.", "partial or unconfirmed")]
    [InlineData("interrupted", "Interrupted by application restart.", "group may be empty or partial")]
    [InlineData("payload", "Job payload could not be read.", "without recorded stages")]
    [InlineData("success", "Replaced and verified. Acknowledged adds: 1. Final membership: 1.", "directory outcome and counts")]
    public async Task C5RecordedOutcomesRemainVisibleWithoutServiceCredentials(string outcome, string message, string expectedSummary)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = C5Page(fixture, progress);
        var moduleConfig = (ModuleConfigService)typeof(Comms10kService).GetField("_moduleConfig", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(fixture.Service)!;
        moduleConfig.SaveModuleConfig("Comms10k", []);
        Assert.False(fixture.Service.IsConfigured);
        var job = C5Job("outcome", outcome == "interrupted" ? BulkJobStatus.Interrupted : BulkJobStatus.Completed);
        job.Message = message;
        repository.Insert(job);
        if (outcome is not "payload" and not "interrupted")
        {
            C5Row(repository, job.Id, 0, BulkJobRowStatus.Success, "1 of 1 matched in AD");
            C5Row(repository, job.Id, 1, outcome == "refused" ? BulkJobRowStatus.Failed
                : outcome == "success" ? BulkJobRowStatus.Success : BulkJobRowStatus.Partial, message);
        }
        await InvokePage(page, "RefreshOwnedJobs");
        await C5Invoke(page, "ToggleJobDetails", job.Id);
        var text = C5RenderText(page);
        Assert.Contains("Your jobs", text, StringComparison.Ordinal);
        Assert.Contains("Module not configured", text, StringComparison.Ordinal);
        Assert.Contains("INC123", text, StringComparison.Ordinal);
        Assert.Contains(job.SubmittedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), text, StringComparison.Ordinal);
        Assert.Contains(message, text, StringComparison.Ordinal);
        Assert.Contains(expectedSummary, text, StringComparison.Ordinal);
        if (outcome is "payload" or "interrupted") Assert.Contains("No stages recorded", text, StringComparison.Ordinal);
        else
        {
            Assert.Contains("Resolve", text, StringComparison.Ordinal);
            Assert.Contains("Replace", text, StringComparison.Ordinal);
            Assert.Contains("1 of 1 matched in AD", text, StringComparison.Ordinal);
            Assert.Contains(outcome == "refused" ? "Failed" : outcome == "success" ? "Success" : "Partial", text, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("newer-load")]
    [InlineData("denial-after-newer")]
    [InlineData("dispose")]
    public async Task C5StaleLoadsCannotPublishOrClearANewerView(string situation)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = C5Page(fixture, progress);
        repository.Insert(C5Job("mine"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = Substitute.For<IAuthorizationService>();
        var calls = 0;
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>()).Returns(_ =>
        {
            if (Interlocked.Increment(ref calls) != 1) return Task.FromResult(AuthorizationResult.Success());
            entered.SetResult();
            return release.Task;
        });
        SetPageProperty(page, "AuthorizationService", auth);
        var old = InvokePage(page, "LoadOwnedJobsAsync");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (situation == "dispose") page.Dispose();
        else await InvokePage(page, "LoadOwnedJobsAsync");
        // Removing the row after the newer load makes a stale publication observably different.
        using (var connection = new SqliteConnectionFactory(repository.DatabasePath).Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM bulk_job;";
            command.ExecuteNonQuery();
        }
        release.SetResult(situation == "denial-after-newer" ? AuthorizationResult.Failed() : AuthorizationResult.Success());
        await old;
        Assert.Equal(situation == "dispose" ? 0 : 1, C5Field<IReadOnlyList<BulkJob>>(page, "activeOwnedJobs").Count);
        if (situation == "dispose") Assert.False(C5Field<bool>(page, "jobsAllowed"));
        Assert.Null(C5Field<string?>(page, "jobsLoadError"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C5DetailContinuationCannotPublishAfterDisposalOrANewerLoad(bool dispose)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = C5Page(fixture, progress);
        repository.Insert(C5Job("mine"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = Substitute.For<AuthenticationStateProvider>();
        var calls = 0;
        provider.GetAuthenticationStateAsync().Returns(_ =>
        {
            if (Interlocked.Increment(ref calls) != 1) return Task.FromResult(C5State());
            entered.SetResult();
            return release.Task;
        });
        SetPageProperty(page, "AuthStateProvider", provider);
        var pending = C5Invoke(page, "ToggleJobDetails", "mine");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        if (dispose) page.Dispose();
        else await InvokePage(page, "LoadOwnedJobsAsync");
        release.SetResult(C5State());
        await pending;
        Assert.Null(C5Field<BulkJob?>(page, "detailsJob"));
        Assert.Empty(C5Field<IReadOnlyList<BulkJobRow>>(page, "detailsRows"));
    }

    [Theory]
    [InlineData("authentication")]
    [InlineData("authorization")]
    [InlineData("initial-load")]
    public async Task C5InitializationCannotLeaveASubscriptionAfterDisposal(string suspendedAt)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = C5Page(fixture, progress);
        repository.Insert(C5Job("mine"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseState = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAuth = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = Substitute.For<AuthenticationStateProvider>();
        var calls = 0;
        provider.GetAuthenticationStateAsync().Returns(_ =>
        {
            var call = Interlocked.Increment(ref calls);
            if ((suspendedAt == "authentication" && call == 1) || (suspendedAt == "initial-load" && call == 2))
            {
                entered.SetResult();
                return releaseState.Task;
            }
            return Task.FromResult(C5State());
        });
        SetPageProperty(page, "AuthStateProvider", provider);
        if (suspendedAt == "authorization")
        {
            var auth = Substitute.For<IAuthorizationService>();
            auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
                .Returns(_ => { entered.SetResult(); return releaseAuth.Task; });
            SetPageProperty(page, "AuthorizationService", auth);
        }
        var pending = InvokePage(page, "OnInitializedAsync");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        var queue = (BulkJobService)typeof(Page).GetProperty("BulkJobs", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var subscription = typeof(BulkJobService).GetField("JobChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;
        if (suspendedAt == "initial-load") Assert.Contains(((Delegate)subscription.GetValue(queue)!).GetInvocationList(), d => ReferenceEquals(d.Target, page));
        page.Dispose();
        releaseState.TrySetResult(C5State());
        releaseAuth.TrySetResult(AuthorizationResult.Success());
        await pending;
        Assert.Null(subscription.GetValue(queue));
        Assert.Empty(C5Field<IReadOnlyList<BulkJob>>(page, "activeOwnedJobs"));
    }

    [Fact]
    public async Task C5JobHandlersGuardReentryBeforeAwaitAndUseTheirOwnBusyState()
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = C5Page(fixture, progress);
        repository.Insert(C5Job("mine"));
        SetField(page, "isLoading", true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = Substitute.For<AuthenticationStateProvider>();
        var calls = 0;
        provider.GetAuthenticationStateAsync().Returns(_ =>
        {
            if (Interlocked.Increment(ref calls) != 1) return Task.FromResult(C5State());
            entered.SetResult();
            return release.Task;
        });
        SetPageProperty(page, "AuthStateProvider", provider);
        var pending = InvokePage(page, "RefreshOwnedJobs");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(C5Field<bool>(page, "jobsBusy"));
        await InvokePage(page, "RefreshOwnedJobs");
        await C5Invoke(page, "ToggleJobDetails", "mine");
        await C5Invoke(page, "CancelOwnedJob", "mine");
        await C5Invoke(page, "RemoveOwnedJob", "mine");
        Assert.Equal(1, calls);
        Assert.Equal(BulkJobStatus.Queued, repository.Get("mine")!.Status);
        Assert.Empty(fixture.C5ControlAudits());
        release.SetResult(C5State());
        await pending;
        Assert.False(C5Field<bool>(page, "jobsBusy"));
        Assert.True(C5Field<bool>(page, "isLoading"));
        Assert.Single(C5Field<IReadOnlyList<BulkJob>>(page, "activeOwnedJobs"));
    }

    [Fact]
    public async Task C5LoadFailuresClearCachesAndManualRefreshReportsFailureWhileAutomaticLoadIsSilent()
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = C5Page(fixture, progress);
        repository.Insert(C5Job("mine"));
        await InvokePage(page, "RefreshOwnedJobs");
        var sink = Substitute.For<IActivityProgress>();
        var handle = Substitute.For<IActivityHandle>();
        sink.Begin(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ActivitySize>()).Returns(handle);
        SetPageProperty(page, "Progress", sink);
        using (var connection = new SqliteConnectionFactory(repository.DatabasePath).Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "DROP TABLE bulk_job_row; DROP TABLE bulk_job;";
            command.ExecuteNonQuery();
        }
        await InvokePage(page, "RefreshOwnedJobs");
        Assert.Empty(C5Field<IReadOnlyList<BulkJob>>(page, "activeOwnedJobs"));
        Assert.Contains("Could not load", C5Field<string>(page, "jobsLoadError"), StringComparison.Ordinal);
        Assert.Contains(handle.ReceivedCalls(), call => call.GetMethodInfo().Name == "Complete" && Equals(call.GetArguments()[0], false));
        await InvokePage(page, "LoadOwnedJobsAsync");
        Assert.Single(sink.ReceivedCalls(), call => call.GetMethodInfo().Name == "Begin");
    }

    [Fact]
    public void C5CallbackMarshallingFailuresAreCaughtAndLogged()
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, _) = C5Page(fixture, progress);
        var logger = Substitute.For<ILogger<Page>>();
        SetPageProperty(page, "Logger", logger);
        // With no renderer attached, InvokeAsync throws. The async-void callback must contain it.
        typeof(Page).GetMethod("OnOwnedJobChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, ["foreign-id-hint"]);
        Assert.Contains(logger.ReceivedCalls(), call => call.GetMethodInfo().Name == "Log"
            && Equals(call.GetArguments()[0], LogLevel.Warning)
            && call.GetArguments()[2]!.ToString()!.Contains("jobs callback failed", StringComparison.Ordinal));
    }

    private static (Page Page, BulkJobRepository Repository) C5Page(Fixture fixture, IActivityProgress progress)
    {
        var page = fixture.Page();
        var (queue, repository) = fixture.JobQueue();
        SetPageProperty(page, "BulkJobs", queue);
        SetPageProperty(page, "Progress", progress);
        SetPageProperty(page, "Comms10kService", fixture.Service);
        SetPageProperty(page, "ClientInfo", new ClientInfoService { IpAddress = "192.0.2.7" });
        SetPageProperty(page, "AuthStateProvider", C5Authentication(C5State().User));
        SetField(page, "authChecked", true);
        return (page, repository);
    }

    private static T C5Field<T>(Page page, string name) =>
        (T)typeof(Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
    private static Task C5Invoke(Page page, string method, params object[] args) =>
        (Task)typeof(Page).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args)!;
    private static AuthenticationState C5State() => new(new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, C5Owner), new Claim(ClaimTypes.GroupSid, C5GroupSid)], "test")));
    private static AuthenticationStateProvider C5Authentication(ClaimsPrincipal principal)
    {
        var provider = Substitute.For<AuthenticationStateProvider>();
        provider.GetAuthenticationStateAsync().Returns(new AuthenticationState(principal));
        return provider;
    }
    private static BulkJob C5Job(string id, BulkJobStatus status = BulkJobStatus.Queued, string owner = C5Owner,
        string module = Comms10kReplaceProcessor.ModuleName, string type = Comms10kReplaceJobPayload.JobType) => new()
        {
            Id = id,
            ModuleId = module,
            JobType = type,
            Status = status,
            SubmittedBy = owner,
            SubmittedIp = "192.0.2.7",
            Ticket = "INC123",
            PayloadJson = "{}",
            TotalRows = 2,
            SubmittedAtUtc = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc),
            StartedAtUtc = new DateTime(2026, 10, 1, 0, 1, 0, DateTimeKind.Utc),
            FinishedAtUtc = status is BulkJobStatus.Completed or BulkJobStatus.Interrupted
                ? new DateTime(2026, 10, 1, 0, 2, 0, DateTimeKind.Utc) : null,
        };
    private static void C5Row(BulkJobRepository repository, string id, int index, BulkJobRowStatus status, string message) =>
        repository.RecordRow(new BulkJobRow { JobId = id, RowIndex = index, Target = "members.csv", Status = status, Message = message, RecordedAtUtc = DateTime.UtcNow }, DateTime.UtcNow);
    private static string C5RenderText(Page page)
    {
        using var builder = new RenderTreeBuilder();
        typeof(Page).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [builder]);
        var frames = builder.GetFrames();
        return string.Join(" ", frames.Array.Take(frames.Count).Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Text => frame.TextContent,
            RenderTreeFrameType.Markup => frame.MarkupContent,
            _ => "",
        }));
    }

    private sealed class C5LivePolicy : IDisposable
    {
        private readonly TempDir _temp = new();
        private readonly ServiceProvider _provider;
        private readonly ModuleEnablementRepository _enablement;
        private readonly SectionAccessRepository _sections;
        public IAuthorizationService Authorization { get; }
        public C5LivePolicy(Fixture fixture)
        {
            var env = Substitute.For<IWebHostEnvironment>();
            env.ContentRootPath.Returns(_temp.Path);
            var catalog = new ModuleCatalog();
            var store = TestConfigStore.Create(_temp.Path);
            _enablement = new ModuleEnablementRepository(store);
            _enablement.SaveAll(new Dictionary<string, bool> { ["Comms10k"] = true });
            _sections = new SectionAccessRepository(store);
            _sections.SaveAll(new Dictionary<string, string[]> { ["Comms10k"] = [C5GroupSid] });
            var config = new ModuleConfigService(catalog, env, TestConfigStore.CreateModuleConfig(_temp.Path), NullLogger<ModuleConfigService>.Instance);
            var enabled = new ModuleEnablementService(catalog, env, config, _enablement, fixture.Config, NullLogger<ModuleEnablementService>.Instance);
            var sections = new SectionAccessService(fixture.Config, NullLogger<SectionAccessService>.Instance, env, catalog, _sections);
            var services = new ServiceCollection().AddLogging();
            services.AddAuthorization(options => options.AddPolicy("Comms10k", policy =>
                policy.RequireAuthenticatedUser().AddRequirements(new GroupAuthorizationRequirement("Comms10k", true))));
            services.AddSingleton<IAuthorizationHandler>(new GroupAuthorizationHandler(NullLogger<GroupAuthorizationHandler>.Instance, sections, catalog, enabled));
            _provider = services.BuildServiceProvider();
            Authorization = _provider.GetRequiredService<IAuthorizationService>();
        }
        public void Revoke(bool disableModule)
        {
            if (disableModule) _enablement.SaveAll(new Dictionary<string, bool> { ["Comms10k"] = false });
            else _sections.SaveAll(new Dictionary<string, string[]> { ["Comms10k"] = ["S-1-5-21-100-200-300-5555"] });
        }
        public void Dispose() { _provider.Dispose(); _temp.Dispose(); }
    }
}
#pragma warning restore BL0006
