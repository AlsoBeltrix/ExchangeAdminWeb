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
    public void EveryThemeSeparatesItsSurfacesVisibly()
    {
        // The defect this guards: the first cut of these themes kept canvas, surface and header
        // within ~12 luminance points of each other. That is below the ~20 a step needs to read as
        // a step, so cards did not sit on the page, table headers did not separate from their
        // rows, and every palette collapsed into two flat colours regardless of how colourful it
        // was underneath (owner, 2026-08-04: "I see really only two main colors").
        //
        // Tokens all being PRESENT - which the other tests check - says nothing about them being
        // DISTINGUISHABLE. This is the difference.
        const double minimumSpread = 15.0;

        var css = ReadAppCss();
        var offenders = new List<string>();

        foreach (var theme in UiThemeCatalog.All)
        {
            var block = FindBlock(css, theme);
            if (block is null)
            {
                continue; // reported by EveryThemeHasASelectorBlock
            }

            var canvas = Luminance(TokenValue(block, "--ui-bg"));
            var header = Luminance(TokenValue(block, "--ui-header"));
            if (canvas is null || header is null)
            {
                continue; // reported by EveryThemeDefinesEveryRequiredToken
            }

            var spread = Math.Abs(header.Value - canvas.Value);
            if (spread < minimumSpread)
            {
                offenders.Add($"{theme.Id}: canvas->header spread is {spread:F1}, needs >= {minimumSpread}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Themes whose surfaces are too close to tell apart:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void EveryThemeSeparatesItsRaisedSurfaceFromItsCanvas()
    {
        // The same rule for the step operators see most: a card against the page behind it.
        const double minimumSpread = 6.0;

        var css = ReadAppCss();
        var offenders = new List<string>();

        foreach (var theme in UiThemeCatalog.All)
        {
            var block = FindBlock(css, theme);
            if (block is null)
            {
                continue;
            }

            var canvas = Luminance(TokenValue(block, "--ui-bg"));
            var surface = Luminance(TokenValue(block, "--ui-surface"));
            if (canvas is null || surface is null)
            {
                continue;
            }

            var spread = Math.Abs(surface.Value - canvas.Value);
            if (spread < minimumSpread)
            {
                offenders.Add($"{theme.Id}: canvas->surface spread is {spread:F1}, needs >= {minimumSpread}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Themes whose cards do not read as raised off the page:\n" + string.Join("\n", offenders));
    }

    /// <summary>
    /// Perceived brightness on a 0-255 scale (Rec. 709 coefficients). Good enough to catch
    /// surfaces that are indistinguishable; not a colour-science claim.
    /// </summary>
    private static double? Luminance(string? hex)
    {
        if (hex is null || hex.Length != 7 || hex[0] != '#')
        {
            return null;
        }

        var r = Convert.ToInt32(hex.Substring(1, 2), 16);
        var g = Convert.ToInt32(hex.Substring(3, 2), 16);
        var b = Convert.ToInt32(hex.Substring(5, 2), 16);

        return (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
    }

    /// <summary>The six-digit hex value a block assigns to a token, or null.</summary>
    private static string? TokenValue(string block, string token)
    {
        var match = Regex.Match(block, Regex.Escape(token) + @":\s*(#[0-9a-fA-F]{6})\s*;");
        return match.Success ? match.Groups[1].Value : null;
    }

    [Fact]
    public void EveryRgbTripletMatchesItsHexToken()
    {
        // Bootstrap needs raw "r, g, b" for its alpha blends, so each accent is declared twice:
        // once as hex and once as a triplet. Two copies of one value drift -- this was caught by
        // hand when Dracula's warn moved from yellow to orange and its triplet did not follow.
        // A stale triplet is invisible: the solid colour is right and only translucent overlays
        // are wrong.
        var css = ReadAppCss();
        var offenders = new List<string>();

        var pairs = new[]
        {
            ("--ui-brand", "--ui-brand-rgb"),
            ("--ui-on", "--ui-on-rgb"),
            ("--ui-warn", "--ui-warn-rgb"),
            ("--ui-danger", "--ui-danger-rgb"),
            ("--ui-info", "--ui-info-rgb")
        };

        foreach (var theme in UiThemeCatalog.All)
        {
            var block = FindBlock(css, theme);
            if (block is null)
            {
                continue;
            }

            foreach (var (hexToken, rgbToken) in pairs)
            {
                var hex = TokenValue(block, hexToken);
                var rgb = RgbTokenValue(block, rgbToken);

                if (hex is null || rgb is null)
                {
                    offenders.Add($"{theme.Id}: {hexToken} or {rgbToken} is missing");
                    continue;
                }

                var expected = $"{Convert.ToInt32(hex.Substring(1, 2), 16)}, " +
                               $"{Convert.ToInt32(hex.Substring(3, 2), 16)}, " +
                               $"{Convert.ToInt32(hex.Substring(5, 2), 16)}";

                if (rgb != expected)
                {
                    offenders.Add($"{theme.Id}: {rgbToken} is '{rgb}' but {hexToken} {hex} is '{expected}'");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "RGB triplets that disagree with their hex token:\n" + string.Join("\n", offenders));
    }

    /// <summary>The "r, g, b" value a block assigns to an -rgb token, or null.</summary>
    private static string? RgbTokenValue(string block, string token)
    {
        var match = Regex.Match(block, Regex.Escape(token) + @":\s*(\d+,\s*\d+,\s*\d+)\s*;");
        return match.Success ? Regex.Replace(match.Groups[1].Value, @"\s+", " ") : null;
    }

    [Fact]
    public void EveryBootstrapColourVariantOverridesItsDisabledState()
    {
        // Bootstrap 5.0 hardcodes its button colours instead of reading --bs-primary, and states
        // it on TWO-class selectors: `.btn-primary.disabled, .btn-primary:disabled`. A
        // single-class override loses to those, so a resting button themed correctly while every
        // DISABLED one stayed Bootstrap blue.
        //
        // That is precisely what shipped in 2.5.4 and what the owner saw: screenshots of empty
        // forms, where every submit button is disabled. Enabled buttons on the same pages were
        // already correct, which made it look inconsistent rather than unfixed.
        //
        // Specificity is invisible to every other check here -- the rule EXISTS, reads a token,
        // and still loses. This asserts the disabled state is stated for each variant.
        var css = StripComments(ReadAppCss());
        var missing = new List<string>();

        var variants = new[]
        {
            "btn-primary", "btn-secondary", "btn-success", "btn-danger", "btn-warning", "btn-info",
            "btn-outline-primary", "btn-outline-secondary", "btn-outline-success",
            "btn-outline-danger", "btn-outline-warning"
        };

        foreach (var variant in variants)
        {
            if (!Regex.IsMatch(css, Regex.Escape($".{variant}:disabled")))
            {
                missing.Add($".{variant}: no :disabled override, so Bootstrap's hardcoded colour wins");
            }
        }

        Assert.True(missing.Count == 0,
            "Button variants whose disabled state is left to Bootstrap:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void NoBootstrapBrandColourIsHardcodedInOurStylesheet()
    {
        // The literal values Bootstrap 5.0 ships for primary/success/danger/warning and their
        // hover shades. Any of these appearing in our own CSS means a rule was written against
        // Bootstrap's palette rather than the theme's, which cannot follow a theme.
        var css = StripComments(ReadAppCss());
        var offenders = new List<string>();

        var bootstrapLiterals = new[]
        {
            "#0d6efd", "#0b5ed7", "#0a58ca", "#0a53be", // primary + its states
            "#198754", "#157347",                       // success
            "#dc3545", "#bb2d3b",                       // danger
            "#ffc107", "#ffca2c",                       // warning
            "#0dcaf0"                                   // info
        };

        foreach (var literal in bootstrapLiterals)
        {
            foreach (Match match in Regex.Matches(css, Regex.Escape(literal), RegexOptions.IgnoreCase))
            {
                var line = css[..match.Index].Count(c => c == '\n') + 1;
                offenders.Add($"line {line}: {literal}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Bootstrap's own brand colours hardcoded in app.css (use a --ui-* token):\n" +
            string.Join("\n", offenders));
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
