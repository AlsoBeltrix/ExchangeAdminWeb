using System.Net;
using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Components.Endpoints;

namespace ExchangeAdminWeb.Middleware;

/// <summary>
/// Confirms a page request's access against the directory before the page is rendered (aud-a29g).
/// </summary>
/// <remarks>
/// <para>
/// Every page is an interactive island that is prerendered: the components endpoint runs the page's
/// <c>OnInitialized</c> / <c>OnInitializedAsync</c> and sends its HTML during the HTTP request, before
/// any circuit exists, against the request's principal - which under Kerberos still carries groups the
/// directory may have removed since the ticket was issued. The circuit's checks (aud-a29b, aud-a29c)
/// start only after that. This runs first: a principal the circuit's own revalidation would refuse gets
/// no page at all - <c>next</c> is never called, so no layout, router or page is rendered or initialized.
/// </para>
/// <para>
/// The verdict is <see cref="CircuitMembershipRevalidator.RevalidatePageRequestAsync"/>, the circuit's
/// check with nothing captured; it may confirm from a directory answer read within the revalidation
/// interval, so an account already being read by its open tabs costs nothing more here.
/// </para>
/// <para>
/// Only page renders are checked - endpoints carrying <see cref="ComponentTypeMetadata"/>, which
/// <c>MapRazorComponents</c> puts on every page it routes. The circuit's hub has its own gate, and the
/// framework's scripts and static files render nothing. An unauthenticated request has nothing to
/// confirm. With <see cref="CircuitRevalidationOptions.Enabled"/> off nothing is read or refused, as on
/// the circuit.
/// </para>
/// <para>
/// Fail closed: any verdict that is not valid refuses - a directory that cannot be read, a store that
/// cannot be read, a check that did not finish - and so does a failure the check did not anticipate.
/// The refusal is a 403 with a short page that says why in plain words (no group is named), one
/// warning and one audit line in the circuit's shape, trigger <c>PageRequest</c>, naming the page it
/// refused (<see cref="AuditPath"/>, aud-a29i).
/// </para>
/// <para>
/// Registered after <c>UseAuthorization</c>, so a request the page's own policy denies costs no
/// directory read, and after <see cref="PageLoadMiddleware"/>, so the status frame reports a page that
/// is waiting on this check as loading - a fact from the server, never a guess in the browser.
/// </para>
/// </remarks>
public sealed class PageRequestRevalidationMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<PageRequestRevalidationMiddleware> _logger;

    public PageRequestRevalidationMiddleware(RequestDelegate next, ILogger<PageRequestRevalidationMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(
        HttpContext context,
        CircuitRevalidationOptions options,
        CircuitMembershipRevalidator revalidator,
        AuditService audit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(revalidator);
        ArgumentNullException.ThrowIfNull(audit);

        // The off-switch first, as on the circuit: off means nothing is read and nothing is refused.
        if (!options.Enabled || !RendersAPage(context) || context.User.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        MembershipRevalidationResult verdict;
        try
        {
            verdict = await revalidator.RevalidatePageRequestAsync(context.User, context.RequestAborted);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The browser stopped waiting. Nothing was rendered, and nothing is.
            return;
        }
        catch (Exception ex)
        {
            verdict = MembershipRevalidationResult.LookupFailed($"The access check failed unexpectedly: {ex.Message}");
        }

        if (verdict.IsValid)
        {
            await _next(context);
            return;
        }

        await RefuseAsync(context, verdict, audit);
    }

    /// <summary>True when the request's endpoint renders a Razor component page.</summary>
    internal static bool RendersAPage(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<ComponentTypeMetadata>() is not null;

    /// <summary>The longest page path a refusal's audit line records; a longer one is cut and marked.</summary>
    internal const int MaxAuditPathLength = 256;

    /// <summary>
    /// The page a refused request asked for, as its audit line records it (aud-a29i): the path base and
    /// the path, never the query string - where a page's search terms and anything an operator typed
    /// would be - percent-encoded, so no character in it can break a log line, and at most
    /// <see cref="MaxAuditPathLength"/> characters, ending "..." when cut.
    /// </summary>
    internal static string AuditPath(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var path = request.PathBase.Add(request.Path).ToUriComponent();
        if (path.Length == 0)
            path = "/";

        return path.Length <= MaxAuditPathLength ? path : path[..(MaxAuditPathLength - 3)] + "...";
    }

    /// <summary>The whole response a refused page request gets: a standalone page, no app markup.</summary>
    internal static string RefusalPage(CircuitRevocationReason reason)
    {
        var title = WebUtility.HtmlEncode(PageRequestRefusalMessages.Title);
        var message = WebUtility.HtmlEncode(PageRequestRefusalMessages.For(reason));
        var advice = WebUtility.HtmlEncode(SessionEndedMessages.ReloadAdvice);

        return $"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8" />
            <meta name="viewport" content="width=device-width, initial-scale=1.0" />
            <title>{title}</title>
            </head>
            <body>
            <main role="alert">
            <h1>{title}</h1>
            <p>{message}</p>
            <p>{advice}</p>
            <p><a href="" data-enhance-nav="false">Reload</a></p>
            </main>
            </body>
            </html>
            """;
    }

    private async Task RefuseAsync(HttpContext context, MembershipRevalidationResult verdict, AuditService audit)
    {
        var reason = verdict.Reason ?? CircuitRevocationReason.LookupFailed;
        var account = context.User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(account))
            account = "Unknown";

        _logger.LogWarning(
            "Page request for {User} refused by access revalidation: {Reason}. {Detail}",
            account, reason, verdict.Detail);

        MembershipRevalidatingAuthenticationStateProvider.WriteAuditLine(
            audit, _logger, account, context.Connection.RemoteIpAddress?.ToString() ?? "Unknown", verdict,
            MembershipRevalidatingAuthenticationStateProvider.TriggerPageRequest,
            requestPath: AuditPath(context.Request));

        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsync(RefusalPage(reason));
    }
}

/// <summary>
/// What an operator whose page request was refused is told (aud-a29g). Plain words, and no group is
/// named: the person reading it may be the one whose access was just removed.
/// </summary>
public static class PageRequestRefusalMessages
{
    public const string Title = "This page was not shown";

    /// <summary>
    /// The reason in words. A page request captures no grant and holds nothing, so only a membership
    /// change, a disabled account or a check that could not confirm access can refuse one.
    /// </summary>
    public static string For(CircuitRevocationReason reason) => reason switch
    {
        CircuitRevocationReason.MembershipChanged =>
            "Your access was checked against Active Directory before this page was shown, and your "
            + "account is no longer in a group that your access in this app comes from.",
        CircuitRevocationReason.AccountDisabled =>
            "Your access was checked against Active Directory before this page was shown, and your "
            + "account is disabled.",
        _ =>
            "Your access could not be confirmed against Active Directory just now, so this page was not shown.",
    };
}
