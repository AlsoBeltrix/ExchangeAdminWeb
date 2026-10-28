using System.Management.Automation;
using System.Text;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a76: the Mailbox Permissions and Calendar Permissions writes say when a failure came before their command was sent to
/// Exchange - a <see cref="PermissionWriteNotSubmittedException"/> - so their pages file it Failed, nothing written, and keep
/// Undetermined for a failure after the command was sent.
/// </summary>
/// <remarks>
/// The tracker itself; then the eight writes over <see cref="ExchangeWriteHarness"/> - a pool whose connect throws, a
/// <c>ConnectOnPrem</c> that throws, a remote command that fails once sent; then the bulk loops, which read a throw's message
/// alone. Nothing here reaches Exchange or a directory.
/// </remarks>
public sealed class PermissionWriteSubmissionTests : IDisposable
{
    private const string Typed = "shared";
    private const string Trustee = "jdoe@contoso.test";
    private static readonly Guid Checked = Guid.Parse("0d0d0d0d-1111-4222-8333-444444444444");

    private readonly ExchangeWriteHarness _h = new();

    public PermissionWriteSubmissionTests() => _h.AddMailbox("shared@contoso.test", Checked);

    public void Dispose() => _h.Dispose();

    // ---- the tracker ---------------------------------------------------------------------------------------------------

    [Fact]
    public async Task AThrowBeforeTheMark_IsNotSubmitted_WithItsCauseInside_InItsCausesMessage()
    {
        // A faulted step - a refused borrow as the pool hands it back - and a step that throws before it returns a task at
        // all: neither reached the mark, so neither can have written.
        var cause = new InvalidOperationException("the borrow was refused");

        var faulted = await Assert.ThrowsAsync<PermissionWriteNotSubmittedException>(
            () => new PermissionWriteSubmission().TrackAsync(() => Task.FromException<PermissionResult>(cause)));
        var thrown = await Assert.ThrowsAsync<PermissionWriteNotSubmittedException>(
            () => new PermissionWriteSubmission().TrackAsync<PermissionResult>(() => throw cause));

        Assert.All(new[] { faulted, thrown }, notSent =>
        {
            Assert.Same(cause, notSent.InnerException);
            Assert.Equal(cause.Message, notSent.Message);
        });
    }

    [Fact]
    public async Task AThrowOnceMarked_GoesOutAsItIs_AndAResultGoesThrough()
    {
        var cause = new InvalidOperationException("the session was lost during the write");
        var submission = new PermissionWriteSubmission();

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => submission.TrackAsync<PermissionResult>(() =>
        {
            submission.Submitting();
            throw cause;
        }));
        var result = await new PermissionWriteSubmission().TrackAsync(() => Task.FromResult(PermissionResult.Ok()));

        Assert.Same(cause, thrown);
        Assert.True(submission.Submitted);
        Assert.True(result.Success);
    }

    // ---- the eight writes ----------------------------------------------------------------------------------------------

    public static TheoryData<string> CloudWrites =>
        ["AddMailboxPermissions", "RemoveMailboxPermissions", "SetCalendarPermission", "RemoveCalendarPermission"];

    public static TheoryData<string> OnPremWrites =>
        ["AddMailboxPermissionsOnPrem", "RemoveMailboxPermissionsOnPrem", "SetCalendarPermissionOnPrem", "RemoveCalendarPermissionOnPrem"];

    /// <summary>A pool whose every connect throws <paramref name="failure"/>: no session can be had.</summary>
    private ExoConnectionPool PoolThatCannotConnect(Exception failure) =>
        new(_h.Config, _h.ModuleConfig, _h.Enablement, NullLogger<ExoConnectionPool>.Instance, _h.Trace, (_, _, _) => throw failure);

    private MailboxPermissionService MailboxService(ExoConnectionPool pool) =>
        new(pool, null!, NullLogger<MailboxPermissionService>.Instance, _h.Config, null!, _h.Trace);

    private CalendarPermissionService CalendarService(ExoConnectionPool pool) =>
        new(pool, null!, NullLogger<CalendarPermissionService>.Instance, _h.Config, null!, _h.Trace);

    private Task<PermissionResult> Cloud(string write, ExoConnectionPool pool)
    {
        var validated = ExchangeWriteHarness.Validated(Checked);
        return write switch
        {
            "AddMailboxPermissions" => MailboxService(pool).AddMailboxPermissionsAsync(Typed, validated, Trustee, fullAccess: true, sendAs: true, autoMapping: false),
            "RemoveMailboxPermissions" => MailboxService(pool).RemoveMailboxPermissionsAsync(Typed, validated, Trustee, fullAccess: true, sendAs: true),
            "SetCalendarPermission" => CalendarService(pool).SetCalendarPermissionAsync(Typed, validated, Trustee, CalendarAccessRight.Reviewer),
            _ => CalendarService(pool).RemoveCalendarPermissionAsync(Typed, validated, Trustee),
        };
    }

    /// <summary>The on-premises Mailbox Permissions writes: the harness's credential, and its session unless the connect throws.</summary>
    private sealed class OnPremMailbox(ExchangeWriteHarness h, Exception? connectFailure)
        : MailboxPermissionService(h.Pool, null!, NullLogger<MailboxPermissionService>.Instance, h.Config, null!, h.Trace)
    {
        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose, CancellationToken cancellationToken) =>
            Task.FromResult<(string username, string password, string domain)?>(("svc", "secret", "CONTOSO"));

        protected override void ConnectOnPrem(PowerShell ps, string username, string password, string domain, int maxRetries = 3, CancellationToken cancellationToken = default)
        {
            if (connectFailure is not null)
                throw connectFailure;
            h.DefineOnPremSession(ps);
        }
    }

    /// <summary>The on-premises Calendar Permissions writes, as <see cref="OnPremMailbox"/>.</summary>
    private sealed class OnPremCalendar(ExchangeWriteHarness h, Exception? connectFailure)
        : CalendarPermissionService(h.Pool, null!, NullLogger<CalendarPermissionService>.Instance, h.Config, null!, h.Trace)
    {
        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose, CancellationToken cancellationToken) =>
            Task.FromResult<(string username, string password, string domain)?>(("svc", "secret", "CONTOSO"));

        protected override void ConnectOnPrem(PowerShell ps, string username, string password, string domain, int maxRetries = 3, CancellationToken cancellationToken = default)
        {
            if (connectFailure is not null)
                throw connectFailure;
            h.DefineOnPremSession(ps);
        }
    }

    private Task<PermissionResult> OnPrem(string write, Exception? connectFailure = null)
    {
        var validated = ExchangeWriteHarness.Validated(Checked);
        return write switch
        {
            "AddMailboxPermissionsOnPrem" => new OnPremMailbox(_h, connectFailure).AddMailboxPermissionsOnPremAsync(Typed, validated, Trustee, fullAccess: true, sendAs: true),
            "RemoveMailboxPermissionsOnPrem" => new OnPremMailbox(_h, connectFailure).RemoveMailboxPermissionsOnPremAsync(Typed, validated, Trustee, fullAccess: true, sendAs: true),
            "SetCalendarPermissionOnPrem" => new OnPremCalendar(_h, connectFailure).SetCalendarPermissionOnPremAsync(Typed, validated, Trustee, "Reviewer"),
            _ => new OnPremCalendar(_h, connectFailure).RemoveCalendarPermissionOnPremAsync(Typed, validated, Trustee),
        };
    }

    [Theory]
    [MemberData(nameof(CloudWrites))]
    public async Task ACloudWriteWhoseConnectionCannotBeHad_SaysNothingWasSent(string write)
    {
        // The borrow - here its connect - is the one throw a cloud write lets out, and it comes before any command.
        var failure = new InvalidOperationException("the connect was refused");
        using var pool = PoolThatCannotConnect(failure);

        var notSent = await Assert.ThrowsAsync<PermissionWriteNotSubmittedException>(() => Cloud(write, pool));

        Assert.Same(failure, notSent.InnerException);
        Assert.Equal(failure.Message, notSent.Message);
        Assert.Empty(_h.Calls);
    }

    [Theory]
    [MemberData(nameof(OnPremWrites))]
    public async Task AnOnPremWriteWhoseConnectFails_SaysNothingWasSent(string write)
    {
        var failure = new InvalidOperationException("the on-premises session could not be opened");

        var notSent = await Assert.ThrowsAsync<PermissionWriteNotSubmittedException>(() => OnPrem(write, failure));

        Assert.Same(failure, notSent.InnerException);
        Assert.Equal(failure.Message, notSent.Message);
        Assert.Empty(_h.CallsTo("Invoke-Command"));
    }

    [Theory]
    [InlineData("SetCalendarPermissionOnPrem", "Add-MailboxFolderPermission")]
    [InlineData("RemoveCalendarPermissionOnPrem", "Remove-MailboxFolderPermission")]
    public async Task AnOnPremCalendarWrite_ExchangeRefuses_IsAFailedResult_NotAThrow(string write, string command)
    {
        // aud-a70 (12). aud-a76's case: the one Invoke-Command let out what its remote command threw, and the page filed a write
        // Exchange refused as one that could not be confirmed. Exchange answered: the write is a failed result, as Mailbox
        // Permissions' TryRightOnPrem makes one, in the command's own words. The set falls back to an add, which fails too.
        _h.Failures["Set-MailboxFolderPermission"] = "the folder permission could not be set";
        _h.Failures[command] = "the remote command failed";

        var result = await OnPrem(write);

        Assert.False(result.Success);
        Assert.False(result.Unconfirmed);
        Assert.Equal($"Failed on {Typed} calendar (on-premises): the remote command failed.", result.Message);
        Assert.Single(_h.CallsTo("Invoke-Command"));
        Assert.Single(_h.CallsTo(command));
    }

    // ---- aud-a70 (14): a write that fails once sent reads as what failed - Exchange's answer, or its session ------------------

    /// <summary>What a session that drops mid-command throws in Exchange Online: words the pool reads as a dead session.</summary>
    private const string SessionLost = "the session to Exchange Online was lost";

    /// <summary>What a session that breaks on premises says: none of the words a connection error is read by.</summary>
    private const string TransportFailure = "Processing data from remote server failed";

    [Theory]
    [InlineData("AddMailboxPermissions", "Add-RecipientPermission", "granted")]
    [InlineData("RemoveMailboxPermissions", "Remove-RecipientPermission", "removed")]
    public async Task ACloudMailboxRight_WhoseSessionFailsOnceSent_IsUnconfirmed_AndTheRightThatTookStands(string write, string command, string verb)
    {
        // TryRight filed a right whose command was sent and whose session then dropped as Failed, though its outcome is unknown.
        _h.Failures[command] = SessionLost;

        var result = await Cloud(write, _h.Pool);

        Assert.False(result.Success);
        Assert.Equal(new[] { "FullAccess" }, result.AppliedRights!);
        Assert.Equal(new[] { "SendAs" }, result.UnconfirmedRights!);
        Assert.Equal($"{char.ToUpperInvariant(verb[0])}{verb[1..]} FullAccess on {Typed}. Whether SendAs was {verb} on {Typed} could not be confirmed: {SessionLost}. Check the permissions before trying again.",
            result.Message);
        Assert.Single(_h.CallsTo(command));
    }

    [Theory]
    [InlineData("the trustee is not valid")]
    [InlineData("You must call Connect-ExchangeOnline before calling any other cmdlet.")]
    public async Task ACloudMailboxRight_ExchangeRefuses_OrThatMeetsThePreFlightError_IsFailed_AsBefore(string failure)
    {
        // Exchange answering is a known outcome; the pre-flight error proves no cmdlet ran. Neither is unconfirmed.
        _h.Failures["Add-RecipientPermission"] = failure;

        var result = await Cloud("AddMailboxPermissions", _h.Pool);

        Assert.False(result.Success);
        Assert.False(result.Unconfirmed);
        Assert.StartsWith($"Partial: granted FullAccess on {Typed}. Failed: SendAs: {failure}", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("SetCalendarPermission")]
    [InlineData("RemoveCalendarPermission")]
    public async Task ACloudCalendarWrite_WhoseSessionFailsOnceSent_IsUnconfirmed(string write)
    {
        // RunAsync made the dropped session a failed result, filed Failed though the write may have taken.
        _h.Failures["Set-MailboxFolderPermission"] = SessionLost;
        _h.Failures["Add-MailboxFolderPermission"] = SessionLost;
        _h.Failures["Remove-MailboxFolderPermission"] = SessionLost;

        var result = await Cloud(write, _h.Pool);

        Assert.False(result.Success);
        Assert.Equal(new[] { CalendarPermissionService.CalendarRight }, result.UnconfirmedRights!);
        Assert.Contains($"could not be confirmed: {SessionLost}. Check the permissions before trying again.", result.Message, StringComparison.Ordinal);
    }

    // ---- kd-43: a cloud Calendar set adds only after Exchange answers that there is no entry to set ------------------------

    private const string NoEntry = "There is no existing permission entry found for user: jdoe@contoso.test.";
    private const string PreFlight = "You must call Connect-ExchangeOnline before calling any other cmdlet.";

    [Fact]
    public async Task ACloudCalendarSet_WhoseSessionDropsOnceSent_IsUnconfirmed_AndNoAddIsSent()
    {
        // The Set was sent and its session dropped before Exchange answered. The catch sent the Add fallback on the dead
        // session, which met the pre-flight error, and the outcome read that as "nothing ran": filed Failed.
        _h.Failures["Set-MailboxFolderPermission"] = SessionLost;
        _h.Failures["Add-MailboxFolderPermission"] = PreFlight;

        var result = await Cloud("SetCalendarPermission", _h.Pool);

        Assert.False(result.Success);
        Assert.Equal(new[] { CalendarPermissionService.CalendarRight }, result.UnconfirmedRights!);
        Assert.Contains($"could not be confirmed: {SessionLost}", result.Message, StringComparison.Ordinal);
        Assert.Single(_h.CallsTo("Set-MailboxFolderPermission"));
        Assert.Empty(_h.CallsTo("Add-MailboxFolderPermission"));
    }

    [Fact]
    public async Task ACloudCalendarSet_ThatExchangeAnswersHasNoEntry_AddsOne()
    {
        _h.Failures["Set-MailboxFolderPermission"] = NoEntry;

        var result = await Cloud("SetCalendarPermission", _h.Pool);

        Assert.True(result.Success, result.Message);
        Assert.Single(_h.CallsTo("Add-MailboxFolderPermission"));
    }

    [Fact]
    public async Task ACloudCalendarSet_ThatExchangeRefusesOtherwise_IsFailed_AndNoAddIsSent()
    {
        // Any refusal but "no entry" is the write's own answer: an Add would meet it too.
        _h.Failures["Set-MailboxFolderPermission"] = "The user isn't valid for this folder.";

        var result = await Cloud("SetCalendarPermission", _h.Pool);

        Assert.False(result.Success);
        Assert.False(result.Unconfirmed);
        Assert.Empty(_h.CallsTo("Add-MailboxFolderPermission"));
    }

    [Fact]
    public async Task ACloudCalendarSet_WhoseSessionDrops_IsNeverFollowedByAnAdd_WhateverItsWords()
    {
        // The session's failure is read first: one the attempt reads as its session's stops the write even when its words
        // also name no entry - whether the Set took is unknown, and an Add would be sent on a session that failed.
        _h.Failures["Set-MailboxFolderPermission"] = $"{NoEntry} {SessionLost}";

        var result = await Cloud("SetCalendarPermission", _h.Pool);

        Assert.False(result.Success);
        Assert.True(result.Unconfirmed, result.Message);
        Assert.Empty(_h.CallsTo("Add-MailboxFolderPermission"));
    }

    [Theory]
    [InlineData(SessionLost, true)]
    [InlineData(PreFlight, false)]
    public async Task ACloudCalendarSetsAddFallback_ReadsItsOwnFailure(string addFailure, bool unconfirmed)
    {
        // After Exchange's "no entry": an Add whose session drops may have taken; one that meets the pre-flight error ran not
        // at all, and the Set had been refused - nothing was written.
        _h.Failures["Set-MailboxFolderPermission"] = NoEntry;
        _h.Failures["Add-MailboxFolderPermission"] = addFailure;

        var result = await Cloud("SetCalendarPermission", _h.Pool);

        Assert.False(result.Success);
        Assert.Equal(unconfirmed, result.Unconfirmed);
        Assert.Single(_h.CallsTo("Add-MailboxFolderPermission"));
    }

    [Theory]
    [InlineData("There is no existing permission entry found for user: jdoe.", true)]
    [InlineData("THERE IS NO EXISTING PERMISSION ENTRY FOUND FOR USER: JDOE.", true)]
    [InlineData("The user isn't valid for this folder.", false)]
    [InlineData("the session to Exchange Online was lost", false)]
    public void OnlyExchangesNoEntryAnswer_NamesNoPermissionEntry(string message, bool named)
    {
        var failure = new InvalidOperationException("raw-text-kd43");
        failure.Data[ExchangeServiceBase.PsErrorsDataKey] = new[] { message };

        Assert.Equal(named, CalendarPermissionService.NamesNoPermissionEntry(failure));
        Assert.Equal(named, CalendarPermissionService.NamesNoPermissionEntry(new InvalidOperationException("wrapped", new InvalidOperationException(message))));
    }

    [Theory]
    [InlineData("SetCalendarPermission")]
    [InlineData("RemoveCalendarPermission")]
    public async Task ACloudCalendarWrite_ExchangeRefuses_IsFailed_AsBefore(string write)
    {
        _h.Failures["Set-MailboxFolderPermission"] = "There is no existing permission entry found for user: jdoe.";
        _h.Failures["Add-MailboxFolderPermission"] = "the permission could not be added";
        _h.Failures["Remove-MailboxFolderPermission"] = "There is no existing permission entry found for user: jdoe.";

        var result = await Cloud(write, _h.Pool);

        Assert.False(result.Success);
        Assert.False(result.Unconfirmed);
    }

    [Theory]
    [InlineData("AddMailboxPermissionsOnPrem", "Add-ADPermission", "granted")]
    [InlineData("RemoveMailboxPermissionsOnPrem", "Remove-ADPermission", "removed")]
    public async Task AnOnPremMailboxRight_WhoseSessionBreaksOnceSent_IsUnconfirmed(string write, string command, string verb)
    {
        // TryRightOnPrem filed every failure Failed. A remoting transport failure, whose words name no connection, is the
        // session's - its type says so.
        _h.OnPremTransportFailures[command] = TransportFailure;

        var result = await OnPrem(write);

        Assert.False(result.Success);
        Assert.Equal(new[] { "FullAccess" }, result.AppliedRights!);
        Assert.Equal(new[] { "SendAs" }, result.UnconfirmedRights!);
        Assert.Equal($"{char.ToUpperInvariant(verb[0])}{verb[1..]} FullAccess on {Typed} (on-premises). Whether SendAs was {verb} on {Typed} (on-premises) could not be confirmed: {TransportFailure}. Check the permissions before trying again.",
            result.Message);
        Assert.Single(_h.CallsTo(command));
    }

    [Theory]
    [InlineData("SetCalendarPermissionOnPrem", "Set-MailboxFolderPermission")]
    [InlineData("RemoveCalendarPermissionOnPrem", "Remove-MailboxFolderPermission")]
    public async Task AnOnPremCalendarWrite_WhoseSessionBreaksOnceSent_IsUnconfirmed(string write, string command)
    {
        _h.OnPremTransportFailures[command] = TransportFailure;

        var result = await OnPrem(write);

        Assert.False(result.Success);
        Assert.Equal(new[] { CalendarPermissionService.CalendarRight }, result.UnconfirmedRights!);
        Assert.Contains($"could not be confirmed: {TransportFailure}. Check the permissions before trying again.", result.Message, StringComparison.Ordinal);
        Assert.Single(_h.CallsTo(command));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABulkRow_WhoseWriteIsUnconfirmed_IsRecordedAsUndetermined(bool mailbox)
    {
        // The bulk loops filed a row by its success alone: a row whose outcome is unknown read as Failed there too.
        _h.Failures[mailbox ? "Add-MailboxPermission" : "Set-MailboxFolderPermission"] = SessionLost;
        _h.Failures["Add-MailboxFolderPermission"] = SessionLost;
        var resolver = ExchangeWriteHarness.ExchangeKnows(Typed, "shared@contoso.test", Checked);
        var email = Substitute.For<EmailService>(_h.Config, Substitute.For<ILogger<EmailService>>());
        _h.Forest.AddUser("jdoe", upn: Trustee, mail: Trustee, proxies: [$"SMTP:{Trustee}"]);
        var validator = _h.NothingProtectedValidator(resolver);

        var bulk = mailbox
            ? await MailboxService(_h.Pool).ProcessMailboxPermissionsCsvAsync(
                Csv("Target,User,FullAccess,SendAs,AutoMapping\n" + $"{Typed},{Trustee},True,False,False\n"), isAdd: true, validator,
                ExchangeWriteHarness.OperatorLogonName, _h.Audit, email, "10.0.0.5", "INC100", actingUser: _h.Operator)
            : await CalendarService(_h.Pool).ProcessCalendarPermissionsCsvAsync(
                Csv("Target,User,AccessRight\n" + $"{Typed},{Trustee},Reviewer\n"), isSet: true, validator,
                ExchangeWriteHarness.OperatorLogonName, _h.Audit, email, "10.0.0.5", "INC100", actingUser: _h.Operator);

        Assert.Equal(0, bulk.SuccessCount);
        var row = Assert.Single(_h.AuditRows(), r => r.GetProperty("action").GetString() == (mailbox ? "BulkAdd_FullAccess" : "BulkSetCalendar"));
        Assert.Equal("Undetermined", row.GetProperty("result").GetString());
        Assert.Contains("could not be confirmed", row.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    // ---- the bulk loops ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ABulkRowWhoseWriteCannotConnect_IsRecordedAsBefore_InTheCausesOwnWords(bool mailbox)
    {
        // The bulk loops record a row's throw by its message alone, and the message of a write never sent is its cause's: the
        // row's error row and its entry read as they did before aud-a76.
        var failure = new InvalidOperationException("the connect was refused");
        using var pool = PoolThatCannotConnect(failure);
        var resolver = ExchangeWriteHarness.ExchangeKnows(Typed, "shared@contoso.test", Checked);
        var email = Substitute.For<EmailService>(_h.Config, Substitute.For<ILogger<EmailService>>());
        // aud-a69: the trustee is someone - the directory holds their account.
        _h.Forest.AddUser("jdoe", upn: Trustee, mail: Trustee, proxies: [$"SMTP:{Trustee}"]);
        var validator = _h.NothingProtectedValidator(resolver);

        var bulk = mailbox
            ? await MailboxService(pool).ProcessMailboxPermissionsCsvAsync(
                Csv("Target,User,FullAccess,SendAs,AutoMapping\n" + $"{Typed},{Trustee},True,False,False\n"), isAdd: true, validator,
                ExchangeWriteHarness.OperatorLogonName, _h.Audit, email, "10.0.0.5", "INC100", actingUser: _h.Operator)
            : await CalendarService(pool).ProcessCalendarPermissionsCsvAsync(
                Csv("Target,User,AccessRight\n" + $"{Typed},{Trustee},Reviewer\n"), isSet: true, validator,
                ExchangeWriteHarness.OperatorLogonName, _h.Audit, email, "10.0.0.5", "INC100", actingUser: _h.Operator);

        Assert.Equal(0, bulk.SuccessCount);
        var entry = Assert.Single(bulk.Entries);
        Assert.Equal("ERROR", entry.Status);
        Assert.Equal(failure.Message, entry.Message);
        Assert.Equal($"{Typed}/{Trustee}: {failure.Message}", Assert.Single(bulk.Errors));
        var row = Assert.Single(_h.AuditRows(), r => r.GetProperty("action").GetString() == (mailbox ? "BulkAdd_Error" : "BulkSetCalendar_Error"));
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal(failure.Message, row.GetProperty("error").GetString());
    }

    private static MemoryStream Csv(string text) => new(Encoding.UTF8.GetBytes(text));
}
