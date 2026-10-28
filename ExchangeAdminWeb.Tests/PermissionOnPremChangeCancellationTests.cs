using System.Collections;
using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-58, slices A6 and A7 of docs/CancellationDepth-Plan.md: a permission page's on-premises change takes the
/// operator's cancel at every step before its first command is sent (E7) - the wait for one of the on-premises server's two
/// slots, the session's runspace (a look before it opens and once it has), the connect (its token handed to ConnectOnPrem,
/// which looks before each attempt and waits on it between attempts, A3) and a last look immediately before the mark
/// (aud-a76). A cancel at any of them ends the change unsent: a <see cref="PermissionWriteNotSubmittedException"/> whose cause
/// is the cancellation, which the page files Cancelled in kd-57's words, no one told (kd-59's filing). Once the first command
/// is sent nothing looks at the token, and the change runs to its end. Before, only the credential fetch took it (kd-59): a
/// cancel while the change waited for the server - up to two minutes - or during the connect was not seen, and the change
/// was sent.
/// </summary>
/// <remarks>
/// The real services over <see cref="ExchangeWriteHarness"/>'s on-premises stand-ins, their seams watched: the connect defines
/// the remote session locally and records the token it was handed, the runspace is counted as it is made, the credential is
/// answered at once. The throttle is the base's own, process-wide, so the classes that hold its two slots share a collection
/// and never run at once; a test holds them only until the change it waits on has ended. The page's <c>ExecuteOnPrem</c>
/// driven by reflection, as kd-59's tests drive it. Every wait bounded. No Exchange, no vault, no directory.
/// </remarks>
[Collection(OnPremThrottleCollection.Name)]
public sealed class PermissionOnPremChangeCancellationTests : IDisposable
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const string MailboxPage = "Mailbox Permissions";
    private const string Logon = @"CONTOSO\jdoe";
    private const string Upn = "jdoe@contoso.test";
    private const string Target = "shared@contoso.test";
    private const string Other = "other@contoso.test";
    private const string Cancelled = "Cancelled by the operator before the change was sent. Nothing was written.";

    private static readonly Guid TargetMailbox = Guid.Parse("0f0f0f0f-3333-4444-8555-777777777777");
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    /// <summary>"At once": far inside the throttle's two-minute wait.</summary>
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(10);

    private readonly ExchangeWriteHarness _h = new();
    private readonly ClaimsPrincipal _jdoe;
    private EmailService? _email;

    public PermissionOnPremChangeCancellationTests()
    {
        _h.AddMailbox(Target, TargetMailbox);
        var account = _h.Forest.AddUser("jdoe", upn: Upn, mail: Upn, proxies: [$"SMTP:{Upn}"]);
        _jdoe = SelfGrantDirectoryStandIn.Principal(Logon, account.Sid);
        _h.Forest.AddUser("other", upn: Other, mail: Other, proxies: [$"SMTP:{Other}"]);
    }

    public void Dispose() => _h.Dispose();

    /// <summary>The on-premises changes that take the cancel: Mailbox Permissions' grant and removal (A6).</summary>
    public static TheoryData<string, bool> Changes => new()
    {
        { MailboxPage, true }, { MailboxPage, false },
    };

    public static TheoryData<string> Pages => [MailboxPage];

    // ---- the server's queue --------------------------------------------------------------------------------------------

    // Both of the on-premises server's slots held, a change cancelled while it waits for one ends at once - no runspace made,
    // no session opened, nothing sent - and leaves as a cancellation never sent. Before, the wait took no token: the change
    // waited until a slot freed, up to two minutes, and was then sent.
    [Theory]
    [MemberData(nameof(Changes))]
    public async Task AChangeCancelledWhileItWaitsForTheOnPremServer_EndsAtOnce_NothingSent(string page, bool grant)
    {
        var seams = new Seams(_h);
        using var cancel = new CancellationTokenSource();
        await HoldBothOnPremSlots();
        var change = Task.FromResult(PermissionResult.Fail("not started"));
        Exception? outcome;
        try
        {
            seams.CredentialsFetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            change = Change(page, grant, seams, cancel.Token);
            await seams.CredentialsFetched.Task.WaitAsync(Bound, TestContext.Current.CancellationToken); // next: the server's queue
            await cancel.CancelAsync();

            outcome = await Record.ExceptionAsync(() => change.WaitAsync(Promptly, TestContext.Current.CancellationToken)); // both slots still held
        }
        finally
        {
            ReleaseBothOnPremSlots();
            await Record.ExceptionAsync(() => change.WaitAsync(Bound, TestContext.Current.CancellationToken));
        }

        Assert.Empty(_h.CallsTo("Invoke-Command")); // first: a change sent after the cancel fails here
        Assert.Equal(0, seams.RunspacesMade);
        AssertACancelNeverSent(outcome, cancel.Token);
    }

    // ---- the session's runspace ----------------------------------------------------------------------------------------

    // Cancelled as the change's runspace is made: the open runs - a local runspace - and the look once it has opened ends the
    // change before the connect. No session, nothing sent.
    [Theory]
    [MemberData(nameof(Changes))]
    public async Task AChangeCancelledAsItsRunspaceOpens_NeverConnects_NothingSent(string page, bool grant)
    {
        var seams = new Seams(_h);
        using var cancel = new CancellationTokenSource();
        seams.AsTheRunspaceIsMade = cancel.Cancel;

        var outcome = await Record.ExceptionAsync(() => Change(page, grant, seams, cancel.Token).WaitAsync(Bound, TestContext.Current.CancellationToken));

        Assert.Empty(_h.CallsTo("Invoke-Command"));
        Assert.Equal(1, seams.RunspacesMade);
        Assert.Equal(0, seams.Connects);
        AssertACancelNeverSent(outcome, cancel.Token);
    }

    // ---- the connect ---------------------------------------------------------------------------------------------------

    // The change hands its own token to ConnectOnPrem, which looks at it before each attempt and waits on it between attempts
    // (A3's tests, on the real ConnectOnPrem). The control as well: with no cancel the change is sent - every command - and
    // its session removed, as before.
    [Theory]
    [MemberData(nameof(Changes))]
    public async Task EveryChangeHandsItsTokenToTheConnect_AndWithNoCancelIsSentAsBefore(string page, bool grant)
    {
        var seams = new Seams(_h);
        using var cancel = new CancellationTokenSource();

        var result = await Change(page, grant, seams, cancel.Token).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.Equal(cancel.Token, Assert.Single(seams.ConnectTokens));
        Assert.Equal(CommandsSent(page), _h.CallsTo("Invoke-Command").Count);
        Assert.Single(seams.SessionsRemoved);
    }

    // ---- immediately before the mark -----------------------------------------------------------------------------------

    // Cancelled as the session opens - the connect has run to its end - the last look, immediately before the mark, ends the
    // change: nothing sent, and the session it opened is still removed.
    [Theory]
    [MemberData(nameof(Changes))]
    public async Task AChangeCancelledAsItsSessionOpens_SendsNothing_AndRemovesTheSession(string page, bool grant)
    {
        var seams = new Seams(_h);
        using var cancel = new CancellationTokenSource();
        seams.AsTheSessionOpens = cancel.Cancel;

        var outcome = await Record.ExceptionAsync(() => Change(page, grant, seams, cancel.Token).WaitAsync(Bound, TestContext.Current.CancellationToken));

        Assert.Empty(_h.CallsTo("Invoke-Command")); // first: a change sent after the cancel fails here
        Assert.Equal(1, seams.Connects);
        Assert.Single(seams.SessionsRemoved);
        AssertACancelNeverSent(outcome, cancel.Token);
    }

    // kd-59's rule, kept: nothing after the mark looks at the token. Cancelled as the first command is sent, the change runs to
    // its end - every command sent - and its result stands.
    [Theory]
    [MemberData(nameof(Changes))]
    public async Task AChangeCancelledAsItsFirstCommandIsSent_RunsToItsEnd(string page, bool grant)
    {
        var seams = new Seams(_h);
        using var cancel = new CancellationTokenSource();
        seams.AsACommandIsSent = cancel.Cancel;

        var result = await Change(page, grant, seams, cancel.Token).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal(CommandsSent(page), _h.CallsTo("Invoke-Command").Count); // first: a change stopped after its first command fails here
        Assert.True(result.Success, result.Message);
    }

    // ---- the page ------------------------------------------------------------------------------------------------------

    // The page: the on-premises change's activity cancelled while the change waits for the server - both slots held - is
    // filed Cancelled at once, in kd-57's words, nothing sent and no one told. Before, it waited for a slot and was sent.
    [Theory]
    [MemberData(nameof(Pages))]
    public async Task ACancelOfTheOnPremChange_WhileItWaitsForTheServer_IsFiledCancelled_AtOnce_NothingSent_NoOneTold(string page)
    {
        var seams = new Seams(_h);
        using var cancel = new CancellationTokenSource();
        var progress = new RecordingProgress(cancel.Token);
        var component = PageFor(page, progress, Service(page, seams));
        await HoldBothOnPremSlots();
        Task? run = null;
        try
        {
            seams.CredentialsFetched = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            run = ExecuteOnPrem(component);
            await seams.CredentialsFetched.Task.WaitAsync(Bound, TestContext.Current.CancellationToken);
            await cancel.CancelAsync();

            await run.WaitAsync(Promptly, TestContext.Current.CancellationToken); // both slots still held
        }
        finally
        {
            ReleaseBothOnPremSlots();
            if (run is not null)
                await Record.ExceptionAsync(() => run.WaitAsync(Bound, TestContext.Current.CancellationToken));
        }

        Assert.Empty(_h.CallsTo("Invoke-Command"));
        Assert.Empty(_h.Writes);
        Assert.Equal(0, seams.RunspacesMade);
        var shown = Assert.IsType<PermissionResult>(GetField(component, "result"));
        Assert.False(shown.Success);
        Assert.Equal(Cancelled, shown.Message);
        Assert.Equal((false, Cancelled), Assert.Single(progress.Handles).Outcome);
        Assert.False((bool)GetField(component, "isLoading")!);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(OnPremAction(page), Text(record, "action"));
        Assert.Equal("Cancelled", Text(record, "result"));
        Assert.Equal(Cancelled, Text(record, "error"));
        Assert.Empty(Notifications());
    }

    // ---- the changes ---------------------------------------------------------------------------------------------------

    private static void AssertACancelNeverSent(Exception? outcome, CancellationToken token)
    {
        var notSent = Assert.IsType<PermissionWriteNotSubmittedException>(outcome);
        Assert.IsAssignableFrom<OperationCanceledException>(notSent.InnerException);
        Assert.True(PermissionChangeFailure.CancelledBeforeItWasSent(notSent, token)); // what the page files Cancelled
    }

    /// <summary>The commands a change sends through its session: Mailbox Permissions' one per right, Full Access and Send As.</summary>
    private static int CommandsSent(string page) => page == MailboxPage
        ? 2
        : throw new ArgumentOutOfRangeException(nameof(page), page, "not a page these tests drive");

    /// <summary><paramref name="page"/>'s on-premises change of <see cref="Other"/>'s access to <see cref="Target"/>, under <paramref name="cancellationToken"/>.</summary>
    private Task<PermissionResult> Change(string page, bool grant, Seams seams, CancellationToken cancellationToken)
    {
        var validated = ExchangeWriteHarness.Validated(TargetMailbox);
        var service = Service(page, seams);
        return (page, grant) switch
        {
            (MailboxPage, true) => ((MailboxPermissionService)service).AddMailboxPermissionsOnPremAsync(
                Target, validated, Other, fullAccess: true, sendAs: true, cancellationToken),
            (MailboxPage, false) => ((MailboxPermissionService)service).RemoveMailboxPermissionsOnPremAsync(
                Target, validated, Other, fullAccess: true, sendAs: true, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(page), page, "not a page these tests drive"),
        };
    }

    private ExchangeServiceBase Service(string page, Seams seams) => page == MailboxPage
        ? new OnPremMailbox(_h, seams)
        : throw new ArgumentOutOfRangeException(nameof(page), page, "not a page these tests drive");

    /// <summary>
    /// A service's on-premises seams, watched: the runspace the change opens (counted, <see cref="AsTheRunspaceIsMade"/> run as
    /// it is made); the connect (the token it was handed recorded, the harness's session defined, the session's removal
    /// recorded, every command sent through the session shown to <see cref="AsACommandIsSent"/>, then
    /// <see cref="AsTheSessionOpens"/> run); and the credential (answered at once, <see cref="CredentialsFetched"/> signalled).
    /// </summary>
    private sealed class Seams(ExchangeWriteHarness h)
    {
        private int _runspacesMade;
        private int _connects;

        public ConcurrentQueue<CancellationToken> ConnectTokens { get; } = new();

        public ArrayList SessionsRemoved { get; } = ArrayList.Synchronized(new ArrayList());

        public Action? AsTheRunspaceIsMade { get; set; }

        public Action? AsTheSessionOpens { get; set; }

        public Action? AsACommandIsSent { get; set; }

        public TaskCompletionSource? CredentialsFetched { get; set; }

        public int RunspacesMade => Volatile.Read(ref _runspacesMade);

        public int Connects => Volatile.Read(ref _connects);

        public void RunspaceMade()
        {
            Interlocked.Increment(ref _runspacesMade);
            AsTheRunspaceIsMade?.Invoke();
        }

        public void Connect(PowerShell ps, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _connects);
            ConnectTokens.Enqueue(cancellationToken);
            h.DefineOnPremSession(ps);
            ps.Runspace.SessionStateProxy.SetVariable("Calls", new ObservedCalls(h.Calls, call =>
            {
                if ((string)call["__Command"]! == "Invoke-Command")
                    AsACommandIsSent?.Invoke();
            }));
            ps.Runspace.SessionStateProxy.SetVariable("SessionsRemoved", SessionsRemoved);
            using (var define = PowerShell.Create())
            {
                define.Runspace = ps.Runspace;
                define.AddScript("""function global:Remove-PSSession { param($Session) [void]$global:SessionsRemoved.Add("$Session") }""").Invoke();
                Assert.False(define.HadErrors, "The stand-in Remove-PSSession could not be defined.");
            }
            AsTheSessionOpens?.Invoke();
        }

        public Task<(string username, string password, string domain)?> Credentials()
        {
            CredentialsFetched?.TrySetResult();
            return Task.FromResult<(string username, string password, string domain)?>(("svc-onprem", "not-a-secret", "CONTOSO"));
        }
    }

    /// <summary>The on-premises Mailbox Permissions change over <see cref="Seams"/>.</summary>
    private sealed class OnPremMailbox(ExchangeWriteHarness h, Seams seams)
        : MailboxPermissionService(h.Pool, null!, NullLogger<MailboxPermissionService>.Instance, h.Config, null!, h.Trace)
    {
        public static SemaphoreSlim Throttle => _onPremThrottle;

        internal override Runspace CreateOnPremRunspace()
        {
            var runspace = base.CreateOnPremRunspace();
            seams.RunspaceMade();
            return runspace;
        }

        protected override void ConnectOnPrem(PowerShell ps, string username, string password, string domain, int maxRetries = 3, CancellationToken cancellationToken = default) =>
            seams.Connect(ps, cancellationToken);

        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose, CancellationToken cancellationToken) =>
            seams.Credentials();
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

    private static async Task HoldBothOnPremSlots()
    {
        Assert.True(await OnPremMailbox.Throttle.WaitAsync(Bound, TestContext.Current.CancellationToken), "The first on-premises slot never came free.");
        if (!await OnPremMailbox.Throttle.WaitAsync(Bound, TestContext.Current.CancellationToken))
        {
            OnPremMailbox.Throttle.Release();
            Assert.Fail("The second on-premises slot never came free.");
        }
    }

    private static void ReleaseBothOnPremSlots() => OnPremMailbox.Throttle.Release(2);

    // ---- the page harness ----------------------------------------------------------------------------------------------

    /// <summary>The row a page files for its on-premises change: Mailbox Permissions' a Full Access grant.</summary>
    private static string OnPremAction(string page) => page == MailboxPage
        ? "OnPrem_AddFullAccess"
        : throw new ArgumentOutOfRangeException(nameof(page), page, "not a page these tests drive");

    /// <summary>
    /// <paramref name="page"/> ready for <c>ExecuteOnPrem</c>: the operator jdoe granting <see cref="Other"/> Full Access on
    /// <see cref="Target"/> through <paramref name="service"/>, nothing protected, the access re-check allowed, the activity
    /// <paramref name="progress"/>'s.
    /// </summary>
    private ComponentBase PageFor(string page, RecordingProgress progress, ExchangeServiceBase service)
    {
        if (page != MailboxPage)
            throw new ArgumentOutOfRangeException(nameof(page), page, "not a page these tests drive");
        ComponentBase component = new MailboxPermissions();
        Set(component, "MailboxService", service);
        Set(component, "Logger", NullLogger<MailboxPermissions>.Instance);
        SetField(component, "onPremFullAccess", true);
        SetField(component, "onPremSendAs", false);

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
        SetField(component, "tabIndex", 0);
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

    /// <summary>Every mail the page asked to send: to the administrators, to the user, to the mailbox owner.</summary>
    private IReadOnlyList<string> Notifications() =>
        _email!.ReceivedCalls().Select(c => c.GetMethodInfo().Name)
            .Where(name => name is nameof(EmailService.SendAdminNotificationAsync) or nameof(EmailService.SendUserNotificationAsync)
                or nameof(EmailService.SendOwnerNotificationAsync))
            .ToList();

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    private static void Set(object page, string property, object? value) =>
        page.GetType().GetProperty(property, Members)!.SetValue(page, value);

    private static void SetField(object page, string field, object? value) =>
        page.GetType().GetField(field, Members)!.SetValue(page, value);

    private static object? GetField(object page, string field) =>
        page.GetType().GetField(field, Members)!.GetValue(page);
}

/// <summary>
/// kd-58: the test classes that hold the on-premises server's two slots - the Exchange base's process-wide throttle - run one
/// at a time. Two of them at once could each take one slot and wait for the other until their bound ran out.
/// </summary>
[CollectionDefinition(Name)]
public sealed class OnPremThrottleCollection
{
    public const string Name = "the on-premises server's two slots";
}
