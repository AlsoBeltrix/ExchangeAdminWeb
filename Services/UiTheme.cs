namespace ExchangeAdminWeb.Services;

/// <summary>
/// One selectable UI theme.
/// </summary>
/// <param name="Id">
/// Stable identifier. Written to localStorage and emitted as the
/// <c>data-theme</c> attribute on &lt;html&gt;, so it must match the selector in
/// <c>wwwroot/app.css</c> exactly. Never rename one: a renamed id silently
/// falls back to the default for every user who had it selected.
/// </param>
/// <param name="Name">Display name for the picker.</param>
/// <param name="IsDark">
/// Whether the palette is fundamentally dark. This drives the browser's
/// <c>color-scheme</c>, which controls the chrome the stylesheet cannot reach:
/// scrollbars, native select popups, date pickers and form autofill. Getting it
/// wrong yields white scrollbars against a black canvas.
/// </param>
/// <param name="Group">Grouping label for the picker's optgroups.</param>
public sealed record UiTheme(string Id, string Name, bool IsDark, string Group);

/// <summary>
/// The catalog of selectable themes.
///
/// This lives in C# rather than as a hardcoded option list in the picker
/// component because there is no bUnit harness in this repo -- anything inside
/// a .razor file cannot be tested. Same reasoning as
/// <see cref="MessageTraceExportListing"/> and <see cref="AdminPageDirtyState"/>.
///
/// The palettes themselves are NOT here. A theme is a block of CSS custom
/// properties in wwwroot/app.css; this type only names them. UiThemeCssTests
/// asserts the two stay in step, which is the guard that matters: a theme whose
/// CSS block is missing a token inherits the default's value for it, so a dark
/// theme missing --ui-fg renders near-black text on a near-black canvas. That
/// is not a crash and not a compile error -- it is an invisible page.
/// </summary>
public static class UiThemeCatalog
{
    /// <summary>Theme applied when nothing is stored or the stored value is unknown.</summary>
    public const string DefaultId = "light";

    /// <summary>
    /// Value the pre-2.5.0 two-state toggle wrote to localStorage for dark mode.
    /// It names the same palette now called <c>oled</c>, so migrating it means an
    /// existing user sees no change at all.
    /// </summary>
    public const string LegacyDarkId = "dark";

    /// <summary>
    /// Every CSS custom property a theme block must define. The non-colour
    /// tokens (--ui-radius, --ui-font, --ui-mono) are deliberately absent: they
    /// are declared once on :root and shared, so requiring them per theme would
    /// force ten copies of the same font stack.
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredTokens =
    [
        "--ui-bg", "--ui-surface", "--ui-header", "--ui-zebra", "--ui-selected",
        "--ui-fg", "--ui-fg2", "--ui-fg3",
        "--ui-line", "--ui-grid",
        "--ui-brand", "--ui-brand-fg", "--ui-off",
        "--ui-on", "--ui-on-bg", "--ui-on-line",
        "--ui-warn", "--ui-warn-bg", "--ui-warn-line",
        "--ui-danger", "--ui-danger-bg", "--ui-danger-line",
        "--ui-info", "--ui-info-bg", "--ui-info-line",
        "--ui-nav-bg", "--ui-nav-fg", "--ui-nav-fg-hover",
        // Raw triplets for Bootstrap's --bs-*-rgb alpha blends. Required, not optional:
        // an absent one leaves Bootstrap's own blue in every translucent overlay while the
        // solid colours look correct, which is close to invisible.
        "--ui-brand-rgb", "--ui-on-rgb", "--ui-warn-rgb", "--ui-danger-rgb", "--ui-info-rgb"
    ];

    /// <summary>
    /// The selectable themes, in picker order. Light first because it is the
    /// default; the rest grouped so a ten-item list stays scannable.
    /// </summary>
    public static readonly IReadOnlyList<UiTheme> All =
    [
        new("light", "Light", false, "Default"),
        new("oled", "OLED Black", true, "Default"),

        new("solarized-light", "Solarized Light", false, "Solarized"),
        new("solarized-dark", "Solarized Dark", true, "Solarized"),

        new("dracula", "Dracula", true, "Editor"),
        new("nord", "Nord", true, "Editor"),
        new("gruvbox-dark", "Gruvbox Dark", true, "Editor"),
        new("monokai", "Monokai", true, "Editor"),
        new("one-dark", "One Dark", true, "Editor"),
        new("tokyo-night", "Tokyo Night", true, "Editor")
    ];

    /// <summary>
    /// Resolves a stored preference to a real theme. Never returns null and
    /// never throws: an unrecognised, empty or null value yields the default,
    /// because the alternative is an unstyled page.
    /// </summary>
    public static UiTheme Resolve(string? storedId)
    {
        if (string.IsNullOrWhiteSpace(storedId))
        {
            return Default;
        }

        var id = storedId.Trim();

        // The old toggle stored "dark". That is the OLED palette under its
        // pre-catalog name; mapping it means the upgrade is invisible.
        if (string.Equals(id, LegacyDarkId, StringComparison.OrdinalIgnoreCase))
        {
            id = "oled";
        }

        return All.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase))
               ?? Default;
    }

    /// <summary>The default theme. Guaranteed present in <see cref="All"/>.</summary>
    public static UiTheme Default =>
        All.First(t => t.Id == DefaultId);

    /// <summary>Themes grouped for the picker, preserving <see cref="All"/> order.</summary>
    public static IEnumerable<IGrouping<string, UiTheme>> Grouped() =>
        All.GroupBy(t => t.Group);
}
