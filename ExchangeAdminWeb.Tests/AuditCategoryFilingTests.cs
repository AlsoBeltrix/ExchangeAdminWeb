using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the audit-category-misfiling fix (ProdReadiness review finding
/// "[audit] EmergencyDisable, Comms10k, and LicensingUpdates write audit events under
/// wrong categories via borrowed audit methods"). Those modules used to call
/// category-hardcoding helpers (LogMigrationAction → "MigrationAction",
/// LogLookupAction → "Lookup"), so a security-critical EmergencyDisable filed as a
/// migration and license mutations filed as read-only lookups, and a compliance query
/// by category missed them. They must use the generic LogModuleAction with their own
/// module category, and the AdminEventLog filter must offer those categories.
///
/// These are source-text guards because the call sites run live AD / Blazor circuit
/// code with no injection seam, and the plan (§2) forbids a testability refactor outside
/// a named finding. The scan walks up from the test base directory to the repo source,
/// mirroring ModuleCatalogTests.GetPagesDirectory.
/// </summary>
public class AuditCategoryFilingTests
{
    private static string FindRepoFile(params string[] relativeSegments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(new[] { dir.FullName }.Concat(relativeSegments).ToArray());
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"Could not locate {string.Join('/', relativeSegments)} from test base directory.");
    }

    [Fact]
    public void EmergencyDisable_AuditsUnderOwnCategory_NotBorrowedMigrationAction()
    {
        var text = File.ReadAllText(FindRepoFile("Services", "EmergencyDisableService.cs"));

        Assert.DoesNotContain("LogMigrationAction", text);
        // Generic helper, with EmergencyDisable as both action and category.
        Assert.Matches(
            new Regex(@"LogModuleAction\(\s*[^;]*?""EmergencyDisable""\s*,\s*""EmergencyDisable""", RegexOptions.Singleline),
            text);
    }

    [Fact]
    public void LicensingUpdates_AuditsMutationsUnderOwnCategory_NotBorrowedLookup()
    {
        var text = File.ReadAllText(FindRepoFile("Services", "LicensingUpdatesService.cs"));

        // LogLookupAction hardcodes category "Lookup" — an AD attribute WRITE must not
        // be filed as a read-only lookup.
        Assert.DoesNotContain("LogLookupAction", text);
        Assert.Matches(
            new Regex(@"LogModuleAction\(\s*[^;]*?""LicensingUpdates_Update""\s*,\s*""LicensingUpdates""", RegexOptions.Singleline),
            text);
    }

    [Fact]
    public void Comms10k_AuditsUnderOwnCategory_NotBorrowedMigrationAction()
    {
        var text = File.ReadAllText(FindRepoFile("Components", "Pages", "Comms10k.razor"));

        Assert.DoesNotContain("LogMigrationAction", text);
        // Every Comms10k_Replace audit routes through LogModuleAction with the Comms10k
        // category (6 call sites: blocked/attempted/committed paths).
        var moduleCalls = Regex.Matches(
            text,
            @"LogModuleAction\(\s*[^;]*?""Comms10k_Replace""\s*,\s*""Comms10k""",
            RegexOptions.Singleline);
        Assert.Equal(6, moduleCalls.Count);
    }

    [Fact]
    public void AdminEventLog_CategoryFilter_OffersTheFormerlyMisfiledModules()
    {
        var text = File.ReadAllText(FindRepoFile("Components", "Pages", "AdminEventLog.razor"));

        // Once the events file under their own categories, an operator must be able to
        // filter to them (previously hidden inside MigrationAction / Lookup).
        Assert.Contains("<option value=\"EmergencyDisable\">", text);
        Assert.Contains("<option value=\"LicensingUpdates\">", text);
        Assert.Contains("<option value=\"Comms10k\">", text);
    }
}
