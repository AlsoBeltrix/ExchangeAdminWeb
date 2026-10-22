using System.Diagnostics;
using System.Management.Automation.Runspaces;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Starts PowerShell once, in the background, as the app starts (aud-a28w), so that the first operator sessions after a start
/// do not open PowerShell's first session themselves - two of them at once being the shape of the start-up race the test
/// suite guards against (aud-a26p).
/// </summary>
/// <remarks>
/// <para>Nothing serialises the app's first opens. Every session is <c>InitialSessionState.CreateDefault()</c>, opened by the
/// service that needs it, and each throttle is that service's own - two at a time for most, five slots for the Exchange
/// Online pool - so two operators acting at once after a start open their first sessions concurrently. The aud-a28w record
/// has the survey.</para>
/// <para>It opens one runspace of the one shape the app uses and disposes it: no module import, no command, no connection.
/// Three attempts; never thrown; one log line. Program.cs calls it from its startup pass, after the host is built and before
/// it runs, and it returns at once, so start-up never waits for it. A one-shot call, not a hosted service: the app's posture
/// since 2026-06-17 is no background worker, and BulkJobService starts the same way.</para>
/// <para>The race was never reproduced and its cause is not established. If it is a process's first start, this closes the
/// window once it completes; if it is a runspace's own parallel bind, it narrows nothing.</para>
/// </remarks>
public static class PowerShellWarmUp
{
    internal const int Attempts = 3;

    // The one shape the app opens: CreateDefault, the runspace a bare PowerShell.Create() opens for itself included (measured:
    // the same 593 commands). The suite also opens CreateDefault2, and its fixture warms that one too.
    internal static readonly IReadOnlyList<(string Name, Func<InitialSessionState> Create)> AppShapes =
        [("CreateDefault", InitialSessionState.CreateDefault)];

    /// <summary>
    /// Starts the warm-up on the thread pool and returns its task at once; the task ends with what the start did, and the
    /// warm-up itself never throws. Program.cs does not wait for it.
    /// </summary>
    public static Task<PowerShellStartOutcome> StartInBackground(ILogger logger) =>
        StartInBackground(logger, AppShapes, Open);

    /// <summary>The warm-up over the given shapes and open: the seam the tests drive.</summary>
    internal static Task<PowerShellStartOutcome> StartInBackground(
        ILogger logger, IReadOnlyList<(string Name, Func<InitialSessionState> Create)> shapes, Action<InitialSessionState> open)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return Task.Run(() => Run(logger, shapes, open));
    }

    /// <summary>
    /// Opens each shape in turn, up to <paramref name="attempts"/> times, and never throws. A failure is kept - the shape
    /// that failed and its exception - and the next attempt opens every shape again; the last failure is what the outcome
    /// carries. Shared with the test suite's start-up fixture, which used to swallow the failure whole (aud-a28w, folded in).
    /// </summary>
    internal static PowerShellStartOutcome Start(
        IReadOnlyList<(string Name, Func<InitialSessionState> Create)> shapes, int attempts, Action<InitialSessionState> open)
    {
        string? failedShape = null;
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            string? shape = null;
            try
            {
                foreach (var (name, create) in shapes)
                {
                    shape = name;
                    open(create());
                }

                return new PowerShellStartOutcome(true, attempt, null, null);
            }
            catch (Exception ex)
            {
                // The start-up race itself, or anything else: kept, and tried again. Never thrown - a warm-up that threw
                // would be worse than the race it narrows.
                failedShape = shape;
                lastFailure = ex;
            }
        }

        return new PowerShellStartOutcome(false, attempts, failedShape, lastFailure);
    }

    // One line either way: how long it took, or what failed and why, with the exception.
    private static PowerShellStartOutcome Run(
        ILogger logger, IReadOnlyList<(string Name, Func<InitialSessionState> Create)> shapes, Action<InitialSessionState> open)
    {
        var clock = Stopwatch.StartNew();
        var outcome = Start(shapes, Attempts, open);
        if (outcome.Started)
        {
            logger.LogInformation(
                "PowerShell started ahead of the first sessions in {ElapsedMs} ms ({Shapes}, attempt {Attempt} of {Attempts})",
                clock.ElapsedMilliseconds, string.Join(", ", shapes.Select(s => s.Name)), outcome.Attempts, Attempts);
        }
        else
        {
            logger.LogWarning(
                outcome.LastFailure,
                "PowerShell could not be started ahead of the first sessions ({Shape}, {Attempts} attempts); each session starts it as it opens, as before",
                outcome.FailedShape, outcome.Attempts);
        }

        return outcome;
    }

    // The app's open: the runspace, and nothing run in it.
    private static void Open(InitialSessionState state)
    {
        using var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.Open();
    }
}

/// <summary>
/// What a start did: whether every shape opened and on which attempt, or, when none did, how many attempts were made, the shape
/// that failed last and its exception.
/// </summary>
public sealed record PowerShellStartOutcome(bool Started, int Attempts, string? FailedShape, Exception? LastFailure);
