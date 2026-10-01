using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Middleware;

/// <summary>
/// Records a page request in <see cref="PageLoadTracker"/> for as long as the server is serving
/// it, so the status frame can say what the app is doing right now.
/// </summary>
/// <remarks>
/// This is where "is a page loading" becomes a fact instead of a guess. The browser cannot
/// answer it - four attempts to make it answer failed, each leaving a stranded readout - but
/// the server is holding the request, so the question is trivial here: it is loading between
/// these two lines and not otherwise.
///
/// The scope is disposed in a finally, so a request that throws, times out or is cancelled
/// still clears. That is the whole anti-stranding guarantee and it is one keyword.
/// </remarks>
public sealed class PageLoadMiddleware
{
    private readonly RequestDelegate _next;

    public PageLoadMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context, PageLoadTracker tracker)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tracker);

        if (!IsOperatorPageRequest(context))
        {
            await _next(context);
            return;
        }

        var user = context.User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(user))
        {
            await _next(context);
            return;
        }

        using (tracker.Begin(user, DisplayRoute(context.Request.Path)))
        {
            await _next(context);
        }
    }

    /// <summary>
    /// Only requests that build a page an operator is waiting on. Everything else would make
    /// the frame flicker with traffic nobody asked about.
    /// </summary>
    private static bool IsOperatorPageRequest(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
            return false;

        var path = context.Request.Path.Value ?? string.Empty;

        // The circuit's own transport and the framework's assets are not page loads. Static
        // files are served from here too and are not what the operator clicked.
        if (path.StartsWith("/_blazor", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_framework", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/_content", StringComparison.OrdinalIgnoreCase))
            return false;

        if (Path.HasExtension(path))
            return false;

        // A document request, not a fetch for data: the browser asks for HTML when it is
        // replacing what the operator is looking at. Enhanced navigation sends the same.
        var accept = context.Request.Headers.Accept.ToString();
        return accept.Contains("text/html", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The route as the operator would recognise it: "mailbox-permissions" becomes
    /// "Mailbox Permissions". Deliberately derived from the path rather than looked up in
    /// ModuleCatalog - the catalog is keyed by route and this runs on every request, but more
    /// importantly a page outside the catalog must still report something true rather than
    /// nothing.
    /// </summary>
    internal static string DisplayRoute(PathString path)
    {
        var raw = (path.Value ?? string.Empty).Trim('/');
        if (raw.Length == 0)
            return "Home";

        var last = raw.Split('/')[^1];
        if (last.Length == 0)
            return "Home";

        var words = last.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => char.ToUpperInvariant(w[0]) + w[1..]);

        return string.Join(' ', words);
    }
}
