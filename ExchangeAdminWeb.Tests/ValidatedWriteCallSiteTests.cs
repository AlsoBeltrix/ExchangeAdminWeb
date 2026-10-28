using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a2d: the page call sites hand each write the mailbox their OWN check validated.
/// </summary>
/// <remarks>
/// Source-level, and only for what the behavioural tests cannot reach: the services' writes are
/// driven for real (<see cref="MailboxPermissionWriteIdentityTests"/> and its siblings), but the
/// pages' handlers have no harness here. The types already rule out the worst regression - no
/// write method takes a string identity any more, so a page cannot pass the typed address again.
/// What the types cannot see is a page passing null (refused, but every write then fails) or the
/// wrong validation's mailbox; this pins each call to the validation it follows. Matched on the
/// arguments, not the formatting.
/// </remarks>
public sealed class ValidatedWriteCallSiteTests
{
    [Theory]
    [InlineData("MailboxPermissions.razor", @"MailboxService\.GetMailboxLocationAsync\(\s*validation\.Mailbox\s*,\s*activity\.CancellationToken\s*\)")] // kd-81: and the activity's token
    [InlineData("MailboxPermissions.razor", @"MailboxService\.AddMailboxPermissionsAsync\(\s*target,\s*validation\.Mailbox,")]
    [InlineData("MailboxPermissions.razor", @"MailboxService\.RemoveMailboxPermissionsAsync\(\s*target,\s*validation\.Mailbox,")]
    [InlineData("MailboxPermissions.razor", @"MailboxService\.AddMailboxPermissionsOnPremAsync\(\s*target,\s*onPremValidation\.Mailbox,")]
    [InlineData("MailboxPermissions.razor", @"MailboxService\.RemoveMailboxPermissionsOnPremAsync\(\s*target,\s*onPremValidation\.Mailbox,")]
    [InlineData("CalendarPermissions.razor", @"CalendarService\.GetMailboxLocationAsync\(\s*validation\.Mailbox\s*,\s*activity\.CancellationToken\s*\)")] // kd-81, as Mailbox's
    [InlineData("CalendarPermissions.razor", @"CalendarService\.SetCalendarPermissionAsync\(\s*target,\s*validation\.Mailbox,")]
    [InlineData("CalendarPermissions.razor", @"CalendarService\.RemoveCalendarPermissionAsync\(\s*target,\s*validation\.Mailbox,")]
    [InlineData("CalendarPermissions.razor", @"CalendarService\.SetCalendarPermissionOnPremAsync\(\s*target,\s*onPremValidation\.Mailbox,")]
    [InlineData("CalendarPermissions.razor", @"CalendarService\.RemoveCalendarPermissionOnPremAsync\(\s*target,\s*onPremValidation\.Mailbox,")]
    [InlineData("OutOfOffice.razor", @"OofService\.GetOutOfOfficeAsync\(\s*protectedValidation\.Mailbox\s*\)")]
    [InlineData("OutOfOffice.razor", @"OofService\.SetOutOfOfficeAsync\(\s*request,\s*protectedValidation\.Mailbox\s*\)")]
    [InlineData("ConferenceRooms.razor", @"RoomService\.GetRoomInfoAsync\(\s*room\s*\)")]
    [InlineData("ConferenceRooms.razor", @"RoomService\.SetRoomMetadataAndListAsync\(\s*email,\s*room,")]
    [InlineData("ConferenceRooms.razor", @"RoomService\.SetRoomTypeAsync\(\s*email,\s*room,")]
    public void EveryPageWrite_IsBoundToItsOwnValidation(string page, string pattern)
    {
        var source = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", page));

        Assert.True(Regex.IsMatch(source, pattern),
            $"{page}: no call matching {pattern} - the write must take the mailbox its validation returned");
    }

    [Theory]
    [InlineData("MailboxPermissions.razor", 5)]
    [InlineData("CalendarPermissions.razor", 5)]
    [InlineData("OutOfOffice.razor", 1)]
    [InlineData("ConferenceRooms.razor", 2)]
    public void EveryPageWriteAudit_RecordsTheIdentityWritten(string page, int expected)
    {
        // The audit keeps the typed target; the identity written rides in extra beside it. aud-a71: on the two permission
        // pages the single change's failure row records it too - the cloud success row, that failure row and the
        // on-premises write's rows. aud-a70 (5): and the row of a change refused before its write (SubmitSingle's Refused).
        // kd-81: and the row of a single change the operator's cancel stopped before it was sent (SubmitSingle's cancel catch).
        var source = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", page));

        Assert.Equal(expected, Regex.Matches(source, @"ValidatedMailbox\.WithWrittenIdentity\(").Count);
    }
}
