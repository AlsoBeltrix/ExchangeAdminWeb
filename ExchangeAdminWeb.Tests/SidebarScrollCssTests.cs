using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Source-level tripwires for the sidebar scroll pane (docs/SidebarScrollbar-Plan.md).
///
/// The defect these guard against is invisible to every other gate. The sidebar rendered a
/// scrollbar on a window tall enough to show the whole menu, with roughly 450 pixels of empty
/// space below the last item - because the scroll pane overflowed by a CONSTANT 0.7rem at every
/// viewport height:
///
///   .nav-category (the EXCHANGE heading) is the first child of nav and carries margin-top: 0.7rem;
///   nav had no padding-top or border-top, so that margin collapsed out and became nav's own;
///   .nav-scrollable is a block formatting context (overflow-y: auto), so the margin could not
///   escape and instead offset nav 0.7rem down INSIDE a container nav was already min-height:100%
///   of. Scroll height therefore exceeded client height by a fixed length, forever.
///
/// nav was a plain block because Bootstrap's .flex-column sets flex-direction ONLY - it does not
/// set display. Flex containers do not collapse margins with their children, so making nav a real
/// flex container is the fix.
///
/// These assertions prove a SHAPE, never a behaviour. Nothing in this repo renders a page, so only
/// the plan's manual checks can show an operator that the scrollbar is gone. Do not report a green
/// suite as evidence for that.
/// </summary>
public class SidebarScrollCssTests
{
    [Fact]
    public void NavInsideTheScrollPaneIsAFlexContainer()
    {
        var block = DesktopBlock("\\.nav-scrollable nav");

        Assert.True(
            Regex.IsMatch(block, @"display:\s*flex\s*;"),
            "Components/Layout/NavMenu.razor.css: `.nav-scrollable nav` must declare `display: flex`.\n" +
            "Without it nav is a block box, the first .nav-category's 0.7rem margin-top collapses\n" +
            "into nav's own margin, and the scroll pane overflows by that amount at EVERY window\n" +
            "height - the permanent-scrollbar defect. Bootstrap's .flex-column on the element sets\n" +
            "flex-direction only and does not save you.\n\n" +
            "Found instead:\n" + block);
    }

    [Fact]
    public void TheScrollPaneCanShrinkBelowItsContent()
    {
        var block = DesktopBlock("\\.nav-scrollable");

        Assert.True(
            Regex.IsMatch(block, @"min-height:\s*0\s*;"),
            "Components/Layout/NavMenu.razor.css: `.nav-scrollable` must declare `min-height: 0`.\n" +
            "It is a flex item now. A flex item refuses to shrink below its content by default, so\n" +
            "without this the pane grows past the sidebar instead of scrolling inside it, and the\n" +
            "menu's last entries become unreachable on a short window.\n\n" +
            "Found instead:\n" + block);
    }

    [Fact]
    public void TheScrollPaneDoesNotHardcodeTheBrandRowHeight()
    {
        var css = ReadNavMenuCss();

        Assert.False(
            css.Contains("calc(100vh - 2.9rem)", StringComparison.Ordinal),
            "Components/Layout/NavMenu.razor.css still subtracts a hardcoded copy of the brand\n" +
            "row's height from 100vh. `.sidebar-brand { height: 2.9rem }` lives ~190 lines away in\n" +
            "the same file with nothing connecting them, so changing one silently desynchronises\n" +
            "the other: too large and the sidebar overflows permanently, too small and it leaves\n" +
            "dead space. The sidebar is a flex column and the pane takes `flex: 1` instead.");
    }

    /// <summary>
    /// The rule that produced the bug. Kept as its own assertion so that if someone ever "fixes"
    /// the scrollbar by deleting this margin instead, they are told why that is the wrong lever:
    /// .nav-category is MIRRORED into wwwroot/app.css, so a one-copy edit half-lands and is
    /// invisible to the build (the trap UiThemeCssTests documents). The container-level fix
    /// touches one file and changes no spacing.
    /// </summary>
    [Fact]
    public void TheCategoryMarginIsUnchangedAndStillMirrored()
    {
        var isolated = ReadNavMenuCss();
        var global = Regex.Replace(
            File.ReadAllText(Path.Combine(RepoRoot(), "wwwroot", "app.css")),
            @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);

        foreach (var (name, css) in new[] { ("NavMenu.razor.css", isolated), ("wwwroot/app.css", global) })
        {
            var block = Block(css, "\\.nav-category");
            Assert.True(
                block != null && Regex.IsMatch(block, @"margin-top:\s*0\.7rem\s*;"),
                $"{name}: `.nav-category` no longer carries `margin-top: 0.7rem`.\n" +
                "If the permanent-scrollbar bug was 'fixed' by removing this, revert it. That margin\n" +
                "is the spacing above every category heading, it is mirrored in two stylesheets, and\n" +
                "a one-copy edit is invisible to the build. The fix belongs on the container.");
        }
    }

    /// <summary>
    /// Extracts a rule from inside the `@media (min-width: 641px)` block. The desktop and mobile
    /// sidebars are different layouts and only the desktop one was changed, so a match anywhere in
    /// the file would not prove the rule lands where it matters.
    /// </summary>
    private static string DesktopBlock(string selectorPattern)
    {
        var css = ReadNavMenuCss();

        var mediaStart = css.IndexOf("@media (min-width: 641px)", StringComparison.Ordinal);
        Assert.True(mediaStart >= 0,
            "Components/Layout/NavMenu.razor.css no longer contains the `@media (min-width: 641px)` " +
            "block that holds the desktop sidebar rules.");

        var block = Block(css[mediaStart..], selectorPattern);
        Assert.True(block != null,
            $"Components/Layout/NavMenu.razor.css: no `{selectorPattern.Replace("\\", "")}` rule " +
            "inside the desktop media block.");

        return block!;
    }

    /// <summary>
    /// Quote-unaware brace matching would be wrong here, but these rules contain no strings or
    /// interpolation - the selector is followed by a flat declaration list - so finding the first
    /// `}` is sufficient and is the same shape UiThemeCssTests uses.
    /// </summary>
    private static string? Block(string css, string selectorPattern)
    {
        var match = Regex.Match(css, selectorPattern + @"\s*\{");
        if (!match.Success) return null;

        var close = css.IndexOf('}', match.Index);
        return close < 0 ? null : css[match.Index..close];
    }

    /// <summary>
    /// Reads the stylesheet with comments stripped. Every assertion here is about what the
    /// BROWSER is told, and a comment is not that.
    ///
    /// This is not hypothetical tidiness: the very first run of
    /// <see cref="TheScrollPaneDoesNotHardcodeTheBrandRowHeight"/> failed because the comment
    /// explaining what the old rule used to be quoted `calc(100vh - 2.9rem)` verbatim. The rule was
    /// gone; the prose describing its removal tripped the guard. A test a comment can fail is a
    /// test that will be silenced rather than fixed the next time it fires.
    /// </summary>
    private static string ReadNavMenuCss()
    {
        var raw = File.ReadAllText(Path.Combine(RepoRoot(), "Components", "Layout", "NavMenu.razor.css"));
        return Regex.Replace(raw, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ExchangeAdminWeb.csproj"))) return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate the repository root from the test assembly.");
    }
}
