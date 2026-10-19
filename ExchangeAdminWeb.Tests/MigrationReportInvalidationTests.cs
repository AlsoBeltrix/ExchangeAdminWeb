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

        // Review of A31, finding 4. The earlier version skipped any match whose argument text
        // began with a type, calling it "the declaration" - so an unused local function,
        // void InvalidateStoredReports(string? ignored) { }, satisfied every check while the
        // real call was gone. A declaration is now identified by its return type immediately
        // before the name, and anything else must be a call with a usable argument.
        var matches = System.Text.RegularExpressions.Regex.Matches(
            code, @"(?<lead>\b\w[\w<>?,\s]*\s)?InvalidateStoredReports\((?<arg>[^)]*)\)");

        var declarations = 0;
        var callSites = 0;

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var arg = match.Groups["arg"].Value.Trim();
            var lead = match.Groups["lead"].Value.Trim();

            // A return type before the name means this is a declaration, not a call.
            if (lead is "void" or "Task" or "async Task" or "private void")
            {
                declarations++;
                Assert.Equal("string? batchName", arg);
                continue;
            }

            callSites++;
            Assert.False(arg.Length == 0,
                "An invalidation with no batch sweeps nothing and leaves stale reports served.");
            Assert.False(arg is "null" or "\"\"",
                "An invalidation naming no batch sweeps nothing and leaves stale reports served.");
        }

        Assert.Equal(1, declarations);
        Assert.True(callSites >= 7,
            $"Expected at least seven invalidation call sites; found {callSites}. A deleted call "
            + "means a path that no longer discards the reports its reload invalidates.");
    }

    [Fact]
    public void EveryAcceptedBatchInABulkActionHasItsReportsDiscarded()
    {
        // Review of A31, finding 1. Leaving this to the reload that follows was a regression:
        // when the accepted batch was the only selected one, the open batch became null and the
        // invalidation swept nothing, so another operator could still reopen the previous
        // migration's report. The accepted names are known here, so they are used here.
        var code = PageCode();

        var deselect = code.IndexOf("selectedBatches.Remove(name);", StringComparison.Ordinal);
        Assert.True(deselect > 0, "The accepted-batch deselect loop is gone - this guard is stale.");

        Assert.Contains("ReportStore.DeleteForBatch(name);", code[deselect..], StringComparison.Ordinal);
    }
}
