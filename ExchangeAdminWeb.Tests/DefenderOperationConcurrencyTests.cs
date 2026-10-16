using System.Globalization;
using System.Net;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CsvHelper;
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
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.DefenderEndpointDevices;

namespace ExchangeAdminWeb.Tests;

// Dispatch the real rendered event, then invoke stale events directly on the same dispatcher.
#pragma warning disable BL0006
public sealed class DefenderOperationConcurrencyTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    [Theory]
    [InlineData("Load", "Load", false)]
    [InlineData("Load", "Download", false)]
    [InlineData("Download", "Load", false)]
    [InlineData("Download", "Download", false)]
    [InlineData("Load", "Load", true)]
    [InlineData("Load", "Download", true)]
    [InlineData("Download", "Load", true)]
    [InlineData("Download", "Download", true)]
    public async Task OverlapIsRejectedWithoutReleasingTheOwnerAndRetryWorks(string owner, string rejected, bool failOwner)
    {
        using var fixture = new Fixture();
        await fixture.RenderAsync();
        fixture.Form("seed");
        await fixture.OnDispatcher(() => fixture.Start("Load"));
        await fixture.DrainAsync();
        var original = fixture.Loaded();
        var originalAudit = Field<object>(fixture.Page, "appliedFilterAudit");
        fixture.ResetProgress();
        fixture.Form("owner");
        var block = new OperationBlock(owner, failOwner);
        fixture.Block = block;

        try
        {
            await fixture.OnDispatcher(() =>
            {
                fixture.Start(owner);
                // Both handlers have yielded, but their continuation cannot run on this stack.
                Assert.True(fixture.OwnsFlag(owner));
                Assert.True(fixture.Reenter(rejected).IsCompletedSuccessfully);
                Assert.True(fixture.OwnsFlag(owner));
            });
            await block.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

            await fixture.OnDispatcher(() =>
            {
                fixture.Form("edited while busy");
                Assert.True(fixture.Reenter(rejected).IsCompletedSuccessfully);
                Assert.True(fixture.Reenter(rejected).IsCompletedSuccessfully);
                Assert.True(fixture.OwnsFlag(owner));
                Assert.Equal(owner == "Load" ? 2 : 1, fixture.Reads);
                Assert.Equal(owner == "Download" ? 1 : 0, fixture.Js.Attempts);
                Assert.Empty(fixture.Js.Delivered);
                Assert.Same(original, fixture.Loaded());
                Assert.Same(originalAudit, Field<object>(fixture.Page, "appliedFilterAudit"));
                Assert.Single(fixture.Audits());
                var handle = Assert.Single(fixture.Handles);
                Assert.DoesNotContain(handle.ReceivedCalls(), call => call.GetMethodInfo().Name is "Complete" or "Dispose");
            });
            Assert.True(await fixture.ButtonDisabledAsync("Search"));
            Assert.True(await fixture.ButtonDisabledAsync("Download CSV"));
        }
        finally
        {
            block.Release.TrySetResult();
            await fixture.DrainAsync();
        }

        Assert.False(Field<bool>(fixture.Page, "isLoading"));
        Assert.False(Field<bool>(fixture.Page, "isDownloadingCsv"));
        Assert.False(await fixture.ButtonDisabledAsync("Search"));
        var ownerAudit = fixture.Audits()[1];
        Assert.Equal(2, fixture.Audits().Length);
        Assert.Equal(owner == "Load" ? "DefenderEndpointDevices_List" : "ExportCsv", ownerAudit.GetProperty("action").GetString());
        Assert.Equal(failOwner ? "Failed" : "Success", ownerAudit.GetProperty("result").GetString());
        Assert.Contains("computerDnsName^=" + (owner == "Load" ? "owner" : "seed"), ownerAudit.GetProperty("RequestedFilters").GetString());
        Assert.DoesNotContain("edited while busy", ownerAudit.GetRawText());
        var ownerHandle = Assert.Single(fixture.Handles);
        Assert.Single(ownerHandle.ReceivedCalls(), call => call.GetMethodInfo().Name == "Dispose");
        Assert.Equal(failOwner ? 0 : 1, ownerHandle.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "Complete"));
        if (owner == "Load")
        {
            if (failOwner)
            {
                Assert.Null(Field<DefenderDeviceListResult?>(fixture.Page, "result"));
                Assert.Null(Field<object?>(fixture.Page, "appliedFilterAudit"));
                Assert.Equal("Inventory failed", Field<string?>(fixture.Page, "loadError"));
            }
            else
            {
                Assert.Equal("row-2", Assert.Single(fixture.Loaded().Devices).Id);
                Assert.Null(Field<string?>(fixture.Page, "loadError"));
                Assert.Equal(1, ownerAudit.GetProperty("Devices").GetInt32());
            }
        }
        else
        {
            Assert.Same(original, fixture.Loaded());
            Assert.Same(originalAudit, Field<object>(fixture.Page, "appliedFilterAudit"));
            Assert.Equal(failOwner ? "CSV export could not be completed. Try Download CSV again." : null,
                Field<string?>(fixture.Page, "csvExportError"));
            Assert.Equal(failOwner ? 0 : 1, fixture.Js.Delivered.Count);
            if (!failOwner) Assert.Equal(["row-1"], fixture.Js.ExportedIds());
        }

        fixture.Block = null;
        fixture.Form("retry");
        await fixture.OnDispatcher(() => fixture.Start(owner));
        await fixture.DrainAsync();

        Assert.False(Field<bool>(fixture.Page, "isLoading"));
        Assert.False(Field<bool>(fixture.Page, "isDownloadingCsv"));
        Assert.False(await fixture.ButtonDisabledAsync("Search"));
        Assert.False(await fixture.ButtonDisabledAsync("Download CSV"));
        var retryAudit = fixture.Audits()[2];
        Assert.Equal(3, fixture.Audits().Length);
        Assert.Equal("Success", retryAudit.GetProperty("result").GetString());
        Assert.Contains("computerDnsName^=" + (owner == "Load" ? "retry" : "seed"), retryAudit.GetProperty("RequestedFilters").GetString());
        Assert.Null(Field<string?>(fixture.Page, "loadError"));
        Assert.Null(Field<string?>(fixture.Page, "csvExportError"));
        Assert.Equal(2, fixture.Handles.Count);
        Assert.Single(fixture.Handles[1].ReceivedCalls(), call => call.GetMethodInfo().Name == "Complete");
        Assert.Single(fixture.Handles[1].ReceivedCalls(), call => call.GetMethodInfo().Name == "Dispose");
        if (owner == "Load")
        {
            Assert.Equal(3, fixture.Reads);
            Assert.Equal("row-3", Assert.Single(fixture.Loaded().Devices).Id);
            Assert.Equal(0, fixture.Js.Attempts);
        }
        else
        {
            Assert.Equal(1, fixture.Reads);
            Assert.Equal(2, fixture.Js.Attempts);
            Assert.Equal(failOwner ? 1 : 2, fixture.Js.Delivered.Count);
            Assert.Equal(["row-1"], fixture.Js.ExportedIds());
        }
    }

    private static T Field<T>(Page page, string name) => (T)typeof(Page).GetField(name, PrivateInstance)!.GetValue(page)!;

    private sealed class OperationBlock(string operation, bool fail)
    {
        public string Operation { get; } = operation;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync()
        {
            Entered.TrySetResult();
            await Release.Task;
            if (fail)
            {
                if (Operation == "Load") throw new InvalidOperationException("Inventory failed");
                throw new JSException("Transfer failed");
            }
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "defender-overlap-" + Guid.NewGuid().ToString("N"));
        private readonly HttpClient http;
        private readonly PageRenderer renderer;
        private readonly int componentId;
        private readonly List<Task> pending = [];
        public Page Page { get; } = new();
        public RecordingJs Js { get; } = new();
        public IActivityProgress Progress { get; } = Substitute.For<IActivityProgress>();
        public List<IActivityHandle> Handles { get; } = [];
        public int Reads { get; private set; }
        public OperationBlock? Block { get; set; }

        public Fixture()
        {
            Directory.CreateDirectory(root);
            http = new HttpClient(new InventoryHandler(() => Reads));
            var client = new DefenderApiClient("tenant", "client", "secret", DefenderApiClient.DefenderBaseUrl, DefenderApiClient.DefenderTokenScope, http);
            var service = new DefenderEndpointDeviceService(async () =>
            {
                Reads++;
                if (Block is { Operation: "Load" } block) await block.WaitAsync();
                return client;
            }, includeDiscoverySourcesOverride: "false");
            Js.BeforeTransfer = async () =>
            {
                if (Block is { Operation: "Download" } block) await block.WaitAsync();
            };
            var store = Substitute.For<IConfigStore>();
            store.Read(Arg.Any<Func<SqliteConnection, Dictionary<string, string>>>())
                .Returns(_ => new Dictionary<string, string> { ["GraphDelineaSecretId"] = "1" });
            var env = Substitute.For<IWebHostEnvironment>();
            env.ContentRootPath.Returns(root);
            var moduleConfig = new ModuleConfigService(new ModuleCatalog(), env, new ModuleConfigRepository(store), NullLogger<ModuleConfigService>.Instance);
            typeof(DefenderEndpointDeviceService).GetField("_moduleConfig", PrivateInstance)!.SetValue(service, moduleConfig);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Audit:LogRoot"] = root }).Build();
            var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
            var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "EXAMPLE\\operator")], "test"));
            var auth = Substitute.For<AuthenticationStateProvider>();
            auth.GetAuthenticationStateAsync().Returns(new AuthenticationState(principal));
            var authorization = Substitute.For<IAuthorizationService>();
            authorization.AuthorizeAsync(principal, Arg.Any<object?>(), "DefenderEndpointDevices").Returns(AuthorizationResult.Success());
            Progress.Begin(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ActivitySize>()).Returns(_ =>
            {
                var handle = Substitute.For<IActivityHandle>();
                Handles.Add(handle);
                return handle;
            });
            var navigation = new TestNavigation();
            Set("DeviceService", service);
            Set("Audit", new AuditService(log, new OperationTraceService(config, log)));
            Set("AuthStateProvider", auth);
            Set("AuthorizationService", authorization);
            Set("Navigation", navigation);
            Set("ClientInfo", new ClientInfoService { IpAddress = "192.0.2.5" });
            Set("Logger", NullLogger<Page>.Instance);
            Set("JS", Js);
            Set("Progress", Progress);
            renderer = new PageRenderer(new ServiceCollection().AddSingleton<NavigationManager>(navigation).AddSingleton<ModuleCatalog>().BuildServiceProvider());
            componentId = renderer.Dispatcher.InvokeAsync(() => renderer.Attach(Page)).GetAwaiter().GetResult();
        }

        private void Set(string property, object value) => typeof(Page).GetProperty(property, PrivateInstance)!.SetValue(Page, value);
        public void Form(string value)
        {
            var form = Field<object>(Page, "form");
            form.GetType().GetProperty("DeviceNameStartsWith")!.SetValue(form, value);
        }
        public DefenderDeviceListResult Loaded() => Field<DefenderDeviceListResult>(Page, "result");
        public bool OwnsFlag(string operation) => Field<bool>(Page, operation == "Load" ? "isLoading" : "isDownloadingCsv");
        public Task RenderAsync() => renderer.Dispatcher.InvokeAsync(() => renderer.RenderAsync(componentId));
        public Task OnDispatcher(Action action) => renderer.Dispatcher.InvokeAsync(action);
        public void ResetProgress()
        {
            Handles.Clear();
            Progress.ClearReceivedCalls();
        }
        public void Start(string operation)
        {
            var button = Button(operation == "Load" ? "Search" : "Download CSV");
            var handler = button.Single(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "onclick");
            pending.Add(renderer.DispatchEventAsync(handler.AttributeEventHandlerId, null, new MouseEventArgs()));
        }
        public Task Reenter(string operation)
        {
            var task = (Task)typeof(Page).GetMethod(operation == "Load" ? "LoadAsync" : "DownloadCsvAsync", PrivateInstance)!.Invoke(Page, null)!;
            pending.Add(task);
            return task;
        }
        public async Task DrainAsync()
        {
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            pending.Clear();
        }
        public Task<bool> ButtonDisabledAsync(string text) => renderer.Dispatcher.InvokeAsync(() =>
            Button(text).Any(frame => frame.FrameType == RenderTreeFrameType.Attribute && frame.AttributeName == "disabled" && Equals(frame.AttributeValue, true)));
        private RenderTreeFrame[] Button(string text)
        {
            var frames = renderer.Frames(componentId);
            return Assert.Single(frames.Select((frame, index) => (frame, index))
                .Where(pair => pair.frame.FrameType == RenderTreeFrameType.Element && pair.frame.ElementName == "button")
                .Select(pair => frames.Skip(pair.index).Take(pair.frame.ElementSubtreeLength).ToArray()),
                element => element.Any(frame => frame.FrameType switch
                {
                    RenderTreeFrameType.Text => frame.TextContent.Contains(text, StringComparison.Ordinal),
                    RenderTreeFrameType.Markup => frame.MarkupContent.Contains(text, StringComparison.Ordinal),
                    _ => false
                }));
        }
        public JsonElement[] Audits() => Directory.GetFiles(root, "exchangeadmin_*.jsonl", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith("_trace", StringComparison.Ordinal))
            .SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        public void Dispose()
        {
            Block?.Release.TrySetResult();
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

    private sealed class InventoryHandler(Func<int> attempt) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(request.RequestUri!.Host == "login.microsoftonline.com"
                    ? """{"access_token":"local-test-token","expires_in":3600}"""
                    : $$"""{"value":[{"id":"row-{{attempt()}}","computerDnsName":"found.example.test","onboardingStatus":"CanBeOnboarded"}]}""")
            });
    }

    private sealed class RecordingJs : IJSRuntime
    {
        public Func<Task> BeforeTransfer { get; set; } = () => Task.CompletedTask;
        public int Attempts { get; private set; }
        public List<string> Delivered { get; } = [];
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, CancellationToken.None, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("downloadFile", identifier);
            Attempts++;
            await BeforeTransfer();
            Assert.Equal("text/csv", args![1]);
            Delivered.Add(Encoding.UTF8.GetString(Convert.FromBase64String((string)args[2]!)));
            return default!;
        }
        public string[] ExportedIds()
        {
            using var reader = new StringReader(Delivered[^1]);
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            Assert.True(csv.Read());
            csv.ReadHeader();
            var ids = new List<string>();
            while (csv.Read()) ids.Add(csv.GetField("DeviceId")!);
            return ids.ToArray();
        }
    }
}
#pragma warning restore BL0006
