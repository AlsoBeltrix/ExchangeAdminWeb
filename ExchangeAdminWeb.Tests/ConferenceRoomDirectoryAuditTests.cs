using System.Collections;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.ConferenceRooms;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a2g: a Room Finder audit row names a written directory object only when an Active Directory
/// write succeeded, and names the object the service says it wrote. Both rows used to carry
/// <c>writtenDirectoryObject</c> on every Finder apply, whatever Active Directory was or was not told.
/// </summary>
/// <remarks>
/// Both audit surfaces, driven for real, not scanned. The page: a real <see cref="Page"/> instance
/// with its injected services set through reflection (the <see cref="OutOfOfficeRequestCaptureTests"/>
/// idiom) and its single-room handler invoked. The bulk path: the real
/// <see cref="ConferenceRoomBulkProcessor"/>. Both over the real <see cref="ConferenceRoomService"/> on
/// <see cref="ExchangeWriteHarness"/>, the real <see cref="ConferenceRoomProtectionGate"/> (its
/// resolver scripted to the synced room) and the real audit, read back from its JSONL file.
/// </remarks>
public sealed class ConferenceRoomDirectoryAuditTests : IDisposable
{
    private const string Typed = "room1@contoso.test";
    private const string Building = "Building 1";
    private static readonly Guid Checked = Guid.Parse("0f0f0f0f-1111-4222-8333-444444444444");
    private static readonly Guid Other = Guid.Parse("0f0f0f0f-5555-4666-8777-888888888888");
    private const string CheckedObject = "5a5a5a5a-0000-4000-8000-000000000001";

    // Exchange Online's refusal of a cloud add to a list synchronized from on-premises.
    private const string OnPremMastered =
        "The operation on the group failed because it's out of the current user's write scope. The action can't be "
        + "performed on the object because the object is being synchronized from your on-premises organization.";

    // A write error that is not a value already there (aud-a2n).
    private const string TimedOut = "The operation was aborted because the client side timeout limit was exceeded.";

    private readonly ExchangeWriteHarness _h = new();
    private readonly ActivityProgressService _progress = new();
    private readonly SectionAccessService _sectionAccess;

    // aud-a2o. The row the bulk surface returned - what a job's row table shows - on the last bulk apply.
    private BulkJobRowOutcome? _bulkRow;

    public ConferenceRoomDirectoryAuditTests()
    {
        _h.AddMailbox(Typed, Checked, recipientTypeDetails: "RoomMailbox");
        _h.DirectoryObjects[CheckedObject] = new Hashtable
        {
            ["ObjectGUID"] = Guid.Parse(CheckedObject),
            ["DistinguishedName"] = "CN=Room 1,OU=Rooms,DC=contoso,DC=test",
        };
        // The processor runs only for an enabled module; Exchange Online stays on for the pool.
        _h.Enablement.SaveEnablement(new Dictionary<string, bool>
        {
            [ExoConnectionPool.ConfigModuleKey] = true,
            [ConferenceRoomBulkProcessor.ModuleName] = true,
        });
        var store = TestConfigStore.Create(_h.Dir);
        new SectionAccessRepository(store).SaveAll(new Dictionary<string, string[]> { ["ConferenceRooms"] = ["AllUsers"] });
        _sectionAccess = new SectionAccessService(_h.Config, NullLogger<SectionAccessService>.Instance, Environment(),
            new ModuleCatalog(), new SectionAccessRepository(store));
    }

    public void Dispose()
    {
        _progress.Dispose();
        _h.Dispose();
    }

    // ---- the scenarios, each on both surfaces ------------------------------------------------

    [Theory]
    [InlineData("page")]
    [InlineData("bulk")]
    public async Task NoActiveDirectoryWrite_TheAuditNamesNoDirectoryObject(string surface)
    {
        // The codex trigger: a synced room (the gate proved its AD object), no City/State/Country, a
        // cloud list. Every write is Exchange's; the row names none in Active Directory.
        var row = await ApplyAndReadAudit(surface);

        Assert.Equal("Success", row.GetProperty("result").GetString());
        Assert.Equal(Checked.ToString("D"), row.GetProperty(ValidatedMailbox.AuditKey).GetString());
        Assert.Empty(_h.CallsTo("Set-ADUser"));
        Assert.Empty(_h.CallsTo("Set-ADGroup"));
        Assert.False(row.TryGetProperty(ValidatedMailbox.DirectoryAuditKey, out _), row.ToString());
    }

    [Theory]
    [InlineData("page")]
    [InlineData("bulk")]
    public async Task CityWritten_TheAuditNamesTheObjectWritten(string surface)
    {
        var row = await ApplyAndReadAudit(surface, city: "Dublin");

        Assert.Equal("Success", row.GetProperty("result").GetString());
        Assert.Equal(CheckedObject, ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Set-ADUser")), "Identity"));
        Assert.Equal(CheckedObject, row.GetProperty(ValidatedMailbox.DirectoryAuditKey).GetString());
    }

    [Theory]
    [InlineData("page")]
    [InlineData("bulk")]
    public async Task ASetADUserThatFails_IsNotRecordedAsWritten(string surface)
    {
        _h.Failures["Set-ADUser"] = "Insufficient access rights to perform the operation";

        var row = await ApplyAndReadAudit(surface, city: "Dublin");

        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Single(_h.CallsTo("Set-ADUser"));
        Assert.False(row.TryGetProperty(ValidatedMailbox.DirectoryAuditKey, out _), row.ToString());
    }

    [Theory]
    [InlineData("page")]
    [InlineData("bulk")]
    public async Task AWriteThatSucceeded_IsRecordedEvenWhenALaterStepFails(string surface)
    {
        // Partial: City/State/Country reached Active Directory, then the timezone failed. The object
        // WAS written, so the failed row still names it.
        _h.Failures["Set-MailboxRegionalConfiguration"] = "The time zone could not be set.";

        var row = await ApplyAndReadAudit(surface, city: "Dublin");

        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Single(_h.CallsTo("Set-ADUser"));
        Assert.Equal(CheckedObject, row.GetProperty(ValidatedMailbox.DirectoryAuditKey).GetString());
    }

    [Theory]
    [InlineData("page")]
    [InlineData("bulk")]
    public async Task TheOnPremRoomListAdd_IsRecordedAsWritten(string surface)
    {
        _h.ListSyncState[ConferenceRoomService.BuildRoomListName(Building)] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;

        var row = await ApplyAndReadAudit(surface);

        Assert.Equal("Success", row.GetProperty("result").GetString());
        Assert.Equal("CN=Room 1,OU=Rooms,DC=contoso,DC=test", ExchangeWriteHarness.AddedMember(Assert.Single(_h.CallsTo("Set-ADGroup"))));
        Assert.Equal(CheckedObject, row.GetProperty(ValidatedMailbox.DirectoryAuditKey).GetString());
        Assert.False(row.TryGetProperty(ConferenceRoomService.MembershipConfirmedAuditKey, out _), row.ToString());
    }

    [Theory]
    [InlineData("page")]
    [InlineData("bulk")]
    public async Task ARoomAlreadyOnItsOnPremList_IsNotRecordedAsWritten(string surface)
    {
        // The on-prem add's idempotency check finds the room already a member - its DN on the group's
        // member attribute (aud-a2i) - and writes nothing.
        _h.ListSyncState[ConferenceRoomService.BuildRoomListName(Building)] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;
        _h.DirectoryGroupMembers.Add("CN=Room 1,OU=Rooms,DC=contoso,DC=test");

        var row = await ApplyAndReadAudit(surface);

        Assert.Equal("Success", row.GetProperty("result").GetString());
        Assert.Empty(_h.CallsTo("Set-ADGroup"));
        Assert.False(row.TryGetProperty(ValidatedMailbox.DirectoryAuditKey, out _), row.ToString());
    }

    [Theory]
    [InlineData("page")]
    [InlineData("bulk")]
    public async Task AnOnPremAddTheReadBackDoesNotShow_IsNotRecordedAsWritten(string surface)
    {
        // aud-a2l. Set-ADGroup raises nothing and changes nothing: the read-back does not find the room, so
        // the row is a failure and names no written object - it used to be a success naming the room.
        _h.ListSyncState[ConferenceRoomService.BuildRoomListName(Building)] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;
        _h.SetAdGroupChangesNothing = true;

        var row = await ApplyAndReadAudit(surface);

        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Single(_h.CallsTo("Set-ADGroup"));
        Assert.False(row.TryGetProperty(ValidatedMailbox.DirectoryAuditKey, out _), row.ToString());
    }

    [Theory]
    [InlineData("page", true)]
    [InlineData("page", false)]
    [InlineData("bulk", true)]
    [InlineData("bulk", false)]
    public async Task AnOnPremAddRefusedAsAValueAlreadyThere_IsNeverRecordedAsWritten(string surface, bool theRoomIsThere)
    {
        // aud-a2l's race, and aud-a2n's folded-in item 1. The write is refused as a value already there: a
        // success when the read-back finds the room (another actor's add landed first), a failure when it does
        // not - and in neither is the room recorded as written, this row's write having been refused (aud-a2g:
        // name only what a write produced). The success records the confirmed membership instead.
        _h.ListSyncState[ConferenceRoomService.BuildRoomListName(Building)] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;
        if (theRoomIsThere)
            _h.AnotherAddLandsFirst = true;
        else
            _h.Failures["Set-ADGroup"] = ExchangeWriteHarness.DuplicateMemberValueError;

        var row = await ApplyAndReadAudit(surface);

        Assert.Single(_h.CallsTo("Set-ADGroup"));
        Assert.False(row.TryGetProperty(ValidatedMailbox.DirectoryAuditKey, out _), row.ToString());
        if (theRoomIsThere)
        {
            Assert.Equal("Success", row.GetProperty("result").GetString());
            Assert.StartsWith(ConferenceRoomService.ConfirmedAfterRefusal,
                row.GetProperty(ConferenceRoomService.MembershipConfirmedAuditKey).GetString(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal("Failed", row.GetProperty("result").GetString());
            Assert.False(row.TryGetProperty(ConferenceRoomService.MembershipConfirmedAuditKey, out _), row.ToString());
        }
    }

    [Theory]
    [InlineData("page")]
    [InlineData("bulk")]
    public async Task AnOnPremAddThatAppliedThenFailed_IsASuccess_RecordedAsConfirmed_NotAsWritten(string surface)
    {
        // aud-a2n, the folded-in item 2: Set-ADGroup applied the add, then reported a timeout. The read-back
        // finds the room, so the row succeeds; the audit names no written object and records the confirmed
        // membership with the write's error text.
        _h.ListSyncState[ConferenceRoomService.BuildRoomListName(Building)] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;
        _h.SetAdGroupFailsAfterApplying = TimedOut;

        var row = await ApplyAndReadAudit(surface);

        Assert.Equal("Success", row.GetProperty("result").GetString());
        Assert.False(row.TryGetProperty(ValidatedMailbox.DirectoryAuditKey, out _), row.ToString());
        var confirmed = row.GetProperty(ConferenceRoomService.MembershipConfirmedAuditKey).GetString();
        Assert.StartsWith(ConferenceRoomService.ConfirmedAfterWriteError, confirmed, StringComparison.Ordinal);
        Assert.Contains(TimedOut, confirmed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("refused, the room there")]
    [InlineData("applied, then failed")]
    public async Task ABulkRowConfirmedNotWritten_SaysSoInItsMessage_AsItsAuditDoes(string write)
    {
        // aud-a2o. A bulk job shows each row's message and not its steps, where the page shows a membership the
        // read-back confirmed rather than this row's write (aud-a2n), so such a row read as any applied add. Its
        // message now carries the detail its audit records.
        _h.ListSyncState[ConferenceRoomService.BuildRoomListName(Building)] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;
        if (write == "refused, the room there")
            _h.AnotherAddLandsFirst = true;
        else
            _h.SetAdGroupFailsAfterApplying = TimedOut;

        var row = await ApplyAndReadAudit("bulk");

        var confirmed = row.GetProperty(ConferenceRoomService.MembershipConfirmedAuditKey).GetString();
        Assert.NotNull(confirmed);
        var outcome = Assert.IsType<BulkJobRowOutcome>(_bulkRow);
        Assert.Equal(BulkJobRowStatus.Success, outcome.Status);
        Assert.Contains(confirmed, outcome.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("page")]
    [InlineData("bulk")]
    public async Task AnApplyThatThrows_IsNotRecordedAsWritten(string surface)
    {
        // Exchange answers the room's GUID with a different mailbox, so the read-back throws before
        // any write. The exception paths have no result to report a write from, and record none.
        _h.AddMailbox("other@contoso.test", Other, recipientTypeDetails: "RoomMailbox");
        _h.Mailboxes[Checked.ToString("D")] = _h.Mailboxes["other@contoso.test"];

        var row = await ApplyAndReadAudit(surface, city: "Dublin");

        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Empty(_h.Writes);
        Assert.False(row.TryGetProperty(ValidatedMailbox.DirectoryAuditKey, out _), row.ToString());
    }

    // ---- the harness ---------------------------------------------------------------------------

    private async Task<JsonElement> ApplyAndReadAudit(string surface, string city = "")
    {
        var service = new Service(_h);
        if (surface == "page")
        {
            var page = BuildPage(service, city);
            await (Task)typeof(Page).GetMethod("SetupSingleRoom", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!;
            return Assert.Single(_h.AuditRows(), r => r.GetProperty("action").GetString() == "ConferenceRooms_SetMetadata");
        }

        var processor = new ConferenceRoomBulkProcessor(service, Gate(), _sectionAccess, _h.Enablement, _h.Trace, _h.Audit,
            Substitute.For<EmailService>(_h.Config, NullLogger<EmailService>.Instance), NullLogger<ConferenceRoomBulkProcessor>.Instance);
        _bulkRow = await processor.ProcessRowAsync(FinderJob(city), 0, CancellationToken.None);
        return Assert.Single(_h.AuditRows(), r => r.GetProperty("action").GetString() == "ConferenceRooms_SetMetadata_Bulk");
    }

    /// <summary>The room service with its credential and directory runspace replaced by the harness.</summary>
    private sealed class Service : ConferenceRoomService
    {
        private readonly ExchangeWriteHarness _h;

        public Service(ExchangeWriteHarness h)
            : base(h.Pool, null!, h.ModuleConfig, null!, h.Trace, h.Config, NullLogger<ConferenceRoomService>.Instance) => _h = h;

        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose) =>
            Task.FromResult<(string username, string password, string domain)?>(("svc", "secret", "CONTOSO"));

        protected override Runspace OpenDirectoryRunspace() => _h.OpenDirectoryRunspace();
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

    private ConferenceRoomProtectionGate Gate()
    {
        var env = Environment();
        var extLog = new ExtendedLogService(_h.Config, env, TestConfigStore.CreateAppSettings(_h.Dir), NullLogger<ExtendedLogService>.Instance);
        var delinea = new DelineaService(HttpClients(), _h.Config, NullLogger<DelineaService>.Instance, extLog, _h.Trace);
        var check = new RoomResolvingCheck(env, _h.Config, _h.ModuleConfig, TestConfigStore.CreateProtectedPrincipal(_h.Dir), delinea);
        var servicers = new ProtectedPrincipalServicerService(_sectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance);
        return new ConferenceRoomProtectionGate(check, servicers, NullLogger<ConferenceRoomProtectionGate>.Instance);
    }

    private Page BuildPage(ConferenceRoomService service, string city)
    {
        var page = new Page();
        SetProperty(page, "RoomService", service);
        SetProperty(page, "Audit", _h.Audit);
        SetProperty(page, "Email", Substitute.For<EmailService>(_h.Config, NullLogger<EmailService>.Instance));
        SetProperty(page, "ServiceNow", new ServiceNowService(_h.Config, NullLogger<ServiceNowService>.Instance, HttpClients()));
        SetProperty(page, "AuthStateProvider", Authentication());
        SetProperty(page, "AuthorizationService", Authorization());
        SetProperty(page, "OpTrace", _h.Trace);
        SetProperty(page, "Logger", NullLogger<Page>.Instance);
        SetProperty(page, "ProtectionGate", Gate());
        SetProperty(page, "Progress", _progress);

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

    private static BulkJob FinderJob(string city)
    {
        var row = new FinderCsvRow
        {
            Email = Typed,
            City = city,
            Building = Building,
            Capacity = 8,
            Floor = "2",
            FloorLabel = "Second",
            DisplayDeviceName = "TV",
            VideoDeviceName = "Polycom",
            TimeZone = "GMT Standard Time",
        };
        string[] groups = ["AllUsers"];
        var snapshot = new JobAuthorizationSnapshot { Section = "ConferenceRooms", RoleClaims = groups, AuthorizedGroups = groups };
        return new BulkJob
        {
            Id = "j1",
            ModuleId = "ConferenceRooms",
            JobType = ConferenceRoomJobPayload.FinderJobType,
            Status = BulkJobStatus.Running,
            SubmittedBy = "jdoe",
            SubmittedIp = "10.0.0.9",
            Ticket = "INC1",
            AuthSnapshotJson = snapshot.ToJson(),
            PayloadJson = JsonSerializer.Serialize(new ConferenceRoomJobPayload { Kind = ConferenceRoomJobPayload.FinderJobType, FinderRows = [row] }),
            SubmittedAtUtc = DateTime.UtcNow,
        };
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
}
