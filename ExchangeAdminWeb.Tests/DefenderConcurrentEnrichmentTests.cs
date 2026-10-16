using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using CsvHelper;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

public class DefenderConcurrentEnrichmentTests
{
    [Theory]
    [InlineData("status", false)]
    [InlineData("malformed", false)]
    [InlineData("send", false)]
    [InlineData("status", true)]
    [InlineData("malformed", true)]
    [InlineData("send", true)]
    public async Task OverlappingListingsKeepTheirOwnFailuresAndAvailableCells(string failure, bool partial)
    {
        using var firstInventoryHttp = new HttpClient(new ScriptedHandler(_ => Json(Machines(
            partial ? ["alpha-failed", "alpha-empty"] : ["alpha-failed"]))));
        using var secondInventoryHttp = new HttpClient(new ScriptedHandler(_ => Json(Machines(
            ["beta-failed", "beta-rows", "beta-empty"]))));
        var firstHuntingHandler = new ScriptedHandler(query => query.Contains("\"alpha-failed\"", StringComparison.Ordinal)
            ? Failure(failure)
            : Json("""{"results":[]}"""), defer: true);
        var secondHuntingHandler = new ScriptedHandler(query =>
        {
            if (query.Contains("\"beta-failed\"", StringComparison.Ordinal))
                return Json("{}", HttpStatusCode.ServiceUnavailable);
            if (query.Contains("\"beta-empty\"", StringComparison.Ordinal))
                return Json("""{"results":[]}""");
            if (query.Contains("| invoke SeenBy()", StringComparison.Ordinal))
                return Failure(failure);
            return Json("""{"results":[{"DeviceId":"beta-rows","DiscoverySources":"Network","DeviceType":"Router","DeviceCategory":"Network","Vendor":"Acme","Model":"R1"}]}""");
        }, defer: true);
        using var firstHuntingHttp = new HttpClient(firstHuntingHandler);
        using var secondHuntingHttp = new HttpClient(secondHuntingHandler);
        var firstInventory = Client(firstInventoryHttp, hunting: false);
        var secondInventory = Client(secondInventoryHttp, hunting: false);
        var firstHunting = Client(firstHuntingHttp, hunting: true);
        var secondHunting = Client(secondHuntingHttp, hunting: true);
        var inventoryCalls = 0;
        var huntingCalls = 0;
        var service = new DefenderEndpointDeviceService(
            () => Task.FromResult<DefenderApiClient?>(Interlocked.Increment(ref inventoryCalls) == 1
                ? firstInventory : secondInventory),
            "50",
            () => Task.FromResult<DefenderApiClient?>(Interlocked.Increment(ref huntingCalls) == 1
                ? firstHunting : secondHunting))
        { HuntingBatchSizeOverride = 1 };

        using var firstQueue = new ControlledContinuations();
        using var secondQueue = new ControlledContinuations();
        var firstTask = firstQueue.Start(() => service.ListDevicesAsync());
        var firstFailureReady = await firstQueue.NextBatchAggregationAsync();

        // The first helper has returned its failure, but its caller has not consumed it.
        // Let a competing call through the SAME singleton finish a different posture failure.
        var secondTask = secondQueue.Start(() => service.ListDevicesAsync());
        var secondFailureReady = await secondQueue.NextBatchAggregationAsync();
        secondQueue.Run(secondFailureReady);
        var secondPostureReady = await secondQueue.NextBatchAggregationAsync();
        secondQueue.Run(secondPostureReady);
        var secondObserverReady = await secondQueue.NextBatchAggregationAsync();

        Assert.False(firstTask.IsCompleted);
        Assert.False(secondTask.IsCompleted);
        Assert.Single(firstHuntingHandler.Queries);
        Assert.Equal(3, secondHuntingHandler.Queries.Count);
        Assert.Contains("| invoke SeenBy()", secondHuntingHandler.Queries.Last());

        // Resume the original run while the other run's observer outcome is also pending.
        // Fresh context identities force queued await continuations, never a timing race.
        firstQueue.Run(firstFailureReady);
        var first = await firstQueue.CompleteAsync(firstTask);
        secondQueue.Run(secondObserverReady);
        var second = await secondQueue.CompleteAsync(secondTask);

        Assert.Equal(DefenderDeviceListOutcome.Complete, first.Outcome);
        Assert.Equal(DefenderDeviceListOutcome.Complete, second.Outcome);
        Assert.Equal(ExpectedReason(failure), first.DiscoveryEnrichmentReason);
        Assert.Equal(DefenderEndpointDeviceService.DescribeEnrichmentFailure(new(null, HttpStatusCode.ServiceUnavailable, null, false)), second.DiscoveryEnrichmentReason);
        Assert.NotEqual(first.DiscoveryEnrichmentReason, second.DiscoveryEnrichmentReason);
        Assert.Equal(partial ? DefenderDiscoveryEnrichmentState.PartiallySucceeded
            : DefenderDiscoveryEnrichmentState.Failed, first.DiscoveryEnrichment);
        Assert.Equal(DefenderDiscoveryEnrichmentState.PartiallySucceeded, second.DiscoveryEnrichment);
        Assert.Equal(1, first.DiscoveryFailedBatches);
        Assert.Equal(partial ? 2 : 1, first.DiscoveryTotalBatches);
        Assert.Equal(0, first.ObserverFailedBatches);
        Assert.Equal(1, second.DiscoveryFailedBatches);
        Assert.Equal(3, second.DiscoveryTotalBatches);
        Assert.Equal(1, second.ObserverFailedBatches);
        Assert.Equal(partial ? ["alpha-failed", "alpha-empty"] : ["alpha-failed"], first.Devices.Select(d => d.Id));
        Assert.Equal(["beta-failed", "beta-rows", "beta-empty"], second.Devices.Select(d => d.Id));
        Assert.Equal(partial ? 2 : 1, first.DistinctCount);
        Assert.Equal(3, second.DistinctCount);
        Assert.Equal(1, first.PagesFetched);
        Assert.Equal(1, second.PagesFetched);
        Assert.Equal(partial ? 3 : 1, firstHuntingHandler.Queries.Count);
        Assert.Equal(5, secondHuntingHandler.Queries.Count);
        Assert.Equal(2, inventoryCalls);
        Assert.Equal(2, huntingCalls);

        AssertUnavailable(first, "alpha-failed");
        AssertUnavailable(second, "beta-failed");
        if (partial)
            AssertEmpty(first, "alpha-empty");
        AssertEmpty(second, "beta-empty");
        var retained = Assert.Single(second.Devices, d => d.Id == "beta-rows");
        Assert.Equal(["Network", "Router", "Network", "Acme", "R1"], Posture(retained));
        Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, retained.RecentlySeenBy);
        var csv = CsvRows(second);
        Assert.Equal(3, csv.Count);
        Assert.Equal("Network", csv["beta-rows"]["DiscoverySources"]);
        Assert.Equal("Acme", csv["beta-rows"]["Vendor"]);
        Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, csv["beta-rows"]["RecentlySeenBy"]);
    }

    private static void AssertUnavailable(DefenderDeviceListResult result, string id)
    {
        var row = Assert.Single(result.Devices, d => d.Id == id);
        var unavailable = DefenderEndpointDeviceService.EnrichmentUnavailable;
        Assert.All(Posture(row).Append(row.RecentlySeenBy), value => Assert.Equal(unavailable,
            DefenderEndpointDeviceService.DescribeEnrichmentCell(result.DiscoveryEnrichment, value)));
        var csv = CsvRows(result)[id];
        foreach (var column in EnrichmentColumns)
            Assert.Equal(unavailable, csv[column]);
    }

    private static void AssertEmpty(DefenderDeviceListResult result, string id)
    {
        var row = Assert.Single(result.Devices, d => d.Id == id);
        Assert.All(Posture(row).Append(row.RecentlySeenBy), value => Assert.Equal("", value));
        var csv = CsvRows(result)[id];
        foreach (var column in EnrichmentColumns)
            Assert.Equal("", csv[column]);
    }

    private static readonly string[] EnrichmentColumns =
        ["DiscoverySources", "DeviceType", "DeviceCategory", "Vendor", "Model", "RecentlySeenBy"];

    private static string[] Posture(DefenderDevice row) =>
        [row.DiscoverySources, row.DeviceType, row.DeviceCategory, row.Vendor, row.Model];

    private static Dictionary<string, Dictionary<string, string>> CsvRows(DefenderDeviceListResult result)
    {
        using var reader = new StringReader(DefenderEndpointDevices.BuildCsv(result.Devices, result.DiscoveryEnrichment));
        using var csv = new CsvReader(reader, CultureInfo.InvariantCulture);
        csv.Read();
        csv.ReadHeader();
        var rows = new Dictionary<string, Dictionary<string, string>>();
        while (csv.Read())
        {
            var cells = csv.HeaderRecord!.ToDictionary(header => header, header => csv.GetField(header)!);
            rows.Add(cells["DeviceId"], cells);
        }
        return rows;
    }

    private static string ExpectedReason(string failure) => failure switch
    {
        "status" => DefenderDiscoveryReasons.Throttled,
        "malformed" => DefenderDiscoveryReasons.MalformedResponse,
        "send" => DefenderDiscoveryReasons.SendFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };

    private static HttpResponseMessage Failure(string failure) => failure switch
    {
        "status" => Json("{}", HttpStatusCode.TooManyRequests),
        "malformed" => Json("""{"results":{}}"""),
        "send" => throw new HttpRequestException("Synthetic transport failure; never report this text."),
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body) };

    private static string Machines(string[] ids) => JsonSerializer.Serialize(new
    {
        value = ids.Select((id, index) => new { id, computerDnsName = $"host{index:D2}.corp.example", onboardingStatus = "CanBeOnboarded" })
    });

    private static DefenderApiClient Client(HttpClient http, bool hunting) => new(
        "tenant", "client", "secret",
        hunting ? DefenderApiClient.GraphBaseUrl : DefenderApiClient.DefenderBaseUrl,
        hunting ? DefenderApiClient.GraphTokenScope : DefenderApiClient.DefenderTokenScope,
        http);

    private sealed class ScriptedHandler(Func<string, HttpResponseMessage> respond, bool defer = false) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Queries { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return Json("""{"access_token":"test-token","expires_in":3600}""");
            var query = "";
            if (request.Content != null)
            {
                using var body = JsonDocument.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
                query = body.RootElement.GetProperty("Query").GetString()!;
            }
            Queries.Enqueue(query);
            if (defer)
                await Task.Yield();
            return respond(query);
        }
    }

    // This schedules real async methods, without production hooks or inspecting private fields.
    // The named aggregation continuation is the precise gap in the original shared-field bug.
    private sealed class ControlledContinuations : IDisposable
    {
        private readonly ConcurrentQueue<Continuation> _queue = new();
        private readonly SemaphoreSlim _posted = new(0);

        internal sealed record Continuation(SendOrPostCallback Callback, object? State)
        {
            internal bool IsBatchAggregation => State is Action action
                && action.Target?.GetType().FullName?.Contains("<GetDiscoveryEnrichmentAsync>", StringComparison.Ordinal) == true;
        }

        private sealed class QueueContext(ControlledContinuations owner) : SynchronizationContext
        {
            public override void Post(SendOrPostCallback callback, object? state)
            {
                owner._queue.Enqueue(new(callback, state));
                owner._posted.Release();
            }

            public override SynchronizationContext CreateCopy() => new QueueContext(owner);
        }

        internal Task<T> Start<T>(Func<Task<T>> action)
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new QueueContext(this));
            try { return action(); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        internal void Run(Continuation continuation)
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new QueueContext(this));
            try { continuation.Callback(continuation.State); }
            finally { SynchronizationContext.SetSynchronizationContext(previous); }
        }

        internal async Task<Continuation> NextBatchAggregationAsync()
        {
            for (var step = 0; step < 100; step++)
            {
                var next = await NextAsync();
                if (next.IsBatchAggregation)
                    return next;
                Run(next);
            }
            throw new InvalidOperationException("No batch aggregation continuation within the bounded test script.");
        }

        internal async Task<T> CompleteAsync<T>(Task<T> task)
        {
            for (var step = 0; !task.IsCompleted && step < 100; step++)
                Run(await NextAsync());
            return await task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        private async Task<Continuation> NextAsync()
        {
            if (!await _posted.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
                throw new TimeoutException("Expected a queued production continuation.");
            return _queue.TryDequeue(out var next) ? next
                : throw new InvalidOperationException("Continuation signal without a queued callback.");
        }

        public void Dispose() => _posted.Dispose();
    }
}
