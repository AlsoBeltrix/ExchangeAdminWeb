using System.Text;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Sweep item 21, driven end to end: a bulk file through both CSV processors against the aud-a2d stand-ins. One mail per
/// affected person after the run, and no mail - and no "granted" - for a row Exchange answered as changing nothing.
/// </summary>
public sealed class BulkPermissionDigestRunTests : IDisposable
{
    private const string Typed = "shared";
    private const string Primary = "shared@contoso.test";
    private static readonly Guid Mailbox = Guid.Parse("0f0f0f0f-1111-4222-8333-444444444444");
    private static readonly string[] Trustees = ["u1@contoso.test", "u2@contoso.test", "u3@contoso.test"];

    private readonly ExchangeWriteHarness _h = new();
    private readonly EmailService _email;

    public BulkPermissionDigestRunTests()
    {
        _h.AddMailbox(Primary, Mailbox);
        foreach (var trustee in Trustees)
            _h.Forest.AddUser(trustee.Split('@')[0], upn: trustee, mail: trustee, proxies: [$"SMTP:{trustee}"]);
        _email = Substitute.For<EmailService>(_h.Config, Substitute.For<ILogger<EmailService>>());
    }

    public void Dispose() => _h.Dispose();

    private Task<Models.BulkOperationResult> MailboxRun(string rows) =>
        new MailboxPermissionService(_h.Pool, null!, NullLogger<MailboxPermissionService>.Instance, _h.Config, null!, _h.Trace)
            .ProcessMailboxPermissionsCsvAsync(
                new MemoryStream(Encoding.UTF8.GetBytes("Target,User,FullAccess,SendAs,AutoMapping\n" + rows)), isAdd: true,
                _h.NothingProtectedValidator(ExchangeWriteHarness.ExchangeKnows(Typed, Primary, Mailbox)),
                ExchangeWriteHarness.OperatorLogonName, _h.Audit, _email, "10.0.0.5", "INC100", actingUser: _h.Operator);

    private Task<Models.BulkOperationResult> CalendarRun(string rows) =>
        new CalendarPermissionService(_h.Pool, null!, NullLogger<CalendarPermissionService>.Instance, _h.Config, null!, _h.Trace)
            .ProcessCalendarPermissionsCsvAsync(
                new MemoryStream(Encoding.UTF8.GetBytes("Target,User,AccessRight\n" + rows)), isSet: true,
                _h.NothingProtectedValidator(ExchangeWriteHarness.ExchangeKnows(Typed, Primary, Mailbox)),
                ExchangeWriteHarness.OperatorLogonName, _h.Audit, _email, "10.0.0.5", "INC100", actingUser: _h.Operator);

    [Fact]
    public async Task AMailboxRun_SendsTheOwnerOneMailListingEveryTrustee()
    {
        var bulk = await MailboxRun(string.Concat(Trustees.Select(t => $"{Typed},{t},True,False,False\n")));

        Assert.Equal(3, bulk.SuccessCount);
        await _email.Received(1).SendOwnerDigestNotificationAsync(Typed,
            Arg.Is<IReadOnlyList<(string User, string Permission)>>(c => c.Select(x => x.User).SequenceEqual(Trustees)),
            ExchangeWriteHarness.OperatorLogonName, true);
        await _email.DidNotReceive().SendOwnerNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        foreach (var trustee in Trustees)
            await _email.Received(1).SendUserNotificationAsync(trustee, Typed, ExchangeWriteHarness.OperatorLogonName, "FullAccess", true);
    }

    [Fact]
    public async Task AMailboxRowExchangeAnswersAsAlreadyInPlace_MailsNobody_AndSaysNoChange()
    {
        _h.Warnings["Add-MailboxPermission"] =
            "The appropriate access control entry is already present on the object \"CN=shared\" for account \"u1\".";

        var bulk = await MailboxRun($"{Typed},{Trustees[0]},True,False,False\n");

        var entry = Assert.Single(bulk.Entries);
        Assert.Equal("SUCCESS", entry.Status);
        Assert.StartsWith("No change:", entry.Message, StringComparison.Ordinal);
        await NoMailToTheAffected();
    }

    [Fact]
    public async Task ACalendarRun_SendsTheOwnerOneMail_AndACalendarSetExchangeAnswersAsUnchangedMailsNobody()
    {
        var bulk = await CalendarRun(string.Concat(Trustees.Select(t => $"{Typed},{t},Reviewer\n")));

        Assert.Equal(3, bulk.SuccessCount);
        await _email.Received(1).SendOwnerDigestNotificationAsync(Typed,
            Arg.Is<IReadOnlyList<(string User, string Permission)>>(c => c.Count == 3 && c.All(x => x.Permission == "Calendar Reviewer")),
            ExchangeWriteHarness.OperatorLogonName, true);

        _email.ClearReceivedCalls();
        _h.Warnings["Set-MailboxFolderPermission"] =
            $"The command completed successfully but no settings of '{Mailbox:D}:\\Calendar' have been modified.";

        var unchanged = await CalendarRun($"{Typed},{Trustees[0]},Reviewer\n");

        var entry = Assert.Single(unchanged.Entries);
        Assert.Equal("SUCCESS", entry.Status);
        Assert.StartsWith("No change:", entry.Message, StringComparison.Ordinal);
        await NoMailToTheAffected();
    }

    private async Task NoMailToTheAffected()
    {
        await _email.DidNotReceive().SendUserNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        await _email.DidNotReceive().SendOwnerNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        await _email.DidNotReceive().SendUserDigestNotificationAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<string>(), Arg.Any<bool>());
        await _email.DidNotReceive().SendOwnerDigestNotificationAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<(string, string)>>(), Arg.Any<string>(), Arg.Any<bool>());
    }
}
