using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the Defender device table's paging: the browse view draws a bounded window, the
/// export still carries the complete set, and a new result cannot leave the view stranded
/// past its end.
/// </summary>
/// <remarks>
/// <para>
/// The defect these exist for shipped: the table rendered every device the tenant returned,
/// this tenant exceeds the old 20,000 ceiling, and the circuit died so nothing could be
/// scrolled. The owner rejected virtualised scrolling and approved portal-style paging with
/// the complete set kept for CSV.
/// </para>
/// <para>
/// Source-level guards. They prove the shape of the markup and the handler, not that a browser
/// paints them.
/// </para>
/// </remarks>
public class DefenderDevicePagingTests
{
    private const string Page = "DefenderEndpointDevices.razor";

    [Fact]
    public void TheTableBodyRendersThePagedWindowRatherThanEveryDevice()
    {
        var source = StripComments(ReadPage());

        Assert.Contains("@foreach (var device in PagedDevices)", source, StringComparison.Ordinal);

        // The exact expression that killed the circuit. Pinned as a negative so a revert, or a
        // well-meant "just show them all", fails here rather than in production.
        Assert.DoesNotContain("foreach (var device in result.Devices)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExportFollowsTheFilterAndNeverThePage()
    {
        // This is the half that makes the paging honest, and the two halves pull opposite ways.
        //
        // The PAGE must never reach the export: it is a property of the viewport, not of the
        // question, and a CSV cut to 50 rows silently answers something nobody asked - in a file
        // that leaves the app, where no banner travels with it to explain the truncation.
        //
        // The FILTER must reach it. Once a location filter exists, "the complete matching set"
        // includes that filter, and an export ignoring it would disagree with the screen the
        // operator is looking at. Owner ruling 2026-10-12, .agents/decisions.md.
        var source = StripComments(ReadPage());

        var exportable = Member(source, "IReadOnlyList<DefenderDevice> ExportableDevices");
        Assert.Contains("FilteredDevices", exportable, StringComparison.Ordinal);
        Assert.DoesNotContain("PagedDevices", exportable, StringComparison.Ordinal);
        Assert.DoesNotContain("DevicePageSize", exportable, StringComparison.Ordinal);
    }

    [Fact]
    public void ADeviceWhoseEnrichmentFailedSurvivesTheLocationFilter()
    {
        // The filter reads a column that a failed enrichment batch never populated. Judging such
        // a device against it would drop it from the result - invisibly, and looking exactly
        // like a clean negative - which hides precisely the machines someone is hunting for.
        var filtered = Member(StripComments(ReadPage()), "IReadOnlyList<DefenderDevice> FilteredDevices");

        Assert.Contains("EnrichmentUnavailable", filtered, StringComparison.Ordinal);
        Assert.Contains("RecentlySeenBy", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangingTheLocationFilterReturnsToPageOne()
    {
        // Filtering from page 40 of an unfiltered list down to a four-page result would
        // otherwise leave the operator past the end, reading an empty table as "nothing
        // matched". Same trap the load path resets for, reached a different way.
        var source = StripComments(ReadPage());

        Assert.Contains("@bind:after=\"OnSeenByFilterChanged\"", source, StringComparison.Ordinal);

        var handler = Member(source, "void OnSeenByFilterChanged()");
        Assert.Contains("devicePageIndex = 0;", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void ANewResultResetsTheViewToTheFirstPage()
    {
        // A stale index from a wider search can sit past the end of a narrower one, which draws
        // an empty table under a header that says there are devices - it reads as "the filter
        // matched nothing", which is a wrong answer rather than a visible bug.
        var source = StripComments(ReadPage());

        var assignment = source.IndexOf("result = loaded;", StringComparison.Ordinal);
        Assert.True(assignment >= 0, "LoadAsync must still assign the result.");

        // Within the few lines after the assignment, not merely somewhere in the method.
        var window = source[assignment..Math.Min(assignment + 400, source.Length)];
        Assert.Contains("devicePageIndex = 0;", window, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePagedWindowIsClampedSoAStaleIndexCannotBlankTheTable()
    {
        var paged = Member(StripComments(ReadPage()), "IReadOnlyList<DefenderDevice> PagedDevices");

        Assert.Contains("Math.Clamp(devicePageIndex", paged, StringComparison.Ordinal);
        Assert.Contains("DevicePageSize", paged, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageCountIsNeverZeroSoThePagerCannotReadPageOneOfZero()
    {
        var count = Member(StripComments(ReadPage()), "int DevicePageCount");

        // The empty and null cases both return 1 rather than falling through to a division that
        // yields 0. "Page 1 of 0" is the tell that this regressed.
        Assert.Contains("? 1", count, StringComparison.Ordinal);
    }

    // ---- helpers ----

    /// <summary>The text of one expression-bodied or block-bodied member, by DECLARATION.</summary>
    /// <remarks>
    /// Matched on the declaration including its return type, never on the bare name. The bare
    /// name hits the MARKUP first - ExportableDevices appears in the export button's disabled
    /// expression and PagedDevices in the table's foreach, both far above their declarations -
    /// and the helper would then assert against a fragment of a razor attribute.
    /// </remarks>
    private static string Member(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{declaration} was not found on the page.");

        // Which KIND of member decides where it ends: an expression-bodied one runs to its
        // semicolon, a block-bodied one is brace walked.
        //
        // Deciding that on "whichever of ; and { comes first" is wrong, and wrong in a way that
        // passes four of these five tests. ExportableDevices is expression-bodied but its body
        // is a property pattern - `result is { Outcome: ... } complete ? ... : []` - so a brace
        // arrives before the semicolon and the brace walk returns only the pattern, cutting the
        // member off before the thing under test. The marker that actually distinguishes them
        // is `=>` before the first brace.
        var semicolon = source.IndexOf(';', start);
        var brace = source.IndexOf('{', start);
        var arrow = source.IndexOf("=>", start, StringComparison.Ordinal);

        var expressionBodied = arrow >= 0 && (brace < 0 || arrow < brace);
        if (expressionBodied || brace < 0)
        {
            Assert.True(semicolon >= 0, $"{declaration} is expression-bodied but unterminated.");
            return source[start..(semicolon + 1)];
        }

        var depth = 0;
        for (var i = brace; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[start..(i + 1)];
            }
        }

        throw new InvalidOperationException($"{declaration}'s body is unbalanced.");
    }

    private static string ReadPage() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "Components", "Pages", Page));

    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var withoutRazorComments = Regex.Replace(withoutBlocks, @"@\*.*?\*@", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutRazorComments, @"//[^\n]*", " ");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ExchangeAdminWeb.csproj")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test base directory.");
    }
}
