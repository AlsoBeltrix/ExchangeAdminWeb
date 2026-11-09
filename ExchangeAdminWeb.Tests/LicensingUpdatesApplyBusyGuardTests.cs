using System.Security.Claims;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using static ExchangeAdminWeb.Tests.SubmittedValueHarness;
using Page = ExchangeAdminWeb.Components.Pages.LicensingUpdates;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-74 (1), Licensing Updates. <c>RunApply</c> had no busy guard: Apply is disabled while <c>isProcessing</c>, but the
/// browser's copy of that is one round trip stale, so a second click sent before the disabled button rendered ran a second
/// bulk licence apply over the same rows - a second write per row and a second administrator notification. A second click
/// during an apply is now ignored, as a second Preview has been since kd-72.
/// </summary>
/// <remarks>
/// Driven through the compiled handler over the real <see cref="LicensingUpdatesService"/> with no secret ID saved, so
/// the apply's credential gate answers "AD credentials unavailable." and the run ends there - before the directory,
/// protection, the trace or the audit - and still reports and notifies as every apply does. The progress stand-in counts
/// the applies that began, the email stand-in the notifications sent. <see cref="SubmittedValueHarness.HeldContinuations"/>
/// holds the first apply at its yield while the second click lands.
/// </remarks>
public sealed class LicensingUpdatesApplyBusyGuardTests : IDisposable
{
    private readonly TempDir _temp = new();
    private readonly RecordingProgress _progress = new();
    private readonly EmailService _email = Substitute.For<EmailService>(new ConfigurationBuilder().Build(), NullLogger<EmailService>.Instance);
    private readonly LicensingUpdatesService _service;

    public LicensingUpdatesApplyBusyGuardTests()
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_temp.Path);
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env,
            TestConfigStore.CreateModuleConfig(_temp.Path), NullLogger<ModuleConfigService>.Instance);
        var credentials = new ModuleCredentialService(moduleConfig, null!, NullLogger<ModuleCredentialService>.Instance);
        _service = new LicensingUpdatesService(credentials, moduleConfig, null!, null!, null!, null!,
            NullLogger<LicensingUpdatesService>.Instance);
    }

    public void Dispose() => _temp.Dispose();

    // The finding: a second click while the first apply runs returns at once, and one apply ran - one activity, one
    // notification.
    [Fact]
    public void ASecondClickWhileAnApplyRuns_IsIgnored_NotASecondBulkApply()
    {
        var page = PageFor();

        RunWithChangeWhileSuspended(() => Call(page, "RunApply"), () =>
        {
            // A click the browser sent before the disabled Apply rendered.
            var second = Call(page, "RunApply");
            Assert.True(second.IsCompleted, "a second click during an apply started a second apply");
        });

        Assert.Equal("Applying licence changes", Assert.Single(_progress.Handles).Label);
        Assert.Single(AdminNotifications());
        Assert.False(Field<bool>(page, "isProcessing"));
    }

    // The control: the guard ignores a click only while an apply runs. Once it has finished, Apply runs again as before.
    [Fact]
    public void AnApplyAfterTheFirstHasFinished_RunsAsBefore()
    {
        var page = PageFor();

        RunWithChangeWhileSuspended(() => Call(page, "RunApply"), () => { });
        RunWithChangeWhileSuspended(() => Call(page, "RunApply"), () => { });

        Assert.Equal(2, _progress.Handles.Count);
        Assert.Equal(2, AdminNotifications().Count);
    }

    // Sweep item 41. The apply's ticket was gated only by the Apply button's markup, and ApplyCsvAsync has no ticket step: it is
    // now checked behind the button - presence by the shared rule, then ServiceNow where enabled - and a refusal is recorded
    // under the run's per-row action with the refusal fields, before the authorization re-check, the progress frame and the
    // apply.
    public static TheoryData<string> UnusableTickets => ["", "   ", ((char)0x200B).ToString()]; // a zero-width space alone: nothing visible once sanitized

    [Theory]
    [MemberData(nameof(UnusableTickets))]
    public async Task AnApplyWithNoUsableTicket_IsRefusedAndRecorded_AndNothingRuns(string ticket)
    {
        var (page, authorization) = PageWithAudit();
        SetField(page, "ticketNumber", ticket);

        await Call(page, "RunApply");

        var expected = "Ticket validation failed: " + TicketPresence.RequiredMessage;
        Assert.Equal((false, expected), Field<(bool, string)?>(page, "operationResult"));
        var row = Assert.Single(AuditRows(_temp.Path));
        Assert.Equal("LicensingUpdates_Update", Text(row, "action"));
        Assert.Equal("(bulk) 1 rows", Text(row, "target"));
        Assert.Equal("TicketValidation", Text(row, "refusedAt"));
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(expected, Text(row, "error"));
        Assert.Empty(authorization.ReceivedCalls());
        Assert.Empty(_progress.Handles);
        Assert.Empty(AdminNotifications());
        Assert.False(Field<bool>(page, "isProcessing"));
    }

    [Fact]
    public async Task AnApplyWithATicketServiceNowRejects_IsRefusedAndRecorded_AndNothingRuns()
    {
        var (page, authorization) = PageWithAudit();
        var (serviceNow, requests) = TestServiceNow.Enabled("""{"result":[]}""");
        Set(page, "ServiceNow", serviceNow);

        await Call(page, "RunApply");

        var row = Assert.Single(AuditRows(_temp.Path));
        Assert.Equal("Ticket validation failed: Ticket INC0001 not found in ServiceNow", Text(row, "error"));
        Assert.Single(requests);
        Assert.Empty(authorization.ReceivedCalls());
        Assert.Empty(_progress.Handles);
    }

    // Decision 2026-11-09: a ticket pasted with a zero-width space is checked and recorded with the character removed.
    [Fact]
    public async Task AnApplyWithATicketTypedWithAZeroWidthSpace_IsCheckedAndRecordedSanitized()
    {
        var (page, _) = PageWithAudit();
        SetField(page, "ticketNumber", TicketPresenceTests.WithZeroWidthSpace("INC0001"));
        var (serviceNow, requests) = TestServiceNow.Enabled("""{"result":[]}""");
        Set(page, "ServiceNow", serviceNow);

        await Call(page, "RunApply");

        var row = Assert.Single(AuditRows(_temp.Path));
        Assert.Equal("Ticket validation failed: Ticket INC0001 not found in ServiceNow", Text(row, "error"));
        Assert.Equal("INC0001", Text(row, "ticket"));
        Assert.Single(requests);
    }

    private (Page Page, IAuthorizationService Authorization) PageWithAudit()
    {
        var page = PageFor();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Audit:LogRoot"] = _temp.Path }).Build();
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        Set(page, "Audit", new AuditService(log, new OperationTraceService(config, log)));
        var authorization = Authorization();
        Set(page, "AuthorizationService", authorization);
        return (page, authorization);
    }

    private List<NSubstitute.Core.ICall> AdminNotifications() =>
        _email.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(EmailService.SendAdminNotificationAsync)).ToList();

    private Page PageFor()
    {
        var page = new Page();
        Set(page, "LicenseService", _service);
        Set(page, "Progress", _progress);
        Set(page, "Email", _email);
        Set(page, "AuthStateProvider", Authentication());
        Set(page, "AuthorizationService", Authorization());
        Set(page, "ServiceNow", TestServiceNow.Dormant()); // sweep item 41: the page checks its ticket with ServiceNow
        Set(page, "Logger", NullLogger<Page>.Instance);
        SetField(page, "currentUser", @"CONTOSO\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "ticketNumber", "INC0001");
        SetField(page, "previewResult", new LicensePreviewResult(true, null, "E3",
        [
            new LicensePreviewRow("alice@contoso.test",
                new ResolvedDirectoryPrincipal("AD", "Alice", "alice@contoso.test", "alice", "alice@contoso.test",
                    "CN=Alice,OU=Users,DC=contoso,DC=test", "5a5a5a5a-0000-4000-8000-000000000001", null),
                "E1", "E3", null),
        ]));
        return page;
    }

    private static AuthenticationStateProvider Authentication()
    {
        var state = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, @"CONTOSO\operator")], "test")));
        var provider = Substitute.For<AuthenticationStateProvider>();
        provider.GetAuthenticationStateAsync().Returns(Task.FromResult(state));
        return provider;
    }

    private static IAuthorizationService Authorization()
    {
        var service = Substitute.For<IAuthorizationService>();
        service.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        return service;
    }
}
