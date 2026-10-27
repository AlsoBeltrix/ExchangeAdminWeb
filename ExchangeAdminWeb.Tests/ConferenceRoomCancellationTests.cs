using System.Collections;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Security.Claims;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using Page = ExchangeAdminWeb.Components.Pages.ConferenceRooms;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-59, Conference Rooms. A single-room Room Finder apply fetched its on-prem AD credential with no cancellation
/// token, so an operator's cancel left the vault request running and the row went on to write. The page's activity token now
/// reaches the two preflights' credential fetches - the City/State/Country one and the on-prem room-list one - both before
/// Set-Place, so a cancel there ends the row with nothing written, filed Cancelled in the app's phrase for one (kd-57). The
/// two fetches after Set-Place take none, on purpose: a cancel there would leave the room half-configured. kd-73 (2): a cancel
/// landing after the preflights' fetches and before Set-Place is caught by one check just before Set-Place, the same way.
/// </summary>
/// <remarks>
/// The real service over <see cref="ExchangeWriteHarness"/> (stand-in Exchange and directory), its credentials through a
/// real <see cref="ModuleCredentialService"/> and a real <see cref="DelineaService"/> whose secret request waits until its
/// caller cancels (<see cref="CancellableVaultCredentials"/>). The page's handler runs by reflection, as
/// <c>ConferenceRoomDirectoryAuditTests</c> runs it, with an activity whose token the test cancels as the navigation guard's
/// OK does. Every wait is bounded. No vault, no directory, no Exchange.
/// </remarks>
public sealed class ConferenceRoomCancellationTests : IDisposable
{
    private const string Typed = "room1@contoso.test";
    private const string Building = "Building 1";
    private static readonly Guid Checked = Guid.Parse("0f0f0f0f-1111-4222-8333-444444444444");
    private const string CheckedObject = "5a5a5a5a-0000-4000-8000-000000000001";
    private static readonly string List = ConferenceRoomService.BuildRoomListName(Building);
    private static readonly TimeSpan Bound = CancellableVaultCredentials.Bound;

    // Exchange Online's refusal of a cloud add to a list synchronized from on-premises.
    private const string OnPremMastered =
        "The operation on the group failed because it's out of the current user's write scope. The action can't be "
        + "performed on the object because the object is being synchronized from your on-premises organization.";

    private readonly ExchangeWriteHarness _h = new();
    private readonly CancellableVaultCredentials _credentials = new("ConferenceRooms");
    private readonly SectionAccessService _sectionAccess;
    private EmailService? _pageEmail;

    public ConferenceRoomCancellationTests()
    {
        _h.AddMailbox(Typed, Checked, recipientTypeDetails: "RoomMailbox");
        _h.DirectoryObjects[CheckedObject] = new Hashtable
        {
            ["ObjectGUID"] = Guid.Parse(CheckedObject),
            ["DistinguishedName"] = "CN=Room 1,OU=Rooms,DC=contoso,DC=test",
        };
        var store = TestConfigStore.Create(_h.Dir);
        new SectionAccessRepository(store).SaveAll(new Dictionary<string, string[]> { ["ConferenceRooms"] = ["AllUsers"] });
        _sectionAccess = new SectionAccessService(_h.Config, NullLogger<SectionAccessService>.Instance, Environment(),
            new ModuleCatalog(), new SectionAccessRepository(store));
    }

    public void Dispose()
    {
        _credentials.Dispose();
        _h.Dispose();
    }

    /// <summary>The room service over the harness, its credentials through the fixture's vault.</summary>
    private sealed class Service : ConferenceRoomService
    {
        private readonly ExchangeWriteHarness _h;

        public Service(ExchangeWriteHarness h, ModuleCredentialService credentials)
            : base(h.Pool, null!, h.ModuleConfig, credentials, h.Trace, h.Config, NullLogger<ConferenceRoomService>.Instance) => _h = h;

        /// <summary>Run as a lookup whose purpose contains <see cref="BeforeLookupFor"/> starts: where a test cancels.</summary>
        public Action? BeforeLookup { get; init; }

        public string? BeforeLookupFor { get; init; }

        /// <summary>
        /// kd-73. Run once a lookup whose purpose contains <see cref="AfterLookupFor"/> has come back with its credential:
        /// where a test cancels after the fetch, while the preflight's directory reads and the room list's resolution run.
        /// </summary>
        public Action? AfterLookup { get; init; }

        public string? AfterLookupFor { get; init; }

        protected override Task<ModuleCredentialLookup> LookupDirectoryCredentialAsync(string purpose, CancellationToken cancellationToken)
        {
            if (BeforeLookupFor is { } text && purpose.Contains(text, StringComparison.Ordinal))
                BeforeLookup?.Invoke();
            var lookup = base.LookupDirectoryCredentialAsync(purpose, cancellationToken);
            return AfterLookupFor is { } after && purpose.Contains(after, StringComparison.Ordinal) ? ThenAsync(lookup) : lookup;
        }

        private async Task<ModuleCredentialLookup> ThenAsync(Task<ModuleCredentialLookup> lookup)
        {
            var answered = await lookup;
            AfterLookup?.Invoke();
            return answered;
        }

        protected override Runspace OpenDirectoryRunspace() => _h.OpenDirectoryRunspace();
    }

    private static ValidatedMailbox Room() => ExchangeWriteHarness.Validated(Checked, CheckedObject);

    private static Task<RoomOperationResult> Apply(Service service, CancellationToken cancellationToken, string city = "") =>
        service.SetRoomMetadataAndListAsync(Typed, Room(), city, Building, 8, "2", "Second", "TV", "Polycom", "", "",
            "GMT Standard Time", cancellationToken);

    // The finding, at the City/State/Country preflight: before, the cancel reached nothing, the vault request ran its course
    // and the row went on to Set-Place.
    [Fact]
    public async Task ACancelDuringTheLocationPreflightsCredentialRead_EndsTheRowBeforeAnyWrite()
    {
        using var cancel = new CancellationTokenSource();
        var apply = Apply(new Service(_h, _credentials.Service), cancel.Token, city: "Dublin");
        await _credentials.SecretRequested.WaitAsync(Bound, TestContext.Current.CancellationToken);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.True(_credentials.RequestCancelled);
        Assert.Empty(_h.Writes);
    }

    // The same at the on-prem room-list preflight: a list synchronized from on-premises, no City/State/Country, so the
    // list's credential fetch is the row's first.
    [Fact]
    public async Task ACancelDuringTheRoomListPreflightsCredentialRead_EndsTheRowBeforeAnyWrite()
    {
        _h.ListSyncState[List] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;
        using var cancel = new CancellationTokenSource();
        var apply = Apply(new Service(_h, _credentials.Service), cancel.Token);
        await _credentials.SecretRequested.WaitAsync(Bound, TestContext.Current.CancellationToken);

        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply.WaitAsync(Bound, TestContext.Current.CancellationToken));
        Assert.True(_credentials.RequestCancelled);
        Assert.Empty(_h.Writes);
    }

    // kd-73 (2), the gap kd-59 left: a cancel landing once the last preflight's credential has come back - here the room-list
    // preflight's, the second of two - while its directory reads run, which take no token. Before, nothing looked at the
    // token again and the row went on to Set-Place. The check just before Set-Place ends it there, nothing written.
    [Fact]
    public async Task ACancelAfterTheLastPreflightsCredentialRead_EndsTheRowBeforeSetPlace()
    {
        _h.ListSyncState[List] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;
        _credentials.WaitsAtSecretRequest = int.MaxValue;
        using var cancel = new CancellationTokenSource();
        var service = new Service(_h, _credentials.Service)
        {
            AfterLookupFor = "on-prem room-list membership preflight",
            AfterLookup = cancel.Cancel,
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Apply(service, cancel.Token, city: "Dublin").WaitAsync(Bound, TestContext.Current.CancellationToken));

        Assert.True(cancel.IsCancellationRequested);
        Assert.False(_credentials.RequestCancelled);
        Assert.Empty(_h.CallsTo("Set-Place"));
        Assert.Empty(_h.Writes);
    }

    // kd-73 (2) at the page: the activity's token cancelled once the City/State/Country preflight's credential has come back,
    // while its directory reads and the room list's resolution run. The row ends at the check before Set-Place: nothing
    // written, shown and filed Cancelled, the administrators told - as for a cancel at the fetch itself.
    [Fact]
    public async Task ACancelOfTheSetupsActivity_AfterThePreflightsCredentialRead_IsFiledCancelled_WithNothingWritten()
    {
        _credentials.WaitsAtSecretRequest = int.MaxValue;
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var service = new Service(_h, _credentials.Service)
        {
            AfterLookupFor = "Set-ADUser preflight",
            AfterLookup = cancel.Cancel,
        };
        var page = BuildPage(service, progress, city: "Dublin");

        await SetupSingleRoom(page).WaitAsync(Bound, TestContext.Current.CancellationToken);

        const string Cancelled = "Cancelled by the operator. Nothing was written.";
        Assert.True(cancel.IsCancellationRequested);
        Assert.False(_credentials.RequestCancelled);
        Assert.Empty(_h.CallsTo("Set-Place"));
        Assert.Empty(_h.Writes);
        var shown = Assert.IsType<RoomOperationResult>(GetField(page, "result"));
        Assert.False(shown.Success);
        Assert.Equal(Cancelled, shown.Message);
        Assert.Equal((false, Cancelled), Assert.Single(progress.Handles).Outcome);
        Assert.False((bool)GetField(page, "isLoading")!);
        var row = Assert.Single(_h.AuditRows(), r => r.GetProperty("action").GetString() == "ConferenceRooms_SetMetadata");
        Assert.Equal("Cancelled", row.GetProperty("result").GetString());
        Assert.Equal(Cancelled, row.GetProperty("error").GetString());
        var (success, error) = AdminNotification();
        Assert.False(success);
        Assert.Equal(Cancelled, error);
    }

    // The deliberate limit: once Set-Place has committed, a cancel does not stop the row half-way - the room would be left
    // half-configured, the partial the preflights exist to prevent. The row finishes as it always did.
    [Fact]
    public async Task ACancelAfterSetPlace_LetsTheRowFinish_RatherThanLeaveTheRoomHalfConfigured()
    {
        _credentials.WaitsAtSecretRequest = int.MaxValue;
        using var cancel = new CancellationTokenSource();
        var service = new Service(_h, _credentials.Service)
        {
            BeforeLookupFor = "Set-ADUser synced attributes",
            BeforeLookup = cancel.Cancel,
        };

        var result = await Apply(service, cancel.Token, city: "Dublin").WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.True(cancel.IsCancellationRequested);
        Assert.True(result.Success, result.Message);
        Assert.Single(_h.CallsTo("Set-Place"));
        Assert.Single(_h.CallsTo("Set-ADUser"));
    }

    // The finding at the page: the activity's token is the one the navigation guard cancels, and the page passes it.
    [Fact]
    public async Task ACancelOfTheSetupsActivity_DuringAPreflightsCredentialRead_IsFiledCancelled_WithNothingWritten()
    {
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var page = BuildPage(new Service(_h, _credentials.Service), progress, city: "Dublin");

        var run = SetupSingleRoom(page);
        await _credentials.SecretRequested.WaitAsync(Bound, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await run.WaitAsync(Bound, TestContext.Current.CancellationToken);

        const string Cancelled = "Cancelled by the operator. Nothing was written.";
        Assert.True(_credentials.RequestCancelled);
        Assert.Empty(_h.Writes);
        var shown = Assert.IsType<RoomOperationResult>(GetField(page, "result"));
        Assert.False(shown.Success);
        Assert.Equal(Cancelled, shown.Message);
        Assert.Equal((false, Cancelled), Assert.Single(progress.Handles).Outcome);
        Assert.False((bool)GetField(page, "isLoading")!);
        var row = Assert.Single(_h.AuditRows(), r => r.GetProperty("action").GetString() == "ConferenceRooms_SetMetadata");
        Assert.Equal("Cancelled", row.GetProperty("result").GetString());
        Assert.Equal(Cancelled, row.GetProperty("error").GetString());
        var (success, error) = AdminNotification();
        Assert.False(success);
        Assert.Equal(Cancelled, error);
    }

    // The control, kd-14's and kd-37's rule: a vault request that gives up by itself, with nobody cancelling, is a credential
    // that could not be read - the preflight's refusal, filed Failed, as before. Not the operator's cancel.
    [Fact]
    public async Task AVaultRequestThatTimesOutUnasked_IsStillTheCredentialRefusal_NotACancel()
    {
        _credentials.TimesOut = true;
        var progress = new RecordingProgress();
        var page = BuildPage(new Service(_h, _credentials.Service), progress, city: "Dublin");

        await SetupSingleRoom(page).WaitAsync(Bound, TestContext.Current.CancellationToken);

        var refusal = $"{ConferenceRoomService.DirectoryCredentialRefusal(ModuleCredentialFailure.NotReturned)}. City/State/Country could not be written. Nothing was written.";
        Assert.Empty(_h.Writes);
        var shown = Assert.IsType<RoomOperationResult>(GetField(page, "result"));
        Assert.Equal(refusal, shown.Message);
        var row = Assert.Single(_h.AuditRows(), r => r.GetProperty("action").GetString() == "ConferenceRooms_SetMetadata");
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal(refusal, row.GetProperty("error").GetString());
    }

    // kd-57's rule, the other control: a cancellation the operator did not ask for - the activity's token is live - is not
    // filed as theirs. It stays a row that failed, in its own words.
    [Fact]
    public async Task ACancellationTheOperatorDidNotAskFor_IsStillARowThatFailed()
    {
        var progress = new RecordingProgress();
        var service = new Service(_h, _credentials.Service)
        {
            BeforeLookupFor = "Set-ADUser preflight",
            BeforeLookup = () => throw new OperationCanceledException("the directory read was cancelled"),
        };
        var page = BuildPage(service, progress, city: "Dublin");

        await SetupSingleRoom(page).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Empty(_h.Writes);
        var shown = Assert.IsType<RoomOperationResult>(GetField(page, "result"));
        Assert.Equal("the directory read was cancelled", shown.Message);
        var row = Assert.Single(_h.AuditRows(), r => r.GetProperty("action").GetString() == "ConferenceRooms_SetMetadata");
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal(shown.Message, row.GetProperty("error").GetString());
    }

    // ---- the page harness, as ConferenceRoomDirectoryAuditTests builds it ------------------------------------------------

    private static Task SetupSingleRoom(Page page) =>
        (Task)typeof(Page).GetMethod("SetupSingleRoom", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!;

    private (bool Success, string? Error) AdminNotification()
    {
        var call = Assert.Single(_pageEmail!.ReceivedCalls(), c => c.GetMethodInfo().Name == nameof(EmailService.SendAdminNotificationAsync)
            && (c.GetArguments()[2] as string) == "ConferenceRooms_SetMetadata");
        var arguments = call.GetArguments();
        return ((bool)arguments[3]!, (string?)arguments[6]);
    }

    private Page BuildPage(ConferenceRoomService service, RecordingProgress progress, string city)
    {
        var page = new Page();
        SetProperty(page, "RoomService", service);
        SetProperty(page, "Audit", _h.Audit);
        _pageEmail = Substitute.For<EmailService>(_h.Config, NullLogger<EmailService>.Instance);
        SetProperty(page, "Email", _pageEmail);
        SetProperty(page, "ServiceNow", new ServiceNowService(_h.Config, NullLogger<ServiceNowService>.Instance, HttpClients()));
        SetProperty(page, "AuthStateProvider", Authentication());
        SetProperty(page, "AuthorizationService", Authorization());
        SetProperty(page, "OpTrace", _h.Trace);
        SetProperty(page, "Logger", NullLogger<Page>.Instance);
        SetProperty(page, "ProtectionGate", Gate());
        SetProperty(page, "Progress", progress);

        SetField(page, "currentUser", @"CONTOSO\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "roomEmail", Typed);
        SetField(page, "ticketNumber", "INC100");
        SetField(page, "city", city);
        SetField(page, "building", Building);
        SetField(page, "capacity", 8);
        SetField(page, "floor", "2");
        SetField(page, "floorLabel", "Second");
        SetField(page, "displayDevice", "TV");
        SetField(page, "videoDevice", "Polycom");
        SetField(page, "timezone", "GMT Standard Time");
        return page;
    }

    /// <summary>A protected-principal check that resolves every target to the synced room, unprotected.</summary>
    private sealed class RoomResolvingCheck(IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
        ProtectedPrincipalRepository repo, DelineaService delinea)
        : ProtectedPrincipalService(env, config, moduleConfig, repo, delinea, NullLogger<ProtectedPrincipalService>.Instance)
    {
        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveExchangeTargetAsync(string identity) =>
            Task.FromResult<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)>((
                new ResolvedDirectoryPrincipal("Test", "Room 1", Typed, null, Typed, null, CheckedObject, null, ExchangeGuid: Checked),
                ResolutionStatus.Resolved));

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target) =>
            Task.FromResult(ProtectedPrincipalResult.NotProtected());
    }

    private ConferenceRoomProtectionGate Gate()
    {
        var env = Environment();
        var extLog = new ExtendedLogService(_h.Config, env, TestConfigStore.CreateAppSettings(_h.Dir), NullLogger<ExtendedLogService>.Instance);
        var delinea = new DelineaService(HttpClients(), _h.Config, NullLogger<DelineaService>.Instance, extLog, _h.Trace);
        var check = new RoomResolvingCheck(env, _h.Config, _h.ModuleConfig, TestConfigStore.CreateProtectedPrincipal(_h.Dir), delinea);
        var servicers = new ProtectedPrincipalServicerService(_sectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance);
        return new ConferenceRoomProtectionGate(check, servicers, NullLogger<ConferenceRoomProtectionGate>.Instance);
    }

    private IWebHostEnvironment Environment()
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_h.Dir);
        return env;
    }

    private static IHttpClientFactory HttpClients()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        return factory;
    }

    private static AuthenticationStateProvider Authentication()
    {
        var state = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, @"CONTOSO\operator")], "test")));
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

    private static void SetProperty(Page page, string name, object value) =>
        typeof(Page).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static void SetField(Page page, string name, object? value) =>
        typeof(Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static object? GetField(Page page, string name) =>
        typeof(Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page);
}
