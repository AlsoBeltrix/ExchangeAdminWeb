using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using CsvHelper;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Page = ExchangeAdminWeb.Components.Pages.DefenderEndpointDevices;

namespace ExchangeAdminWeb.Tests;

#pragma warning disable BL0006 // Inspect the compiled page, as in the existing renderer tests.
public class DefenderSiteMatchingDeadlineTests
{
    [Fact]
    public async Task DeadlineBeforeSiteContinuationResumesKeepsInventoryAndCompletedHunting()
    {
        var clock = new D7Clock();
        var scheduler = new HeldWorkerScheduler();
        using var context = new PausedContext();
        using var inventory = Inventory();
        using var hunting = Hunting();
        var directory = new AdSiteDirectory(() => new D7Commands(D7.RootOrSubnet)) { WorkerScheduler = scheduler };
        var service = D7.Service(inventory, hunting, directory, clock);
        var listing = context.Start(() => service.ListDevicesAsync());

        scheduler.RunWorker(); // The consumer has already awaited the pending directory task.
        var continuation = await context.NextAsync();
        Assert.Contains("<ApplySitesAsync>", continuation.Label);
        Assert.False(listing.IsCompleted);
        clock.Advance(TimeSpan.FromMinutes(4));
        context.Run(continuation);
        var result = await context.CompleteAsync(listing);

        Assert.False(result.SiteMapRead);
        Assert.Equal(DefenderDiscoveryReasons.DeadlineReached, result.EnrichmentStopReason);
        Assert.Equal(1, clock.TimerCount);
        Assert.Equal(["a", "b", "c"], result.Devices.Select(device => device.Id));
        Assert.All(result.Devices, device => Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, device.Site));
        AssertCompletedHunting(result);
        AssertAppliedLocationAndExport(result, ["a", "b", "c"], "Unavailable", 0, 3);
    }

    [Fact]
    public async Task DeadlineWithinOneSubnetScanRetainsCompletedSitesAndMarksTheRestUnavailable()
    {
        using var inventory = Inventory();
        using var hunting = Hunting();
        var loaded = await D7.Service(inventory, hunting).ListDevicesAsync();
        var clock = new D7Clock();
        using var budget = new CancellationTokenSource(DefenderEndpointDeviceService.DefaultEnrichmentDeadline, clock);
        var subnets = new ObservedSubnets((enumeration, index) =>
        {
            if (enumeration == 2 && index == 0)
                clock.Advance(TimeSpan.FromMinutes(4));
        });

        var complete = DefenderEndpointDeviceService.ApplySiteMatches(loaded.Devices, subnets, budget.Token);

        Assert.False(complete);
        Assert.Equal(3, subnets.Visited); // Two completed subnets, then the canceled device's first.
        Assert.Equal(2, subnets.Enumerations);
        Assert.Equal("Elsewhere", loaded.Devices[0].Site);
        Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, loaded.Devices[1].Site);
        Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, loaded.Devices[2].Site);
        AssertCompletedHunting(loaded);
        var result = WithSiteOutcome(loaded, complete);
        AssertAppliedLocationAndExport(result, ["b", "c"], "Degraded", 1, 2);
    }

    [Fact]
    public async Task DeadlineAtEndOfSubnetEnumerationDoesNotPublishOrClaimCompletedSiteData()
    {
        using var inventory = Inventory();
        using var hunting = Hunting();
        var loaded = await D7.Service(inventory, hunting).ListDevicesAsync();
        var clock = new D7Clock();
        using var budget = new CancellationTokenSource(DefenderEndpointDeviceService.DefaultEnrichmentDeadline, clock);
        var subnets = new ObservedSubnets((_, index) =>
        {
            if (index == 2) // MoveNext finished, before the match could be published.
                clock.Advance(TimeSpan.FromMinutes(4));
        });
        var device = loaded.Devices[0];

        var complete = DefenderEndpointDeviceService.ApplySiteMatches([device], subnets, budget.Token);

        Assert.False(complete);
        Assert.Equal(2, subnets.Visited);
        Assert.Equal(1, subnets.Enumerations);
        Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, device.Site);
        Assert.Equal("Network", device.DiscoverySources);
    }

    private static void AssertCompletedHunting(DefenderDeviceListResult result)
    {
        Assert.Equal(DefenderDeviceListOutcome.Complete, result.Outcome);
        Assert.Equal(DefenderDiscoveryEnrichmentState.Succeeded, result.DiscoveryEnrichment);
        Assert.Equal(3, result.DistinctCount);
        Assert.All(result.Devices, device => Assert.Equal("Network", device.DiscoverySources));
        Assert.All(result.Devices, device => Assert.Equal("", device.RecentlySeenBy));
    }

    private static DefenderDeviceListResult WithSiteOutcome(DefenderDeviceListResult loaded, bool complete) =>
        DefenderDeviceListResult.CompleteRun(loaded.Devices, loaded.DistinctCount, loaded.PagesFetched,
            loaded.RangesCompleted, loaded.Ceiling, loaded.DiscoveryEnrichment, loaded.DiscoveryEnrichmentReason,
            loaded.DiscoveryFailedBatches, loaded.DiscoveryTotalBatches, complete, loaded.ObserverFailedBatches,
            loaded.DiscoveryAttemptedBatches, loaded.ObserverAttemptedBatches, loaded.ObserverEnrichmentReason,
            DefenderDiscoveryReasons.DeadlineReached);

    private static HttpClient Inventory() => D7.Http((request, _) =>
        request.RequestUri!.Host == "login.microsoftonline.com" ? D7.Token() : D7.Json(JsonSerializer.Serialize(new
        {
            value = new[] { "a", "b", "c" }.Select((id, index) => new
            {
                id,
                computerDnsName = "host-" + id,
                lastIpAddress = "10.0." + index + ".1"
            })
        })));

    private static HttpClient Hunting() => D7.Http((request, _) =>
    {
        if (request.RequestUri!.Host == "login.microsoftonline.com")
            return D7.Token();
        var query = D7.Query(request);
        return D7.Json(query.Contains("SeenBy()", StringComparison.Ordinal) ? "{\"results\":[]}"
            : D7.Posture(new[] { "a", "b", "c" }.Single(id => query.Contains("\"" + id + "\"", StringComparison.Ordinal))));
    });

    private static void AssertAppliedLocationAndExport(DefenderDeviceListResult result, string[] ids,
        string evaluation, int known, int unavailable)
    {
        const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
        const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
        var page = new Page();
        typeof(Page).GetField("result", instance)!.SetValue(page, result);
        typeof(Page).GetField("appliedSite", instance)!.SetValue(page, "London");
        var filtered = (IReadOnlyList<DefenderDevice>)typeof(Page).GetProperty("FilteredDevices", instance)!.GetValue(page)!;
        Assert.Equal(ids, filtered.Select(device => device.Id));
        var snapshot = typeof(Page).GetMethod("CreateFilterAudit", statics)!.Invoke(null,
            [new DefenderDeviceFilters(), "London", "", result]);
        var details = typeof(Page).GetMethod("FilterAuditDetails", statics)!.Invoke(null, [snapshot, filtered.Count]);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(details, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
        var site = json.RootElement.GetProperty("SiteFilterEvaluation");
        Assert.Equal(evaluation, site.GetProperty("status").GetString());
        Assert.Equal(known, site.GetProperty("evaluatedDevices").GetInt32());
        Assert.Equal(unavailable, site.GetProperty("unavailableDevices").GetInt32());
        Assert.Equal(ids.Length, json.RootElement.GetProperty("Devices").GetInt32());
        Assert.Equal(3, json.RootElement.GetProperty("InventoryDevices").GetInt32());

        using var reader = new StringReader(Page.BuildCsv(filtered, result.DiscoveryEnrichment));
        using var csv = new CsvReader(reader, System.Globalization.CultureInfo.InvariantCulture);
        csv.Read();
        csv.ReadHeader();
        var exported = new List<string>();
        while (csv.Read())
        {
            exported.Add(csv.GetField("DeviceId")!);
            Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, csv.GetField("Site"));
            Assert.Equal("Network", csv.GetField("DiscoverySources"));
        }
        Assert.Equal(ids, exported);

        using var http = D7.Inventory("fixture");
        using var fixture = new DefenderEnrichmentCancellationTests.VaultFixture(http);
        typeof(Page).GetProperty("DeviceService", instance)!.SetValue(page,
            new DefenderEndpointDeviceService(fixture.Config, fixture.Vault, fixture.HttpFactory,
                new AdSiteDirectory(() => new D7Commands(D7.RootOrSubnet))));
        typeof(Page).GetField("authChecked", instance)!.SetValue(page, true);
        using var builder = new RenderTreeBuilder();
        typeof(Page).GetMethod("BuildRenderTree", instance)!.Invoke(page, [builder]);
        var frames = builder.GetFrames();
        var text = string.Join(" ", frames.Array.Take(frames.Count).Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Text => frame.TextContent,
            RenderTreeFrameType.Markup => frame.MarkupContent,
            _ => ""
        }));
        Assert.Contains("Site details could not be completed for every device.", text);
        Assert.Contains("Completed Site details and the rest of the report are retained.", text);
        Assert.DoesNotContain("Every device shows", text);
    }

    private sealed class ObservedSubnets(Action<int, int> visit) : IReadOnlyList<AdSiteSubnetMap.Subnet>
    {
        private readonly AdSiteSubnetMap.Subnet[] _rows = [new("10.0.0.0/24", "Elsewhere"), new("10.0.1.0/24", "London")];
        internal int Enumerations;
        internal int Visited;
        public int Count => _rows.Length;
        public AdSiteSubnetMap.Subnet this[int index] => _rows[index];
        public IEnumerator<AdSiteSubnetMap.Subnet> GetEnumerator()
        {
            var enumeration = ++Enumerations;
            for (var i = 0; i < _rows.Length; i++)
            {
                Visited++;
                visit(enumeration, i);
                yield return _rows[i];
            }
            visit(enumeration, _rows.Length);
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class HeldWorkerScheduler : TaskScheduler
    {
        private readonly ConcurrentQueue<Task> _tasks = new();
        protected override void QueueTask(Task task) => _tasks.Enqueue(task);
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task> GetScheduledTasks() => _tasks.ToArray();
        internal void RunWorker()
        {
            Assert.True(_tasks.TryDequeue(out var task));
            Assert.True(TryExecuteTask(task!));
        }
    }

    private sealed class PausedContext : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<Continuation> _queue = new();
        private readonly SemaphoreSlim _posted = new(0);
        internal sealed record Continuation(SendOrPostCallback Callback, object? State)
        {
            internal string Label => (State as Action)?.Target?.GetType().FullName ?? "";
        }
        public override void Post(SendOrPostCallback callback, object? state)
        {
            _queue.Enqueue(new(callback, state));
            _posted.Release();
        }
        internal Task<T> Start<T>(Func<Task<T>> action)
        {
            var prior = Current;
            SetSynchronizationContext(this);
            try { return action(); }
            finally { SetSynchronizationContext(prior); }
        }
        internal void Run(Continuation continuation)
        {
            var prior = Current;
            SetSynchronizationContext(this);
            try { continuation.Callback(continuation.State); }
            finally { SetSynchronizationContext(prior); }
        }
        internal async Task<Continuation> NextAsync()
        {
            if (!await _posted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
                throw new TimeoutException("Expected a captured site continuation.");
            return _queue.TryDequeue(out var next) ? next : throw new InvalidOperationException("No queued continuation.");
        }
        internal async Task<T> CompleteAsync<T>(Task<T> task)
        {
            for (var i = 0; !task.IsCompleted && i < 30; i++)
                Run(await NextAsync());
            return await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
        public void Dispose() => _posted.Dispose();
    }
}
#pragma warning restore BL0006
