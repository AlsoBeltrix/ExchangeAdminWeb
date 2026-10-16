using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.DefenderEndpointDevices;

namespace ExchangeAdminWeb.Tests;

// Exercise the compiled page's real lifecycle, render frames and Search event without a circuit.
#pragma warning disable BL0006
public sealed class DefenderSearchLifecycleTests
{
    private const string Ready = "No search has been run.";
    private const string Empty = "No devices matched.";
    private const string Failed = "Could not read the device inventory.";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public async Task FirstAndRepeatedRendersWaitForSearchAndPreserveTheDefaultFilter()
    {
        using var fixture = new Fixture();

        await fixture.RenderAsync();
        Assert.Equal(0, fixture.Reads);
        Assert.Empty(fixture.Handler.Urls);
        Assert.Empty(fixture.Progress.ReceivedCalls());
        Assert.Empty(fixture.Audits());
        Assert.Contains(Ready, await fixture.TextAsync());
        Assert.Equal(DefenderDeviceFilters.CanBeOnboarded, await fixture.InputValueAsync("onboardingStatus"));

        await fixture.RenderAsync();
        Assert.Equal(0, fixture.Reads);
        Assert.Contains(Ready, await fixture.TextAsync());
        Assert.False(await fixture.SearchDisabledAsync());

        await fixture.SearchAsync();

        Assert.Equal(1, fixture.Reads);
        var url = Uri.UnescapeDataString(Assert.Single(fixture.Handler.Urls));
        Assert.Contains("onboardingStatus eq 'CanBeOnboarded'", url);
        Assert.Equal("Success", Assert.Single(fixture.Audits()).GetProperty("result").GetString());
        Assert.Contains("found.example.test", await fixture.TextAsync());
        Assert.DoesNotContain(Ready, await fixture.TextAsync());
        Assert.False(await fixture.SearchDisabledAsync());
    }

    [Fact]
    public async Task DeliberateBlankSearchIsEnabledAndQueriesWithoutFilters()
    {
        using var fixture = new Fixture();
        await fixture.RenderAsync();
        fixture.Form("OnboardingStatus", "");
        await fixture.RenderAsync();
        Assert.Contains(Ready, await fixture.TextAsync());
        Assert.False(await fixture.SearchDisabledAsync());
        Assert.Empty(fixture.Handler.Urls);

        await fixture.SearchAsync();

        Assert.Equal(1, fixture.Reads);
        var url = Uri.UnescapeDataString(Assert.Single(fixture.Handler.Urls));
        Assert.DoesNotContain("$filter", url);
        Assert.Single(fixture.Loaded().Devices);
        Assert.Contains("found.example.test", await fixture.TextAsync());
        Assert.DoesNotContain(Ready, await fixture.TextAsync());
        Assert.Equal("Success", Assert.Single(fixture.Audits()).GetProperty("result").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstSearchDistinguishesReadyPendingAndEmptyOrFailedResults(bool fail)
    {
        using var fixture = new Fixture();
        await fixture.RenderAsync();
        Assert.Contains(Ready, await fixture.TextAsync());
        Assert.DoesNotContain(Empty, await fixture.TextAsync());
        Assert.DoesNotContain(Failed, await fixture.TextAsync());
        fixture.HoldRead = true;
        fixture.FailRead = fail;
        fixture.Handler.Empty = true;

        var search = fixture.SearchAsync();
        await fixture.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(await fixture.SearchDisabledAsync());
        var pending = await fixture.TextAsync();
        Assert.DoesNotContain(Ready, pending);
        Assert.DoesNotContain(Empty, pending);
        Assert.DoesNotContain(Failed, pending);
        Assert.Single(fixture.Progress.ReceivedCalls(), call => call.GetMethodInfo().Name == "Begin");
        fixture.ReleaseRead.TrySetResult();
        await search;

        var settled = await fixture.TextAsync();
        Assert.DoesNotContain(Ready, settled);
        Assert.Contains(fail ? Failed : Empty, settled);
        Assert.DoesNotContain(fail ? Empty : Failed, settled);
        Assert.False(await fixture.SearchDisabledAsync());
        Assert.Equal(1, fixture.Reads);
        var audit = Assert.Single(fixture.Audits());
        Assert.Equal(fail ? "Failed" : "Success", audit.GetProperty("result").GetString());
        if (fail)
        {
            Assert.Null(Field<DefenderDeviceListResult?>(fixture.Page, "result"));
            Assert.Equal("Device inventory unavailable", Field<string?>(fixture.Page, "loadError"));
            Assert.Empty(fixture.Handler.Urls);
        }
        else
        {
            Assert.Equal(DefenderDeviceListOutcome.Complete, fixture.Loaded().Outcome);
            Assert.Empty(fixture.Loaded().Devices);
            Assert.Single(fixture.Handler.Urls);
        }
    }

    private static T Field<T>(Page page, string name) => (T)typeof(Page).GetField(name, PrivateInstance)!.GetValue(page)!;

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "defender-search-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient http;
        private readonly PageRenderer renderer;
        private readonly int componentId;
        public Page Page { get; } = new();
        public InventoryHandler Handler { get; } = new();
        public IActivityProgress Progress { get; } = Substitute.For<IActivityProgress>();
        public int Reads { get; private set; }
        public bool HoldRead { get; set; }
        public bool FailRead { get; set; }
        public TaskCompletionSource ReadEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Fixture()
        {
            Directory.CreateDirectory(root);
            http = new HttpClient(Handler);
            var client = new DefenderApiClient("tenant", "client", "secret", DefenderApiClient.DefenderBaseUrl, DefenderApiClient.DefenderTokenScope, http);
            var service = new DefenderEndpointDeviceService(async () =>
            {
                Reads++;
                if (HoldRead)
                {
                    ReadEntered.TrySetResult();
                    await ReleaseRead.Task;
                }
                if (FailRead) throw new InvalidOperationException("Device inventory unavailable");
                return client;
            }, includeDiscoverySourcesOverride: "false");
            var store = Substitute.For<IConfigStore>();
            store.Read(Arg.Any<Func<SqliteConnection, Dictionary<string, string>>>())
                .Returns(_ => new Dictionary<string, string> { ["GraphDelineaSecretId"] = "1" });
            var env = Substitute.For<IWebHostEnvironment>();
            env.ContentRootPath.Returns(root);
            var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, new ModuleConfigRepository(store), NullLogger<ModuleConfigService>.Instance);
            // The canned-client seam still uses the real configuration gate in compiled markup.
            typeof(DefenderEndpointDeviceService).GetField("_moduleConfig", PrivateInstance)!.SetValue(service, moduleConfig);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Audit:LogRoot"] = root }).Build();
            var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "EXAMPLE\\operator")], "test"));
            var auth = Substitute.For<AuthenticationStateProvider>();
            auth.GetAuthenticationStateAsync().Returns(new AuthenticationState(principal));
            var authorization = Substitute.For<IAuthorizationService>();
            authorization.AuthorizeAsync(principal, Arg.Any<object?>(), "DefenderEndpointDevices").Returns(AuthorizationResult.Success());
            Progress.Begin(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ActivitySize>()).Returns(Substitute.For<IActivityHandle>());
            var navigation = new TestNavigation();
            Set("DeviceService", service);
            Set("Audit", new AuditService(log, new OperationTraceService(config, log)));
            Set("AuthStateProvider", auth);
            Set("AuthorizationService", authorization);
            Set("Navigation", navigation);
            Set("ClientInfo", new ClientInfoService { IpAddress = "192.0.2.5" });
            Set("Logger", NullLogger<Page>.Instance);
            Set("JS", Substitute.For<IJSRuntime>());
            Set("Progress", Progress);
            renderer = new PageRenderer(new ServiceCollection().AddSingleton<NavigationManager>(navigation).AddSingleton<ModuleCatalog>().BuildServiceProvider());
            componentId = renderer.Dispatcher.InvokeAsync(() => renderer.Attach(Page)).GetAwaiter().GetResult();
        }

        private void Set(string property, object value) => typeof(Page).GetProperty(property, PrivateInstance)!.SetValue(Page, value);
        public void Form(string name, object value)
        {
            var form = Field<object>(Page, "form");
            form.GetType().GetProperty(name)!.SetValue(form, value);
        }
        public DefenderDeviceListResult Loaded() => Field<DefenderDeviceListResult>(Page, "result");
        public Task RenderAsync() => renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync(componentId));
        public Task<string> TextAsync() => renderer.Dispatcher.InvokeAsync(() => Text(renderer.Frames(componentId)));
        public Task<string?> InputValueAsync(string id) => renderer.Dispatcher.InvokeAsync(() =>
            Element("select", id: id).Skip(1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
                .Single(frame => frame.AttributeName == "value").AttributeValue?.ToString());
        public Task<bool> SearchDisabledAsync() => renderer.Dispatcher.InvokeAsync(() =>
            Element("button", text: "Search").Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "disabled" && Equals(frame.AttributeValue, true)));
        public Task SearchAsync() => renderer.Dispatcher.InvokeAsync(() =>
        {
            var handler = Element("button", text: "Search").Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onclick");
            return renderer.DispatchEventAsync(handler.AttributeEventHandlerId, null, new MouseEventArgs());
        });
        private RenderTreeFrame[] Element(string name, string? id = null, string? text = null)
        {
            var frames = renderer.Frames(componentId);
            return Assert.Single(frames.Select((frame, index) => (frame, index))
                .Where(pair => pair.frame.FrameType == RenderTreeFrameType.Element && pair.frame.ElementName == name)
                .Select(pair => frames.Skip(pair.index).Take(pair.frame.ElementSubtreeLength).ToArray()),
                element => (id is null || element.Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "id" && Equals(frame.AttributeValue, id)))
                    && (text is null || Text(element).Contains(text, StringComparison.Ordinal)));
        }
        private static string Text(RenderTreeFrame[] frames) => string.Join("\n", frames.Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Text => frame.TextContent,
            RenderTreeFrameType.Markup => frame.MarkupContent,
            _ => ""
        }));
        public JsonElement[] Audits() => Directory.GetFiles(root, "exchangeadmin_*.jsonl", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith("_trace", StringComparison.Ordinal))
            .SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        public void Dispose()
        {
            ReleaseRead.TrySetResult();
            renderer.Dispose();
            http.Dispose();
            Directory.Delete(root, true);
        }
    }

    private sealed class PageRenderer(IServiceProvider services) : Renderer(services, NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public int Attach(IComponent component) => AssignRootComponentId(component);
        public Task RenderAsync(int id) => RenderRootComponentAsync(id);
        public RenderTreeFrame[] Frames(int id)
        {
            var frames = GetCurrentRenderTreeFrames(id);
            return frames.Array.Take(frames.Count).ToArray();
        }
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
    }

    private sealed class TestNavigation : NavigationManager
    {
        public TestNavigation() => Initialize("https://example.test/", "https://example.test/defender-endpoint-devices");
        protected override void NavigateToCore(string uri, bool forceLoad) => throw new InvalidOperationException("Unexpected navigation: " + uri);
    }

    private sealed class InventoryHandler : HttpMessageHandler
    {
        public bool Empty { get; set; }
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return Task.FromResult(Json("""{"access_token":"local-test-token","expires_in":3600}"""));
            Urls.Add(request.RequestUri.ToString());
            return Task.FromResult(Json(Empty ? """{"value":[]}""" : """{"value":[{"id":"m1","computerDnsName":"found.example.test","onboardingStatus":"CanBeOnboarded"}]}"""));
        }
        private static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content) };
    }
}
#pragma warning restore BL0006
