using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Logging;
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
        // aud-a2t. aud-a2s's seam (ConferenceRoomRoomListPreflightTests.Service), for the City/State/Country paths: for a
        // purpose containing this text, a credential with no password, so the step that builds the AD credential from it
        // throws - the way a test reaches that step. Live it is not reached so: the module's PAM provider returns no
        // credential at all for a secret without a password (DelineaService), which the paths refuse as not configured.
        public string? IncompleteCredentialFor;
        // aud-a2v. For a purpose containing this text, no credential at all. Since aud-a2x the base reads it as no secret ID
        // set, the one reason a lookup through this seam can give: with no credential service the service has nothing else
        // to ask. A corrupt module config, or a PAM provider that fails, is CredentialFailureFor's.
        public string? NoCredentialFor;
        // aud-a2x. For a purpose containing this text, no credential, and CredentialFailure the reason the lookup gives - as the
        // module's credential service now says beside its none.
        public string? CredentialFailureFor;
        public ModuleCredentialFailure CredentialFailure = ModuleCredentialFailure.NotReturned;
        // aud-a2v. Each purpose the lookup was asked with, in order: the text a path's refusal line names beside the room.
        public List<string> CredentialPurposes { get; } = [];

        // aud-a2x. With a credential service, the base's lookup asks it, and neither seam below is reached.
        // aud-a2e. With the harness's protection services - nothing protected - as the container supplies them.
        public Service(ExchangeWriteHarness h, ILogger<ConferenceRoomService>? logger = null, ModuleCredentialService? credentials = null)
            : base(h.Pool, null!, h.ModuleConfig, credentials!, h.Trace, h.Config, logger ?? NullLogger<ConferenceRoomService>.Instance,
                h.ProtectedPrincipals, h.Servicers) => _h = h;

        protected override Task<ModuleCredentialLookup> LookupDirectoryCredentialAsync(string purpose)
        {
            if (CredentialFailureFor is { } failing && purpose.Contains(failing, StringComparison.Ordinal))
            {
                CredentialRequests++;
                CredentialPurposes.Add(purpose);
                return Task.FromResult(new ModuleCredentialLookup(null, CredentialFailure));
            }
            return base.LookupDirectoryCredentialAsync(purpose);
        }

        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose)
        {
            CredentialRequests++;
            CredentialPurposes.Add(purpose);
            if (NoCredentialFor is { } none && purpose.Contains(none, StringComparison.Ordinal))
                return Task.FromResult<(string username, string password, string domain)?>(null);
            if (IncompleteCredentialFor is { } incomplete && purpose.Contains(incomplete, StringComparison.Ordinal))
                return Task.FromResult<(string username, string password, string domain)?>(("svc", null!, "CONTOSO"));
            return Task.FromResult<(string username, string password, string domain)?>(("svc", "secret", "CONTOSO"));
        }

        protected override Runspace OpenDirectoryRunspace() => _h.OpenDirectoryRunspace();
    }

    private ValidatedMailbox Room(string? directoryObject = CheckedObject) => ExchangeWriteHarness.Validated(Checked, directoryObject);

    private Task<RoomOperationResult> Finder(Service service, ValidatedMailbox? room, string city = "", string state = "", string country = "")
        => service.SetRoomMetadataAndListAsync(Typed, room, actingUser: null, city, "Building 1", 8, "2", "Second", "TV", "Polycom", country, state, "GMT Standard Time");

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

    // ---- aud-a2q: the write goes only where its preflight proved it can, and every outcome is read back ----
    // The preflight read the room's object with no -Server; the write (aud-a2p) went to the domain its DN names. A
    // domain this host reaches through the default domain controller but not by its DNS name passed the preflight,
    // Set-Place committed, and the routed write failed: partial. And a Set-ADUser that committed and then threw was
    // the failure it threw, its values in place; the outer catch blamed Set-ADUser for every step around it.

    private const string TimedOut = "The operation was aborted because the client side timeout limit was exceeded.";

    // The real module's text for a server it cannot use, as near as it matters here.
    private const string Unreachable = "Unable to contact the server. This may be because this server does not exist, it is currently down, or it does not have the Active Directory Web Services running.";

    private sealed class CapturingLogger : ILogger<ConferenceRoomService>
    {
        // aud-a2w. The exception logged with an entry, too: the row's room read logs the throw it rethrows.
        public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception), exception));
    }

    // The calls recorded before the Set-Place - the preflight's - and after it - the write's.
    private (List<System.Collections.Hashtable> Before, List<System.Collections.Hashtable> After) AroundSetPlace()
    {
        var calls = _h.Calls.Cast<System.Collections.Hashtable>().ToList();
        var place = calls.FindIndex(c => (string)c["__Command"]! == "Set-Place");
        Assert.True(place >= 0, "no Set-Place");
        return (calls.Take(place).ToList(), calls.Skip(place + 1).ToList());
    }

    private static List<System.Collections.Hashtable> Named(IEnumerable<System.Collections.Hashtable> calls, string command) =>
        calls.Where(c => (string)c["__Command"]! == command).ToList();

    // aud-a2t. The text a failure injected at a step carries: the one given, or, for the credential, which has none to give,
    // the message of what building a credential with no password throws.
    private static string InjectedText(string failing, string injected) =>
        failing == "credential" ? new NullReferenceException().Message : injected;

    [Fact]
    public async Task TheCityStateAndCountryPreflight_RefusesBeforeAnyWrite_WhenTheObjectCannotBeReadThroughTheServerTheWriteUses()
    {
        // The folded-in scenario: the default domain answers a read with no -Server; its DNS name, the server the room's
        // DN names and the write goes to, cannot be used from here. Refused while nothing is written - no Set-Place - in
        // one step, naming the server, the room, its object and the error; the read with no -Server answered, the read
        // through the server failed.
        _h.DirectoryServerFailures[RoomDomain] = Unreachable;

        var result = await Finder(new Service(_h), Room(), city: "Dublin", state: "Leinster", country: "IE");

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Empty(_h.Writes);
        Assert.Null(result.WrittenDirectoryObject);
        var step = Assert.Single(result.Steps);
        Assert.Equal("Set-ADUser preflight (City/State/Country)", step.Stage);
        Assert.False(step.Success);
        Assert.Equal(result.Message, step.Error);
        Assert.Contains(RoomDomain, result.Message, StringComparison.Ordinal);
        Assert.Contains($"'{Typed}'", result.Message, StringComparison.Ordinal);
        Assert.Contains(CheckedObject, result.Message, StringComparison.Ordinal);
        Assert.Contains(Unreachable, result.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was written.", result.Message, StringComparison.Ordinal);
        Assert.False(RoomObject.ContainsKey("l"));
        Assert.Equal(new object?[] { null, RoomDomain }, _h.CallsTo("Get-ADUser").Select(c => ExchangeWriteHarness.Bound(c, "Server")));
    }

    [Fact]
    public async Task TheCityStateAndCountryPreflight_ReadsTheObjectThroughTheServerTheWriteUses_AndTheWriteGoesThere()
    {
        // The normal case: before Set-Place the object is read with no -Server - which finds it and its DN - and then by
        // its objectGUID through the domain that DN names; after Set-Place the write and its read-back go there.
        var result = await Finder(new Service(_h), Room(), city: "Dublin", state: "Leinster", country: "IE");

        Assert.True(result.Success, result.Message);
        Assert.Equal(CheckedObject, result.WrittenDirectoryObject);
        var (before, after) = AroundSetPlace();
        var preflight = Named(before, "Get-ADUser");
        Assert.Equal(2, preflight.Count);
        Assert.All(preflight, c =>
        {
            Assert.Equal(CheckedObject, ExchangeWriteHarness.Bound(c, "Identity"));
            Assert.Null(ExchangeWriteHarness.Bound(c, "Properties"));
        });
        Assert.Null(ExchangeWriteHarness.Bound(preflight[0], "Server"));
        Assert.Equal(RoomDomain, ExchangeWriteHarness.Bound(preflight[1], "Server"));
        Assert.Empty(Named(before, "Set-ADUser"));
        Assert.Equal(RoomDomain, ExchangeWriteHarness.Bound(Assert.Single(Named(after, "Set-ADUser")), "Server"));
        Assert.Equal(RoomDomain, ExchangeWriteHarness.Bound(TheWriteAndItsReadBack().ReadBack, "Server"));
    }

    [Fact]
    public async Task TheWrite_GoesToTheServerThePreflightProved_NotOneItDerivesAgain()
    {
        // After Set-Place the object answers with a DN in the forest's other domain. A write that derived its server
        // from its own read would go to fabrikam.test, which nothing proved; it goes to the server the preflight read
        // the object through, and so does its read-back.
        _h.ObjectDnAfterSetPlace = "CN=Room 1,OU=Rooms," + ExchangeWriteHarness.OtherDirectoryDomainDn;

        var result = await Finder(new Service(_h), Room(), city: "Dublin");

        Assert.True(result.Success, result.Message);
        var (write, readBack) = TheWriteAndItsReadBack();
        Assert.Equal(RoomDomain, ExchangeWriteHarness.Bound(write, "Server"));
        Assert.Equal(RoomDomain, ExchangeWriteHarness.Bound(readBack, "Server"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("CN=Room 1,OU=Rooms")]
    public async Task ARoomObjectWhoseDnNamesNoDomain_IsWrittenOnTheRouteThePreflightRead(string? dn)
    {
        // The explicit fallback: no DN, or one without domain components, names no server, so the write and the
        // read-back go to the default domain controller - the route the preflight's one read took - and a warning says so.
        if (dn is null)
            RoomObject.Remove("DistinguishedName");
        else
            RoomObject["DistinguishedName"] = dn;
        var logger = new CapturingLogger();

        var result = await Finder(new Service(_h, logger), Room(), city: "Dublin");

        Assert.True(result.Success, result.Message);
        var (before, _) = AroundSetPlace();
        Assert.Null(ExchangeWriteHarness.Bound(Assert.Single(Named(before, "Get-ADUser")), "Server"));
        var (write, readBack) = TheWriteAndItsReadBack();
        Assert.Null(ExchangeWriteHarness.Bound(write, "Server"));
        Assert.Null(ExchangeWriteHarness.Bound(readBack, "Server"));
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("default domain controller", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("runspace")]
    [InlineData("import")]
    [InlineData("credential")]
    [InlineData("object read")]
    [InlineData("write")]
    [InlineData("read-back")]
    public async Task AFailureAtEachStepOfTheCityStateAndCountryWrite_NamesThatStep(string failing)
    {
        // Each step of the write fails in turn - after Set-Place, so the preflight's own steps pass. Every one is a failed,
        // partial row with nothing written, and its message and its log line name the step that failed. The four before
        // the write say the attributes were not written and are not told in the write's words; the write keeps its own.
        // aud-a2t: the credential through the seam, for the write's purpose only - a step no test reached before, and no
        // failure reaches live.
        const string Injected = "The injected failure.";
        var logger = new CapturingLogger();
        var service = new Service(_h, logger);
        switch (failing)
        {
            case "runspace": _h.DirectoryRunspaceFailureAfterSetPlace = Injected; break;
            case "import": _h.ImportModuleFailureAfterSetPlace = Injected; break;
            case "credential": service.IncompleteCredentialFor = "Set-ADUser synced attributes"; break;
            case "object read": _h.ObjectReadFailureAfterSetPlace = Injected; break;
            case "write": _h.Failures["Set-ADUser"] = Injected; break;
            default: _h.AttributeReadBackFailure = Injected; break;
        }

        var result = await Finder(service, Room(), city: "Dublin");

        Assert.False(result.Success);
        Assert.True(result.Partial);
        Assert.Null(result.WrittenDirectoryObject);
        Assert.Null(result.DirectoryAttributesConfirmed);
        var step = Assert.Single(result.Steps, s => s.Stage == "Set-ADUser (City/State/Country)");
        Assert.False(step.Success);
        Assert.NotNull(step.Error);
        Assert.Contains(step.Error, result.Message, StringComparison.Ordinal);
        Assert.Contains(InjectedText(failing, Injected), step.Error, StringComparison.Ordinal);
        var (message, logged) = failing switch
        {
            "runspace" => ("opening the directory runspace failed: ", "opening the directory runspace failed"),
            "import" => ("importing the ActiveDirectory module failed: ", "importing the ActiveDirectory module failed"),
            "credential" => ("preparing the module's AD credential failed: ", "preparing the module's AD credential failed"),
            "object read" => ("reading the AD object failed: ", "reading the AD object failed"),
            "write" => ("Failed to write City/State/Country on the AD object: ", "Set-ADUser City/State/Country write failed"),
            _ => ($"reading l, st, co back from {RoomDomain} failed: ", $"could not be confirmed from {RoomDomain}"),
        };
        Assert.Contains(message, step.Error, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, e => e.Level >= LogLevel.Warning && e.Message.Contains(logged, StringComparison.Ordinal));
        if (failing is "runspace" or "import" or "credential" or "object read")
        {
            Assert.StartsWith($"City/State/Country were not written on the AD object {CheckedObject}: ", step.Error, StringComparison.Ordinal);
            Assert.DoesNotContain("Failed to write", step.Error, StringComparison.Ordinal);
            Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("Set-ADUser", StringComparison.Ordinal));
            Assert.Empty(_h.CallsTo("Set-ADUser"));
        }
        else
        {
            Assert.Single(_h.CallsTo("Set-ADUser"));
        }
        Assert.Empty(_h.CallsTo("Set-MailboxRegionalConfiguration"));
    }

    [Fact]
    public async Task ASetADUserThatAppliedThenFailed_IsASuccess_WithTheErrorInTheDetail_NothingRecordedAsWritten()
    {
        // The write committed and then threw - a timeout after the server took it. The read-back finds the values, so the
        // row succeeds; no object is recorded as written, the write having failed, and the detail says why with the error.
        _h.SetAdUserFailsAfterApplying = TimedOut;

        var result = await Finder(new Service(_h), Room(), city: "Dublin", state: "Leinster", country: "IE");

        Assert.True(result.Success, result.Message);
        Assert.False(result.Partial);
        Assert.Null(result.WrittenDirectoryObject);
        var detail = result.DirectoryAttributesConfirmed;
        Assert.NotNull(detail);
        Assert.StartsWith(ConferenceRoomService.AttributesConfirmedAfterWriteError, detail, StringComparison.Ordinal);
        Assert.Contains(TimedOut, detail, StringComparison.Ordinal);
        Assert.Contains(CheckedObject, detail, StringComparison.Ordinal);
        Assert.Contains($"read back from {RoomDomain}", detail, StringComparison.Ordinal);
        var step = Assert.Single(result.Steps, s => s.Stage == "Set-ADUser (City/State/Country)");
        Assert.True(step.Success);
        Assert.Equal(detail, step.Error);
        TheWriteAndItsReadBack();
        Assert.Equal(["Dublin", "Leinster", "Ireland"], new[] { "l", "st", "co" }.Select(a => Text(RoomObject[a])));
        Assert.DoesNotContain("were written", result.Message, StringComparison.Ordinal);
        Assert.Single(_h.CallsTo("Set-MailboxRegionalConfiguration"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASetADUserThatFailedWithoutApplying_StaysAFailedRow_CarryingTheWriteError(bool readBackFails)
    {
        // The same error, nothing applied, an older City in place: the read-back is made and does not show the write, so
        // the row is the partial it was, carrying the write's error in its own words and what the read-back found - or,
        // when the read-back cannot be made either, both errors.
        RoomObject["l"] = "Cork";
        _h.Failures["Set-ADUser"] = TimedOut;
        if (readBackFails)
            _h.AttributeReadBackFailure = "The server is not operational.";

        var result = await Finder(new Service(_h), Room(), city: "Dublin");

        Assert.False(result.Success);
        Assert.True(result.Partial);
        Assert.Null(result.WrittenDirectoryObject);
        Assert.Null(result.DirectoryAttributesConfirmed);
        Assert.Contains($"Failed to write City/State/Country on the AD object: {TimedOut}", result.Message, StringComparison.Ordinal);
        TheWriteAndItsReadBack();
        if (readBackFails)
        {
            Assert.Contains($"Reading l, st, co back from {RoomDomain} afterwards failed too: ", result.Message, StringComparison.Ordinal);
            Assert.Contains("The server is not operational.", result.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains($"Read back from {RoomDomain} afterwards, l (City) is 'Cork', not 'Dublin' as written.", result.Message, StringComparison.Ordinal);
        }
        Assert.Equal("Cork", Text(RoomObject["l"]));
        Assert.Empty(_h.CallsTo("Set-MailboxRegionalConfiguration"));
    }

    [Fact]
    public void TheHarness_ADUserStandIns_RefuseAServerTheyCannotUse_AndFailAfterSetPlace_OrAfterApplying()
    {
        // The stand-ins the tests above rest on. A -Server DirectoryServerFailures names fails Get-ADUser and Set-ADUser
        // with its message, one the stand-in forest lacks cannot be contacted, and a call with no -Server, or to the
        // forest's other domain, is unaffected. SetAdUserFailsAfterApplying applies the -Replace and then throws. The
        // after-Set-Place knobs fail nothing before a Set-Place and each thing they name after one; the DN knob changes
        // the DN read only after one too.
        var replace = new System.Collections.Hashtable { ["l"] = "Dublin" };
        (string Name, object Value)[] routedRead = [("Identity", CheckedObject), ("Server", RoomDomain), ("ErrorAction", "Stop")];
        (string Name, object Value)[] unroutedRead = [("Identity", CheckedObject), ("ErrorAction", "Stop")];
        (string Name, object Value)[] otherDomainRead = [("Identity", OtherDomainObject), ("Server", OtherDomain), ("ErrorAction", "Stop")];
        (string Name, object Value)[] unknownServerRead = [("Identity", CheckedObject), ("Server", "dc9.invalid"), ("ErrorAction", "Stop")];
        (string Name, object Value)[] propertiesRead = [("Identity", CheckedObject), ("Properties", new[] { "l" }), ("ErrorAction", "Stop")];
        (string Name, object Value)[] routedWrite = [("Identity", CheckedObject), ("Replace", replace), ("Server", RoomDomain), ("ErrorAction", "Stop")];
        (string Name, object Value)[] import = [("Name", "ActiveDirectory"), ("ErrorAction", "Stop")];
        AddOtherDomainObject();

        _h.DirectoryServerFailures[RoomDomain] = Unreachable;
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Assert.Contains(Unreachable, Assert.ThrowsAny<RuntimeException>(() => Run(ps, "Get-ADUser", routedRead)).Message, StringComparison.Ordinal);
            Assert.Contains(Unreachable, Assert.ThrowsAny<RuntimeException>(() => Run(ps, "Set-ADUser", routedWrite)).Message, StringComparison.Ordinal);
            Assert.False(RoomObject.ContainsKey("l"));
            Assert.Single(Run(ps, "Get-ADUser", unroutedRead));
            Assert.Single(Run(ps, "Get-ADUser", otherDomainRead));
            Assert.Contains("Unable to contact the server 'dc9.invalid'", Assert.ThrowsAny<RuntimeException>(() => Run(ps, "Get-ADUser", unknownServerRead)).Message, StringComparison.Ordinal);
        }

        _h.DirectoryServerFailures.Clear();
        _h.SetAdUserFailsAfterApplying = TimedOut;
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Assert.Contains(TimedOut, Assert.ThrowsAny<RuntimeException>(() => Run(ps, "Set-ADUser", routedWrite)).Message, StringComparison.Ordinal);
            Assert.Equal("Dublin", Text(RoomObject["l"]));
        }

        _h.SetAdUserFailsAfterApplying = null;
        _h.DirectoryRunspaceFailureAfterSetPlace = "The runspace failed.";
        _h.ImportModuleFailureAfterSetPlace = "The import failed.";
        _h.ObjectReadFailureAfterSetPlace = "The object read failed.";
        _h.ObjectDnAfterSetPlace = "CN=Room 1,OU=Rooms," + ExchangeWriteHarness.OtherDirectoryDomainDn;
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Run(ps, "Import-Module", import);
            Assert.Equal("CN=Room 1,OU=Rooms,DC=contoso,DC=test", Text(Value(Run(ps, "Get-ADUser", unroutedRead).Single(), "DistinguishedName")));

            _h.Calls.Add(new System.Collections.Hashtable { ["__Command"] = "Set-Place" });
            Assert.Contains("The import failed.", Assert.ThrowsAny<RuntimeException>(() => Run(ps, "Import-Module", import)).Message, StringComparison.Ordinal);
            Assert.Contains("The object read failed.", Assert.ThrowsAny<RuntimeException>(() => Run(ps, "Get-ADUser", unroutedRead)).Message, StringComparison.Ordinal);
            Assert.Equal("CN=Room 1,OU=Rooms," + ExchangeWriteHarness.OtherDirectoryDomainDn,
                Text(Value(Run(ps, "Get-ADUser", propertiesRead).Single(), "DistinguishedName")));
        }
        Assert.Equal("The runspace failed.", Assert.Throws<InvalidOperationException>(() => _h.OpenDirectoryRunspace()).Message);
    }

    // ---- aud-a2r: every step named, an import's error the one it throws, and "Nothing was written" only where it is true ----
    // The preflight's catch named no step. Each of the four directory paths took a record its Import-Module left for that
    // import's error, unnamed, though under -ErrorAction Stop the import's own error is thrown. ConfirmDirectoryObject's
    // refusals said "Nothing was written." inside the partial of a write made after Set-Place. And the stand-in Get-ADUser
    // and Set-ADUser answered on any server they reached, so a wrong -Server was caught only by pinning its value.

    private const string OtherDomainObject = "5a5a5a5a-0000-4000-8000-000000000003";

    // The forest's other domain by its DNS name: the -Server that reaches an object of it.
    private const string OtherDomain = "fabrikam.test";

    // An object of the forest's other domain, found through its DNS name or the catalog only.
    private void AddOtherDomainObject() =>
        _h.DirectoryObjects[OtherDomainObject] = new System.Collections.Hashtable
        {
            ["ObjectGUID"] = Guid.Parse(OtherDomainObject),
            ["DistinguishedName"] = "CN=Room 3,OU=Rooms," + ExchangeWriteHarness.OtherDirectoryDomainDn,
        };

    // The Building's list synchronized from on-premises, refusing the cloud add: the row's room-list add is made on-prem.
    private void SyncedListTakingTheRoomOnPrem()
    {
        _h.ListSyncState[List] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;
    }

    [Theory]
    [InlineData("runspace")]
    [InlineData("import")]
    [InlineData("credential")]
    [InlineData("object read")]
    public async Task AFailureAtEachStepOfTheCityStateAndCountryPreflight_NamesThatStep(string failing)
    {
        // Each step of the preflight fails in turn, before Set-Place. Each refuses the row while nothing is written, in its
        // one step, and the message and the log line name the step that failed and carry its error. aud-a2t: the credential
        // through the seam, for the preflight's purpose only - a step no test reached before, and no failure reaches live.
        const string Injected = "The injected failure.";
        var logger = new CapturingLogger();
        var service = new Service(_h, logger);
        switch (failing)
        {
            case "runspace": _h.DirectoryRunspaceFailure = Injected; break;
            case "import": _h.ImportModuleFailure = Injected; break;
            case "credential": service.IncompleteCredentialFor = "Set-ADUser preflight"; break;
            default: _h.Failures["Get-ADUser"] = Injected; break;
        }

        var result = await Finder(service, Room(), city: "Dublin");

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Empty(_h.Writes);
        var step = Assert.Single(result.Steps);
        Assert.Equal("Set-ADUser preflight (City/State/Country)", step.Stage);
        Assert.False(step.Success);
        Assert.Equal(result.Message, step.Error);
        var named = failing switch
        {
            "runspace" => "opening the directory runspace failed",
            "import" => "importing the ActiveDirectory module failed",
            "credential" => "preparing the module's AD credential failed",
            _ => "reading the AD object failed",
        };
        Assert.Contains($"{named}: ", result.Message, StringComparison.Ordinal);
        Assert.Contains(InjectedText(failing, Injected), result.Message, StringComparison.Ordinal);
        Assert.Contains(CheckedObject, result.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was written.", result.Message, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains(named, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("preflight")]
    [InlineData("add")]
    public async Task AFailedImportOnTheOnPremRoomListPaths_NamesThatStep(string path)
    {
        // The room list's on-prem paths keep no step of their own; their import's error - the one it throws - is told as
        // the import's, before any write in the preflight and after Set-Place in the add, and logged so.
        const string Injected = "The injected failure.";
        SyncedListTakingTheRoomOnPrem();
        if (path == "preflight")
            _h.ImportModuleFailure = Injected;
        else
            _h.ImportModuleFailureAfterSetPlace = Injected;
        var logger = new CapturingLogger();

        var result = await Finder(new Service(_h, logger), Room());

        Assert.False(result.Success);
        Assert.Contains("importing the ActiveDirectory module failed: ", result.Message, StringComparison.Ordinal);
        Assert.Contains(Injected, result.Message, StringComparison.Ordinal);
        Assert.Contains(List, result.Message, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("importing the ActiveDirectory module failed", StringComparison.Ordinal));
        Assert.Empty(_h.CallsTo("Set-ADGroup"));
        if (path == "preflight")
        {
            Assert.False(result.Partial);
            Assert.Empty(_h.Writes);
            Assert.EndsWith("Nothing was written.", result.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(result.Partial);
            Assert.Single(_h.CallsTo("Set-Place"));
            Assert.DoesNotContain("Nothing was written", result.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AnErrorTheImportReportsWithoutThrowing_IsNotTakenForAnyStepsError()
    {
        // Under -ErrorAction Stop an error a cmdlet writes is thrown, so one the import reports without a throw - the
        // stand-in's own -ErrorAction Continue - is not the import's. A row that opens all four directory runspaces - the
        // City/State/Country preflight and write, the synced list's preflight and add - completes, the reported text
        // nowhere: no check takes it for the import's error, and the record is gone before the read after the import,
        // whose own error check would take it for that read's.
        const string Reported = "A non-terminating error the import reported and carried on past.";
        _h.ImportModuleReportsErrorWithoutThrowing = Reported;
        SyncedListTakingTheRoomOnPrem();

        var result = await Finder(new Service(_h), Room(), city: "Dublin");

        Assert.True(result.Success, result.Message);
        Assert.False(result.Partial);
        Assert.Equal(CheckedObject, result.WrittenDirectoryObject);
        Assert.Single(_h.CallsTo("Set-ADUser"));
        Assert.Single(_h.CallsTo("Set-ADGroup"));
        Assert.DoesNotContain(Reported, result.Message, StringComparison.Ordinal);
        Assert.All(result.Steps, s => Assert.DoesNotContain(Reported, s.Error ?? "", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("City/State/Country", "preflight")]
    [InlineData("City/State/Country", "write")]
    [InlineData("room list", "preflight")]
    [InlineData("room list", "add")]
    public async Task TheObjectReadsRefusal_SaysNothingWasWritten_OnlyWhereNothingWas(string path, string phase)
    {
        // The read that proves the room's object is answered by another object, on each of the four paths. A preflight's
        // refusal comes before any write and says nothing was written; a write's comes after Set-Place, inside the
        // partial, and does not say it.
        const string Another = "7a7a7a7a-0000-4000-8000-000000000002";
        if (phase == "preflight")
            _h.ObjectReadAnswersAs = Another;
        else
            _h.ObjectReadAnswersAsAfterSetPlace = Another;
        if (path == "room list")
            SyncedListTakingTheRoomOnPrem();

        var result = await Finder(new Service(_h), Room(), city: path == "room list" ? "" : "Dublin");

        Assert.False(result.Success);
        var refusal = $"Active Directory answered {CheckedObject} with a different object ({Another}).";
        var step = Assert.Single(result.Steps, s => !s.Success);
        Assert.NotNull(step.Error);
        Assert.Contains(refusal, step.Error, StringComparison.Ordinal);
        Assert.Contains(refusal, result.Message, StringComparison.Ordinal);
        if (phase == "preflight")
        {
            Assert.False(result.Partial);
            Assert.Empty(_h.Writes);
            Assert.EndsWith($"{refusal} Nothing was written.", result.Message, StringComparison.Ordinal);
        }
        else
        {
            Assert.True(result.Partial);
            Assert.Single(_h.CallsTo("Set-Place"));
            Assert.Empty(_h.CallsTo(path == "room list" ? "Set-ADGroup" : "Set-ADUser"));
            Assert.DoesNotContain("Nothing was written", result.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheHarness_ADUserStandIns_FindAnObjectOnlyInADomainTheirServerReaches()
    {
        // The routing the tests above rest on, so a wrong -Server fails one by what the stand-in directory does with it,
        // not only by the value pinned. As the stand-in Get-ADGroup since aud-a2j: an object is read or written only
        // through its own domain's DNS name, the global catalog (a read; the catalog refuses a write), or - in the default
        // domain only - no -Server or a bare port. An object whose DN names no domain lives in the default one.
        AddOtherDomainObject();
        using var rs = _h.OpenDirectoryRunspace();
        using var ps = PowerShell.Create();
        ps.Runspace = rs;

        foreach (var properties in new[] { false, true })
        {
            Assert.Single(ReadObject(ps, CheckedObject, null, properties));
            Assert.Single(ReadObject(ps, CheckedObject, RoomDomain, properties));
            Assert.Single(ReadObject(ps, CheckedObject, ExchangeWriteHarness.GlobalCatalogEndpoint, properties));
            Assert.Single(ReadObject(ps, CheckedObject, ":3268", properties));
            NotFound(() => ReadObject(ps, CheckedObject, OtherDomain, properties));
            Assert.Single(ReadObject(ps, OtherDomainObject, OtherDomain, properties));
            Assert.Single(ReadObject(ps, OtherDomainObject, ExchangeWriteHarness.GlobalCatalogEndpoint, properties));
            NotFound(() => ReadObject(ps, OtherDomainObject, null, properties));
            NotFound(() => ReadObject(ps, OtherDomainObject, RoomDomain, properties));
            NotFound(() => ReadObject(ps, OtherDomainObject, ":3268", properties));
        }

        var other = (System.Collections.Hashtable)_h.DirectoryObjects[OtherDomainObject]!;
        WriteCity(ps, CheckedObject, null, "Cork");
        Assert.Equal("Cork", Text(RoomObject["l"]));
        WriteCity(ps, CheckedObject, RoomDomain, "Dublin");
        Assert.Equal("Dublin", Text(RoomObject["l"]));
        NotFound(() => WriteCity(ps, CheckedObject, OtherDomain, "Galway"));
        Assert.Contains("unwilling to process", Assert.ThrowsAny<RuntimeException>(() =>
            WriteCity(ps, CheckedObject, ExchangeWriteHarness.GlobalCatalogEndpoint, "Galway")).Message, StringComparison.Ordinal);
        Assert.Equal("Dublin", Text(RoomObject["l"]));
        WriteCity(ps, OtherDomainObject, OtherDomain, "Limerick");
        Assert.Equal("Limerick", Text(other["l"]));
        NotFound(() => WriteCity(ps, OtherDomainObject, null, "Galway"));
        NotFound(() => WriteCity(ps, OtherDomainObject, RoomDomain, "Galway"));
        Assert.Equal("Limerick", Text(other["l"]));

        foreach (var dn in new[] { null, "CN=Room 1,OU=Rooms" })
        {
            if (dn is null)
                RoomObject.Remove("DistinguishedName");
            else
                RoomObject["DistinguishedName"] = dn;
            Assert.Single(ReadObject(ps, CheckedObject, null, false));
            Assert.Single(ReadObject(ps, CheckedObject, RoomDomain, false));
            NotFound(() => ReadObject(ps, CheckedObject, OtherDomain, false));
        }
    }

    private static System.Collections.ObjectModel.Collection<PSObject> ReadObject(PowerShell ps, string objectGuid, string? server, bool properties)
    {
        var parameters = new List<(string Name, object Value)> { ("Identity", objectGuid), ("ErrorAction", "Stop") };
        if (properties)
            parameters.Add(("Properties", new[] { "l", "st", "co" }));
        if (server is not null)
            parameters.Add(("Server", server));
        return Run(ps, "Get-ADUser", parameters.ToArray());
    }

    private static void WriteCity(PowerShell ps, string objectGuid, string? server, string city)
    {
        var parameters = new List<(string Name, object Value)>
        {
            ("Identity", objectGuid), ("Replace", new System.Collections.Hashtable { ["l"] = city }), ("ErrorAction", "Stop"),
        };
        if (server is not null)
            parameters.Add(("Server", server));
        Run(ps, "Set-ADUser", parameters.ToArray());
    }

    private static void NotFound(Action call) =>
        Assert.Contains("Cannot find an object with identity", Assert.ThrowsAny<RuntimeException>(call).Message, StringComparison.Ordinal);

    [Fact]
    public void TheHarness_ImportAndObjectReadKnobs_DoWhatTheTestsAboveRestOn()
    {
        // Under -ErrorAction Stop, as the code calls the stand-ins. The import's reported error is not thrown and leaves its
        // one record; its failure is a non-terminating error that Stop throws, leaving none - and, without Stop, is not
        // thrown; DirectoryRunspaceFailure fails a runspace at any time; ObjectReadAnswersAs makes another object answer the
        // read that proves one and not the read-back, and its AfterSetPlace twin only once a Set-Place is recorded.
        (string Name, object Value)[] import = [("Name", "ActiveDirectory"), ("ErrorAction", "Stop")];
        (string Name, object Value)[] importWithoutStop = [("Name", "ActiveDirectory")];
        (string Name, object Value)[] objectRead = [("Identity", CheckedObject), ("ErrorAction", "Stop")];
        (string Name, object Value)[] readBack = [("Identity", CheckedObject), ("Properties", new[] { "l" }), ("ErrorAction", "Stop")];
        const string Another = "7a7a7a7a-0000-4000-8000-000000000002";

        _h.ImportModuleReportsErrorWithoutThrowing = "Reported, not thrown.";
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Run(ps, "Import-Module", import);
            Assert.True(ps.HadErrors);
            Assert.Equal("Reported, not thrown.", Assert.Single(ps.Streams.Error).Exception.Message);
        }

        _h.ImportModuleReportsErrorWithoutThrowing = null;
        _h.ImportModuleFailure = "The import failed.";
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Assert.Contains("The import failed.", Assert.ThrowsAny<RuntimeException>(() => Run(ps, "Import-Module", import)).Message, StringComparison.Ordinal);
            Assert.Empty(ps.Streams.Error);
            Run(ps, "Import-Module", importWithoutStop);
            Assert.Equal("The import failed.", Assert.Single(ps.Streams.Error).Exception.Message);
        }

        _h.ImportModuleFailure = null;
        _h.DirectoryRunspaceFailure = "The runspace failed.";
        Assert.Equal("The runspace failed.", Assert.Throws<InvalidOperationException>(() => _h.OpenDirectoryRunspace()).Message);

        _h.DirectoryRunspaceFailure = null;
        _h.ObjectReadAnswersAs = Another;
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Assert.Equal(Guid.Parse(Another), Value(Run(ps, "Get-ADUser", objectRead).Single(), "ObjectGUID"));
            Assert.Equal(Guid.Parse(CheckedObject), Value(Run(ps, "Get-ADUser", readBack).Single(), "ObjectGUID"));
        }

        _h.ObjectReadAnswersAs = null;
        _h.ObjectReadAnswersAsAfterSetPlace = Another;
        using (var rs = _h.OpenDirectoryRunspace())
        using (var ps = PowerShell.Create())
        {
            ps.Runspace = rs;
            Assert.Equal(Guid.Parse(CheckedObject), Value(Run(ps, "Get-ADUser", objectRead).Single(), "ObjectGUID"));
            _h.Calls.Add(new System.Collections.Hashtable { ["__Command"] = "Set-Place" });
            Assert.Equal(Guid.Parse(Another), Value(Run(ps, "Get-ADUser", objectRead).Single(), "ObjectGUID"));
            Assert.Equal(Guid.Parse(CheckedObject), Value(Run(ps, "Get-ADUser", readBack).Single(), "ObjectGUID"));
        }
    }

    [Fact]
    public void TheRealImportModule_ThrowsItsOwnErrorUnderStop_AndLeavesNoRecord()
    {
        // The measurement the removed HadErrors branches were judged by, on this SDK: the real Import-Module, unable to load
        // its module, under -ErrorAction Stop, throws from Invoke and leaves no record in the error stream, so a check made
        // after Invoke never sees the import's own error; without Stop it throws nothing and leaves the record. A local
        // runspace and a module name nothing provides: no directory is touched.
        var missing = "EawNoSuchModule-" + Guid.NewGuid().ToString("N");
        using var rs = RunspaceFactory.CreateRunspace();
        rs.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = rs;

        ps.AddCommand("Import-Module").AddParameter("Name", missing).AddParameter("ErrorAction", "Stop");
        Assert.ThrowsAny<RuntimeException>(() => ps.Invoke());
        Assert.Empty(ps.Streams.Error);

        ps.Commands.Clear();
        ps.AddCommand("Import-Module").AddParameter("Name", missing);
        ps.Invoke();
        Assert.Single(ps.Streams.Error);
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

    // ---- aud-a2u: the room-object read's refusals, and those decided before the directory work, are logged once ----
    // Each path took the reason into its refusal and logged nothing: the room-object read's refusal on all four directory
    // paths, and - before any runspace - an unmappable country, a list Exchange reported with no GUID or no sync state, or a
    // room with no proven directory object. Only the row's result and audit carried them. Each is now logged once, at
    // Warning, where the path refuses (aud-a2t's placement); the row, which puts the refusal on its result, adds no line.

    private const string NothingWritten = " Nothing was written.";

    // The line each path logs for a refusal - aud-a2t's two room-list lines among them, since aud-a2v the row's own, and since
    // aud-a2w the Type path's - for the room as it was typed.
    private static string RefusalLine(string path, string refusal) => path switch
    {
        "Room Finder apply" => $"Room Finder apply for {Typed} refused the row; nothing was written: {refusal}",
        "Room Type apply" => $"Room Type apply for {Typed} refused the row; nothing was written: {refusal}",
        "City/State/Country preflight" => $"Set-ADUser preflight for {Typed} refused the row; nothing was written: {refusal}",
        "City/State/Country write" => $"Set-ADUser City/State/Country write for {Typed} refused before its write: {refusal}",
        "room-list preflight" => $"Room-list preflight for {Typed} on {List} refused the row; nothing was written: {refusal}",
        "on-prem room-list preflight" => $"On-prem room-list preflight for {Typed} on {List} refused the row; nothing was written: {refusal}",
        "on-prem room-list add" => $"On-prem room-list add for {Typed} on {List} refused before its write: {refusal}",
        _ => throw new ArgumentOutOfRangeException(nameof(path), path, null),
    };

    // Exactly one entry carries the refusal, so nothing logs it a second time: a warning, the path's own line.
    private static void AssertLoggedOnce(CapturingLogger logger, string path, string refusal)
    {
        var logged = Assert.Single(logger.Entries, e => e.Message.Contains(refusal, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Equal(RefusalLine(path, refusal), logged.Message);
    }

    [Theory]
    [InlineData("City/State/Country", "preflight")]
    [InlineData("City/State/Country", "write")]
    [InlineData("room list", "preflight")]
    [InlineData("room list", "add")]
    public async Task TheObjectReadsRefusal_IsLoggedOnce_WhereThePathRefuses(string path, string phase)
    {
        // The read that proves the room's object answered by another object, on each of the four paths, set up as in
        // TheObjectReadsRefusal_SaysNothingWasWritten_OnlyWhereNothingWas: a preflight refused with nothing written, a write
        // partial with its own write not made - and one warning, the path's, carrying the refusal.
        const string Another = "7a7a7a7a-0000-4000-8000-000000000002";
        if (phase == "preflight")
            _h.ObjectReadAnswersAs = Another;
        else
            _h.ObjectReadAnswersAsAfterSetPlace = Another;
        if (path == "room list")
            SyncedListTakingTheRoomOnPrem();
        var logger = new CapturingLogger();

        var result = await Finder(new Service(_h, logger), Room(), city: path == "room list" ? "" : "Dublin");

        Assert.False(result.Success);
        Assert.Equal(phase != "preflight", result.Partial);
        if (phase == "preflight")
            Assert.Empty(_h.Writes);
        else
            Assert.Empty(_h.CallsTo(path == "room list" ? "Set-ADGroup" : "Set-ADUser"));
        var refusal = $"Active Directory answered {CheckedObject} with a different object ({Another}).";
        var step = Assert.Single(result.Steps, s => !s.Success);
        Assert.NotNull(step.Error);
        Assert.Contains(refusal, step.Error, StringComparison.Ordinal);
        AssertLoggedOnce(logger, (path, phase) switch
        {
            ("City/State/Country", "preflight") => "City/State/Country preflight",
            ("City/State/Country", _) => "City/State/Country write",
            (_, "preflight") => "on-prem room-list preflight",
            _ => "on-prem room-list add",
        }, refusal);
    }

    [Theory]
    [InlineData("an unmappable country")]
    [InlineData("no AD object for City/State/Country")]
    [InlineData("a list with no GUID")]
    [InlineData("a list with no sync state")]
    [InlineData("no AD object for a synced list")]
    public async Task ARefusalBeforeTheDirectoryWork_IsLoggedOnce_NamingTheRoomTheListAndTheReason(string refusal)
    {
        // Each refusal a path decides before it opens a runspace or asks for a credential, through the whole row: refused
        // while nothing is written, in its one step, as before - and one warning, the path's, naming the room, the list where
        // there is one, and the refusal as the row reports it, less the " Nothing was written." each preflight appends.
        var room = Room();
        var city = "";
        var country = "";
        switch (refusal)
        {
            case "an unmappable country": country = "ZZ"; break;
            case "no AD object for City/State/Country": room = Room(directoryObject: null); city = "Dublin"; break;
            case "a list with no GUID": _h.ListGuids[List] = null; break;
            case "a list with no sync state": _h.ListSyncState[List] = null; break;
            case "no AD object for a synced list": room = Room(directoryObject: null); SyncedListTakingTheRoomOnPrem(); break;
            default: throw new ArgumentOutOfRangeException(nameof(refusal), refusal, null);
        }
        var logger = new CapturingLogger();
        var service = new Service(_h, logger);

        var result = await Finder(service, room, city: city, country: country);

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Empty(_h.Writes);
        Assert.Equal(0, service.CredentialRequests);
        var step = Assert.Single(result.Steps);
        Assert.False(step.Success);
        Assert.Equal(result.Message, step.Error);
        var cityStateAndCountry = refusal is "an unmappable country" or "no AD object for City/State/Country";
        Assert.Equal(cityStateAndCountry ? "Set-ADUser preflight (City/State/Country)" : "Room list preflight", step.Stage);
        // aud-a2v. All five end so, the City/State/Country preflight's two as well since that preflight took the room-list
        // preflight's rule: they said only what could not be written.
        Assert.EndsWith(NothingWritten, result.Message, StringComparison.Ordinal);
        var reason = result.Message[..^NothingWritten.Length];
        Assert.Contains(refusal switch
        {
            "an unmappable country" => "'ZZ' is not a recognized ISO 3166-1 alpha-2 country code",
            "no AD object for City/State/Country" => $"No on-premises Active Directory object was validated for '{Typed}' (a cloud-only room has none), so City/State/Country could not be written.",
            "a list with no GUID" => $"Exchange reported no GUID for room list '{List}'",
            "a list with no sync state" => $"Exchange did not report whether room list '{List}' is synchronized from on-premises",
            _ => $"(a cloud-only room has none), so it cannot be added to room list '{List}', which is synchronized from on-premises and takes members only there.",
        }, reason, StringComparison.Ordinal);
        AssertLoggedOnce(logger, refusal switch
        {
            "an unmappable country" or "no AD object for City/State/Country" => "City/State/Country preflight",
            "no AD object for a synced list" => "on-prem room-list preflight",
            _ => "room-list preflight",
        }, reason);
    }

    [Fact]
    public async Task TheOnPremAddsRefusalOfARoomWithNoDirectoryObject_IsLoggedOnce()
    {
        // The on-prem add's own refusal of a room with no proven object. Through the row only a race reached it - the
        // preflight's membership read finding the room on its synced list, the add's no longer - and since aud-a2e not even
        // that: no group was checked for such a list, so the add refuses its cloud write first. It stays the add's own guard,
        // so the add is called directly, as the row calls it after Set-Place (aud-a2p), handed a list carrying a cleared
        // group as if a check had run: it fails with nothing written on-prem and no credential asked for, and one warning
        // names the room, the list and the refusal.
        SyncedListTakingTheRoomOnPrem();
        var logger = new CapturingLogger();
        var service = new Service(_h, logger);
        var list = new ConferenceRoomService.RoomListTarget(List, Exists: true, IsLegacy: false, IsDirSynced: true,
            ExchangeWriteHarness.DefaultListAddress, ExchangeWriteHarness.DefaultListGuid, DirectoryGroup: ExchangeWriteHarness.DirectoryGroupAsChecked());

        var result = await service.AddToRoomListAsync(Room(directoryObject: null), "room1@contoso.test", Typed, list, actingUser: null);

        Assert.False(result.Success);
        Assert.Null(result.WrittenDirectoryObject);
        var step = Assert.Single(result.Steps, s => s.Stage == $"Add to '{List}' (on-prem)");
        Assert.False(step.Success);
        Assert.NotNull(step.Error);
        Assert.Equal(step.Error, result.Message);
        Assert.Equal($"No on-premises Active Directory object was validated for '{Typed}' (a cloud-only room has none), so the room could not be added to the on-prem room list.", step.Error);
        Assert.Empty(_h.CallsTo("Set-ADGroup"));
        Assert.Equal(0, service.CredentialRequests);
        AssertLoggedOnce(logger, "on-prem room-list add", step.Error);
    }

    // ---- aud-a2v: the row's own refusals and a credential no lookup returned are logged; one refusal style ----
    // The row's own refusals before its preflights - no validated room, an invalid time zone, a recipient that is not a room
    // mailbox - were returned and logged nowhere. A credential lookup returning none left only the credential services'
    // lines, which name the secret, not the room or the row. The unmappable country reached the operator ending with .NET's
    // " (Parameter 'countryOrRegion')". And the City/State/Country preflight's refusals before its runspace did not end
    // "Nothing was written." as the room-list preflight's do.

    [Theory]
    [InlineData("no validated room")]
    [InlineData("an invalid time zone")]
    [InlineData("a recipient that is not a room mailbox")]
    public async Task TheRowsOwnRefusal_IsLoggedOnce_NamingTheRoomAndTheReason(string refusal)
    {
        // Each refusal the row makes before its preflights, with every field set so that a row past it would ask for a
        // credential and write: refused in no step, nothing written, nothing asked for, its message the refusal - since
        // aud-a2w ending " Nothing was written." but for the validated-mailbox one, which says so in its own words - and one
        // warning, the row's, naming the room as typed and the refusal.
        ValidatedMailbox? room = Room();
        var timezone = "GMT Standard Time";
        switch (refusal)
        {
            case "no validated room": room = null; break;
            case "an invalid time zone": timezone = "Mars Standard Time"; break;
            case "a recipient that is not a room mailbox": _h.AddMailbox("room1@contoso.test", Checked, recipientTypeDetails: "EquipmentMailbox"); break;
            default: throw new ArgumentOutOfRangeException(nameof(refusal), refusal, null);
        }
        var logger = new CapturingLogger();
        var service = new Service(_h, logger);

        var result = await service.SetRoomMetadataAndListAsync(
            Typed, room, actingUser: null, "Dublin", "Building 1", 8, "2", "Second", "TV", "Polycom", "IE", "Leinster", timezone);

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Empty(result.Steps);
        Assert.Empty(_h.Writes);
        Assert.Equal(0, service.CredentialRequests);
        var reason = refusal switch
        {
            "no validated room" => ValidatedMailbox.Refusal(Typed),
            "an invalid time zone" => "Invalid timezone: 'Mars Standard Time'.",
            _ => $"'{Typed}' is a EquipmentMailbox, not a room mailbox. Conference Room operations require RoomMailbox or RemoteRoomMailbox.",
        };
        Assert.Equal(refusal == "no validated room" ? reason : reason + NothingWritten, result.Message);
        AssertSaysNothingWasWrittenOnce(result.Message);
        AssertLoggedOnce(logger, "Room Finder apply", reason);
    }

    [Theory]
    [InlineData("City/State/Country preflight")]
    [InlineData("City/State/Country write")]
    [InlineData("on-prem room-list preflight")]
    [InlineData("on-prem room-list add")]
    public async Task ACredentialTheLookupDoesNotReturn_IsLoggedOnce_NamingTheRoomTheListAndThePurpose(string path)
    {
        // The module's credential lookup returning none for one path's purpose only, on each of the four. Refused as before -
        // a preflight while nothing is written, a write after Set-Place, inside the partial - and one warning, the path's,
        // naming the room, the list where there is one, and the purpose the lookup was asked with: the purpose the credential
        // service's own line names, where it logs one.
        var cityStateAndCountry = path.StartsWith("City/State/Country", StringComparison.Ordinal);
        var preflight = path.EndsWith("preflight", StringComparison.Ordinal);
        if (!cityStateAndCountry)
            SyncedListTakingTheRoomOnPrem();
        var logger = new CapturingLogger();
        var service = new Service(_h, logger)
        {
            NoCredentialFor = path switch
            {
                "City/State/Country preflight" => "Set-ADUser preflight",
                "City/State/Country write" => "Set-ADUser synced attributes",
                "on-prem room-list preflight" => "membership preflight",
                _ => "membership add",
            },
        };
        var purpose = path switch
        {
            "City/State/Country preflight" => $"Set-ADUser preflight for {Typed}",
            "City/State/Country write" => $"Set-ADUser synced attributes for {Typed}",
            "on-prem room-list preflight" => $"on-prem room-list membership preflight for {Typed}",
            _ => $"on-prem room-list membership add for {Typed}",
        };
        var refusal = path switch
        {
            "on-prem room-list preflight" => $"On-prem AD credential is not configured for Conference Rooms (set the AD Delinea Secret ID in Module Config), and room list '{List}' is synchronized from on-premises, so the room cannot be added to it.",
            "on-prem room-list add" => "On-prem AD credential is not configured for Conference Rooms (set the AD Delinea Secret ID in Module Config). The room could not be added to the on-prem room list.",
            _ => "On-prem AD credential is not configured for Conference Rooms (set the AD Delinea Secret ID in Module Config). City/State/Country could not be written.",
        };

        var result = await Finder(service, Room(), city: cityStateAndCountry ? "Dublin" : "");

        Assert.False(result.Success);
        Assert.Equal(!preflight, result.Partial);
        Assert.Equal(purpose, service.CredentialPurposes.Last());
        var step = Assert.Single(result.Steps, s => !s.Success);
        Assert.Equal(path switch
        {
            "City/State/Country preflight" => "Set-ADUser preflight (City/State/Country)",
            "City/State/Country write" => "Set-ADUser (City/State/Country)",
            "on-prem room-list preflight" => "Room list preflight",
            _ => $"Add to '{List}' (on-prem)",
        }, step.Stage);
        if (preflight)
        {
            Assert.Empty(_h.Writes);
            Assert.Single(result.Steps);
            Assert.Equal(refusal + NothingWritten, result.Message);
            Assert.Equal(result.Message, step.Error);
        }
        else
        {
            Assert.Single(_h.CallsTo("Set-Place"));
            Assert.Empty(_h.CallsTo(cityStateAndCountry ? "Set-ADUser" : "Set-ADGroup"));
            Assert.Equal(refusal, step.Error);
            Assert.Contains(refusal, result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Nothing was written", result.Message, StringComparison.Ordinal);
        }
        AssertLoggedOnce(logger, path, $"no AD credential was returned for {purpose}: {refusal}");
        // And that line is the only one carrying the refusal: the row, which puts it on its result, logs it no second time.
        Assert.Single(logger.Entries, e => e.Message.Contains(refusal, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheUnmappableCountrysMessage_CarriesNoParameterSuffix()
    {
        // An unmappable country through the whole row: the operator reads the mapping's own words - the value, and what to do
        // about it - then that nothing was written, and no " (Parameter 'countryOrRegion')", which ArgumentException adds to
        // the message of an exception naming its parameter. The line carries the same words.
        const string Mapping = "'ZZ' is not a recognized ISO 3166-1 alpha-2 country code. Use the two-letter code (e.g. IE, US, GB). If this is a new country, add it to IsoCountryCodes.cs.";
        var logger = new CapturingLogger();

        var result = await Finder(new Service(_h, logger), Room(), country: "ZZ");

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Empty(_h.Writes);
        Assert.DoesNotContain("(Parameter", result.Message, StringComparison.Ordinal);
        Assert.Equal(Mapping + NothingWritten, result.Message);
        AssertLoggedOnce(logger, "City/State/Country preflight", Mapping);
    }

    [Fact]
    public void TheCountryMappingsOtherRefusal_CarriesNoParameterSuffix()
    {
        // The mapping's other refusal: a code in the ISO table that this runtime's RegionInfo cannot name, so co would have no
        // value. Western Sahara (EH) is one on this machine's runtime (.NET 10 over the operating system's ICU; measured
        // 2026-10-22, the only one of the table's 249 codes). On a runtime that can name it, EH maps, and that is pinned instead.
        string? englishName;
        try
        {
            englishName = new System.Globalization.RegionInfo("EH").EnglishName;
        }
        catch (ArgumentException)
        {
            englishName = null;
        }

        if (englishName is null)
        {
            var refused = Assert.Throws<ArgumentException>(() => ConferenceRoomService.BuildCountryAttributes("EH"));
            Assert.Equal("Country 'EH' has no display name on this system; cannot set the 'co' attribute.", refused.Message);
        }
        else
        {
            var mapped = ConferenceRoomService.BuildCountryAttributes("EH");
            Assert.NotNull(mapped);
            Assert.Equal(("EH", englishName, 732), mapped.Value);
        }
    }

    [Theory]
    [InlineData("City/State/Country", "an unmappable country")]
    [InlineData("City/State/Country", "no AD object")]
    [InlineData("City/State/Country", "no credential")]
    [InlineData("room list", "no GUID")]
    [InlineData("room list", "no sync state")]
    [InlineData("room list", "no AD object")]
    [InlineData("room list", "no credential")]
    public async Task EveryRefusalEitherPreflightDecidesBeforeTheDirectoryWork_EndsNothingWasWritten(string preflight, string refusal)
    {
        // One rule for both preflights: a refusal decided before any write says so at its end, in the same words. The
        // room-list preflight's did; the City/State/Country preflight's three before its runspace said only what could not be
        // written, so the operator read two styles for one fact.
        var room = Room();
        var city = "";
        var country = "";
        var service = new Service(_h);
        switch (preflight, refusal)
        {
            case ("City/State/Country", "an unmappable country"): country = "ZZ"; break;
            case ("City/State/Country", "no AD object"): city = "Dublin"; room = Room(directoryObject: null); break;
            case ("City/State/Country", "no credential"): city = "Dublin"; service.NoCredentialFor = "Set-ADUser preflight"; break;
            case ("room list", "no GUID"): _h.ListGuids[List] = null; break;
            case ("room list", "no sync state"): _h.ListSyncState[List] = null; break;
            case ("room list", "no AD object"): room = Room(directoryObject: null); SyncedListTakingTheRoomOnPrem(); break;
            case ("room list", "no credential"): service.NoCredentialFor = "membership preflight"; SyncedListTakingTheRoomOnPrem(); break;
            default: throw new ArgumentOutOfRangeException(nameof(refusal), refusal, null);
        }

        var result = await Finder(service, room, city: city, country: country);

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Empty(_h.Writes);
        var step = Assert.Single(result.Steps);
        Assert.False(step.Success);
        Assert.Equal(preflight == "room list" ? "Room list preflight" : "Set-ADUser preflight (City/State/Country)", step.Stage);
        Assert.Equal(result.Message, step.Error);
        Assert.EndsWith(NothingWritten, result.Message, StringComparison.Ordinal);
        Assert.EndsWith(".", result.Message[..^NothingWritten.Length], StringComparison.Ordinal);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(result.Message, "Nothing was written"));
    }

    // ---- aud-a2w: the Type path's refusals are logged, the row's room read that throws is logged, one rule for the module ----
    // SetRoomTypeAsync refused a row with no validated room, an invalid time zone, a recipient that is not a room mailbox or
    // the module's groups unconfigured, and logged none of them. A throw from the Finder row's room read left the service
    // unlogged: the page's catch audits and notifies, and writes no log line. And the row's and the Type path's refusals
    // said only what was wrong, not that nothing was written, as both preflights' do since aud-a2v.

    // That nothing was written is said once - by the suffix, or by the validated-mailbox refusal's own words.
    private static void AssertSaysNothingWasWrittenOnce(string message) =>
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(
            message, "nothing was written", System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    [Theory]
    [InlineData("no validated room")]
    [InlineData("an invalid time zone")]
    [InlineData("a recipient that is not a room mailbox")]
    [InlineData("the module's groups unconfigured")]
    public async Task TheTypePathsOwnRefusal_IsLoggedOnce_NamingTheRoomAndTheReason(string refusal)
    {
        // Each refusal the Type path makes before any write, with everything else in place: refused in no step, nothing
        // written, nothing asked for, the message the refusal - ending " Nothing was written." but for the validated-mailbox
        // one - and one warning, the Type path's, naming the room as typed and the refusal.
        ValidatedMailbox? room = Room();
        var timezone = "GMT Standard Time";
        switch (refusal)
        {
            case "no validated room": room = null; break;
            case "an invalid time zone": timezone = "Mars Standard Time"; break;
            case "a recipient that is not a room mailbox": _h.AddMailbox("room1@contoso.test", Checked, recipientTypeDetails: "EquipmentMailbox"); break;
            case "the module's groups unconfigured": _h.ModuleConfig.SaveModuleConfig("ConferenceRooms", new Dictionary<string, string>()); break;
            default: throw new ArgumentOutOfRangeException(nameof(refusal), refusal, null);
        }
        var logger = new CapturingLogger();
        var service = new Service(_h, logger);

        var result = await service.SetRoomTypeAsync(Typed, room, RoomType.Standard, timezone);

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Empty(result.Steps);
        Assert.Empty(_h.Writes);
        Assert.Equal(0, service.CredentialRequests);
        var reason = refusal switch
        {
            "no validated room" => ValidatedMailbox.Refusal(Typed),
            "an invalid time zone" => "Invalid timezone: 'Mars Standard Time'.",
            "a recipient that is not a room mailbox" => $"'{Typed}' is a EquipmentMailbox, not a room mailbox. Conference Room operations require RoomMailbox or RemoteRoomMailbox.",
            _ => $"Conference Rooms module is not configured. Set {string.Join(", ", ConferenceRoomService.RequiredGroupConfigKeys)} in Module Config.",
        };
        Assert.Equal(refusal == "no validated room" ? reason : reason + NothingWritten, result.Message);
        AssertSaysNothingWasWrittenOnce(result.Message);
        AssertLoggedOnce(logger, "Room Type apply", reason);
    }

    [Theory]
    [InlineData("another mailbox")]
    [InlineData("no mailbox")]
    public async Task TheRowsRoomRead_ThatThrows_IsLoggedOnce_AndStillThrows(string answer)
    {
        // The Finder row's read of the validated room throws when Exchange answers with another mailbox or none. It still
        // throws, with its message as before - the page's catch and the bulk row's audit and notify on it as they did - and
        // one warning names the room as typed and the message, with the exception. Nothing is written, nothing asked for.
        if (answer == "another mailbox")
            _h.Mailboxes[Identity(Checked)] = _h.Mailboxes["execroom@contoso.test"];
        else
            _h.Mailboxes.Remove(Identity(Checked));
        var logger = new CapturingLogger();
        var service = new Service(_h, logger);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => Finder(service, Room(), city: "Dublin"));

        Assert.Contains(answer == "another mailbox" ? "to a different mailbox" : "couldn't be found", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
        Assert.Equal(0, service.CredentialRequests);
        var logged = Assert.Single(logger.Entries, e => e.Message.Contains(thrown.Message, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Equal($"Room Finder apply for {Typed}: reading the room failed; nothing was written: {thrown.Message}", logged.Message);
        Assert.Same(thrown, logged.Exception);
    }

    // ---- aud-a2y (1): the Type path's room read that throws is logged, as the Finder row's is ----
    // aud-a2w logged the Finder row's throwing room read; SetRoomTypeAsync's own read of the validated room throws the same
    // way and still left no log line - the page's catch and the bulk row's audit and notify, and write none.

    [Theory]
    [InlineData("another mailbox")]
    [InlineData("no mailbox")]
    public async Task TheTypePathsRoomRead_ThatThrows_IsLoggedOnce_AndStillThrows(string answer)
    {
        // The Type path's read of the validated room throws when Exchange answers with another mailbox or none. It still
        // throws, with its message as before, and one warning names the room as typed and the message, with the exception.
        // Nothing is written, nothing asked for, no step recorded.
        if (answer == "another mailbox")
            _h.Mailboxes[Identity(Checked)] = _h.Mailboxes["execroom@contoso.test"];
        else
            _h.Mailboxes.Remove(Identity(Checked));
        var logger = new CapturingLogger();
        var service = new Service(_h, logger);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => service.SetRoomTypeAsync(Typed, Room(), RoomType.Standard, "GMT Standard Time"));

        Assert.Contains(answer == "another mailbox" ? "to a different mailbox" : "couldn't be found", thrown.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
        Assert.Equal(0, service.CredentialRequests);
        var logged = Assert.Single(logger.Entries, e => e.Message.Contains(thrown.Message, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Equal($"Room Type apply for {Typed}: reading the room failed; nothing was written: {thrown.Message}", logged.Message);
        Assert.Same(thrown, logged.Exception);
    }

    // ---- aud-a2x: a credential that could not be read does not send the operator to set the ID that is set ----
    // Each of the four directory paths refused a lookup that returned no credential with "On-prem AD credential is not
    // configured for Conference Rooms (set the AD Delinea Secret ID in Module Config)" - also when the ID was set, and the
    // module's configuration could not be read or Delinea returned nothing for it. The credential service now says which,
    // and each path says it, in the clause its refusal opens with.

    private const string NotSetClause =
        "On-prem AD credential is not configured for Conference Rooms (set the AD Delinea Secret ID in Module Config)";
    private const string NotReturnedClause =
        "The on-prem AD credential for Conference Rooms could not be read from Delinea: the AD Delinea Secret ID is set, but Delinea returned no credential for it (the application log has its reason)";
    private const string UnreadableClause =
        "The on-prem AD credential for Conference Rooms could not be read: the module's configuration could not be read, so neither could its AD Delinea Secret ID (the application log records it)";

    [Theory]
    [InlineData("City/State/Country preflight", ModuleCredentialFailure.NotReturned)]
    [InlineData("City/State/Country write", ModuleCredentialFailure.NotReturned)]
    [InlineData("on-prem room-list preflight", ModuleCredentialFailure.NotReturned)]
    [InlineData("on-prem room-list add", ModuleCredentialFailure.NotReturned)]
    [InlineData("City/State/Country preflight", ModuleCredentialFailure.ConfigUnreadable)]
    [InlineData("City/State/Country write", ModuleCredentialFailure.ConfigUnreadable)]
    [InlineData("on-prem room-list preflight", ModuleCredentialFailure.ConfigUnreadable)]
    [InlineData("on-prem room-list add", ModuleCredentialFailure.ConfigUnreadable)]
    public async Task ACredentialThatCouldNotBeRead_IsRefusedWithoutSendingTheOperatorToSetTheId(string path, ModuleCredentialFailure failure)
    {
        // The lookup returning none for one path's purpose only, with the reason the credential service gives. Refused as
        // aud-a2v's theory has it - a preflight while nothing is written, a write after Set-Place, inside the partial - the
        // message opening with the clause for that reason and never naming the ID to set; one warning, the path's line.
        var cityStateAndCountry = path.StartsWith("City/State/Country", StringComparison.Ordinal);
        var preflight = path.EndsWith("preflight", StringComparison.Ordinal);
        if (!cityStateAndCountry)
            SyncedListTakingTheRoomOnPrem();
        var logger = new CapturingLogger();
        var service = new Service(_h, logger)
        {
            CredentialFailureFor = path switch
            {
                "City/State/Country preflight" => "Set-ADUser preflight",
                "City/State/Country write" => "Set-ADUser synced attributes",
                "on-prem room-list preflight" => "membership preflight",
                _ => "membership add",
            },
            CredentialFailure = failure,
        };
        var purpose = path switch
        {
            "City/State/Country preflight" => $"Set-ADUser preflight for {Typed}",
            "City/State/Country write" => $"Set-ADUser synced attributes for {Typed}",
            "on-prem room-list preflight" => $"on-prem room-list membership preflight for {Typed}",
            _ => $"on-prem room-list membership add for {Typed}",
        };
        var clause = failure == ModuleCredentialFailure.NotReturned ? NotReturnedClause : UnreadableClause;
        var refusal = path switch
        {
            "on-prem room-list preflight" => $"{clause}, and room list '{List}' is synchronized from on-premises, so the room cannot be added to it.",
            "on-prem room-list add" => $"{clause}. The room could not be added to the on-prem room list.",
            _ => $"{clause}. City/State/Country could not be written.",
        };

        var result = await Finder(service, Room(), city: cityStateAndCountry ? "Dublin" : "");

        Assert.False(result.Success);
        Assert.Equal(!preflight, result.Partial);
        Assert.Equal(purpose, service.CredentialPurposes.Last());
        var step = Assert.Single(result.Steps, s => !s.Success);
        if (preflight)
        {
            Assert.Empty(_h.Writes);
            Assert.Equal(refusal + NothingWritten, result.Message);
            Assert.Equal(result.Message, step.Error);
        }
        else
        {
            Assert.Single(_h.CallsTo("Set-Place"));
            Assert.Empty(_h.CallsTo(cityStateAndCountry ? "Set-ADUser" : "Set-ADGroup"));
            Assert.Equal(refusal, step.Error);
            Assert.Contains(refusal, result.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("Nothing was written", result.Message, StringComparison.Ordinal);
        }
        Assert.DoesNotContain("set the AD Delinea Secret ID", result.Message, StringComparison.Ordinal);
        AssertLoggedOnce(logger, path, $"no AD credential was returned for {purpose}: {refusal}");
        Assert.Single(logger.Entries, e => e.Message.Contains(refusal, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("no secret ID")]
    [InlineData("a secret ID Delinea returns no credential for")]
    [InlineData("an unreadable module configuration")]
    public async Task TheCredentialServicesOwnAnswer_ChoosesTheWords(string configured)
    {
        // Through a real credential service and the service's own lookup - neither seam - so the words come from what the
        // credential service found: the City/State/Country preflight refuses, nothing written, in that reason's clause.
        // Offline: its Delinea service has no bootstrap credential, so it refuses before any request.
        var moduleConfig = _h.ModuleConfig;
        switch (configured)
        {
            case "no secret ID":
                break;
            case "a secret ID Delinea returns no credential for":
                var values = _h.ModuleConfig.GetModuleConfig("ConferenceRooms");
                values["DelineaSecretId"] = "4242";
                _h.ModuleConfig.SaveModuleConfig("ConferenceRooms", values);
                break;
            case "an unreadable module configuration":
                moduleConfig = new ModuleConfigService(new ModuleCatalog(), ModuleCredentialLookupTests.Environment(_h.Dir),
                    new ModuleConfigRepository(new UnreadableModuleConfigStore()), NullLogger<ModuleConfigService>.Instance);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(configured), configured, null);
        }
        using var offline = new OfflineCredentials(_h.Dir, _h.Config, moduleConfig);
        var service = new Service(_h, credentials: offline.Service);

        var result = await Finder(service, Room(), city: "Dublin");

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Empty(_h.Writes);
        Assert.Equal(0, service.CredentialRequests);
        Assert.Equal(0, offline.RequestsSent);
        var clause = configured switch
        {
            "no secret ID" => NotSetClause,
            "a secret ID Delinea returns no credential for" => NotReturnedClause,
            _ => UnreadableClause,
        };
        Assert.Equal($"{clause}. City/State/Country could not be written.{NothingWritten}", result.Message);
    }
}
