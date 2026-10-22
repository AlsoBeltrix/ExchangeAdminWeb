using System.Collections;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a2f: a Room Finder row with a Building settles whether the room can be added to its room
/// list BEFORE anything is written. A list synchronized from on-premises takes the room only
/// on-prem, and what that add needs - a proven AD object, the AD credential, that object and the
/// list's on-prem group - used to be tested only at the add, after Set-Place and the timezone had
/// committed, so the row came back partial.
/// </summary>
/// <remarks>
/// The real service over <see cref="ExchangeWriteHarness"/>: the stand-in Get-DistributionGroup
/// reports IsDirSynced per list name (False unless a test says otherwise), the stand-in directory
/// answers by objectGUID, and the stand-in Get-ADGroup returns the harness's group set.
/// </remarks>
public sealed class ConferenceRoomRoomListPreflightTests : IDisposable
{
    private const string Typed = "room1@contoso.test";
    private const string Building = "Building 1";
    private static readonly Guid Checked = Guid.Parse("0f0f0f0f-1111-4222-8333-444444444444");
    private const string CheckedObject = "5a5a5a5a-0000-4000-8000-000000000001";
    private static readonly string List = ConferenceRoomService.BuildRoomListName(Building);

    // Exchange Online's refusal of a cloud add to a list synchronized from on-premises.
    private const string OnPremMastered =
        "The operation on the group failed because it's out of the current user's write scope. The action can't be "
        + "performed on the object because the object is being synchronized from your on-premises organization.";

    private readonly ExchangeWriteHarness _h = new();

    public ConferenceRoomRoomListPreflightTests()
    {
        _h.AddMailbox(Typed, Checked, recipientTypeDetails: "RoomMailbox");
        _h.DirectoryObjects[CheckedObject] = new Hashtable
        {
            ["ObjectGUID"] = Guid.Parse(CheckedObject),
            ["DistinguishedName"] = "CN=Room 1,OU=Rooms,DC=contoso,DC=test",
        };
    }

    public void Dispose() => _h.Dispose();

    /// <summary>The room service with its credential and directory runspace replaced by the harness.</summary>
    private sealed class Service : ConferenceRoomService
    {
        private readonly ExchangeWriteHarness _h;
        public bool NoCredential;
        public int CredentialRequests;

        public Service(ExchangeWriteHarness h)
            : base(h.Pool, null!, h.ModuleConfig, null!, h.Trace, h.Config, NullLogger<ConferenceRoomService>.Instance) => _h = h;

        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose)
        {
            CredentialRequests++;
            return Task.FromResult<(string username, string password, string domain)?>(
                NoCredential ? null : ("svc", "secret", "CONTOSO"));
        }

        protected override Runspace OpenDirectoryRunspace() => _h.OpenDirectoryRunspace();
    }

    private static ValidatedMailbox Room(string? directoryObject = CheckedObject) => ExchangeWriteHarness.Validated(Checked, directoryObject);

    private static Task<RoomOperationResult> Apply(Service service, ValidatedMailbox room, string city = "")
        => service.SetRoomMetadataAndListAsync(Typed, room, city, Building, 8, "2", "Second", "TV", "Polycom", "", "", "GMT Standard Time");

    [Fact]
    public async Task ACloudOnlyRoom_ForARoomListSyncedFromOnPremises_IsRefusedBeforeAnyWrite()
    {
        // The codex scenario. A cloud-only room (the gate proved no on-premises object), a Building,
        // and a list only on-prem can add to: nothing can complete the row, so nothing is written -
        // no Set-Place, no timezone - and no credential is asked for.
        _h.ListSyncState[List] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;
        var service = new Service(_h);

        var result = await Apply(service, Room(directoryObject: null));

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Contains(List, result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
        Assert.Equal(0, service.CredentialRequests);
    }

    [Theory]
    [InlineData("no credential")]
    [InlineData("room object unreadable")]
    [InlineData("no on-prem group")]
    [InlineData("two on-prem groups")]
    public async Task ASyncedRoomList_WhoseOnPremAddCannotBeProven_IsRefusedBeforeAnyWrite(string missing)
    {
        // A synced room, so there is an object to add - but each of the other things the on-prem add
        // needs is missing in turn. No City/State/Country, so the location preflight has nothing to
        // check and only the room-list preflight stands between the row and Set-Place.
        _h.ListSyncState[List] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;
        var service = new Service(_h);
        switch (missing)
        {
            case "no credential": service.NoCredential = true; break;
            case "room object unreadable": _h.DirectoryObjects.Remove(CheckedObject); break;
            case "no on-prem group": _h.DirectoryGroupGuids.Clear(); break;
            case "two on-prem groups": _h.DirectoryGroupGuids.Add("88888888-8888-8888-8888-888888888888"); break;
        }

        var result = await Apply(service, Room());

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Empty(_h.Writes);
    }

    [Fact]
    public async Task ARoomListThatDoesNotSayWhetherItIsSynced_IsRefusedBeforeAnyWrite()
    {
        _h.ListSyncState[List] = null;

        var result = await Apply(new Service(_h), Room());

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Contains("did not report", result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
    }

    [Fact]
    public async Task AProvableRow_ForASyncedRoomList_WritesInTheSameOrderAsBefore()
    {
        // Everything the on-prem add needs is there: the row writes what it always wrote, in the
        // order it always wrote it - the cloud add is still tried first and refused.
        _h.ListSyncState[List] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;

        var result = await Apply(new Service(_h), Room(), city: "Dublin");

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            ["Set-Place", "Set-ADUser", "Set-MailboxRegionalConfiguration", "Set-MailboxCalendarConfiguration",
             "Add-DistributionGroupMember", "Add-ADGroupMember"],
            _h.Writes);
    }

    [Fact]
    public async Task ACloudOnlyRoom_ForACloudRoomList_IsStillWritten()
    {
        // The refusal belongs to the synced list, not to the cloud-only room: on a list Exchange
        // Online holds, the cloud add is the add and the row goes through.
        var service = new Service(_h);

        var result = await Apply(service, Room(directoryObject: null));

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            ["Set-Place", "Set-MailboxRegionalConfiguration", "Set-MailboxCalendarConfiguration", "Add-DistributionGroupMember"],
            _h.Writes);
        Assert.Equal(0, service.CredentialRequests);
    }

    [Fact]
    public async Task ACloudRoomList_RefusingTheAddAsOnPremMastered_IsNotAddedOnPrem()
    {
        // Exchange reported the list as cloud, so the preflight proved nothing about an on-prem add.
        // A refusal worded as on-prem-mastered is surfaced; no on-prem group is looked up or written.
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;

        var result = await Apply(new Service(_h), Room());

        Assert.False(result.Success);
        Assert.True(result.Partial);
        Assert.Contains("not synchronized from on-premises", result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.CallsTo("Get-ADGroup"));
        Assert.Empty(_h.CallsTo("Add-ADGroupMember"));
    }

    [Fact]
    public async Task TheRoomList_IsReadOnce_BeforeAnythingIsWritten()
    {
        // The add uses the list the preflight read. Looked up again after the writes, a failed read
        // escaped as an exception with Set-Place, City/State/Country and the timezone committed.
        var result = await Apply(new Service(_h), Room(), city: "Dublin");

        Assert.True(result.Success, result.Message);
        var commands = _h.Commands;
        var firstWrite = Enumerable.Range(0, commands.Count).First(i => commands[i] == "Set-Place");
        var reads = Enumerable.Range(0, commands.Count).Where(i => commands[i] == "Get-DistributionGroup").ToList();
        Assert.Single(reads);
        Assert.True(reads[0] < firstWrite, $"Get-DistributionGroup at {reads[0]}, Set-Place at {firstWrite}");
    }
}
