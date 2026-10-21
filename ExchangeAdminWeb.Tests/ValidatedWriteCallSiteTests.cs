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
    [InlineData("MailboxPermissions.razor", @"MailboxService\.GetMailboxLocationAsync\(\s*validation\.Mailbox\s*\)")]
    [InlineData("MailboxPermissions.razor", @"MailboxService\.AddMailboxPermissionsAsync\(\s*target,\s*validation\.Mailbox,")]
    [InlineData("MailboxPermissions.razor", @"MailboxService\.RemoveMailboxPermissionsAsync\(\s*target,\s*validation\.Mailbox,")]
    [InlineData("MailboxPermissions.razor", @"MailboxService\.AddMailboxPermissionsOnPremAsync\(\s*target,\s*onPremValidation\.Mailbox,")]
    [InlineData("MailboxPermissions.razor", @"MailboxService\.RemoveMailboxPermissionsOnPremAsync\(\s*target,\s*onPremValidation\.Mailbox,")]
    public void EveryPageWrite_IsBoundToItsOwnValidation(string page, string pattern)
    {
        var source = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", page));

        Assert.True(Regex.IsMatch(source, pattern),
            $"{page}: no call matching {pattern} - the write must take the mailbox its validation returned");
    }

    [Theory]
    [InlineData("MailboxPermissions.razor", 2)]
    public void EveryPageWriteAudit_RecordsTheIdentityWritten(string page, int expected)
    {
        // The audit keeps the typed target; the identity written rides in extra beside it.
        var source = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", page));

        Assert.Equal(expected, Regex.Matches(source, @"ValidatedMailbox\.WithWrittenIdentity\(").Count);
    }
}
