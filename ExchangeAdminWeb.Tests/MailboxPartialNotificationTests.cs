using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A23: a partial cloud permission change notified the affected user of nothing,
/// because the page gated the notice on overall success and a partial result is not a success.
/// </summary>
/// <remarks>
/// The aggregation half is driven for real - <see cref="MailboxPermissionOutcome"/> is the seam
/// that exists precisely so these decisions are provable without a tenant. That the PAGE reads
/// the new field instead of <c>opResult.Success</c> is a source scan, because the surrounding
/// handler needs a live Exchange connection to reach.
/// </remarks>
public class MailboxPartialNotificationTests
{
    private const string Target = "ceo@analog.com";
    private const string User = "jdoe@analog.com";

    [Fact]
    public void APartialGrant_NamesTheRightThatLanded()
    {
        // The finding. FullAccess really was granted; the user can open the mailbox and nobody
        // told them.
        var result = MailboxPermissionOutcome.ForGrant(Target, User, [
            RightOutcome.Ok("FullAccess"),
            RightOutcome.Failed("SendAs", "insufficient rights")
        ]);

        Assert.False(result.Success);
        Assert.Equal(["FullAccess"], result.AppliedRights);
    }

    [Fact]
    public void APartialRevoke_NamesTheRightThatWasRemoved()
    {
        var result = MailboxPermissionOutcome.ForRevoke(Target, User, [
            RightOutcome.Failed("FullAccess", "boom"),
            RightOutcome.Ok("SendAs")
        ]);

        Assert.False(result.Success);
        Assert.Equal(["SendAs"], result.AppliedRights);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFullGrant_NamesEveryRight(bool onPrem)
    {
        var result = MailboxPermissionOutcome.ForGrant(Target, User, [
            RightOutcome.Ok("FullAccess"),
            RightOutcome.Ok("SendAs")
        ], onPrem);

        Assert.True(result.Success);
        Assert.Equal(["FullAccess", "SendAs"], result.AppliedRights);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFullRevoke_NamesEveryRight(bool onPrem)
    {
        var result = MailboxPermissionOutcome.ForRevoke(Target, User, [
            RightOutcome.Ok("FullAccess"),
            RightOutcome.Ok("SendAs")
        ], onPrem);

        Assert.True(result.Success);
        Assert.Equal(["FullAccess", "SendAs"], result.AppliedRights);
    }

    [Fact]
    public void ATotalFailure_AppliesNothing()
    {
        // Fail closed: nothing landed, so nobody is told their access changed.
        var result = MailboxPermissionOutcome.ForGrant(Target, User, [
            RightOutcome.Failed("FullAccess", "denied"),
            RightOutcome.Failed("SendAs", "denied")
        ]);

        Assert.False(result.Success);
        Assert.True(result.AppliedRights is null || result.AppliedRights.Count == 0);
    }

    [Fact]
    public void ASuccessfulCloudGrant_KeepsTheLinkOutOfTheAppliedRights()
    {
        // Why this is a field and not a parse of Detail. On the cloud success path Detail holds
        // the OWA link; reading rights out of it would put a URL in the permission-type line of
        // the user's mail.
        var result = MailboxPermissionOutcome.ForGrant(Target, User, [RightOutcome.Ok("FullAccess")]);

        Assert.Contains("outlook.office.com", result.Detail);
        Assert.Equal(["FullAccess"], result.AppliedRights);
        Assert.DoesNotContain(result.AppliedRights!, r => r.Contains("http", StringComparison.Ordinal));
    }

    [Fact]
    public void AServicedNote_DoesNotDropTheAppliedRights()
    {
        // WithServicedNote rebuilds the record field by field, so a new field is exactly the
        // kind of thing it silently loses.
        var result = MailboxPermissionOutcome
            .ForGrant(Target, User, [RightOutcome.Ok("FullAccess"), RightOutcome.Failed("SendAs", "boom")])
            .WithServicedNote("authorised by ExchangeServicers");

        Assert.Equal(["FullAccess"], result.AppliedRights);
        Assert.Equal("authorised by ExchangeServicers", result.ServicedNote);
    }

    [Fact]
    public void ThePageNotifiesOnWhatApplied_NotOnOverallSuccess()
    {
        // The wiring. A fix that populated the field and left the gate alone would pass every
        // test above and change nothing for the user it is for.
        var body = AuditCategoryFilingTests.MethodBody(
            File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "MailboxPermissions.razor")),
            "private async Task SubmitSingle");

        Assert.Contains("opResult.AppliedRights", body, StringComparison.Ordinal);
        Assert.Contains("if (applied.Count > 0)", body, StringComparison.Ordinal);

        // The notice must carry what landed, not the requested set.
        Assert.Contains("SendUserNotificationAsync(user, target, currentUser, notifyPerm", body, StringComparison.Ordinal);
        Assert.Contains("SendOwnerNotificationAsync(target, user, currentUser, notifyPerm", body, StringComparison.Ordinal);

        // Review of the A23 batch: this passed if the whole applied-rights block were
        // nested back under `if (opResult.Success)`, which re-breaks the partial case while
        // every string above is still present. Nothing between taking the applied set and
        // sending may test overall success.
        var from = body.IndexOf("var applied = opResult.AppliedRights", StringComparison.Ordinal);
        var to = body.IndexOf("SendOwnerNotificationAsync", StringComparison.Ordinal);
        Assert.True(from > 0 && to > from, "the applied-rights block must still be there");

        Assert.DoesNotContain("opResult.Success", body[from..to], StringComparison.Ordinal);
    }
}
