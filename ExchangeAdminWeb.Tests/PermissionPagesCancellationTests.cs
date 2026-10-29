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
/// Finding kd-59, the permission pages. An on-premises change fetched its credential with no cancellation token, so an
/// operator's cancel left the vault request running and the change went on to be sent. The page's activity token now
/// reaches the fetch, which runs before the write's command is sent (aud-a76's mark), so a cancel there ends the change
/// unsent: filed Cancelled in the app's phrase for a cancel (kd-57), nothing written. A vault request that times out with
/// nobody cancelling is still the refusal it was (kd-14, kd-37); a cancellation the operator did not ask for is still a
/// failure in its own words.
/// </summary>
/// <remarks>
/// The pages' <c>ExecuteOnPrem</c> driven by reflection, as <c>PermissionPagesSelfGrantTests</c> drives it, over
/// <see cref="ExchangeWriteHarness"/>'s validator and on-premises stand-ins; the service's credential through a real
/// <see cref="ModuleCredentialService"/> and a real <see cref="DelineaService"/> whose secret request waits until its caller
/// cancels (<see cref="CancellableVaultCredentials"/>). Every wait is bounded. No vault, no directory, no Exchange.
/// </remarks>
public sealed class PermissionPagesCancellationTests : IDisposable
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const string MailboxPage = "Mailbox Permissions";
    private const string CalendarPage = "Calendar Permissions";
    private const string Logon = @"CONTOSO\jdoe";
    private const string Upn = "jdoe@contoso.test";
    private const string Target = "shared@contoso.test";
    private const string Other = "other@contoso.test";
    private static readonly Guid TargetMailbox = Guid.Parse("0f0f0f0f-3333-4444-8555-777777777777");
    private static readonly TimeSpan Bound = CancellableVaultCredentials.Bound;

    private readonly ExchangeWriteHarness _h = new();
    private readonly ClaimsPrincipal _jdoe;
    private CancellableVaultCredentials? _credentials;
    private EmailService? _email;

    public PermissionPagesCancellationTests()
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

    public static TheoryData<string> Pages => [MailboxPage, CalendarPage];

    /// <summary>Each page's two on-premises changes: the grant (tab 0) and the removal (tab 1).</summary>
    public static TheoryData<string, bool> Changes => new()
    {
        { MailboxPage, true }, { MailboxPage, false }, { CalendarPage, true }, { CalendarPage, false },
    };

    private static string ModuleOf(string page) => page == MailboxPage ? "MailboxPermissions" : "CalendarPermissions";

    private CancellableVaultCredentials Credentials(string page) => _credentials ??= new CancellableVaultCredentials(ModuleOf(page));

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    /// <summary>
    /// The on-premises Mailbox Permissions write, its credential through the fixture's vault - or the throw a test gives -
    /// and its session the harness's.
    /// </summary>
    private sealed class OnPremMailboxService : MailboxPermissionService
    {
        private readonly ExchangeWriteHarness _h;

        public OnPremMailboxService(ExchangeWriteHarness h, ModuleCredentialService credentials)
            : base(h.Pool, null!, NullLogger<MailboxPermissionService>.Instance, h.Config, credentials, h.Trace) => _h = h;

        public Exception? CredentialFailure { get; init; }

        /// <summary>Run as the on-premises session opens, before the write is sent: where a test makes the connect fail.</summary>
        public Action? Connect { get; init; }

        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose, CancellationToken cancellationToken) =>
            CredentialFailure is { } failure
                ? Task.FromException<(string username, string password, string domain)?>(failure)
                : base.GetModuleCredentialsAsync(purpose, cancellationToken);

        protected override void ConnectOnPrem(PowerShell ps, string username, string password, string domain, int maxRetries = 3, CancellationToken cancellationToken = default)
        {
            Connect?.Invoke();
            _h.DefineOnPremSession(ps);
        }
    }

    /// <summary>The on-premises Calendar Permissions write, as <see cref="OnPremMailboxService"/>.</summary>
    private sealed class OnPremCalendarService : CalendarPermissionService
    {
        private readonly ExchangeWriteHarness _h;

        public OnPremCalendarService(ExchangeWriteHarness h, ModuleCredentialService credentials)
            : base(h.Pool, null!, NullLogger<CalendarPermissionService>.Instance, h.Config, credentials, h.Trace) => _h = h;

        public Exception? CredentialFailure { get; init; }

        public Action? Connect { get; init; }

        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose, CancellationToken cancellationToken) =>
            CredentialFailure is { } failure
                ? Task.FromException<(string username, string password, string domain)?>(failure)
                : base.GetModuleCredentialsAsync(purpose, cancellationToken);

        protected override void ConnectOnPrem(PowerShell ps, string username, string password, string domain, int maxRetries = 3, CancellationToken cancellationToken = default)
        {
            Connect?.Invoke();
            _h.DefineOnPremSession(ps);
        }
    }

    private ComponentBase PageFor(string page, RecordingProgress progress, Exception? credentialFailure = null, bool grant = true,
        Action? connect = null)
    {
        var credentials = Credentials(page).Service;
        ComponentBase component;
        if (page == MailboxPage)
        {
            component = new MailboxPermissions();
            Set(component, "MailboxService", new OnPremMailboxService(_h, credentials) { CredentialFailure = credentialFailure, Connect = connect });
            Set(component, "Logger", NullLogger<MailboxPermissions>.Instance);
            SetField(component, "onPremFullAccess", true);
            SetField(component, "onPremSendAs", false);
        }
        else
        {
            component = new CalendarPermissions();
            Set(component, "CalendarService", new OnPremCalendarService(_h, credentials) { CredentialFailure = credentialFailure, Connect = connect });
            Set(component, "Logger", NullLogger<CalendarPermissions>.Instance);
            SetField(component, "onPremAccessRight", CalendarAccessRight.Reviewer);
        }

        var auth = Substitute.For<AuthenticationStateProvider>();
        auth.GetAuthenticationStateAsync().Returns(new AuthenticationState(_jdoe));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        _email = Substitute.For<EmailService>(_h.Config, Substitute.For<ILogger<EmailService>>());

        Set(component, "AuthStateProvider", auth);
        Set(component, "AuthorizationService", authorization);
        Set(component, "Validator", _h.NothingProtectedValidator(ExchangeKnowsTheTargetAndTheOperator()));
        Set(component, "Audit", _h.Audit);
        Set(component, "Email", _email);
        Set(component, "Progress", progress);
        SetField(component, "currentUser", Logon);
        SetField(component, "tabIndex", grant ? 0 : 1);
        SetField(component, "onPremTarget", Target);
        SetField(component, "onPremUser", Other);
        SetField(component, "onPremTicket", "INC100");
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

    private static Task ExecuteOnPrem(ComponentBase page) =>
        (Task)page.GetType().GetMethod("ExecuteOnPrem", Members)!.Invoke(page, null)!;

    private static string OnPremAction(string page, bool grant) => (page, grant) switch
    {
        (MailboxPage, true) => "OnPrem_AddFullAccess",
        (MailboxPage, false) => "OnPrem_RemoveFullAccess",
        (_, true) => "OnPrem_SetCalendar",
        _ => "OnPrem_RemoveCalendar",
    };

    private int AdminNotifications() =>
        _email!.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(EmailService.SendAdminNotificationAsync));

    // The finding at the page: the activity's token is the one the navigation guard cancels, and the page passes it. Before,
    // the vault request ran its course and the change was sent.
    [Theory]
    [MemberData(nameof(Changes))]
    public async Task ACancelOfTheOnPremActivity_DuringTheCredentialRead_IsFiledCancelled_WithNothingSent(string page, bool grant)
    {
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var component = PageFor(page, progress, grant: grant);

        var run = ExecuteOnPrem(component);
        await Credentials(page).SecretRequested.WaitAsync(Bound, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        await run.WaitAsync(Bound, TestContext.Current.CancellationToken);

        const string Cancelled = "Cancelled by the operator before the change was sent. Nothing was written.";
        Assert.True(Credentials(page).RequestCancelled);
        Assert.Empty(_h.CallsTo("Invoke-Command"));
        Assert.Empty(_h.Writes);
        var result = Assert.IsType<PermissionResult>(GetField(component, "result"));
        Assert.False(result.Success);
        Assert.Equal(Cancelled, result.Message);
        Assert.Equal((false, Cancelled), Assert.Single(progress.Handles).Outcome);
        Assert.False((bool)GetField(component, "isLoading")!);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(OnPremAction(page, grant), Text(record, "action"));
        Assert.Equal("Cancelled", Text(record, "result"));
        Assert.Equal(Cancelled, Text(record, "error"));
        Assert.Equal(0, AdminNotifications()); // nothing was sent: no notification, as for any write never sent
    }

    // The control, kd-14's and kd-37's rule: a vault request that gives up by itself, with nobody cancelling, is a credential
    // that could not be read - the refusal it always was, filed Failed. Not the operator's cancel.
    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AVaultRequestThatTimesOutUnasked_IsStillTheCredentialRefusal(string page)
    {
        Credentials(page).TimesOut = true;
        var progress = new RecordingProgress();
        var component = PageFor(page, progress);

        await ExecuteOnPrem(component).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Empty(_h.CallsTo("Invoke-Command"));
        var result = Assert.IsType<PermissionResult>(GetField(component, "result"));
        Assert.Equal("Cannot connect to on-prem Exchange: credentials unavailable.", result.Message);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(result.Message, Text(record, "error"));
    }

    // kd-57's rule, the other control: a cancellation the operator did not ask for - the activity's token is live - is the
    // connection's failure in its own words (aud-a76), filed Failed, never their cancel.
    [Theory]
    [MemberData(nameof(Pages))]
    public async Task ACancellationTheOperatorDidNotAskFor_IsStillAChangeThatFailed(string page)
    {
        var progress = new RecordingProgress();
        var component = PageFor(page, progress, credentialFailure: new OperationCanceledException("the credential read was cancelled"));

        await ExecuteOnPrem(component).WaitAsync(Bound, TestContext.Current.CancellationToken);

        const string Failure = "Nothing was written: the connection to Exchange failed: the credential read was cancelled.";
        Assert.Empty(_h.CallsTo("Invoke-Command"));
        var result = Assert.IsType<PermissionResult>(GetField(component, "result"));
        Assert.Equal(Failure, result.Message);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(Failure, Text(record, "error"));
    }

    // And a write that fails before it is sent for a reason of its own - its session could not be opened - while the operator
    // happens to cancel is that failure, in its own words: only a cancellation that stopped it is their cancel.
    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AWriteThatFailedForItsOwnReason_AsTheOperatorCancelled_IsThatFailure(string page)
    {
        Credentials(page).WaitsAtSecretRequest = int.MaxValue;
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var component = PageFor(page, progress, connect: () =>
        {
            cancel.Cancel();
            throw new InvalidOperationException("the on-premises session could not be opened");
        });

        await ExecuteOnPrem(component).WaitAsync(Bound, TestContext.Current.CancellationToken);

        const string Failure = "Nothing was written: the connection to Exchange failed: the on-premises session could not be opened.";
        Assert.True(cancel.IsCancellationRequested);
        Assert.Empty(_h.CallsTo("Invoke-Command"));
        var result = Assert.IsType<PermissionResult>(GetField(component, "result"));
        Assert.Equal(Failure, result.Message);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(Failure, Text(record, "error"));
    }

    // ---- aud-a70 (6): the operator's cancel and the self-grant check's credential -----------------------------------------

    /// <summary>aud-a70 (6): the pages this is pinned on.</summary>
    public static TheoryData<string> CredentialPages => [MailboxPage, CalendarPage];

    /// <summary>
    /// <paramref name="component"/>'s validator over an Exchange that names <see cref="Other"/> as a person - so the grant needs
    /// the directory-read credential (aud-a70 (6)) - whose credential fetch says it was asked (<paramref name="asked"/>), then
    /// answers as <paramref name="answer"/> does when it completes.
    /// </summary>
    private void CheckCredentialAnswersWith(ComponentBase component, TaskCompletionSource asked,
        Task<(string username, string password, string domain)?> answer)
    {
        var resolver = ExchangeKnowsTheTargetAndTheOperator();
        resolver.ResolveRecipientAsync(Other).Returns(
            new ResolvedRecipient(Other, "ext-other", "UserMailbox", ExistsOnPrem: true, ExchangeGuid: Guid.NewGuid()));
        var validator = _h.NothingProtectedValidator(resolver);
        validator.DirectoryReadSecretFetch = _ =>
        {
            asked.TrySetResult();
            return answer;
        };
        Set(component, "Validator", validator);
    }

    // The self-grant check's credential fetch is handed no token, so the operator's cancel is not seen there: the credential
    // arrives, the check passes, and the cancel stops the change at the write's own credential fetch, which runs next - filed
    // Cancelled, nothing sent, as kd-59 files it. Every grant now needs the check's credential (aud-a70 (6)); its refusal does
    // not take the cancel's place.
    [Theory]
    [MemberData(nameof(CredentialPages))]
    public async Task ACancelWhileTheSelfGrantCheckHasItsCredentialFetched_IsFiledCancelled_NotTheCredentialRefusal(string page)
    {
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var component = PageFor(page, progress);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<(string username, string password, string domain)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CheckCredentialAnswersWith(component, asked, answer.Task);

        var run = ExecuteOnPrem(component);
        await asked.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        answer.SetResult(("svc-dirread", "stand-in-secret", "CONTOSO"));
        await run.WaitAsync(Bound, TestContext.Current.CancellationToken);

        const string Cancelled = "Cancelled by the operator before the change was sent. Nothing was written.";
        Assert.Empty(_h.CallsTo("Invoke-Command"));
        Assert.Empty(_h.Writes);
        Assert.Equal(0, Credentials(page).SecretRequests); // the write's vault request never went out: its token was cancelled
        var result = Assert.IsType<PermissionResult>(GetField(component, "result"));
        Assert.Equal(Cancelled, result.Message);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(OnPremAction(page, grant: true), Text(record, "action"));
        Assert.Equal("Cancelled", Text(record, "result"));
        Assert.Equal(Cancelled, Text(record, "error"));
    }

    // kd-59's rule from the other side: the check's fetch fails for a reason of its own while the operator happens to cancel,
    // and that failure is the answer - the grant refused, naming the credential's problem, filed Failed. Only a cancellation
    // that stopped the change is the operator's cancel, and none reached that fetch.
    [Theory]
    [MemberData(nameof(CredentialPages))]
    public async Task TheSelfGrantChecksCredentialFailing_AsTheOperatorCancels_IsTheCredentialRefusal(string page)
    {
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var component = PageFor(page, progress);
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<(string username, string password, string domain)?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CheckCredentialAnswersWith(component, asked, answer.Task);

        var run = ExecuteOnPrem(component);
        await asked.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
        await cancel.CancelAsync();
        answer.SetException(new InvalidOperationException("the secret store is unreachable"));
        await run.WaitAsync(Bound, TestContext.Current.CancellationToken);

        const string Refusal = "Access denied: The self-grant check could not be completed: the on-premises directory could not resolve 'other@contoso.test': the protected-principal directory-read credential could not be retrieved; nothing was written. Contact your administrator.";
        Assert.Empty(_h.CallsTo("Invoke-Command"));
        Assert.Equal(0, Credentials(page).SecretRequests);
        var result = Assert.IsType<PermissionResult>(GetField(component, "result"));
        Assert.Equal(Refusal + " The same check had passed before you confirmed; made again right before the write, it refused.", result.Message);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(page == MailboxPage ? "AddFullAccess_SelfGrantAttemptAtWrite" : "SetCalendar_SelfGrantAttemptAtWrite", Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(Refusal, Text(record, "error"));
    }

    private static void Set(object page, string property, object? value) =>
        page.GetType().GetProperty(property, Members)!.SetValue(page, value);

    private static void SetField(object page, string field, object? value) =>
        page.GetType().GetField(field, Members)!.SetValue(page, value);

    private static object? GetField(object page, string field) =>
        page.GetType().GetField(field, Members)!.GetValue(page);
}
