using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

public class ConnectionErrorTrackerTests
{
    [Fact]
    public void Tracker_DefaultState_NoConnectionError()
    {
        var tracker = new ExchangeServiceBase.ConnectionErrorTracker();
        Assert.False(tracker.HasConnectionError);
    }

    [Fact]
    public void Tracker_SetError_Persists()
    {
        var tracker = new ExchangeServiceBase.ConnectionErrorTracker();
        tracker.HasConnectionError = true;
        Assert.True(tracker.HasConnectionError);
    }

    [Fact]
    public async Task Tracker_SurvivesAsyncBoundary()
    {
        var tracker = new ExchangeServiceBase.ConnectionErrorTracker();

        await Task.Run(async () =>
        {
            tracker.HasConnectionError = true;
            await Task.Yield();
        });

        Assert.True(tracker.HasConnectionError);
    }

    [Fact]
    public async Task Tracker_SurvivesMultipleAwaits()
    {
        var tracker = new ExchangeServiceBase.ConnectionErrorTracker();

        await Task.Run(async () =>
        {
            await Task.Delay(1);
            tracker.HasConnectionError = true;
            await Task.Delay(1);
        });

        Assert.True(tracker.HasConnectionError);
    }

    [Fact]
    public async Task Tracker_NotSharedAcrossOperations()
    {
        var tracker1 = new ExchangeServiceBase.ConnectionErrorTracker();
        var tracker2 = new ExchangeServiceBase.ConnectionErrorTracker();

        await Task.Run(() => tracker1.HasConnectionError = true);

        Assert.True(tracker1.HasConnectionError);
        Assert.False(tracker2.HasConnectionError);
    }

    [Fact]
    public async Task Tracker_CapturedByLambda_VisibleAfterTaskRun()
    {
        var tracker = new ExchangeServiceBase.ConnectionErrorTracker();
        bool discard = false;

        await Task.Run(async () =>
        {
            await Task.Yield();
            tracker.HasConnectionError = true;
            await Task.Yield();
        });

        discard = tracker.HasConnectionError;
        Assert.True(discard);
    }
}
