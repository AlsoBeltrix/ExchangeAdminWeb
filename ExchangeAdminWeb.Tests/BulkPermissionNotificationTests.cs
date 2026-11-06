using System.Management.Automation;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A24: a bulk permission run audited every row and sent the operator's administrators one summary, and told
/// the affected people nothing. Sweep item 21: the A24 fix then mailed the trustee and the owner once per applied row from
/// inside the loop - a 200-row file sent 400 mails - and mailed "granted" for a row Exchange treated as a no-op. The owner's
/// ruling of 2026-11-03: one digest per affected person after the loop, and no mail for a no-op.
/// </summary>
/// <remarks>
/// The decision is driven for real against a substituted <see cref="EmailService"/>: <see cref="PermissionChangeDigest"/>
/// exists as a seam because the CSV loops around it need a live Exchange connection. That both processors use it, and where,
/// is a source scan for the same reason. The no-op is read from Exchange's warning stream, driven here by a local
/// PowerShell that writes the warning.
/// </remarks>
public class BulkPermissionNotificationTests
{
    private readonly EmailService _email = Substitute.For<EmailService>(
        new ConfigurationBuilder().Build(), Substitute.For<ILogger<EmailService>>());

    private readonly ILogger _logger = Substitute.For<ILogger>();

    private const string Admin = "admin@example.com";

    [Fact]
    public async Task ASingleChangeSendsTheSingleRowMailToTheTrusteeAndTheOwner()
    {
        var digest = new PermissionChangeDigest();
        digest.Record(["FullAccess"], "ceo@example.com", "ceo@example.com", "jdoe@example.com");

        await digest.SendAsync(_email, Admin, isGrant: true, _logger);

        await _email.Received(1).SendUserNotificationAsync("jdoe@example.com", "ceo@example.com", Admin, "FullAccess", true);
        await _email.Received(1).SendOwnerNotificationAsync("ceo@example.com", "jdoe@example.com", Admin, "FullAccess", true);
        await _email.DidNotReceive().SendUserDigestNotificationAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<string>(), Arg.Any<bool>());
        await _email.DidNotReceive().SendOwnerDigestNotificationAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Fact]
    public async Task ATrusteeGrantedSeveralMailboxesGetsOneMailListingEveryOne()
    {
        var digest = new PermissionChangeDigest();
        digest.Record(["FullAccess"], "a@example.com", "a@example.com", "jdoe@example.com");
        digest.Record(["FullAccess", "SendAs"], "b@example.com", "b@example.com", "JDoe@Example.com");
        digest.Record(["SendAs"], "c@example.com", "c@example.com", "jdoe@example.com");

        await digest.SendAsync(_email, Admin, isGrant: true, _logger);

        (string, string)[] expected = [("a@example.com", "FullAccess"), ("b@example.com", "FullAccess+SendAs"), ("c@example.com", "SendAs")];
        await _email.Received(1).SendUserDigestNotificationAsync("jdoe@example.com",
            Arg.Is<IReadOnlyList<(string Target, string Permission)>>(c => c.SequenceEqual(expected)), Admin, true);
        await _email.DidNotReceive().SendUserNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        // Three owners, one change each: three single-row owner mails.
        await _email.Received(3).SendOwnerNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Admin, Arg.Any<string>(), true);
    }

    [Fact]
    public async Task AnOwnerWithSeveralChangesGetsOneMailListingEveryUser()
    {
        var digest = new PermissionChangeDigest();
        digest.Record(["Calendar Reviewer"], "ceo@example.com (Calendar)", "ceo@example.com", "u1@example.com");
        digest.Record(["Calendar Editor"], "ceo@example.com (Calendar)", "CEO@example.com", "u2@example.com");

        await digest.SendAsync(_email, Admin, isGrant: true, _logger);

        (string, string)[] expected = [("u1@example.com", "Calendar Reviewer"), ("u2@example.com", "Calendar Editor")];
        await _email.Received(1).SendOwnerDigestNotificationAsync("ceo@example.com",
            Arg.Is<IReadOnlyList<(string User, string Permission)>>(c => c.SequenceEqual(expected)), Admin, true);
        await _email.DidNotReceive().SendOwnerNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        await _email.Received(2).SendUserNotificationAsync(
            Arg.Any<string>(), "ceo@example.com (Calendar)", Admin, Arg.Any<string>(), true);
    }

    [Fact]
    public async Task NothingIsSentUntilTheRunAsks_AndARowThatChangedNothingIsToldToNobody()
    {
        var digest = new PermissionChangeDigest();
        digest.Record(["FullAccess"], "a@example.com", "a@example.com", "jdoe@example.com");
        digest.Record([], "b@example.com", "b@example.com", "other@example.com");

        await _email.DidNotReceive().SendUserNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());

        await digest.SendAsync(_email, Admin, isGrant: false, _logger);

        await _email.DidNotReceive().SendUserNotificationAsync("other@example.com",
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        await _email.DidNotReceive().SendOwnerNotificationAsync("b@example.com",
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        await _email.Received(1).SendUserNotificationAsync("jdoe@example.com", "a@example.com", Admin, "FullAccess", false);
    }

    [Fact]
    public async Task AFailedNoticeStillSendsTheOthers_AndNothingThrows()
    {
        _email.SendUserDigestNotificationAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns<Task>(_ => throw new InvalidOperationException("smtp down"));
        _email.SendOwnerNotificationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns<Task>(_ => throw new InvalidOperationException("smtp down"));

        var digest = new PermissionChangeDigest();
        digest.Record(["FullAccess"], "a@example.com", "a@example.com", "jdoe@example.com");
        digest.Record(["FullAccess"], "b@example.com", "b@example.com", "jdoe@example.com");
        digest.Record(["FullAccess"], "a@example.com", "a@example.com", "second@example.com");

        await digest.SendAsync(_email, Admin, isGrant: true, _logger);

        await _email.Received(1).SendUserNotificationAsync("second@example.com", "a@example.com", Admin, "FullAccess", true);
        await _email.Received(1).SendOwnerDigestNotificationAsync("a@example.com",
            Arg.Any<IReadOnlyList<(string, string)>>(), Admin, true);
        await _email.Received(1).SendOwnerNotificationAsync("b@example.com",
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Theory]
    [InlineData("The appropriate access control entry is already present on the object \"CN=x\" for account \"y\".")]
    [InlineData("Can't remove the access control entry on the object \"CN=x\" for account \"y\" because the ACE isn't present.")]
    [InlineData("The command completed successfully but no settings of 'x:\\Calendar' have been modified.")]
    public void ExchangesNoChangeWarningsAreRecognised(string warning) =>
        Assert.True(ExchangeNoChangeWarning.SaysNothingChanged([warning]));

    [Theory]
    [InlineData("The mailbox is hidden from address lists.")]
    [InlineData("The ACE was added; replication may take up to 15 minutes.")]
    [InlineData("")]
    public void OtherWarningsAreNotANoChange(string warning) =>
        Assert.False(ExchangeNoChangeWarning.SaysNothingChanged([warning]));

    [Fact]
    public void ARightExchangeAnswersAsAlreadyPresent_IsAnUnchangedSuccess()
    {
        using var ps = PowerShell.Create();

        var outcome = MailboxPermissionService.TryRight(ps, new ExchangeServiceBase.ConnectionErrorTracker(), "FullAccess", _ =>
        {
            ps.AddScript("Write-Warning 'The appropriate access control entry is already present on the object \"x\" for account \"y\".'");
            ps.Invoke();
            ps.Commands.Clear();
        });

        Assert.True(outcome.Succeeded);
        Assert.True(outcome.Unchanged);
    }

    [Fact]
    public void AWarningLeftFromAnEarlierCommand_DoesNotMakeTheRightUnchanged()
    {
        using var ps = PowerShell.Create();
        ps.AddScript("Write-Warning 'The appropriate access control entry is already present on the object \"x\" for account \"y\".'");
        ps.Invoke();
        ps.Commands.Clear();

        var outcome = MailboxPermissionService.TryRight(ps, new ExchangeServiceBase.ConnectionErrorTracker(), "SendAs", _ =>
        {
            ps.AddScript("$null");
            ps.Invoke();
            ps.Commands.Clear();
        });

        Assert.True(outcome.Succeeded);
        Assert.False(outcome.Unchanged);
    }

    [Fact]
    public void TheResultNamesTheUnchangedRights_AndKeepsThemApplied()
    {
        var result = MailboxPermissionOutcome.ForGrant("box@example.com", "jdoe@example.com",
            [RightOutcome.Ok("FullAccess"), RightOutcome.Ok("SendAs", unchanged: true)]);

        Assert.True(result.Success);
        Assert.Equal(["FullAccess", "SendAs"], result.AppliedRights!);
        Assert.Equal(["SendAs"], result.UnchangedRights!);
        Assert.Equal(["FullAccess"], result.ChangedRights);
    }

    [Fact]
    public void ARowThatChangedNothingIsReportedAsNoChange_NotAsGranted()
    {
        var result = MailboxPermissionOutcome.ForGrant("box@example.com", "jdoe@example.com",
            [RightOutcome.Ok("FullAccess", unchanged: true)]);

        var message = MailboxPermissionService.BulkRowMessage(result, "jdoe@example.com", "box@example.com", isAdd: true);

        Assert.Empty(result.ChangedRights);
        Assert.StartsWith("No change:", message, StringComparison.Ordinal);
        Assert.DoesNotContain("has been granted", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowThatChangedEverythingKeepsItsOwnMessage()
    {
        var result = MailboxPermissionOutcome.ForRevoke("box@example.com", "jdoe@example.com", [RightOutcome.Ok("FullAccess")]);

        Assert.Null(result.UnchangedRights);
        Assert.Equal(result.Message, MailboxPermissionService.BulkRowMessage(result, "jdoe@example.com", "box@example.com", isAdd: false));
    }

    [Theory]
    [InlineData("MailboxPermissionService.cs", "ProcessMailboxPermissionsCsvAsync", "digest.Record(result.ChangedRights,")]
    [InlineData("CalendarPermissionService.cs", "ProcessCalendarPermissionsCsvAsync", "digest.Record(result.Success && !changedNothing ? [notifyPermType] : []")]
    public void TheBulkLoopsRecordEachRowAndSendOncePerPersonAfterTheLoop(string file, string method, string record)
    {
        var body = Body(file, method);

        // Recorded per row, before the success branch so a partial row still counts.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, @"digest\.Record\("));
        Assert.Contains(record, body, StringComparison.Ordinal);
        var recordAt = body.IndexOf("digest.Record(", StringComparison.Ordinal);
        Assert.True(body.IndexOf("if (result.Success)", recordAt, StringComparison.Ordinal) > recordAt,
            "the row must be recorded before the success branch");

        // Sent once, after the foreach has closed; never a send inside the loop.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(body, @"digest\.SendAsync\("));
        var sendAt = body.IndexOf("digest.SendAsync(", StringComparison.Ordinal);
        var loopAt = body.IndexOf("foreach (var row in records)", StringComparison.Ordinal);
        Assert.True(loopAt >= 0, "the row loop was not found");
        Assert.True(sendAt > LoopEnd(body, loopAt), "the digest must be sent after the row loop, not inside it");

        Assert.DoesNotContain("email.Send", body, StringComparison.Ordinal);
        Assert.DoesNotContain("PermissionChangeNotifier.", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheCalendarLoopNamesTheAccessNotTheOperatorsWord()
    {
        // Review of A24: permType is the OPERATOR's word - "Reviewer", or "Remove" for a revoke. The single-target paths say
        // "Calendar <level>" and "Calendar Access"; this pins the same wording, and the "(Calendar)" trustee label.
        var body = Body("CalendarPermissionService.cs", "ProcessCalendarPermissionsCsvAsync");

        Assert.Contains("$\"Calendar {row.AccessRight}\" : \"Calendar Access\"", body, StringComparison.Ordinal);
        Assert.Contains("(Calendar)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("? [permType]", body, StringComparison.Ordinal);
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

    /// <summary>The index of the brace that closes the foreach starting at <paramref name="loopAt"/>.</summary>
    private static int LoopEnd(string body, int loopAt)
    {
        var depth = 0;
        for (var i = body.IndexOf('{', loopAt); i >= 0 && i < body.Length; i++)
        {
            if (body[i] == '{') depth++;
            else if (body[i] == '}' && --depth == 0) return i;
        }
        return int.MaxValue;
    }

    private static string Body(string file, string method) =>
        AuditCategoryFilingTests.MethodBody(
            File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", file)),
            method);
}
