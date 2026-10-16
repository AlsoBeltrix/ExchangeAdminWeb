using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Page = ExchangeAdminWeb.Components.Pages.DefenderEndpointDevices;

namespace ExchangeAdminWeb.Tests;

#pragma warning disable BL0006 // Inspect the compiled page frames, matching existing renderer tests.
public class DefenderEnrichmentDeadlineTests
{
    [Theory]
    [InlineData("setup")]
    [InlineData("token")]
    [InlineData("posture")]
    [InlineData("observer")]
    [InlineData("site")]
    public async Task OneDeadlineSpansEveryStageAndRetainsTheInventory(string stage)
    {
        var clock = new D7Clock();
        var calls = new ConcurrentQueue<string>();
        var commands = new D7Commands(command =>
        {
            if (stage == "site" && command == "Get-ADRootDSE")
                clock.Advance(TimeSpan.FromMinutes(1));
            return D7.RootOrSubnet(command);
        });
        using var inventoryHttp = D7.Http((request, _) =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return D7.Token();
            Assert.Equal(0, clock.TimerCount);
            clock.Advance(TimeSpan.FromHours(1)); // Inventory is outside the enrichment budget.
            return D7.Json(D7.Machines("a"));
        });
        using var huntingHttp = D7.Http((request, token) =>
        {
            var kind = request.RequestUri!.Host == "login.microsoftonline.com" ? "token"
                : request.Content!.ReadAsStringAsync().Result.Contains("SeenBy()", StringComparison.Ordinal)
                    ? "observer" : "posture";
            calls.Enqueue(kind);
            clock.Advance(stage == kind ? TimeSpan.FromMinutes(kind == "token" ? 3 : kind == "posture" ? 2 : 1)
                : TimeSpan.FromSeconds(stage == "site" && kind != "token" ? 30 : 60));
            token.ThrowIfCancellationRequested();
            return kind == "token" ? D7.Token() : D7.Json(kind == "posture" ? D7.Posture("a") : "{\"results\":[]}");
        });
        var service = new DefenderEndpointDeviceService(() => Task.FromResult<DefenderApiClient?>(D7.Client(inventoryHttp)),
            siteDirectory: new AdSiteDirectory(() => commands),
            cancellableHuntingClientFactory: token =>
            {
                calls.Enqueue("setup");
                Assert.True(token.CanBeCanceled);
                clock.Advance(TimeSpan.FromMinutes(stage == "setup" ? 4 : 1));
                token.ThrowIfCancellationRequested();
                return Task.FromResult<DefenderApiClient?>(D7.Client(huntingHttp, true));
            })
        { EnrichmentTimeProvider = clock, HuntingBatchSizeOverride = 1 };

        var result = await service.ListDevicesAsync();

        Assert.Equal(1, clock.TimerCount);
        Assert.Equal(TimeSpan.FromMinutes(4), clock.InitialDue);
        Assert.Equal(DefenderDiscoveryReasons.DeadlineReached, result.EnrichmentStopReason);
        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Equal("host-a", Assert.Single(result.Devices).ComputerDnsName);
        Assert.False(result.SiteMapRead);
        Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, result.Devices[0].Site);
        Assert.Equal(stage is "setup" or "token" ? stage == "setup" ? ["setup"] : ["setup", "token"]
            : stage == "posture" ? ["setup", "token", "posture"] : ["setup", "token", "posture", "observer"], calls);
        Assert.Equal(1, result.DiscoveryTotalBatches);
        Assert.Equal(stage == "setup" ? 0 : 1, result.DiscoveryAttemptedBatches);
        Assert.Equal(stage is "token" or "posture" ? 1 : 0, result.DiscoveryFailedBatches);
        Assert.Equal(stage is "observer" or "site" ? "Network" : DefenderEndpointDeviceService.EnrichmentUnavailable,
            DefenderEndpointDeviceService.DescribeEnrichmentCell(result.DiscoveryEnrichment, result.Devices[0].DiscoverySources));
        if (stage == "observer")
        {
            Assert.Equal(DefenderDiscoveryEnrichmentState.Succeeded, result.DiscoveryEnrichment);
            Assert.Equal("", result.DiscoveryEnrichmentReason);
            Assert.Equal(DefenderDiscoveryReasons.DeadlineReached, result.ObserverEnrichmentReason);
            Assert.Equal(1, result.ObserverAttemptedBatches);
            Assert.Equal(1, result.ObserverFailedBatches);
            Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, result.Devices[0].RecentlySeenBy);
        }
        if (stage != "site")
            Assert.Empty(commands.Calls);
    }

    [Theory]
    [InlineData(false, 401)]
    [InlineData(false, 403)]
    [InlineData(true, 401)]
    [InlineData(true, 403)]
    public async Task SystemicFailureFromEitherPassStopsLaterBatchesAndMarksSkippedRows(bool observer, int status)
    {
        var calls = new List<string>();
        using var inventory = D7.Inventory("a", "b", "c");
        using var hunting = D7.Http((request, _) =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return D7.Token();
            var query = D7.Query(request);
            calls.Add(query);
            var isObserver = query.Contains("SeenBy()", StringComparison.Ordinal);
            if (query.Contains("\"b\"", StringComparison.Ordinal) && isObserver == observer)
                return D7.Json("{}", (HttpStatusCode)status);
            return D7.Json(isObserver ? "{\"results\":[]}" : D7.Posture(query.Contains("\"a\"", StringComparison.Ordinal) ? "a" : "b"));
        });
        var site = new D7Commands(D7.RootOrSubnet);
        var service = D7.Service(inventory, hunting, new AdSiteDirectory(() => site));
        var result = await service.ListDevicesAsync();

        Assert.Equal(observer ? 4 : 3, calls.Count);
        Assert.Equal(3, result.DiscoveryTotalBatches);
        Assert.Equal(2, result.DiscoveryAttemptedBatches);
        Assert.Equal(observer ? 0 : 1, result.DiscoveryFailedBatches);
        Assert.Equal(1, result.DiscoveryUnattemptedBatches);
        Assert.Equal(observer ? 2 : 1, result.ObserverAttemptedBatches);
        Assert.Equal(observer ? 1 : 0, result.ObserverFailedBatches);
        Assert.Equal(observer ? 1 : 2, result.ObserverUnattemptedBatches);
        Assert.Equal(status == 401 ? DefenderDiscoveryReasons.Unauthorized : DefenderDiscoveryReasons.Forbidden, result.EnrichmentStopReason);
        Assert.Equal(DefenderDiscoveryEnrichmentState.PartiallySucceeded, result.DiscoveryEnrichment);
        Assert.Equal("Network", result.Devices[0].DiscoverySources);
        Assert.Equal(observer ? "Network" : DefenderEndpointDeviceService.EnrichmentUnavailable, result.Devices[1].DiscoverySources);
        Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, result.Devices[1].RecentlySeenBy);
        Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, result.Devices[2].DiscoverySources);
        Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, result.Devices[2].RecentlySeenBy);
        Assert.True(result.SiteMapRead); // Authorization stop leaves the remaining budget for AD.
        Assert.All(result.Devices, device => Assert.Equal("Site", device.Site));
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(403)]
    public async Task KnownTokenAuthenticationFailureDoesNotRepeatForEveryBatch(int status)
    {
        var tokens = 0;
        var queries = 0;
        using var inventory = D7.Inventory("a", "b", "c");
        using var hunting = D7.Http((request, _) =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
            {
                tokens++;
                return D7.Json("secret auth text must never be returned", (HttpStatusCode)status);
            }
            queries++;
            return D7.Json("{\"results\":[]}");
        });
        var result = await D7.Service(inventory, hunting).ListDevicesAsync();

        Assert.Equal(1, tokens);
        Assert.Equal(0, queries);
        Assert.Equal(1, result.DiscoveryAttemptedBatches);
        Assert.Equal(1, result.DiscoveryFailedBatches);
        Assert.Equal(2, result.DiscoveryUnattemptedBatches);
        Assert.Equal(DefenderDiscoveryReasons.SendFailed, result.EnrichmentStopReason);
        Assert.Equal(3, result.Devices.Count);
    }

    [Theory]
    [InlineData("429")]
    [InlineData("503")]
    [InlineData("malformed")]
    [InlineData("exception")]
    public async Task IsolatedFailuresAndObserver400KeepLaterEmptySuccesses(string failure)
    {
        var queries = 0;
        using var inventory = D7.Inventory("a", "b", "c");
        using var hunting = D7.Http((request, _) =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return D7.Token();
            queries++;
            if (queries == 1)
                return failure switch
                {
                    "429" => D7.Json("{}", HttpStatusCode.TooManyRequests),
                    "503" => D7.Json("{}", HttpStatusCode.ServiceUnavailable),
                    "malformed" => D7.Json("{\"results\":{}}"),
                    _ => throw new InvalidOperationException("401 Unauthorized is arbitrary exception text")
                };
            if (queries == 3)
                return D7.Json("{}", HttpStatusCode.BadRequest); // Observer-specific unsupported query.
            return D7.Json("{\"results\":[]}");
        });
        var result = await D7.Service(inventory, hunting).ListDevicesAsync();

        Assert.Equal(5, queries);
        Assert.Equal(3, result.DiscoveryAttemptedBatches);
        Assert.Equal(1, result.DiscoveryFailedBatches);
        Assert.Equal(0, result.DiscoveryUnattemptedBatches);
        Assert.Equal(2, result.ObserverAttemptedBatches);
        Assert.Equal(1, result.ObserverFailedBatches);
        Assert.Equal(1, result.ObserverUnattemptedBatches);
        Assert.Equal("", result.EnrichmentStopReason);
        Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, result.Devices[0].DiscoverySources);
        Assert.Equal("", result.Devices[1].DiscoverySources);
        Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, result.Devices[1].RecentlySeenBy);
        Assert.Equal("", result.Devices[2].DiscoverySources);
        Assert.Equal("", result.Devices[2].RecentlySeenBy);
    }

    [Fact]
    public async Task DeadlineBetweenBatchesUsesRemainingBudgetAndKeepsSuccessfulRows()
    {
        var clock = new D7Clock();
        var queries = 0;
        using var inventory = D7.Inventory("a", "b", "c");
        using var hunting = D7.Http((request, token) =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return D7.Token();
            queries++;
            clock.Advance(TimeSpan.FromMinutes(1));
            token.ThrowIfCancellationRequested();
            return D7.Json(queries == 1 ? D7.Posture("a") : "{\"results\":[]}");
        });
        var service = D7.Service(inventory, hunting, clock: clock);
        var result = await service.ListDevicesAsync();

        Assert.Equal(4, queries);
        Assert.Equal(2, result.DiscoveryAttemptedBatches);
        Assert.Equal(0, result.DiscoveryFailedBatches);
        Assert.Equal(1, result.DiscoveryUnattemptedBatches);
        Assert.Equal(1, result.ObserverFailedBatches);
        Assert.Equal(1, clock.TimerCount);
        Assert.Equal("Network", result.Devices[0].DiscoverySources);
        Assert.Equal("", result.Devices[1].DiscoverySources);
        Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, result.Devices[1].RecentlySeenBy);
        Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, result.Devices[2].DiscoverySources);
        Assert.Equal(DefenderDiscoveryReasons.DeadlineReached, result.EnrichmentStopReason);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public async Task CompiledPageReportsDeadlineAndActualFailedVersusSkippedCounts(int count)
    {
        var clock = new D7Clock();
        using var inventory = D7.Inventory(count == 1 ? ["a"] : ["a", "b", "c"]);
        using var hunting = D7.Http((request, token) =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return D7.Token();
            if (D7.Query(request).Contains("SeenBy()", StringComparison.Ordinal))
            {
                clock.Advance(TimeSpan.FromMinutes(4));
                token.ThrowIfCancellationRequested();
            }
            return D7.Json(D7.Posture("a"));
        });
        var result = await D7.Service(inventory, hunting, clock: clock).ListDevicesAsync();
        using var fixture = new DefenderEnrichmentCancellationTests.VaultFixture(inventory);
        var page = new Page();
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(Page).GetProperty("DeviceService", instance)!.SetValue(page,
            new DefenderEndpointDeviceService(fixture.Config, fixture.Vault, fixture.HttpFactory,
                new AdSiteDirectory(() => new D7Commands(D7.RootOrSubnet))));
        typeof(Page).GetField("authChecked", instance)!.SetValue(page, true);
        typeof(Page).GetField("result", instance)!.SetValue(page, result);
        using var builder = new RenderTreeBuilder();
        typeof(Page).GetMethod("BuildRenderTree", instance)!.Invoke(page, [builder]);
        var frames = builder.GetFrames();
        var text = Regex.Replace(string.Join(" ", frames.Array.Take(frames.Count).Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Text => frame.TextContent,
            RenderTreeFrameType.Markup => frame.MarkupContent,
            _ => ""
        })), @"\s+", " ");

        Assert.Contains(DefenderDiscoveryReasons.DeadlineReached, text);
        Assert.Contains($"1 failed, {count - 1} not attempted out of {count} observer lookups", text);
        if (count > 1)
            Assert.Contains("0 failed, 2 not attempted out of 3 posture lookups", text);
        Assert.Contains(DefenderEndpointDeviceService.EnrichmentUnavailable, text);
        Assert.Contains("Completed posture details are retained.", text);
        Assert.DoesNotContain("lookups failed", text);
    }
}

internal static class D7
{
    internal static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };
    internal static HttpClient Inventory(params string[] ids) => Http((request, _) => request.RequestUri!.Host == "login.microsoftonline.com" ? Token() : Json(Machines(ids)));
    internal static HttpResponseMessage Token() => Json("{\"access_token\":\"test-token\",\"expires_in\":3600}");
    internal static string Machines(params string[] ids) => JsonSerializer.Serialize(new
    {
        value = ids.Select(id => new { id, computerDnsName = "host-" + id, lastIpAddress = "10.0.0.1" })
    });
    internal static string Posture(string id) => JsonSerializer.Serialize(new
    {
        results = new[] { new { DeviceId = id, DiscoverySources = "Network", DeviceType = "Router" } }
    });
    internal static string Query(HttpRequestMessage request)
    {
        using var body = JsonDocument.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
        return body.RootElement.GetProperty("Query").GetString()!;
    }
    internal static HttpClient Http(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) =>
        new(new Handler((request, token) => Task.FromResult(handler(request, token))));
    internal static HttpClient AsyncHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => new(new Handler(handler));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handler(request, cancellationToken);
    }
    internal static DefenderApiClient Client(HttpClient http, bool hunting = false) => new("tenant", "client", "secret",
        hunting ? DefenderApiClient.GraphBaseUrl : DefenderApiClient.DefenderBaseUrl,
        hunting ? DefenderApiClient.GraphTokenScope : DefenderApiClient.DefenderTokenScope, http);
    internal static DefenderEndpointDeviceService Service(HttpClient inventory, HttpClient hunting,
        AdSiteDirectory? directory = null, D7Clock? clock = null) => new(
            () => Task.FromResult<DefenderApiClient?>(Client(inventory)),
            huntingClientFactory: () => Task.FromResult<DefenderApiClient?>(Client(hunting, true)), siteDirectory: directory)
        { HuntingBatchSizeOverride = 1, EnrichmentTimeProvider = clock ?? (TimeProvider)TimeProvider.System };
    internal static DirectoryCommandOutcome RootOrSubnet(string command) => command == "Get-ADRootDSE"
        ? DirectoryCommandOutcome.Success(new DirectoryObject(new Dictionary<string, object?> { ["configurationNamingContext"] = "CN=Configuration,DC=example,DC=test" }))
        : DirectoryCommandOutcome.Success(new DirectoryObject(new Dictionary<string, object?> { ["cn"] = "10.0.0.0/24", ["siteObject"] = "CN=Site,CN=Sites,DC=example,DC=test" }));
}

internal sealed class D7Commands(Func<string, DirectoryCommandOutcome> invoke) : ICancellableDirectoryCommands
{
    internal ConcurrentQueue<string> Calls { get; } = new();
    internal bool Disposed { get; private set; }
    internal int Stops;
    public DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters)
    {
        Calls.Enqueue(command);
        return invoke(command);
    }
    public DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) => Invoke(command, parameters);
    public string? TranslateSidToNTAccount(string sid) => null;
    public void RequestStop() => Interlocked.Increment(ref Stops);
    public void Dispose() => Disposed = true;
}

internal sealed class D7Clock : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ClockTimer> _timers = [];
    private TimeSpan _elapsed;
    internal int TimerCount { get; private set; }
    internal TimeSpan InitialDue { get; private set; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch + _elapsed;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            TimerCount++;
            InitialDue = dueTime;
            var timer = new ClockTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }
    internal void Advance(TimeSpan elapsed)
    {
        List<ClockTimer> due;
        lock (_gate)
        {
            _elapsed += elapsed;
            due = _timers.Where(timer => timer.Due <= _elapsed).ToList();
            foreach (var timer in due)
                timer.Due = TimeSpan.MaxValue;
        }
        foreach (var timer in due)
            timer.Fire();
    }
    private sealed class ClockTimer(D7Clock owner, TimerCallback callback, object? state) : ITimer
    {
        internal TimeSpan Due = TimeSpan.MaxValue;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
                Due = dueTime == Timeout.InfiniteTimeSpan ? TimeSpan.MaxValue : owner._elapsed + dueTime;
            return true;
        }
        internal void Fire() => callback(state);
        public void Dispose() { lock (owner._gate) Due = TimeSpan.MaxValue; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
#pragma warning restore BL0006
