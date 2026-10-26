using System.Collections;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a2ac: only Exchange's affirmative "no such object" means a Building has no room list. An ambiguous identity or a
/// lookup that could not be made refuses the row before any write, where it used to arrive as an empty collection and be
/// read as absence - sending the row to the legacy name, or on as though the building had no list.
/// </summary>
/// <remarks>
/// The real <see cref="ConferenceRoomService"/> over <see cref="ExchangeWriteHarness"/>, whose stand-in
/// <c>Get-DistributionGroup</c> reports a missing list with the <c>ObjectNotFound</c> category Exchange uses, refuses a
/// name in <see cref="ExchangeWriteHarness.AmbiguousLists"/> as matching several recipients, and answers a name in
/// <see cref="ExchangeWriteHarness.ListsAnsweringTwice"/> twice.
/// </remarks>
public sealed class ConferenceRoomListLookupTests : IDisposable
{
    private const string Typed = "room1@contoso.test";
    private const string Building = "Building 1";
    private static readonly Guid Checked = Guid.Parse("0f0f0f0f-1111-4222-8333-444444444444");
    private const string CheckedObject = "5a5a5a5a-0000-4000-8000-000000000001";
    private static readonly string List = ConferenceRoomService.BuildRoomListName(Building);
    private static readonly string Legacy = ConferenceRoomService.BuildLegacyRoomListName(Building);

    private readonly ExchangeWriteHarness _h = new();

    public ConferenceRoomListLookupTests()
    {
        _h.AddMailbox(Typed, Checked, recipientTypeDetails: "RoomMailbox");
        _h.DirectoryObjects[CheckedObject] = new Hashtable
        {
            ["ObjectGUID"] = Guid.Parse(CheckedObject),
            ["DistinguishedName"] = "CN=Room 1,OU=Rooms,DC=contoso,DC=test",
        };
    }

    public void Dispose() => _h.Dispose();

    private sealed class Service(ExchangeWriteHarness h, ILogger<ConferenceRoomService>? logger = null)
        : ConferenceRoomService(h.Pool, null!, h.ModuleConfig, null!, h.Trace, h.Config, logger ?? NullLogger<ConferenceRoomService>.Instance,
            h.ProtectedPrincipals, h.Servicers)
    {
        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose, CancellationToken cancellationToken = default) =>
            Task.FromResult<(string username, string password, string domain)?>(("svc", "secret", "CONTOSO"));

        protected override Runspace OpenDirectoryRunspace() => h.OpenDirectoryRunspace();
    }

    private sealed class CapturingLogger : ILogger<ConferenceRoomService>
    {
        public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    private static ValidatedMailbox Room() => ExchangeWriteHarness.Validated(Checked, CheckedObject);

    private static Task<RoomOperationResult> Apply(ConferenceRoomService service) =>
        service.SetRoomMetadataAndListAsync(Typed, Room(), actingUser: null, "", Building, 8, "2", "Second", "TV", "Polycom", "", "", "GMT Standard Time");

    private void AssertRefusedBeforeAnyWrite(RoomOperationResult result)
    {
        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Contains($"'{List}'", result.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Nothing was written.", result.Message, StringComparison.Ordinal);
        Assert.Contains(result.Steps, s => s.Stage == "Room list resolution" && !s.Success);
        Assert.Empty(_h.Writes);
        Assert.Empty(_h.CallsTo("New-DistributionGroup"));
    }

    [Fact]
    public async Task AnAmbiguousRoomListName_IsRefusedBeforeAnyWrite_NotReadAsAMissingList()
    {
        // The finding. Two recipients answer the building's list name, so Exchange refuses the identity. That refusal used
        // to be swallowed: the row went on to the legacy name and then proceeded as though the building had no list. It now
        // refuses before Set-Place, carrying Exchange's own words, and never asks for the legacy name at all.
        _h.AmbiguousLists.Add(List);
        var logger = new CapturingLogger();

        var result = await Apply(new Service(_h, logger));

        AssertRefusedBeforeAnyWrite(result);
        Assert.Contains("matches multiple entries", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(_h.CallsTo("Get-DistributionGroup"), c => Equals(ExchangeWriteHarness.Bound(c, "Identity"), Legacy));
        var logged = Assert.Single(logger.Entries, e => e.Message.Contains("matches multiple entries", StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.StartsWith($"Room-list resolution for {Typed} on {List} refused the row; nothing was written:", logged.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAmbiguousLegacyRoomListName_IsRefusedBeforeAnyWrite()
    {
        // The canonical name is absent, so the search moves on - and the legacy name is the ambiguous one. The row refuses
        // there too, rather than reading it as "no list at all".
        _h.MissingLists.Add(List);
        _h.AmbiguousLists.Add(Legacy);

        var result = await Apply(new Service(_h));

        AssertRefusedBeforeAnyWrite(result);
    }

    [Fact]
    public async Task ARoomListLookupThatCouldNotBeMade_IsRefusedBeforeAnyWrite()
    {
        // Not an ambiguity but any other failure - a throttle, a permission, a dead session. It says nothing about whether
        // the list exists, so it is never absence.
        _h.Failures["Get-DistributionGroup"] = "The operation couldn't be performed because the server was too busy.";

        var result = await Apply(new Service(_h));

        AssertRefusedBeforeAnyWrite(result);
        Assert.Contains("too busy", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARoomListNameAnsweredTwice_IsRefusedBeforeAnyWrite()
    {
        // The defensive half: two answers are not one list, whatever Exchange would really do with such an identity.
        _h.ListsAnsweringTwice.Add(List);

        var result = await Apply(new Service(_h));

        AssertRefusedBeforeAnyWrite(result);
        Assert.Contains("matches 2 recipients", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AConfirmedNotFound_IsStillAbsence_AndTheSearchMovesOnToTheLegacyName()
    {
        // The other direction, and the one the rule must not break: Exchange's affirmative "no such object" is an answer.
        // The canonical name is absent, so the legacy list is found and used - as before.
        _h.MissingLists.Add(List);

        var result = await Apply(new Service(_h));

        Assert.True(result.Success, result.Message);
        Assert.Contains(result.Steps, s => s.Stage == $"Using legacy room list '{Legacy}'" && s.Success);
        Assert.Equal(Legacy, ExchangeWriteHarness.Bound(_h.CallsTo("Get-DistributionGroup")[1], "Identity"));
        Assert.Single(_h.CallsTo("Add-DistributionGroupMember"));
    }

    [Fact]
    public async Task BothNamesConfirmedNotFound_IsABuildingWithNoRoomList()
    {
        // Both affirmatively absent: the building genuinely has no list, which since aud-a2ab refuses the row with that
        // reason - not the resolution refusal above.
        _h.MissingLists.Add(List);
        _h.MissingLists.Add(Legacy);

        var result = await Apply(new Service(_h));

        Assert.False(result.Success);
        Assert.Equal($"{ConferenceRoomService.RoomListDoesNotExistRefusal(List)} Nothing was written.", result.Message);
        Assert.Contains(result.Steps, s => s.Stage == "Room list preflight" && !s.Success);
        Assert.Empty(_h.Writes);
    }

    [Fact]
    public void TheHarness_ReportsAMissingListAsExchangeDoes_AndRefusesAnAmbiguousOne()
    {
        // The stand-in the tests above rest on: a missing list is an ObjectNotFound-category error - what the service takes
        // for an answer - an ambiguous one is an error that is not, and a name answered twice returns two objects.
        _h.MissingLists.Add(List);
        _h.AmbiguousLists.Add(Legacy);
        _h.ListsAnsweringTwice.Add("Building 2 Conference Rooms");
        using var rs = _h.OpenExchangeRunspace();
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.Runspace = rs;

        var missing = Assert.ThrowsAny<System.Management.Automation.RuntimeException>(() => Read(ps, List));
        Assert.True(ExchangeServiceBase.IsNotFound(missing), "a missing list must read as Exchange's affirmative not-found");
        var ambiguous = Assert.ThrowsAny<System.Management.Automation.RuntimeException>(() => Read(ps, Legacy));
        Assert.False(ExchangeServiceBase.IsNotFound(ambiguous), "an ambiguous list must never read as not-found");
        Assert.Contains("matches multiple entries", ambiguous.Message, StringComparison.Ordinal);
        Assert.Equal(2, Read(ps, "Building 2 Conference Rooms").Count);
    }

    private static System.Collections.ObjectModel.Collection<System.Management.Automation.PSObject> Read(
        System.Management.Automation.PowerShell ps, string identity)
    {
        ps.Commands.Clear();
        ps.Streams.Error.Clear();
        ps.AddCommand("Get-DistributionGroup").AddParameter("Identity", identity).AddParameter("ErrorAction", "Stop");
        return ps.Invoke();
    }
}
