using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

public class DefenderSiteDeadlineTests
{
    [Theory]
    [InlineData("opening")]
    [InlineData("invoking")]
    [InlineData("cleanup")]
    public async Task StalledNativeLifetimeReturnsInventoryAndKeepsAdmissionThroughCleanup(string phase)
    {
        var clock = new D7Clock();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var factories = 0;
        var rootCommands = 0;
        var subnetCommands = 0;
        var stops = 0;
        var scheduler = new RecordingScheduler();
        void Block()
        {
            entered.TrySetResult();
            release.Wait();
        }
        Action root = () =>
        {
            Interlocked.Increment(ref rootCommands);
            if (phase == "invoking")
                Block();
        };
        Action subnet = () => Interlocked.Increment(ref subnetCommands);
        var directory = new AdSiteDirectory(() =>
        {
            if (Interlocked.Increment(ref factories) != 1)
                return new D7Commands(D7.RootOrSubnet);
            ISectionAccessDirectoryCommands native = phase == "cleanup"
                ? new D7Commands(D7.RootOrSubnet)
                : new PowerShellDirectoryCommands(() =>
                {
                    if (phase == "opening")
                        Block();
                    // Local, synthetic PowerShell functions only: no RSAT import or AD call.
                    var state = InitialSessionState.CreateDefault();
                    state.Variables.Add(new SessionStateVariableEntry("rootGate", root, "fixture"));
                    state.Variables.Add(new SessionStateVariableEntry("subnetGate", subnet, "fixture"));
                    state.Commands.Add(new SessionStateFunctionEntry("Get-ADRootDSE",
                        "$rootGate.Invoke(); [pscustomobject]@{configurationNamingContext='CN=Configuration,DC=example,DC=test'}"));
                    state.Commands.Add(new SessionStateFunctionEntry("Get-ADObject", "$subnetGate.Invoke()"));
                    var runspace = RunspaceFactory.CreateRunspace(state);
                    runspace.Open();
                    return runspace;
                });
            return new TrackedCommands(native, () => Interlocked.Increment(ref stops), () =>
            {
                if (phase == "cleanup")
                    Block();
            }, () => disposed.TrySetResult());
        })
        { WorkerScheduler = scheduler };
        using var inventory = D7.Inventory("a");
        using var hunting = D7.Http((request, _) => request.RequestUri!.Host == "login.microsoftonline.com"
            ? D7.Token() : D7.Json("{\"results\":[]}"));
        var service = D7.Service(inventory, hunting, directory, clock);

        try
        {
            var firstTask = service.ListDevicesAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            clock.Advance(TimeSpan.FromMinutes(4));
            var first = await firstTask;
            Assert.Equal(DefenderDeviceListOutcome.Complete, first.Outcome);
            Assert.False(first.SiteMapRead);
            Assert.Equal(DefenderDiscoveryReasons.DeadlineReached, first.EnrichmentStopReason);
            Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, first.Devices[0].Site);
            Assert.False(disposed.Task.IsCompleted);
            Assert.Equal(phase == "cleanup" ? 0 : 1, stops);

            var secondTask = service.ListDevicesAsync();
            // Admission is awaited, so another full budget expires without another worker.
            Assert.Equal(1, factories);
            Assert.Equal(1, scheduler.Submitted);
            clock.Advance(TimeSpan.FromMinutes(4));
            var second = await secondTask;
            Assert.False(second.SiteMapRead);
            Assert.Equal(1, factories);
            Assert.Equal(1, scheduler.Submitted);
            Assert.Equal(2, clock.TimerCount);

            release.Set();
            await disposed.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
            Assert.Equal(phase == "invoking" ? 1 : 0, rootCommands);
            Assert.Equal(0, subnetCommands);
            Assert.Equal(DefenderEndpointDeviceService.EnrichmentUnavailable, first.Devices[0].Site);
            var third = await directory.ReadSubnetsAsync(TestContext.Current.CancellationToken);
            Assert.Equal("Site", Assert.Single(third).SiteName);
            Assert.Equal(2, factories);
        }
        finally
        {
            release.Set();
            await disposed.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        }
    }

    // Queue admission is recorded synchronously, before ThreadPool execution. This makes the
    // no-second-worker assertion independent of whether the second worker wins a scheduling race.
    private sealed class RecordingScheduler : TaskScheduler
    {
        internal int Submitted;
        protected override void QueueTask(Task task)
        {
            Interlocked.Increment(ref Submitted);
            ThreadPool.QueueUserWorkItem(_ => TryExecuteTask(task));
        }
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override IEnumerable<Task> GetScheduledTasks() => [];
    }

    private sealed class TrackedCommands(ISectionAccessDirectoryCommands inner, Action stop, Action beforeDispose, Action afterDispose)
        : ICancellableDirectoryCommands
    {
        public DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters) => inner.Invoke(command, parameters);
        public DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters, CancellationToken cancellationToken) =>
            inner is ICancellableDirectoryCommands cancellable ? cancellable.Invoke(command, parameters, cancellationToken) : inner.Invoke(command, parameters);
        public string? TranslateSidToNTAccount(string sid) => inner.TranslateSidToNTAccount(sid);
        public void RequestStop()
        {
            stop();
            if (inner is ICancellableDirectoryCommands cancellable)
                cancellable.RequestStop();
        }
        public void Dispose()
        {
            beforeDispose();
            try { inner.Dispose(); }
            finally { afterDispose(); }
        }
    }
}
