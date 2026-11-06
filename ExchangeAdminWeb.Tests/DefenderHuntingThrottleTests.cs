using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// sweep-d item 31. A 429 from advanced hunting is the tenant's quota, not one batch: only 401 and 403 stopped the loop,
/// so every later batch was refused the same way and each greyed its devices. Now a 429 waits out a short Retry-After
/// once per run and asks again; a second 429, or a missing or long Retry-After, stops the loop and reports how many
/// devices were not queried. Real <see cref="DefenderApiClient"/>s over scripted handlers, one device per batch.
/// </summary>
public class DefenderHuntingThrottleTests
{
    private static readonly string[] Devices = ["a", "b", "c"];

    private static HttpResponseMessage Throttled(TimeSpan? retryAfter = null)
    {
        var response = D7.Json("{}", HttpStatusCode.TooManyRequests);
        if (retryAfter is { } wait)
            response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
        return response;
    }

    private sealed class Run
    {
        public int Queries;
        public List<TimeSpan> Waits { get; } = [];
        public CapturingLogger Log { get; } = new();
        public DefenderDeviceListResult? Result;
    }

    /// <summary>Answers query N with <paramref name="script"/>(N), or an empty success when it returns null.</summary>
    private static async Task<Run> RunWith(Func<int, HttpResponseMessage?> script)
    {
        var run = new Run();
        using var inventory = D7.Inventory(Devices);
        using var hunting = D7.Http((request, _) =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return D7.Token();
            var n = Interlocked.Increment(ref run.Queries);
            return script(n) ?? D7.Json("{\"results\":[]}");
        });
        var service = new DefenderEndpointDeviceService(
            () => Task.FromResult<DefenderApiClient?>(D7.Client(inventory)),
            huntingClientFactory: () => Task.FromResult<DefenderApiClient?>(D7.Client(hunting, true)))
        {
            HuntingBatchSizeOverride = 1,
            Logger = run.Log,
            ThrottleDelayForTests = (wait, _) => { lock (run.Waits) run.Waits.Add(wait); return Task.CompletedTask; },
        };
        run.Result = await service.ListDevicesAsync();
        return run;
    }

    [Fact]
    public async Task AShort429_IsWaitedOutOnce_AndTheBatchIsAskedAgain()
    {
        var run = await RunWith(n => n == 1 ? Throttled(TimeSpan.FromSeconds(5)) : null);

        Assert.Equal([TimeSpan.FromSeconds(5)], run.Waits);
        Assert.Equal(1 + 2 * Devices.Length, run.Queries); // the refused one, then posture and observer for each
        Assert.Equal(0, run.Result!.DiscoveryFailedBatches);
        Assert.Equal(0, run.Result.DiscoveryUnattemptedBatches);
        Assert.Equal("", run.Result.EnrichmentStopReason);
    }

    [Fact]
    public async Task ASecond429_AfterTheWait_StopsTheLoop_AndReportsHowManyWereNotQueried()
    {
        var run = await RunWith(n => n <= 2 ? Throttled(TimeSpan.FromSeconds(5)) : null);

        Assert.Equal([TimeSpan.FromSeconds(5)], run.Waits);
        Assert.Equal(2, run.Queries);
        Assert.Equal(1, run.Result!.DiscoveryAttemptedBatches);
        Assert.Equal(2, run.Result.DiscoveryUnattemptedBatches);
        Assert.Contains("429", run.Result.EnrichmentStopReason, StringComparison.Ordinal);
        Assert.Contains("refused again after waiting", run.Result.EnrichmentStopReason, StringComparison.Ordinal);
        Assert.Contains("3 device(s) were not queried", run.Result.EnrichmentStopReason, StringComparison.Ordinal);
        Assert.Equal(DefenderDiscoveryEnrichmentState.Failed, run.Result.DiscoveryEnrichment);
        var stop = Assert.Single(run.Log.Warnings, w => w.Contains("stopped", StringComparison.Ordinal));
        Assert.Contains("3 device(s) were not queried", stop, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheWaitIsUsedOncePerRun_SoALater429StopsAtOnce()
    {
        // Batch 1's posture is refused and retried; batch 2's posture (query 4) is refused too.
        var run = await RunWith(n => n is 1 or 4 ? Throttled(TimeSpan.FromSeconds(5)) : null);

        Assert.Single(run.Waits);
        Assert.Equal(4, run.Queries);
        Assert.Equal(2, run.Result!.DiscoveryAttemptedBatches);
        Assert.Contains("2 device(s) were not queried", run.Result.EnrichmentStopReason, StringComparison.Ordinal);
        Assert.DoesNotContain("refused again", run.Result.EnrichmentStopReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(61)]
    public async Task A429WithNoRetryAfter_OrALongOne_StopsAtOnce(int? seconds)
    {
        var run = await RunWith(n => n == 1 ? Throttled(seconds is { } s ? TimeSpan.FromSeconds(s) : null) : null);

        Assert.Empty(run.Waits);
        Assert.Equal(1, run.Queries);
        Assert.Equal(2, run.Result!.DiscoveryUnattemptedBatches);
        Assert.Contains("3 device(s) were not queried", run.Result.EnrichmentStopReason, StringComparison.Ordinal);
        Assert.Single(run.Log.Warnings, w => w.Contains("stopped", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnObserver429_StopsTheLoopToo()
    {
        var run = await RunWith(n => n == 2 ? Throttled() : null);

        Assert.Equal(2, run.Queries);
        Assert.Equal(1, run.Result!.ObserverFailedBatches);
        Assert.Contains("3 device(s) were not queried", run.Result.EnrichmentStopReason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, DefenderDiscoveryReasons.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, DefenderDiscoveryReasons.Forbidden)]
    public async Task A401Or403_StillStopsAtOnce_WithItsOwnReason(HttpStatusCode status, string reason)
    {
        var run = await RunWith(n => n == 1 ? D7.Json("{}", status) : null);

        Assert.Empty(run.Waits);
        Assert.Equal(1, run.Queries);
        Assert.Equal(reason, run.Result!.EnrichmentStopReason);
    }

    [Fact]
    public void RetryAfter_IsReadAsSecondsOrAsADate_AndNeverNegative()
    {
        var delta = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        delta.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
        Assert.Equal(TimeSpan.FromSeconds(7), DefenderApiClient.RetryAfterOf(delta));

        var date = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        date.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(30));
        var wait = DefenderApiClient.RetryAfterOf(date)!.Value;
        Assert.InRange(wait, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(31));

        var past = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        past.Headers.RetryAfter = new RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(TimeSpan.Zero, DefenderApiClient.RetryAfterOf(past));

        Assert.Null(DefenderApiClient.RetryAfterOf(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
    }

    internal sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Message)> _entries = new();

        public IReadOnlyList<string> Warnings => _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Message).ToList();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue((logLevel, formatter(state, exception)));
    }
}
