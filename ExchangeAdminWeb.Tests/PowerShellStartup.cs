using System.Management.Automation;
using System.Management.Automation.Runspaces;

[assembly: AssemblyFixture(typeof(ExchangeAdminWeb.Tests.PowerShellStartup))]

namespace ExchangeAdminWeb.Tests;

public sealed class PowerShellStartup
{
    private const int Attempts = 3;
    private static int _started;

    public PowerShellStartup()
    {
        for (var attempt = 0; attempt < Attempts && !Started; attempt++)
        {
            try
            {
                Open(InitialSessionState.CreateDefault());
                Open(InitialSessionState.CreateDefault2());
                Volatile.Write(ref _started, 1);
            }
            catch (Exception)
            {
            }
        }
    }

    public static bool Started => Volatile.Read(ref _started) == 1;

    private static void Open(InitialSessionState state)
    {
        using var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.Open();
        using var shell = PowerShell.Create();
        shell.Runspace = runspace;
        shell.AddScript("1").Invoke();
    }
}
