using System.Collections;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a2z: a synced room list's on-prem group is the group that carries the list's address. The forest's global catalog
/// finds it by that address (aud-a2k) and its own domain re-reads it (aud-a2j); the re-read now confirms the address as well
/// as the objectGUID and DN, so a catalog behind an address move cannot bind the list to the group the address has left.
/// </summary>
/// <remarks>
/// The real <see cref="ConferenceRoomService"/> over <see cref="ExchangeWriteHarness"/>: the stand-in catalog search matches
/// the group's mail as the harness holds it, and the stand-in re-read in its own domain answers
/// <see cref="ExchangeWriteHarness.DirectoryGroupMailInItsDomain"/> where a test moves it - the lagging copy and the domain
/// that moved on.
/// </remarks>
public sealed class ConferenceRoomListGroupAddressTests : IDisposable
{
    private const string Typed = "room1@contoso.test";
    private const string Building = "Building 1";
    private static readonly Guid Checked = Guid.Parse("0f0f0f0f-1111-4222-8333-444444444444");
    private const string CheckedObject = "5a5a5a5a-0000-4000-8000-000000000001";
    private const string RoomDn = "CN=Room 1,OU=Rooms,DC=contoso,DC=test";
    private static readonly string List = ConferenceRoomService.BuildRoomListName(Building);
    private static readonly string GroupGuid = ExchangeWriteHarness.DefaultDirectoryGroupGuid;
    private const string Moved = "moved.elsewhere@contoso.test";

    // Exchange Online's refusal of a cloud add to a list synchronized from on-premises.
    private const string OnPremMastered =
        "The operation on the group failed because it's out of the current user's write scope. The action can't be "
        + "performed on the object because the object is being synchronized from your on-premises organization.";

    private readonly ExchangeWriteHarness _h = new();

    public ConferenceRoomListGroupAddressTests()
    {
        _h.AddMailbox(Typed, Checked, recipientTypeDetails: "RoomMailbox");
        _h.DirectoryObjects[CheckedObject] = new Hashtable
        {
            ["ObjectGUID"] = Guid.Parse(CheckedObject),
            ["DistinguishedName"] = RoomDn,
        };
        // A synced list the room is not on in Exchange: the preflight resolves its on-prem group, and the add writes there.
        _h.ListSyncState[List] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;
    }

    public void Dispose() => _h.Dispose();

    /// <summary>The room service with its credential and directory runspace replaced by the harness.</summary>
    private sealed class Service(ExchangeWriteHarness h, ILogger<ConferenceRoomService>? logger = null)
        : ConferenceRoomService(h.Pool, null!, h.ModuleConfig, null!, h.Trace, h.Config, logger ?? NullLogger<ConferenceRoomService>.Instance)
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
        service.SetRoomMetadataAndListAsync(Typed, Room(), "", Building, 8, "2", "Second", "TV", "Polycom", "", "", "GMT Standard Time");

    // The Get-ADGroup reads of the group's member attribute: the membership reads, before and after the write.
    private int MembershipReads() => _h.CallsTo("Get-ADGroup").Count(c =>
        ExchangeWriteHarness.Bound(c, "Properties") is IEnumerable properties
        && properties.Cast<object>().Any(p => string.Equals(p?.ToString(), "member", StringComparison.OrdinalIgnoreCase)));

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public async Task AGroupWhoseOwnDomainNoLongerCarriesTheListsAddress_IsRefusedBeforeAnyWrite()
    {
        // The finding. The catalog still matches the group by the list's address; its own domain has moved the address
        // elsewhere. The objectGUID and DN check out - and the row is refused before any write, naming the list, the address
        // searched and what the domain holds now, logged once where it is decided.
        _h.DirectoryGroupMailInItsDomain[GroupGuid] = Moved;
        var logger = new CapturingLogger();

        var result = await Apply(new Service(_h, logger));

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Contains($"'{List}'", result.Message, StringComparison.Ordinal);
        Assert.Contains($"'{ExchangeWriteHarness.DefaultListAddress}'", result.Message, StringComparison.Ordinal);
        Assert.Contains($"'{Moved}'", result.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Nothing was written.", result.Message, StringComparison.Ordinal);
        Assert.Equal(1, Occurrences(result.Message, "Nothing was written."));
        Assert.Empty(_h.Writes);
        Assert.Equal(0, MembershipReads());
        var refusal = result.Message[..^" Nothing was written.".Length];
        var logged = Assert.Single(logger.Entries, e => e.Message.Contains(refusal, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Equal($"On-prem room-list preflight for {Typed} on {List} refused the row; nothing was written: {refusal}", logged.Message);
    }

    [Fact]
    public async Task AGroupWhoseOwnDomainHoldsNoMail_IsRefusedBeforeAnyWrite()
    {
        _h.DirectoryGroupMailInItsDomain[GroupGuid] = null;

        var result = await Apply(new Service(_h));

        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Contains("its mail there is not set", result.Message, StringComparison.Ordinal);
        Assert.EndsWith(" Nothing was written.", result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
    }

    [Fact]
    public async Task TheOnPremAdd_DoesNotAddTheRoomToAGroupTheListsAddressHasLeft()
    {
        // The add, called as the row calls it after Set-Place - the address moved after the preflight. Exchange refuses the
        // cloud add for the synced list, the add resolves the group again, and its re-read refuses it: the room is not added
        // on-prem, and the refusal, coming after the row's other writes, does not claim nothing was written.
        _h.DirectoryGroupMailInItsDomain[GroupGuid] = Moved;
        var list = new ConferenceRoomService.RoomListTarget(List, Exists: true, IsLegacy: false, IsDirSynced: true,
            ExchangeWriteHarness.DefaultListAddress, ExchangeWriteHarness.DefaultListGuid);

        var result = await new Service(_h).AddToRoomListAsync(Room(), Typed, Typed, list);

        Assert.False(result.Success);
        Assert.Empty(_h.CallsTo("Set-ADGroup"));
        Assert.Null(result.WrittenDirectoryObject);
        var step = Assert.Single(result.Steps, s => s.Stage == $"Add to '{List}' (on-prem)");
        Assert.False(step.Success);
        Assert.Contains($"'{Moved}'", step.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was written", step.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheListsAddressInAnotherCase_IsStillTheListsGroup()
    {
        // An address is matched ignoring case: Exchange's spelling of the list's address and the group's own differ only in
        // case, and the room is added to the group as before. (The re-read must ask for mail to see it at all.)
        _h.ListAddresses[List] = "Building1.Rooms@Contoso.Test";
        _h.SetDirectoryGroupAttributes(GroupGuid, mail: "building1.rooms@contoso.test", displayName: List, name: List);

        var result = await Apply(new Service(_h));

        Assert.True(result.Success, result.Message);
        Assert.Equal(GroupGuid, ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Set-ADGroup")), "Identity"));
    }
}
