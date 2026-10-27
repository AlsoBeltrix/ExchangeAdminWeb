using System.Security.Claims;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using static ExchangeAdminWeb.Tests.SubmittedValueHarness;
using Page = ExchangeAdminWeb.Components.Pages.ConferenceRooms;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-74 (4), Conference Rooms. The two CSV applies - Room Finder's and Room Type's - completed their progress activity
/// from <c>result</c>, the result banner's field, in their finally. The success path publishes its result and then
/// awaits RefreshJobs, and the banner's dismiss - wired straight at <c>() =&gt; result = null</c> - stays clickable
/// meanwhile, so a dismiss there ended the bar as not succeeded, with no message, for a job that had been submitted:
/// prog-7's shape. Both now complete from their own local, as SetupSingleRoom does.
/// </summary>
/// <remarks>
/// The compiled handlers run by reflection with a real job store, a dormant ServiceNow (every ticket passes) and an
/// authorization that grants; the apply only queues a job, so no room service, Exchange or directory is reached.
/// <see cref="SubmittedValueHarness.RunWithChangeWhen"/> pumps each handler one continuation at a time and lands the
/// dismiss where a queued click would: once the success is on the banner and the handler is suspended in RefreshJobs.
/// The progress stand-in keeps how each activity ended.
/// </remarks>
public sealed class ConferenceRoomCsvApplyCompletionTests : IDisposable
{
    private const string Room = "room1@contoso.test";
    private const string Submitted = "Submitted 1 room(s) as a background job.";

    private readonly ExchangeWriteHarness _h = new();
    private readonly RecordingProgress _progress = new();
    private readonly BulkJobService _jobs;

    public ConferenceRoomCsvApplyCompletionTests()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_h.Dir, $"jobs-{Guid.NewGuid():N}.db"));
        new JobStoreMigrator(factory).Migrate();
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        _jobs = new BulkJobService(scopes, new BulkJobRepository(factory), new BulkJobProcessorRegistry([]), _h.Config,
            NullLogger<BulkJobService>.Instance);
    }

    public void Dispose() => _h.Dispose();

    public static TheoryData<string, bool> Applies() => new()
    {
        { "ApplyFinderCsv", true },
        { "ApplyTypeCsv", true },
        { "ApplyFinderCsv", false },
        { "ApplyTypeCsv", false },
    };

    // The finding: the banner dismissed while the job list refreshes, after the success is published. The job was
    // submitted, and the bar now says so; the banner is gone, as the operator asked. Before, the bar ended not succeeded,
    // with no message. The control is the same run with the banner left up, so an ending that succeeds above is the
    // local, not a run that never published.
    [Theory]
    [MemberData(nameof(Applies))]
    public void ACsvApplyWhoseBannerIsDismissedWhileTheJobListRefreshes_StillEndsTheBarSucceeded(string apply, bool dismissed)
    {
        var page = PageFor(apply);

        RunWithChangeWhen(() => Call(page, apply),
            when: () => Field<RoomOperationResult>(page, "result") is { Success: true },
            change: () =>
            {
                if (dismissed)
                    SetField(page, "result", null);
            });

        var bar = Assert.Single(_progress.Handles, handle => handle.Label == "Applying the room CSV");
        Assert.True(bar.Outcome?.Success, $"the bar ended {bar.Outcome}");
        Assert.StartsWith(Submitted, bar.Outcome?.Message, StringComparison.Ordinal);
        Assert.Equal(dismissed, Field<RoomOperationResult>(page, "result") is null);
        Assert.NotNull(Field<BulkJob>(page, apply == "ApplyFinderCsv" ? "finderJob" : "typeJob"));
        Assert.False(Field<bool>(page, "isCsvProcessing"));
    }

    // What the local keeps as it was: ReauthorizeAsync writes its refusal into the banner itself, and the bar went on
    // saying why from the field. It now takes the same words from the handler - still "Authorization denied.", not an
    // ending with no message - and nothing is queued.
    [Theory]
    [InlineData("ApplyFinderCsv")]
    [InlineData("ApplyTypeCsv")]
    public async Task ACsvApplyRefusedAtTheAuthorizationRecheck_StillEndsTheBarSayingWhy(string apply)
    {
        var page = PageFor(apply, granted: false);

        await Call(page, apply);

        var bar = Assert.Single(_progress.Handles, handle => handle.Label == "Applying the room CSV");
        Assert.Equal((false, "Authorization denied."), bar.Outcome);
        Assert.Equal("Authorization denied.", Field<RoomOperationResult>(page, "result")?.Message);
        Assert.Null(Field<BulkJob>(page, apply == "ApplyFinderCsv" ? "finderJob" : "typeJob"));
    }

    private Page PageFor(string apply, bool granted = true)
    {
        var page = new Page();
        Set(page, "Audit", _h.Audit);
        Set(page, "ServiceNow", new ServiceNowService(_h.Config, NullLogger<ServiceNowService>.Instance, HttpClients()));
        Set(page, "AuthStateProvider", Authentication());
        Set(page, "AuthorizationService", Authorization(granted));
        Set(page, "BulkJobs", _jobs);
        Set(page, "Logger", NullLogger<Page>.Instance);
        Set(page, "Progress", _progress);
        SetField(page, "currentUser", @"CONTOSO\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");

        if (apply == "ApplyFinderCsv")
        {
            SetField(page, "csvFinderTicket", "INC100");
            SetField(page, "finderCsvData", new List<FinderCsvRow> { new() { Email = Room, Building = "Building 1", Capacity = 8 } });
            SetField(page, "finderPreview", new List<RoomFinderPreviewRow> { new() { RowIndex = 0, Email = Room, RoomResolved = true } });
        }
        else
        {
            SetField(page, "csvTypeTicket", "INC100");
            SetField(page, "typeCsvData", new List<TypeCsvRow> { new() { Email = Room, Type = "Standard" } });
            SetField(page, "typePreview", new List<RoomTypePreviewRow> { new() { RowIndex = 0, Email = Room, RoomResolved = true } });
        }

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
        var state = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, @"CONTOSO\operator")], "test")));
        var provider = Substitute.For<AuthenticationStateProvider>();
        provider.GetAuthenticationStateAsync().Returns(Task.FromResult(state));
        return provider;
    }

    private static IAuthorizationService Authorization(bool granted)
    {
        var service = Substitute.For<IAuthorizationService>();
        service.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(granted ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return service;
    }
}
