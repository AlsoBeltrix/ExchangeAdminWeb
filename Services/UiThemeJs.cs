using System.Text;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Renders the theme catalog as a JavaScript object literal for the pre-paint
/// script in Components/App.razor.
///
/// That script has to run before &lt;body&gt; -- otherwise the page paints in the
/// wrong palette and then corrects itself, which is visible as a flash -- so it
/// cannot call into C#. It needs its own copy of "which themes exist and which
/// are dark". Generating that copy here means the two cannot drift; a
/// hand-maintained copy that fell behind would send every user of a newly added
/// theme to the default, silently.
/// </summary>
public static class UiThemeJs
{
    /// <summary>
    /// A JS object literal mapping theme id to its IsDark flag, e.g.
    /// <c>{"light":false,"oled":true}</c>.
    ///
    /// Ids are validated by <c>UiThemeCatalogTests.ThemeIdsAreCssAndAttributeSafe</c>
    /// to be lowercase alphanumeric-and-hyphen only, so they need no escaping;
    /// this method asserts that rather than assuming it, because the output is
    /// emitted into a script block.
    /// </summary>
    public static string CatalogLiteral()
    {
        var sb = new StringBuilder("{");
        var first = true;

        foreach (var theme in UiThemeCatalog.All)
        {
            if (!IsSafeId(theme.Id))
            {
                throw new InvalidOperationException(
                    $"Theme id '{theme.Id}' is not safe to emit into a script block. " +
                    "Ids must match ^[a-z0-9-]+$.");
            }

            if (!first)
            {
                sb.Append(',');
            }

            sb.Append('"').Append(theme.Id).Append("\":").Append(theme.IsDark ? "true" : "false");
            first = false;
        }

        return sb.Append('}').ToString();
    }

    private static bool IsSafeId(string id) =>
        id.Length > 0 && id.All(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-');
}
