using System.Collections;
using System.Collections.ObjectModel;
using System.Management.Automation;
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

    // Exchange Online's refusal of an add for a member the list already holds (aud-a2h).
    private static readonly string AlreadyAMember = $"The recipient \"{Typed}\" is already a member of the group \"{List}\".";

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
    public async Task ASyncedRoom_AlreadyOnItsSyncedRoomList_NeedsNothingOnPrem()
    {
        // The repair. A re-run for a room already on its synced list: the add writes nothing, so the
        // row needs no AD credential, object or group and must not be refused for lacking them - it
        // succeeded before the preflight existed. Typed by a string that is not its address, so the
        // membership test can pass only on the address Exchange returned for the validated room.
        _h.ListSyncState[List] = true;
        _h.ListMembers.Add(Typed);
        var service = new Service(_h) { NoCredential = true };

        var result = await service.SetRoomMetadataAndListAsync(
            "Room One", Room(), "", Building, 8, "2", "Second", "TV", "Polycom", "", "", "GMT Standard Time");

        Assert.True(result.Success, result.Message);
        Assert.Equal(["Set-Place", "Set-MailboxRegionalConfiguration", "Set-MailboxCalendarConfiguration"], _h.Writes);
        Assert.Equal(0, service.CredentialRequests);
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

    // aud-a2h. A list one member longer than Exchange Online's default page, the room last: only a
    // read that asks for the whole list can see it.
    private void FillListPastTheDefaultPage(string room)
    {
        for (var i = 0; i < ExchangeWriteHarness.ExchangeDefaultResultSize; i++)
            _h.ListMembers.Add($"member{i}@contoso.test");
        _h.ListMembers.Add(room);
    }

    private void AssertEveryMembershipReadAskedForTheWholeList(int reads)
    {
        var calls = _h.CallsTo("Get-DistributionGroupMember");
        Assert.Equal(reads, calls.Count);
        Assert.All(calls, c => Assert.Equal("Unlimited", ExchangeWriteHarness.Bound(c, "ResultSize")));
    }

    [Fact]
    public async Task ASyncedRoom_PastTheDefaultPageOfItsSyncedRoomList_IsSeenByThePreflightAndTheAdd()
    {
        // aud-a2h, the synced half. The repair above, on a list longer than one page: the preflight
        // must find the room to spare it the on-prem proof (no credential here, so a miss refuses the
        // row), and the add must find it to write nothing (a miss sends the cloud add, which a synced
        // list refuses).
        _h.ListSyncState[List] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;
        FillListPastTheDefaultPage(Typed);
        var service = new Service(_h) { NoCredential = true };

        var result = await service.SetRoomMetadataAndListAsync(
            "Room One", Room(), "", Building, 8, "2", "Second", "TV", "Polycom", "", "", "GMT Standard Time");

        Assert.True(result.Success, result.Message);
        Assert.Equal(["Set-Place", "Set-MailboxRegionalConfiguration", "Set-MailboxCalendarConfiguration"], _h.Writes);
        Assert.Equal(0, service.CredentialRequests);
        AssertEveryMembershipReadAskedForTheWholeList(reads: 2);
    }

    [Fact]
    public async Task ARoom_PastTheDefaultPageOfItsCloudRoomList_IsNotAddedAgain()
    {
        // aud-a2h, the cloud half. A cloud list needs no preflight, so the add's read is the only one.
        // Missing the room, it sent Add-DistributionGroupMember for a member Exchange already holds,
        // was refused, and reported partial with Set-Place and the timezone already committed.
        FillListPastTheDefaultPage(Typed);
        _h.Failures["Add-DistributionGroupMember"] = AlreadyAMember;

        var result = await Apply(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Partial);
        Assert.Empty(_h.CallsTo("Add-DistributionGroupMember"));
        Assert.Contains(result.Steps, s => s.Success && s.Stage == $"Already in '{List}'");
        AssertEveryMembershipReadAskedForTheWholeList(reads: 1);
    }

    [Fact]
    public void TheHarness_RefusesAMembershipReadThatDoesNotAskForTheWholeList()
    {
        // aud-a2h. The stand-in answers a read without -ResultSize with an error and no members, under
        // every -ErrorAction, so no test can find a member through a read Exchange would have cut
        // short. A number is a page of that size; Unlimited is the whole list.
        FillListPastTheDefaultPage(Typed);
        using var rs = _h.OpenExchangeRunspace();
        using var ps = PowerShell.Create();
        ps.Runspace = rs;

        Assert.Empty(ReadMembers(ps, resultSize: null, errorAction: "SilentlyContinue"));
        Assert.Empty(ReadMembers(ps, resultSize: null, errorAction: "Continue"));
        Assert.Contains("-ResultSize", Assert.Single(ps.Streams.Error).ToString(), StringComparison.Ordinal);
        Assert.ThrowsAny<RuntimeException>(() => ReadMembers(ps, resultSize: null, errorAction: "Stop"));

        var page = ExchangeWriteHarness.ExchangeDefaultResultSize;
        Assert.Equal(page, ReadMembers(ps, resultSize: page, errorAction: "Stop").Count);
        var all = ReadMembers(ps, resultSize: "Unlimited", errorAction: "Stop");
        Assert.Equal(page + 1, all.Count);
        Assert.Equal(Typed, all[^1].Properties["PrimarySmtpAddress"]?.Value);
    }

    private static Collection<PSObject> ReadMembers(PowerShell ps, object? resultSize, string errorAction)
    {
        ps.Commands.Clear();
        ps.Streams.Error.Clear();
        var read = ps.AddCommand("Get-DistributionGroupMember").AddParameter("Identity", List);
        if (resultSize is not null)
            read.AddParameter("ResultSize", resultSize);
        read.AddParameter("ErrorAction", errorAction);
        return ps.Invoke();
    }
}
