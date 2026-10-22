using System.Diagnostics;
using System.Management.Automation.Runspaces;

namespace ExchangeAdminWeb.Services;

public static class PowerShellWarmUp
{
    internal const int Attempts = 3;

    internal static readonly IReadOnlyList<(string Name, Func<InitialSessionState> Create)> AppShapes =
        [("CreateDefault", InitialSessionState.CreateDefault)];

    public static Task<PowerShellStartOutcome> StartInBackground(ILogger logger) =>
        StartInBackground(logger, AppShapes, Open);

    internal static Task<PowerShellStartOutcome> StartInBackground(
        ILogger logger, IReadOnlyList<(string Name, Func<InitialSessionState> Create)> shapes, Action<InitialSessionState> open)
    {
        ArgumentNullException.ThrowIfNull(logger);
        return Task.Run(() => Run(logger, shapes, open));
    }

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
                failedShape = shape;
                lastFailure = ex;
            }
        }

        return new PowerShellStartOutcome(false, attempts, failedShape, lastFailure);
    }

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

    private static void Open(InitialSessionState state)
    {
        using var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.Open();
    }
}

public sealed record PowerShellStartOutcome(bool Started, int Attempts, string? FailedShape, Exception? LastFailure);
