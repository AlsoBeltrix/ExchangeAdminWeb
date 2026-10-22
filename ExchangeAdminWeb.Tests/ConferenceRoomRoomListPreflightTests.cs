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
    // aud-a2i. The DN of the room's AD object, and of its list's on-prem group, as the stand-in directory
    // holds them: the on-prem membership is compared by the first and read by the second.
    private const string RoomDn = "CN=Room 1,OU=Rooms,DC=contoso,DC=test";
    private static readonly string List = ConferenceRoomService.BuildRoomListName(Building);
    private static readonly string GroupDn = ExchangeWriteHarness.DirectoryGroupDn(ExchangeWriteHarness.DefaultDirectoryGroupGuid);

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
            ["DistinguishedName"] = RoomDn,
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

    // aud-a2i. A synced list the room is not on in Exchange - a first add, or a re-run inside the sync
    // window after an on-prem add - with Exchange refusing the cloud add, as it does for such a list:
    // the preflight proves the on-prem add and the row reaches it.
    private void SyncedListTheRoomIsNotOnInExchange()
    {
        _h.ListSyncState[List] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;
    }

    // The positions in the call log of every on-prem membership read: Get-ADGroup by -Identity.
    private List<int> MembershipReads()
    {
        var calls = _h.Calls.Cast<Hashtable>().ToList();
        return Enumerable.Range(0, calls.Count)
            .Where(i => (string)calls[i]["__Command"]! == "Get-ADGroup" && calls[i].ContainsKey("Identity"))
            .ToList();
    }

    // The repo's membership read: the group's member attribute, by the group's DN, on the domain the DN names.
    private static void AssertReadsTheGroupsMemberAttributeInItsDomain(Hashtable read)
    {
        Assert.Equal(GroupDn, ExchangeWriteHarness.Bound(read, "Identity"));
        Assert.Equal("contoso.test", ExchangeWriteHarness.Bound(read, "Server"));
        var properties = Assert.IsAssignableFrom<IEnumerable>(ExchangeWriteHarness.Bound(read, "Properties"));
        Assert.Contains("member", properties.Cast<object>().Select(p => p?.ToString()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARoomAlreadyInItsOnPremGroup_ByTheMemberAttribute_IsNotAddedAgain(bool dnInAnotherCase)
    {
        // aud-a2i. The room is already in the list's on-prem group - its DN on the group's member
        // attribute, in either case, DNs comparing case-insensitively - but not yet on Exchange's copy.
        // Nothing on-prem is written and nothing is partial. The membership was read before Set-Place,
        // by the preflight, and again by the add, the same way both times - never by Get-ADGroupMember.
        SyncedListTheRoomIsNotOnInExchange();
        _h.DirectoryGroupMembers.Add("CN=Someone,OU=People,DC=contoso,DC=test");
        _h.DirectoryGroupMembers.Add(dnInAnotherCase ? RoomDn.ToUpperInvariant() : RoomDn);

        var result = await Apply(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Partial);
        Assert.Empty(_h.CallsTo("Add-ADGroupMember"));
        Assert.Empty(_h.CallsTo("Get-ADGroupMember"));
        var calls = _h.Calls.Cast<Hashtable>().ToList();
        var setPlace = calls.FindIndex(c => (string)c["__Command"]! == "Set-Place");
        var reads = MembershipReads();
        Assert.Equal(2, reads.Count);
        Assert.True(reads[0] < setPlace && setPlace < reads[1], $"membership reads at {reads[0]} and {reads[1]}, Set-Place at {setPlace}");
        Assert.All(reads, i => AssertReadsTheGroupsMemberAttributeInItsDomain(calls[i]));
    }

    [Theory]
    [InlineData("over the ADWS cap", false)]
    [InlineData("over the ADWS cap", true)]
    [InlineData("a member in another domain", false)]
    [InlineData("a member in another domain", true)]
    public async Task AGroupGetAdGroupMemberWouldRefuse_NoLongerAffectsTheRow(string shape, bool alreadyAMember)
    {
        // aud-a2i, the finding. The two groups Get-ADGroupMember cannot read - more members than the ADWS
        // cap, or a member in another domain - which the stand-in refuses as the real cmdlet does. The old
        // read failed after Set-Place and the timezone had committed, so the row came back partial on
        // every re-run; the member attribute reads both, and the row completes: added when the room was
        // not a member, left alone when it was.
        SyncedListTheRoomIsNotOnInExchange();
        if (shape == "over the ADWS cap")
        {
            for (var i = 0; i < ExchangeWriteHarness.AdwsMaxGroupOrMemberEntries; i++)
                _h.DirectoryGroupMembers.Add($"CN=Member {i},OU=People,DC=contoso,DC=test");
            if (!alreadyAMember)
                _h.DirectoryGroupMembers.Add("CN=One More,OU=People,DC=contoso,DC=test");
        }
        else
        {
            _h.DirectoryGroupMembers.Add("CN=Someone,OU=People,DC=fabrikam,DC=test");
        }
        if (alreadyAMember)
            _h.DirectoryGroupMembers.Add(RoomDn);

        var result = await Apply(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Partial);
        if (alreadyAMember)
            Assert.Empty(_h.CallsTo("Add-ADGroupMember"));
        else
            Assert.Equal(CheckedObject, ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Add-ADGroupMember")), "Members"));
    }

    [Fact]
    public async Task AnotherObjectWithTheRoomsName_InItsOnPremGroup_IsNotTheRoom()
    {
        // aud-a2i. The room is compared by the DN of the object proven by its objectGUID, never by a name:
        // a member named like the room, elsewhere in the directory, is another object, and the room is added.
        SyncedListTheRoomIsNotOnInExchange();
        _h.DirectoryGroupMembers.Add("CN=Room 1,OU=Retired Rooms,DC=contoso,DC=test");

        var result = await Apply(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        Assert.Equal(CheckedObject, ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Add-ADGroupMember")), "Members"));
    }

    [Theory]
    [InlineData("the membership read fails")]
    [InlineData("the group answers as another object")]
    [InlineData("the room's object has no DN")]
    public async Task AnOnPremGroupWhoseMembershipCannotBeRead_IsRefusedBeforeAnyWrite(string problem)
    {
        // aud-a2i. Whether the on-prem add will write is settled before any write. A membership that
        // cannot be read - the read fails, the group answers as another object, or the room's object has
        // no DN to compare - refuses the row, not partial, nothing written. It used to surface only at the
        // add, after Set-Place and the timezone.
        SyncedListTheRoomIsNotOnInExchange();
        switch (problem)
        {
            case "the membership read fails": _h.Failures["Get-ADGroup -Identity"] = "Unable to contact the server."; break;
            case "the group answers as another object": _h.DirectoryGroupReadBack[GroupDn] = "77777777-7777-7777-7777-777777777777"; break;
            case "the room's object has no DN": ((Hashtable)_h.DirectoryObjects[CheckedObject]!).Remove("DistinguishedName"); break;
        }

        var result = await Apply(new Service(_h), Room());

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Contains(List, result.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was written.", result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
    }

    [Fact]
    public void TheHarness_GetAdGroupMember_RefusesAGroupOverTheCapAndAMemberInAnotherDomain()
    {
        // aud-a2i. The stand-in refuses what the real cmdlet refuses, so a restored Get-ADGroupMember read
        // fails the tests above for the reason it fails live: a small group in its own domain is answered
        // (the member's objectGUID resolved), a member in another domain faults the read, and a group past
        // the cap is refused.
        using var rs = _h.OpenDirectoryRunspace();
        using var ps = PowerShell.Create();
        ps.Runspace = rs;

        _h.DirectoryGroupMembers.Add(RoomDn);
        Assert.Equal(CheckedObject, Assert.Single(ReadGroupMembers(ps)).Properties["ObjectGUID"]?.Value?.ToString());

        _h.DirectoryGroupMembers.Add("CN=Someone,OU=People,DC=fabrikam,DC=test");
        Assert.Contains("An operations error occurred",
            Assert.ThrowsAny<RuntimeException>(() => ReadGroupMembers(ps)).Message, StringComparison.Ordinal);

        _h.DirectoryGroupMembers.RemoveAt(1);
        for (var i = 0; i < ExchangeWriteHarness.AdwsMaxGroupOrMemberEntries; i++)
            _h.DirectoryGroupMembers.Add($"CN=Member {i},OU=People,DC=contoso,DC=test");
        Assert.Contains("The size limit for this request was exceeded",
            Assert.ThrowsAny<RuntimeException>(() => ReadGroupMembers(ps)).Message, StringComparison.Ordinal);
    }

    private static Collection<PSObject> ReadGroupMembers(PowerShell ps)
    {
        ps.Commands.Clear();
        ps.Streams.Error.Clear();
        ps.AddCommand("Get-ADGroupMember").AddParameter("Identity", GroupDn).AddParameter("ErrorAction", "Stop");
        return ps.Invoke();
    }
}
