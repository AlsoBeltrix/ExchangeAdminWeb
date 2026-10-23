using System.Management.Automation;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
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

        // aud-a69: the someone else the grants below name is someone - the directory holds their account. A grantee neither
        // Exchange nor the directory holds is refused as a check that could not be completed.
        _h.Forest.AddUser("other", upn: Other, mail: Other, proxies: [$"SMTP:{Other}"]);
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

    /// <summary>
    /// The services <c>SubmitSingle</c> reaches before its write, set on <paramref name="page"/>; the mail and the progress
    /// service a test gives, else a substitute mail service and a real progress service (aud-a53).
    /// </summary>
    private void Wire(ComponentBase page, IIdentityResolver resolver, AuditService audit, ILogger logger,
        EmailService? email = null, IActivityProgress? progress = null)
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
        Set(page, "Email", email ?? Substitute.For<EmailService>(_h.Config, Substitute.For<ILogger<EmailService>>()));
        Set(page, "ClientInfo", new ClientInfoService());
        Set(page, "Logger", logger);
        Set(page, "Progress", progress ?? new ActivityProgressService());
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

    /// <summary>
    /// Mailbox Permissions' <c>SubmitSingle</c> over a Full Access grant to the alias - or to <paramref name="grantee"/>, on
    /// <paramref name="target"/> - with the mail and the progress service a test gives (aud-a53, aud-a59), and the pool the
    /// page's own service borrows from, the harness's unless a test gives one (aud-a71).
    /// </summary>
    private async Task<PermissionResult> SubmitMailboxGrant(IIdentityResolver resolver, AuditService audit, ILogger logger,
        string? grantee = null, EmailService? email = null, IActivityProgress? progress = null, string? target = null,
        ExoConnectionPool? pool = null)
    {
        var page = new MailboxPermissions();
        Wire(page, resolver, audit, logger, email, progress);
        Set(page, "MailboxService", new MailboxPermissionService(pool ?? _h.Pool, null!, NullLogger<MailboxPermissionService>.Instance, _h.Config, null!, _h.Trace));
        SetField(page, "grantFullAccess", true);
        SetField(page, "grantSendAs", false);
        if (grantee is not null) SetField(page, "affectedUser", grantee);
        if (target is not null) SetField(page, "targetMailbox", target);

        await Call(page, "SubmitSingle");

        Assert.False(GetField<bool>(page, "isLoading"));
        return Assert.IsType<PermissionResult>(GetField<PermissionResult>(page, "result"));
    }

    /// <summary>Calendar Permissions' <c>SubmitSingle</c> over a Reviewer grant, as <see cref="SubmitMailboxGrant"/>.</summary>
    private async Task<PermissionResult> SubmitCalendarGrant(IIdentityResolver resolver, AuditService audit, ILogger logger,
        string? grantee = null, EmailService? email = null, IActivityProgress? progress = null, string? target = null,
        ExoConnectionPool? pool = null)
    {
        var page = new CalendarPermissions();
        Wire(page, resolver, audit, logger, email, progress);
        Set(page, "CalendarService", new CalendarPermissionService(pool ?? _h.Pool, null!, NullLogger<CalendarPermissionService>.Instance, _h.Config, null!, _h.Trace));
        SetField(page, "accessRight", CalendarAccessRight.Reviewer);
        if (grantee is not null) SetField(page, "affectedUser", grantee);
        if (target is not null) SetField(page, "targetMailbox", target);

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

    /// <summary>
    /// The on-premises Mailbox Permissions write, its credential and its session the harness's; over the pool and the settings
    /// a test gives, else the harness's (aud-a73); its connect throwing the exception a test gives (aud-a75).
    /// </summary>
    private sealed class OnPremMailboxService(ExchangeWriteHarness h, ExoConnectionPool? pool = null, IConfiguration? config = null,
        Exception? connectFailure = null, string? onPremScript = null)
        : MailboxPermissionService(pool ?? h.Pool, null!, NullLogger<MailboxPermissionService>.Instance, config ?? h.Config, null!, h.Trace)
    {
        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose) =>
            Task.FromResult<(string username, string password, string domain)?>(("svc", "secret", "CONTOSO"));

        protected override void ConnectOnPrem(PowerShell ps, string username, string password, string domain, int maxRetries = 3)
        {
            if (connectFailure is not null)
                throw connectFailure;
            h.DefineOnPremSession(ps);
            DefineOnPrem(ps, onPremScript);
        }
    }

    /// <summary>The on-premises Calendar Permissions write, as <see cref="OnPremMailboxService"/>.</summary>
    private sealed class OnPremCalendarService(ExchangeWriteHarness h, ExoConnectionPool? pool = null, IConfiguration? config = null,
        Exception? connectFailure = null, string? onPremScript = null)
        : CalendarPermissionService(pool ?? h.Pool, null!, NullLogger<CalendarPermissionService>.Instance, config ?? h.Config, null!, h.Trace)
    {
        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose) =>
            Task.FromResult<(string username, string password, string domain)?>(("svc", "secret", "CONTOSO"));

        protected override void ConnectOnPrem(PowerShell ps, string username, string password, string domain, int maxRetries = 3)
        {
            if (connectFailure is not null)
                throw connectFailure;
            h.DefineOnPremSession(ps);
            DefineOnPrem(ps, onPremScript);
        }
    }

    /// <summary>aud-a70 (5). A test's own stand-ins, defined in an on-premises session after the harness's, so they take their place.</summary>
    private static void DefineOnPrem(PowerShell ps, string? script)
    {
        if (script is null)
            return;
        using var define = PowerShell.Create();
        define.Runspace = ps.Runspace;
        define.AddScript(script).Invoke();
        if (define.HadErrors)
            throw new InvalidOperationException("the test's on-premises stand-ins could not be defined");
    }

    /// <summary>The grant <c>SubmitSingle</c> stages before the operator's confirmation: the target, the grantee and the ticket.</summary>
    private static void Stage(object page, string grantee, string target = Target)
    {
        SetField(page, "onPremTarget", target);
        SetField(page, "onPremUser", grantee);
        SetField(page, "onPremTicket", "INC100");
    }

    /// <summary>
    /// What the operator's Continue reaches: <paramref name="page"/>'s <c>ExecuteOnPrem</c>, over a staged Full Access
    /// (Mailbox Permissions) or Reviewer (Calendar Permissions) grant to <paramref name="grantee"/>; the result and the
    /// warnings the page logged; the mail service a test gives (aud-a53); the staged target a test gives, and an exception the
    /// on-premises permission check throws (aud-a59); an exception the write's on-premises connect throws (aud-a75).
    /// </summary>
    private async Task<(PermissionResult Result, IReadOnlyList<(string Message, Exception? Exception)> Warnings)> ConfirmOnPrem(
        string page, string grantee, IIdentityResolver resolver, AuditService audit, EmailService? email = null,
        string target = Target, Exception? onPremAuthorization = null, Exception? onPremConnect = null, bool onPremDenied = false,
        IActivityProgress? progress = null)
    {
        ComponentBase component;
        Func<IReadOnlyList<(string Message, Exception? Exception)>> warnings;
        if (page == MailboxPage)
        {
            var logger = new CapturingLogger<MailboxPermissions>();
            component = new MailboxPermissions();
            Wire(component, resolver, audit, logger, email, progress);
            Set(component, "MailboxService", new OnPremMailboxService(_h, connectFailure: onPremConnect));
            SetField(component, "onPremFullAccess", true);
            SetField(component, "onPremSendAs", false);
            warnings = () => logger.Warnings;
        }
        else
        {
            var logger = new CapturingLogger<CalendarPermissions>();
            component = new CalendarPermissions();
            Wire(component, resolver, audit, logger, email, progress);
            Set(component, "CalendarService", new OnPremCalendarService(_h, connectFailure: onPremConnect));
            SetField(component, "onPremAccessRight", CalendarAccessRight.Reviewer);
            warnings = () => logger.Warnings;
        }
        Stage(component, grantee, target);
        if (onPremAuthorization is not null || onPremDenied)
        {
            // aud-a70 (5): or the operator is not permitted to change on-premises permissions.
            var authorization = (IAuthorizationService)component.GetType().GetProperty("AuthorizationService", Members)!.GetValue(component)!;
            authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                    page == MailboxPage ? "MailboxPermissionsOnPrem" : "CalendarPermissionsOnPrem")
                .Returns(onPremAuthorization is not null
                    ? Task.FromException<AuthorizationResult>(onPremAuthorization)
                    : Task.FromResult(AuthorizationResult.Failed()));
        }

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

    /// <summary>
    /// <paramref name="page"/>'s <c>SubmitSingle</c> over a Full Access or Reviewer grant to the alias, or to
    /// <paramref name="grantee"/> on <paramref name="target"/>, the page's service over <paramref name="pool"/> when given;
    /// the result and the page's warnings.
    /// </summary>
    private async Task<(PermissionResult Result, IReadOnlyList<(string Message, Exception? Exception)> Warnings)> Submit(
        string page, IIdentityResolver resolver, AuditService audit, string? grantee = null, EmailService? email = null,
        IActivityProgress? progress = null, string? target = null, ExoConnectionPool? pool = null)
    {
        if (page == MailboxPage)
        {
            var logger = new CapturingLogger<MailboxPermissions>();
            return (await SubmitMailboxGrant(resolver, audit, logger, grantee, email, progress, target, pool), logger.Warnings);
        }

        var calendarLogger = new CapturingLogger<CalendarPermissions>();
        return (await SubmitCalendarGrant(resolver, audit, calendarLogger, grantee, email, progress, target, pool), calendarLogger.Warnings);
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

    // ---- aud-a53: a notification or a record that throws leaves the change's result; a step that throws is named -------

    public static TheoryData<string, string> PagesAndNotifications => new()
    {
        { MailboxPage, "admin" }, { MailboxPage, "user" }, { MailboxPage, "owner" },
        { CalendarPage, "admin" }, { CalendarPage, "user" }, { CalendarPage, "owner" },
    };

    /// <summary>An EmailService whose <paramref name="which"/> notification throws; every other notification succeeds.</summary>
    private EmailService EmailWhere(string? which)
    {
        var email = Substitute.For<EmailService>(_h.Config, Substitute.For<ILogger<EmailService>>());
        var refused = Task.FromException(new InvalidOperationException("the mail relay refused the message - payload-a53"));
        if (which == "admin")
            email.SendAdminNotificationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<string?>()).Returns(refused);
        if (which == "user")
            email.SendUserNotificationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
                .Returns(refused);
        if (which == "owner")
            email.SendOwnerNotificationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
                .Returns(refused);
        return email;
    }

    /// <summary>The three notifications <paramref name="email"/> was asked to send, each counted.</summary>
    private static void AssertEveryNotificationWasSent(EmailService email)
    {
        email.Received(1).SendAdminNotificationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<string?>());
        email.Received(1).SendUserNotificationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        email.Received(1).SendOwnerNotificationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Theory]
    [MemberData(nameof(PagesAndNotifications))]
    public async Task AWrittenChange_WhenANotificationThrows_StandsAsWritten_AndTheOtherNotificationsAreStillSent(string page, string which)
    {
        // The notifications sat unguarded in SubmitSingle's try, whose catch turned any throw into "Operation may have
        // succeeded but audit logging failed" - a written change shown as failed, the rest of the mails unsent.
        var email = EmailWhere(which);

        var (result, warnings) = await Submit(page, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, grantee: Other, email: email);

        Assert.True(result.Success, result.Message);
        Assert.DoesNotContain("audit logging failed", result.Message, StringComparison.Ordinal);
        AssertEveryNotificationWasSent(email);
        var (_, exception) = Assert.Single(warnings);
        Assert.Contains("payload-a53", exception!.Message, StringComparison.Ordinal);
        Assert.Equal("Success", Text(Assert.Single(_h.AuditRows()), "result"));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AWrittenChange_WhenItsRecordThrows_StandsAsWritten(string page)
    {
        // Audit failure must not make an already-completed operation appear to have failed; it is logged on its own.
        var email = EmailWhere(null);

        var (result, warnings) = await Submit(page, ExchangeNamesTheAliasAsTheOperator(), AuditWithoutAWriter(), grantee: Other, email: email);

        Assert.True(result.Success, result.Message);
        AssertEveryNotificationWasSent(email);
        var (_, exception) = Assert.Single(warnings);
        Assert.IsType<NullReferenceException>(exception);
        Assert.NotEmpty(_h.Writes);
        // aud-a70 (1): the banner says the row was not written.
        Assert.EndsWith(" The audit row could not be written.", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AThrowAfterTheWrite_IsNamedAsSuch_InTheCausesOwnWords_NotItsRawText(string page)
    {
        // The one throw left past the write: the activity's completion. The banner says the change was written and which
        // step failed, in the words ExchangeServiceBase.ResolveFailure takes from the exception (aud-a23i), not its raw text.
        var progress = Substitute.For<IActivityProgress>();
        var activity = Substitute.For<IActivityHandle>();
        progress.Begin(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ActivitySize>()).Returns(activity);
        var failure = new InvalidOperationException("raw-text-a53");
        failure.Data[ExchangeServiceBase.PsErrorsDataKey] = new[] { "the progress panel is gone" };
        activity.When(handle => handle.Complete(true, Arg.Any<string?>())).Do(_ => throw failure);

        var (result, warnings) = await Submit(page, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, grantee: Other, progress: progress);

        var banner = "The change was written, but a step after the write failed: the progress panel is gone. Check the permissions to confirm the result.";
        Assert.False(result.Success);
        Assert.Equal(banner, result.Message);
        Assert.DoesNotContain("raw-text-a53", result.Message, StringComparison.Ordinal);
        activity.Received(1).Complete(false, banner);
        Assert.Same(failure, Assert.Single(warnings).Exception);
        Assert.NotEmpty(_h.Writes);

        // aud-a71: the catch records, as ExecuteOnPrem's does - beside the success row, a failed row that says the change was
        // written and which step failed after it.
        var rows = _h.AuditRows();
        Assert.Equal(2, rows.Count);
        var written = Assert.Single(rows, row => Text(row, "result") == "Success");
        var failed = Assert.Single(rows, row => Text(row, "result") == "Failed");
        Assert.Equal(Text(written, "action"), Text(failed, "action"));
        Assert.Equal(banner, Text(failed, "error"));
    }

    [Theory]
    [MemberData(nameof(PagesAndNotifications))]
    public async Task AnOnPremChange_WhenANotificationThrows_StandsAsWritten(string page, string which)
    {
        // ExecuteOnPrem sent the same three notifications inside a try whose catch recorded the written change as failed.
        var email = EmailWhere(which);

        var (result, warnings) = await ConfirmOnPrem(page, Other, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, email);

        Assert.True(result.Success, result.Message);
        AssertEveryNotificationWasSent(email);
        Assert.Single(warnings);
        Assert.All(_h.AuditRows(), row => Assert.Equal("Success", Text(row, "result")));
    }

    [Theory]
    [InlineData("a step before the write", "The change was not made: a step before the write failed: the cmdlet's own words. Nothing was written.")]
    [InlineData("the write", "Whether the change was made could not be confirmed: the write failed: the cmdlet's own words. Check the permissions before trying again.")]
    [InlineData("a step after the write", "The change was written, but a step after the write failed: the cmdlet's own words. Check the permissions to confirm the result.")]
    [InlineData("the connection to Exchange", "Nothing was written: the connection to Exchange failed: the cmdlet's own words.")]
    public void TheStepBanner_SaysWhatCouldHaveBeenWritten_InTheCausesOwnWords(string step, string banner)
    {
        // aud-a76: at the connection the throw is the service's word that nothing was sent, the cause inside it - and the
        // banner is the cause's words, not the wrapper's, which restates only the cause's raw message.
        Exception Thrown(Exception cause) =>
            step == PermissionChangeFailure.TheConnection ? new PermissionWriteNotSubmittedException(cause) : cause;
        var failure = new InvalidOperationException("raw-text-a53");
        failure.Data[ExchangeServiceBase.PsErrorsDataKey] = new[] { "the cmdlet's own words.", "a second record" };

        Assert.Equal(banner, PermissionChangeFailure.Banner(step, Thrown(failure)));
        Assert.Equal(banner.Replace("the cmdlet's own words", "plain text", StringComparison.Ordinal),
            PermissionChangeFailure.Banner(step, Thrown(new InvalidOperationException("plain text."))));
    }

    // ---- aud-a59: the protected-target denial says when its row was not written; ExecuteOnPrem's catch names its step --

    private const string Unknown = "nobody@contoso.test";

    public static TheoryData<string, bool> PagesAndPaths => new()
    {
        { MailboxPage, false }, { MailboxPage, true }, { CalendarPage, false }, { CalendarPage, true },
    };

    /// <summary>
    /// <paramref name="page"/>'s single submit, or its on-premises write when <paramref name="onPrem"/>, of a grant to a target
    /// Exchange holds no mailbox for - which the protected-target check denies - with <paramref name="audit"/>.
    /// </summary>
    private async Task<(PermissionResult Result, IReadOnlyList<(string Message, Exception? Exception)> Warnings)> Deny(
        string page, bool onPrem, AuditService audit) => onPrem
        ? await ConfirmOnPrem(page, Other, ExchangeNamesTheAliasAsTheOperator(), audit, target: Unknown)
        : await Submit(page, ExchangeNamesTheAliasAsTheOperator(), audit, grantee: Other, target: Unknown);

    [Theory]
    [MemberData(nameof(PagesAndPaths))]
    public async Task AProtectedTargetDenial_WhoseRowTheWriterDrops_SaysTheRowCouldNotBeWritten(string page, bool onPrem)
    {
        // The writer does not throw (aud-a28q); it hands back that it dropped the row, and the denial's record ignored that.
        PermissionResult result;
        IReadOnlyList<(string Message, Exception? Exception)> warnings;
        using (HoldTheAuditFile())
            (result, warnings) = await Deny(page, onPrem, _h.Audit);

        Assert.False(result.Success);
        Assert.Equal(PermissionValidator.NoMailboxMessage(Unknown) + " The audit row could not be written.", result.Message);
        Assert.Empty(warnings);
        Assert.Empty(_h.AuditRows());
        Assert.Empty(_h.Writes);
    }

    [Theory]
    [MemberData(nameof(PagesAndPaths))]
    public async Task AProtectedTargetDenial_WhoseRecordThrows_SaysSo_AndNothingEscapesTheHandler(string page, bool onPrem)
    {
        // ExecuteOnPrem's record sat in no try of its own: the throw went to the handler's catch, whose own record threw again
        // out of the handler.
        var (result, warnings) = await Deny(page, onPrem, AuditWithoutAWriter());

        Assert.False(result.Success);
        Assert.Equal(PermissionValidator.NoMailboxMessage(Unknown) + " The audit row could not be written.", result.Message);
        var (message, exception) = Assert.Single(warnings);
        Assert.IsType<NullReferenceException>(exception);
        Assert.Contains("protected-target denial", message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AnOnPremStepThatThrowsBeforeTheWrite_IsNamed_InTheCausesOwnWords_AndItsRecordCarriesTheSameText(string page)
    {
        // ExecuteOnPrem's catch recorded and showed the raw exception text, whatever had thrown.
        var failure = new InvalidOperationException("raw-text-a59");
        failure.Data[ExchangeServiceBase.PsErrorsDataKey] = new[] { "the policy store cannot be read" };

        var (result, warnings) = await ConfirmOnPrem(page, Other, ExchangeNamesTheAliasAsTheOperator(), _h.Audit,
            onPremAuthorization: failure);

        var banner = "The change was not made: a step before the write failed: the policy store cannot be read. Nothing was written.";
        Assert.False(result.Success);
        Assert.Equal(banner, result.Message);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(page == MailboxPage ? "OnPrem_AddFullAccess" : "OnPrem_SetCalendar", Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(banner, Text(record, "error"));
        Assert.Same(failure, Assert.Single(warnings).Exception);
        Assert.Empty(_h.CallsTo("Invoke-Command"));
    }

    // ---- aud-a71: a single change whose write throws is recorded, as the on-premises write's is ----------------------

    /// <summary>
    /// The harness's call list - every call recorded there as before - that does <paramref name="first"/> once, when the first
    /// call is recorded through it.
    /// </summary>
    private sealed class CallsThen(System.Collections.IList calls, Action first) : System.Collections.ArrayList
    {
        private int _fired;

        public override int Add(object? value)
        {
            var index = calls.Add(value);
            if (Interlocked.Exchange(ref _fired, 1) == 0)
                first();
            return index;
        }
    }

    /// <summary>
    /// A pool for the page's own service, over the harness's stand-ins and settings - its module config the one a test gives,
    /// else the harness's: its first call - the location read - does <paramref name="atTheLocationRead"/> to it, once, and
    /// every connect after its first throws <paramref name="connectFailure"/> when a test gives one. So the location is read
    /// and the write's own borrow meets what the test arranged: a borrow the pool refuses, or a connection that cannot be
    /// had - the one throw a cloud write reaches its handler with, every failure inside the write being reported as a result
    /// (aud-a71). The validator keeps the harness's pool.
    /// </summary>
    private ExoConnectionPool PoolWhoseWriteBorrowFails(Action<ExoConnectionPool> atTheLocationRead, Exception? connectFailure = null,
        ModuleConfigService? moduleConfig = null)
    {
        ExoConnectionPool? pool = null;
        var connects = 0;
        pool = new ExoConnectionPool(_h.Config, moduleConfig ?? _h.ModuleConfig, _h.Enablement, NullLogger<ExoConnectionPool>.Instance, _h.Trace,
            (_, generation, _) =>
            {
                if (Interlocked.Increment(ref connects) > 1 && connectFailure is not null)
                    throw connectFailure;
                var runspace = _h.OpenExchangeRunspace();
                runspace.SessionStateProxy.SetVariable("Calls", new CallsThen(_h.Calls, () => atTheLocationRead(pool!)));
                var ps = PowerShell.Create();
                ps.Runspace = runspace;
                return new PooledRunspace(runspace, ps, generation, "stand-in");
            });
        return pool;
    }

    /// <summary>
    /// <see cref="PoolWhoseWriteBorrowFails"/> whose location read retires the connection it runs on, so the write's own borrow
    /// connects again, and that connect throws <paramref name="failure"/>.
    /// </summary>
    private ExoConnectionPool PoolWhoseWriteBorrowThrows(Exception failure) =>
        PoolWhoseWriteBorrowFails(pool => pool.DrainPool(), failure);

    /// <summary>What the write's borrow throws here: raw text unlike the PowerShell error it carries.</summary>
    private static InvalidOperationException LostSession()
    {
        var failure = new InvalidOperationException("raw-text-a71");
        failure.Data[ExchangeServiceBase.PsErrorsDataKey] = new[] { "the session to Exchange Online was lost" };
        return failure;
    }

    /// <summary>
    /// The banner for <see cref="LostSession"/> at the write once its command was sent: whether the change was made could not
    /// be confirmed.
    /// </summary>
    private const string WriteNotConfirmed =
        "Whether the change was made could not be confirmed: the write failed: the session to Exchange Online was lost. Check the permissions before trying again.";

    /// <summary>
    /// aud-a76. The banner for <see cref="LostSession"/> as the write's own borrow throws it, before its command was sent:
    /// nothing was written.
    /// </summary>
    private const string WriteNotSent = "Nothing was written: the connection to Exchange failed: the session to Exchange Online was lost.";

    /// <summary>A progress service whose activity a test can read back.</summary>
    private static (IActivityProgress Progress, IActivityHandle Activity) WatchedProgress()
    {
        var progress = Substitute.For<IActivityProgress>();
        var activity = Substitute.For<IActivityHandle>();
        progress.Begin(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ActivitySize>()).Returns(activity);
        return (progress, activity);
    }

    // ---- aud-a76: a write that failed before its command was sent is filed Failed, nothing written ---------------------

    /// <summary>What a cloud write's own borrow can meet before its command is sent, on each page.</summary>
    public static TheoryData<string, string> PagesAndRefusedBorrows => new()
    {
        { MailboxPage, "Exchange Online disabled" }, { MailboxPage, "settings unreadable" }, { MailboxPage, "connect busy" },
        { MailboxPage, "pool stopped" }, { MailboxPage, "connection lost" },
        { CalendarPage, "Exchange Online disabled" }, { CalendarPage, "settings unreadable" }, { CalendarPage, "connect busy" },
        { CalendarPage, "pool stopped" }, { CalendarPage, "connection lost" },
    };

    /// <summary>A store whose reads fail once a test says so, as a store that cannot be read; writes pass.</summary>
    private sealed class StoreThatFailsWhenTold(IConfigStore inner) : IConfigStore
    {
        private volatile bool _failing;

        public void Fail() => _failing = true;

        public long GetChangeToken() => inner.GetChangeToken();

        public T Read<T>(Func<SqliteConnection, T> read) =>
            _failing ? throw new InvalidOperationException("config store unreadable") : inner.Read(read);

        public T Write<T>(Func<SqliteConnection, SqliteTransaction, T> write) => inner.Write(write);

        public void Write(Action<SqliteConnection, SqliteTransaction> write) => inner.Write(write);
    }

    /// <summary>
    /// A pool for a cloud write whose own borrow meets <paramref name="refusal"/>, arranged at the location read, and in
    /// <paramref name="cause"/> the words the pool gives it in: Exchange Online switched off; the ExchangeOnline rows, read
    /// through a store of their own, unreadable from then on; the connect refused as busy, in <c>CreateConnected</c>'s own
    /// words for its connect gate; the pool stopped; or the connection lost (<see cref="LostSession"/>, whose captured words
    /// are the cause).
    /// </summary>
    private ExoConnectionPool PoolWhoseWriteBorrowMeets(string refusal, out string cause)
    {
        switch (refusal)
        {
            case "Exchange Online disabled":
                cause = "Exchange Online module is not enabled. Enable it in Admin Settings and configure the connection on the Exchange Online config page.";
                return PoolWhoseWriteBorrowFails(_ =>
                    _h.Enablement.SaveEnablement(new Dictionary<string, bool> { [ExoConnectionPool.ConfigModuleKey] = false }));
            case "settings unreadable":
                var rows = new StoreThatFailsWhenTold(TestConfigStore.Create(_h.Dir));
                var env = Substitute.For<IWebHostEnvironment>();
                env.ContentRootPath.Returns(_h.Dir);
                cause = "Exchange Online settings could not be read: " + SettingsStoreText.ServiceModuleConfig(ExoConnectionPool.ConfigModuleKey);
                return PoolWhoseWriteBorrowFails(_ => rows.Fail(), moduleConfig: new ModuleConfigService(
                    new ModuleCatalog(), env, new ModuleConfigRepository(rows), NullLogger<ModuleConfigService>.Instance));
            case "connect busy":
                cause = "Exchange service is busy establishing connections. Please try again shortly.";
                return PoolWhoseWriteBorrowThrows(new InvalidOperationException(cause));
            case "pool stopped":
                cause = new ObjectDisposedException(typeof(ExoConnectionPool).FullName).Message;
                return PoolWhoseWriteBorrowFails(pool => pool.Dispose());
            default:
                cause = "the session to Exchange Online was lost";
                return PoolWhoseWriteBorrowThrows(LostSession());
        }
    }

    [Theory]
    [MemberData(nameof(PagesAndRefusedBorrows))]
    public async Task ACloudWriteWhoseBorrowIsRefused_IsRecordedAsFailed_NothingWasWritten(string page, string refusal)
    {
        // aud-a71: the catch said the change could not be confirmed and recorded nothing; aud-a73 filed its row Undetermined.
        // But a cloud write's one throw is its own borrow - the pool refusing it, or the connection failing - before any
        // command was sent (aud-a76): nothing can have been written, so the row is Failed and says so, in the pool's words.
        using var pool = PoolWhoseWriteBorrowMeets(refusal, out var cause);
        var (progress, activity) = WatchedProgress();

        var (result, warnings) = await Submit(page, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, grantee: Other, progress: progress, pool: pool);

        // aud-a70 (17): one line - the pool's "stopped" refusal puts its object's name on a line of its own.
        var banner = $"Nothing was written: the connection to Exchange failed: {OneLine(cause).TrimEnd('.')}.";
        Assert.False(result.Success);
        Assert.Equal(banner, result.Message);
        Assert.DoesNotContain('\n', result.Message);
        Assert.DoesNotContain('\r', result.Message);
        Assert.DoesNotContain("raw-text-a71", result.Message, StringComparison.Ordinal);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(page == MailboxPage ? "AddFullAccess" : "SetCalendar", Text(record, "action"));
        Assert.Equal(page == MailboxPage ? "FullAccess" : "Reviewer", Text(record, page == MailboxPage ? "permissionType" : "accessRight"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(banner, Text(record, "error"));
        Assert.Equal(Target, Text(record, "target"));
        Assert.Equal(Other, Text(record, "affectedUser"));
        Assert.Equal(TargetMailbox.ToString("D"), Text(record, ValidatedMailbox.AuditKey));
        activity.Received(1).Complete(false, banner);
        var thrown = Assert.IsType<PermissionWriteNotSubmittedException>(Assert.Single(warnings).Exception);
        Assert.Equal(Assert.IsAssignableFrom<Exception>(thrown.InnerException).Message, thrown.Message);
        Assert.NotEmpty(_h.CallsTo("Get-Mailbox"));
        Assert.Empty(_h.Writes);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task ACloudWriteThatThrows_WhoseRowTheWriterDrops_SaysTheRowCouldNotBeWritten(string page)
    {
        using var pool = PoolWhoseWriteBorrowThrows(LostSession());
        var (progress, activity) = WatchedProgress();
        PermissionResult result;
        IReadOnlyList<(string Message, Exception? Exception)> warnings;
        using (HoldTheAuditFile())
            (result, warnings) = await Submit(page, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, grantee: Other, progress: progress, pool: pool);

        var banner = WriteNotSent + " The audit row could not be written.";
        Assert.False(result.Success);
        Assert.Equal(banner, result.Message);
        activity.Received(1).Complete(false, banner);
        Assert.Single(warnings);
        Assert.Empty(_h.AuditRows());
        Assert.Empty(_h.Writes);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task ACloudWriteThatThrows_WhoseRecordThrows_SaysSo_AndNothingEscapesTheHandler(string page)
    {
        var failure = LostSession();
        using var pool = PoolWhoseWriteBorrowThrows(failure);

        var (result, warnings) = await Submit(page, ExchangeNamesTheAliasAsTheOperator(), AuditWithoutAWriter(), grantee: Other, pool: pool);

        Assert.False(result.Success);
        Assert.Equal(WriteNotSent + " The audit row could not be written.", result.Message);
        Assert.Equal(2, warnings.Count);
        Assert.Same(failure, Assert.IsType<PermissionWriteNotSubmittedException>(warnings[0].Exception).InnerException);
        Assert.IsType<NullReferenceException>(warnings[1].Exception);
        Assert.Contains("the failed permission change", warnings[1].Message, StringComparison.Ordinal);
        Assert.Contains(Target, warnings[1].Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
    }

    [Fact]
    public async Task ACloudRemovalWhoseRetryCannotConnect_AfterItsCommandWasSent_IsRecordedAsUndetermined()
    {
        // aud-a76. The one cloud write that can throw after its command was sent: the Calendar removal, which the pool runs
        // again when its session answers with the pre-flight error. Here the first attempt sends Remove-MailboxFolderPermission,
        // which answers so, and the retry's connect fails. Nothing rests "nothing was written" on the pool's reading of that
        // error: the command was sent, so the row is Undetermined, as its banner says.
        var failure = LostSession();
        using var pool = PoolWhoseWriteBorrowFails(_ => { }, failure);
        _h.Failures["Remove-MailboxFolderPermission"] = "You must call Connect-ExchangeOnline before calling any other cmdlet.";
        var logger = new CapturingLogger<CalendarPermissions>();
        var page = new CalendarPermissions();
        Wire(page, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, logger);
        Set(page, "CalendarService", new CalendarPermissionService(pool, null!, NullLogger<CalendarPermissionService>.Instance, _h.Config, null!, _h.Trace));
        SetField(page, "tabIndex", 1);
        SetField(page, "affectedUser", Other);

        await Call(page, "SubmitSingle");

        var result = Assert.IsType<PermissionResult>(GetField<PermissionResult>(page, "result"));
        Assert.False(result.Success);
        Assert.Equal(WriteNotConfirmed, result.Message);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal("RemoveCalendar", Text(record, "action"));
        Assert.Equal("Undetermined", Text(record, "result"));
        Assert.Equal(WriteNotConfirmed, Text(record, "error"));
        Assert.Same(failure, Assert.Single(logger.Warnings).Exception);
        Assert.Single(_h.CallsTo("Remove-MailboxFolderPermission"));
    }

    // ---- aud-a73: a write whose outcome is unknown is filed Undetermined; a step before or after it, Failed --------------

    [Theory]
    [InlineData(PermissionChangeFailure.BeforeTheWrite, AuditResult.Failed)]
    [InlineData(PermissionChangeFailure.TheWrite, AuditResult.Undetermined)]
    [InlineData(PermissionChangeFailure.TheConnection, AuditResult.Failed)]
    [InlineData(PermissionChangeFailure.AfterTheWrite, AuditResult.Failed)]
    public void TheStepsAuditResult_IsUndetermined_OnlyAtTheWrite(string step, AuditResult filed) =>
        Assert.Equal(filed, PermissionChangeFailure.AuditResultOf(step));

    [Theory]
    [InlineData(PermissionChangeFailure.BeforeTheWrite, true, PermissionChangeFailure.BeforeTheWrite)]
    [InlineData(PermissionChangeFailure.BeforeTheWrite, false, PermissionChangeFailure.BeforeTheWrite)]
    [InlineData(PermissionChangeFailure.TheWrite, true, PermissionChangeFailure.TheConnection)]
    [InlineData(PermissionChangeFailure.TheWrite, false, PermissionChangeFailure.TheWrite)]
    [InlineData(PermissionChangeFailure.AfterTheWrite, true, PermissionChangeFailure.AfterTheWrite)]
    [InlineData(PermissionChangeFailure.AfterTheWrite, false, PermissionChangeFailure.AfterTheWrite)]
    public void TheStageOfAThrow_IsTheConnection_OnlyForAWriteThatWasNeverSent(string step, bool neverSent, string stage)
    {
        // aud-a76: only the service's word that the write's command was never sent moves a throw at the write - to the
        // connection, where nothing can have been written. Any other throw there may have written.
        var cause = new InvalidOperationException("the cause");
        Exception thrown = neverSent ? new PermissionWriteNotSubmittedException(cause) : cause;

        Assert.Equal(stage, PermissionChangeFailure.StageOf(step, thrown));
    }

    /// <summary>
    /// A pool for the page's own service whose Exchange Online answers, as Exchange does, that it holds no such mailbox: an
    /// ObjectNotFound error, which the location read takes as an answer, so it asks on-premises Exchange next.
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

    /// <summary>The harness's settings and an on-premises server, so the page service's location read can ask it.</summary>
    private IConfiguration WithAnOnPremServer() => new ConfigurationBuilder()
        .AddConfiguration(_h.Config)
        .AddInMemoryCollection(new Dictionary<string, string?> { ["OnPremExchange:ServerUri"] = "https://exchange.contoso.test/PowerShell/" })
        .Build();

    /// <summary>
    /// <paramref name="page"/>'s <c>SubmitSingle</c> over a Full Access or Reviewer grant to someone else on a mailbox only
    /// on-premises Exchange holds - Exchange Online answers that it has none, the harness's stand-in on-premises finds it -
    /// whose on-premises permission check throws <paramref name="failure"/>; the result, the page's warnings, and whether the
    /// change was staged for the operator's confirmation.
    /// </summary>
    private async Task<(PermissionResult Result, IReadOnlyList<(string Message, Exception? Exception)> Warnings, bool Staged)> SubmitToAMailboxOnlyOnPremisesHolds(
        string page, Exception failure)
    {
        using var pool = PoolWhereExchangeOnlineHoldsNoMailbox();
        ComponentBase component;
        Func<IReadOnlyList<(string Message, Exception? Exception)>> warnings;
        if (page == MailboxPage)
        {
            var logger = new CapturingLogger<MailboxPermissions>();
            component = new MailboxPermissions();
            Wire(component, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, logger);
            Set(component, "MailboxService", new OnPremMailboxService(_h, pool, WithAnOnPremServer()));
            SetField(component, "grantFullAccess", true);
            SetField(component, "grantSendAs", false);
            warnings = () => logger.Warnings;
        }
        else
        {
            var logger = new CapturingLogger<CalendarPermissions>();
            component = new CalendarPermissions();
            Wire(component, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, logger);
            Set(component, "CalendarService", new OnPremCalendarService(_h, pool, WithAnOnPremServer()));
            SetField(component, "accessRight", CalendarAccessRight.Reviewer);
            warnings = () => logger.Warnings;
        }
        SetField(component, "affectedUser", Other);
        var authorization = (IAuthorizationService)component.GetType().GetProperty("AuthorizationService", Members)!.GetValue(component)!;
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                page == MailboxPage ? "MailboxPermissionsOnPrem" : "CalendarPermissionsOnPrem")
            .Returns(Task.FromException<AuthorizationResult>(failure));

        await Call(component, "SubmitSingle");

        Assert.False(GetField<bool>(component, "isLoading"));
        return (Assert.IsType<PermissionResult>(GetField<PermissionResult>(component, "result")), warnings(),
            GetField<bool>(component, "onPremConfirmPending"));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task ASingleChangeThatThrowsBeforeTheWrite_IsRecordedAsFailed(string page)
    {
        // SubmitSingle's one step before the write that can throw: the on-premises permission check, asked once the location
        // read finds the mailbox on-premises. Nothing was written, so the row is Failed - never Undetermined.
        var failure = new InvalidOperationException("raw-text-a73");
        failure.Data[ExchangeServiceBase.PsErrorsDataKey] = new[] { "the policy store cannot be read" };

        var (result, warnings, staged) = await SubmitToAMailboxOnlyOnPremisesHolds(page, failure);

        var banner = "The change was not made: a step before the write failed: the policy store cannot be read. Nothing was written.";
        Assert.False(result.Success);
        Assert.Equal(banner, result.Message);
        Assert.False(staged);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(page == MailboxPage ? "AddFullAccess" : "SetCalendar", Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(banner, Text(record, "error"));
        Assert.Same(failure, Assert.Single(warnings).Exception);
        Assert.NotEmpty(_h.CallsTo("Invoke-Command"));
        Assert.Empty(_h.Writes);
    }

    // ---- aud-a75: the on-premises write's catch files by its step, as SubmitSingle's does --------------------------------

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AnOnPremWriteWhoseConnectFails_IsRecordedAsFailed_NothingWasWritten(string page)
    {
        // aud-a75 filed a throw at ExecuteOnPrem's write step Undetermined, and this one with it: the write's connect, which
        // throws before any Invoke-Command is sent. aud-a76: the service says so, so nothing can have been written - Failed,
        // in the connect's captured words, never its raw text. A throw before the write stays Failed as before: aud-a59's
        // AnOnPremStepThatThrowsBeforeTheWrite_... pins it.
        var failure = new InvalidOperationException("raw-text-a75");
        failure.Data[ExchangeServiceBase.PsErrorsDataKey] = new[] { "the on-premises session could not be opened" };

        var (result, warnings) = await ConfirmOnPrem(page, Other, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, onPremConnect: failure);

        var banner = "Nothing was written: the connection to Exchange failed: the on-premises session could not be opened.";
        Assert.False(result.Success);
        Assert.Equal(banner, result.Message);
        Assert.DoesNotContain("raw-text-a75", result.Message, StringComparison.Ordinal);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(page == MailboxPage ? "OnPrem_AddFullAccess" : "OnPrem_SetCalendar", Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(banner, Text(record, "error"));
        Assert.Same(failure, Assert.IsType<PermissionWriteNotSubmittedException>(Assert.Single(warnings).Exception).InnerException);
        Assert.Empty(_h.CallsTo("Invoke-Command"));
        Assert.Empty(_h.Writes);
    }

    [Fact]
    public async Task AnOnPremCalendarWriteExchangeRefuses_IsItsOwnFailedRow_AsMailboxPermissionsIs()
    {
        // aud-a70 (12). The remote set failed, its fallback add failed too, and the Invoke-Command threw what the command said:
        // the catch filed a write Exchange refused as one that could not be confirmed (aud-a75, aud-a76). Exchange answered, so
        // the write is a failed result - its own row Failed through the write's own record, as on Mailbox Permissions.
        _h.Failures["Set-MailboxFolderPermission"] = "the folder permission could not be set";
        _h.Failures["Add-MailboxFolderPermission"] = "the folder permission could not be added";

        var (result, warnings) = await ConfirmOnPrem(CalendarPage, Other, ExchangeNamesTheAliasAsTheOperator(), _h.Audit);

        Assert.False(result.Success);
        Assert.Equal($"Failed on {Target} calendar (on-premises): the folder permission could not be added.", result.Message);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal("OnPrem_SetCalendar", Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(result.Message, Text(record, "error"));
        Assert.Empty(warnings);
        Assert.Single(_h.CallsTo("Invoke-Command"));
        Assert.Equal(new[] { "Set-MailboxFolderPermission", "Add-MailboxFolderPermission" }, _h.Writes);
    }

    // ---- aud-a70 (14): a write whose session failed once its command was sent is filed Undetermined ----------------------

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task ACloudWriteWhoseSessionFailsOnceSent_IsRecordedAsUndetermined(string page)
    {
        // The right's result said Failed (TryRight; Calendar's RunAsync) though its command was sent and its outcome is unknown.
        const string lost = "the session to Exchange Online was lost";
        _h.Failures["Add-MailboxPermission"] = lost;
        _h.Failures["Set-MailboxFolderPermission"] = lost;
        _h.Failures["Add-MailboxFolderPermission"] = lost;
        var email = EmailWhere(null);

        var (result, _) = await Submit(page, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, grantee: Other, email: email);

        Assert.False(result.Success);
        Assert.True(result.Unconfirmed);
        Assert.Contains($"could not be confirmed: {lost}. Check the permissions before trying again.", result.Message, StringComparison.Ordinal);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(page == MailboxPage ? "AddFullAccess" : "SetCalendar", Text(record, "action"));
        Assert.Equal("Undetermined", Text(record, "result"));
        Assert.Equal(result.Message, Text(record, "error"));
        // Nothing is known to have changed, so neither the user nor the owner is told it did.
        await email.DidNotReceive().SendUserNotificationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        await email.DidNotReceive().SendOwnerNotificationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AnOnPremWriteWhoseSessionBreaksOnceSent_IsRecordedAsUndetermined(string page)
    {
        const string broken = "Processing data from remote server failed";
        _h.OnPremTransportFailures[TheWrite(page)] = broken;

        var (result, warnings) = await ConfirmOnPrem(page, Other, ExchangeNamesTheAliasAsTheOperator(), _h.Audit);

        Assert.False(result.Success);
        Assert.True(result.Unconfirmed);
        Assert.Contains($"could not be confirmed: {broken}. Check the permissions before trying again.", result.Message, StringComparison.Ordinal);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(page == MailboxPage ? "OnPrem_AddFullAccess" : "OnPrem_SetCalendar", Text(record, "action"));
        Assert.Equal("Undetermined", Text(record, "result"));
        Assert.Equal(result.Message, Text(record, "error"));
        Assert.Single(_h.CallsTo(TheWrite(page)));
        Assert.DoesNotContain(warnings, warning => warning.Exception is not null && warning.Message.Contains("failed at", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnOnPremRightExchangeRefuses_IsItsOwnFailedRow_AsBefore()
    {
        // The Mailbox Permissions counterpart, which aud-a76 leaves as it was: each right is TryRightOnPrem, so Exchange
        // refusing one is the write's result, not a throw - its own row Failed, through the write's own record, and nothing
        // reaches the catch. So no throw after an on-premises Mailbox Permissions command was sent reaches the catch. aud-a70
        // (14): a session that breaks once sent is the right's result too, unconfirmed (AnOnPremWriteWhoseSessionBreaksOnceSent_...).
        _h.Failures["Add-MailboxPermission"] = "the grant was refused";

        var (result, warnings) = await ConfirmOnPrem(MailboxPage, Other, ExchangeNamesTheAliasAsTheOperator(), _h.Audit);

        Assert.False(result.Success);
        Assert.Contains("the grant was refused", result.Message, StringComparison.Ordinal);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal("OnPrem_AddFullAccess", Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(result.Message, Text(record, "error"));
        Assert.Empty(warnings);
        Assert.Single(_h.CallsTo("Invoke-Command"));
    }

    // ---- aud-a70 (1): a written change's own record sits in a guard, and a row not written is said ------------------------

    /// <summary>The write each page's grant to someone else makes, on-premises or in Exchange Online alike.</summary>
    private static string TheWrite(string page) => page == MailboxPage ? "Add-MailboxPermission" : "Set-MailboxFolderPermission";

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AWrittenChange_WhoseRowTheWriterDrops_StandsAsWritten_AndSaysTheRowCouldNotBeWritten(string page)
    {
        // The writer hands back that it dropped the row (aud-a28u) and logs it; the change's banner said nothing of it.
        var email = EmailWhere(null);
        PermissionResult result;
        IReadOnlyList<(string Message, Exception? Exception)> warnings;
        using (HoldTheAuditFile())
            (result, warnings) = await Submit(page, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, grantee: Other, email: email);

        Assert.True(result.Success, result.Message);
        Assert.EndsWith(" The audit row could not be written.", result.Message, StringComparison.Ordinal);
        AssertEveryNotificationWasSent(email);
        Assert.Empty(warnings);
        Assert.Empty(_h.AuditRows());
        Assert.Equal(TheWrite(page), Assert.Single(_h.Writes));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AnOnPremChange_WhoseRecordThrows_StandsAsWritten_AndSaysTheRowCouldNotBeWritten(string page)
    {
        // ExecuteOnPrem's success records sat in no guard: a throw went to the catch, which said "The change was written, but a
        // step after the write failed" and showed the written change as failed.
        var email = EmailWhere(null);

        var (result, warnings) = await ConfirmOnPrem(page, Other, ExchangeNamesTheAliasAsTheOperator(), AuditWithoutAWriter(), email);

        Assert.True(result.Success, result.Message);
        Assert.EndsWith(" The audit row could not be written.", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("a step after the write failed", result.Message, StringComparison.Ordinal);
        AssertEveryNotificationWasSent(email);
        var (message, exception) = Assert.Single(warnings);
        Assert.IsType<NullReferenceException>(exception);
        Assert.Contains("the on-premises permission change", message, StringComparison.Ordinal);
        Assert.Contains(Target, message, StringComparison.Ordinal);
        Assert.Equal(TheWrite(page), Assert.Single(_h.Writes));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AnOnPremChange_WhoseRowTheWriterDrops_StandsAsWritten_AndSaysTheRowCouldNotBeWritten(string page)
    {
        var email = EmailWhere(null);
        PermissionResult result;
        IReadOnlyList<(string Message, Exception? Exception)> warnings;
        using (HoldTheAuditFile())
            (result, warnings) = await ConfirmOnPrem(page, Other, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, email);

        Assert.True(result.Success, result.Message);
        Assert.EndsWith(" The audit row could not be written.", result.Message, StringComparison.Ordinal);
        AssertEveryNotificationWasSent(email);
        Assert.Empty(warnings);
        Assert.Empty(_h.AuditRows());
        Assert.Equal(TheWrite(page), Assert.Single(_h.Writes));
    }

    [Fact]
    public void AChangesOwnRecord_SaysWhetherItsRowWasWritten_AndAThrowNeverLeavesIt()
    {
        var logger = new CapturingLogger<MailboxPermissions>();
        var failure = new InvalidOperationException("the disk is full - payload-a70");
        var ok = PermissionResult.Ok("granted");

        Assert.True(SelfGrantRefusalRecord.Recorded(() => JsonlWriteOutcome.Written, logger, Target, SelfGrantRefusalRecord.OnPremChangeRecord));
        Assert.False(SelfGrantRefusalRecord.Recorded(() => JsonlWriteOutcome.FileWriteFailed, logger, Target, SelfGrantRefusalRecord.OnPremChangeRecord));
        Assert.Empty(logger.Warnings);
        Assert.False(SelfGrantRefusalRecord.Recorded(() => throw failure, logger, Target, SelfGrantRefusalRecord.OnPremChangeRecord));
        var (message, exception) = Assert.Single(logger.Warnings);
        Assert.Same(failure, exception);
        Assert.Equal($"Failed to write audit for the on-premises permission change on {Target}; the change stands", message);

        Assert.Same(ok, SelfGrantRefusalRecord.Said(ok, written: true));
        var said = SelfGrantRefusalRecord.Said(ok, written: false);
        Assert.True(said.Success);
        Assert.Equal("granted The audit row could not be written.", said.Message);
    }

    // ---- aud-a70 (4): the on-premises write's failure row carries what the write was bound to -----------------------------

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AnOnPremChangeThatFails_IsRecordedWithTheIdentityItWasBoundTo(string page)
    {
        // ExecuteOnPrem's catch recorded a failed change with neither the identity written nor the servicer note, which its
        // success rows and SubmitSingle's catch row carry: the row did not say which mailbox the failed change was bound to.
        // Both ride one dictionary, built from the write-time re-check, so the identity here is the note's carrier too.
        var failure = new InvalidOperationException("the on-premises session could not be opened");

        await ConfirmOnPrem(page, Other, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, onPremConnect: failure);

        var record = Assert.Single(_h.AuditRows());
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(TargetMailbox.ToString("D"), Text(record, ValidatedMailbox.AuditKey));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AnOnPremChangeThatFailsBeforeItsReCheck_IsRecordedWithNoIdentity(string page)
    {
        // A throw before the write-time re-check has nothing validated to name, and the row carries none.
        var failure = new InvalidOperationException("the policy store cannot be read");

        await ConfirmOnPrem(page, Other, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, onPremAuthorization: failure);

        var record = Assert.Single(_h.AuditRows());
        Assert.Equal("Failed", Text(record, "result"));
        Assert.False(record.TryGetProperty(ValidatedMailbox.AuditKey, out _));
    }

    // ---- aud-a70 (5): a refusal before the write is recorded, as a throw before it is ---------------------------------------

    public static TheoryData<string, string> PagesAndRefusalsBeforeTheWrite => new()
    {
        { MailboxPage, "the location cannot be read" }, { MailboxPage, "no mailbox anywhere" }, { MailboxPage, "on-premises not permitted" },
        { CalendarPage, "the location cannot be read" }, { CalendarPage, "no mailbox anywhere" }, { CalendarPage, "on-premises not permitted" },
    };

    /// <summary>On-premises Exchange answering, as Exchange does, that it holds no such mailbox and no such remote mailbox.</summary>
    private const string OnPremHoldsNothing = """
        function global:Get-Mailbox {
            [CmdletBinding()] param([Parameter(Position = 0)] $Identity)
            Write-StandInCall 'Get-Mailbox' $PSBoundParameters
            Write-Error -Message "The operation couldn't be performed because object '$Identity' couldn't be found." -Category ObjectNotFound
        }
        function global:Get-RemoteMailbox {
            [CmdletBinding()] param([Parameter(Position = 0)] $Identity)
            Write-StandInCall 'Get-RemoteMailbox' $PSBoundParameters
            Write-Error -Message "The operation couldn't be performed because object '$Identity' couldn't be found." -Category ObjectNotFound
        }
        """;

    /// <summary>
    /// <paramref name="page"/>'s <c>SubmitSingle</c> over a Full Access or Reviewer grant to someone else, refused before its write
    /// as <paramref name="refusal"/> says: Exchange Online's location read failing; neither Exchange Online nor on-premises
    /// Exchange holding the mailbox; or on-premises Exchange holding it and the operator not permitted to change it there. The
    /// result, the page's warnings, the refusal's words and whether anything was staged for confirmation.
    /// </summary>
    private async Task<(PermissionResult Result, IReadOnlyList<(string Message, Exception? Exception)> Warnings, string Refusal, bool Staged)> RefuseBeforeTheWrite(
        string page, string refusal)
    {
        ExoConnectionPool? pool = null;
        string? onPremScript = null;
        string expected;
        switch (refusal)
        {
            case "the location cannot be read":
                _h.Failures["Get-Mailbox"] = "the directory is busy";
                expected = $"Could not determine mailbox location for {Target}: Exchange Online could not be asked: the directory is busy. Nothing was written.";
                break;
            case "no mailbox anywhere":
                pool = PoolWhereExchangeOnlineHoldsNoMailbox();
                onPremScript = OnPremHoldsNothing;
                expected = $"Could not determine mailbox location for {Target}. No cloud mailbox, on-prem mailbox, or on-prem remote mailbox was found. Nothing was written.";
                break;
            default:
                pool = PoolWhereExchangeOnlineHoldsNoMailbox();
                expected = $"{Target} is an on-premises mailbox. Please escalate this ticket to the Exchange team. Nothing was written.";
                break;
        }

        try
        {
            ComponentBase component;
            Func<IReadOnlyList<(string Message, Exception? Exception)>> warnings;
            if (page == MailboxPage)
            {
                var logger = new CapturingLogger<MailboxPermissions>();
                component = new MailboxPermissions();
                Wire(component, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, logger);
                Set(component, "MailboxService", new OnPremMailboxService(_h, pool, WithAnOnPremServer(), onPremScript: onPremScript));
                SetField(component, "grantFullAccess", true);
                SetField(component, "grantSendAs", false);
                warnings = () => logger.Warnings;
            }
            else
            {
                var logger = new CapturingLogger<CalendarPermissions>();
                component = new CalendarPermissions();
                Wire(component, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, logger);
                Set(component, "CalendarService", new OnPremCalendarService(_h, pool, WithAnOnPremServer(), onPremScript: onPremScript));
                SetField(component, "accessRight", CalendarAccessRight.Reviewer);
                warnings = () => logger.Warnings;
            }
            SetField(component, "affectedUser", Other);
            if (refusal == "on-premises not permitted")
            {
                var authorization = (IAuthorizationService)component.GetType().GetProperty("AuthorizationService", Members)!.GetValue(component)!;
                authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(),
                        page == MailboxPage ? "MailboxPermissionsOnPrem" : "CalendarPermissionsOnPrem")
                    .Returns(Task.FromResult(AuthorizationResult.Failed()));
            }

            await Call(component, "SubmitSingle");

            Assert.False(GetField<bool>(component, "isLoading"));
            return (Assert.IsType<PermissionResult>(GetField<PermissionResult>(component, "result")), warnings(), expected,
                GetField<bool>(component, "onPremConfirmPending"));
        }
        finally
        {
            pool?.Dispose();
        }
    }

    [Theory]
    [MemberData(nameof(PagesAndRefusalsBeforeTheWrite))]
    public async Task ARefusalBeforeTheWrite_IsRecordedAsFailed_NothingWasWritten(string page, string refusal)
    {
        // SubmitSingle's refusals once the target was validated returned with no audit row - a refused change the event log
        // never saw. Each is now the change's own row, Failed, its error the refusal, with the identity it was bound to.
        var (result, warnings, expected, staged) = await RefuseBeforeTheWrite(page, refusal);

        Assert.False(result.Success);
        Assert.Equal(expected, result.Message);
        Assert.False(staged);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(page == MailboxPage ? "AddFullAccess" : "SetCalendar", Text(record, "action"));
        Assert.Equal(page == MailboxPage ? "FullAccess" : "Reviewer", Text(record, page == MailboxPage ? "permissionType" : "accessRight"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(expected, Text(record, "error"));
        Assert.Equal(Other, Text(record, "affectedUser"));
        Assert.Equal(TargetMailbox.ToString("D"), Text(record, ValidatedMailbox.AuditKey));
        Assert.Empty(warnings);
        Assert.Empty(_h.Writes);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task TheOnPremWrite_WhenNotAuthorized_IsRecordedAsFailed_NothingWasWritten(string page)
    {
        // ExecuteOnPrem's own authorization refusal, the twin of SubmitSingle's on-premises refusal, returned with no row too.
        var (result, warnings) = await ConfirmOnPrem(page, Other, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, onPremDenied: true);

        Assert.False(result.Success);
        Assert.Equal("Authorization denied for on-prem operations. Nothing was written.", result.Message);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(page == MailboxPage ? "OnPrem_AddFullAccess" : "OnPrem_SetCalendar", Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(result.Message, Text(record, "error"));
        Assert.Empty(warnings);
        Assert.Empty(_h.CallsTo("Invoke-Command"));
        Assert.Empty(_h.Writes);
    }

    // ---- aud-a70 (9): a progress panel that throws on completion never takes the handler with it ---------------------------

    /// <summary>A progress service whose activity throws on every completion, and what it throws.</summary>
    private static (IActivityProgress Progress, IActivityHandle Activity, Exception Failure) ProgressThatCannotComplete()
    {
        var (progress, activity) = WatchedProgress();
        var failure = new InvalidOperationException("the progress panel is gone");
        activity.When(handle => handle.Complete(Arg.Any<bool>(), Arg.Any<string?>())).Do(_ => throw failure);
        return (progress, activity, failure);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task ASingleChange_WhoseProgressCannotComplete_EndsWithItsBanner_AndNothingEscapesTheHandler(string page)
    {
        // The success path's completion throws; the catch records the step after the write, and its own completion - its
        // last statement - threw again, out of the handler, the banner and the rows already set.
        var (progress, activity, failure) = ProgressThatCannotComplete();

        var (result, warnings) = await Submit(page, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, grantee: Other, progress: progress);

        Assert.False(result.Success);
        Assert.StartsWith("The change was written, but a step after the write failed: the progress panel is gone.", result.Message, StringComparison.Ordinal);
        activity.Received(2).Complete(Arg.Any<bool>(), Arg.Any<string?>());
        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, warning => Assert.Same(failure, warning.Exception));
        Assert.Contains("could not be completed", warnings[1].Message, StringComparison.Ordinal);
        Assert.Equal(2, _h.AuditRows().Count);
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AnOnPremChange_WhoseProgressCannotComplete_EndsWithItsResult_AndNothingEscapesTheHandler(string page)
    {
        // ExecuteOnPrem completes its activity in its finally, above the staged target's reset: a throw there left the handler
        // with the target still staged.
        var (progress, activity, failure) = ProgressThatCannotComplete();

        var (result, warnings) = await ConfirmOnPrem(page, Other, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, progress: progress);

        Assert.True(result.Success, result.Message);
        activity.Received(1).Complete(true, Arg.Any<string?>());
        var (message, exception) = Assert.Single(warnings);
        Assert.Same(failure, exception);
        Assert.Contains("could not be completed", message, StringComparison.Ordinal);
    }

    // ---- aud-a70 (17): a banner is one line, whatever lines its cause spans ----------------------------------------------

    /// <summary><paramref name="text"/> with its line breaks and runs of white space as one space each.</summary>
    private static string OneLine(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [Fact]
    public void TheStepBanner_IsOneLine_WhateverLinesItsCauseSpans()
    {
        // The pool's "stopped" refusal is an ObjectDisposedException, whose message puts the object's name on a line of its
        // own: the banner, and the row's error it becomes, carried the line break.
        var stopped = new ObjectDisposedException(typeof(ExoConnectionPool).FullName);
        Assert.Contains('\n', stopped.Message);

        var banner = PermissionChangeFailure.Banner(PermissionChangeFailure.TheConnection, new PermissionWriteNotSubmittedException(stopped));
        var twoLines = PermissionChangeFailure.Banner(PermissionChangeFailure.TheWrite, new InvalidOperationException("the first line\r\n  and the second."));

        Assert.Equal($"Nothing was written: the connection to Exchange failed: {OneLine(stopped.Message).TrimEnd('.')}.", banner);
        Assert.Contains(typeof(ExoConnectionPool).FullName!, banner, StringComparison.Ordinal);
        Assert.Equal("Whether the change was made could not be confirmed: the write failed: the first line and the second. Check the permissions before trying again.", twoLines);
        Assert.All(new[] { banner, twoLines }, text => Assert.False(text.Contains('\n') || text.Contains('\r'), text));
    }

    // ---- aud-a70 (15): a change filed Undetermined tells the administrators, and no one else -------------------------------

    /// <summary>The actions of the administrator notifications <paramref name="email"/> was asked to send, in order.</summary>
    private static IReadOnlyList<string> AdminActions(EmailService email) =>
        email.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(EmailService.SendAdminNotificationAsync))
            .Select(call => (string)call.GetArguments()[2]!)
            .ToList();

    [Fact]
    public async Task ASingleChangeItsCatchFilesUndetermined_TellsTheAdministrators_AndNoOneElse()
    {
        // The Calendar removal whose retry could not connect once its command was sent (aud-a76's case) is filed Undetermined -
        // it may have been made - and sent no notification at all.
        using var pool = PoolWhoseWriteBorrowFails(_ => { }, LostSession());
        _h.Failures["Remove-MailboxFolderPermission"] = "You must call Connect-ExchangeOnline before calling any other cmdlet.";
        var email = EmailWhere(null);
        var page = new CalendarPermissions();
        Wire(page, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, new CapturingLogger<CalendarPermissions>(), email);
        Set(page, "CalendarService", new CalendarPermissionService(pool, null!, NullLogger<CalendarPermissionService>.Instance, _h.Config, null!, _h.Trace));
        SetField(page, "tabIndex", 1);
        SetField(page, "affectedUser", Other);

        await Call(page, "SubmitSingle");

        Assert.Equal("Undetermined", Text(Assert.Single(_h.AuditRows()), "result"));
        await email.Received(1).SendAdminNotificationAsync(Logon, Arg.Any<string>(), "RemoveCalendar_UNCONFIRMED", Target, Other, "Remove",
            false, "INC100", WriteNotConfirmed);
        Assert.Single(AdminActions(email));
        await email.DidNotReceive().SendUserNotificationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
        await email.DidNotReceive().SendOwnerNotificationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>());
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AWriteWhoseOutcomeIsUnknown_TellsTheAdministratorsItIsUnconfirmed(string page)
    {
        // Its administrator notification went out under the change's own action and "FAILED", as though nothing had been made.
        const string lost = "the session to Exchange Online was lost";
        _h.Failures["Add-MailboxPermission"] = lost;
        _h.Failures["Set-MailboxFolderPermission"] = lost;
        _h.Failures["Add-MailboxFolderPermission"] = lost;
        var email = EmailWhere(null);

        var (result, _) = await Submit(page, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, grantee: Other, email: email);

        Assert.True(result.Unconfirmed);
        Assert.Equal(new[] { (page == MailboxPage ? "AddFullAccess" : "SetCalendar") + PermissionChangeNotifier.UnconfirmedSuffix }, AdminActions(email));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AnOnPremWriteWhoseOutcomeIsUnknown_TellsTheAdministratorsItIsUnconfirmed(string page)
    {
        _h.OnPremTransportFailures[TheWrite(page)] = "Processing data from remote server failed";
        var email = EmailWhere(null);

        var (result, _) = await ConfirmOnPrem(page, Other, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, email);

        Assert.True(result.Unconfirmed);
        Assert.Equal(new[] { (page == MailboxPage ? "OnPrem_AddFullAccess" : "OnPrem_SetCalendar") + PermissionChangeNotifier.UnconfirmedSuffix }, AdminActions(email));
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public async Task AChangeThatWroteNothing_SendsNoUnconfirmedNotification(string page)
    {
        // A borrow refused before the command was sent wrote nothing: it is Failed, and no administrator is told of a change
        // that may have been made.
        using var pool = PoolWhoseWriteBorrowThrows(LostSession());
        var email = EmailWhere(null);

        var (result, _) = await Submit(page, ExchangeNamesTheAliasAsTheOperator(), _h.Audit, grantee: Other, email: email, pool: pool);

        Assert.Equal(WriteNotSent, result.Message);
        Assert.Empty(AdminActions(email));
    }

    [Fact]
    public async Task TheUnconfirmedNotification_IsSentOnlyForAnUndeterminedChange_AndAThrowIsLogged()
    {
        var email = EmailWhere(null);
        var logger = new CapturingLogger<MailboxPermissions>();

        foreach (var filed in new[] { AuditResult.Success, AuditResult.Failed })
            await PermissionChangeNotifier.NotifyAdminIfUnconfirmedAsync(filed, email, Logon, "10.0.0.5", "AddFullAccess", Target, Other, "FullAccess", "INC100", "the banner", logger);
        Assert.Empty(AdminActions(email));

        await PermissionChangeNotifier.NotifyAdminIfUnconfirmedAsync(AuditResult.Undetermined, email, Logon, "10.0.0.5", "AddFullAccess", Target, Other, "FullAccess", "INC100", "the banner", logger);
        await email.Received(1).SendAdminNotificationAsync(Logon, "10.0.0.5", "AddFullAccess_UNCONFIRMED", Target, Other, "FullAccess", false, "INC100", "the banner");
        Assert.Empty(logger.Warnings);

        await PermissionChangeNotifier.NotifyAdminIfUnconfirmedAsync(AuditResult.Undetermined, EmailWhere("admin"), Logon, "10.0.0.5", "AddFullAccess", Target, Other, "FullAccess", "INC100", "the banner", logger);
        var (message, exception) = Assert.Single(logger.Warnings);
        Assert.Contains("payload-a53", exception!.Message, StringComparison.Ordinal);
        Assert.Contains(Target, message, StringComparison.Ordinal);
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
