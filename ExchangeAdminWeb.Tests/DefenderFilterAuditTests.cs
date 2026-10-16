using System.Collections;
using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using CsvHelper;
using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.DefenderEndpointDevices;

namespace ExchangeAdminWeb.Tests;

#pragma warning disable BL0006
public sealed class DefenderFilterAuditTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const string SafeExportError = "CSV export could not be completed. Try Download CSV again.";

    [Fact]
    public async Task LookupAuditsTheFiveDisplayedRowsRatherThanTheHundredLoadedCandidates()
    {
        using var fixture = new Fixture(100);
        fixture.Form("Site", "London");
        fixture.Form("SeenBy", "reception");
        fixture.Form("DeviceNameStartsWith", "host");

        await fixture.Invoke("LoadAsync");

        Assert.Equal(5, Devices(fixture.Page).Count);
        var audit = Assert.Single(fixture.Audits());
        Assert.Equal("Success", Property(audit, "result").GetString());
        Assert.Equal("DefenderEndpointDevices_List", Property(audit, "action").GetString());
        Assert.Equal("operator", Property(audit, "user").GetString());
        Assert.Equal("192.0.2.5", Property(audit, "ip").GetString());
        Assert.Equal(5, Property(audit, "Devices").GetInt32());
        Assert.Equal(100, Property(audit, "InventoryDevices").GetInt32());
        Assert.Equal(100, Property(audit, "DistinctDevices").GetInt32());
        Assert.Equal("Evaluated", Property(audit, "RequestFiltersEvaluation").GetString());
        AssertRequested(audit, "London", "reception", "host");
        Assert.Equal(Property(audit, "RequestedFilters").GetString(), Property(audit, "target").GetString());
        AssertEvaluation(audit, "Site", "Evaluated", 100, 0);
        AssertEvaluation(audit, "SeenBy", "Evaluated", 100, 0);
        var completion = Assert.Single(fixture.Handle.ReceivedCalls(), c => c.GetMethodInfo().Name == "Complete");
        Assert.Equal("5 device(s)", completion.GetArguments()[1]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LookupDistinguishesKnownNegativesFromUnavailableLocationValues(bool siteFails)
    {
        using var fixture = new Fixture(6);
        fixture.Handler.SiteFails = siteFails;
        fixture.Handler.ObserverFailures = true;
        fixture.Form("Site", "London");
        fixture.Form("SeenBy", "reception");

        await fixture.Invoke("LoadAsync");
        await fixture.Invoke("DownloadCsvAsync");

        string[] expected = siteFails ? ["0000", "0001", "0003", "0005"] : ["0000", "0005"];
        Assert.Equal(expected, Devices(fixture.Page).Select(d => d.Id));
        Assert.Equal(expected, fixture.Js.ExportedIds());
        var audits = fixture.Audits();
        Assert.Equal(2, audits.Length);
        foreach (var audit in audits)
        {
            AssertRequested(audit, "London", "reception");
            Assert.Equal(expected.Length, Property(audit, "Devices").GetInt32());
            Assert.Equal(6, Property(audit, "InventoryDevices").GetInt32());
            AssertEvaluation(audit, "Site", siteFails ? "Unavailable" : "Evaluated", siteFails ? 0 : 6, siteFails ? 6 : 0);
            AssertEvaluation(audit, "SeenBy", "Degraded", 4, 2);
        }
        Assert.Equal("ExportCsv", Property(audits[1], "action").GetString());
        Assert.Equal(expected.Length + " rows", Property(audits[1], "target").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WholeRunMissingEnrichmentDoesNotClaimTheSeenByFilterWasEvaluated(bool huntingFails)
    {
        using var fixture = new Fixture(6, includeEnrichment: huntingFails);
        fixture.Handler.HuntingFails = huntingFails;
        fixture.Form("Site", "London");
        fixture.Form("SeenBy", "no real observer matches");

        await fixture.Invoke("LoadAsync");
        await fixture.Invoke("DownloadCsvAsync");

        string[] expected = ["0000", "0002", "0004", "0005"];
        Assert.Equal(expected, Devices(fixture.Page).Select(d => d.Id));
        Assert.Equal(expected, fixture.Js.ExportedIds());
        var audits = fixture.Audits();
        Assert.Equal(2, audits.Length);
        foreach (var audit in audits)
        {
            AssertRequested(audit, "London", "no real observer matches");
            Assert.Equal(4, Property(audit, "Devices").GetInt32());
            AssertEvaluation(audit, "Site", "Evaluated", 6, 0);
            AssertEvaluation(audit, "SeenBy", "Unavailable", 0, 6);
        }
    }

    [Fact]
    public async Task AppliedQuestionSurvivesFormEditsAndBindsExportsAcrossAReplacementLoad()
    {
        using var fixture = new Fixture(100);
        fixture.Form("Site", "London");
        fixture.Form("SeenBy", "reception");
        fixture.Form("DeviceNameStartsWith", "host");
        await fixture.Invoke("LoadAsync");
        fixture.Form("Site", "edited before export");
        await fixture.Invoke("DownloadCsvAsync");
        Assert.Equal(5, fixture.Js.ExportedIds().Count);
        AssertRequested(fixture.Audits()[1], "London", "reception", "host");

        // A pending second request must leave the first result and its applied question paired.
        fixture.Form("Site", "Paris");
        fixture.Form("SeenBy", "warehouse");
        fixture.Form("DeviceNameStartsWith", "host0");
        fixture.Handler.HoldInventory = true;
        var loading = fixture.Invoke("LoadAsync");
        await fixture.Handler.InventoryEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            fixture.Form("Site", "edited during load");
            fixture.Form("SeenBy", "edited during load");
            fixture.Form("DeviceNameStartsWith", "edited during load");
            Assert.Equal(5, Devices(fixture.Page).Count);
            await fixture.Invoke("DownloadCsvAsync");
            Assert.Equal(5, fixture.Js.ExportedIds().Count);
            Assert.Equal(2, fixture.Audits().Length);
        }
        finally
        {
            fixture.Handler.ReleaseInventory.TrySetResult();
            await loading;
        }

        Assert.Equal(90, Devices(fixture.Page).Count);
        var secondLookup = fixture.Audits()[2];
        AssertRequested(secondLookup, "Paris", "warehouse", "host0");
        Assert.Equal(90, Property(secondLookup, "Devices").GetInt32());
        Assert.Contains("startswith(computerDnsName,'host0')", Uri.UnescapeDataString(fixture.Handler.InventoryUrls[1]));

        // Editing after completion must also leave the next export bound to the applied result.
        fixture.Form("Site", "London");
        fixture.Form("SeenBy", "reception");
        fixture.Form("DeviceNameStartsWith", "host");
        var transferEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTransfer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Js.BeforeTransfer = () => { transferEntered.TrySetResult(); return releaseTransfer.Task; };
        var exporting = fixture.Invoke("DownloadCsvAsync");
        await transferEntered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        try
        {
            // A refused load cannot replace the selected export's inventory or applied question.
            await fixture.Invoke("LoadAsync");
            Assert.Equal(90, Devices(fixture.Page).Count);
            Assert.Equal(3, fixture.Audits().Length);
            fixture.Form("Site", "edited after load");
            fixture.Form("SeenBy", "edited after load");
        }
        finally
        {
            releaseTransfer.TrySetResult();
            await exporting;
        }

        Assert.Equal(90, fixture.Js.ExportedIds().Count);
        var export = fixture.Audits()[^1];
        AssertRequested(export, "Paris", "warehouse", "host0");
        Assert.Equal(90, Property(export, "Devices").GetInt32());
        foreach (var key in new[] { "RequestedFilters", "RequestFiltersEvaluation", "SiteFilterEvaluation", "SeenByFilterEvaluation", "InventoryDevices" })
            Assert.Equal(Property(secondLookup, key).GetRawText(), Property(export, key).GetRawText());
        Assert.Null(Field<string?>(fixture.Page, "csvExportError"));
    }

    [Theory]
    [InlineData("filtered-empty")]
    [InlineData("refused")]
    [InlineData("failed")]
    public async Task EmptyOrUncompletedLookupReportsZeroDisplayedAndHonestEvaluation(string outcome)
    {
        using var fixture = new Fixture(6);
        fixture.Form("Site", "London");
        await fixture.Invoke("LoadAsync");
        Assert.Equal(4, Devices(fixture.Page).Count);
        fixture.Form("Site", "no matching site");
        fixture.Form("SeenBy", "");
        fixture.Handler.RefuseInventory = outcome == "refused";
        fixture.CredentialsMissing = outcome == "failed";

        await fixture.Invoke("LoadAsync");
        await fixture.Invoke("DownloadCsvAsync");

        Assert.Empty(Devices(fixture.Page));
        Assert.Empty(fixture.Js.Delivered);
        var audits = fixture.Audits();
        Assert.Equal(2, audits.Length);
        var audit = audits[1];
        Assert.Equal(0, Property(audit, "Devices").GetInt32());
        Assert.Equal(outcome == "filtered-empty" ? "Success" : "Failed", Property(audit, "result").GetString());
        Assert.Contains("site~=no matching site", Property(audit, "RequestedFilters").GetString());
        Assert.Equal(outcome == "filtered-empty" ? "Evaluated" : "Not completed", Property(audit, "RequestFiltersEvaluation").GetString());
        AssertEvaluation(audit, "Site", outcome == "filtered-empty" ? "Evaluated" : "Not evaluated", outcome == "filtered-empty" ? 6 : 0, 0);
        AssertEvaluation(audit, "SeenBy", "Not requested", 0, 0);
        if (outcome == "failed")
        {
            Assert.Null(Field<object?>(fixture.Page, "appliedFilterAudit"));
            Assert.Null(Field<DefenderDeviceListResult?>(fixture.Page, "result"));
        }
    }

    [Theory]
    [InlineData("selection")]
    [InlineData("browser")]
    public async Task FailedExportKeepsAppliedFiltersAndDistinguishesSelectionFromConfirmedDelivery(string stage)
    {
        using var fixture = new Fixture(6);
        fixture.Form("Site", "London");
        fixture.Form("SeenBy", "reception");
        await fixture.Invoke("LoadAsync");
        fixture.Form("Site", "edited since search");
        if (stage == "selection")
        {
            var loaded = Field<DefenderDeviceListResult>(fixture.Page, "result");
            var failing = DefenderDeviceListResult.CompleteRun(new FaultingRows(), loaded.DistinctCount,
                loaded.PagesFetched, loaded.RangesCompleted, loaded.Ceiling, loaded.DiscoveryEnrichment,
                loaded.DiscoveryEnrichmentReason);
            typeof(Page).GetField("result", PrivateInstance)!.SetValue(fixture.Page, failing);
        }
        fixture.Js.BeforeTransfer = () => Task.FromException(new JSException("RAW_AUTH_BODY secret=sentinel"));

        Assert.Null(await Record.ExceptionAsync(() => fixture.Invoke("DownloadCsvAsync")));

        var audit = fixture.Audits()[^1];
        Assert.Equal("Failed", Property(audit, "result").GetString());
        AssertRequested(audit, "London", "reception");
        Assert.Equal(JsonValueKind.String, Property(audit, "Devices").ValueKind);
        Assert.Equal("Unknown", Property(audit, "Devices").GetString());
        if (stage == "selection")
            Assert.Equal("Unknown", Property(audit, "SelectedDevices").GetString());
        else
            Assert.Equal(2, Property(audit, "SelectedDevices").GetInt32());
        Assert.Equal(SafeExportError, Property(audit, "error").GetString());
        Assert.Equal(SafeExportError, Field<string?>(fixture.Page, "csvExportError"));
        Assert.False(Field<bool>(fixture.Page, "isDownloadingCsv"));
        Assert.DoesNotContain("sentinel", audit.GetRawText());
        Assert.Empty(fixture.Js.Delivered);
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        Assert.True(element.TryGetProperty(name, out var property), "Missing audit field: " + name);
        return property;
    }

    private static void AssertRequested(JsonElement audit, string site, string seenBy, string? name = null)
    {
        var requested = Property(audit, "RequestedFilters").GetString();
        Assert.Contains("onboardingStatus=CanBeOnboarded", requested);
        Assert.Contains("site~=" + site, requested);
        Assert.Contains("recentlySeenBy~=" + seenBy, requested);
        if (name is not null) Assert.Contains("computerDnsName^=" + name, requested);
    }

    private static void AssertEvaluation(JsonElement audit, string name, string status, int evaluated, int unavailable)
    {
        var detail = Property(audit, name + "FilterEvaluation");
        Assert.Equal(status, Property(detail, "status").GetString());
        Assert.Equal(evaluated, Property(detail, "evaluatedDevices").GetInt32());
        Assert.Equal(unavailable, Property(detail, "unavailableDevices").GetInt32());
    }

    private static T Field<T>(Page page, string name) => (T)typeof(Page).GetField(name, PrivateInstance)!.GetValue(page)!;
    private static IReadOnlyList<DefenderDevice> Devices(Page page) =>
        (IReadOnlyList<DefenderDevice>)typeof(Page).GetProperty("FilteredDevices", PrivateInstance)!.GetValue(page)!;

    // Attach the real handlers to a renderer for StateHasChanged, suppressing unrelated children.
    // All filtering, service calls, progress, CSV projection, interop and audit remain production code.
    private sealed class HandlerPage : Page
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder) { }
    }

    private sealed class HandlerRenderer() : Renderer(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public void Attach(IComponent component) => AssignRootComponentId(component);
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "defender-filter-audit-" + Guid.NewGuid().ToString("N"));
        private readonly HandlerRenderer renderer = new();
        private readonly HttpClient http;
        public Page Page { get; } = new HandlerPage();
        public InventoryHandler Handler { get; }
        public RecordingJs Js { get; } = new();
        public IActivityHandle Handle { get; } = Substitute.For<IActivityHandle>();
        public bool CredentialsMissing { get; set; }

        public Fixture(int count, bool includeEnrichment = true)
        {
            Handler = new InventoryHandler(count);
            http = new HttpClient(Handler);
            var client = new DefenderApiClient("tenant", "client", "secret", DefenderApiClient.DefenderBaseUrl, DefenderApiClient.DefenderTokenScope, http);
            var hunting = new DefenderApiClient("tenant", "client", "secret", DefenderApiClient.GraphBaseUrl, DefenderApiClient.GraphTokenScope, http);
            var commands = Substitute.For<ISectionAccessDirectoryCommands>();
            commands.Invoke("Get-ADRootDSE", Arg.Any<IReadOnlyDictionary<string, object?>>())
                .Returns(new DirectoryCommandOutcome([new DirectoryObject(new Dictionary<string, object?>
                { ["configurationNamingContext"] = "CN=Configuration,DC=example,DC=test" })], null));
            commands.Invoke("Get-ADObject", Arg.Any<IReadOnlyDictionary<string, object?>>()).Returns(_ => new DirectoryCommandOutcome(
                [Subnet("10.0.0.0/24", "London"), Subnet("10.0.1.0/24", "Paris")], Handler.SiteFails ? "Unavailable" : null));
            var service = new DefenderEndpointDeviceService(() => Task.FromResult(CredentialsMissing ? null : client), "500",
                () => Task.FromResult<DefenderApiClient?>(hunting), includeEnrichment.ToString(), siteDirectory: new AdSiteDirectory(() => commands))
            { HuntingBatchSizeOverride = count == 6 ? 1 : 1000 };
            Directory.CreateDirectory(root);
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Audit:LogRoot"] = root }).Build();
            var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
            var progress = Substitute.For<IActivityProgress>();
            progress.Begin(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<ActivitySize>()).Returns(Handle);
            Set("DeviceService", service);
            Set("Audit", new AuditService(log, new OperationTraceService(config, log)));
            Set("JS", Js);
            Set("Progress", progress);
            Set("Logger", Substitute.For<ILogger<Page>>());
            typeof(Page).GetField("currentUser", PrivateInstance)!.SetValue(Page, "EXAMPLE\\operator");
            typeof(Page).GetField("clientIpAddress", PrivateInstance)!.SetValue(Page, "192.0.2.5");
            renderer.Dispatcher.InvokeAsync(() => renderer.Attach(Page)).GetAwaiter().GetResult();
        }

        private void Set(string property, object value) => typeof(Page).GetProperty(property, PrivateInstance)!.SetValue(Page, value);
        public void Form(string name, object value)
        {
            var form = Field<object>(Page, "form");
            form.GetType().GetProperty(name)!.SetValue(form, value);
        }
        public Task Invoke(string method) => renderer.Dispatcher.InvokeAsync(() => (Task)typeof(Page).GetMethod(method, PrivateInstance)!.Invoke(Page, null)!);
        public JsonElement[] Audits() => Directory.GetFiles(root, "exchangeadmin_*.jsonl", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileNameWithoutExtension(path).EndsWith("_trace", StringComparison.Ordinal))
            .SelectMany(File.ReadAllLines).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        public void Dispose()
        {
            Handler.ReleaseInventory.TrySetResult();
            renderer.Dispose();
            http.Dispose();
            Directory.Delete(root, true);
        }
        private static DirectoryObject Subnet(string cidr, string site) => new(new Dictionary<string, object?>
        { ["cn"] = cidr, ["siteObject"] = $"CN={site},CN=Sites,CN=Configuration,DC=example,DC=test" });
    }

    private sealed class InventoryHandler(int count) : HttpMessageHandler
    {
        public bool SiteFails { get; set; }
        public bool ObserverFailures { get; set; }
        public bool HuntingFails { get; set; }
        public bool RefuseInventory { get; set; }
        public bool HoldInventory { get; set; }
        public TaskCompletionSource InventoryEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseInventory { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> InventoryUrls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return Json("""{"access_token":"local-test-token","expires_in":3600}""");
            if (request.Method == HttpMethod.Get)
            {
                InventoryUrls.Add(request.RequestUri.ToString());
                if (HoldInventory)
                {
                    InventoryEntered.TrySetResult();
                    await ReleaseInventory.Task.WaitAsync(cancellationToken);
                }
                if (RefuseInventory) return new HttpResponseMessage(HttpStatusCode.Forbidden);
                return Json(JsonSerializer.Serialize(new
                {
                    value = Enumerable.Range(0, count).Select(i => new
                    {
                        id = i.ToString("x4"),
                        computerDnsName = $"host{i:000}.example.test",
                        onboardingStatus = "CanBeOnboarded",
                        lastIpAddress = count == 6 ? i == 1 ? "10.0.1.1" : i == 3 ? "192.0.2.3" : "10.0.0.1"
                        : i < 10 ? "10.0.0.1" : "10.0.1.1"
                    })
                }));
            }
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            var query = body.RootElement.GetProperty("Query").GetString()!;
            var observer = query.Contains("invoke SeenBy()", StringComparison.Ordinal);
            if (HuntingFails)
                return new HttpResponseMessage(HttpStatusCode.Forbidden);
            if (ObserverFailures && observer && (query.Contains("\"0001\"", StringComparison.Ordinal) || query.Contains("\"0005\"", StringComparison.Ordinal)))
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            var ids = Enumerable.Range(0, count).Where(i => query.Contains("\"" + i.ToString("x4") + "\"", StringComparison.Ordinal));
            return Json(JsonSerializer.Serialize(new
            {
                results = ids.Select(i => new
                {
                    DeviceId = i.ToString("x4"),
                    DiscoverySources = "sensor",
                    RecentlySeenBy = count == 6 ? i == 2 ? "warehouse" : i == 4 ? "" : "reception" : i < 5 ? "reception" : "warehouse"
                })
            }));
        }
        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body) };
    }

    private sealed class RecordingJs : IJSRuntime
    {
        public List<object?[]> Delivered { get; } = [];
        public Func<Task> BeforeTransfer { get; set; } = () => Task.CompletedTask;
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public async ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("downloadFile", identifier);
            await BeforeTransfer();
            Delivered.Add(args!);
            return default!;
        }
        public IReadOnlyList<string> ExportedIds()
        {
            Assert.NotEmpty(Delivered);
            using var reader = new StringReader(Encoding.UTF8.GetString(Convert.FromBase64String((string)Delivered[^1][2]!)));
            using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
            Assert.True(csv.Read());
            csv.ReadHeader();
            var ids = new List<string>();
            while (csv.Read()) ids.Add(csv.GetField("DeviceId")!);
            return ids;
        }
    }

    private sealed class FaultingRows : IReadOnlyList<DefenderDevice>
    {
        public int Count => 6;
        public DefenderDevice this[int index] => throw new InvalidOperationException("Selection unavailable");
        public IEnumerator<DefenderDevice> GetEnumerator() => throw new InvalidOperationException("Selection unavailable");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
#pragma warning restore BL0006
