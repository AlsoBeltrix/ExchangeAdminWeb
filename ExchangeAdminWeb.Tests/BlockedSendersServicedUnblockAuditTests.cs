using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.BlockedSenders;

namespace ExchangeAdminWeb.Tests;

#pragma warning disable BL0006 // the minimal handler renderer below, the EventLogUndoRefusalTests pattern

/// <summary>
/// kd-10. An authorised servicer's unblock of a protected principal carries the servicer note on its
/// audit row whatever the outcome (the pgwt-7 rule, <see cref="ExchangeAdminWeb.Models.PermissionResult.WithServicedNote"/>):
/// a failed one dropped it, so the row could not say who had been authorised to attempt the change.
/// </summary>
/// <remarks>
/// The compiled page's ConfirmUnblock, driven by reflection with its services set. The note is the real
/// <see cref="BlockedSenderProtectionGate"/>'s own: a scripted protected-principal service reports the
/// target protected, and the section-access store grants the operator's group the module's servicer key,
/// so the gate allows as a servicer. The unblock itself runs on the real service and pool over the stub
/// cmdlets (<see cref="MigrationStubExchange"/>), once failing and once succeeding. No Exchange, no directory.
/// </remarks>
public sealed class BlockedSendersServicedUnblockAuditTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Sender = "ceo@fabrikam.test";
    private const string ServicerGroupSid = "S-1-5-21-1-2-3-4001";

    private readonly MigrationStubExchange _exchange = new();
    private readonly HandlerRenderer _renderer = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "eaw-kd10-" + Guid.NewGuid().ToString("N"));

    public BlockedSendersServicedUnblockAuditTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        _renderer.Dispose();
        _exchange.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A render handle for StateHasChanged, which a successful unblock reaches through its list refresh. The
    /// page renders nothing here - its markup needs services the handler does not.
    /// </summary>
    private sealed class QuietPage : Page
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
        }
    }

    private sealed class HandlerRenderer() : Renderer(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public void Attach(IComponent component) => AssignRootComponentId(component);

        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;

        protected override void HandleException(Exception exception) => throw exception;
    }

    private Task ConfirmUnblock(Page page) =>
        _renderer.Dispatcher.InvokeAsync(() =>
            (Task)typeof(Page).GetMethod("ConfirmUnblock", PrivateInstance)!.Invoke(page, null)!);

    /// <summary>Reports every target protected by one user rule, without a directory.</summary>
    private sealed class ProtectedTarget(IWebHostEnvironment environment, IConfiguration config, ModuleConfigService moduleConfig,
        ExchangeAdminWeb.Services.Storage.ProtectedPrincipalRepository repository, DelineaService delinea)
        : ProtectedPrincipalService(environment, config, moduleConfig, repository, delinea, NullLogger<ProtectedPrincipalService>.Instance)
    {
        public override bool HasCentralConfig => true;

        // A rule must exist, or the gate allows without resolving and no servicer note is ever made.
        public override (ProtectedPrincipalConfig? config, string[] legacyExclusions, string? error) LoadEffectiveConfig() =>
            (new ProtectedPrincipalConfig { Users = [Sender] }, [], null);

        public override Task<(ResolvedDirectoryPrincipal? principal, ResolutionStatus status)> ResolveWithExchangeFallbackAsync(string identity) =>
            Task.FromResult<(ResolvedDirectoryPrincipal?, ResolutionStatus)>((
                new ResolvedDirectoryPrincipal("Test", identity, identity, "ceo", identity, "CN=ceo,DC=contoso,DC=test", "guid", null),
                ResolutionStatus.Resolved));

        public override Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target) =>
            Task.FromResult(ProtectedPrincipalResult.Protected("protected", $"User:{Sender}"));
    }

    private BlockedSenderProtectionGate GateThatAllowsTheServicer()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://secrets.invalid",
            ["Audit:LogRoot"] = _directory,
        }).Build();
        var environment = _exchange.Environment;
        var moduleConfig = new ModuleConfigService(new ModuleCatalog(), environment,
            TestConfigStore.CreateModuleConfig(_directory), NullLogger<ModuleConfigService>.Instance);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var delinea = new DelineaService(http, config, NullLogger<DelineaService>.Instance,
            new ExtendedLogService(config, environment, TestConfigStore.CreateAppSettings(_directory), NullLogger<ExtendedLogService>.Instance),
            new OperationTraceService(config, log));

        var sectionAccess = new SectionAccessService(config, NullLogger<SectionAccessService>.Instance, environment,
            new ModuleCatalog(), new ExchangeAdminWeb.Services.Storage.SectionAccessRepository(TestConfigStore.Create(_directory)));
        sectionAccess.SaveSectionAccess(new Dictionary<string, string[]>
        {
            [ProtectedPrincipalServicerService.SectionKeyFor(BlockedSenderProtectionGate.ModuleId)] = [ServicerGroupSid],
        });

        return new BlockedSenderProtectionGate(
            new ProtectedTarget(environment, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_directory), delinea),
            new ProtectedPrincipalServicerService(sectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance),
            NullLogger<BlockedSenderProtectionGate>.Instance);
    }

    /// <summary>The page with its confirm row open for <see cref="Sender"/>, the operator a member of the servicer group.</summary>
    private Page ServicerUnblockPage(ActivityProgressService progress)
    {
        var page = new QuietPage();
        _renderer.Dispatcher.InvokeAsync(() => _renderer.Attach(page)).GetAwaiter().GetResult();
        var servicer = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "CONTOSO\\execsupport"), new Claim(ClaimTypes.Role, ServicerGroupSid)],
            "Negotiate", ClaimTypes.Name, ClaimTypes.Role));
        var auth = Substitute.For<AuthenticationStateProvider>();
        auth.GetAuthenticationStateAsync().Returns(new AuthenticationState(servicer));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(Task.FromResult(AuthorizationResult.Success()));

        Set(page, "BlockedSenderSvc", new BlockedSenderService(_exchange.Pool(), null!, NullLogger<BlockedSenderService>.Instance, _exchange.Trace));
        Set(page, "Progress", progress);
        Set(page, "ProtectionGate", GateThatAllowsTheServicer());
        Set(page, "Audit", _exchange.Audit);
        Set(page, "AuthStateProvider", auth);
        Set(page, "AuthorizationService", authorization);
        Set(page, "OperationTrace", _exchange.Trace);
        Set(page, "Logger", NullLogger<Page>.Instance);
        // Email stays unset: the page's admin notification is caught and logged when it cannot be sent.
        SetField(page, "currentUser", "CONTOSO\\execsupport");
        SetField(page, "clientIpAddress", "10.0.0.5");
        SetField(page, "confirmAddress", Sender);
        SetField(page, "ticketNumber", "INC0001");
        return page;
    }

    private static void Set(Page page, string property, object? value) =>
        typeof(Page).GetProperty(property, PrivateInstance)!.SetValue(page, value);

    private static void SetField(Page page, string field, object? value) =>
        typeof(Page).GetField(field, PrivateInstance)!.SetValue(page, value);

    private JsonElement TheUnblockRow() =>
        Assert.Single(_exchange.AuditEvents(), row => row.GetProperty("action").GetString() == "UnblockSender");

    private static void AssertCarriesTheServicerNote(JsonElement row)
    {
        Assert.True(row.TryGetProperty("protectedPrincipalServiced", out var note), "the row does not say who was authorised");
        var text = note.GetString() ?? "";
        Assert.Contains("authorised by", text, StringComparison.Ordinal);
        Assert.Contains($"User:{Sender}", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedServicedUnblock_KeepsTheServicerNoteOnItsRow()
    {
        _exchange.Failures["Remove-BlockedSenderAddress"] = "the request was throttled";
        using var progress = new ActivityProgressService();
        var page = ServicerUnblockPage(progress);

        await ConfirmUnblock(page);

        var row = TheUnblockRow();
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal(Sender, row.GetProperty("target").GetString());
        Assert.Contains("the request was throttled", row.GetProperty("error").GetString(), StringComparison.Ordinal);
        AssertCarriesTheServicerNote(row);
    }

    [Fact]
    public async Task ASuccessfulServicedUnblock_KeepsTheServicerNoteOnItsRow()
    {
        using var progress = new ActivityProgressService();
        var page = ServicerUnblockPage(progress);

        await ConfirmUnblock(page);

        var row = TheUnblockRow();
        Assert.Equal("Success", row.GetProperty("result").GetString());
        AssertCarriesTheServicerNote(row);
    }
}
#pragma warning restore BL0006
