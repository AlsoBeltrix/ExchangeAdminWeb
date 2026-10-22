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
        // order it always wrote it - the cloud add is still tried first and refused. Since aud-a2j the
        // on-prem add writes the group's member attribute (Set-ADGroup), not Add-ADGroupMember.
        _h.ListSyncState[List] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;

        var result = await Apply(new Service(_h), Room(), city: "Dublin");

        Assert.True(result.Success, result.Message);
        Assert.Equal(
            ["Set-Place", "Set-ADUser", "Set-MailboxRegionalConfiguration", "Set-MailboxCalendarConfiguration",
             "Add-DistributionGroupMember", "Set-ADGroup"],
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
        Assert.Empty(_h.CallsTo("Set-ADGroup"));
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

    // The positions in the call log of every on-prem membership read: Get-ADGroup by -Identity, asking
    // for the member attribute. (aud-a2j: the re-read of the group in its own domain, which comes just
    // before, is a read by -Identity too, and is not one.)
    private List<int> MembershipReads()
    {
        var calls = _h.Calls.Cast<Hashtable>().ToList();
        return Enumerable.Range(0, calls.Count)
            .Where(i => (string)calls[i]["__Command"]! == "Get-ADGroup" && calls[i].ContainsKey("Identity") && ReadsMember(calls[i]))
            .ToList();
    }

    // Whether a Get-ADGroup call asked for the group's member attribute.
    private static bool ReadsMember(Hashtable call) =>
        ExchangeWriteHarness.Bound(call, "Properties") is IEnumerable properties
        && properties.Cast<object>().Any(p => string.Equals(p?.ToString(), "member", StringComparison.OrdinalIgnoreCase));

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
        Assert.Empty(_h.CallsTo("Set-ADGroup"));
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
            Assert.Empty(_h.CallsTo("Set-ADGroup"));
        else
            Assert.Equal(RoomDn, ExchangeWriteHarness.AddedMember(Assert.Single(_h.CallsTo("Set-ADGroup"))));
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
        Assert.Equal(RoomDn, ExchangeWriteHarness.AddedMember(Assert.Single(_h.CallsTo("Set-ADGroup"))));
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
        // add, after Set-Place and the timezone. (aud-a2j: a failing read by identity now fails the re-read
        // of the group first, which refuses as well; "another object" answers the member read only, so the
        // re-read passes and the membership read's own objectGUID check is the one that refuses.)
        SyncedListTheRoomIsNotOnInExchange();
        switch (problem)
        {
            case "the membership read fails": _h.Failures["Get-ADGroup -Identity"] = "Unable to contact the server."; break;
            case "the group answers as another object": _h.DirectoryGroupMemberReadBack[GroupDn] = "77777777-7777-7777-7777-777777777777"; break;
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

    // aud-a2j. The room list's on-prem group was searched with no -Server, so only the default domain
    // answered and a synced list whose group lives in another domain of the forest was refused as "No
    // on-prem AD group found". These pin the forest search through the global catalog, the refusals
    // that come with it, the re-read in the group's own domain, and the write routed there. The room's
    // object is in the default domain (RoomDn), where the protection gate proves it; the list's group,
    // in these, is in the forest's other one.
    private const string OtherDomainGroupGuid = "66666666-6666-6666-6666-666666666666";
    private static readonly string OtherDomainGroupDn =
        ExchangeWriteHarness.DirectoryGroupDn(OtherDomainGroupGuid, ExchangeWriteHarness.OtherDirectoryDomainDn);
    private const string OtherDomainName = "fabrikam.test";

    // The list's group in the other domain - the only one unless the default domain's is kept too, which
    // makes the list's name match a group in each.
    private void PlaceAGroupInTheOtherDomain(bool keepTheDefaultGroup = false)
    {
        if (!keepTheDefaultGroup)
            _h.DirectoryGroupGuids.Clear();
        _h.DirectoryGroupGuids.Add(OtherDomainGroupGuid);
        _h.DirectoryGroupDomains[OtherDomainGroupGuid] = ExchangeWriteHarness.OtherDirectoryDomainDn;
    }

    // The list's group searched for by name (no -Identity), and re-read by its DN without the member
    // attribute - the membership read is the other read by -Identity.
    private static bool IsGroupSearch(Hashtable call) =>
        (string)call["__Command"]! == "Get-ADGroup" && !call.ContainsKey("Identity");

    private static bool IsGroupReRead(Hashtable call) =>
        (string)call["__Command"]! == "Get-ADGroup" && call.ContainsKey("Identity") && !ReadsMember(call);

    // The positions in the call log of the calls that match.
    private List<int> CallsWhere(Func<Hashtable, bool> match)
    {
        var calls = _h.Calls.Cast<Hashtable>().ToList();
        return Enumerable.Range(0, calls.Count).Where(i => match(calls[i])).ToList();
    }

    [Fact]
    public async Task ARoomListGroupInAnotherDomain_IsFoundThroughTheForestGlobalCatalog()
    {
        // The finding. A synced list the room is not on in Exchange, its group in the other domain: found
        // by searching the forest's global catalog - the preflight's search and the add's - and written on
        // the group's own domain, the room's DN on its member attribute. Not partial, nothing refused.
        // Add-ADGroupMember could not do it (it resolves the room on the group's domain), so it is not used.
        SyncedListTheRoomIsNotOnInExchange();
        PlaceAGroupInTheOtherDomain();

        var result = await Apply(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Partial);
        Assert.Contains(result.Steps, s => s.Success && s.Stage == $"Add to '{List}' (on-prem)");
        var searches = _h.CallsTo("Get-ADGroup").Where(IsGroupSearch).ToList();
        Assert.Equal(2, searches.Count);
        Assert.All(searches, c => Assert.Equal(ExchangeWriteHarness.GlobalCatalogEndpoint, ExchangeWriteHarness.Bound(c, "Server")));
        var write = Assert.Single(_h.CallsTo("Set-ADGroup"));
        Assert.Equal(OtherDomainGroupGuid, ExchangeWriteHarness.Bound(write, "Identity"));
        Assert.Equal(OtherDomainName, ExchangeWriteHarness.Bound(write, "Server"));
        Assert.Equal(RoomDn, ExchangeWriteHarness.AddedMember(write));
        Assert.Empty(_h.CallsTo("Add-ADGroupMember"));
    }

    [Fact]
    public void TheHarness_AnswersAGroupSearchOnlyFromTheDomainsItsServerReaches()
    {
        // The default-domain shape, as the stand-in reproduces it: a search with no -Server sees only the
        // default domain's group - the old search would have missed the other domain's - and so does one
        // sent to a bare ":3268", which the real cmdlet accepts and answers from the local domain. The
        // catalog endpoint sees both; a domain's own name sees only its own; an unknown server answers
        // nothing but an error.
        PlaceAGroupInTheOtherDomain(keepTheDefaultGroup: true);
        using var rs = _h.OpenDirectoryRunspace();
        using var ps = PowerShell.Create();
        ps.Runspace = rs;

        Assert.Equal([ExchangeWriteHarness.DefaultDirectoryGroupGuid], SearchGroups(ps, server: null));
        Assert.Equal([ExchangeWriteHarness.DefaultDirectoryGroupGuid], SearchGroups(ps, server: ":3268"));
        Assert.Equal([ExchangeWriteHarness.DefaultDirectoryGroupGuid, OtherDomainGroupGuid],
            SearchGroups(ps, server: ExchangeWriteHarness.GlobalCatalogEndpoint));
        Assert.Equal([OtherDomainGroupGuid], SearchGroups(ps, server: OtherDomainName));
        Assert.ThrowsAny<RuntimeException>(() => SearchGroups(ps, server: "elsewhere.test"));
    }

    // The objectGUIDs a stand-in Get-ADGroup search answers with, sent to that server (or none).
    private static List<string> SearchGroups(PowerShell ps, string? server)
    {
        ps.Commands.Clear();
        ps.Streams.Error.Clear();
        var search = ps.AddCommand("Get-ADGroup").AddParameter("Filter", $"name -eq '{List}'");
        if (server is not null)
            search.AddParameter("Server", server);
        search.AddParameter("ErrorAction", "Stop");
        return ps.Invoke().Select(g => g.Properties["ObjectGUID"]?.Value?.ToString() ?? "").ToList();
    }

    [Fact]
    public async Task ARoomListNameMatchingAGroupInEachDomain_IsRefusedBeforeAnyWrite()
    {
        // The catalog widens what can match, and the exactly-one rule holds across it: the list's name
        // matching a group in each domain refuses before any write. The default-domain search saw one of
        // them and wrote it.
        SyncedListTheRoomIsNotOnInExchange();
        PlaceAGroupInTheOtherDomain(keepTheDefaultGroup: true);

        var result = await Apply(new Service(_h), Room());

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Contains(List, result.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was written.", result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
        var search = Assert.Single(_h.CallsTo("Get-ADGroup"), IsGroupSearch);
        Assert.Equal(ExchangeWriteHarness.GlobalCatalogEndpoint, ExchangeWriteHarness.Bound(search, "Server"));
        Assert.DoesNotContain(_h.CallsTo("Get-ADGroup"), c => c.ContainsKey("Identity"));
    }

    [Theory]
    [InlineData("Get-ADForest fails")]
    [InlineData("the forest reports no catalog")]
    [InlineData("the forest reports a blank catalog host")]
    public async Task AForestWhoseGlobalCatalogCannotBeResolved_IsRefusedBeforeAnyWrite(string problem)
    {
        // No catalog - the forest read fails, names none, or names a blank host, the lookup that once
        // produced a bare ":3268" - refuses the row before any write, and no group is searched for at all.
        // The group is in the default domain, so a fallback to a one-domain search would have found it and
        // written: that is what the refusal rules out.
        SyncedListTheRoomIsNotOnInExchange();
        switch (problem)
        {
            case "Get-ADForest fails": _h.Failures["Get-ADForest"] = "The server has rejected the client credentials."; break;
            case "the forest reports no catalog": _h.GlobalCatalogs.Clear(); break;
            case "the forest reports a blank catalog host": _h.GlobalCatalogs.Clear(); _h.GlobalCatalogs.Add(""); break;
        }

        var result = await Apply(new Service(_h), Room());

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Contains(List, result.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was written.", result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
        Assert.Single(_h.CallsTo("Get-ADForest"));
        Assert.Empty(_h.CallsTo("Get-ADGroup"));
    }

    // The endpoint is a real host and the catalog port, or nothing - never a bare ":3268".
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("gc01.contoso.test", "gc01.contoso.test:3268")]
    [InlineData(" gc01.contoso.test ", "gc01.contoso.test:3268")]
    public void AGlobalCatalogEndpoint_IsAHostAndItsPort_NeverABarePort(string? host, string? endpoint) =>
        Assert.Equal(endpoint, ConferenceRoomService.GlobalCatalogEndpoint(host));

    [Fact]
    public async Task TheGroupFoundInTheForest_IsReReadInItsOwnDomain_SoThePreflightAndTheAddResolveTheSameObject()
    {
        // Both phases resolve the group the same way: the catalog search, then a re-read of the DN it found
        // on the domain that DN names, then the membership read of that DN there - the preflight before
        // Set-Place, the add after - and the write names the objectGUID those re-reads answered, on the
        // same domain.
        SyncedListTheRoomIsNotOnInExchange();
        PlaceAGroupInTheOtherDomain();

        var result = await Apply(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        var calls = _h.Calls.Cast<Hashtable>().ToList();
        var setPlace = calls.FindIndex(c => (string)c["__Command"]! == "Set-Place");
        var searches = CallsWhere(IsGroupSearch);
        var reReads = CallsWhere(IsGroupReRead);
        var memberReads = MembershipReads();
        Assert.Equal(2, searches.Count);
        Assert.Equal(2, reReads.Count);
        Assert.Equal(2, memberReads.Count);
        for (var phase = 0; phase < 2; phase++)
        {
            Assert.True(searches[phase] < reReads[phase] && reReads[phase] < memberReads[phase],
                $"phase {phase}: search at {searches[phase]}, re-read at {reReads[phase]}, membership read at {memberReads[phase]}");
            Assert.Equal(OtherDomainGroupDn, ExchangeWriteHarness.Bound(calls[reReads[phase]], "Identity"));
            Assert.Equal(OtherDomainName, ExchangeWriteHarness.Bound(calls[reReads[phase]], "Server"));
            Assert.Equal(OtherDomainGroupDn, ExchangeWriteHarness.Bound(calls[memberReads[phase]], "Identity"));
            Assert.Equal(OtherDomainName, ExchangeWriteHarness.Bound(calls[memberReads[phase]], "Server"));
        }
        Assert.True(memberReads[0] < setPlace && setPlace < searches[1],
            $"the preflight's membership read at {memberReads[0]}, Set-Place at {setPlace}, the add's search at {searches[1]}");
        var write = Assert.Single(_h.CallsTo("Set-ADGroup"));
        Assert.Equal(OtherDomainGroupGuid, ExchangeWriteHarness.Bound(write, "Identity"));
        Assert.Equal(OtherDomainName, ExchangeWriteHarness.Bound(write, "Server"));
    }

    [Fact]
    public async Task AGroupItsOwnDomainHoldsAsAnotherObject_IsRefusedBeforeAnyWrite()
    {
        // The catalog is a partial copy that can lag its domain. When the group's own domain answers the DN
        // the catalog found with another objectGUID, the row is refused at the re-read - before any write,
        // and before its membership is even read.
        SyncedListTheRoomIsNotOnInExchange();
        PlaceAGroupInTheOtherDomain();
        _h.DirectoryGroupReadBack[OtherDomainGroupDn] = "77777777-7777-7777-7777-777777777777";

        var result = await Apply(new Service(_h), Room());

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Contains(List, result.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was written.", result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
        Assert.Empty(MembershipReads());
    }

    [Fact]
    public void TheHarness_AWriteFindsItsGroupAndMembersOnlyInTheDomainItReaches()
    {
        // The stand-in writes behave as the real ones do, so a restored Add-ADGroupMember fails the rows
        // above for the reason it fails live. Add-ADGroupMember takes a member of the domain it reaches and
        // refuses one from another - routed to the other domain's group, the default domain's room is not
        // found there (the 2026-09-03 failure) - and with no -Server cannot find that group at all.
        // Set-ADGroup finds the group only when routed to its domain.
        const string OtherDomainRoom = "5a5a5a5a-0000-4000-8000-000000000002";
        _h.DirectoryObjects[OtherDomainRoom] = new Hashtable
        {
            ["ObjectGUID"] = Guid.Parse(OtherDomainRoom),
            ["DistinguishedName"] = "CN=Room 2,OU=Rooms," + ExchangeWriteHarness.OtherDirectoryDomainDn,
        };
        PlaceAGroupInTheOtherDomain(keepTheDefaultGroup: true);
        using var rs = _h.OpenDirectoryRunspace();
        using var ps = PowerShell.Create();
        ps.Runspace = rs;

        Run(ps, "Add-ADGroupMember", ("Identity", ExchangeWriteHarness.DefaultDirectoryGroupGuid), ("Members", CheckedObject));
        Run(ps, "Add-ADGroupMember", ("Identity", OtherDomainGroupGuid), ("Members", OtherDomainRoom), ("Server", OtherDomainName));
        Assert.ThrowsAny<RuntimeException>(() =>
            Run(ps, "Add-ADGroupMember", ("Identity", OtherDomainGroupGuid), ("Members", CheckedObject), ("Server", OtherDomainName)));
        Assert.ThrowsAny<RuntimeException>(() =>
            Run(ps, "Add-ADGroupMember", ("Identity", OtherDomainGroupGuid), ("Members", OtherDomainRoom)));

        var addTheRoom = GroupManagementService.BuildMemberAttributeWrite(RoomDn);
        Run(ps, "Set-ADGroup", ("Identity", OtherDomainGroupGuid), ("Add", addTheRoom), ("Server", OtherDomainName));
        Assert.ThrowsAny<RuntimeException>(() => Run(ps, "Set-ADGroup", ("Identity", OtherDomainGroupGuid), ("Add", addTheRoom)));
    }

    // One stand-in command with these parameters, -ErrorAction Stop, so a refusal throws.
    private static Collection<PSObject> Run(PowerShell ps, string command, params (string Name, object Value)[] parameters)
    {
        ps.Commands.Clear();
        ps.Streams.Error.Clear();
        var invocation = ps.AddCommand(command);
        foreach (var (name, value) in parameters)
            invocation.AddParameter(name, value);
        invocation.AddParameter("ErrorAction", "Stop");
        return ps.Invoke();
    }
}
