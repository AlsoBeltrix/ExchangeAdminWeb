using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a26p: two test classes opening their first PowerShell sessions at the same moment tripped PowerShell's start-up
/// race ("An item with the same key has already been added. Key: Alias") in a filtered run. PowerShell is now started
/// once, alone, by <see cref="PowerShellStartup"/> before any test in this assembly runs.
/// </summary>
/// <remarks>
/// The race has not been reproduced on demand, in this suite or in a cold process racing sixteen first sessions, so this
/// cannot fail on the race itself. It pins the two things that can be known: that the one-time start ran before any test,
/// and that two sessions opened at once through <see cref="AttributeEditorHarness"/> - the directory session the failure
/// was seen in - both start.
/// </remarks>
public sealed class PowerShellStartupTests
{
    [Fact]
    public async Task PowerShellIsStartedBeforeAnyTest_AndTwoSessionsOpenedAtOnceThroughTheHarness_BothStart()
    {
        Assert.True(PowerShellStartup.Started,
            "PowerShell was not started before the tests ran: the assembly fixture is not registered, or its start failed");

        var cancel = TestContext.Current.CancellationToken;
        using var harness = new AttributeEditorHarness();
        using var ready = new Barrier(2);
        var sessions = Enumerable.Range(0, 2)
            .Select(_ => Task.Factory.StartNew(() =>
            {
                var state = harness.Ad.CreateSessionState();
                ready.SignalAndWait(cancel);
                using var runspace = RunspaceFactory.CreateRunspace(state);
                runspace.Open();
                using var shell = PowerShell.Create();
                shell.Runspace = runspace;
                return shell.AddScript("1 + 1").Invoke().Single().BaseObject;
            }, cancel, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();

        var results = await Task.WhenAll(sessions).WaitAsync(TimeSpan.FromMinutes(2), cancel);
        Assert.All(results, result => Assert.Equal(2, result));
    }
}
