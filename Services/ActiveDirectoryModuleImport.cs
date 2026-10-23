using System.Management.Automation;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Imports the ActiveDirectory module into a directory session, as its first command (psl-7).
/// </summary>
/// <remarks>
/// aud-a2r's shape, first written as a private copy in ConferenceRoomService, which now imports through this too. Shared
/// by every service whose import a later check on the same session could read: psl-7's five, and since psl-8 Conference
/// Rooms, Emergency Disable and Self-Service Groups. The other imports were audited by psl-8 and left as they are, each
/// with no check that could take a record of theirs (its record lists them). The import's error is the one it throws,
/// named by whatever names its caller's steps. Under -ErrorAction Stop the cmdlet's own failure is thrown from Invoke
/// and leaves no record, so a HadErrors check after it never saw that error: it saw only a record written past the
/// preference, and failed an import whose module had loaded (AccountLockoutRemediationService,
/// ADDirectorySearchService). That makes Stop
/// load-bearing - without it a module that cannot load is a record, cleared below unread, and the failure surfaces
/// at the next step instead. The finally clears the error stream as well as the commands: three services cleared the
/// commands only, and a record the import left stayed in the stream, where the next read's own HadErrors check -
/// true while any record remains (psl-2) - took it for that read's error. Measured with stand-in functions in an
/// in-process runspace (psl-7's record). *Assumption, not probed*: the real module's import writes no error past
/// Stop; were one written, the session would go on with the module loaded.
/// </remarks>
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
