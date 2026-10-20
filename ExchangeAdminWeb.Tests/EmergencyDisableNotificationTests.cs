using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A27: the emergency-disable notice went to the global admin address instead
/// of the module's configured security recipient, and the page claimed the security team had
/// been notified whatever happened.
/// </summary>
/// <remarks>
/// Behavioural, not source scans: the notification step is reachable on its own with a
/// substituted <see cref="EmailService"/>, so these drive the real method and read the real
/// recipient it addressed.
/// </remarks>
public class EmergencyDisableNotificationTests : IDisposable
{
    private const string SecurityTeam = "soc@contoso.com";
    private const string AdminList = "admins@contoso.com";

    private readonly string _tempDir;
    private readonly EmailService _email = Substitute.For<EmailService>(
        new ConfigurationBuilder().Build(), Substitute.For<ILogger<EmailService>>());

    public EmergencyDisableNotificationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ed-notify-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task TheConfiguredSecurityAddress_IsTheRecipient()
    {
        // The whole finding. The address was read, used as an on/off switch, and then the
        // mail went to Email:AdminNotificationEmail instead.
        var service = CreateService(SecurityTeam);
        Dispatch(new NotificationDispatch(1, 1, null));

        var outcome = await Notify(service);

        Assert.Equal(SecurityNotificationOutcome.Sent, outcome);
        await _email.Received(1).SendModuleNotificationAsync(
            SecurityTeam,
            Arg.Any<string>(), Arg.Any<string>(), "EmergencyDisable", Arg.Any<bool>(),
            Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>());

        // And NOT through the overload that addresses the global admin list.
        await _email.DidNotReceive().SendAdminNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
            Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task NoConfiguredAddress_ReportsNotConfiguredAndSendsNothing()
    {
        var service = CreateService(null);

        var outcome = await Notify(service);

        Assert.Equal(SecurityNotificationOutcome.NotConfigured, outcome);
        await _email.DidNotReceive().SendModuleNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<string?>());
    }

    [Theory]
    [InlineData(1, 0, SecurityNotificationOutcome.Failed)]
    [InlineData(2, 1, SecurityNotificationOutcome.PartiallySent)]
    [InlineData(2, 2, SecurityNotificationOutcome.Sent)]
    public async Task TheOutcomeFollowsWhatWasActuallyDelivered(
        int requested, int delivered, SecurityNotificationOutcome expected)
    {
        // Known Failure Class 2. A send to two recipients that reached one is not a success,
        // and the operator is the one who has to make up the difference.
        var service = CreateService(SecurityTeam);
        Dispatch(new NotificationDispatch(requested, delivered, delivered == requested ? null : "smtp refused"));

        Assert.Equal(expected, await Notify(service));
    }

    [Fact]
    public async Task AThrowingEmailService_ReportsFailedRatherThanSent()
    {
        var service = CreateService(SecurityTeam);
        _email.SendModuleNotificationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string?>())
            .Returns<Task<NotificationDispatch>>(_ => throw new InvalidOperationException("smtp down"));

        Assert.Equal(SecurityNotificationOutcome.Failed, await Notify(service));
    }

    [Fact]
    public void OnlyASentNotification_ClaimsTheSecurityTeamWasNotified()
    {
        // The worse half of the finding was the sentence, so the sentence is pinned.
        Assert.Equal("Security team notified.", SecurityNotificationText.For(SecurityNotificationOutcome.Sent));

        foreach (var outcome in new[]
                 {
                     SecurityNotificationOutcome.NotAttempted,
                     SecurityNotificationOutcome.NotConfigured,
                     SecurityNotificationOutcome.PartiallySent,
                     SecurityNotificationOutcome.Failed,
                 })
        {
            var text = SecurityNotificationText.For(outcome);
            Assert.DoesNotContain("Security team notified", text, StringComparison.Ordinal);
        }

        // The two that mean "nobody heard" say so in terms an operator mid-incident can act on.
        Assert.Contains("NOT NOTIFIED", SecurityNotificationText.For(SecurityNotificationOutcome.NotConfigured), StringComparison.Ordinal);
        Assert.Contains("NOT NOTIFIED", SecurityNotificationText.For(SecurityNotificationOutcome.Failed), StringComparison.Ordinal);
    }

    [Fact]
    public void NoRecipient_IsNeitherFullyNorPartlyDelivered()
    {
        Assert.False(NotificationDispatch.NoRecipient.AnyDelivered);
        Assert.False(NotificationDispatch.NoRecipient.AllDelivered);

        // Zero requested must not read as "all of them got it".
        Assert.False(new NotificationDispatch(0, 0, null).AllDelivered);
    }

    [Fact]
    public void ThePageNoLongerStatesTheNotificationUnconditionally()
    {
        // The service decides; the page may only repeat what it decided. Without this, the
        // next edit to the page can reintroduce the sentence with nothing to stop it.
        var page = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "EmergencyDisable.razor"));

        Assert.DoesNotContain("Security team has been notified.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("disabled. Security team notified.", page, StringComparison.Ordinal);
        Assert.Contains("SecurityNotificationText.For(disableResult.Notification)", page, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNotificationGapIsStillRecordedByDisableAsync()
    {
        // Review of the A27 batch: every other guard here drives the notification in
        // isolation, and the page scan pins one specific sentence. Nothing checked that
        // DisableAsync still WRITES the gap record - the helper could keep working while
        // nothing called it, which is how aud-a40 nearly shipped.
        var body = AuditCategoryFilingTests.MethodBody(
            File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "EmergencyDisableService.cs")),
            "public async Task<EmergencyDisableResult> DisableAsync");

        Assert.Contains("LogNotificationGap(target, performedBy, ip, ticket, notification)", body, StringComparison.Ordinal);

        // And it is gated on the outcome, not written unconditionally.
        Assert.Contains("if (notification != SecurityNotificationOutcome.Sent)", body, StringComparison.Ordinal);
    }

    private void Dispatch(NotificationDispatch result) =>
        _email.SendModuleNotificationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
                Arg.Any<string?>())
            .Returns(Task.FromResult(result));

    private static Task<SecurityNotificationOutcome> Notify(EmergencyDisableService service) =>
        service.SendSecurityNotificationAsync(
            new ResolvedDirectoryPrincipal(
                Source: "Test",
                DisplayName: "User",
                UserPrincipalName: "user@contoso.com",
                SamAccountName: "user",
                PrimarySmtpAddress: "user@contoso.com",
                DistinguishedName: "CN=User,OU=Users,DC=contoso,DC=com",
                ObjectGuid: Guid.NewGuid().ToString(),
                EntraObjectId: Guid.NewGuid().ToString()),
            performedBy: "admin@contoso.com",
            ip: "10.0.0.1",
            ticket: "INC0001",
            success: true,
            steps: [],
            lockdownRequested: false,
            lockdownOutcome: LockdownOutcome.NotRequested,
            lockdownStampOutcome: LockdownStampOutcome.NotAttempted);

    private EmergencyDisableService CreateService(string? notifySecurityTeam)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:LogRoot"] = _tempDir,
                ["Audit:RotationPeriod"] = "daily",
                ["OperationTrace:Enabled"] = "true",
                ["Delinea:SecretServerUrl"] = "https://fake.local",
                // Deliberately set, and deliberately NOT the expected recipient: if the fix
                // regresses, this is where the mail goes.
                ["Email:AdminNotificationEmail"] = AdminList,
            })
            .Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(
            catalog, env, TestConfigStore.CreateModuleConfig(_tempDir),
            Substitute.For<ILogger<ModuleConfigService>>());

        if (notifySecurityTeam != null)
        {
            moduleConfig.SaveModuleConfig("EmergencyDisable", new Dictionary<string, string>
            {
                ["NotifySecurityTeam"] = notifySecurityTeam,
            });
        }

        var jsonlLog = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        var operationTrace = new OperationTraceService(config, jsonlLog);
        var audit = new AuditService(jsonlLog, operationTrace);

        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        var extendedLog = new ExtendedLogService(
            config, env, TestConfigStore.CreateAppSettings(_tempDir),
            Substitute.For<ILogger<ExtendedLogService>>());
        var delinea = new DelineaService(
            httpClientFactory, config, Substitute.For<ILogger<DelineaService>>(), extendedLog, operationTrace);
        var moduleCredentials = new ModuleCredentialService(
            moduleConfig, delinea, Substitute.For<ILogger<ModuleCredentialService>>());
        var protectedPrincipalService = new ProtectedPrincipalService(
            env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea,
            Substitute.For<ILogger<ProtectedPrincipalService>>());

        var sectionAccessRepo = new Services.Storage.SectionAccessRepository(TestConfigStore.Create(_tempDir));
        var sectionAccess = new SectionAccessService(
            config, Substitute.For<ILogger<SectionAccessService>>(), env, catalog, sectionAccessRepo);
        var servicers = new ProtectedPrincipalServicerService(
            sectionAccess, Substitute.For<ILogger<ProtectedPrincipalServicerService>>());

        return new EmergencyDisableService(
            moduleCredentials,
            moduleConfig,
            protectedPrincipalService,
            servicers,
            operationTrace,
            audit,
            _email,
            delinea,
            httpClientFactory,
            env,
            config,
            Substitute.For<ILogger<EmergencyDisableService>>());
    }
}
