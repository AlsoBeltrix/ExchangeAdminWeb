using System.Security.Claims;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using static ExchangeAdminWeb.Tests.SubmittedValueHarness;
using Page = ExchangeAdminWeb.Components.Pages.BlockedSenders;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-72 on Blocked Senders: <c>ConfirmUnblock</c> took the address, the ticket and the reason on the line after its
/// paint yield, so a keystroke in the ticket box - or Cancel, which clears all three - landing in that yield changed
/// the address unblocked and what was recorded against it. They are now read above the yield, and a second click
/// during an unblock is ignored.
/// </summary>
/// <remarks>
/// Driven through the compiled handler with the authorization re-check refusing, so the run stops at its first record -
/// the denied row, which carries the address and the ticket - and nothing reaches Exchange.
/// <see cref="SubmittedValueHarness.HeldContinuations"/> holds the handler at its yield while the test changes the
/// fields. The reason is read with the other two but is recorded only by the write's notice, which this does not reach.
/// </remarks>
public sealed class BlockedSendersSubmittedUnblockTests : IDisposable
{
    private const string SenderA = "first.sender@contoso.com";
    private const string TicketA = "INC0001";

    private readonly TempDir _temp = new();
    private readonly AuditService _audit;
    private readonly RecordingProgress _progress = new();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();

    public BlockedSendersSubmittedUnblockTests()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _temp.Path,
        }).Build();
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        _audit = new AuditService(log, new OperationTraceService(config, log));
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(Task.FromResult(AuthorizationResult.Failed()));
    }

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void ATicketTypedOrCancelLandingInTheYield_LeavesTheRecordOnTheAddressAndTicketConfirmed()
    {
        var page = PageFor();

        RunWithChangeWhileSuspended(() => Call(page, "ConfirmUnblock"), () =>
        {
            SetField(page, "ticketNumber", "INC9999");
            // Cancel, the confirm row's other button: it clears all three.
            SetField(page, "confirmAddress", null);
            SetField(page, "reason", "");
        });

        Assert.Equal($"Unblocking {SenderA}", Assert.Single(_progress.Handles).Label);
        var row = Assert.Single(AuditRows(_temp.Path));
        Assert.Equal("UnblockSender_Denied", Text(row, "action"));
        Assert.Equal(SenderA, Text(row, "target"));
        Assert.Equal(TicketA, Text(row, "ticket"));
    }

    [Fact]
    public void ASecondClickWhileAnUnblockRuns_IsIgnored_NotRunBesideIt()
    {
        var page = PageFor();

        RunWithChangeWhileSuspended(() => Call(page, "ConfirmUnblock"), () =>
        {
            // A click the browser sent before the disabled Confirm rendered.
            var second = Call(page, "ConfirmUnblock");
            Assert.True(second.IsCompleted, "a second click during an unblock started a second unblock");
        });

        Assert.Single(_progress.Handles);
        Assert.Single(AuditRows(_temp.Path));
        Assert.Single(_authorization.ReceivedCalls());
    }

    private Page PageFor()
    {
        var page = new Page();
        var auth = Substitute.For<AuthenticationStateProvider>();
        auth.GetAuthenticationStateAsync().Returns(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, @"CONTOSO\operator")], "Negotiate"))));
        Set(page, "Progress", _progress);
        Set(page, "Audit", _audit);
        Set(page, "AuthStateProvider", auth);
        Set(page, "AuthorizationService", _authorization);
        Set(page, "Logger", NullLogger<Page>.Instance);
        SetField(page, "currentUser", @"CONTOSO\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "confirmAddress", SenderA);
        SetField(page, "ticketNumber", TicketA);
        SetField(page, "reason", "listed in error");
        return page;
    }
}
