using System.Security.Claims;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.Comms10k;

namespace ExchangeAdminWeb.Tests;

public sealed partial class Comms10kRefusalTests
{
    private const string C5ReviewOtherOwner = "OTHER\\operator";
    private const string C5ReviewPrivateReason = "Actor A private recorded reason";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C5ReviewActorSwitchClearsRenderedCachesForDetailsAndHide(bool hide)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = await C5ReviewCachedPage(fixture, progress, hide);
        repository.Insert(C5Job("b-owned", owner: C5ReviewOtherOwner));
        var oldGeneration = C5Field<int>(page, "jobsGeneration");
        SetPageProperty(page, "AuthStateProvider", C5Authentication(C5ReviewPrincipal(C5ReviewOtherOwner)));

        await C5Invoke(page, "ToggleJobDetails", "a-private-finished");

        C5ReviewAssertEmptyScope(page);
        Assert.True(C5Field<int>(page, "jobsGeneration") > oldGeneration);
        Assert.False(C5Field<bool>(page, "jobsActionSucceeded"));
        Assert.Equal("Job unavailable or this action is no longer allowed.", C5Field<string>(page, "jobsActionMessage"));
        await C5Invoke(page, "CancelOwnedJob", "a-private-active");
        Assert.False(C5Field<bool>(page, "jobsActionSucceeded"));
        await C5Invoke(page, "RemoveOwnedJob", "a-private-finished");
        Assert.False(C5Field<bool>(page, "jobsActionSucceeded"));
        Assert.Equal(BulkJobStatus.Queued, repository.Get("a-private-active")!.Status);
        Assert.NotNull(repository.Get("a-private-finished"));
        await InvokePage(page, "RefreshOwnedJobs");
        Assert.Equal(["b-owned"], C5Field<IReadOnlyList<BulkJob>>(page, "activeOwnedJobs").Select(j => j.Id));
        Assert.Empty(C5Field<IReadOnlyList<BulkJob>>(page, "finishedOwnedJobs"));
        Assert.DoesNotContain(C5ReviewPrivateReason, C5RenderText(page), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C5ReviewOldActorContinuationCannotPublishOrClearTheNewActor(bool oldDenial)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = await C5ReviewCachedPage(fixture, progress, true);
        repository.Insert(C5Job("b-owned", owner: C5ReviewOtherOwner));
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
        var older = InvokePage(page, "LoadOwnedJobsAsync");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            SetPageProperty(page, "AuthStateProvider", C5Authentication(C5ReviewPrincipal(C5ReviewOtherOwner)));
            await C5Invoke(page, "ToggleJobDetails", "a-private-finished");
            C5ReviewAssertEmptyScope(page);
            await InvokePage(page, "RefreshOwnedJobs");
        }
        finally
        {
            release.TrySetResult(oldDenial ? AuthorizationResult.Failed() : AuthorizationResult.Success());
            await older;
        }
        Assert.Equal(["b-owned"], C5Field<IReadOnlyList<BulkJob>>(page, "activeOwnedJobs").Select(j => j.Id));
        Assert.True(C5Field<bool>(page, "jobsAllowed"));
        Assert.Null(C5Field<string?>(page, "jobsLoadError"));
        Assert.DoesNotContain(C5ReviewPrivateReason, C5RenderText(page), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C5ReviewIdentityCasingKeepsTheSameActorsCacheAndPaging(bool unicode)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var actor = unicode ? "EXAMPLE\\Op\u00e9rateur" : C5Owner;
        var (page, _) = await C5ReviewCachedPage(fixture, progress, true, actor);
        SetPageProperty(page, "AuthStateProvider", C5Authentication(C5ReviewPrincipal(actor.ToUpperInvariant())));
        await C5Invoke(page, "ToggleJobDetails", "a-private-finished");
        Assert.True(C5Field<bool>(page, "jobsAllowed"));
        Assert.Equal("a-private-active", Assert.Single(C5Field<IReadOnlyList<BulkJob>>(page, "activeOwnedJobs")).Id);
        Assert.Equal("a-private-finished", Assert.Single(C5Field<IReadOnlyList<BulkJob>>(page, "finishedOwnedJobs")).Id);
        Assert.Null(C5Field<BulkJob?>(page, "detailsJob"));
        Assert.Equal("Actor A submitted a-private-active", C5Field<string>(page, "submittedNotice"));
        Assert.Equal(3, C5Field<int>(page, "historyPage"));
        Assert.True(C5Field<bool>(page, "hasMoreHistory"));
    }

    [Theory]
    [InlineData("section")]
    [InlineData("disabled")]
    [InlineData("unauthenticated")]
    [InlineData("blank")]
    [InlineData("spaces")]
    [InlineData("display-only")]
    public async Task C5ReviewSubmissionDenialClearsPanelAndInvalidatesOlderLoads(string denial)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        using var policy = new C5LivePolicy(fixture);
        var (page, repository) = await C5ReviewCachedPage(fixture, progress, true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var auth = Substitute.For<IAuthorizationService>();
        var calls = 0;
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>()).Returns(call =>
        {
            if (Interlocked.Increment(ref calls) != 1)
                return policy.Authorization.AuthorizeAsync(call.ArgAt<ClaimsPrincipal>(0), call.ArgAt<object?>(1), call.ArgAt<string>(2));
            entered.SetResult();
            return release.Task;
        });
        SetPageProperty(page, "AuthorizationService", auth);
        var older = InvokePage(page, "LoadOwnedJobsAsync");
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        string deniedFrames;
        try
        {
            if (denial is "section" or "disabled") policy.Revoke(denial == "disabled");
            else SetPageProperty(page, "AuthStateProvider", C5Authentication(C5ReviewPrincipal(
                denial == "blank" ? "" : denial == "spaces" ? "  " : C5Owner,
                authenticated: denial != "unauthenticated", nameClaim: denial != "display-only")));
            SetField(page, "resolveResult", CompleteResolution());
            SetField(page, "confirmation", new Comms10kConfirmation([Address], "members.csv", "INC123", CompleteResolution().ResolvedMembers));
            await InvokeReplace(page);
            deniedFrames = C5RenderText(page);
        }
        finally
        {
            release.TrySetResult(AuthorizationResult.Success());
            await older;
        }
        C5ReviewAssertEmptyScope(page);
        Assert.DoesNotContain("a-private-", deniedFrames, StringComparison.Ordinal);
        Assert.DoesNotContain(C5ReviewPrivateReason, deniedFrames, StringComparison.Ordinal);
        Assert.Equal("Authorization denied.", C5Field<Comms10kUpdateResult>(page, "result").Message);
        Assert.Contains("denied", C5Field<string>(page, "jobsLoadError"), StringComparison.Ordinal);
        Assert.Equal(["a-private-active"], repository.GetActive().Select(j => j.Id));
        Assert.NotNull(repository.Get("a-private-finished"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task C5ReviewAuthorizedSubmissionSwitchesScopeBeforeRefusalOrNotice(bool completeFile)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = await C5ReviewCachedPage(fixture, progress, true);
        SetPageProperty(page, "AuthStateProvider", C5Authentication(C5ReviewPrincipal(C5ReviewOtherOwner)));
        if (completeFile)
        {
            SetField(page, "resolveResult", CompleteResolution());
            SetField(page, "confirmation", new Comms10kConfirmation([Address], "approved.csv", "INC123", CompleteResolution().ResolvedMembers));
        }
        await InvokeReplace(page);
        Assert.DoesNotContain("a-private-", C5RenderText(page), StringComparison.Ordinal);
        Assert.DoesNotContain(C5ReviewPrivateReason, C5RenderText(page), StringComparison.Ordinal);
        Assert.Equal(0, C5Field<int>(page, "historyPage"));
        if (completeFile)
        {
            var submitted = Assert.Single(repository.GetActiveOwned(Comms10kReplaceProcessor.ModuleName, Comms10kReplaceJobPayload.JobType, C5ReviewOtherOwner));
            Assert.Contains(submitted.Id, C5Field<string>(page, "submittedNotice"), StringComparison.Ordinal);
            Assert.Contains("Submitted.", C5RenderText(page), StringComparison.Ordinal);
            Assert.Equal("INC123", submitted.Ticket);
            Assert.Null(C5Field<Comms10kUpdateResult?>(page, "result"));
        }
        else
        {
            C5ReviewAssertEmptyScope(page);
            Assert.False(C5Field<Comms10kUpdateResult>(page, "result").Success);
            Assert.Empty(repository.GetActiveOwned(Comms10kReplaceProcessor.ModuleName, Comms10kReplaceJobPayload.JobType, C5ReviewOtherOwner));
        }
    }

    private static ClaimsPrincipal C5ReviewPrincipal(string actor, bool authenticated = true, bool nameClaim = true) =>
        new(new ClaimsIdentity([new Claim(nameClaim ? ClaimTypes.Name : "displayName", actor),
            new Claim(ClaimTypes.GroupSid, C5GroupSid)], authenticated ? "test" : null));

    private static async Task<(Page Page, BulkJobRepository Repository)> C5ReviewCachedPage(
        Fixture fixture, IActivityProgress progress, bool details, string actor = C5Owner)
    {
        var (page, repository) = C5Page(fixture, progress);
        SetPageProperty(page, "AuthStateProvider", C5Authentication(C5ReviewPrincipal(actor)));
        var active = C5Job("a-private-active", owner: actor);
        var finished = C5Job("a-private-finished", BulkJobStatus.Completed, actor);
        active.Message = C5ReviewPrivateReason;
        finished.Message = C5ReviewPrivateReason;
        repository.Insert(active);
        repository.Insert(finished);
        C5Row(repository, finished.Id, 1, BulkJobRowStatus.Partial, C5ReviewPrivateReason);
        await InvokePage(page, "RefreshOwnedJobs");
        if (details) await C5Invoke(page, "ToggleJobDetails", finished.Id);
        SetField(page, "submittedNotice", "Actor A submitted a-private-active");
        SetField(page, "historyPage", 3);
        SetField(page, "hasMoreHistory", true);
        Assert.Contains(C5ReviewPrivateReason, C5RenderText(page), StringComparison.Ordinal);
        return (page, repository);
    }

    private static void C5ReviewAssertEmptyScope(Page page)
    {
        Assert.Empty(C5Field<IReadOnlyList<BulkJob>>(page, "activeOwnedJobs"));
        Assert.Empty(C5Field<IReadOnlyList<BulkJob>>(page, "finishedOwnedJobs"));
        Assert.Null(C5Field<BulkJob?>(page, "detailsJob"));
        Assert.Empty(C5Field<IReadOnlyList<BulkJobRow>>(page, "detailsRows"));
        Assert.Null(C5Field<string?>(page, "submittedNotice"));
        Assert.Equal(0, C5Field<int>(page, "historyPage"));
        Assert.False(C5Field<bool>(page, "hasMoreHistory"));
        Assert.False(C5Field<bool>(page, "jobsAllowed"));
        Assert.DoesNotContain("a-private-", C5RenderText(page), StringComparison.Ordinal);
        Assert.DoesNotContain(C5ReviewPrivateReason, C5RenderText(page), StringComparison.Ordinal);
    }
}
