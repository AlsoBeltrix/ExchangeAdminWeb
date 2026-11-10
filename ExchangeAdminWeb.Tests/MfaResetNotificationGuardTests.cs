using System.Reflection;
using System.Security.Claims;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// rev-d2. MFA Reset's reset handler, driven on the page with its services stood in: the administrator notification at the
/// end of the handler is held open, and a second click arrives while it is. sweep-e item 42's guard tested isLoading, which
/// the handler lowers before that notification, so the second click was admitted and ran the reset path again for the
/// listed user. The handler is now guarded by a flag lowered only when it has finished.
/// </summary>
public sealed class MfaResetNotificationGuardTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly TempDir _temp = new();

    public void Dispose() => _temp.Dispose();

    /// <summary>The protection resolution, counted: each reset attempt that got past its gates asks it once.</summary>
    private sealed class CountingProtection(IWebHostEnvironment env, IConfiguration config, ModuleConfigService moduleConfig,
        ProtectedPrincipalRepository repository, DelineaService delinea)
        : ProtectedPrincipalService(env, config, moduleConfig, repository, delinea, NullLogger<ProtectedPrincipalService>.Instance)
    {
        public int Resolutions;

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status, bool unresolved)> ResolveCloudUserAsync(
            string? entraObjectId, string userPrincipalName)
        {
            Interlocked.Increment(ref Resolutions);
            // Unavailable refuses the reset: no Graph call is needed for the attempt to count, and the notification still goes.
            return Task.FromResult<(ResolvedDirectoryPrincipal?, ResolutionStatus, bool)>((null, ResolutionStatus.Unavailable, false));
        }
    }

    private static void Inject(object page, string property, object value) =>
        (typeof(MfaReset).GetProperty(property, PrivateInstance)
            ?? throw new InvalidOperationException($"MfaReset.{property} not found - this test is stale")).SetValue(page, value);

    private static void Set(object page, string field, object? value) =>
        (typeof(MfaReset).GetField(field, PrivateInstance)
            ?? throw new InvalidOperationException($"MfaReset.{field} not found - this test is stale")).SetValue(page, value);

    private static Task Click(object page) => (Task)typeof(MfaReset).GetMethod("ExecuteReset", PrivateInstance)!.Invoke(page, null)!;

    [Fact]
    public async Task ASecondClickWhileTheNotificationSends_IsIgnored_AndTheResetPathRunsOnce()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = _temp.Path,
            ["Audit:RotationPeriod"] = "daily",
        }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_temp.Path);
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(_temp.Path), NullLogger<ModuleConfigService>.Instance);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_temp.Path), NullLogger<ExtendedLogService>.Instance);
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(config, log);
        var delinea = new DelineaService(http, config, NullLogger<DelineaService>.Instance, extLog, trace);
        var protection = new CountingProtection(env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_temp.Path), delinea);

        // The administrator notification, held open until the test lets it go.
        var notificationSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var notificationAsked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var email = Substitute.For<EmailService>(config, NullLogger<EmailService>.Instance);
        email.SendAdminNotificationAsync(default!, default!, default!, default, default!, default!, default)
            .ReturnsForAnyArgs(_ =>
            {
                notificationAsked.TrySetResult();
                return notificationSent.Task;
            });

        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "EXAMPLE\\operator")], "TestAuth"));
        var authState = Substitute.For<AuthenticationStateProvider>();
        authState.GetAuthenticationStateAsync().Returns(new AuthenticationState(user));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());

        var page = new MfaReset();
        Inject(page, "Progress", Substitute.For<IActivityProgress>());
        Inject(page, "Audit", new AuditService(log, trace));
        Inject(page, "Email", email);
        Inject(page, "ServiceNow", new ServiceNowService(config, NullLogger<ServiceNowService>.Instance, http));
        Inject(page, "AuthStateProvider", authState);
        Inject(page, "AuthorizationService", authorization);
        Inject(page, "Logger", NullLogger<MfaReset>.Instance);
        Inject(page, "ProtectedPrincipalService", protection);
        Set(page, "listedUpn", "alex.morgan@example.test");
        Set(page, "ticketNumber", "INC0010001");
        Set(page, "resetConfirmPending", true);

        var first = Click(page);
        await notificationAsked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // The first click is suspended at the notification, the page usable again; the second click the browser queued.
        Assert.False((bool)typeof(MfaReset).GetField("isLoading", PrivateInstance)!.GetValue(page)!);
        var second = Click(page);

        Assert.True(second.IsCompletedSuccessfully, "the second click ran on while the first was still sending its notification");
        notificationSent.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, protection.Resolutions);
        await email.ReceivedWithAnyArgs(1).SendAdminNotificationAsync(default!, default!, default!, default, default!, default!, default);
        Assert.False((bool)typeof(MfaReset).GetField("resetInProgress", PrivateInstance)!.GetValue(page)!);
    }
}
