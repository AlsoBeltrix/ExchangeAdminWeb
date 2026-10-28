using System.Collections;
using System.Management.Automation;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-81, the cloud half. The Mailbox Permissions and Calendar Permissions single Exchange Online change ran under an
/// activity whose token reached neither the change nor the location read before it, and the change's wait for a pool
/// connection takes no token: a cancel made during that wait was not seen, the change was made once a connection freed, and
/// it was filed and notified as made. The change now takes the activity's token. A token already cancelled starts nothing
/// (kd-76's shape); inside the pooled call, the connection held, the token is looked at immediately before the first write
/// command (kd-75's shape), so a cancel during the wait ends the change with nothing sent - shown and filed Cancelled in
/// kd-57's words, no one notified (kd-59's filing). The location read takes it where the seam exists: its on-premises
/// credential fetch (kd-37). Nothing after the first write command looks at it, and only the activity's own token is the
/// operator's cancel. kd-58 A6 and A7 (the supervisor's ruling, 2026-10-28): the change's own wait for a pool connection takes
/// the token too, so a cancel during it ends the change at once, nothing asked of Exchange; kd-81's look stays, for a cancel
/// made during the change's reads.
/// </summary>
/// <remarks>
/// The real services over <see cref="ExchangeWriteHarness"/>'s stand-in cmdlets on real pools; the pages' <c>SubmitSingle</c>
/// driven by reflection, as <c>PermissionPagesSelfGrantTests</c> drives it, with an activity whose token the test cancels as
/// the navigation guard's OK does. The pool is saturated as kd-75's test saturates it - every connection it admits held by
/// the test - and a borrow waiting is read from the pool's own trace, where every borrow writes <c>ExoPoolSlotRequested</c>
/// before it waits. Every wait is bounded. No Exchange, no vault, no directory.
/// </remarks>
public sealed class PermissionCloudChangeCancellationTests : IDisposable
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const string MailboxPage = "Mailbox Permissions";
    private const string CalendarPage = "Calendar Permissions";
    private const string Logon = @"CONTOSO\jdoe";
    private const string Upn = "jdoe@contoso.test";
    private const string Target = "shared@contoso.test";
    private const string Other = "other@contoso.test";
    private const string Cancelled = "Cancelled by the operator before the change was sent. Nothing was written.";

    /// <summary>The connections <see cref="ExoConnectionPool"/> admits at once: its slots.</summary>
    private const int PoolConnections = 5;

    private static readonly Guid TargetMailbox = Guid.Parse("0f0f0f0f-3333-4444-8555-777777777777");
    private static readonly TimeSpan Bound = CancellableVaultCredentials.Bound;

    /// <summary>kd-58: "at once" - far inside the pool's two-minute wait for a connection.</summary>
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(10);

    private readonly ExchangeWriteHarness _h = new();
    private readonly ClaimsPrincipal _jdoe;
    private CancellableVaultCredentials? _credentials;
    private EmailService? _email;

    public PermissionCloudChangeCancellationTests()
    {
        _h.AddMailbox(Target, TargetMailbox);
        var account = _h.Forest.AddUser("jdoe", upn: Upn, mail: Upn, proxies: [$"SMTP:{Upn}"]);
        _jdoe = SelfGrantDirectoryStandIn.Principal(Logon, account.Sid);
        _h.Forest.AddUser("other", upn: Other, mail: Other, proxies: [$"SMTP:{Other}"]);
    }

    public void Dispose()
    {
        _credentials?.Dispose();
        _h.Dispose();
    }

    /// <summary>Each page's two single changes: the grant (tab 0) and the removal (tab 1).</summary>
    public static TheoryData<string, bool> Changes => new()
    {
        { MailboxPage, true }, { MailboxPage, false }, { CalendarPage, true }, { CalendarPage, false },
    };

    public static TheoryData<string> Pages => [MailboxPage, CalendarPage];

    /// <summary>
    /// kd-58 A6, A7: the single changes whose own wait for a pool connection takes the token - Mailbox Permissions' (A6).
    /// </summary>
    public static TheoryData<string, bool> ChangesWhoseWaitTakesTheToken => new() { { MailboxPage, true }, { MailboxPage, false } };

    /// <summary>The single changes whose wait does not take it yet - Calendar Permissions', until A7: kd-81's look sees the cancel.</summary>
    public static TheoryData<string, bool> ChangesWhoseWaitTakesNoToken => new() { { CalendarPage, true }, { CalendarPage, false } };

    private static string ModuleOf(string page) => page == MailboxPage ? "MailboxPermissions" : "CalendarPermissions";

    private CancellableVaultCredentials Credentials(string page) => _credentials ??= new CancellableVaultCredentials(ModuleOf(page));

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    // ---- the services ------------------------------------------------------------------------------------------------

    private ExchangeServiceBase CloudService(string page, ExoConnectionPool pool) => page == MailboxPage
        ? new MailboxPermissionService(pool, null!, NullLogger<MailboxPermissionService>.Instance, _h.Config, null!, _h.Trace)
        : new CalendarPermissionService(pool, null!, NullLogger<CalendarPermissionService>.Instance, _h.Config, null!, _h.Trace);

    /// <summary>
    /// <paramref name="page"/>'s single change of <see cref="Other"/>'s access to <see cref="Target"/> - Full Access and Send
    /// As, or Reviewer - over <paramref name="pool"/>, under <paramref name="cancellationToken"/>.
    /// </summary>
    private Task<PermissionResult> Change(string page, bool grant, ExoConnectionPool pool, CancellationToken cancellationToken)
    {
        var validated = ExchangeWriteHarness.Validated(TargetMailbox);
        var service = CloudService(page, pool);
        return (page, grant) switch
        {
            (MailboxPage, true) => ((MailboxPermissionService)service).AddMailboxPermissionsAsync(
                Target, validated, Other, fullAccess: true, sendAs: true, autoMapping: false, cancellationToken),
            (MailboxPage, false) => ((MailboxPermissionService)service).RemoveMailboxPermissionsAsync(
                Target, validated, Other, fullAccess: true, sendAs: true, cancellationToken),
            (CalendarPage, true) => ((CalendarPermissionService)service).SetCalendarPermissionAsync(
                Target, validated, Other, CalendarAccessRight.Reviewer, cancellationToken),
            (CalendarPage, false) => ((CalendarPermissionService)service).RemoveCalendarPermissionAsync(
                Target, validated, Other, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(page), page, "not a page these tests drive"),
        };
    }

    /// <summary>
    /// The change's first write command, the one after it, and Exchange's answer to the first that leads to the second when
    /// one does: Mailbox Permissions sends its second right whatever the first's answer; a Calendar Permissions Set that
    /// finds no entry falls back to an Add; a Calendar removal whose session answers with the pre-flight error is run again
    /// by the pool.
    /// </summary>
    private static (string First, string Then, string? FirstAnswers) WriteCommands(string page, bool grant) => (page, grant) switch
    {
        (MailboxPage, true) => ("Add-MailboxPermission", "Add-RecipientPermission", null),
        (MailboxPage, false) => ("Remove-MailboxPermission", "Remove-RecipientPermission", null),
        (_, true) => ("Set-MailboxFolderPermission", "Add-MailboxFolderPermission", "There is no existing permission entry found for user: other."),
        _ => ("Remove-MailboxFolderPermission", "Remove-MailboxFolderPermission", "You must call Connect-ExchangeOnline before calling any other cmdlet."),
    };

    // kd-81, the finding at the service: every connection the pool admits is held elsewhere, the change waits for one, and the
    // operator cancels during that wait. Before, nothing looked at the token again, and once a connection freed the change was
    // sent. The look inside the pooled call ends it there: its reads ran, its write command did not, and it leaves as a
    // cancellation never sent - what the page files Cancelled. kd-58: a change whose wait takes the token ends sooner (below).
    [Theory]
    [MemberData(nameof(ChangesWhoseWaitTakesNoToken))]
    public async Task ACancelWhileTheChangeWaitsForAPoolConnection_SendsNothing_AndLeavesAsACancelNeverSent(string page, bool grant)
    {
        using var cancel = new CancellationTokenSource();
        var held = await HoldEveryPoolConnection(_h.Pool);
        Exception? outcome;
        try
        {
            var change = Change(page, grant, _h.Pool, cancel.Token);
            await WaitUntil(() => BorrowsWaitingForAPoolConnection() == 1, "the change waits for a pool connection");
            Assert.False(change.IsCompleted);
            await cancel.CancelAsync();
            ReturnOne(_h.Pool, held);
            outcome = await Record.ExceptionAsync(() => change.WaitAsync(Bound, TestContext.Current.CancellationToken));
        }
        finally
        {
            ReturnAll(_h.Pool, held);
        }

        Assert.Empty(_h.Writes); // first: a change sent after the cancel fails here, naming what it sent
        Assert.NotEmpty(_h.CallsTo("Get-Recipient")); // the call ran once a connection freed: its look ended it, not the wait
        var notSent = Assert.IsType<PermissionWriteNotSubmittedException>(outcome);
        Assert.IsAssignableFrom<OperationCanceledException>(notSent.InnerException);
        Assert.True(PermissionChangeFailure.CancelledBeforeItWasSent(notSent, cancel.Token)); // what the page files Cancelled
    }

    // kd-58 A6 (E4): the change's own wait for a pool connection takes the token, so a cancel during it ends the change at
    // once - every connection still held elsewhere - with nothing asked of Exchange, no read and no write. It leaves as a
    // cancellation never sent. Before, the wait ran on until a connection freed, and the look inside the call ended it there.
    [Theory]
    [MemberData(nameof(ChangesWhoseWaitTakesTheToken))]
    public async Task ACancelWhileTheChangeWaitsForAPoolConnection_EndsItAtOnce_NothingAsked(string page, bool grant)
    {
        using var cancel = new CancellationTokenSource();
        var held = await HoldEveryPoolConnection(_h.Pool);
        var change = Task.FromResult(PermissionResult.Fail("not started"));
        Exception? outcome;
        try
        {
            change = Change(page, grant, _h.Pool, cancel.Token);
            await WaitUntil(() => BorrowsWaitingForAPoolConnection() == 1, "the change waits for a pool connection");
            Assert.False(change.IsCompleted);
            await cancel.CancelAsync();

            outcome = await Record.ExceptionAsync(() => change.WaitAsync(Promptly, TestContext.Current.CancellationToken)); // every connection still held
        }
        finally
        {
            ReturnAll(_h.Pool, held);
            await Record.ExceptionAsync(() => change.WaitAsync(Bound, TestContext.Current.CancellationToken));
        }

        Assert.Empty(_h.Writes); // first: a change sent after the cancel fails here, naming what it sent
        Assert.Empty(_h.Calls); // nothing asked of Exchange: the change never had a connection
        var notSent = Assert.IsType<PermissionWriteNotSubmittedException>(outcome);
        Assert.IsAssignableFrom<OperationCanceledException>(notSent.InnerException);
        Assert.True(PermissionChangeFailure.CancelledBeforeItWasSent(notSent, cancel.Token)); // what the page files Cancelled
    }

    // kd-81's look inside the call, kept: a cancel made once the change has its connection - here as it reads the recipient -
    // is seen immediately before the first write command. Its reads ran, nothing was written, and it leaves as a cancellation
    // never sent.
    [Theory]
    [MemberData(nameof(ChangesWhoseWaitTakesTheToken))]
    public async Task ACancelDuringTheChangesReads_SendsNothing_AndLeavesAsACancelNeverSent(string page, bool grant)
    {
        using var cancel = new CancellationTokenSource();
        using var pool = Pool(observe: (_, call) =>
        {
            if ((string)call["__Command"]! == "Get-Recipient")
                cancel.Cancel();
        });

        var outcome = await Record.ExceptionAsync(() => Change(page, grant, pool, cancel.Token).WaitAsync(Bound, TestContext.Current.CancellationToken));

        Assert.Empty(_h.Writes); // first: a change sent after the cancel fails here, naming what it sent
        Assert.NotEmpty(_h.CallsTo("Get-Recipient")); // its reads ran: the look before the first write ended it
        var notSent = Assert.IsType<PermissionWriteNotSubmittedException>(outcome);
        Assert.IsAssignableFrom<OperationCanceledException>(notSent.InnerException);
        Assert.True(PermissionChangeFailure.CancelledBeforeItWasSent(notSent, cancel.Token));
    }

    // kd-76's shape: a token already cancelled - the operator cancelled during the page's checks before the change - starts
    // nothing. The pool is never asked for a connection; nothing is read or sent.
    [Theory]
    [MemberData(nameof(Changes))]
    public async Task AChangeWhoseTokenIsAlreadyCancelled_StartsNothing_AndLeavesAsACancelNeverSent(string page, bool grant)
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var outcome = await Record.ExceptionAsync(
            () => Change(page, grant, _h.Pool, cancel.Token).WaitAsync(Bound, TestContext.Current.CancellationToken));

        Assert.Equal(0, PoolSlotRequests()); // first: a change that started fails here
        Assert.Equal(0, _h.Connects);
        Assert.Empty(_h.Calls);
        var notSent = Assert.IsType<PermissionWriteNotSubmittedException>(outcome);
        Assert.IsAssignableFrom<OperationCanceledException>(notSent.InnerException);
    }

    // kd-59's rule, kept: nothing after the first write command looks at the token. Cancelled as that command is sent, the
    // change runs to its end as it always did - the command after it is still sent - and its result stands.
    [Theory]
    [MemberData(nameof(Changes))]
    public async Task ACancelAsTheFirstWriteCommandIsSent_LetsTheChangeRunToItsEnd(string page, bool grant)
    {
        var (first, then, firstAnswers) = WriteCommands(page, grant);
        if (firstAnswers is not null)
            _h.Failures[first] = firstAnswers;
        using var cancel = new CancellationTokenSource();
        var firstSent = 0;
        using var pool = Pool(observe: (_, call) =>
        {
            var command = (string)call["__Command"]!;
            if (command == first && Interlocked.Increment(ref firstSent) == 1)
                cancel.Cancel();
            else if (command == "Get-Mailbox" && Volatile.Read(ref firstSent) == 1)
                _h.Failures.Remove(first); // a run of the change again: its session answers this time
        });

        var result = await Change(page, grant, pool, cancel.Token).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal(new[] { first, then }, _h.Writes); // first: a change stopped after its first command fails here
        Assert.True(result.Success, result.Message);
    }

    // ---- the pages ---------------------------------------------------------------------------------------------------

    // kd-81, the finding at the page: the activity's token cancelled while the change waits for a pool connection - every
    // connection the pool admits held elsewhere once the location read has given its own back. Before, the change was made once
    // a connection freed, and filed and notified as made. Now it ends there, nothing written: shown and filed Cancelled in
    // kd-57's words, and no one told - not the administrators, the user or the mailbox owner. kd-58: sooner where the wait
    // takes the token (below).
    [Theory]
    [MemberData(nameof(ChangesWhoseWaitTakesNoToken))]
    public async Task ACancelOfTheChangesActivity_WhileTheChangeWaitsForAPoolConnection_IsFiledCancelled_NothingWritten_NoOneTold(
        string page, bool grant)
    {
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var testToken = TestContext.Current.CancellationToken;
        var asked = 0;
        var lastConnection = new TaskCompletionSource<Task<PooledRunspace>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pool = Pool(observe: (itself, call) =>
        {
            // The location read's Get-Mailbox, the first call on this pool, runs on the last connection the pool admits: a
            // borrow asked for now is handed that connection as the read gives it back - before the change asks for one.
            if ((string)call["__Command"]! == "Get-Mailbox" && Interlocked.Exchange(ref asked, 1) == 0)
                lastConnection.SetResult(itself.BorrowAsync(testToken));
        });
        var held = new List<PooledRunspace>();
        for (var i = 0; i < PoolConnections - 1; i++)
            held.Add(await pool.BorrowAsync(testToken));
        var component = SubmitPage(page, grant, progress, CloudService(page, pool));

        var run = SubmitSingle(component);
        try
        {
            var borrow = await lastConnection.Task.WaitAsync(Bound, testToken);
            held.Add(await borrow.WaitAsync(Bound, testToken));
            await WaitUntil(() => BorrowsWaitingForAPoolConnection() == 1, "the change waits for a pool connection");
            Assert.False(run.IsCompleted);
            await cancel.CancelAsync();
            ReturnOne(pool, held);

            await run.WaitAsync(Bound, testToken);
        }
        finally
        {
            ReturnAll(pool, held);
        }

        Assert.Empty(_h.Writes); // first: a change made after the cancel fails here, naming what it sent
        Assert.NotEmpty(_h.CallsTo("Get-Recipient")); // the change's call ran once a connection freed: its look ended it
        AssertFiledCancelled(page, grant, component, progress);
    }

    // kd-58 A6, at the page: the activity's token cancelled while the change waits for a pool connection - every connection
    // the pool admits held elsewhere once the location read has given its own back - ends the change at once, every
    // connection still held: the change's call never runs, nothing is written, and it is filed Cancelled in kd-57's words with
    // no one told. Before, it waited for a connection, read, and only then did its look end it.
    [Theory]
    [MemberData(nameof(ChangesWhoseWaitTakesTheToken))]
    public async Task ACancelOfTheChangesActivity_WhileTheChangeWaitsForAPoolConnection_IsFiledCancelled_AtOnce_NothingAsked(
        string page, bool grant)
    {
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var testToken = TestContext.Current.CancellationToken;
        var asked = 0;
        var lastConnection = new TaskCompletionSource<Task<PooledRunspace>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var pool = Pool(observe: (itself, call) =>
        {
            // As above: the location read's Get-Mailbox runs on the last connection the pool admits, and a borrow asked for now
            // is handed it as the read gives it back - before the change asks for one.
            if ((string)call["__Command"]! == "Get-Mailbox" && Interlocked.Exchange(ref asked, 1) == 0)
                lastConnection.SetResult(itself.BorrowAsync(testToken));
        });
        var held = new List<PooledRunspace>();
        for (var i = 0; i < PoolConnections - 1; i++)
            held.Add(await pool.BorrowAsync(testToken));
        var component = SubmitPage(page, grant, progress, CloudService(page, pool));

        var run = SubmitSingle(component);
        try
        {
            var borrow = await lastConnection.Task.WaitAsync(Bound, testToken);
            held.Add(await borrow.WaitAsync(Bound, testToken));
            await WaitUntil(() => BorrowsWaitingForAPoolConnection() == 1, "the change waits for a pool connection");
            Assert.False(run.IsCompleted);
            await cancel.CancelAsync();

            await run.WaitAsync(Promptly, testToken); // every connection still held
        }
        finally
        {
            ReturnAll(pool, held);
            await Record.ExceptionAsync(() => run.WaitAsync(Bound, testToken));
        }

        Assert.Empty(_h.Writes); // first: a change made after the cancel fails here, naming what it sent
        Assert.Empty(_h.CallsTo("Get-Recipient")); // the change's call never ran: its wait ended it
        AssertFiledCancelled(page, grant, component, progress);
    }

    // kd-76's shape at the page: the operator cancels during the page's checks before the change - here its access re-check.
    // kd-58 A2: the location read's Exchange Online half takes the token now, so it starts nothing either - it used to run,
    // find the mailbox in the cloud, and only then hand over to the change's look as it starts. No connection asked for,
    // nothing read or sent. Filed Cancelled.
    [Theory]
    [MemberData(nameof(Changes))]
    public async Task ACancelDuringTheChecksBeforeTheChange_StartsNothing_AndIsFiledCancelled(string page, bool grant)
    {
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(_ =>
            {
                cancel.Cancel();
                return Task.FromResult(AuthorizationResult.Success());
            });
        var component = SubmitPage(page, grant, progress, CloudService(page, _h.Pool), authorization);

        await SubmitSingle(component).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Empty(_h.Writes); // first, as above
        Assert.Equal(0, PoolSlotRequests()); // kd-58 A2: not even the location read's borrow
        Assert.Empty(_h.CallsTo("Get-Mailbox"));
        Assert.Empty(_h.CallsTo("Get-Recipient"));
        AssertFiledCancelled(page, grant, component, progress);
    }

    // kd-58 A2, at the page: the location read's Exchange Online half takes the activity's token, so a cancel while that read
    // waits for a pool connection - every connection the pool admits held elsewhere - ends the change at once, with nothing
    // asked of Exchange: filed Cancelled, nothing written, no one told. Before, the read waited until a connection freed, ran
    // Get-Mailbox, and only then did the change's look as it starts see the cancel.
    [Theory]
    [MemberData(nameof(Pages))]
    public async Task ACancelWhileTheLocationReadWaitsForAPoolConnection_IsFiledCancelled_AtOnce_NothingAsked(string page)
    {
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var held = await HoldEveryPoolConnection(_h.Pool);
        var component = SubmitPage(page, grant: true, progress, CloudService(page, _h.Pool));
        Task? run = null;
        try
        {
            run = SubmitSingle(component);
            await WaitUntil(() => BorrowsWaitingForAPoolConnection() == 1, "the location read waits for a pool connection");
            await cancel.CancelAsync();

            await run.WaitAsync(Promptly, TestContext.Current.CancellationToken); // with every connection still held
        }
        finally
        {
            ReturnAll(_h.Pool, held);
            if (run is not null)
                await Record.ExceptionAsync(() => run.WaitAsync(Bound, TestContext.Current.CancellationToken));
        }

        Assert.Empty(_h.Writes);
        Assert.Empty(_h.Calls); // nothing asked of Exchange at all: the five held ran nothing either
        AssertFiledCancelled(page, grant: true, component, progress);
    }

    // The location read takes the token where the seam exists: on a mailbox Exchange Online does not hold, it asks
    // on-premises Exchange, whose credential fetch takes the activity's token (kd-37's seam). A cancel during that fetch ends
    // the change before it started: filed Cancelled, nothing written, no one told. Before, the read ran its course.
    [Theory]
    [MemberData(nameof(Pages))]
    public async Task ACancelDuringTheLocationReadsOnPremCredentialFetch_IsFiledCancelled_NothingWritten_NoOneTold(string page)
    {
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        using var pool = PoolWhereExchangeOnlineHoldsNoMailbox();
        var component = SubmitPage(page, grant: true, progress, LocationReadService(page, pool, Credentials(page).Service));

        var run = SubmitSingle(component);
        await Credentials(page).SecretRequested.WaitAsync(Bound, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await run.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.True(Credentials(page).RequestCancelled);
        Assert.Empty(_h.Writes);
        Assert.Empty(_h.CallsTo("Get-Recipient")); // the change never started
        AssertFiledCancelled(page, grant: true, component, progress);
    }

    // kd-57's rule, the control at the change: only the activity's own token is the operator's cancel. The change's borrow
    // fails with a cancellation nobody asked for - its connect gives up so - while the activity's token is live: that is the
    // connection's failure in its own words (aud-a76), filed Failed, never the operator's cancel.
    [Theory]
    [MemberData(nameof(Pages))]
    public async Task ACancellationTheOperatorDidNotAskFor_AtTheChange_IsTheConnectionsFailure(string page)
    {
        var progress = new RecordingProgress();
        var drained = 0;
        using var pool = Pool(
            observe: (itself, _) =>
            {
                // The location read's connection retired as it runs, so the change's borrow connects again - and that fails.
                if (Interlocked.Exchange(ref drained, 1) == 0)
                    itself.DrainPool();
            },
            connectFailure: new OperationCanceledException("the sign-in was abandoned"));
        var component = SubmitPage(page, grant: true, progress, CloudService(page, pool));

        await SubmitSingle(component).WaitAsync(Bound, TestContext.Current.CancellationToken);

        const string Failure = "Nothing was written: the connection to Exchange failed: the sign-in was abandoned.";
        var shown = Assert.IsType<PermissionResult>(GetField(component, "result"));
        Assert.Equal(Failure, shown.Message);
        Assert.Equal((false, Failure), Assert.Single(progress.Handles).Outcome);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(ChangeAction(page, grant: true), Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(Failure, Text(record, "error"));
        Assert.Empty(_h.Writes);
    }

    // And the control at the location read: its on-premises credential fetch throws a cancellation nobody asked for, the
    // activity's token live. That is a location read that failed, refused in its own words before any write (aud-a23o) and
    // filed Failed - not the operator's cancel.
    [Theory]
    [MemberData(nameof(Pages))]
    public async Task ACancellationTheOperatorDidNotAskFor_AtTheLocationRead_IsALocationReadThatFailed(string page)
    {
        var progress = new RecordingProgress();
        using var pool = PoolWhereExchangeOnlineHoldsNoMailbox();
        var component = SubmitPage(page, grant: true, progress,
            LocationReadService(page, pool, null!, credentialFailure: new OperationCanceledException("the credential read was abandoned")));

        await SubmitSingle(component).WaitAsync(Bound, TestContext.Current.CancellationToken);

        const string Refusal = $"Could not determine mailbox location for {Target}: the credential read was abandoned. Nothing was written.";
        var shown = Assert.IsType<PermissionResult>(GetField(component, "result"));
        Assert.Equal(Refusal, shown.Message);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(ChangeAction(page, grant: true), Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(Refusal, Text(record, "error"));
        Assert.Empty(_h.Writes);
    }

    // ---- the page harness --------------------------------------------------------------------------------------------

    /// <summary>The row a page files for its single change: Mailbox Permissions' a Full Access change, Calendar's a Reviewer one.</summary>
    private static string ChangeAction(string page, bool grant) => (page, grant) switch
    {
        (MailboxPage, true) => "AddFullAccess",
        (MailboxPage, false) => "RemoveFullAccess",
        (_, true) => "SetCalendar",
        _ => "RemoveCalendar",
    };

    /// <summary>
    /// The operator's cancel as both pages file it, before anything was sent: shown, the activity ended, the one row filed
    /// Cancelled with the identity it was bound to, the page free - and no notification to anyone.
    /// </summary>
    private void AssertFiledCancelled(string page, bool grant, ComponentBase component, RecordingProgress progress)
    {
        var shown = Assert.IsType<PermissionResult>(GetField(component, "result"));
        Assert.False(shown.Success);
        Assert.Equal(Cancelled, shown.Message);
        Assert.Equal((false, Cancelled), Assert.Single(progress.Handles).Outcome);
        Assert.False((bool)GetField(component, "isLoading")!);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(ChangeAction(page, grant), Text(record, "action"));
        Assert.Equal("Cancelled", Text(record, "result"));
        Assert.Equal(Cancelled, Text(record, "error"));
        Assert.Equal(TargetMailbox.ToString("D"), Text(record, ValidatedMailbox.AuditKey));
        Assert.Empty(Notifications());
    }

    /// <summary>Every mail the page asked to send: to the administrators, to the user, to the mailbox owner.</summary>
    private IReadOnlyList<string> Notifications() =>
        _email!.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Where(name => name is nameof(EmailService.SendAdminNotificationAsync) or nameof(EmailService.SendUserNotificationAsync)
                or nameof(EmailService.SendOwnerNotificationAsync))
            .ToList();

    /// <summary>
    /// <paramref name="page"/> ready for <c>SubmitSingle</c>: the operator jdoe granting <see cref="Other"/> Full Access or
    /// Reviewer on <see cref="Target"/> - or removing it - through <paramref name="service"/>, nothing protected, the access
    /// re-checks <paramref name="authorization"/>'s (else allowed), the activity <paramref name="progress"/>'s.
    /// </summary>
    private ComponentBase SubmitPage(string page, bool grant, RecordingProgress progress, ExchangeServiceBase service,
        IAuthorizationService? authorization = null)
    {
        ComponentBase component;
        if (page == MailboxPage)
        {
            component = new MailboxPermissions();
            Set(component, "MailboxService", service);
            Set(component, "Logger", NullLogger<MailboxPermissions>.Instance);
            SetField(component, "grantFullAccess", true);
            SetField(component, "grantSendAs", false);
        }
        else
        {
            component = new CalendarPermissions();
            Set(component, "CalendarService", service);
            Set(component, "Logger", NullLogger<CalendarPermissions>.Instance);
            SetField(component, "accessRight", CalendarAccessRight.Reviewer);
        }

        var auth = Substitute.For<AuthenticationStateProvider>();
        auth.GetAuthenticationStateAsync().Returns(new AuthenticationState(_jdoe));
        if (authorization is null)
        {
            authorization = Substitute.For<IAuthorizationService>();
            authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
                .Returns(AuthorizationResult.Success());
        }
        _email = Substitute.For<EmailService>(_h.Config, Substitute.For<ILogger<EmailService>>());

        Set(component, "AuthStateProvider", auth);
        Set(component, "AuthorizationService", authorization);
        Set(component, "ServiceNow", new ServiceNowService(_h.Config, NullLogger<ServiceNowService>.Instance, Substitute.For<IHttpClientFactory>()));
        Set(component, "Validator", _h.NothingProtectedValidator(ExchangeKnowsTheTargetAndTheOperator()));
        Set(component, "Audit", _h.Audit);
        Set(component, "Email", _email);
        Set(component, "Progress", progress);
        SetField(component, "currentUser", Logon);
        SetField(component, "tabIndex", grant ? 0 : 1);
        SetField(component, "targetMailbox", Target);
        SetField(component, "affectedUser", Other);
        SetField(component, "ticketNumber", "INC100");
        return component;
    }

    /// <summary>Exchange knows the target mailbox, and knows the operator by their UPN, so the grant to Other is no self-grant.</summary>
    private static IIdentityResolver ExchangeKnowsTheTargetAndTheOperator()
    {
        var resolver = ExchangeWriteHarness.ExchangeKnows(Target, Target, TargetMailbox);
        var own = new ResolvedRecipient("jdoe.mailbox@contoso.test", "ext-jdoe", "UserMailbox", ExistsOnPrem: true, ExchangeGuid: Guid.NewGuid());
        resolver.ResolveRecipientAsync(Upn).Returns(own);
        return resolver;
    }

    private static Task SubmitSingle(ComponentBase page) =>
        (Task)page.GetType().GetMethod("SubmitSingle", Members)!.Invoke(page, null)!;

    /// <summary>The harness's settings and an on-premises server, so the location read can ask on-premises Exchange.</summary>
    private IConfiguration WithAnOnPremServer() => new ConfigurationBuilder()
        .AddConfiguration(_h.Config)
        .AddInMemoryCollection(new Dictionary<string, string?> { ["OnPremExchange:ServerUri"] = "https://exchange.contoso.test/PowerShell/" })
        .Build();

    /// <summary>
    /// <paramref name="page"/>'s service whose location read asks on-premises Exchange - a server configured, the credential
    /// fetched through <paramref name="credentials"/> - or whose credential fetch throws <paramref name="credentialFailure"/>.
    /// </summary>
    private ExchangeServiceBase LocationReadService(string page, ExoConnectionPool pool, ModuleCredentialService credentials,
        Exception? credentialFailure = null) => page == MailboxPage
        ? new LocationReadMailboxService(pool, WithAnOnPremServer(), credentials, _h.Trace, credentialFailure)
        : new LocationReadCalendarService(pool, WithAnOnPremServer(), credentials, _h.Trace, credentialFailure);

    private sealed class LocationReadMailboxService(ExoConnectionPool pool, IConfiguration config, ModuleCredentialService credentials,
        OperationTraceService trace, Exception? credentialFailure)
        : MailboxPermissionService(pool, null!, NullLogger<MailboxPermissionService>.Instance, config, credentials, trace)
    {
        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose, CancellationToken cancellationToken) =>
            credentialFailure is { } failure
                ? Task.FromException<(string username, string password, string domain)?>(failure)
                : base.GetModuleCredentialsAsync(purpose, cancellationToken);
    }

    private sealed class LocationReadCalendarService(ExoConnectionPool pool, IConfiguration config, ModuleCredentialService credentials,
        OperationTraceService trace, Exception? credentialFailure)
        : CalendarPermissionService(pool, null!, NullLogger<CalendarPermissionService>.Instance, config, credentials, trace)
    {
        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose, CancellationToken cancellationToken) =>
            credentialFailure is { } failure
                ? Task.FromException<(string username, string password, string domain)?>(failure)
                : base.GetModuleCredentialsAsync(purpose, cancellationToken);
    }

    // ---- the pools ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// A pool of the test's own over the harness's stand-ins and settings. Every stand-in call is recorded in the harness's list
    /// as before, then shown to <paramref name="observe"/> - with the pool - on the pipeline's own thread, before the stand-in
    /// answers; every connect after the first throws <paramref name="connectFailure"/> when a test gives one.
    /// </summary>
    private ExoConnectionPool Pool(Action<ExoConnectionPool, Hashtable>? observe = null, Exception? connectFailure = null)
    {
        ExoConnectionPool? pool = null;
        var connects = 0;
        pool = new ExoConnectionPool(_h.Config, _h.ModuleConfig, _h.Enablement, NullLogger<ExoConnectionPool>.Instance, _h.Trace,
            (_, generation, _) =>
            {
                if (Interlocked.Increment(ref connects) > 1 && connectFailure is not null)
                    throw connectFailure;
                var runspace = _h.OpenExchangeRunspace();
                if (observe is not null)
                    runspace.SessionStateProxy.SetVariable("Calls", new ObservedCalls(_h.Calls, call => observe(pool!, call)));
                var ps = PowerShell.Create();
                ps.Runspace = runspace;
                return new PooledRunspace(runspace, ps, generation, "stand-in");
            });
        return pool;
    }

    /// <summary>The harness's call list: every call added there as before, then shown to <paramref name="observe"/>.</summary>
    private sealed class ObservedCalls(IList calls, Action<Hashtable> observe) : ArrayList
    {
        public override int Add(object? value)
        {
            var index = calls.Add(value);
            observe((Hashtable)value!);
            return index;
        }
    }

    /// <summary>
    /// A pool whose Exchange Online answers, as Exchange does, that it holds no such mailbox: an ObjectNotFound error, which the
    /// location read takes as an answer, so it asks on-premises Exchange next.
    /// </summary>
    private ExoConnectionPool PoolWhereExchangeOnlineHoldsNoMailbox() =>
        new(_h.Config, _h.ModuleConfig, _h.Enablement, NullLogger<ExoConnectionPool>.Instance, _h.Trace,
            (_, generation, _) =>
            {
                var runspace = _h.OpenExchangeRunspace();
                using (var define = PowerShell.Create())
                {
                    define.Runspace = runspace;
                    define.AddScript("""
                        function global:Get-Mailbox {
                            [CmdletBinding()] param([Parameter(Position = 0)] $Identity)
                            Write-StandInCall 'Get-Mailbox' $PSBoundParameters
                            Write-Error -Message "The operation couldn't be performed because object '$Identity' couldn't be found." -Category ObjectNotFound
                        }
                        """).Invoke();
                }
                var ps = PowerShell.Create();
                ps.Runspace = runspace;
                return new PooledRunspace(runspace, ps, generation, "stand-in");
            });

    /// <summary>Every connection <paramref name="pool"/> admits at once, borrowed and held, so its next borrow waits for one.</summary>
    private static async Task<List<PooledRunspace>> HoldEveryPoolConnection(ExoConnectionPool pool)
    {
        var held = new List<PooledRunspace>();
        for (var i = 0; i < PoolConnections; i++)
            held.Add(await pool.BorrowAsync(TestContext.Current.CancellationToken));
        return held;
    }

    private static void ReturnOne(ExoConnectionPool pool, List<PooledRunspace> held)
    {
        pool.Return(held[0]);
        held.RemoveAt(0);
    }

    private static void ReturnAll(ExoConnectionPool pool, List<PooledRunspace> held)
    {
        foreach (var runspace in held)
            pool.Return(runspace);
        held.Clear();
    }

    /// <summary>
    /// The pools' own trace, oldest first: each step's stage and result. Read while a borrow waits, so a line still being
    /// written can be incomplete; it is skipped and read whole the next time.
    /// </summary>
    private List<(string? Stage, string? Result)> PoolTrace()
    {
        var steps = new List<(string? Stage, string? Result)>();
        foreach (var path in _h.Jsonl.GetTraceLogPaths(DateTime.Now).Where(File.Exists))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using var step = JsonDocument.Parse(line);
                    steps.Add((step.RootElement.TryGetProperty("stage", out var s) ? s.GetString() : null,
                        step.RootElement.TryGetProperty("result", out var r) ? r.GetString() : null));
                }
                catch (JsonException)
                {
                    continue;
                }
            }
        }
        return steps;
    }

    /// <summary>How many borrows asked the pools for a slot - granted, refused or still waiting.</summary>
    private int PoolSlotRequests() => Requests(PoolTrace());

    // kd-58: a slot wait ends refused (Failed, the pool busy) or cancelled (Cancelled, the caller's token); neither is a request.
    private static int Requests(List<(string? Stage, string? Result)> trace) =>
        trace.Count(s => s.Stage == "ExoPoolSlotRequested" && s.Result is not ("Failed" or "Cancelled"));

    /// <summary>How many borrows are waiting for a free connection: a slot asked for that was neither granted nor refused.</summary>
    private int BorrowsWaitingForAPoolConnection()
    {
        var trace = PoolTrace();
        var refused = trace.Count(s => s.Stage == "ExoPoolSlotRequested" && s.Result is "Failed" or "Cancelled");
        var granted = trace.Count(s => s.Stage == "ExoPoolSlotAcquired");
        return Requests(trace) - granted - refused;
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {what}.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private static void Set(object page, string property, object? value) =>
        page.GetType().GetProperty(property, Members)!.SetValue(page, value);

    private static void SetField(object page, string field, object? value) =>
        page.GetType().GetField(field, Members)!.SetValue(page, value);

    private static object? GetField(object page, string field) =>
        page.GetType().GetField(field, Members)!.GetValue(page);
}
