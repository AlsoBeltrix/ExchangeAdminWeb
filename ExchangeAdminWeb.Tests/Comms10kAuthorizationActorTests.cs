using System.Security.Claims;
using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public sealed partial class Comms10kRefusalTests
{
    private const string C2ReviewFreshActor = "OTHER\\fresh-operator";

    [Theory]
    [InlineData("disabled")]
    [InlineData("removed-grant")]
    [InlineData("capture-unreadable")]
    public async Task C2ReviewCaptureRefusalsReportTheSameFreshActorInAuditAndNotification(string mode)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = await C5ReviewCachedPage(fixture, progress, true);
        SetField(page, "currentUser", "OLD\\page-initialization");
        SetField(page, "resolveResult", CompleteResolution());
        SetField(page, "confirmation", new Comms10kConfirmation([Address], "members.csv", "INC123", CompleteResolution().ResolvedMembers));
        var principal = new C2ReviewPrincipal(mode == "capture-unreadable");
        SetPageProperty(page, "AuthStateProvider", C5Authentication(principal));
        var services = new ServiceCollection().AddLogging();
        services.AddAuthorization(options => options.AddPolicy("Comms10k", policy =>
            policy.RequireAuthenticatedUser().AddRequirements(new GroupAuthorizationRequirement("Comms10k", true))));
        services.AddSingleton<IAuthorizationHandler>(new GroupAuthorizationHandler(
            NullLogger<GroupAuthorizationHandler>.Instance, fixture.Sections, new ModuleCatalog(), fixture.Enablement));
        using var provider = services.BuildServiceProvider();
        var livePolicy = provider.GetRequiredService<IAuthorizationService>();
        var authorization = Substitute.For<IAuthorizationService>();
        var authorized = false;
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), "Comms10k").Returns(async call =>
        {
            Assert.Same(principal, call.ArgAt<ClaimsPrincipal>(0));
            var result = await livePolicy.AuthorizeAsync(principal, "Comms10k");
            Assert.True(result.Succeeded);
            authorized = true;
            if (mode == "disabled") C2Revoke(fixture, true);
            if (mode == "removed-grant") C2Revoke(fixture, false);
            return result;
        });
        SetPageProperty(page, "AuthorizationService", authorization);

        await InvokeReplace(page);

        Assert.True(authorized);
        Assert.Equal(["a-private-active"], repository.GetActive().Select(job => job.Id));
        Assert.Empty(repository.GetActiveOwned("Comms10k", Comms10kReplaceJobPayload.JobType, C2ReviewFreshActor));
        C5ReviewAssertEmptyScope(page);
        Assert.Equal("OLD\\page-initialization", C5Field<string>(page, "currentUser"));
        var reason = mode == "capture-unreadable" ? "Authorization could not be verified." : "Authorization denied.";
        Assert.Equal(reason, C5Field<Comms10kUpdateResult>(page, "result").Message);
        Assert.Multiple(
            () => fixture.AssertAudit(reason, Comms10kOutcome.RefusedBeforeAnyChange, actor: "fresh-operator"),
            () => fixture.AssertNotification(reason, Comms10kOutcome.RefusedBeforeAnyChange, actor: C2ReviewFreshActor));
    }

    private sealed class C2ReviewPrincipal(bool unreadableRoles) : ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.Name, C2ReviewFreshActor), new Claim(ClaimTypes.GroupSid, C5GroupSid)], "test"))
    {
        public override bool IsInRole(string role) => unreadableRoles
            ? throw new InvalidOperationException("Token roles unavailable.") : base.IsInRole(role);
    }
}
