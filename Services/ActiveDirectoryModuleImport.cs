using System.Management.Automation;

namespace ExchangeAdminWeb.Services;

internal static class ActiveDirectoryModuleImport
{
    internal static void Import(PowerShell ps)
    {
        ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
        try
        {
            ps.Invoke();
        }
        finally
        {
            ps.Commands.Clear();
            ps.Streams.Error.Clear();
        }
    }
}
