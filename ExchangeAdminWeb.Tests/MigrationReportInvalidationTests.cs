namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A31: the Migration page discarded stored reports with
/// <c>ReportStore.DeleteAll()</c>, and the store is one shared directory - so any operator
/// expanding any batch deleted every other operator's reports instance-wide.
/// </summary>
/// <remarks>
/// Source scans, and the limit is real: the behaviour they stand in for -
/// <c>DeleteForBatch</c> actually leaving other batches alone - is covered against a real
/// temporary directory in <see cref="MigrationReportStoreTests"/>. What cannot be driven from
/// a test is the page's seven call sites, because the page is a Blazor component with no
/// harness here. So these pin the two properties that would silently regress: that the page
/// never reaches for the instance-wide delete, and that no call site drops the batch argument.
///
/// Both read <see cref="ProgressScan.CodeView"/>, which blanks comments and string literals -
/// the comments around this method name <c>DeleteAll</c> and would satisfy a raw-text scan.
/// </remarks>
public class MigrationReportInvalidationTests
{
    private static string PageCode() =>
        ProgressScan.CodeView(File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "Migration.razor")));

    [Fact]
    public void ThePageNeverDiscardsEveryOperatorsReports()
    {
        // DeleteAll is still on the store for a caller that genuinely wants to wipe it. The
        // Migration page is not that caller, and was the only one.
        Assert.DoesNotContain("DeleteAll(", PageCode(), StringComparison.Ordinal);
    }

    [Fact]
    public void EveryInvalidationNamesTheBatchItIsInvalidating()
    {
        // A call with no argument would not compile today, but the parameter is nullable so a
        // future site could pass null, and the store treats a blank name as "delete nothing".
        // That would silently leave the stale reports R24b exists to remove - the guard would
        // still be green and the bug would be back. Null or empty is refused here instead.
        var code = PageCode();

        var calls = System.Text.RegularExpressions.Regex.Matches(
            code, @"InvalidateStoredReports\(([^)]*)\)");

        // One declaration plus its call sites; the declaration is matched too and skipped below.
        Assert.True(calls.Count >= 8, $"Expected the declaration and its call sites; found {calls.Count}.");

        foreach (System.Text.RegularExpressions.Match call in calls)
        {
            var arg = call.Groups[1].Value.Trim();
            if (arg.StartsWith("string?", StringComparison.Ordinal))
                continue;   // the declaration

            Assert.False(arg.Length == 0,
                "An invalidation with no batch sweeps nothing and leaves stale reports served.");
            Assert.False(arg is "null" or "\"\"",
                "An invalidation naming no batch sweeps nothing and leaves stale reports served.");
        }
    }
}
