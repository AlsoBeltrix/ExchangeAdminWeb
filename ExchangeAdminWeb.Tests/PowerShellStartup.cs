using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Services;

[assembly: AssemblyFixture(typeof(ExchangeAdminWeb.Tests.PowerShellStartup))]

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Starts PowerShell once, alone, before any test in this assembly runs (aud-a26p).
/// </summary>
/// <remarks>
/// <para>Two test classes opening their first sessions at the same moment tripped PowerShell's start-up race ("An item
/// with the same key has already been added. Key: Alias") in a filtered run. xUnit v3 runs classes in parallel, and
/// dozens of them open sessions, directly or through the services they drive. A shared collection was the other way: it
/// serialises only the classes that carry it, so it has to be on every one that opens a session - indirectly included,
/// which no list stays true to - it cannot help a test that opens two at once, and it would run tens of slow classes one
/// after another for the whole suite. The failure was the process's first sessions, so they are started here, before any
/// class can open one, as an assembly fixture: xUnit creates it before the first test, whether or not a class asks for
/// it.</para>
/// <para>Both default session shapes the suite uses are opened: <c>CreateDefault</c> builds its session from the
/// built-in snap-ins and <c>CreateDefault2</c> from modules, so each has a first time of its own. A start that fails is
/// tried again and never thrown: an assembly fixture that throws fails every test, which is worse than the flake it
/// guards against. <see cref="Outcome"/> keeps what happened - whether it took, and when it did not, the shape that failed
/// and the last exception - and PowerShellStartupTests reports it.</para>
/// <para>The attempts are the app's own warm-up loop, <see cref="PowerShellWarmUp.Start"/> (aud-a28w), given this suite's
/// two shapes and an open that also runs a command in each. A start that failed used to be swallowed whole, leaving the
/// pin to say only that it had failed.</para>
/// </remarks>
public sealed class PowerShellStartup
{
    private const int Attempts = 3;
    private static PowerShellStartOutcome? _outcome;

    public PowerShellStartup() =>
        Volatile.Write(ref _outcome, PowerShellWarmUp.Start(
            [("CreateDefault", InitialSessionState.CreateDefault), ("CreateDefault2", InitialSessionState.CreateDefault2)],
            Attempts,
            Open));

    /// <summary>What the start did; null until the fixture has run - and so when it is not registered.</summary>
    public static PowerShellStartOutcome? Outcome => Volatile.Read(ref _outcome);

    /// <summary>Whether PowerShell was started before the tests ran.</summary>
    public static bool Started => Outcome?.Started == true;

    private static void Open(InitialSessionState state)
    {
        using var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.Open();
        using var shell = PowerShell.Create();
        shell.Runspace = runspace;
        shell.AddScript("1").Invoke();
    }
}
