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
/// the line hands the check the signed-in principal end to end. Nothing here reaches Exchange or a directory.
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
}
