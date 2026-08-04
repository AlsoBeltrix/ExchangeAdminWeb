using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Asserts that wwwroot/app.css and the theme catalog agree
/// (docs/ThemeSupport-Plan.md, Verification).
///
/// This is the load-bearing automated test for theming, because the failure
/// mode is neither a compile error nor an obviously wrong pixel. A theme block
/// missing a token silently inherits the Light value for it, so a dark theme
/// missing --ui-fg renders near-black text on a near-black canvas: the page is
/// not broken, it is invisible. Nothing else in the toolchain catches that.
///
/// It also guards the mirror trap recorded in docs/AdminUIRedesign-Plan.md
/// slice 2 -- a rule fixed in one stylesheet copy and not the other, which the
/// build reports as success.
/// </summary>
public class UiThemeCssTests
{
    [Fact]
    public void EveryThemeHasASelectorBlock()
    {
        var css = ReadAppCss();

        var missing = UiThemeCatalog.All
            .Where(t => FindBlock(css, t) is null)
            .Select(t => t.Id)
            .ToList();

        Assert.True(missing.Count == 0,
            "Themes in the catalog with no block in wwwroot/app.css (they would render as Light):\n" +
            string.Join("\n", missing));
    }

    [Fact]
    public void EveryThemeDefinesEveryRequiredToken()
    {
        var css = ReadAppCss();
        var offenders = new List<string>();

        foreach (var theme in UiThemeCatalog.All)
        {
            var block = FindBlock(css, theme);
            if (block is null)
            {
                continue; // reported by EveryThemeHasASelectorBlock
            }

            var absent = UiThemeCatalog.RequiredTokens
                .Where(token => !Regex.IsMatch(block, Regex.Escape(token) + @"\s*:"))
                .ToList();

            if (absent.Count > 0)
            {
                offenders.Add($"{theme.Id}: {string.Join(", ", absent)}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Theme blocks missing tokens. A missing token inherits the Light value, which on a " +
            "dark theme can mean invisible text:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void NoRuleIsConditionalOnAParticularTheme()
    {
        // The property that makes a theme pure data. If a rule names a theme,
        // the eleventh theme needs a code change rather than a token block --
        // and, worse, every theme that is not named silently misses the rule.
        // The token blocks themselves are excluded: those are the definitions.
        var css = ReadAppCss();
        var offenders = new List<string>();

        foreach (Match match in Regex.Matches(StripComments(css), @"^.*\[data-theme[^\r\n]*",
                                              RegexOptions.Multiline))
        {
            var line = match.Value.Trim();

            // A definition looks exactly like: html[data-theme="id"] {
            if (Regex.IsMatch(line, @"^html\[data-theme=""[a-z0-9-]+""\]\s*\{$"))
            {
                continue;
            }

            offenders.Add(line);
        }

        Assert.True(offenders.Count == 0,
            "Rules conditional on a specific theme. Read a token instead:\n" +
            string.Join("\n", offenders));
    }

    [Fact]
    public void NoRuleIsConditionalOnTheDarkClass()
    {
        // The `dark` class survives only to drive color-scheme, which controls
        // browser chrome the stylesheet cannot reach. Any OTHER use of it is a
        // rule that will not fire for eight of the ten themes -- the exact
        // defect that produced the greyed-out sidebar (e442df4).
        var offenders = new List<string>();

        foreach (var file in StylesheetPaths())
        {
            // Comments are stripped first: this file's own comments discuss the
            // dark class by name, and prose is not a rule.
            var css = StripComments(File.ReadAllText(file));

            foreach (Match match in Regex.Matches(css, @"^[^\r\n]*html(\.dark|:not\(\.dark\))[^\r\n]*",
                                                  RegexOptions.Multiline))
            {
                var line = match.Value.Trim();

                // The single permitted use, in app.css.
                if (line == "html.dark {" && IsColorSchemeBlock(css, match.Index))
                {
                    continue;
                }

                offenders.Add($"{Path.GetFileName(file)}: {line}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Theme-conditional rules keyed off the dark class. These do not fire for the other " +
            "eight themes; read a --ui-* token instead:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void DarkThemesOutnumberedByNothingSilly()
    {
        // Not an aesthetic assertion -- it pins that at least one light option
        // besides the default exists, so the picker is not "Light or nine
        // shades of dark".
        var lightThemes = UiThemeCatalog.All.Count(t => !t.IsDark);

        Assert.True(lightThemes >= 2, "At least two light themes are expected.");
    }

    /// <summary>
    /// Removes CSS block comments, replacing each with a single space so line
    /// structure elsewhere is unaffected. Without this, a comment that
    /// discusses a selector reads as that selector.
    /// </summary>
    private static string StripComments(string css) =>
        Regex.Replace(css, @"/\*.*?\*/", " ", RegexOptions.Singleline);

    /// <summary>
    /// True when the block starting at <paramref name="index"/> contains only a
    /// color-scheme declaration. That is the one legitimate use of the dark
    /// class; anything else in the block would be a styling rule in disguise.
    /// </summary>
    private static bool IsColorSchemeBlock(string css, int index)
    {
        var open = css.IndexOf('{', index);
        var close = css.IndexOf('}', index);
        if (open < 0 || close < 0 || close < open)
        {
            return false;
        }

        var body = css[(open + 1)..close];
        var declarations = body.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return declarations.All(d => d.StartsWith("color-scheme", StringComparison.Ordinal));
    }

    /// <summary>
    /// Returns the body of a theme's selector block, or null when it has none.
    /// </summary>
    private static string? FindBlock(string css, UiTheme theme)
    {
        // Light lives on :root so that an unrecognised data-theme degrades to
        // it rather than to unstyled.
        var selector = theme.Id == UiThemeCatalog.DefaultId
            ? @":root\s*\{"
            : @"html\[data-theme=""" + Regex.Escape(theme.Id) + @"""\]\s*\{";

        var match = Regex.Match(css, selector);
        if (!match.Success)
        {
            return null;
        }

        var close = css.IndexOf('}', match.Index);
        return close < 0 ? null : css[match.Index..close];
    }

    private static string ReadAppCss() =>
        File.ReadAllText(Path.Combine(GetWwwrootDirectory(), "app.css"));

    /// <summary>
    /// Every stylesheet the app ships: the global sheet plus the CSS-isolation
    /// files. Both are checked because app.css deliberately MIRRORS some of
    /// NavMenu.razor.css (isolation scoping has been unreliable on published
    /// IIS), and a rule converted in one copy but not the other is invisible to
    /// the build.
    /// </summary>
    private static IEnumerable<string> StylesheetPaths()
    {
        var root = GetRepoDirectory();

        yield return Path.Combine(root, "wwwroot", "app.css");

        foreach (var file in Directory.GetFiles(Path.Combine(root, "Components"), "*.razor.css",
                                                SearchOption.AllDirectories))
        {
            yield return file;
        }
    }

    private static string GetWwwrootDirectory() =>
        Path.Combine(GetRepoDirectory(), "wwwroot");

    private static string GetRepoDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "wwwroot", "app.css")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate wwwroot/app.css from test base directory.");
    }
}
