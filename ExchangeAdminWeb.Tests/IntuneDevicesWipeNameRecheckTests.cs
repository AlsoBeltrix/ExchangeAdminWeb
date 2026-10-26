using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-4. A wipe's typed device-name confirmation is re-checked by the handler, not only by the
/// confirm button's disabled expression: a stale render, a second dispatch or a programmatic click
/// must not wipe a device whose name the operator did not type.
/// </summary>
/// <remarks>
/// ExecuteActionAsync is driven itself - the compiled page's handler, invoked by reflection with its
/// injected services set, the AttributeEditor and Comms10k page-handler pattern; no renderer is
/// needed because the handler never renders. The device has no primary user, so the
/// protected-principal check has nothing to resolve (T4 / AC10) and the typed name is the only gate
/// left between the confirm and the Graph write. The stand-in Graph client factory counts the write's
/// attempts and then hands back no client, so a write that is reached fails closed with "credentials
/// not available" and nothing leaves the process.
/// </remarks>
public sealed class IntuneDevicesWipeNameRecheckTests : IDisposable
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string Operator = "CONTOSO\\operator";
    private const string Ticket = "INC0042";

    private readonly TempDir _temp = new();
    private readonly IConfiguration _config;
    private readonly AuditService _audit;
    private readonly ActivityProgressService _progress = new();
    private int _graphWrites;

    public IntuneDevicesWipeNameRecheckTests()
    {
        _config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _temp.Path,
        }).Build();
        var log = new JsonlLogService(_config, NullLogger<JsonlLogService>.Instance);
        _audit = new AuditService(log, new OperationTraceService(_config, log));
    }

    public void Dispose()
    {
        _progress.Dispose();
        _temp.Dispose();
    }

    private static IntuneDevice Laptop() => new()
    {
        Id = "0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0",
        DeviceName = "LAPTOP-FIN-0042",
        UserPrincipalName = "",
    };

    /// <summary>The page with its confirm bar open for <paramref name="action"/> on the device, as BeginAction leaves it, then the operator's typing.</summary>
    private IntuneDevices PageConfirming(IntuneDevice device, IntuneDeviceAction action, string typedName)
    {
        var page = new IntuneDevices();
        var authState = Substitute.For<AuthenticationStateProvider>();
        authState.GetAuthenticationStateAsync().Returns(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, Operator)], "Negotiate"))));
        var authorization = Substitute.For<IAuthorizationService>();
        authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(Task.FromResult(AuthorizationResult.Success()));
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(new HttpClient());

        SetProperty(page, "IntuneDeviceService", new IntuneDeviceService(() =>
        {
            Interlocked.Increment(ref _graphWrites);
            return Task.FromResult<GraphTokenClient?>(null);
        }));
        SetProperty(page, "Audit", _audit);
        SetProperty(page, "AuthStateProvider", authState);
        SetProperty(page, "AuthorizationService", authorization);
        // ServiceNow:Enabled is unset, so the integration is dormant and every ticket validates.
        SetProperty(page, "ServiceNow", new ServiceNowService(_config, NullLogger<ServiceNowService>.Instance, http));
        SetProperty(page, "Logger", NullLogger<IntuneDevices>.Instance);
        SetProperty(page, "Progress", _progress);
        SetField(page, "currentUser", Operator);
        SetField(page, "clientIpAddress", "10.0.0.5");

        typeof(IntuneDevices).GetMethod("BeginAction", PrivateInstance)!.Invoke(page, [device, action]);
        SetField(page, "actionTicket", Ticket);
        SetField(page, "wipeConfirmName", typedName);
        return page;
    }

    private static void SetProperty(IntuneDevices page, string name, object value) =>
        typeof(IntuneDevices).GetProperty(name, PrivateInstance)!.SetValue(page, value);

    private static void SetField(IntuneDevices page, string name, object? value) =>
        typeof(IntuneDevices).GetField(name, PrivateInstance)!.SetValue(page, value);

    private static Task Execute(IntuneDevices page, IntuneDevice device, IntuneDeviceAction action) =>
        (Task)typeof(IntuneDevices).GetMethod("ExecuteActionAsync", PrivateInstance)!.Invoke(page, [device, action])!;

    /// <summary>The verdict the page shows for the device.</summary>
    private static (bool Success, string Message) Shown(IntuneDevices page, string deviceId)
    {
        var outcomes = (System.Collections.IDictionary)typeof(IntuneDevices)
            .GetField("deviceOutcomes", PrivateInstance)!.GetValue(page)!;
        var outcome = outcomes[deviceId]!;
        return ((bool)outcome.GetType().GetProperty("Success")!.GetValue(outcome)!,
            (string)outcome.GetType().GetProperty("Message")!.GetValue(outcome)!);
    }

    private List<JsonElement> AuditRows()
    {
        var folder = Path.Combine(_temp.Path, "ExchangeAdminWeb");
        if (!Directory.Exists(folder))
            return [];

        return Directory.GetFiles(folder, "exchangeadmin_*.jsonl")
            .Where(file => !Path.GetFileNameWithoutExtension(file).EndsWith("_trace", StringComparison.OrdinalIgnoreCase))
            .SelectMany(File.ReadAllLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(row => row.TryGetProperty("eventType", out var type) && type.GetString() == "audit")
            .ToList();
    }

    [Theory]
    [InlineData("LAPTOP-FIN-0043")] // another device's name - the confirm bar moved, or the click raced the re-render
    [InlineData("LAPTOP-FIN-004")]  // a keystroke short
    [InlineData("")]                // nothing typed at all - a programmatic click
    public async Task AWipeWhoseTypedNameDoesNotMatchTheDevice_IsRefusedAndAudited_AndNeverReachesGraph(string typedName)
    {
        var device = Laptop();
        var page = PageConfirming(device, IntuneDeviceAction.Wipe, typedName);

        await Execute(page, device, IntuneDeviceAction.Wipe);

        Assert.Equal(0, _graphWrites);
        var row = Assert.Single(AuditRows());
        Assert.Equal("IntuneDevices_Wipe", row.GetProperty("action").GetString());
        Assert.Equal("IntuneDevices", row.GetProperty("category").GetString());
        Assert.Equal("Failed", row.GetProperty("result").GetString());
        Assert.Equal($"LAPTOP-FIN-0042 ({device.Id})", row.GetProperty("target").GetString());
        Assert.Equal(Ticket, row.GetProperty("ticket").GetString());
        Assert.Contains("Typed device name does not match", row.GetProperty("error").GetString(), StringComparison.Ordinal);

        var (success, message) = Shown(page, device.Id);
        Assert.False(success);
        Assert.Contains("does not match", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWipeWhoseTypedNameMatches_ReachesTheWrite()
    {
        // The control for the test above: the same fixture, the name typed as the button accepts it
        // (any case, surrounding spaces), and the write IS attempted - so a zero above is the name
        // check refusing, not a fixture that never reaches Graph.
        var device = Laptop();
        var page = PageConfirming(device, IntuneDeviceAction.Wipe, " laptop-fin-0042 ");

        await Execute(page, device, IntuneDeviceAction.Wipe);

        Assert.Equal(1, _graphWrites);
        var row = Assert.Single(AuditRows());
        Assert.Equal("IntuneDevices_Wipe", row.GetProperty("action").GetString());
        Assert.DoesNotContain("does not match", row.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARetireAsksForNoTypedName()
    {
        // The second key is the wipe's alone: retire is recoverable by re-enrolling.
        var device = Laptop();
        var page = PageConfirming(device, IntuneDeviceAction.Retire, "");

        await Execute(page, device, IntuneDeviceAction.Retire);

        Assert.Equal(1, _graphWrites);
        Assert.Equal("IntuneDevices_Retire", Assert.Single(AuditRows()).GetProperty("action").GetString());
    }
}
