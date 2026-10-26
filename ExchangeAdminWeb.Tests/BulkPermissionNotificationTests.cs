using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A24: a bulk permission run audited every row and sent the operator's
/// administrators one summary, and told the affected people nothing.
/// </summary>
/// <remarks>
/// The decision is driven for real against a substituted <see cref="EmailService"/>:
/// <see cref="PermissionChangeNotifier"/> exists as a seam precisely because the CSV loops
/// around it need a live Exchange connection. That both processors CALL it, and with the
/// right arguments, is a source scan for the same reason.
/// </remarks>
public class BulkPermissionNotificationTests
{
    private readonly EmailService _email = Substitute.For<EmailService>(
        new ConfigurationBuilder().Build(), Substitute.For<ILogger<EmailService>>());

    private readonly ILogger _logger = Substitute.For<ILogger>();

    private Task Notify(IReadOnlyList<string> applied, bool isGrant = true) =>
        PermissionChangeNotifier.NotifyAffectedAsync(
            _email, applied, "ceo@analog.com", "ceo@analog.com", "jdoe@analog.com",
            "admin@analog.com", isGrant, _logger);

    [Fact]
    public async Task AnAppliedRowNotifiesBothTheTrusteeAndTheOwner()
    {
        await Notify(["FullAccess"]);

        await _email.Received(1).SendUserNotificationAsync(
            "jdoe@analog.com", "ceo@analog.com", "admin@analog.com", "FullAccess", true);
        await _email.Received(1).SendOwnerNotificationAsync(
            "ceo@analog.com", "jdoe@analog.com", "admin@analog.com", "FullAccess", true);
    }

    [Fact]
    public async Task ARowThatAppliedNothingNotifiesNobody()
    {
        // The gate. A failed row must not tell someone their access changed.
        await Notify([]);

        await _email.DidNotReceive().SendUserNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        await _email.DidNotReceive().SendOwnerNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task SeveralAppliedRightsAreNamedTogether()
    {
        await Notify(["FullAccess", "SendAs"]);

        await _email.Received(1).SendUserNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), "FullAccess+SendAs", Arg.Any<bool>());
    }

    [Fact]
    public async Task AFailedTrusteeNoticeStillSendsTheOwnerNotice()
    {
        // Independent sends. One address being undeliverable must not silence the other.
        _email.SendUserNotificationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns<Task>(_ => throw new InvalidOperationException("mailbox full"));

        await Notify(["FullAccess"]);

        await _email.Received(1).SendOwnerNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task AnUnsendableNoticeDoesNotFailTheRow()
    {
        // The permission is applied and audited. A mail server being down cannot turn that
        // into a failure, and must not abandon the rest of the CSV.
        _email.SendUserNotificationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns<Task>(_ => throw new InvalidOperationException("smtp down"));
        _email.SendOwnerNotificationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns<Task>(_ => throw new InvalidOperationException("smtp down"));

        await Notify(["FullAccess"]);
    }

    [Fact]
    public void TheMailboxBulkLoopNotifiesEveryRow()
    {
        var body = Body("MailboxPermissionService.cs", "ProcessMailboxPermissionsCsvAsync");

        Assert.Contains("PermissionChangeNotifier.NotifyAffectedAsync", body, StringComparison.Ordinal);

        // AppliedRights, not result.Success - a partial row must notify on the part that took.
        Assert.Contains("result.AppliedRights ?? []", body, StringComparison.Ordinal);

        // Review of the A24 batch: the scan passed with the notifier duplicated, or sitting
        // in the wrong branch, as long as the strings appeared somewhere. Exactly one call,
        // and it must come BEFORE the success/failure branch so a partial row still fires.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, "NotifyAffectedAsync"));

        var notifyAt = body.IndexOf("PermissionChangeNotifier.NotifyAffectedAsync", StringComparison.Ordinal);
        var branchAt = body.IndexOf("if (result.Success)", StringComparison.Ordinal);
        Assert.True(branchAt > notifyAt, "the notifier must run before the success branch");
    }

    [Fact]
    public void TheCalendarBulkLoopNotifiesEveryRow()
    {
        var body = Body("CalendarPermissionService.cs", "ProcessCalendarPermissionsCsvAsync");

        Assert.Contains("PermissionChangeNotifier.NotifyAffectedAsync", body, StringComparison.Ordinal);

        // A calendar row sets one right, so success is the applied set.
        Assert.Contains("result.Success ? [notifyPermType] : []", body, StringComparison.Ordinal);
        Assert.Contains("(Calendar)", body, StringComparison.Ordinal);

        // Review of the A24 batch found the real bug this guard had missed: the notice sent
        // `permType`, which is the OPERATOR's word for the row - "Reviewer", or "Remove" for
        // a revoke - so a bulk removal told the affected person 'Permission: Remove', naming
        // an action rather than the access. The single-target paths say "Calendar <level>"
        // and "Calendar Access"; this pins the same wording.
        Assert.Contains("$\"Calendar {row.AccessRight}\" : \"Calendar Access\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("result.Success ? [permType]", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MailboxPermissionService.cs", "ProcessMailboxPermissionsCsvAsync", "result.UnconfirmedRights ?? []")]
    [InlineData("CalendarPermissionService.cs", "ProcessCalendarPermissionsCsvAsync", "result.Unconfirmed ? [notifyPermType] : []")]
    public void TheBulkLoopsTellTheAffectedOfARowThatCouldNotBeConfirmed(string file, string method, string rights)
    {
        // kd-44. A row whose outcome is unknown tells the trustee and the owner it may have changed: once, on what is
        // unconfirmed, and before the success branch - such a row is not a success.
        var body = Body(file, method);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, "NotifyAffectedOfUnconfirmedAsync"));
        var notifyAt = body.IndexOf("PermissionChangeNotifier.NotifyAffectedOfUnconfirmedAsync", StringComparison.Ordinal);
        Assert.True(notifyAt > 0, "the bulk loop must tell the affected of an unconfirmed row");
        Assert.True(body.IndexOf(rights, notifyAt, StringComparison.Ordinal) > notifyAt, "on what is unconfirmed");
        Assert.True(body.IndexOf("if (result.Success)", notifyAt, StringComparison.Ordinal) > notifyAt, "before the success branch");
    }

    [Theory]
    [InlineData("MailboxPermissions.razor", "ProcessMailboxPermissionsCsvAsync")]
    [InlineData("CalendarPermissions.razor", "ProcessCalendarPermissionsCsvAsync")]
    public void ThePagesHandTheProcessorAnEmailService(string page, string call)
    {
        // Without this the wiring compiles only because some other argument happens to fit.
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", page));

        var line = text.Split('\n').Single(l => l.Contains(call, StringComparison.Ordinal));
        Assert.Contains("Audit, Email,", line, StringComparison.Ordinal);
    }

    private static string Body(string file, string method) =>
        AuditCategoryFilingTests.MethodBody(
            File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", file)),
            method);
}
