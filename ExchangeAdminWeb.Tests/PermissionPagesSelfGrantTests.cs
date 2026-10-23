using System.Management.Automation;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a43: the Mailbox Permissions and Calendar Permissions single-grant handlers, driven through their compiled code - the
/// injected services and the form's fields set by reflection, <c>SubmitSingle</c> invoked - over
/// <see cref="ExchangeWriteHarness"/>'s validator, a stand-in resolver and <see cref="SelfGrantDirectoryStandIn"/>.
/// </summary>
/// <remarks>
/// The pages are line-keyed by ClickGateRegistry, so their self-grant call changed on its own line only; this is what shows
/// the line hands the check the signed-in principal end to end. aud-a44: when the refusal's record cannot be written the
/// banner is the validator's own refusal and says so, with no exception text. Nothing here reaches Exchange or a directory.
/// </remarks>
public sealed class PermissionPagesSelfGrantTests : IDisposable
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
    private const string Logon = @"CONTOSO\jdoe";
    private const string Upn = "jdoe@contoso.test";
    private const string CloudAlias = "jdoe.cloud@contoso.test";
    private const string Target = "shared@contoso.test";
    private const string SelfGrantRefusal = "Access denied: You cannot grant permissions to yourself.";
    private static readonly Guid TargetMailbox = Guid.Parse("0f0f0f0f-3333-4444-8555-777777777777");

    private readonly ExchangeWriteHarness _h = new();
    private readonly ClaimsPrincipal _jdoe;

    public PermissionPagesSelfGrantTests()
    {
        _h.AddMailbox(Target, TargetMailbox);
        var account = _h.Forest.AddUser("jdoe", upn: Upn, mail: Upn, proxies: [$"SMTP:{Upn}"]);
        _jdoe = SelfGrantDirectoryStandIn.Principal(Logon, account.Sid);
    }

    public void Dispose() => _h.Dispose();

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    /// <summary>
    /// Exchange knows the target mailbox, and knows the operator - by their UPN, never their logon name - and an alias as
    /// one mailbox, named by an address the directory does not carry: only Exchange, asked about the right name, can see it.
    /// </summary>
    private static IIdentityResolver ExchangeNamesTheAliasAsTheOperator()
    {
        var resolver = ExchangeWriteHarness.ExchangeKnows(Target, Target, TargetMailbox);
        var own = new ResolvedRecipient("jdoe.mailbox@contoso.test", "ext-jdoe", "UserMailbox", ExistsOnPrem: true, ExchangeGuid: Guid.NewGuid());
        resolver.ResolveRecipientAsync(Upn).Returns(own);
        resolver.ResolveRecipientAsync(CloudAlias).Returns(own);
        return resolver;
    }

    /// <summary>The services <c>SubmitSingle</c> reaches before its write, set on <paramref name="page"/>.</summary>
    private void Wire(ComponentBase page, IIdentityResolver resolver, AuditService audit, ILogger logger)
    {
        var auth = Substitute.For<AuthenticationStateProvider>();
        auth.GetAuthenticationStateAsync().Returns(new AuthenticationState(_jdoe));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        var http = Substitute.For<IHttpClientFactory>();

        Set(page, "AuthStateProvider", auth);
        Set(page, "AuthorizationService", authorization);
        Set(page, "ServiceNow", new ServiceNowService(_h.Config, NullLogger<ServiceNowService>.Instance, http));
        Set(page, "Validator", _h.NothingProtectedValidator(resolver));
        Set(page, "Audit", audit);
        Set(page, "Email", Substitute.For<EmailService>(_h.Config, Substitute.For<ILogger<EmailService>>()));
        Set(page, "ClientInfo", new ClientInfoService());
        Set(page, "Logger", logger);
        Set(page, "Progress", new ActivityProgressService());
        SetField(page, "currentUser", Logon);
        SetField(page, "tabIndex", 0);
        SetField(page, "targetMailbox", Target);
        SetField(page, "affectedUser", CloudAlias);
        SetField(page, "ticketNumber", "INC100");
    }

    private static void Set(object page, string property, object? value) =>
        page.GetType().GetProperty(property, Members)!.SetValue(page, value);

    private static void SetField(object page, string field, object? value) =>
        page.GetType().GetField(field, Members)!.SetValue(page, value);

    private static T? GetField<T>(object page, string field) =>
        (T?)page.GetType().GetField(field, Members)!.GetValue(page);

    private static Task Call(object page, string method) =>
        (Task)page.GetType().GetMethod(method, Members)!.Invoke(page, null)!;

    private async Task<PermissionResult> SubmitMailboxGrant(IIdentityResolver resolver, AuditService audit, ILogger logger)
    {
        var page = new MailboxPermissions();
        Wire(page, resolver, audit, logger);
        Set(page, "MailboxService", new MailboxPermissionService(_h.Pool, null!, NullLogger<MailboxPermissionService>.Instance, _h.Config, null!, _h.Trace));
        SetField(page, "grantFullAccess", true);
        SetField(page, "grantSendAs", false);

        await Call(page, "SubmitSingle");

        Assert.False(GetField<bool>(page, "isLoading"));
        return Assert.IsType<PermissionResult>(GetField<PermissionResult>(page, "result"));
    }

    private async Task<PermissionResult> SubmitCalendarGrant(IIdentityResolver resolver, AuditService audit, ILogger logger)
    {
        var page = new CalendarPermissions();
        Wire(page, resolver, audit, logger);
        Set(page, "CalendarService", new CalendarPermissionService(_h.Pool, null!, NullLogger<CalendarPermissionService>.Instance, _h.Config, null!, _h.Trace));
        SetField(page, "accessRight", CalendarAccessRight.Reviewer);

        await Call(page, "SubmitSingle");

        Assert.False(GetField<bool>(page, "isLoading"));
        return Assert.IsType<PermissionResult>(GetField<PermissionResult>(page, "result"));
    }

    [Fact]
    public async Task TheMailboxPermissionsPage_PutsTheSignedInPrincipalToTheSelfGrantCheck()
    {
        var result = await SubmitMailboxGrant(ExchangeNamesTheAliasAsTheOperator(), _h.Audit, NullLogger<MailboxPermissions>.Instance);

        Assert.False(result.Success);
        Assert.Equal(SelfGrantRefusal, result.Message);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal("AddFullAccess_SelfGrantAttempt", Text(record, "action"));
        Assert.Equal(SelfGrantRefusal, Text(record, "error"));
        Assert.Empty(_h.Writes);
        Assert.Equal(0, _h.Connects);
        Assert.Empty(_h.Forest.Errors);
    }

    [Fact]
    public async Task TheCalendarPermissionsPage_PutsTheSignedInPrincipalToTheSelfGrantCheck()
    {
        var result = await SubmitCalendarGrant(ExchangeNamesTheAliasAsTheOperator(), _h.Audit, NullLogger<CalendarPermissions>.Instance);

        Assert.False(result.Success);
        Assert.Equal(SelfGrantRefusal, result.Message);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal("SetCalendar_SelfGrantAttempt", Text(record, "action"));
        Assert.Equal(SelfGrantRefusal, Text(record, "error"));
        Assert.Empty(_h.Writes);
        Assert.Equal(0, _h.Connects);
        Assert.Empty(_h.Forest.Errors);
    }

    // ---- aud-a44: the banner when the refusal's record cannot be written ---------------------------------------

    /// <summary>An audit service with no writer: the record throws, as a writer that throws would.</summary>
    private AuditService AuditWithoutAWriter() => new(null!, _h.Trace);

    /// <summary>Exchange knows the target mailbox and cannot answer for the operator: the check cannot be completed.</summary>
    private static IIdentityResolver ExchangeCannotAnswerForTheOperator()
    {
        var resolver = ExchangeWriteHarness.ExchangeKnows(Target, Target, TargetMailbox);
        resolver.ResolveRecipientAsync(Upn).Returns(
            Task.FromException<ResolvedRecipient?>(new InvalidOperationException("Exchange Online is unreachable")));
        return resolver;
    }

    private static (IIdentityResolver resolver, string refusal) Refusal(string shape) => shape == "a self-grant"
        ? (ExchangeNamesTheAliasAsTheOperator(), SelfGrantRefusal)
        : (ExchangeCannotAnswerForTheOperator(), PermissionValidator.SelfGrantCheckIncomplete(Upn));

    /// <summary>
    /// The banner is the validator's own refusal - "self-grant" only when it was one - and says the record could not be
    /// written, with no exception text in it; the exception goes to the page's log, once.
    /// </summary>
    private void AssertTheBanner(PermissionResult result, string refusal, IReadOnlyList<(string Message, Exception? Exception)> warnings)
    {
        Assert.False(result.Success);
        Assert.Equal(refusal + " The audit row could not be written.", result.Message);
        Assert.DoesNotContain("Self-grant blocked", result.Message, StringComparison.Ordinal);
        var (message, exception) = Assert.Single(warnings);
        Assert.IsType<NullReferenceException>(exception);
        Assert.DoesNotContain(exception!.Message, result.Message, StringComparison.Ordinal);
        Assert.Contains(Target, message, StringComparison.Ordinal);
        Assert.Empty(_h.AuditRows());
        Assert.Empty(_h.Writes);
    }

    [Theory]
    [InlineData("a self-grant")]
    [InlineData("a check that could not be completed")]
    public async Task TheMailboxPermissionsBanner_WhenTheRefusalsRecordCannotBeWritten_IsTheValidatorsRefusal_WithoutTheException(string shape)
    {
        var (resolver, refusal) = Refusal(shape);
        var logger = new CapturingLogger<MailboxPermissions>();

        var result = await SubmitMailboxGrant(resolver, AuditWithoutAWriter(), logger);

        AssertTheBanner(result, refusal, logger.Warnings);
    }

    [Theory]
    [InlineData("a self-grant")]
    [InlineData("a check that could not be completed")]
    public async Task TheCalendarPermissionsBanner_WhenTheRefusalsRecordCannotBeWritten_IsTheValidatorsRefusal_WithoutTheException(string shape)
    {
        var (resolver, refusal) = Refusal(shape);
        var logger = new CapturingLogger<CalendarPermissions>();

        var result = await SubmitCalendarGrant(resolver, AuditWithoutAWriter(), logger);

        AssertTheBanner(result, refusal, logger.Warnings);
    }

    // ---- aud-a50, part 1: the on-premises write re-checks the grantee right before it writes -----------------------

    private const string MailboxPage = "Mailbox Permissions";
    private const string CalendarPage = "Calendar Permissions";
    private const string Other = "other@contoso.test";

    public static TheoryData<string> Pages => [MailboxPage, CalendarPage];

    /// <summary>The on-premises Mailbox Permissions write, its credential and its session the harness's.</summary>
    private sealed class OnPremMailboxService(ExchangeWriteHarness h)
        : MailboxPermissionService(h.Pool, null!, NullLogger<MailboxPermissionService>.Instance, h.Config, null!, h.Trace)
    {
        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose) =>
            Task.FromResult<(string username, string password, string domain)?>(("svc", "secret", "CONTOSO"));

        protected override void ConnectOnPrem(PowerShell ps, string username, string password, string domain, int maxRetries = 3) =>
            h.DefineOnPremSession(ps);
    }

    /// <summary>The on-premises Calendar Permissions write, its credential and its session the harness's.</summary>
    private sealed class OnPremCalendarService(ExchangeWriteHarness h)
        : CalendarPermissionService(h.Pool, null!, NullLogger<CalendarPermissionService>.Instance, h.Config, null!, h.Trace)
    {
        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose) =>
            Task.FromResult<(string username, string password, string domain)?>(("svc", "secret", "CONTOSO"));

        protected override void ConnectOnPrem(PowerShell ps, string username, string password, string domain, int maxRetries = 3) =>
            h.DefineOnPremSession(ps);
    }

    /// <summary>The grant <c>SubmitSingle</c> stages before the operator's confirmation: the target, the grantee and the ticket.</summary>
    private static void Stage(object page, string grantee)
    {
        SetField(page, "onPremTarget", Target);
        SetField(page, "onPremUser", grantee);
        SetField(page, "onPremTicket", "INC100");
    }

    /// <summary>
    /// What the operator's Continue reaches: <paramref name="page"/>'s <c>ExecuteOnPrem</c>, over a staged Full Access
    /// (Mailbox Permissions) or Reviewer (Calendar Permissions) grant to <paramref name="grantee"/>; the result and the
    /// warnings the page logged.
    /// </summary>
    private async Task<(PermissionResult Result, IReadOnlyList<(string Message, Exception? Exception)> Warnings)> ConfirmOnPrem(
        string page, string grantee, IIdentityResolver resolver, AuditService audit)
    {
        ComponentBase component;
        Func<IReadOnlyList<(string Message, Exception? Exception)>> warnings;
        if (page == MailboxPage)
        {
            var logger = new CapturingLogger<MailboxPermissions>();
            component = new MailboxPermissions();
            Wire(component, resolver, audit, logger);
            Set(component, "MailboxService", new OnPremMailboxService(_h));
            SetField(component, "onPremFullAccess", true);
            SetField(component, "onPremSendAs", false);
            warnings = () => logger.Warnings;
        }
        else
        {
            var logger = new CapturingLogger<CalendarPermissions>();
            component = new CalendarPermissions();
            Wire(component, resolver, audit, logger);
            Set(component, "CalendarService", new OnPremCalendarService(_h));
            SetField(component, "onPremAccessRight", CalendarAccessRight.Reviewer);
            warnings = () => logger.Warnings;
        }
        Stage(component, grantee);

        await Call(component, "ExecuteOnPrem");

        Assert.False(GetField<bool>(component, "isLoading"));
        Assert.Null(GetField<string>(component, "onPremTarget"));
        return (Assert.IsType<PermissionResult>(GetField<PermissionResult>(component, "result")), warnings());
    }

    /// <summary>
    /// aud-a61. The write-time re-check's refusal, as its banner shows it: the validator's refusal, and that the same check had
    /// passed before the confirmation - the operator could not tell the two apart.
    /// </summary>
    private static readonly string RefusedAtWrite =
        SelfGrantRefusal + " The same check had passed before you confirmed; made again right before the write, it refused.";

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task TheOnPremWrite_ReChecksTheGrantee_RightBeforeItWrites_AndRefusesASelfGrant(string page)
    {
        var (result, warnings) = await ConfirmOnPrem(page, CloudAlias, ExchangeNamesTheAliasAsTheOperator(), _h.Audit);

        // aud-a61: the banner says the earlier check had passed, and the record is the write-time action, not SubmitSingle's;
        // the record's error is the validator's own refusal still.
        Assert.False(result.Success);
        Assert.Equal(RefusedAtWrite, result.Message);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(page == MailboxPage ? "AddFullAccess_SelfGrantAttemptAtWrite" : "SetCalendar_SelfGrantAttemptAtWrite", Text(record, "action"));
        Assert.Equal(SelfGrantRefusal, Text(record, "error"));
        Assert.Equal(CloudAlias, Text(record, "affectedUser"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Empty(_h.CallsTo("Invoke-Command"));
        Assert.Empty(_h.Writes);
        Assert.Empty(warnings);
        Assert.Empty(_h.Forest.Errors);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task TheOnPremWrite_ToSomeoneElse_StillWrites(string page)
    {
        var (result, _) = await ConfirmOnPrem(page, Other, ExchangeNamesTheAliasAsTheOperator(), _h.Audit);

        Assert.True(result.Success, result.Message);
        Assert.NotEmpty(_h.CallsTo("Invoke-Command"));
        Assert.Equal(page == MailboxPage ? "Add-MailboxPermission" : "Set-MailboxFolderPermission", Assert.Single(_h.Writes));
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(page == MailboxPage ? "OnPrem_AddFullAccess" : "OnPrem_SetCalendar", Text(record, "action"));
        Assert.Equal("Success", Text(record, "result"));
        Assert.Empty(_h.Forest.Errors);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task TheOnPremRefusal_WhoseRecordCannotBeWritten_IsTheValidatorsRefusal_AndSaysSo(string page)
    {
        var (result, warnings) = await ConfirmOnPrem(page, CloudAlias, ExchangeNamesTheAliasAsTheOperator(), AuditWithoutAWriter());

        AssertTheBanner(result, RefusedAtWrite, warnings);
        Assert.Empty(_h.CallsTo("Invoke-Command"));
    }

    [Fact]
    public void TheRefusalsBanner_IsTheRefusal_WhenItsRecordIsWritten()
    {
        var logger = new CapturingLogger<MailboxPermissions>();
        var written = 0;

        var banner = SelfGrantRefusalRecord.Banner(SelfGrantRefusal, () =>
        {
            written++;
            return JsonlWriteOutcome.Written;
        }, logger, Target);

        Assert.Equal(SelfGrantRefusal, banner);
        Assert.Equal(1, written);
        Assert.Empty(logger.Warnings);
    }

    [Theory]
    [InlineData(JsonlWriteOutcome.NotSerializable)]
    [InlineData(JsonlWriteOutcome.FileWriteFailed)]
    public void TheRefusalsBanner_WhenTheWriterDropsItsRecord_SaysTheRowCouldNotBeWritten(JsonlWriteOutcome dropped)
    {
        // aud-a50, part 4: the writer does not throw (aud-a28q), it says what became of the row - and it logs its own error.
        var logger = new CapturingLogger<MailboxPermissions>();

        var banner = SelfGrantRefusalRecord.Banner(SelfGrantRefusal, () => dropped, logger, Target);

        Assert.Equal(SelfGrantRefusal + " The audit row could not be written.", banner);
        Assert.Empty(logger.Warnings);
    }

    // ---- aud-a50, part 4: a row the writer drops is a row not written ----------------------------------------

    /// <summary>The audit file of this minute and the next held with no sharing: the writer drops the row and says so.</summary>
    private IDisposable HoldTheAuditFile()
    {
        var now = DateTime.Now;
        var streams = new[] { now, now.AddMinutes(1) }
            .Select(date => _h.Jsonl.GetAuditLogPaths(date).Last())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            .ToList();
        return new HeldFiles(streams);
    }

    private sealed class HeldFiles(List<FileStream> streams) : IDisposable
    {
        public void Dispose()
        {
            foreach (var stream in streams)
                stream.Dispose();
        }
    }

    /// <summary><paramref name="page"/>'s <c>SubmitSingle</c> over a Full Access or Reviewer grant to the alias; the result and the page's warnings.</summary>
    private async Task<(PermissionResult Result, IReadOnlyList<(string Message, Exception? Exception)> Warnings)> Submit(
        string page, IIdentityResolver resolver, AuditService audit)
    {
        if (page == MailboxPage)
        {
            var logger = new CapturingLogger<MailboxPermissions>();
            return (await SubmitMailboxGrant(resolver, audit, logger), logger.Warnings);
        }

        var calendarLogger = new CapturingLogger<CalendarPermissions>();
        return (await SubmitCalendarGrant(resolver, audit, calendarLogger), calendarLogger.Warnings);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task TheSubmitSingleBanner_WhenTheWriterDropsTheRefusalsRow_SaysTheRowCouldNotBeWritten(string page)
    {
        // Nothing throws: the writer hands back that it dropped the row, and the banner said nothing of it.
        PermissionResult result;
        IReadOnlyList<(string Message, Exception? Exception)> warnings;
        using (HoldTheAuditFile())
            (result, warnings) = await Submit(page, ExchangeNamesTheAliasAsTheOperator(), _h.Audit);

        Assert.False(result.Success);
        Assert.Equal(SelfGrantRefusal + " The audit row could not be written.", result.Message);
        Assert.Empty(warnings);
        Assert.Empty(_h.AuditRows());
        Assert.Empty(_h.Writes);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task TheOnPremBanner_WhenTheWriterDropsTheRefusalsRow_SaysTheRowCouldNotBeWritten(string page)
    {
        PermissionResult result;
        IReadOnlyList<(string Message, Exception? Exception)> warnings;
        using (HoldTheAuditFile())
            (result, warnings) = await ConfirmOnPrem(page, CloudAlias, ExchangeNamesTheAliasAsTheOperator(), _h.Audit);

        Assert.False(result.Success);
        Assert.Equal(RefusedAtWrite + " The audit row could not be written.", result.Message);
        Assert.Empty(warnings);
        Assert.Empty(_h.AuditRows());
        Assert.Empty(_h.CallsTo("Invoke-Command"));
    }

    [Fact]
    public void TheRefusalsBanner_WhenItsRecordThrows_SaysTheRowCouldNotBeWritten_AndLogsTheException()
    {
        var logger = new CapturingLogger<MailboxPermissions>();
        var failure = new InvalidOperationException("the disk is full - payload-5e1f");

        var banner = SelfGrantRefusalRecord.Banner(SelfGrantRefusal, () => throw failure, logger, Target);

        Assert.Equal(SelfGrantRefusal + " The audit row could not be written.", banner);
        var (message, exception) = Assert.Single(logger.Warnings);
        Assert.Same(failure, exception);
        Assert.Contains(Target, message, StringComparison.Ordinal);
        Assert.DoesNotContain("payload-5e1f", banner, StringComparison.Ordinal);
    }

    /// <summary>Keeps every warning a page logs, with its exception.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(string Message, Exception? Exception)> _warnings = [];

        public IReadOnlyList<(string Message, Exception? Exception)> Warnings
        {
            get { lock (_warnings) return _warnings.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                lock (_warnings) _warnings.Add((formatter(state, exception), exception));
        }
    }
}
