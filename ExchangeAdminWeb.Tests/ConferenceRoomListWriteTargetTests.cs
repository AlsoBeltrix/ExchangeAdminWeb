using System.Collections;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.ConferenceRooms;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a2e: the room list a Room Finder row adds the room to is a GROUP written into, and is checked as a write target
/// (Protected Group Targets, through the shared <see cref="ProtectedPrincipalServicing.ForWriteTarget"/>) - before anything
/// is written, and again immediately before each write into it, on the object that write names.
/// </summary>
/// <remarks>
/// The real <see cref="ConferenceRoomService"/> over <see cref="ExchangeWriteHarness"/>, with the harness's REAL
/// protected-principal service and servicer grants: rules are saved through the store (a Group Targets row in the form the
/// admin page's validator produces, AC7's through-the-store shape) and read back by the check itself, never handed to it.
/// The page and the bulk processor are driven for real where the servicer exemption and its audit are at stake.
/// </remarks>
public sealed class ConferenceRoomListWriteTargetTests : IDisposable
{
    private const string Typed = "room1@contoso.test";
    private const string Building = "Building 1";
    private static readonly Guid Checked = Guid.Parse("0f0f0f0f-1111-4222-8333-444444444444");
    private const string CheckedObject = "5a5a5a5a-0000-4000-8000-000000000001";
    private const string RoomDn = "CN=Room 1,OU=Rooms,DC=contoso,DC=test";
    private static readonly string List = ConferenceRoomService.BuildRoomListName(Building);
    private static readonly string GroupGuid = ExchangeWriteHarness.DefaultDirectoryGroupGuid;
    private static readonly string GroupDn = ExchangeWriteHarness.DirectoryGroupDn(ExchangeWriteHarness.DefaultDirectoryGroupGuid);
    // A group no rule names: what a test carries as the group the preflight cleared, when the add resolves another.
    private const string OtherGroupGuid = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string ServicerSid = "S-1-5-21-1-2-3-4001";
    private const string NothingWritten = " Nothing was written.";

    // Exchange Online's refusal of a cloud add to a list synchronized from on-premises.
    private const string OnPremMastered =
        "The operation on the group failed because it's out of the current user's write scope. The action can't be "
        + "performed on the object because the object is being synchronized from your on-premises organization.";

    private readonly ExchangeWriteHarness _h = new();
    private readonly ActivityProgressService _progress = new();

    public ConferenceRoomListWriteTargetTests()
    {
        _h.AddMailbox(Typed, Checked, recipientTypeDetails: "RoomMailbox");
        _h.DirectoryObjects[CheckedObject] = new Hashtable
        {
            ["ObjectGUID"] = Guid.Parse(CheckedObject),
            ["DistinguishedName"] = RoomDn,
        };
    }

    public void Dispose()
    {
        _progress.Dispose();
        _h.Dispose();
    }

    /// <summary>The room service with its credential and directory runspace replaced by the harness.</summary>
    private sealed class Service : ConferenceRoomService
    {
        private readonly ExchangeWriteHarness _h;

        // The protection services as the container supplies them: the harness's, unless a test hands others - or none.
        public Service(ExchangeWriteHarness h, ILogger<ConferenceRoomService>? logger = null,
            ProtectedPrincipalService? protectedPrincipals = null, bool withoutProtection = false)
            : base(h.Pool, null!, h.ModuleConfig, null!, h.Trace, h.Config, logger ?? NullLogger<ConferenceRoomService>.Instance,
                withoutProtection ? null : protectedPrincipals ?? h.ProtectedPrincipals,
                withoutProtection ? null : h.Servicers) => _h = h;

        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose) =>
            Task.FromResult<(string username, string password, string domain)?>(("svc", "secret", "CONTOSO"));

        protected override Runspace OpenDirectoryRunspace() => _h.OpenDirectoryRunspace();
    }

    private static ValidatedMailbox Room() => ExchangeWriteHarness.Validated(Checked, CheckedObject);

    private static Task<RoomOperationResult> Apply(ConferenceRoomService service, ClaimsPrincipal? user = null)
        => service.SetRoomMetadataAndListAsync(Typed, Room(), user, "", Building, 8, "2", "Second", "TV", "Polycom", "", "", "GMT Standard Time");

    private void SyncedListTheRoomIsNotOnInExchange()
    {
        _h.ListSyncState[List] = true;
        _h.Failures["Add-DistributionGroupMember"] = OnPremMastered;
    }

    private void NoListYet()
    {
        _h.MissingLists.Add(List);
        _h.MissingLists.Add(ConferenceRoomService.BuildLegacyRoomListName(Building));
    }

    private void Protect(ProtectedPrincipalConfig rules) => _h.ProtectedPrincipals.SaveConfig(rules);

    // The list's on-prem group protected as a write target, in the stored form the admin page's validator produces for a
    // Protected Group Targets entry ("objectGUID|DN") - the value its ADD decision hands the save, never a hand-built row.
    private void ProtectTheGroupAsATarget(string groupGuid, string groupDn)
    {
        var match = new ADSearchResult($"Group {groupGuid}", groupDn, $"Group {groupGuid}", null, null, "Group", ObjectGuid: groupGuid);
        var decision = ProtectedPrincipalEntryValidator.Decide(
            [], groupDn, "GroupTarget", new DirectoryValidationResult(DirectoryLookupOutcome.Found, match));
        Assert.True(decision.Accepted, decision.ErrorMessage);
        Protect(new ProtectedPrincipalConfig { GroupTargets = [decision.ValueToAdd!] });
    }

    private void GrantServicing(string sid)
    {
        // Something under the module's own key too, so the store counts as configured (ppsvc-1).
        new SectionAccessRepository(TestConfigStore.Create(_h.Dir)).SaveAll(new Dictionary<string, string[]>
        {
            ["ConferenceRooms"] = ["AllUsers"],
            [ProtectedPrincipalServicerService.SectionKeyFor("ConferenceRooms")] = [sid],
        });
    }

    private static ClaimsPrincipal UserIn(string groupSid) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, @"CONTOSO\operator"), new Claim(ClaimTypes.Role, groupSid)], "TestAuth"));

    private static ConferenceRoomService.RoomListTarget TheCloudList() =>
        new(List, Exists: true, IsLegacy: false, IsDirSynced: false, ExchangeWriteHarness.DefaultListAddress, ExchangeWriteHarness.DefaultListGuid);

    private static ConferenceRoomService.RoomListTarget TheSyncedList(ResolvedDirectoryPrincipal? cleared) =>
        new(List, Exists: true, IsLegacy: false, IsDirSynced: true, ExchangeWriteHarness.DefaultListAddress, ExchangeWriteHarness.DefaultListGuid,
            DirectoryGroup: cleared);

    private static void AssertRefusedBeforeAnyWrite(RoomOperationResult result, ExchangeWriteHarness h, string because)
    {
        Assert.False(result.Success);
        Assert.False(result.Partial);
        Assert.Contains($"'{List}'", result.Message, StringComparison.Ordinal);
        Assert.Contains(because, result.Message, StringComparison.Ordinal);
        Assert.EndsWith(NothingWritten, result.Message, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(result.Message, "Nothing was written."));
        Assert.Contains(result.Steps, s => s.Stage == "Room list preflight" && !s.Success);
        Assert.Empty(h.Writes);
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private const string ProtectedGroup = "is a protected group";

    // ---- before anything is written: the synced list's on-prem group ------------------------------------------------

    [Fact]
    public async Task ASyncedRoomListWhoseGroupIsAProtectedGroupTarget_IsRefusedBeforeAnyWrite()
    {
        // The finding. The list is synchronized from on-premises, the room not on it in Exchange, and its on-prem group -
        // the object the on-prem add writes into - is listed under Protected Group Targets. Refused before Set-Place: the
        // fail-closed answer to whether the rest of the row may go ahead without its room list. Logged once where decided.
        SyncedListTheRoomIsNotOnInExchange();
        ProtectTheGroupAsATarget(GroupGuid, GroupDn);
        var logger = new CapturingLogger();

        var result = await Apply(new Service(_h, logger));

        AssertRefusedBeforeAnyWrite(result, _h, ProtectedGroup);
        var refusal = result.Message[..^NothingWritten.Length];
        var logged = Assert.Single(logger.Entries, e => e.Message.Contains(refusal, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Equal($"On-prem room-list preflight for {Typed} on {List} refused the row; nothing was written: {refusal}", logged.Message);
    }

    [Fact]
    public async Task AProtectedGroupTarget_IsMatchedByTheGroupsObjectGuid_NotItsName_AfterARenameOrMove()
    {
        // By immutable identifier: the row was saved when the group had another DN; its objectGUID - what the check is run
        // on, the identity the write names - still matches.
        SyncedListTheRoomIsNotOnInExchange();
        ProtectTheGroupAsATarget(GroupGuid, "CN=Old Name,OU=Retired,DC=contoso,DC=test");

        var result = await Apply(new Service(_h));

        AssertRefusedBeforeAnyWrite(result, _h, ProtectedGroup);
    }

    [Fact]
    public async Task ASyncedRoomListGroupMatchingAProtectedPattern_IsRefusedBeforeAnyWrite()
    {
        // AC9 / pgwt-2 for this module: the pattern rules key on the sAMAccountName, which a DN-only snapshot does not
        // carry - the rule a bare DN skips in silence.
        SyncedListTheRoomIsNotOnInExchange();
        _h.DirectoryGroupSamAccountNames[GroupGuid] = "rooms-building1";
        Protect(new ProtectedPrincipalConfig { SamAccountNamePatterns = ["rooms-*"] });

        var result = await Apply(new Service(_h));

        AssertRefusedBeforeAnyWrite(result, _h, ProtectedGroup);
    }

    [Fact]
    public async Task ASyncedRoomListGroupInsideAProtectedOu_IsRefusedBeforeAnyWrite()
    {
        SyncedListTheRoomIsNotOnInExchange();
        Protect(new ProtectedPrincipalConfig { OrganizationalUnits = ["OU=Groups," + ExchangeWriteHarness.DirectoryDomainDn] });

        var result = await Apply(new Service(_h));

        AssertRefusedBeforeAnyWrite(result, _h, ProtectedGroup);
    }

    [Fact]
    public async Task ASyncedRoomListGroupReadBackWithoutASamAccountName_IsRefusedBeforeAnyWrite()
    {
        // Fail closed when the lookup data a rule needs is missing: a group its own domain answered without a
        // sAMAccountName cannot be asked about the pattern rules, so it is not written to.
        SyncedListTheRoomIsNotOnInExchange();
        _h.DirectoryGroupSamAccountNames[GroupGuid] = null;

        var result = await Apply(new Service(_h));

        AssertRefusedBeforeAnyWrite(result, _h, "without a sAMAccountName");
    }

    [Fact]
    public async Task AGroupListedOnlyToProtectItsMembers_IsStillWritten()
    {
        // AC8, the anti-lockout criterion: the Groups list means "everyone inside is protected", not "this group may not be
        // written". A room list listed there - and a rule naming another group as a target - leaves this row untouched.
        SyncedListTheRoomIsNotOnInExchange();
        Protect(new ProtectedPrincipalConfig
        {
            Groups = [GroupDn],
            GroupTargets = [ProtectedGroupTargetEntry.Format(OtherGroupGuid, ExchangeWriteHarness.DirectoryGroupDn(OtherGroupGuid))],
        });

        var result = await Apply(new Service(_h));

        Assert.True(result.Success, result.Message);
        Assert.Equal(GroupGuid, ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Set-ADGroup")), "Identity"));
        Assert.Null(result.RoomListServicedNote);
    }

    // ---- before anything is written: a list Exchange Online holds -----------------------------------------------------

    [Fact]
    public async Task ACloudRoomListProtectedByItsAddress_IsRefusedBeforeAnyWrite()
    {
        // A cloud list has no on-premises object, so only a protected user row can name it - by its address here.
        Protect(new ProtectedPrincipalConfig { Users = [ExchangeWriteHarness.DefaultListAddress] });

        var result = await Apply(new Service(_h));

        AssertRefusedBeforeAnyWrite(result, _h, ProtectedGroup);
    }

    [Fact]
    public async Task ACloudRoomListProtectedByItsEntraObjectId_IsRefusedBeforeAnyWrite()
    {
        _h.ListExternalIds[List] = "7e7e7e7e-0000-4000-8000-000000000007";
        Protect(new ProtectedPrincipalConfig { Users = ["7e7e7e7e-0000-4000-8000-000000000007"] });

        var result = await Apply(new Service(_h));

        AssertRefusedBeforeAnyWrite(result, _h, ProtectedGroup);
    }

    [Fact]
    public async Task ACloudRoomListNoRuleNames_IsWritten()
    {
        // The control for the two above: rules naming other objects - another address, another list's Entra id, a target
        // group - leave the cloud add as it was.
        _h.ListExternalIds[List] = "7e7e7e7e-0000-4000-8000-000000000007";
        Protect(new ProtectedPrincipalConfig
        {
            Users = ["someone-else@contoso.test", "7e7e7e7e-0000-4000-8000-000000000008"],
            GroupTargets = [ProtectedGroupTargetEntry.Format(GroupGuid, GroupDn)],
        });

        var result = await Apply(new Service(_h));

        Assert.True(result.Success, result.Message);
        Assert.Single(_h.CallsTo("Add-DistributionGroupMember"));
    }

    // ---- fail closed ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task WithoutTheProtectedPrincipalServices_ARoomListIsRefusedBeforeAnyWrite()
    {
        // A host that supplies no protection services refuses every list write rather than skipping the check.
        var result = await Apply(new Service(_h, withoutProtection: true));

        AssertRefusedBeforeAnyWrite(result, _h, "could not be checked against Protected Group Targets");
        Assert.Contains("Protection check unavailable", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProtectionStoreThatCannotBeRead_RefusesBeforeAnyWrite()
    {
        // The check that cannot be made refuses with its reason; it is never read as "not protected".
        var unreadable = new UnreadableRules(_h);

        var result = await Apply(new Service(_h, protectedPrincipals: unreadable));

        AssertRefusedBeforeAnyWrite(result, _h, "could not be checked against Protected Group Targets");
        Assert.Contains(UnreadableRules.Reason, result.Message, StringComparison.Ordinal);
    }

    // ---- again, immediately before each write into the list --------------------------------------------------------

    [Fact]
    public async Task TheCloudAdd_ChecksTheListImmediatelyBeforeItWrites()
    {
        // The add, called as the row calls it after Set-Place, on a list the check refuses - as when a rule was saved after
        // the preflight passed. Not added; no Add-DistributionGroupMember; refused in the add's step, logged once, and the
        // refusal does not claim nothing was written (the row's other writes have committed by then).
        Protect(new ProtectedPrincipalConfig { Users = [ExchangeWriteHarness.DefaultListAddress] });
        var logger = new CapturingLogger();

        var result = await new Service(_h, logger).AddToRoomListAsync(Room(), Typed, Typed, TheCloudList(), actingUser: null);

        Assert.False(result.Success);
        Assert.Empty(_h.CallsTo("Add-DistributionGroupMember"));
        Assert.Single(_h.CallsTo("Get-DistributionGroupMember"));
        var step = Assert.Single(result.Steps, s => s.Stage == "Add to room list");
        Assert.False(step.Success);
        Assert.Equal(result.Message, step.Error);
        Assert.Contains(ProtectedGroup, result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was written", result.Message, StringComparison.Ordinal);
        var logged = Assert.Single(logger.Entries, e => e.Message.Contains(result.Message, StringComparison.Ordinal));
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.Equal($"Room-list add for {Typed} on {List} refused before its write: {result.Message}", logged.Message);
    }

    [Fact]
    public async Task TheOnPremAdd_ChecksTheGroupItResolves_ImmediatelyBeforeItWrites()
    {
        // The preflight cleared one group; by the time of the add the list's address finds another, which is protected.
        // The cloud add is checked against the group the preflight cleared, and Exchange refuses it; the on-prem write is
        // checked against the group it resolves and would name - and is not made.
        SyncedListTheRoomIsNotOnInExchange();
        ProtectTheGroupAsATarget(GroupGuid, GroupDn);
        var logger = new CapturingLogger();

        var result = await new Service(_h, logger).AddToRoomListAsync(
            Room(), Typed, Typed, TheSyncedList(ExchangeWriteHarness.DirectoryGroupAsChecked(OtherGroupGuid)), actingUser: null);

        Assert.False(result.Success);
        Assert.Single(_h.CallsTo("Add-DistributionGroupMember"));
        Assert.Empty(_h.CallsTo("Set-ADGroup"));
        Assert.Null(result.WrittenDirectoryObject);
        var step = Assert.Single(result.Steps, s => s.Stage == $"Add to '{List}' (on-prem)");
        Assert.False(step.Success);
        Assert.Contains(ProtectedGroup, step.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing was written", step.Error!, StringComparison.Ordinal);
        var logged = Assert.Single(logger.Entries, e => e.Message.Contains(step.Error!, StringComparison.Ordinal));
        Assert.Equal($"On-prem room-list add for {Typed} on {List} refused before its write: {step.Error}", logged.Message);
    }

    [Fact]
    public async Task ASyncedListsCloudAdd_WithNoGroupCheckedBeforeIt_IsNotMade()
    {
        // No group was checked for the list - the room was on it when the row was checked - so the cloud add has nothing it
        // was checked against, and is not made: never unchecked. Nothing on-prem is asked either.
        SyncedListTheRoomIsNotOnInExchange();

        var result = await new Service(_h).AddToRoomListAsync(Room(), Typed, Typed, TheSyncedList(cleared: null), actingUser: null);

        Assert.False(result.Success);
        Assert.Equal(ConferenceRoomService.RoomListNotCheckedRefusal(List), result.Message);
        Assert.Empty(_h.CallsTo("Add-DistributionGroupMember"));
        Assert.Empty(_h.CallsTo("Get-ADGroup"));
        Assert.Empty(_h.CallsTo("Set-ADGroup"));
    }

    [Fact]
    public async Task ARoomTakenOffItsSyncedListAfterTheRowWasChecked_IsNotAddedUnchecked()
    {
        // The race, through the row: the preflight's membership read finds the room on its synced list (nothing on-prem
        // needed or asked, aud-a2f), the add's no longer does. The row is the partial it reports; the list is not written.
        _h.ListSyncState[List] = true;
        _h.ListMembers.Add(Typed);
        _h.ListMembersClearedAfterSetPlace = true;

        var result = await Apply(new Service(_h));

        Assert.False(result.Success);
        Assert.True(result.Partial);
        Assert.Contains(ConferenceRoomService.RoomListNotCheckedRefusal(List), result.Message, StringComparison.Ordinal);
        Assert.Equal(["Set-Place", "Set-MailboxRegionalConfiguration", "Set-MailboxCalendarConfiguration"], _h.Writes);
        Assert.Empty(_h.CallsTo("Get-ADGroup"));
    }

    [Fact]
    public async Task AListTheRowCreates_IsCheckedBeforeTheRoomIsAddedToIt()
    {
        // A list that does not exist yet cannot be checked before the writes; the add checks the list it created, as the
        // creation returned it, before adding the room. A rule naming its new address refuses the add.
        NoListYet();
        _h.CreatedListAddress = "building1-rooms@contoso.test";
        Protect(new ProtectedPrincipalConfig { Users = ["building1-rooms@contoso.test"] });

        var result = await Apply(new Service(_h));

        Assert.False(result.Success);
        Assert.True(result.Partial);
        Assert.Contains(ProtectedGroup, result.Message, StringComparison.Ordinal);
        Assert.Single(_h.CallsTo("New-DistributionGroup"));
        Assert.Empty(_h.CallsTo("Add-DistributionGroupMember"));
    }

    // ---- the servicer exemption --------------------------------------------------------------------------------------

    [Fact]
    public async Task AnAuthorisedServicer_MayAddARoomToAProtectedList_AndTheResultCarriesTheNote()
    {
        // ForWriteTarget's exemption, as elsewhere: the module's servicer grant lets the operator past the protected list,
        // and the result carries the note naming the authorising group and the rule overridden, for the audit.
        SyncedListTheRoomIsNotOnInExchange();
        ProtectTheGroupAsATarget(GroupGuid, GroupDn);
        GrantServicing(ServicerSid);

        var result = await Apply(new Service(_h), UserIn(ServicerSid));

        Assert.True(result.Success, result.Message);
        Assert.Equal(GroupGuid, ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Set-ADGroup")), "Identity"));
        var note = result.RoomListServicedNote;
        Assert.NotNull(note);
        Assert.Contains("(write target)", note, StringComparison.Ordinal);
        Assert.Contains($"Target:{GroupDn}", note, StringComparison.Ordinal);
        Assert.Contains(ServicerSid, note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SomeoneOutsideTheServicerGroup_IsRefusedBeforeAnyWrite()
    {
        SyncedListTheRoomIsNotOnInExchange();
        ProtectTheGroupAsATarget(GroupGuid, GroupDn);
        GrantServicing(ServicerSid);

        var result = await Apply(new Service(_h), UserIn("S-1-5-21-1-2-3-9999"));

        AssertRefusedBeforeAnyWrite(result, _h, ProtectedGroup);
        Assert.Null(result.RoomListServicedNote);
    }

    [Fact]
    public async Task ThePage_PassesTheOperator_AndRecordsTheListsServicerNoteInTheAudit()
    {
        // The page's Room Finder apply, driven for real as a servicer: the operator reaches the list's check, and the
        // audit row's protectedPrincipalServiced carries the list's note - in extra, the one place a succeeding row keeps
        // it - beside the room gate's (none here: the room is not protected).
        SyncedListTheRoomIsNotOnInExchange();
        ProtectTheGroupAsATarget(GroupGuid, GroupDn);
        GrantServicing(ServicerSid);
        var page = BuildPage(new Service(_h), UserIn(ServicerSid));

        await (Task)typeof(Page).GetMethod("SetupSingleRoom", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!;

        var row = Assert.Single(_h.AuditRows(), r => r.GetProperty("action").GetString() == "ConferenceRooms_SetMetadata");
        Assert.Equal("Success", row.GetProperty("result").GetString());
        Assert.Single(_h.CallsTo("Set-ADGroup"));
        var note = row.GetProperty(ProtectedPrincipalServicing.AuditKey).GetString();
        Assert.NotNull(note);
        Assert.Contains("(write target)", note, StringComparison.Ordinal);
        Assert.Contains(ServicerSid, note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABulkRow_NeverServicesAProtectedList()
    {
        // Off-circuit, under nobody: the servicer grant exists and still does not apply. Refused before any write.
        SyncedListTheRoomIsNotOnInExchange();
        ProtectTheGroupAsATarget(GroupGuid, GroupDn);
        GrantServicing(ServicerSid);
        _h.Enablement.SaveEnablement(new Dictionary<string, bool>
        {
            [ExoConnectionPool.ConfigModuleKey] = true,
            [ConferenceRoomBulkProcessor.ModuleName] = true,
        });
        var processor = new ConferenceRoomBulkProcessor(new Service(_h), Gate(), SectionAccess(), _h.Enablement, _h.Trace, _h.Audit,
            Substitute.For<EmailService>(_h.Config, NullLogger<EmailService>.Instance), NullLogger<ConferenceRoomBulkProcessor>.Instance);

        var outcome = await processor.ProcessRowAsync(FinderJob(), 0, CancellationToken.None);

        Assert.Equal(BulkJobRowStatus.Failed, outcome.Status);
        Assert.Contains(ProtectedGroup, outcome.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
        var row = Assert.Single(_h.AuditRows(), r => r.GetProperty("action").GetString() == "ConferenceRooms_SetMetadata_Bulk");
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.False(row.TryGetProperty(ProtectedPrincipalServicing.AuditKey, out _), row.ToString());
    }

    // ---- the harness ---------------------------------------------------------------------------------------------------

    private sealed class CapturingLogger : ILogger<ConferenceRoomService>
    {
        public System.Collections.Concurrent.ConcurrentQueue<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, formatter(state, exception)));
    }

    /// <summary>A protected-principal service whose rules cannot be read: every check fails, with this reason.</summary>
    private sealed class UnreadableRules(ExchangeWriteHarness h)
        : ProtectedPrincipalService(Environment(h), h.Config, h.ModuleConfig, TestConfigStore.CreateProtectedPrincipal(h.Dir), Delinea(h),
            NullLogger<ProtectedPrincipalService>.Instance)
    {
        public const string Reason = "Protected-principals configuration is corrupt. Contact your administrator.";

        public override (ProtectedPrincipalConfig? config, string[] legacyExclusions, string? error) LoadEffectiveConfig() => (null, [], Reason);
    }

    /// <summary>The room gate's check, resolving every target to the synced room, unprotected - the room is not at issue here.</summary>
    private sealed class RoomResolvingCheck(ExchangeWriteHarness h)
        : ProtectedPrincipalService(Environment(h), h.Config, h.ModuleConfig, TestConfigStore.CreateProtectedPrincipal(h.Dir), Delinea(h),
            NullLogger<ProtectedPrincipalService>.Instance)
    {
        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveExchangeTargetAsync(string identity) =>
            Task.FromResult<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)>((
                new ResolvedDirectoryPrincipal("Test", "Room 1", Typed, null, Typed, null, CheckedObject, null, ExchangeGuid: Checked),
                ResolutionStatus.Resolved));

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target) =>
            Task.FromResult(ProtectedPrincipalResult.NotProtected());
    }

    private static IWebHostEnvironment Environment(ExchangeWriteHarness h)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(h.Dir);
        return env;
    }

    private static IHttpClientFactory HttpClients()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        return factory;
    }

    private static DelineaService Delinea(ExchangeWriteHarness h)
    {
        var extLog = new ExtendedLogService(h.Config, Environment(h), TestConfigStore.CreateAppSettings(h.Dir), NullLogger<ExtendedLogService>.Instance);
        return new DelineaService(HttpClients(), h.Config, NullLogger<DelineaService>.Instance, extLog, h.Trace);
    }

    private SectionAccessService SectionAccess() =>
        new(_h.Config, NullLogger<SectionAccessService>.Instance, Environment(_h), new ModuleCatalog(),
            new SectionAccessRepository(TestConfigStore.Create(_h.Dir)));

    private ConferenceRoomProtectionGate Gate() =>
        new(new RoomResolvingCheck(_h), _h.Servicers, NullLogger<ConferenceRoomProtectionGate>.Instance);

    private Page BuildPage(ConferenceRoomService service, ClaimsPrincipal user)
    {
        var page = new Page();
        SetProperty(page, "RoomService", service);
        SetProperty(page, "Audit", _h.Audit);
        SetProperty(page, "Email", Substitute.For<EmailService>(_h.Config, NullLogger<EmailService>.Instance));
        SetProperty(page, "ServiceNow", new ServiceNowService(_h.Config, NullLogger<ServiceNowService>.Instance, HttpClients()));
        SetProperty(page, "AuthStateProvider", Authentication(user));
        SetProperty(page, "AuthorizationService", Authorization());
        SetProperty(page, "OpTrace", _h.Trace);
        SetProperty(page, "Logger", NullLogger<Page>.Instance);
        SetProperty(page, "ProtectionGate", Gate());
        SetProperty(page, "Progress", _progress);

        SetField(page, "currentUser", @"CONTOSO\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "roomEmail", Typed);
        SetField(page, "ticketNumber", "INC100");
        SetField(page, "city", "");
        SetField(page, "building", Building);
        SetField(page, "capacity", 8);
        SetField(page, "floor", "2");
        SetField(page, "floorLabel", "Second");
        SetField(page, "displayDevice", "TV");
        SetField(page, "videoDevice", "Polycom");
        SetField(page, "timezone", "GMT Standard Time");
        return page;
    }

    private static BulkJob FinderJob()
    {
        var row = new FinderCsvRow
        {
            Email = Typed,
            City = "",
            Building = Building,
            Capacity = 8,
            Floor = "2",
            FloorLabel = "Second",
            DisplayDeviceName = "TV",
            VideoDeviceName = "Polycom",
            TimeZone = "GMT Standard Time",
        };
        string[] groups = ["AllUsers"];
        var snapshot = new JobAuthorizationSnapshot { Section = "ConferenceRooms", RoleClaims = groups, AuthorizedGroups = groups };
        return new BulkJob
        {
            Id = "j1",
            ModuleId = "ConferenceRooms",
            JobType = ConferenceRoomJobPayload.FinderJobType,
            Status = BulkJobStatus.Running,
            SubmittedBy = "jdoe",
            SubmittedIp = "10.0.0.9",
            Ticket = "INC1",
            AuthSnapshotJson = snapshot.ToJson(),
            PayloadJson = JsonSerializer.Serialize(new ConferenceRoomJobPayload { Kind = ConferenceRoomJobPayload.FinderJobType, FinderRows = [row] }),
            SubmittedAtUtc = DateTime.UtcNow,
        };
    }

    private static AuthenticationStateProvider Authentication(ClaimsPrincipal user)
    {
        var provider = Substitute.For<AuthenticationStateProvider>();
        provider.GetAuthenticationStateAsync().Returns(Task.FromResult(new AuthenticationState(user)));
        return provider;
    }

    private static IAuthorizationService Authorization()
    {
        var service = Substitute.For<IAuthorizationService>();
        service.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        return service;
    }

    private static void SetProperty(Page page, string name, object value) =>
        typeof(Page).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static void SetField(Page page, string name, object? value) =>
        typeof(Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
}
