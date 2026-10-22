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

    // ---- aud-a2p: the City/State/Country write is done when its attributes read back as written ------------
    // Set-ADUser raising no error was taken as the write done: a domain controller that dropped the values, or
    // a constraint it did not report, raises none and changes nothing, and the row was reported, and audited,
    // as written (Known Failure Class 2). The stand-in Set-ADUser applies its -Replace to the room's object, so
    // the read-back sees what a write changed - and, told to, changes nothing.

    // The domain the room's DN names (CN=Room 1,OU=Rooms,DC=contoso,DC=test): where the write and its read-back go.
    private const string RoomDomain = "contoso.test";

    private System.Collections.Hashtable RoomObject => (System.Collections.Hashtable)_h.DirectoryObjects[CheckedObject]!;

    private static string? Text(object? value) => (value is PSObject wrapped ? wrapped.BaseObject : value)?.ToString();

    // The Set-ADUser and the first Get-ADUser after it - the read-back.
    private (System.Collections.Hashtable Write, System.Collections.Hashtable ReadBack) TheWriteAndItsReadBack()
    {
        var calls = _h.Calls.Cast<System.Collections.Hashtable>().ToList();
        var write = calls.FindIndex(c => (string)c["__Command"]! == "Set-ADUser");
        Assert.True(write >= 0, "no Set-ADUser");
        var readBack = calls.FindIndex(write + 1, c => (string)c["__Command"]! == "Get-ADUser");
        Assert.True(readBack > write, "no Get-ADUser after the Set-ADUser");
        return (calls[write], calls[readBack]);
    }

    [Fact]
    public async Task TheCityStateAndCountryWrite_IsConfirmedByReadingItsAttributesBack_FromTheServerItWroteTo()
    {
        // The normal case: the write applied, then l, st and co read back by the objectGUID written, from the
        // domain the write went to - the room object's own, named by its DN - so the row succeeds and the object
        // is reported written.
        var result = await Finder(new Service(_h), Room(), city: "Dublin", state: "Leinster", country: "IE");

        Assert.True(result.Success, result.Message);
        Assert.False(result.Partial);
        Assert.Equal(CheckedObject, result.WrittenDirectoryObject);
        Assert.Equal(["Dublin", "Leinster", "Ireland"], new[] { "l", "st", "co" }.Select(a => Text(RoomObject[a])));
        var (write, readBack) = TheWriteAndItsReadBack();
        Assert.Equal(CheckedObject, ExchangeWriteHarness.Bound(readBack, "Identity"));
        Assert.Equal(["l", "st", "co"],
            Assert.IsAssignableFrom<System.Collections.IEnumerable>(ExchangeWriteHarness.Bound(readBack, "Properties")).Cast<object?>().Select(Text));
        Assert.Equal(RoomDomain, ExchangeWriteHarness.Bound(write, "Server"));
        Assert.Equal(RoomDomain, ExchangeWriteHarness.Bound(readBack, "Server"));
    }

    [Fact]
    public async Task ACityStateAndCountryWriteThatChangesNothing_IsAFailedStep_NotAWrite()
    {
        // The finding: Set-ADUser raises nothing and changes nothing, an older City in place. The read-back does
        // not show the write, so the step fails - partial, Set-Place having committed - nothing is reported
        // written, nothing after it runs, and the step names each attribute, what it reads and what was written,
        // and the server read.
        RoomObject["l"] = "Cork";
        _h.SetAdUserChangesNothing = true;

        var result = await Finder(new Service(_h), Room(), city: "Dublin", state: "Leinster", country: "IE");

        Assert.False(result.Success);
        Assert.True(result.Partial);
        Assert.Null(result.WrittenDirectoryObject);
        Assert.Single(_h.CallsTo("Set-ADUser"));
        var step = Assert.Single(result.Steps, s => s.Stage == "Set-ADUser (City/State/Country)");
        Assert.False(step.Success);
        Assert.NotNull(step.Error);
        Assert.Contains("was not confirmed", step.Error, StringComparison.Ordinal);
        Assert.Contains(CheckedObject, step.Error, StringComparison.Ordinal);
        Assert.Contains($"read back from {RoomDomain}", step.Error, StringComparison.Ordinal);
        Assert.Contains("l (City) is 'Cork', not 'Dublin' as written", step.Error, StringComparison.Ordinal);
        Assert.Contains("st (State) is not set, not 'Leinster' as written", step.Error, StringComparison.Ordinal);
        Assert.Contains("co (Country) is not set, not 'Ireland' as written", step.Error, StringComparison.Ordinal);
        Assert.Contains(step.Error, result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.CallsTo("Set-MailboxRegionalConfiguration"));
    }

    [Fact]
    public async Task ACityStateAndCountryReadBackThatFails_IsAFailedStep_NotAWrite()
    {
        // The write applied, but its read-back cannot be made: unconfirmed is not done. Failed, partial, nothing
        // reported written, the message naming the server read and the read's own error.
        _h.AttributeReadBackFailure = "The server is not operational.";

        var result = await Finder(new Service(_h), Room(), city: "Dublin");

        Assert.False(result.Success);
        Assert.True(result.Partial);
        Assert.Null(result.WrittenDirectoryObject);
        Assert.Equal("Dublin", Text(RoomObject["l"]));
        Assert.Contains("could not be confirmed", result.Message, StringComparison.Ordinal);
        Assert.Contains($"back from {RoomDomain} failed", result.Message, StringComparison.Ordinal);
        Assert.Contains("The server is not operational.", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACityStateAndCountryReadBackAnsweredByAnotherObject_IsAFailedStep_NotAWrite()
    {
        // Another object answering the read-back says nothing about the object written: failed, partial, nothing
        // reported written, the object that answered named.
        const string Another = "7a7a7a7a-0000-4000-8000-000000000002";
        _h.AttributeReadBackAnswersAs = Another;

        var result = await Finder(new Service(_h), Room(), city: "Dublin");

        Assert.False(result.Success);
        Assert.True(result.Partial);
        Assert.Null(result.WrittenDirectoryObject);
        Assert.Contains("could not be confirmed", result.Message, StringComparison.Ordinal);
        Assert.Contains(Another, result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnErrorTheCityStateAndCountryWriteReportsWithoutThrowing_IsNotTakenForItsError_TheReadBackDecides(bool applied)
    {
        // aud-a2o's knob on this write. Under -ErrorAction Stop an error a cmdlet writes is thrown, so one reported
        // without a throw - the stand-in's own -ErrorAction Continue - is not Set-ADUser's, and the HadErrors check
        // that took it is gone. The read-back decides, the record the write left cleared before it so the read is
        // not failed by it: applied - success, the object written; not applied - the not-confirmed failure. In
        // neither is the reported text taken for the write's error.
        const string Reported = "A non-terminating error the write reported and carried on past.";
        _h.SetAdUserReportsErrorWithoutThrowing = Reported;
        _h.SetAdUserChangesNothing = !applied;

        var result = await Finder(new Service(_h), Room(), city: "Dublin");

        Assert.Single(_h.CallsTo("Set-ADUser"));
        Assert.Equal(applied, result.Success);
        Assert.Equal(!applied, result.Partial);
        Assert.Equal(applied ? CheckedObject : null, result.WrittenDirectoryObject);
        Assert.DoesNotContain(Reported, result.Message, StringComparison.Ordinal);
        if (!applied)
            Assert.Contains("was not confirmed", result.Message, StringComparison.Ordinal);
    }

    private static PSObject ReadAnswer(params (string Name, object? Value)[] properties)
    {
        var read = new PSObject();
        foreach (var (name, value) in properties)
            read.Properties.Add(new PSNoteProperty(name, value));
        return read;
    }

    private static List<string> Unconfirmed(Dictionary<string, object> written, PSObject read) =>
        ConferenceRoomService.UnconfirmedSyncedAttributes(written, read);

    [Fact]
    public void TheReadBack_ComparesWhatWasWritten_Ordinal_AnEmptyWriteAsAnAbsentAttribute()
    {
        var written = new Dictionary<string, object> { ["l"] = "Dublin", ["st"] = "Leinster", ["co"] = "Ireland", ["c"] = "IE", ["countryCode"] = 372 };

        // As written: confirmed. c and countryCode, in the same -Replace as co, are not read.
        Assert.Empty(Unconfirmed(written, ReadAnswer(("l", "Dublin"), ("st", "Leinster"), ("co", "Ireland"))));
        // Ordinal: a case difference is a difference, and nothing read is trimmed.
        Assert.Equal(["l (City) is 'dublin', not 'Dublin' as written"],
            Unconfirmed(written, ReadAnswer(("l", "dublin"), ("st", "Leinster"), ("co", "Ireland"))));
        Assert.Equal(["l (City) is 'Dublin ', not 'Dublin' as written"],
            Unconfirmed(written, ReadAnswer(("l", "Dublin "), ("st", "Leinster"), ("co", "Ireland"))));
        // A written value absent - no property, or null - is not confirmed.
        Assert.Equal(["st (State) is not set, not 'Leinster' as written", "co (Country) is not set, not 'Ireland' as written"],
            Unconfirmed(written, ReadAnswer(("l", "Dublin"), ("co", null))));
        // An attribute the row did not write is not compared, whatever the object holds.
        Assert.Empty(Unconfirmed(new Dictionary<string, object> { ["l"] = "Dublin" }, ReadAnswer(("l", "Dublin"), ("st", "Munster"))));
        // An empty write compares to an absent attribute, and to an empty one; not to a value.
        Assert.Empty(Unconfirmed(new Dictionary<string, object> { ["l"] = "" }, ReadAnswer()));
        Assert.Empty(Unconfirmed(new Dictionary<string, object> { ["l"] = "" }, ReadAnswer(("l", ""))));
        Assert.Equal(["l (City) is 'Cork', not empty as written"], Unconfirmed(new Dictionary<string, object> { ["l"] = "" }, ReadAnswer(("l", "Cork"))));
        // A one-element collection is its element; two elements are not the one value written.
        Assert.Empty(Unconfirmed(new Dictionary<string, object> { ["co"] = "Ireland" }, ReadAnswer(("co", new System.Collections.ArrayList { "Ireland" }))));
        Assert.Single(Unconfirmed(new Dictionary<string, object> { ["co"] = "Ireland" }, ReadAnswer(("co", new System.Collections.ArrayList { "Ireland", "Ireland" }))));
    }

    [Fact]
    public void TheHarness_SetADUser_AppliesItsReplace_AndCanChangeNothing_ReportAnError_OrFailItsReadBack()
    {
        // The stand-ins the tests above rest on, or they would pass for the wrong reason: Set-ADUser applies its
        // -Replace to the object; a failure throws before applying; told to change nothing, it changes nothing;
        // told to report an error without throwing, it applies and reports one that -ErrorAction Stop does not
        // throw. Get-ADUser, told the read-back fails, fails a read asking for -Properties made after a Set-ADUser
        // and not a read before it or one without -Properties; told to answer as another object, the read-back
        // answers as it and the read without -Properties does not.
        const string Another = "7a7a7a7a-0000-4000-8000-000000000002";
        var replace = new System.Collections.Hashtable { ["l"] = "Dublin" };
        (string Name, object Value)[] write = [("Identity", CheckedObject), ("Replace", replace), ("Server", RoomDomain), ("ErrorAction", "Stop")];
        (string Name, object Value)[] readBack = [("Identity", CheckedObject), ("Properties", new[] { "l", "st", "co" }), ("Server", RoomDomain), ("ErrorAction", "Stop")];
        (string Name, object Value)[] confirm = [("Identity", CheckedObject), ("ErrorAction", "Stop")];

        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Run(ps, "Set-ADUser", write);
            Assert.Equal("Dublin", Text(RoomObject["l"]));
            Assert.Equal("Dublin", Text(Value(Run(ps, "Get-ADUser", readBack).Single(), "l")));
        }

        RoomObject.Remove("l");
        _h.Failures["Set-ADUser"] = "Insufficient access rights to perform the operation";
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Assert.ThrowsAny<RuntimeException>(() => Run(ps, "Set-ADUser", write));
            Assert.False(RoomObject.ContainsKey("l"));
        }

        _h.Failures.Remove("Set-ADUser");
        _h.SetAdUserChangesNothing = true;
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Run(ps, "Set-ADUser", write);
            Assert.False(RoomObject.ContainsKey("l"));
        }

        _h.SetAdUserChangesNothing = false;
        _h.SetAdUserReportsErrorWithoutThrowing = "Reported, not thrown.";
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Run(ps, "Set-ADUser", write);
            Assert.True(ps.HadErrors);
            Assert.Equal("Reported, not thrown.", Assert.Single(ps.Streams.Error).Exception.Message);
            Assert.Equal("Dublin", Text(RoomObject["l"]));
        }

        _h.Calls.Clear();
        _h.SetAdUserReportsErrorWithoutThrowing = null;
        _h.AttributeReadBackFailure = "The server is not operational.";
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Assert.Single(Run(ps, "Get-ADUser", readBack));
            Run(ps, "Set-ADUser", write);
            Assert.Single(Run(ps, "Get-ADUser", confirm));
            Assert.Contains("not operational", Assert.ThrowsAny<RuntimeException>(() => Run(ps, "Get-ADUser", readBack)).Message, StringComparison.Ordinal);
        }

        _h.AttributeReadBackFailure = null;
        _h.AttributeReadBackAnswersAs = Another;
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Assert.Equal(Guid.Parse(Another), Value(Run(ps, "Get-ADUser", readBack).Single(), "ObjectGUID"));
            Assert.Equal(Guid.Parse(CheckedObject), Value(Run(ps, "Get-ADUser", confirm).Single(), "ObjectGUID"));
        }
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

        // Since aud-a2j the add writes the group's member attribute, so the member is a DN: the DN of the
        // object read by the proven objectGUID - every Get-ADUser asked for that GUID and nothing else.
        Assert.True(result.Success, result.Message);
        Assert.Equal("CN=Room 1,OU=Rooms,DC=contoso,DC=test", ExchangeWriteHarness.AddedMember(Assert.Single(_h.CallsTo("Set-ADGroup"))));
        Assert.All(_h.CallsTo("Get-ADUser"), c => Assert.Equal(CheckedObject, ExchangeWriteHarness.Bound(c, "Identity")));
    }

    // aud-a2m. The room list itself, bound the same way: every Exchange call on it after the read names it by
    // the Guid of the object Get-DistributionGroup returned, never by the name the Building derives.
    private const string ListGuid = "42424242-4242-4242-8242-424242424242";
    private const string NamesakeGuid = "43434343-4343-4343-8343-434343434343";
    private static readonly string List = ConferenceRoomService.BuildRoomListName("Building 1");

    private const string OnPremMastered =
        "The operation on the group failed because it's out of the current user's write scope. The action can't be "
        + "performed on the object because the object is being synchronized from your on-premises organization.";

    private IReadOnlyList<System.Collections.Hashtable> ListCalls() =>
        _h.CallsTo("Get-DistributionGroupMember").Concat(_h.CallsTo("Add-DistributionGroupMember")).ToList();

    private bool AnyCallNames(string value) =>
        _h.Calls.Cast<System.Collections.Hashtable>().Any(c => c.Values.Cast<object?>()
            .Any(v => string.Equals((v is PSObject wrapped ? wrapped.BaseObject : v)?.ToString(), value, StringComparison.OrdinalIgnoreCase)));

    [Fact]
    public async Task TheCloudAddAndItsMembershipRead_NameTheListByTheGuidItWasReadWith()
    {
        // By the time of the add a second recipient also answers to the list's name. Named by that name,
        // Exchange refuses the identity as ambiguous and the row is partial after Set-Place; named by the
        // Guid the read returned, the row completes, and the namesake is never addressed.
        _h.ListGuids[List] = ListGuid;
        _h.ListNamesakes[List] = NamesakeGuid;

        var result = await Finder(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        Assert.False(result.Partial);
        Assert.Equal(List, ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Get-DistributionGroup")), "Identity"));
        var calls = ListCalls();
        Assert.Equal(["Get-DistributionGroupMember", "Add-DistributionGroupMember"], calls.Select(c => (string)c["__Command"]!));
        Assert.All(calls, c => Assert.Equal(ListGuid, ExchangeWriteHarness.Bound(c, "Identity")));
        Assert.False(AnyCallNames(NamesakeGuid));
    }

    [Fact]
    public async Task ASyncedList_IsReadAndRefusedTheCloudAddByItsGuid_ThenAddedOnPrem()
    {
        // A synced list makes three Exchange calls on it: the preflight's membership read, the add's, and the
        // cloud add Exchange refuses as on-prem-mastered. Each names the Guid. (The refusal is thrown before
        // the stand-in looks at the identity, so here only the pins show a call made by name.)
        _h.ListGuids[List] = ListGuid;
        _h.ListNamesakes[List] = NamesakeGuid;
        _h.ListSyncState[List] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;

        var result = await Finder(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        var calls = ListCalls();
        Assert.Equal(["Get-DistributionGroupMember", "Get-DistributionGroupMember", "Add-DistributionGroupMember"],
            calls.Select(c => (string)c["__Command"]!));
        Assert.All(calls, c => Assert.Equal(ListGuid, ExchangeWriteHarness.Bound(c, "Identity")));
        Assert.Single(_h.CallsTo("Set-ADGroup"));
        Assert.False(AnyCallNames(NamesakeGuid));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-guid")]
    public async Task ARoomListExchangeReportsWithNoGuid_IsRefusedBeforeAnyWrite(string? guid)
    {
        // The property absent, all zeros, or not a GUID: nothing immutable to name the list by, so the row is
        // refused before Set-Place - a cloud list, which otherwise needs no preflight at all - and the list is
        // never read or added to by its name.
        _h.ListGuids[List] = guid;

        var result = await Finder(new Service(_h), Room());

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Contains(List, result.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was written.", result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
        Assert.Empty(ListCalls());
    }

    // A Building with no room list under either name: the row creates one cloud-side.
    private void NoListYet()
    {
        _h.MissingLists.Add(List);
        _h.MissingLists.Add(ConferenceRoomService.BuildLegacyRoomListName("Building 1"));
    }

    [Fact]
    public async Task AListTheRowCreates_IsNamedByTheGuidTheCreationReturned()
    {
        // No list was read, so the Guid is the one New-DistributionGroup returned for the object it made - with
        // a namesake present, the name would make the add ambiguous.
        NoListYet();
        _h.ListNamesakes[List] = NamesakeGuid;

        var result = await Finder(new Service(_h), Room());

        Assert.True(result.Success, result.Message);
        Assert.Equal(List, ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("New-DistributionGroup")), "Name"));
        var calls = ListCalls();
        Assert.Equal(["Get-DistributionGroupMember", "Add-DistributionGroupMember"], calls.Select(c => (string)c["__Command"]!));
        Assert.All(calls, c => Assert.Equal(ExchangeWriteHarness.DefaultCreatedListGuid, ExchangeWriteHarness.Bound(c, "Identity")));
    }

    [Fact]
    public async Task AListCreatedWithoutAGuid_IsNotAddedToByItsName()
    {
        // The creation returned no Guid: the row fails, partial, and nothing is read or added by the name.
        NoListYet();
        _h.CreatedListGuid = null;

        var result = await Finder(new Service(_h), Room());

        Assert.False(result.Success);
        Assert.True(result.Partial);
        Assert.Single(_h.CallsTo("New-DistributionGroup"));
        Assert.Empty(ListCalls());
    }

    [Fact]
    public void TheHarness_RefusesAListNameASecondRecipientAnswersTo_AndAnswersTheListsGuid()
    {
        // The stand-ins the tests above rest on: Get-DistributionGroup reports a Guid (the test's, else the
        // default) and finds no missing list; the member cmdlets refuse a name with a namesake and answer the
        // list's Guid; New-DistributionGroup returns the group it made.
        var legacy = ConferenceRoomService.BuildLegacyRoomListName("Building 1");
        _h.ListGuids[List] = ListGuid;
        _h.MissingLists.Add(legacy);
        _h.ListNamesakes[List] = NamesakeGuid;
        _h.ListMembers.Add("room1@contoso.test");
        using var rs = _h.OpenExchangeRunspace();
        using var ps = PowerShell.Create();
        ps.Runspace = rs;

        Assert.Equal(Guid.Parse(ListGuid), Value(Run(ps, "Get-DistributionGroup", ("Identity", List)).Single(), "Guid"));
        Assert.Equal(Guid.Parse(ExchangeWriteHarness.DefaultListGuid), Value(Run(ps, "Get-DistributionGroup", ("Identity", "Building 2 Conference Rooms")).Single(), "Guid"));
        Assert.Empty(Run(ps, "Get-DistributionGroup", ("Identity", legacy), ("ErrorAction", "SilentlyContinue")));
        Assert.Contains("matches multiple entries", Assert.ThrowsAny<RuntimeException>(() =>
            Run(ps, "Get-DistributionGroupMember", ("Identity", List), ("ResultSize", "Unlimited"), ("ErrorAction", "Stop"))).Message, StringComparison.Ordinal);
        Assert.Contains("matches multiple entries", Assert.ThrowsAny<RuntimeException>(() =>
            Run(ps, "Add-DistributionGroupMember", ("Identity", List), ("Member", Identity(Checked)), ("ErrorAction", "Stop"))).Message, StringComparison.Ordinal);
        Assert.Equal("room1@contoso.test",
            Value(Run(ps, "Get-DistributionGroupMember", ("Identity", ListGuid), ("ResultSize", "Unlimited"), ("ErrorAction", "Stop")).Single(), "PrimarySmtpAddress"));
        Run(ps, "Add-DistributionGroupMember", ("Identity", ListGuid), ("Member", Identity(Checked)), ("ErrorAction", "Stop"));
        Assert.Equal(Guid.Parse(ExchangeWriteHarness.DefaultCreatedListGuid), Value(Run(ps, "New-DistributionGroup", ("Name", List), ("ErrorAction", "Stop")).Single(), "Guid"));
    }

    private static object? Value(PSObject item, string property) =>
        item.Properties[property]?.Value is PSObject wrapped ? wrapped.BaseObject : item.Properties[property]?.Value;

    private static System.Collections.ObjectModel.Collection<PSObject> Run(PowerShell ps, string command, params (string Name, object Value)[] parameters)
    {
        ps.Commands.Clear();
        ps.Streams.Error.Clear();
        var invocation = ps.AddCommand(command);
        foreach (var (name, value) in parameters)
            invocation.AddParameter(name, value);
        return ps.Invoke();
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
