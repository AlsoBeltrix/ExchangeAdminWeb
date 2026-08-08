using System.Reflection;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards that Mailbox/Calendar bulk and on-prem paths honour the protected-principal servicer
/// grant, and that the on-prem write re-checks protection after its confirmation dialog (pps-1).
/// </summary>
/// <remarks>
/// Two defects, one module pair.
///
/// The bulk CSV path called the back-compat `ValidateTargetMailboxAsync` overload, which by
/// construction never services - so a servicer was allowed one row at a time through the single
/// form and refused for the same mailbox in a CSV. Bulk is where the grant is most useful, and it
/// was the path that could not use it.
///
/// The on-prem path validated protection before showing a confirmation prompt, then wrote after the
/// operator confirmed, with no fresh check. A confirmation dialog is an unbounded pause, and the
/// Constitution requires the check immediately before the write.
///
/// The bulk guard is REFLECTION over the compiled signature; the on-prem guard is source-level,
/// because it lives in a `.razor` handler and no bUnit harness exists. Neither is behavioural
/// coverage of a real write - stated so nobody reads them as more than they are.
/// </remarks>
public sealed class BulkAndOnPremServicingTests
{
    [Theory]
    [InlineData(typeof(MailboxPermissionService), "ProcessMailboxPermissionsCsvAsync")]
    [InlineData(typeof(CalendarPermissionService), "ProcessCalendarPermissionsCsvAsync")]
    public void TheBulkCsvEntryPoint_TakesTheActingPrincipal(Type serviceType, string methodName)
    {
        // Without a principal parameter the method CANNOT service, whatever its body does: there is
        // nothing to evaluate the grant against, and the only overload reachable is the one that
        // refuses by construction.
        var method = serviceType.GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);
        Assert.Contains(method!.GetParameters(),
            p => p.ParameterType == typeof(System.Security.Claims.ClaimsPrincipal));
    }

    [Theory]
    [InlineData("MailboxPermissionService.cs", "MailboxPermissions")]
    [InlineData("CalendarPermissionService.cs", "CalendarPermissions")]
    public void TheBulkLoop_UsesTheServicingOverload_WithItsOwnModuleId(string file, string moduleId)
    {
        // The signature can carry a principal that the body then ignores by calling the one-arg
        // overload - which is exactly the defect, and reflection cannot see it.
        //
        // Matched on the ARGUMENTS rather than on exact source text: asserting formatting makes a
        // guard that a reformat breaks and a real regression could slip past, which is noise
        // pretending to be coverage.
        var source = ReadService(file);

        var call = Regex.Match(
            source,
            @"ValidateTargetMailboxAsync\(\s*row\.Target,\s*actingUser,\s*ServicerModuleId\s*\)",
            RegexOptions.Singleline);

        Assert.True(call.Success,
            "the bulk loop must call the servicing overload with the acting principal and this "
            + "module's own id; the one-argument overload can never service");

        // Its OWN id. PermissionValidator serves three modules, so a borrowed id would let a grant
        // in one authorise the others.
        Assert.Contains($"ServicerModuleId = \"{moduleId}\"", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MailboxPermissionService.cs")]
    [InlineData("CalendarPermissionService.cs")]
    public void TheBulkRowAudit_CarriesTheServicedNote(string file)
    {
        // Per row, in extra. A serviced row SUCCEEDS and errorDetail is written as null on success,
        // so that channel would silently discard the record of who permitted it.
        var source = ReadService(file);

        Assert.Contains("ProtectedPrincipalServicing.Extra(servicedNote)", source, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MailboxPermissions.razor")]
    [InlineData("CalendarPermissions.razor")]
    public void TheOnPremExecutePath_RevalidatesProtectionBeforeTheWrite(string page)
    {
        // Anchored inside ExecuteOnPrem rather than the whole page: the single-submit handler
        // validates too, and a file-wide match would pass on that while the on-prem write stayed
        // unchecked - which is precisely the state this finding describes.
        var body = ExecuteOnPremBody(ReadPage(page));

        Assert.Contains("ValidateTargetMailboxAsync", body, StringComparison.Ordinal);
        Assert.Contains("ServicerModuleId", body, StringComparison.Ordinal);

        // And the check must refuse, not merely run.
        Assert.Contains("onPremValidation.Error is not null", body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("MailboxPermissions.razor")]
    [InlineData("CalendarPermissions.razor")]
    public void TheOnPremAudit_CarriesTheServicedNote(string page)
    {
        var body = ExecuteOnPremBody(ReadPage(page));

        Assert.Contains("ProtectedPrincipalServicing.Extra", body, StringComparison.Ordinal);
    }

    // ---- harness ------------------------------------------------------------------------------

    /// <summary>The ExecuteOnPrem handler body, so a match elsewhere on the page cannot satisfy a guard.</summary>
    private static string ExecuteOnPremBody(string source)
    {
        var start = source.IndexOf("private async Task ExecuteOnPrem()", StringComparison.Ordinal);
        Assert.True(start >= 0, "ExecuteOnPrem handler not found");

        // Up to the next method declaration at the same indent.
        var rest = source[start..];
        var next = Regex.Match(rest[1..], @"\n    private (async )?(Task|void|bool|string)");

        return next.Success ? rest[..next.Index] : rest;
    }

    private static string ReadService(string fileName) =>
        File.ReadAllText(Path.Combine(RepoSubdirectory("Services"), fileName));

    private static string ReadPage(string fileName) =>
        File.ReadAllText(Path.Combine(RepoSubdirectory(Path.Combine("Components", "Pages")), fileName));

    private static string RepoSubdirectory(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (Directory.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException($"Could not locate {relative} from the test base directory.");
    }
}
