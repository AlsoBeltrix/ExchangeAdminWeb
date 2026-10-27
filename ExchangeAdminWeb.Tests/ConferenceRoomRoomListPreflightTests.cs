using System.Collections;
using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AdException = Microsoft.ActiveDirectory.Management.ADException;

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
/// answers by objectGUID, and the stand-in Get-ADGroup returns the harness's groups that its filter
/// matches (aud-a2k).
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
        // aud-a2s. For a purpose containing this text, a credential with no password, so the step that builds the AD
        // credential from it throws: the way a test reaches that step. Live it is not reached so - the module's PAM
        // provider returns no credential at all for a secret without a password (DelineaService).
        public string? IncompleteCredentialFor;

        public Service(ExchangeWriteHarness h, ILogger<ConferenceRoomService>? logger = null)
            : base(h.Pool, null!, h.ModuleConfig, null!, h.Trace, h.Config, logger ?? NullLogger<ConferenceRoomService>.Instance) => _h = h;

        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose, CancellationToken cancellationToken)
        {
            CredentialRequests++;
            if (IncompleteCredentialFor is { } incomplete && purpose.Contains(incomplete, StringComparison.Ordinal))
                return Task.FromResult<(string username, string password, string domain)?>(("svc", null!, "CONTOSO"));
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
    // makes the list's address match a group in each (both carry the harness's default address).
    private void PlaceAGroupInTheOtherDomain(bool keepTheDefaultGroup = false)
    {
        if (!keepTheDefaultGroup)
            _h.DirectoryGroupGuids.Clear();
        _h.DirectoryGroupGuids.Add(OtherDomainGroupGuid);
        _h.DirectoryGroupDomains[OtherDomainGroupGuid] = ExchangeWriteHarness.OtherDirectoryDomainDn;
    }

    // The list's group searched for by the list's address (no -Identity, aud-a2k), and re-read by its DN
    // without the member attribute - the membership read is the other read by -Identity.
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

    // The objectGUIDs a stand-in Get-ADGroup search answers with, sent to that server (or none): by the
    // address every stand-in group carries unless a test gives it others (aud-a2k), or by the filter given.
    private static List<string> SearchGroups(PowerShell ps, string? server, string? filter = null)
    {
        ps.Commands.Clear();
        ps.Streams.Error.Clear();
        var search = ps.AddCommand("Get-ADGroup").AddParameter("Filter", filter ?? $"mail -eq '{ExchangeWriteHarness.DefaultListAddress}'");
        if (server is not null)
            search.AddParameter("Server", server);
        search.AddParameter("ErrorAction", "Stop");
        return ps.Invoke().Select(g => g.Properties["ObjectGUID"]?.Value?.ToString() ?? "").ToList();
    }

    [Fact]
    public async Task ARoomListAddressMatchingAGroupInEachDomain_IsRefusedBeforeAnyWrite()
    {
        // The catalog widens what can match, and the exactly-one rule holds across it: the list's address
        // carried by a group in each domain refuses before any write. The default-domain search saw one of
        // them and wrote it. (Named for the list's name until aud-a2k, which binds the group by address.)
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
        // The preflight's and the add's, and since aud-a2l the add's read-back after its write.
        Assert.Equal(3, memberReads.Count);
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

    // aud-a2k. The room list's on-prem group was matched by the list's name (displayName or name), so one
    // unrelated group carrying that name could be written in place of the synced list. These pin the match
    // by the primary SMTP address Exchange holds for the list, alone, and the refusals that come with it.
    // The list's address here is not the harness's default, so no default-attribute group can match it by
    // accident; the synced group carries it, and a namesake - a group with the list's name and another
    // address, or none - stands for the group a name match would have found.
    private const string ListAddress = "building1-rooms@contoso.test";
    private const string SyncedGroupGuid = "44444444-4444-4444-4444-444444444444";
    private const string NamesakeGroupGuid = "33333333-3333-3333-3333-333333333333";

    // The stand-in directory holds these groups and no others.
    private void OnlyTheseGroups(params string[] objectGuids)
    {
        _h.DirectoryGroupGuids.Clear();
        foreach (var objectGuid in objectGuids)
            _h.DirectoryGroupGuids.Add(objectGuid);
    }

    [Fact]
    public async Task TheListsGroup_IsTheOneCarryingItsAddress_NotAGroupThatSharesItsName()
    {
        // The finding's shape with the synced group present too: the list's name is carried by the synced
        // group and by an unrelated one elsewhere in the forest. The address picks the synced group and the
        // row completes on it; matched by name, the two made the row refuse as ambiguous - and with the
        // synced group out of the result, the namesake was the one match, and written. Every search, the
        // preflight's and the add's, asks for the address and nothing else.
        SyncedListTheRoomIsNotOnInExchange();
        _h.ListAddresses[List] = ListAddress;
        OnlyTheseGroups(NamesakeGroupGuid, SyncedGroupGuid);
        _h.DirectoryGroupDomains[NamesakeGroupGuid] = ExchangeWriteHarness.OtherDirectoryDomainDn;
        _h.SetDirectoryGroupAttributes(NamesakeGroupGuid, mail: "facilities@contoso.test", displayName: List, name: List);
        _h.SetDirectoryGroupAttributes(SyncedGroupGuid, mail: ListAddress, displayName: List, name: List);

        var result = await Apply(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Partial);
        Assert.Equal(SyncedGroupGuid, ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Set-ADGroup")), "Identity"));
        var searches = _h.CallsTo("Get-ADGroup").Where(IsGroupSearch).ToList();
        Assert.Equal(2, searches.Count);
        Assert.All(searches, c => Assert.Equal($"mail -eq '{ListAddress}'", ExchangeWriteHarness.Bound(c, "Filter")));
    }

    [Fact]
    public async Task AGroupThatOnlySharesTheListsName_IsNeverWritten()
    {
        // The finding itself: the only group in the forest with the list's name carries another address. It
        // is not the list's group, so the row is refused before any write - not partial - and the group is
        // never even re-read; matched by name it was the one match, and the room was written to it.
        SyncedListTheRoomIsNotOnInExchange();
        _h.ListAddresses[List] = ListAddress;
        OnlyTheseGroups(NamesakeGroupGuid);
        _h.SetDirectoryGroupAttributes(NamesakeGroupGuid, mail: "facilities@contoso.test", displayName: List, name: List);

        var result = await Apply(new Service(_h), Room());

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.EndsWith("Nothing was written.", result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
        Assert.DoesNotContain(_h.CallsTo("Get-ADGroup"), c => c.ContainsKey("Identity"));
    }

    [Fact]
    public async Task ASyncedListWhoseGroupCarriesAnotherAddress_IsRefused_NamingTheListAndTheAddressSearched()
    {
        // The list's own group, by every name it has, but its mail is not the address Exchange holds for the
        // list - one side changed and the other not yet. Which is right is the operator's to settle, not the
        // app's to guess: refused before any write, the message naming the list and the address searched, so
        // the operator knows what to look for in Active Directory.
        SyncedListTheRoomIsNotOnInExchange();
        _h.ListAddresses[List] = ListAddress;
        _h.SetDirectoryGroupAttributes(ExchangeWriteHarness.DefaultDirectoryGroupGuid, mail: "building1.rooms@contoso.test", displayName: List, name: List);

        var result = await Apply(new Service(_h), Room());

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Contains(List, result.Message, StringComparison.Ordinal);
        Assert.Contains(ListAddress, result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ARoomListWithNoPrimaryAddress_IsRefusedBeforeAnyGroupIsLookedUp(string? address)
    {
        // Exchange reports no address for the list - the property absent, empty or blank - so there is
        // nothing to find its group by: refused before any write and before any group is searched for or
        // read. The directory holds a group with the list's name and no mail, the group a search for an
        // empty mail would find (the stand-in reads a missing attribute as empty), and nothing reaches it.
        SyncedListTheRoomIsNotOnInExchange();
        _h.ListAddresses[List] = address;
        OnlyTheseGroups(NamesakeGroupGuid);
        _h.SetDirectoryGroupAttributes(NamesakeGroupGuid, mail: null, displayName: List, name: List);

        var result = await Apply(new Service(_h), Room());

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Contains(List, result.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was written.", result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
        Assert.Empty(_h.CallsTo("Get-ADGroup"));
    }

    [Fact]
    public void TheHarness_AGroupSearchMatchesOnlyTheAttributesItsFilterNames()
    {
        // The stand-in search evaluates its filter, so the tests above pass or fail on what the service asks
        // for, as Active Directory would answer it: an address finds only the group carrying it (quotes
        // doubled, any case); a name finds a group only through a name attribute; any clause of an -or
        // matches; a missing attribute reads as empty; and a filter it cannot read is an error, never every
        // group.
        _h.SetDirectoryGroupAttributes(ExchangeWriteHarness.DefaultDirectoryGroupGuid, mail: "o'brien-rooms@contoso.test", displayName: List, name: "Rooms-B1");
        _h.DirectoryGroupGuids.Add(NamesakeGroupGuid);
        _h.SetDirectoryGroupAttributes(NamesakeGroupGuid, mail: null, displayName: "Facilities", name: "Facilities");
        using var rs = _h.OpenDirectoryRunspace();
        using var ps = PowerShell.Create();
        ps.Runspace = rs;
        string[] theGroup = [ExchangeWriteHarness.DefaultDirectoryGroupGuid];
        var catalog = ExchangeWriteHarness.GlobalCatalogEndpoint;

        Assert.Equal(theGroup, SearchGroups(ps, catalog, "mail -eq 'o''brien-rooms@contoso.test'"));
        Assert.Equal(theGroup, SearchGroups(ps, catalog, "mail -eq 'O''BRIEN-ROOMS@CONTOSO.TEST'"));
        Assert.Empty(SearchGroups(ps, catalog, $"mail -eq '{List}'"));
        Assert.Empty(SearchGroups(ps, catalog, $"mail -eq '{ExchangeWriteHarness.DefaultListAddress}'"));
        Assert.Equal(theGroup, SearchGroups(ps, catalog, $"displayName -eq '{List}'"));
        Assert.Equal(theGroup, SearchGroups(ps, catalog, "name -eq 'Rooms-B1'"));
        Assert.Equal(theGroup, SearchGroups(ps, catalog, $"mail -eq 'facilities@contoso.test' -or displayName -eq '{List}'"));
        Assert.Equal([NamesakeGroupGuid], SearchGroups(ps, catalog, "mail -eq ''"));
        Assert.ThrowsAny<RuntimeException>(() => SearchGroups(ps, catalog, "mail -like '*'"));
    }

    // aud-a2l. The on-prem add took "Set-ADGroup raised no error" as success, so a write that changed nothing
    // was audited and announced as done; and it took every error as failure, so a write refused because
    // another actor had just added the same room came back partial with the room in place. These pin the
    // read-back that now decides both, and the one write error it is asked about.
    private const string TimedOut = "The operation was aborted because the client side timeout limit was exceeded.";

    [Fact]
    public async Task TheOnPremAdd_IsConfirmedByReadingTheGroupsMemberAttributeBack()
    {
        // The normal case: the write, then a third membership read - after the preflight's and the add's own
        // check - made after the write, the a2i read of the group's member attribute by its DN on its domain.
        // It finds the room, so the row succeeds and the room's object is reported written.
        SyncedListTheRoomIsNotOnInExchange();

        var result = await Apply(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Partial);
        Assert.Equal(CheckedObject, result.WrittenDirectoryObject);
        Assert.Null(result.DirectoryMembershipConfirmed);
        var calls = _h.Calls.Cast<Hashtable>().ToList();
        var write = calls.FindIndex(c => (string)c["__Command"]! == "Set-ADGroup");
        var reads = MembershipReads();
        Assert.Equal(3, reads.Count);
        Assert.True(reads[1] < write && write < reads[2], $"membership reads at {string.Join(", ", reads)}, the write at {write}");
        AssertReadsTheGroupsMemberAttributeInItsDomain(calls[reads[2]]);
    }

    [Fact]
    public async Task AnOnPremAddTheGroupDoesNotShowAfterwards_IsAFailedRow_NotAWrite()
    {
        // The finding: Set-ADGroup raises nothing and changes nothing. The read-back does not find the room,
        // so the row fails - partial, the Exchange writes having committed - nothing is reported written, and
        // the message names the group and the server it was read from (the group is in the other domain
        // here, so neither can be confused with the room's own address).
        SyncedListTheRoomIsNotOnInExchange();
        PlaceAGroupInTheOtherDomain();
        _h.SetAdGroupChangesNothing = true;

        var result = await Apply(new Service(_h), Room());

        Assert.False(result.Success);
        Assert.True(result.Partial);
        Assert.Null(result.WrittenDirectoryObject);
        Assert.Single(_h.CallsTo("Set-ADGroup"));
        Assert.Contains(OtherDomainGroupDn, result.Message, StringComparison.Ordinal);
        Assert.Contains(OtherDomainName, result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOnPremAddRefusedAsAValueAlreadyThere_IsSettledByTheReadBack(bool theRoomIsThere)
    {
        // The folded-in race: the write is refused as a value already there. With the room on the group -
        // another actor's add landed between the add's membership read and its write - the read-back finds it
        // and the row succeeds; it used to be partial. With the room absent (the refusal here in its text form,
        // no code), the same refusal is a failure. Either way the read-back was made, and since aud-a2n the
        // room is never reported written: this row's write was refused. The success says so instead.
        SyncedListTheRoomIsNotOnInExchange();
        if (theRoomIsThere)
            _h.AnotherAddLandsFirst = true;
        else
            _h.Failures["Set-ADGroup"] = ExchangeWriteHarness.DuplicateMemberValueError;

        var result = await Apply(new Service(_h), Room());

        Assert.Single(_h.CallsTo("Set-ADGroup"));
        Assert.Equal(3, MembershipReads().Count);
        Assert.Equal(theRoomIsThere, result.Success);
        Assert.Equal(!theRoomIsThere, result.Partial);
        Assert.Null(result.WrittenDirectoryObject);
        if (theRoomIsThere)
            Assert.StartsWith(ConferenceRoomService.ConfirmedAfterRefusal, result.DirectoryMembershipConfirmed, StringComparison.Ordinal);
        else
            Assert.Null(result.DirectoryMembershipConfirmed);
    }

    [Fact]
    public async Task AnyOtherWriteError_WithTheRoomThere_IsSettledByTheReadBack_NotRecordedAsWritten()
    {
        // aud-a2n, the folded-in item 2: every write error is read back, not only the duplicate. A timeout here,
        // with another actor's add of the room landing first: the read-back - a third membership read - finds
        // the room, so the row succeeds, recorded as confirmed after a write error (not a refusal: the error is
        // not one), with the error's text kept and no object reported written. aud-a2l left it partial.
        SyncedListTheRoomIsNotOnInExchange();
        _h.AnotherAddLandsFirst = true;
        _h.Failures["Set-ADGroup"] = TimedOut;

        var result = await Apply(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Partial);
        Assert.Null(result.WrittenDirectoryObject);
        Assert.StartsWith(ConferenceRoomService.ConfirmedAfterWriteError, result.DirectoryMembershipConfirmed, StringComparison.Ordinal);
        Assert.Contains(TimedOut, result.DirectoryMembershipConfirmed, StringComparison.Ordinal);
        Assert.Equal(3, MembershipReads().Count);
    }

    // aud-a2n. The duplicate was recognized by English text alone, so a refusal in another language read as some
    // other error; it is now known by its code, the text deciding only when no code is carried. Each test makes
    // the write fail both ways the harness can: thrown (the code in the exception's InnerException, as
    // Failures throws it) and refused by the stand-in itself (the code in the error record, as Write-Error under
    // -ErrorAction Stop leaves it).
    private void TheWriteFails(Exception error, bool thrown)
    {
        if (thrown)
            _h.Failures["Set-ADGroup"] = error;
        else
            _h.DuplicateMemberValueRefusal = error;
    }

    [Theory]
    [InlineData(68, "Der angegebene Kontoname ist bereits ein Mitglied der Gruppe.", false)]
    [InlineData(68, "Der angegebene Kontoname ist bereits ein Mitglied der Gruppe.", true)]
    [InlineData(20, "La valeur ou l'attribut indique figure deja dans l'annuaire.", false)]
    [InlineData(1378, "De opgegeven accountnaam is al lid van de groep.", true)]
    public async Task AValueAlreadyThereRefusal_IsKnownByItsCode_InAnyLanguage(int code, string message, bool thrown)
    {
        // Another actor's add lands first and the write is refused with a value-already-there code under a
        // message no English marker matches. The code decides: confirmed after a refusal, nothing written.
        SyncedListTheRoomIsNotOnInExchange();
        _h.AnotherAddLandsFirst = true;
        TheWriteFails(new AdException(message, code), thrown);

        var result = await Apply(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Partial);
        Assert.Null(result.WrittenDirectoryObject);
        Assert.StartsWith(ConferenceRoomService.ConfirmedAfterRefusal, result.DirectoryMembershipConfirmed, StringComparison.Ordinal);
        Assert.Equal(3, MembershipReads().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AWriteErrorWithAnotherCode_IsNotTakenForAValueAlreadyThere_ThoughItsTextSaysAlreadyExists(bool thrown)
    {
        // The code is not a value-already-there code (8224, ERROR_DS_OPERATIONS_ERROR), though the message
        // happens to say "already exists": the code wins. Read back like any write error, the room there, the
        // row succeeds - confirmed after a write error, not after a refusal.
        SyncedListTheRoomIsNotOnInExchange();
        _h.AnotherAddLandsFirst = true;
        TheWriteFails(new AdException("The operation failed: a request for this object already exists on the server.", 8224), thrown);

        var result = await Apply(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        Assert.Null(result.WrittenDirectoryObject);
        Assert.StartsWith(ConferenceRoomService.ConfirmedAfterWriteError, result.DirectoryMembershipConfirmed, StringComparison.Ordinal);
        Assert.Equal(3, MembershipReads().Count);
    }

    [Fact]
    public async Task ASetADGroupThatAppliedTheAddThenFailed_IsASuccess_WithTheErrorInTheDetail_NothingRecordedAsWritten()
    {
        // The write the server took and then reported as failed - a timeout after the commit. The read-back
        // finds the room: a success. Whether this row's write put it there cannot be told from the error, so
        // nothing is reported written; the detail - the on-prem step's too - keeps the error's text.
        SyncedListTheRoomIsNotOnInExchange();
        _h.SetAdGroupFailsAfterApplying = TimedOut;

        var result = await Apply(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Partial);
        Assert.Null(result.WrittenDirectoryObject);
        Assert.Contains(RoomDn, _h.DirectoryGroupMembers.Cast<string>());
        var detail = result.DirectoryMembershipConfirmed;
        Assert.StartsWith(ConferenceRoomService.ConfirmedAfterWriteError, detail, StringComparison.Ordinal);
        Assert.Contains(TimedOut, detail, StringComparison.Ordinal);
        Assert.Contains(result.Steps, s => s.Success && s.Stage == $"Add to '{List}' (on-prem)" && s.Error == detail);
        Assert.Equal(3, MembershipReads().Count);
    }

    [Fact]
    public async Task ASetADGroupThatFailedWithoutApplying_StaysAFailedRow_CarryingTheWriteError()
    {
        // The same error, the add not applied: the read-back - made, the third membership read - does not find
        // the room, so the row fails, partial after the Exchange writes, the write's error in its message.
        SyncedListTheRoomIsNotOnInExchange();
        _h.Failures["Set-ADGroup"] = TimedOut;

        var result = await Apply(new Service(_h), Room());

        Assert.False(result.Success);
        Assert.True(result.Partial);
        Assert.Null(result.WrittenDirectoryObject);
        Assert.Null(result.DirectoryMembershipConfirmed);
        Assert.Contains(TimedOut, result.Message, StringComparison.Ordinal);
        Assert.Equal(3, MembershipReads().Count);
    }

    [Fact]
    public async Task AReadBackThatFailsAfterAWriteError_IsAFailedRow_CarryingBothErrors()
    {
        // The write errs and the read-back cannot be made: nothing settles the row, so it fails - and says both
        // what the read and what the write reported.
        const string ReadBackDown = "The server is not operational.";
        SyncedListTheRoomIsNotOnInExchange();
        _h.Failures["Set-ADGroup"] = TimedOut;
        _h.MemberReadBackFailure = ReadBackDown;

        var result = await Apply(new Service(_h), Room());

        Assert.False(result.Success);
        Assert.True(result.Partial);
        Assert.Null(result.WrittenDirectoryObject);
        Assert.Contains(ReadBackDown, result.Message, StringComparison.Ordinal);
        Assert.Contains(TimedOut, result.Message, StringComparison.Ordinal);
        Assert.Equal(3, MembershipReads().Count);
    }

    [Fact]
    public async Task TheOnPremNoOp_HasAStepOfItsOwn_NotTheAddsStep()
    {
        // aud-a2o. A re-run inside the sync window: the first run adds the room to the list's on-prem group; the
        // second finds it already a member there - not yet on Exchange's copy - and writes nothing. The second's
        // room-list step read as the first's, as if it had written too. The two now differ, both successful, the
        // no-op's naming the list and carrying no detail; only the first wrote.
        SyncedListTheRoomIsNotOnInExchange();
        var service = new Service(_h);

        var added = await Apply(service, Room());
        var alreadyThere = await Apply(service, Room());

        Assert.True(added.Success, added.Message);
        Assert.True(alreadyThere.Success, alreadyThere.Message);
        Assert.False(alreadyThere.Partial);
        Assert.Single(_h.CallsTo("Set-ADGroup"));
        Assert.Equal(CheckedObject, added.WrittenDirectoryObject);
        Assert.Null(alreadyThere.WrittenDirectoryObject);
        Assert.Null(alreadyThere.DirectoryMembershipConfirmed);
        // The room-list step is each run's last.
        var addStep = added.Steps[^1];
        var noOpStep = alreadyThere.Steps[^1];
        Assert.True(addStep.Success, addStep.Stage);
        Assert.True(noOpStep.Success, noOpStep.Stage);
        Assert.NotEqual(addStep.Stage, noOpStep.Stage);
        Assert.Contains(List, noOpStep.Stage, StringComparison.Ordinal);
        Assert.Null(noOpStep.Error);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnErrorTheWriteReportsWithoutThrowing_IsNotTakenForItsError_TheReadBackDecides(bool applied)
    {
        // aud-a2o. The write's error is the one it throws, as in GroupManagementService's Set-ADGroup writes: under
        // -ErrorAction Stop an error a cmdlet writes is thrown, so one reported without a throw - the stand-in's own
        // -ErrorAction Continue - is not Set-ADGroup's, and the HadErrors check that took it is gone. The read-back
        // decides, the record the write left cleared before it so the read is not failed by it: applied, a clean
        // write - the room's object written, no confirmed detail; not applied, the not-confirmed failure, without
        // the reported text.
        const string Reported = "A non-terminating error the write reported and carried on past.";
        SyncedListTheRoomIsNotOnInExchange();
        _h.SetAdGroupReportsErrorWithoutThrowing = Reported;
        _h.SetAdGroupChangesNothing = !applied;

        var result = await Apply(new Service(_h), Room());

        Assert.Single(_h.CallsTo("Set-ADGroup"));
        Assert.Equal(3, MembershipReads().Count);
        Assert.Equal(applied, result.Success);
        Assert.Equal(!applied, result.Partial);
        Assert.Equal(applied ? CheckedObject : null, result.WrittenDirectoryObject);
        Assert.Null(result.DirectoryMembershipConfirmed);
        Assert.DoesNotContain(Reported, result.Message, StringComparison.Ordinal);
    }

    // aud-a2p. The list as the preflight reads a synced one: existing, synchronized from on-premises, with the
    // harness's address and Guid - the list the on-prem add is handed.
    private static ConferenceRoomService.RoomListTarget TheSyncedList() =>
        new(List, Exists: true, IsLegacy: false, IsDirSynced: true, ExchangeWriteHarness.DefaultListAddress, ExchangeWriteHarness.DefaultListGuid);

    [Theory]
    [InlineData("already a member")]
    [InlineData("confirmed after a refused add")]
    [InlineData("written")]
    public async Task TheOnPremAddsOwnMessage_ClaimsAWriteOnlyWhenItsWriteAddedTheRoom(string outcome)
    {
        // aud-a2p. The list add's message, which its caller replaces on success: every on-prem success said
        // "Membership was written on-prem", the no-op that wrote nothing and the add the read-back confirmed after
        // a refusal included. Each now says what happened; only the add whose write put the room there claims one.
        // Called directly, as the message reaches no result the row returns.
        SyncedListTheRoomIsNotOnInExchange();
        if (outcome == "already a member")
            _h.DirectoryGroupMembers.Add(RoomDn);
        else if (outcome == "confirmed after a refused add")
            _h.AnotherAddLandsFirst = true;

        var result = await new Service(_h).AddToRoomListAsync(Room(), Typed, Typed, TheSyncedList());

        Assert.True(result.Success, result.Message);
        var added = ConferenceRoomService.BuildRoomListAddedMessage(List, viaOnPrem: true);
        switch (outcome)
        {
            case "already a member":
                Assert.Empty(_h.CallsTo("Set-ADGroup"));
                Assert.NotEqual(added, result.Message);
                Assert.Equal(ConferenceRoomService.BuildOnPremRoomListNotWrittenMessage(List, confirmedAfterWrite: false), result.Message);
                break;
            case "confirmed after a refused add":
                Assert.Single(_h.CallsTo("Set-ADGroup"));
                Assert.NotNull(result.DirectoryMembershipConfirmed);
                Assert.NotEqual(added, result.Message);
                Assert.Equal(ConferenceRoomService.BuildOnPremRoomListNotWrittenMessage(List, confirmedAfterWrite: true), result.Message);
                break;
            default:
                Assert.Single(_h.CallsTo("Set-ADGroup"));
                Assert.Equal(CheckedObject, result.WrittenDirectoryObject);
                Assert.Equal(added, result.Message);
                break;
        }
    }

    [Fact]
    public void TheOnPremAddsNotWrittenMessages_NameTheList_AndClaimNoAdd()
    {
        var alreadyThere = ConferenceRoomService.BuildOnPremRoomListNotWrittenMessage(List, confirmedAfterWrite: false);
        var confirmed = ConferenceRoomService.BuildOnPremRoomListNotWrittenMessage(List, confirmedAfterWrite: true);

        Assert.NotEqual(alreadyThere, confirmed);
        Assert.All(new[] { alreadyThere, confirmed }, message =>
        {
            Assert.Contains($"'{List}'", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Added to", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Membership was written", message, StringComparison.OrdinalIgnoreCase);
        });
    }

    // aud-a2q. The list as the preflight reads a cloud one: existing, not synchronized from on-premises - the list the
    // cloud add is handed.
    private static ConferenceRoomService.RoomListTarget TheCloudList() =>
        new(List, Exists: true, IsLegacy: false, IsDirSynced: false, ExchangeWriteHarness.DefaultListAddress, ExchangeWriteHarness.DefaultListGuid);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheCloudAddsOwnMessage_ClaimsAnAddOnlyWhenItAddedTheRoom(bool alreadyThere)
    {
        // aud-a2q. The cloud twin of the on-prem messages above: the no-op that found the room already on the list - its
        // step "Already in '<list>'" - still said "Added to room list". Each now says what happened. Called directly, as
        // the message reaches no result the row returns.
        if (alreadyThere)
            _h.ListMembers.Add(Typed);

        var result = await new Service(_h).AddToRoomListAsync(Room(), Typed, Typed, TheCloudList());

        Assert.True(result.Success, result.Message);
        var added = ConferenceRoomService.BuildRoomListAddedMessage(List, viaOnPrem: false);
        if (alreadyThere)
        {
            Assert.Empty(_h.CallsTo("Add-DistributionGroupMember"));
            Assert.Contains(result.Steps, s => s.Stage == $"Already in '{List}'" && s.Success);
            Assert.NotEqual(added, result.Message);
            Assert.Equal(ConferenceRoomService.BuildRoomListAlreadyInMessage(List), result.Message);
        }
        else
        {
            Assert.Single(_h.CallsTo("Add-DistributionGroupMember"));
            Assert.Equal(added, result.Message);
        }
    }

    [Fact]
    public void TheCloudAddsAlreadyInMessage_NamesTheList_AndClaimsNoAdd()
    {
        var alreadyIn = ConferenceRoomService.BuildRoomListAlreadyInMessage(List);

        Assert.Contains($"'{List}'", alreadyIn, StringComparison.Ordinal);
        Assert.DoesNotContain("Added to", alreadyIn, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(ConferenceRoomService.BuildOnPremRoomListNotWrittenMessage(List, confirmedAfterWrite: false), alreadyIn);
    }

    // A directory exception type other than the base one, as the real module derives several (aud-a2n): the
    // code is read through the base type's name.
    private sealed class DerivedDirectoryException(string message, int errorCode) : AdException(message, errorCode);

    [Theory]
    // A code a value already there is reported with decides, the text ignored - none of these has an English marker.
    [InlineData(20, "Wert existiert bereits.", "direct", true)]
    [InlineData(68, "Wert existiert bereits.", "inner", true)]
    [InlineData(1378, "Wert existiert bereits.", "record", true)]
    [InlineData(8205, "Wert existiert bereits.", "direct", true)]
    [InlineData(8305, "Wert existiert bereits.", "record", true)]
    [InlineData(8323, "Wert existiert bereits.", "inner", true)]
    [InlineData(1378, "Wert existiert bereits.", "derived", true)]
    // Any other code decides as well, whatever the text says.
    [InlineData(8224, "The object already exists.", "direct", false)]
    [InlineData(50, ExchangeWriteHarness.DuplicateMemberValueError, "record", false)]
    [InlineData(1722, "00000562: UpdErr: DSID-031A11C4, problem 6005 (ENTRY_EXISTS), data 0", "inner", false)]
    // No code - 0 is none - and the text decides, aud-a2l's forms.
    [InlineData(0, ExchangeWriteHarness.DuplicateMemberValueError, "record", true)]
    [InlineData(0, "Wert existiert bereits.", "record", false)]
    public void AValueAlreadyThereRefusal_IsDecidedByItsCode_TheTextOnlyWhenItCarriesNone(int code, string message, string where, bool expected)
    {
        var directory = where == "derived" ? new DerivedDirectoryException(message, code) : new AdException(message, code);
        Exception error = where switch
        {
            "direct" or "derived" => directory,
            "inner" => new RuntimeException("The pipeline failed.", directory),
            "record" => new RuntimeException("The running command stopped.", null, new ErrorRecord(directory, "SetADGroup", ErrorCategory.NotSpecified, null)),
            _ => throw new ArgumentOutOfRangeException(nameof(where)),
        };

        Assert.Equal(expected, ConferenceRoomService.IsValueAlreadyPresentRefusal(error));
    }

    [Fact]
    public void AnErrorCodeOnAnyOtherExceptionType_IsNotADirectoryCode()
    {
        // Only a directory exception's ErrorCode is a directory code: another type's (an ExternalException's,
        // here 8224) is not, so the text decides - and this one's is the duplicate's.
        var error = new System.Runtime.InteropServices.ExternalException(ExchangeWriteHarness.DuplicateMemberValueError, 8224);

        Assert.True(ConferenceRoomService.IsValueAlreadyPresentRefusal(error));
    }

    [Theory]
    [InlineData(ExchangeWriteHarness.DuplicateMemberValueError, true)]
    [InlineData("The specified directory service attribute or value already exists.", true)]
    [InlineData("The object already exists.", true)]
    [InlineData("00000562: UpdErr: DSID-031A11C4, problem 6005 (ENTRY_EXISTS), data 0", true)]
    [InlineData("The running command stopped because the preference variable \"ErrorActionPreference\" or common parameter is set to Stop: The specified account name is already a member of the group.", true)]
    [InlineData("Insufficient access rights to perform the operation", false)]
    [InlineData(TimedOut, false)]
    [InlineData("Cannot find an object with identity: 'CN=Room 1,OU=Rooms,DC=contoso,DC=test'", false)]
    [InlineData("", false)]
    public void ADuplicateValueRefusal_IsRecognisedInEachFormItIsReported(string message, bool duplicate)
    {
        // The forms Active Directory and ADSI report a value already present in, alone and as PowerShell wraps
        // them under -ErrorAction Stop, and as an inner exception; other write errors are not one.
        Assert.Equal(duplicate, ConferenceRoomService.IsDuplicateMemberValue(message));
        Assert.Equal(duplicate, ConferenceRoomService.IsDuplicateMemberValue(new RuntimeException("The pipeline failed.", new InvalidOperationException(message))));
    }

    [Fact]
    public void TheHarness_SetADGroup_AddsTheMember_RefusesOneAlreadyThere_AndCanDropOrBeRaced()
    {
        // The stand-in write behaves as the tests above need it to, or they would pass for the wrong reason:
        // it puts the DN on the group, and the same add again is refused with an error the service recognizes
        // as a value already there - the exception PowerShell actually throws, not a string, its code in the
        // error record (aud-a2n); told to change nothing, it changes nothing; told another add lands first, the
        // DN is there and this write is refused; told to fail after applying, it applies the add and throws; told
        // the read-back fails, a member read made after a write fails, one made before does not; and told to report
        // an error without throwing (aud-a2o), it applies the add and reports one that -ErrorAction Stop does not
        // throw - so a test of an error the write did not throw has one to ignore.
        (string Name, object Value)[] addTheRoom =
            [("Identity", ExchangeWriteHarness.DefaultDirectoryGroupGuid), ("Add", GroupManagementService.BuildMemberAttributeWrite(RoomDn)), ("Server", "contoso.test")];
        (string Name, object Value)[] readTheMembers =
            [("Identity", GroupDn), ("Properties", new[] { "member" }), ("Server", "contoso.test")];
        List<string> Members() => _h.DirectoryGroupMembers.Cast<object>().Select(m => m.ToString()!).ToList();
        void AssertRefusedAsAValueAlreadyThere(RuntimeException refused)
        {
            Assert.Same(_h.DuplicateMemberValueRefusal, Assert.IsAssignableFrom<IContainsErrorRecord>(refused).ErrorRecord.Exception);
            Assert.True(ConferenceRoomService.IsValueAlreadyPresentRefusal(refused));
        }

        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Run(ps, "Set-ADGroup", addTheRoom);
            Assert.Equal([RoomDn], Members());
            AssertRefusedAsAValueAlreadyThere(Assert.ThrowsAny<RuntimeException>(() => Run(ps, "Set-ADGroup", addTheRoom)));
            Assert.Equal([RoomDn], Members());
        }

        _h.DirectoryGroupMembers.Clear();
        _h.SetAdGroupChangesNothing = true;
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Run(ps, "Set-ADGroup", addTheRoom);
            Assert.Empty(Members());
        }

        _h.SetAdGroupChangesNothing = false;
        _h.AnotherAddLandsFirst = true;
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            AssertRefusedAsAValueAlreadyThere(Assert.ThrowsAny<RuntimeException>(() => Run(ps, "Set-ADGroup", addTheRoom)));
            Assert.Equal([RoomDn], Members());
        }

        _h.DirectoryGroupMembers.Clear();
        _h.Calls.Clear();
        _h.AnotherAddLandsFirst = false;
        _h.SetAdGroupFailsAfterApplying = TimedOut;
        _h.MemberReadBackFailure = "The server is not operational.";
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Assert.Single(Run(ps, "Get-ADGroup", readTheMembers));
            Assert.Contains(TimedOut, Assert.ThrowsAny<RuntimeException>(() => Run(ps, "Set-ADGroup", addTheRoom)).Message, StringComparison.Ordinal);
            Assert.Equal([RoomDn], Members());
            Assert.Contains("not operational", Assert.ThrowsAny<RuntimeException>(() => Run(ps, "Get-ADGroup", readTheMembers)).Message, StringComparison.Ordinal);
        }

        _h.DirectoryGroupMembers.Clear();
        _h.SetAdGroupFailsAfterApplying = null;
        _h.MemberReadBackFailure = null;
        _h.SetAdGroupReportsErrorWithoutThrowing = "Reported, not thrown.";
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Run(ps, "Set-ADGroup", addTheRoom);
            Assert.True(ps.HadErrors);
            Assert.Equal("Reported, not thrown.", Assert.Single(ps.Streams.Error).Exception.Message);
            Assert.Equal([RoomDn], Members());
        }
    }

    // ---- aud-a2s: a failure on either on-prem path names the step it came from ----
    // The preflight's catch told every throw as "Failed to validate the on-prem room list '<list>' before writing:
    // <error>", and the add's as "Failed to add the room to the on-prem room list on the AD object: <error>", logged as a
    // failed Set-ADGroup, whichever step had thrown - the runspace, the credential, the room's object read, the group
    // search, the group's re-read in its own domain, the membership read. Only the import was named (aud-a2r), by a catch
    // of its own.

    private const string Injected = "The injected failure.";

    private sealed class CapturingLogger : ILogger<ConferenceRoomService>
    {
        public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    // The step a failure is injected at, as the catch names it. (The catalog is named by its own refusal instead.)
    private static string StepNamed(string failing) => failing switch
    {
        "runspace" => "opening the directory runspace",
        "import" => "importing the ActiveDirectory module",
        "credential" => "preparing the module's AD credential",
        "room object read" => "reading the room's AD object",
        "group search" => "searching the forest's global catalog for the list's group",
        "group re-read" => "reading the list's group in its own domain",
        "membership read" => "reading the membership of the list's group",
        _ => throw new ArgumentOutOfRangeException(nameof(failing), failing, null),
    };

    // The text the injected failure carries: the one given, or, for the credential, which has none to give, the message of
    // what building a credential with no password throws.
    private static string InjectedText(string failing) =>
        failing == "credential" ? new NullReferenceException().Message : Injected;

    [Theory]
    [InlineData("runspace")]
    [InlineData("import")]
    [InlineData("credential")]
    [InlineData("room object read")]
    [InlineData("catalog")]
    [InlineData("group search")]
    [InlineData("group re-read")]
    [InlineData("membership read")]
    public async Task AFailureAtEachStepOfTheRoomListPreflight_NamesThatStep(string failing)
    {
        // Each step of the preflight fails in turn, before Set-Place. Each refuses the row while nothing is written, in its
        // one step, and the message and the log line name the step that failed and carry its error. The catalog's failure
        // is the resolver's own refusal, which names it.
        SyncedListTheRoomIsNotOnInExchange();
        var logger = new CapturingLogger();
        var service = new Service(_h, logger);
        switch (failing)
        {
            case "runspace": _h.DirectoryRunspaceFailure = Injected; break;
            case "import": _h.ImportModuleFailure = Injected; break;
            case "credential": service.IncompleteCredentialFor = "membership preflight"; break;
            case "room object read": _h.Failures["Get-ADUser"] = Injected; break;
            case "catalog": _h.Failures["Get-ADForest"] = Injected; break;
            case "group search": _h.Failures["Get-ADGroup"] = Injected; break;
            case "group re-read": _h.Failures["Get-ADGroup -Identity"] = Injected; break;
            default: _h.Failures[ExchangeWriteHarness.MemberReadFailureKey] = Injected; break;
        }

        var result = await Apply(service, Room());

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Empty(_h.Writes);
        var step = Assert.Single(result.Steps);
        Assert.Equal("Room list preflight", step.Stage);
        Assert.False(step.Success);
        Assert.Equal(result.Message, step.Error);
        Assert.Contains(List, result.Message, StringComparison.Ordinal);
        Assert.Contains(InjectedText(failing), result.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was written.", result.Message, StringComparison.Ordinal);
        if (failing == "catalog")
        {
            Assert.StartsWith($"The forest's global catalog could not be resolved ({Injected})", result.Message, StringComparison.Ordinal);
            return;
        }
        var named = StepNamed(failing);
        Assert.StartsWith($"Failed to validate the on-prem room list '{List}' before writing: {named} failed: ", result.Message, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains($": {named} failed; nothing was written", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("runspace")]
    [InlineData("import")]
    [InlineData("credential")]
    [InlineData("room object read")]
    [InlineData("catalog")]
    [InlineData("group search")]
    [InlineData("group re-read")]
    [InlineData("membership read")]
    [InlineData("write")]
    [InlineData("read-back")]
    public async Task AFailureAtEachStepOfTheOnPremRoomListAdd_NamesThatStep(string failing)
    {
        // Each step of the add fails in turn - after Set-Place, so the preflight's own steps pass. Each is a failed, partial
        // row naming nothing written, and its message names the step that failed: the steps before the write in the catch's
        // words, logged naming the step and never Set-ADGroup, no write made; the catalog in the resolver's own refusal; the
        // write and the read-back each in their own words (aud-a2n), the write made.
        SyncedListTheRoomIsNotOnInExchange();
        var logger = new CapturingLogger();
        var service = new Service(_h, logger);
        switch (failing)
        {
            case "runspace": _h.DirectoryRunspaceFailureAfterSetPlace = Injected; break;
            case "import": _h.ImportModuleFailureAfterSetPlace = Injected; break;
            case "credential": service.IncompleteCredentialFor = "membership add"; break;
            case "room object read": _h.ObjectReadFailureAfterSetPlace = Injected; break;
            case "catalog": _h.FailuresAfterSetPlace["Get-ADForest"] = Injected; break;
            case "group search": _h.FailuresAfterSetPlace["Get-ADGroup"] = Injected; break;
            case "group re-read": _h.FailuresAfterSetPlace["Get-ADGroup -Identity"] = Injected; break;
            case "membership read": _h.FailuresAfterSetPlace[ExchangeWriteHarness.MemberReadFailureKey] = Injected; break;
            case "write": _h.Failures["Set-ADGroup"] = Injected; break;
            default: _h.MemberReadBackFailure = Injected; break;
        }

        var result = await Apply(service, Room());

        Assert.False(result.Success);
        Assert.True(result.Partial);
        Assert.Null(result.WrittenDirectoryObject);
        Assert.Null(result.DirectoryMembershipConfirmed);
        Assert.Single(_h.CallsTo("Set-Place"));
        var step = Assert.Single(result.Steps, s => s.Stage == $"Add to '{List}' (on-prem)");
        Assert.False(step.Success);
        Assert.NotNull(step.Error);
        Assert.Contains(step.Error, result.Message, StringComparison.Ordinal);
        Assert.Contains(InjectedText(failing), step.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was written", result.Message, StringComparison.Ordinal);
        switch (failing)
        {
            case "catalog":
                Assert.StartsWith($"The forest's global catalog could not be resolved ({Injected})", step.Error, StringComparison.Ordinal);
                Assert.Empty(_h.CallsTo("Set-ADGroup"));
                break;
            case "write":
                Assert.StartsWith($"Adding {Typed} to on-prem room list '{List}' was not confirmed: ", step.Error, StringComparison.Ordinal);
                Assert.Contains(", and Set-ADGroup reported an error (", step.Error, StringComparison.Ordinal);
                Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not confirmed", StringComparison.Ordinal));
                Assert.Single(_h.CallsTo("Set-ADGroup"));
                break;
            case "read-back":
                Assert.StartsWith($"Adding {Typed} to on-prem room list '{List}' could not be confirmed: reading its group {GroupDn} back from contoso.test failed: ", step.Error, StringComparison.Ordinal);
                Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("could not be confirmed", StringComparison.Ordinal));
                Assert.Single(_h.CallsTo("Set-ADGroup"));
                break;
            default:
                var named = StepNamed(failing);
                Assert.StartsWith($"Failed to add the room to the on-prem room list '{List}': {named} failed: ", step.Error, StringComparison.Ordinal);
                Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains($": {named} failed", StringComparison.Ordinal));
                Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("Set-ADGroup", StringComparison.Ordinal));
                Assert.Empty(_h.CallsTo("Set-ADGroup"));
                break;
        }
    }

    [Fact]
    public void TheHarness_FailuresAfterSetPlace_AndTheMemberReadKey_FailOnlyWhatTheyName()
    {
        // The knobs the two tests above rest on, so that they cannot pass for the wrong reason. Under -ErrorAction Stop, as
        // the code calls the stand-ins: the member key in Failures fails a read asking for the member attribute, at any time,
        // and neither the group search nor the re-read; each key of FailuresAfterSetPlace fails nothing before a Set-Place
        // is recorded and its own call after one - Get-ADForest, the search (any Get-ADGroup), a read by identity, a member
        // read - and the member key and the read-by-identity key no other read.
        const string Later = "The failure after Set-Place.";
        (string Name, object Value)[] search = [("Filter", $"mail -eq '{ExchangeWriteHarness.DefaultListAddress}'"), ("Server", ExchangeWriteHarness.GlobalCatalogEndpoint)];
        (string Name, object Value)[] reRead = [("Identity", GroupDn), ("Server", "contoso.test")];
        (string Name, object Value)[] memberRead = [("Identity", GroupDn), ("Properties", new[] { "member" }), ("Server", "contoso.test")];
        void SetPlaceRecorded() => _h.Calls.Add(new Hashtable { ["__Command"] = "Set-Place" });

        _h.Failures[ExchangeWriteHarness.MemberReadFailureKey] = Injected;
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Assert.Single(Run(ps, "Get-ADGroup", search));
            Assert.Single(Run(ps, "Get-ADGroup", reRead));
            Assert.Contains(Injected, Assert.ThrowsAny<RuntimeException>(() => Run(ps, "Get-ADGroup", memberRead)).Message, StringComparison.Ordinal);
        }
        _h.Failures.Clear();

        void FailsOnlyAfterSetPlace(string key, string command, (string Name, object Value)[] parameters)
        {
            _h.Calls.Clear();
            _h.FailuresAfterSetPlace.Clear();
            _h.FailuresAfterSetPlace[key] = Later;
            using var rs = _h.OpenDirectoryRunspace();
            using var ps = PowerShell.Create();
            ps.Runspace = rs;
            Assert.NotEmpty(Run(ps, command, parameters));
            SetPlaceRecorded();
            Assert.Contains(Later, Assert.ThrowsAny<RuntimeException>(() => Run(ps, command, parameters)).Message, StringComparison.Ordinal);
        }
        FailsOnlyAfterSetPlace("Get-ADForest", "Get-ADForest", []);
        FailsOnlyAfterSetPlace("Get-ADGroup", "Get-ADGroup", search);
        FailsOnlyAfterSetPlace("Get-ADGroup -Identity", "Get-ADGroup", reRead);
        FailsOnlyAfterSetPlace(ExchangeWriteHarness.MemberReadFailureKey, "Get-ADGroup", memberRead);

        _h.Calls.Clear();
        _h.FailuresAfterSetPlace.Clear();
        _h.FailuresAfterSetPlace[ExchangeWriteHarness.MemberReadFailureKey] = Later;
        SetPlaceRecorded();
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Assert.Single(Run(ps, "Get-ADGroup", search));
            Assert.Single(Run(ps, "Get-ADGroup", reRead));
            _h.FailuresAfterSetPlace.Clear();
            _h.FailuresAfterSetPlace["Get-ADGroup -Identity"] = Later;
            Assert.Single(Run(ps, "Get-ADGroup", search));
        }
    }

    // ---- aud-a2t: a refusal the resolver or the membership read returns is logged, once, where the path refuses ----
    // Each on-prem path took a refusal its resolver or its membership read returned - no address, no catalog, no group or
    // several, a group answering as another object - into the row and logged nothing: only the row's audit carried it, so
    // the application log held no trace of why the row was refused. Only a throw was logged, by the step catch (aud-a2s).

    private const string AnotherObject = "77777777-7777-7777-7777-777777777777";

    // The refusals the stand-ins can produce, each set up on the directory - or, for the address, on what Exchange reports
    // for the list. Not among them: the resolver's no-ObjectGUID refusal and the three error-record refusals - a stand-in
    // group always has an objectGUID, and the stand-ins throw their errors under -ErrorAction Stop. The line that logs the
    // refusals below logs those as well: it logs whatever refusal its helper returns.
    private void SetUpRefusal(string refusal)
    {
        switch (refusal)
        {
            case "no address": _h.ListAddresses[List] = null; break;
            case "no catalog": _h.GlobalCatalogs.Clear(); break;
            case "no group": _h.DirectoryGroupGuids.Clear(); break;
            case "two groups": _h.DirectoryGroupGuids.Add("88888888-8888-8888-8888-888888888888"); break;
            case "a group DN naming no domain": _h.DirectoryGroupDomains[ExchangeWriteHarness.DefaultDirectoryGroupGuid] = "OU=Nowhere"; break;
            case "the group's own domain answering as another object": _h.DirectoryGroupReadBack[GroupDn] = AnotherObject; break;
            case "the room read with no DN": ((Hashtable)_h.DirectoryObjects[CheckedObject]!).Remove("DistinguishedName"); break;
            case "the membership read answering as another object": _h.DirectoryGroupMemberReadBack[GroupDn] = AnotherObject; break;
            default: throw new ArgumentOutOfRangeException(nameof(refusal), refusal, null);
        }
    }

    // Words of each refusal's own, so a case is known to have met the refusal it names and not another.
    private static string RefusalWords(string refusal) => refusal switch
    {
        "no address" => "Exchange reported no primary SMTP address for room list",
        "no catalog" => "The forest's global catalog could not be resolved",
        "no group" => "No on-prem AD group in the forest has mail",
        "two groups" => "Multiple on-prem AD groups (2) in the forest have mail",
        "a group DN naming no domain" => "has no distinguished name that names its domain",
        "the group's own domain answering as another object" => "is not what its own domain",
        "the room read with no DN" => "The room's AD object was read without a distinguished name",
        "the membership read answering as another object" => "did not return its group",
        _ => throw new ArgumentOutOfRangeException(nameof(refusal), refusal, null),
    };

    [Theory]
    [InlineData("no address")]
    [InlineData("no catalog")]
    [InlineData("no group")]
    [InlineData("two groups")]
    [InlineData("a group DN naming no domain")]
    [InlineData("the group's own domain answering as another object")]
    [InlineData("the room read with no DN")]
    [InlineData("the membership read answering as another object")]
    public async Task ARefusalOfTheRoomListPreflight_IsLoggedOnce_NamingTheRoomTheListAndTheReason(string refusal)
    {
        // Each refusal in turn, before Set-Place, through the whole row: refused while nothing is written, as before - and
        // logged, in one line: a warning naming the room, the list and the refusal as the row reports it. Nothing else logs
        // it: not the row, which puts it on its result.
        SyncedListTheRoomIsNotOnInExchange();
        SetUpRefusal(refusal);
        var logger = new CapturingLogger();

        var result = await Apply(new Service(_h, logger), Room());

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Empty(_h.Writes);
        Assert.Equal("Room list preflight", Assert.Single(result.Steps).Stage);
        const string NothingWritten = " Nothing was written.";
        Assert.EndsWith(NothingWritten, result.Message, StringComparison.Ordinal);
        var reason = result.Message[..^NothingWritten.Length];
        Assert.Contains(RefusalWords(refusal), reason, StringComparison.Ordinal);
        var logged = Assert.Single(logger.Entries, e => e.Message.Contains(reason, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Equal($"On-prem room-list preflight for {Typed} on {List} refused the row; nothing was written: {reason}", logged.Message);
    }

    [Theory]
    [InlineData("no address")]
    [InlineData("no catalog")]
    [InlineData("no group")]
    [InlineData("two groups")]
    [InlineData("a group DN naming no domain")]
    [InlineData("the group's own domain answering as another object")]
    [InlineData("the room read with no DN")]
    [InlineData("the membership read answering as another object")]
    public async Task ARefusalOfTheOnPremRoomListAdd_IsLoggedOnce_NamingTheRoomTheListAndTheReason(string refusal)
    {
        // The same refusals met by the add's own resolver and membership read: the add called directly, as the row calls it
        // after Set-Place, since through the row the preflight would meet each first. The add fails, its step carrying the
        // refusal, nothing written on-prem - and one warning names the room, the list and the refusal.
        SyncedListTheRoomIsNotOnInExchange();
        SetUpRefusal(refusal);
        var logger = new CapturingLogger();
        var list = refusal == "no address" ? TheSyncedList() with { PrimarySmtpAddress = null } : TheSyncedList();

        var result = await new Service(_h, logger).AddToRoomListAsync(Room(), Typed, Typed, list);

        Assert.False(result.Success);
        Assert.Null(result.WrittenDirectoryObject);
        var step = Assert.Single(result.Steps, s => s.Stage == $"Add to '{List}' (on-prem)");
        Assert.False(step.Success);
        var reason = step.Error;
        Assert.NotNull(reason);
        Assert.Equal(reason, result.Message);
        Assert.Contains(RefusalWords(refusal), reason, StringComparison.Ordinal);
        Assert.Empty(_h.CallsTo("Set-ADGroup"));
        var logged = Assert.Single(logger.Entries, e => e.Message.Contains(reason, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Equal($"On-prem room-list add for {Typed} on {List} refused before its write: {reason}", logged.Message);
    }

    [Fact]
    public async Task ARefusalOfTheOnPremRoomListAdd_InTheWholeRow_IsLoggedOnce()
    {
        // The add's refusal reached through the row: the catalog fails only after Set-Place, so the preflight passes and the
        // add's resolver refuses. The row is the partial it was, carrying the refusal, and one warning names it: the row,
        // which wraps it in its partial message, does not log it again.
        SyncedListTheRoomIsNotOnInExchange();
        _h.FailuresAfterSetPlace["Get-ADForest"] = Injected;
        var logger = new CapturingLogger();

        var result = await Apply(new Service(_h, logger), Room());

        Assert.False(result.Success);
        Assert.True(result.Partial);
        var reason = Assert.Single(result.Steps, s => s.Stage == $"Add to '{List}' (on-prem)").Error;
        Assert.NotNull(reason);
        Assert.StartsWith($"The forest's global catalog could not be resolved ({Injected})", reason, StringComparison.Ordinal);
        Assert.Contains(reason, result.Message, StringComparison.Ordinal);
        var logged = Assert.Single(logger.Entries, e => e.Message.Contains(reason, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Equal($"On-prem room-list add for {Typed} on {List} refused before its write: {reason}", logged.Message);
    }
}
