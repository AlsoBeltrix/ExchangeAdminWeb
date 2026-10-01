using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Source-level tripwires for the app's layout height chain
/// (docs/AppLayoutAndScrolling-Plan.md).
/// </summary>
/// <remarks>
/// <para>
/// The defect these guard against was invisible to every other gate and shipped for months.
/// Nothing above <c>&lt;article&gt;</c> declared a height - <c>main</c> was <c>flex: 1</c> and
/// stopped - so a page that wanted to fill the remaining space had nothing to measure against
/// and <c>height: 100%</c> resolved to nothing. Every page that needed a full-height list
/// therefore wrote <c>100vh</c> minus a literal it had typed for the chrome above it. Nine
/// accumulated, no two alike, and each one is wrong the moment a heading, banner, tab strip or
/// version badge changes above it. The symptom the owner reported was module content sliced off
/// the bottom of the window regardless of window size.
/// </para>
/// <para>
/// <b>These assertions prove a SHAPE, never a behaviour.</b> Nothing in this repo can render a
/// Blazor component or measure a laid-out box, so only a person looking at a real browser can
/// say a scrollbar appeared or a pager is visible. Do not report a green suite as evidence for
/// that. Same standing as <see cref="SidebarScrollCssTests"/>, and for the same reason.
/// </para>
/// </remarks>
public class AppLayoutCssTests
{
    /// <summary>
    /// Values that legitimately scale WITH the window, as opposed to arithmetic on the chrome
    /// above an element. A proportion cannot go stale when a banner is added; a subtraction can.
    /// </summary>
    private const string WhyProportionsAreAllowed =
        "A proportion of the window (70vh, 90vw) is fine: adding a banner above an element does "
        + "not change it. Arithmetic on the chrome - 100vh minus a literal - is the defect, "
        + "because the literal is a guess at what sits above and goes wrong silently.";

    [Fact]
    public void NoPageDoesArithmeticOnTheChromeAboveIt()
    {
        // The whole point of the height chain. A page that needs to fill the remaining space
        // says so and lets the browser compute the chrome; it never subtracts a number someone
        // measured once on their own monitor.
        var offenders = new List<string>();

        foreach (var file in PageAndSharedFiles())
        {
            var text = StripComments(File.ReadAllText(file));
            foreach (Match m in Regex.Matches(text, @"calc\(\s*100vh\s*-[^)]*\)"))
                offenders.Add($"{Path.GetFileName(file)}: {m.Value}");
        }

        Assert.True(offenders.Count == 0,
            "Chrome arithmetic is back in a page:\n  " + string.Join("\n  ", offenders)
            + "\n\n" + WhyProportionsAreAllowed
            + "\nThe layout gives article a real height (Components/Layout/MainLayout.razor.css), "
            + "so a full-height page can use height: 100% or flex: 1 instead.");
    }

    [Fact]
    public void TheLayoutGivesTheContentAreaARealHeightToMeasureAgainst()
    {
        // Every "height: 100%" in a page below this is meaningless without these rules. Delete
        // any one of them and percentages silently resolve to auto again - which is the state
        // that made nine pages invent their own numbers.
        var css = File.ReadAllText(Path.Combine(GetLayoutDirectory(), "MainLayout.razor.css"));
        var desktop = DesktopBlock(css);

        // The window height moved up one level when the status frame became a real row of the
        // window rather than a footer inside main (2026-10-01). The chain is unchanged in kind
        // - exactly one element owns the viewport height and everything below measures against
        // it, with no literal anywhere - so this assertion follows it rather than being
        // dropped. .page claiming 100vh here would now push the status frame below the fold.
        Assert.Contains("height: 100vh", DeclarationsOf(desktop, ".app-shell"), StringComparison.Ordinal);

        // The two declarations that make the shell's height mean anything. Without them
        // .page's `flex: 1` is inert, the chain silently detaches at its new root, and the
        // "percentages have nothing real to measure against" failure returns - while every
        // other assertion here still passes. The chain is only as strong as its top link, and
        // this source scan is the only guard: there is no bUnit or browser harness.
        var baseShell = DeclarationsOf(StripComments(css), ".app-shell");
        Assert.Contains("display: flex", baseShell, StringComparison.Ordinal);
        Assert.Contains("flex-direction: column", baseShell, StringComparison.Ordinal);

        // .page takes the space the status frame leaves at BOTH breakpoints, so these live in
        // the base rule rather than the desktop block - on mobile they are what put the frame
        // at the bottom of a short page instead of directly under the content.
        var basePage = DeclarationsOf(StripComments(css), ".page");
        Assert.Contains("flex: 1", basePage, StringComparison.Ordinal);
        Assert.Contains("min-height: 0", basePage, StringComparison.Ordinal);

        Assert.DoesNotContain("100vh", DeclarationsOf(desktop, ".page"), StringComparison.Ordinal);

        // Mobile has no viewport-height owner of its own, so without this floor the shell is
        // only as tall as its content and the frame lands wherever the page happens to end.
        Assert.Contains("min-height: 100vh", DeclarationsOf(StripComments(css), ".app-shell"),
            StringComparison.Ordinal);

        // The sidebar measures the row it is given, not the viewport: a 100vh sidebar would
        // run underneath the status frame.
        Assert.DoesNotContain("100vh", DeclarationsOf(desktop, ".sidebar"), StringComparison.Ordinal);

        var main = DeclarationsOf(StripComments(css), "main");
        Assert.Contains("display: flex", main, StringComparison.Ordinal);
        Assert.Contains("flex-direction: column", main, StringComparison.Ordinal);
        Assert.Contains("min-height: 0", main, StringComparison.Ordinal);

        var article = DeclarationsOf(desktop, "article.content");
        Assert.Contains("flex: 1", article, StringComparison.Ordinal);
        Assert.Contains("min-height: 0", article, StringComparison.Ordinal);
        Assert.Contains("overflow-y: auto", article, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAdminShellTakesAPercentageRatherThanRestatingTheChrome()
    {
        // .adm was already the right SHAPE - flex column, panes own their scroll - and still
        // carried `calc(100vh - 2.9rem - 1.1rem)`, the top row plus the article padding typed
        // out by hand, because there was nothing to take a percentage of. With the chain
        // complete there is, and the number is gone rather than reduced.
        var adm = DeclarationsOf(StripComments(File.ReadAllText(AppCssPath())), ".adm");

        Assert.Contains("height: 100%", adm, StringComparison.Ordinal);
        Assert.DoesNotContain("100vh", adm, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMigrationReportDialogsScrollChainIsNotBrokenByItsWrapper()
    {
        // Item 21. .mig-report-text declared overflow/flex/min-height and the report STILL ran
        // off the bottom of the screen, because the <pre> is not a flex child of .mig-modal -
        // the Bootstrap .card-body between them is, and a .card-body is a BLOCK. The flex
        // properties on the <pre> were inert and overflow:auto had no overflow to act on.
        // A correct rule on the wrong element is the shape worth guarding.
        var css = StripComments(File.ReadAllText(Path.Combine(GetPagesDirectory(), "Migration.razor.css")));

        var wrapper = DeclarationsOf(css, ".mig-modal .card-body");
        Assert.Contains("display: flex", wrapper, StringComparison.Ordinal);
        Assert.Contains("flex-direction: column", wrapper, StringComparison.Ordinal);
        Assert.Contains("min-height: 0", wrapper, StringComparison.Ordinal);

        var text = DeclarationsOf(css, ".mig-report-text");
        Assert.Contains("overflow: auto", text, StringComparison.Ordinal);
        Assert.Contains("min-height: 0", text, StringComparison.Ordinal);

        // So the next break in this chain clips visibly instead of painting outside the box.
        Assert.Contains("overflow: hidden", DeclarationsOf(css, ".mig-modal"), StringComparison.Ordinal);
    }

    // ---- helpers ----

    /// <summary>The rule body for one selector, declarations only.</summary>
    private static string DeclarationsOf(string css, string selector)
    {
        var match = Regex.Match(css, Regex.Escape(selector) + @"\s*\{(?<body>[^}]*)\}");
        Assert.True(match.Success, $"selector '{selector}' not found");
        return match.Groups["body"].Value;
    }

    /// <summary>The desktop media block, where the height chain lives.</summary>
    private static string DesktopBlock(string css)
    {
        var start = css.IndexOf("@media (min-width: 641px)", StringComparison.Ordinal);
        Assert.True(start >= 0, "the desktop media query was not found in MainLayout.razor.css");
        return StripComments(css[start..]);
    }

    /// <summary>
    /// CSS with comments removed. Every rule here is EXPLAINED in a comment beside it, naming
    /// the very pattern the assertion forbids - so a scan that did not strip them would read the
    /// explanation as a violation.
    /// </summary>
    private static string StripComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", "", RegexOptions.Singleline);

    private static IEnumerable<string> PageAndSharedFiles()
    {
        foreach (var dir in new[] { GetPagesDirectory(), GetSharedDirectory() })
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.razor", SearchOption.AllDirectories))
                yield return file;
            foreach (var file in Directory.EnumerateFiles(dir, "*.css", SearchOption.AllDirectories))
                yield return file;
        }
    }

    private static string AppCssPath() => Path.Combine(RepoRoot(), "wwwroot", "app.css");
    private static string GetPagesDirectory() => Path.Combine(RepoRoot(), "Components", "Pages");
    private static string GetSharedDirectory() => Path.Combine(RepoRoot(), "Components", "Shared");
    private static string GetLayoutDirectory() => Path.Combine(RepoRoot(), "Components", "Layout");

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "Components", "Pages")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repo root from the test base directory.");
    }
}
