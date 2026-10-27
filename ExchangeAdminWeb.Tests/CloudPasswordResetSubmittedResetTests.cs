using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using static ExchangeAdminWeb.Tests.SubmittedValueHarness;
using Page = ExchangeAdminWeb.Components.Pages.CloudPasswordReset;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-72 on Cloud Password Reset: <c>ExecuteResetAsync</c> took its ticket after its paint yield, and read the
/// force-change choice at the write, in the owner's email, in every audit row and in the administrator alert - all
/// after several awaits. The tick box reaches the browser's disabled state a round trip late, so a tick or a keystroke
/// landing there changed the reset and what it recorded. Both are now taken above the yield.
/// </summary>
/// <remarks>
/// Driven through the compiled handler with the authorization re-check refusing, so the run stops at its first record
/// - a refusal row carrying the ticket and the force-change choice - and nothing reaches Graph or a directory. The
/// administrator alert is a substitute that records what it is handed. <see cref="SubmittedValueHarness.HeldContinuations"/>
/// holds the handler at its yield.
/// </remarks>
public sealed class CloudPasswordResetSubmittedResetTests : IDisposable
{
    private const string TicketA = "INC0001";

    private static readonly CloudPasswordTarget Target = new(
        "0f0f0f0f-1111-4222-8333-444444444444", "first.admin@contoso.onmicrosoft.com", "First Admin",
        AccountEnabled: true, CloudOnly: true, EmployeeId: "E100", DirectoryRoles: [], DirectoryRolesReadFailed: false);

    private readonly TempDir _temp = new();
    private readonly AuditService _audit;
    private readonly EmailService _email;
    private readonly RecordingProgress _progress = new();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();

    public CloudPasswordResetSubmittedResetTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _temp.Path,
        }).Build();
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        _audit = new AuditService(log, new OperationTraceService(config, log));
        _email = Substitute.For<EmailService>(config, Substitute.For<ILogger<EmailService>>());
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(Task.FromResult(AuthorizationResult.Failed()));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ATickOrAKeystrokeLandingInTheYield_LeavesTheRecordsOnTheTicketAndChoiceConfirmed()
    {
        var page = PageFor();

        RunWithChangeWhileSuspended(() => Call(page, "ExecuteResetAsync"), () =>
        {
            SetField(page, "forceChangeAtNextSignIn", false);
            SetField(page, "ticketInput", "INC9999");
        });

        Assert.Equal($"Resetting {Target.UserPrincipalName}", Assert.Single(_progress.Handles).Label);
        var row = Assert.Single(AuditRows(_temp.Path));
        Assert.Equal("CloudPasswordReset_Execute", Text(row, "action"));
        Assert.Equal(Target.UserPrincipalName, Text(row, "target"));
        Assert.Equal(TicketA, Text(row, "ticket"));
        Assert.True(row.GetProperty("forceChangePasswordNextSignIn").GetBoolean());

        var alert = Assert.Single(_email.ReceivedCalls(), call => call.GetMethodInfo().Name == nameof(EmailService.SendAdminNotificationAsync));
        Assert.Contains(TicketA, alert.GetArguments().OfType<string>());
        var details = alert.GetArguments().OfType<Dictionary<string, string>>().Single();
        Assert.Equal("Yes", details["Force change at next sign-in"]);
    }

    [Fact]
    public void TheForceChangeChoice_IsReadFromTheBoxOnce_AboveTheYield_AndTheWriteAndTheMailUseTheCapture()
    {
        // The runtime test stops at the authorization re-check, before the write and the owner's email, so their use of
        // the captured choice is read from the handler: the box is read once, above the first await, and never again
        // by the handler or by the two record builders it calls.
        var source = ClickGateSource.Load("CloudPasswordReset.razor");
        var handler = source.MethodBody("ExecuteResetAsync");
        var read = new Regex(@"\bforceChangeAtNextSignIn\b");

        var reads = read.Matches(handler);
        Assert.Single(reads);
        Assert.True(reads[0].Index < handler.IndexOf("await ", StringComparison.Ordinal),
            "the force-change choice is read below the first await");
        Assert.Contains("resetForceChange = forceChangeAtNextSignIn;", handler, StringComparison.Ordinal);
        Assert.Contains("ResetPasswordAsync(target.ObjectId, resetForceChange)", handler, StringComparison.Ordinal);
        Assert.Contains("outcome.Password, ticket, resetForceChange)", handler, StringComparison.Ordinal);
        Assert.DoesNotMatch(read, source.MethodBody("AuditReset"));
        Assert.DoesNotMatch(read, source.MethodBody("NotifyAdminsAsync"));
    }

    private Page PageFor()
    {
        var page = new Page();
        var auth = Substitute.For<AuthenticationStateProvider>();
        auth.GetAuthenticationStateAsync().Returns(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, @"CONTOSO\operator")], "Negotiate"))));
        Set(page, "Progress", _progress);
        Set(page, "Audit", _audit);
        Set(page, "Email", _email);
        Set(page, "AuthStateProvider", auth);
        Set(page, "AuthorizationService", _authorization);
        Set(page, "Logger", NullLogger<Page>.Instance);
        SetField(page, "currentUser", @"CONTOSO\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "target", Target);
        SetField(page, "ticketInput", TicketA);
        SetField(page, "forceChangeAtNextSignIn", true);
        return page;
    }

    private static string? Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
