using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a2d, Conference Rooms: every room write - Exchange and Active Directory - is bound to the
/// room the protection gate validated, never to the string the operator typed.
/// </summary>
/// <remarks>
/// The typed string is an alias of a DIFFERENT room in Exchange, so an Exchange write handed the
/// string would land on that room. The Active Directory steps used to find their object by matching
/// the typed string against userPrincipalName; they now write the objectGUID the gate proved owns
/// the mailbox, and the stand-in directory answers only by objectGUID - a UPN search is recorded
/// and returns nothing.
/// </remarks>
public sealed class ConferenceRoomWriteIdentityTests : IDisposable
{
    private const string Typed = "boardroom";
    private static readonly Guid Checked = Guid.Parse("0f0f0f0f-1111-4222-8333-444444444444");
    private static readonly Guid Other = Guid.Parse("0f0f0f0f-5555-4666-8777-888888888888");
    private const string CheckedObject = "5a5a5a5a-0000-4000-8000-000000000001";

    private readonly ExchangeWriteHarness _h = new();

    public ConferenceRoomWriteIdentityTests()
    {
        _h.AddMailbox("room1@contoso.test", Checked, recipientTypeDetails: "RoomMailbox");
        _h.AddMailbox("execroom@contoso.test", Other, recipientTypeDetails: "RoomMailbox", aliases: Typed);
        _h.DirectoryObjects[CheckedObject] = new System.Collections.Hashtable
        {
            ["ObjectGUID"] = Guid.Parse(CheckedObject),
            ["DistinguishedName"] = "CN=Room 1,OU=Rooms,DC=contoso,DC=test",
        };
        // The groups SetRoomTypeAsync refuses without (module config, no defaults).
        var groups = ConferenceRoomService.RequiredGroupConfigKeys.ToDictionary(k => k, k => k.ToLowerInvariant() + "@contoso.test");
        _h.ModuleConfig.SaveModuleConfig("ConferenceRooms", groups);
    }

    public void Dispose() => _h.Dispose();

    private static string Identity(Guid guid) => guid.ToString("D");

    /// <summary>The room service with its credential and directory runspace replaced by the harness.</summary>
    private sealed class Service : ConferenceRoomService
    {
        private readonly ExchangeWriteHarness _h;
        public int CredentialRequests;

        public Service(ExchangeWriteHarness h)
            : base(h.Pool, null!, h.ModuleConfig, null!, h.Trace, h.Config, NullLogger<ConferenceRoomService>.Instance) => _h = h;

        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose)
        {
            CredentialRequests++;
            return Task.FromResult<(string username, string password, string domain)?>(("svc", "secret", "CONTOSO"));
        }

        protected override Runspace OpenDirectoryRunspace() => _h.OpenDirectoryRunspace();
    }

    private ValidatedMailbox Room(string? directoryObject = CheckedObject) => ExchangeWriteHarness.Validated(Checked, directoryObject);

    private Task<RoomOperationResult> Finder(Service service, ValidatedMailbox? room, string city = "", string state = "", string country = "")
        => service.SetRoomMetadataAndListAsync(Typed, room, city, "Building 1", 8, "2", "Second", "TV", "Polycom", country, state, "GMT Standard Time");

    [Fact]
    public async Task TheFinderWrites_AreBoundToTheValidatedRoom_NotTheStringTyped()
    {
        var result = await Finder(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        Assert.All(_h.CallsTo("Get-Mailbox"), c => Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(c, "Identity")));
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Set-Place")), "Identity"));
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Set-MailboxRegionalConfiguration")), "Identity"));
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Set-MailboxCalendarConfiguration")), "Identity"));
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Add-DistributionGroupMember")), "Member"));
    }

    [Fact]
    public async Task CityStateAndCountry_AreWrittenToTheProvenDirectoryObject_NotAUserPrincipalNameMatch()
    {
        var result = await Finder(new Service(_h), Room(), city: "Dublin", state: "Leinster", country: "IE");

        Assert.True(result.Success, result.Message);
        Assert.Equal(CheckedObject, ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Set-ADUser")), "Identity"));
        Assert.All(_h.CallsTo("Get-ADUser"), c =>
        {
            Assert.Equal(CheckedObject, ExchangeWriteHarness.Bound(c, "Identity"));
            Assert.Null(ExchangeWriteHarness.Bound(c, "Filter"));
        });
    }

    [Fact]
    public async Task ACloudOnlyRoomWithLocationFields_IsRefusedBeforeAnyWrite()
    {
        // The gate proved no on-premises object (a cloud-only room). There is no validated object
        // to write City/State/Country to, so the row stops before Set-Place commits anything.
        var service = new Service(_h);

        var result = await Finder(service, Room(directoryObject: null), city: "Dublin");

        Assert.False(result.Success);
        Assert.Empty(_h.Writes);
        Assert.Equal(0, service.CredentialRequests);
    }

    [Fact]
    public async Task ARoomAlreadyOnTheList_ByItsValidatedAddress_IsNotAddedAgain()
    {
        // The already-member test compares the list's members with the address Exchange returned
        // for the validated room. It compared them with the typed string, so a room reached by an
        // alias always read as "not a member".
        _h.ListMembers.Add("room1@contoso.test");

        var result = await Finder(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        Assert.Empty(_h.CallsTo("Add-DistributionGroupMember"));
    }

    [Fact]
    public async Task TheOnPremRoomListFallback_AddsTheProvenDirectoryObject()
    {
        // A list Exchange reports as synchronized from on-premises: since aud-a2f the only kind the
        // fallback serves, and the kind that refuses the cloud add below.
        _h.ListSyncState[ConferenceRoomService.BuildRoomListName("Building 1")] = true;
        _h.Failures["Add-DistributionGroupMember"] =
            "The operation on the group failed because it is being synchronized from your on-premises organization.";

        var result = await Finder(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        Assert.Equal("CN=Room 1,OU=Rooms,DC=contoso,DC=test", ExchangeWriteHarness.AddedMember(Assert.Single(_h.CallsTo("Set-ADGroup"))));
        Assert.All(_h.CallsTo("Get-ADUser"), c => Assert.Equal(CheckedObject, ExchangeWriteHarness.Bound(c, "Identity")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheTypeWrites_AreBoundToTheValidatedRoom_NotTheStringTyped(bool removeExisting)
    {
        var result = await new Service(_h).SetRoomTypeAsync(Typed, Room(), RoomType.Standard, "GMT Standard Time",
            removeExistingPermissions: removeExisting);

        Assert.True(result.Success, result.Message);
        var folder = Identity(Checked) + @":\Calendar";
        Assert.All(_h.CallsTo("Get-Mailbox"), c => Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(c, "Identity")));
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Get-MailboxFolderStatistics")), "Identity"));
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Set-CalendarProcessing")), "Identity"));
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Set-Mailbox")), "Identity"));
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Set-MailboxRegionalConfiguration")), "Identity"));
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Set-MailboxCalendarConfiguration")), "Identity"));
        Assert.NotEmpty(_h.CallsTo("Set-MailboxFolderPermission"));
        Assert.All(_h.CallsTo("Add-MailboxFolderPermission").Concat(_h.CallsTo("Set-MailboxFolderPermission")),
            c => Assert.Equal(folder, ExchangeWriteHarness.Bound(c, "Identity")));
        if (removeExisting)
        {
            Assert.Equal(folder, ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Get-MailboxFolderPermission")), "Identity"));
            Assert.Equal(folder, ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Remove-MailboxFolderPermission")), "Identity"));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithoutAValidatedRoom_NothingIsWritten_AndNothingIsAsked(bool finder)
    {
        var service = new Service(_h);

        var result = finder
            ? await Finder(service, null, city: "Dublin")
            : await service.SetRoomTypeAsync(Typed, null, RoomType.Standard, "GMT Standard Time");

        Assert.False(result.Success);
        Assert.Equal(ValidatedMailbox.Refusal(Typed), result.Message);
        Assert.Empty(_h.Calls);
        Assert.Equal(0, _h.Connects);
        Assert.Equal(0, service.CredentialRequests);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AReadBackOfADifferentMailbox_RefusesBeforeAnyWrite(bool finder)
    {
        _h.Mailboxes[Identity(Checked)] = _h.Mailboxes["execroom@contoso.test"];
        var service = new Service(_h);

        var failure = await Record.ExceptionAsync(async () =>
        {
            var result = finder
                ? await Finder(service, Room())
                : await service.SetRoomTypeAsync(Typed, Room(), RoomType.Standard, "GMT Standard Time");
            Assert.False(result.Success);
        });

        Assert.True(failure is null || failure.Message.Contains("different mailbox", StringComparison.Ordinal), failure?.Message);
        Assert.Empty(_h.Writes);
    }
}
