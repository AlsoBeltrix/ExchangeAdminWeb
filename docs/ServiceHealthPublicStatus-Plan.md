# Service Health Public Status Plan

Status: Draft

Owner: Michael
Repository base: `337e07b`
Base app version at drafting: 2.21.0 (no base bump proposed)
ServiceHealth module version: 1.3.2 at drafting, 1.4.0 proposed
Research performed live against `status.cloud.microsoft` on 2026-09-18; every URL,
payload shape and cadence claim below is from a request made that day, and the
request is named where the claim is made.

Owner-reported issue 2 of the 2026-09-15 queue, recorded in `.agents/state.md`
lines 318-322, where it is marked blocked on exactly the decision this plan exists
to unblock: "Needs a decision on how that second source is obtained (no documented
Graph equivalent is established) before any design."

## 1. The ask, verbatim

> 2. https://status.cloud.microsoft/ add to o365 status page somehow because
> microsoft splits status reports for some reason

The "o365 status page" is this repo's Service Health module:
`Components/Pages/ServiceHealth.razor`, `Services/ServiceHealthService.cs`,
`docs/ServiceHealth-Plan.md` (appearance, binding) and
`docs/ServiceHealthLoadFeedback-Plan.md` (load shape, must not regress).

## 2. Research findings

### 2.1 A machine-readable feed exists, and it is not a scrape

`https://status.cloud.microsoft/` is a React single-page app. The served HTML is
2058 bytes and contains no data: the body is `<div id="root"></div>`. Scraping the
rendered page would require a headless browser. That is not necessary, because the
SPA's own backing API is reachable, unauthenticated, over plain HTTPS GET.

The inline bootstrap script in that HTML declares, among others:

```
window.azureStatusPageUrl  = "https://azure.status.microsoft"
window.azureRSSFeedLink    = "https://rssfeed.azure.status.microsoft/en-us/status/feed/"
window.adminPortalUrl      = "https://portal.office.com"
window.ppacPortalUrl       = "https://admin.powerplatform.microsoft.com"
```

The application bundle `/dist/app.74abccc061f9662ecef6.js` contains the API surface
in clear text:

```
n.default.defaults.baseURL = window.location.origin,
t.getCurrentMacPost      = function(){ return i("api/posts/mac") },
t.getCurrentPPACPost     = function(){ return i("api/posts/ppac") },
t.getCurrentAzurePost    = function(){ return i("api/posts/azure") },
t.getCurrentConsumerWorkloads = function(){ var e = "api/posts/m365Consumer" ...
```

All four were fetched successfully with no credentials, no API key, no tenant
context and no `Origin` header:

| URL | Format | HTTP | Shape |
| --- | --- | --- | --- |
| `https://status.cloud.microsoft/api/posts/mac` | JSON | 200 | single object |
| `https://status.cloud.microsoft/api/posts/ppac` | JSON | 200 | single object |
| `https://status.cloud.microsoft/api/posts/azure` | JSON | 200 | single object |
| `https://status.cloud.microsoft/api/posts/m365Consumer` | JSON | 200 | array of 10 |
| `https://status.cloud.microsoft/api/feed/mac` | RSS 2.0 | 200 | 1 item |
| `https://status.cloud.microsoft/api/feed/ppac` | RSS 2.0 | 200 | 1 item |
| `https://rssfeed.azure.status.microsoft/en-us/status/feed/` | RSS 2.0 | 200 | 0 items today |

`/api/feed/azure` and `/api/feed/m365Consumer` return HTTP 400; there is no RSS for
those two on this host. `/robots.txt` returns 401. Anything under `/api/` that is
not a recognised surface (`/api/posts/m365`, `/api/posts/dynamics`) returns 400, so
the four names above are the whole documented-by-the-bundle surface.

Live payload of `/api/posts/mac`, verbatim:

```json
{
  "Id": "972a3801-cef4-49eb-817a-6e8b79ac845a",
  "AuthoredDateTime": "2026-09-16T18:30:21+00:00",
  "InternalDateTime": "2026-09-18T21:22:55+00:00",
  "LastUpdatedTime": "2026-09-18T21:24:00.490309+00:00",
  "Title": "",
  "Message": "<div>This site is updated when service issues are preventing tenant administrators from accessing Service health in the Microsoft 365 admin center. Alternatively, customers can reference&nbsp;<a target=\"_blank\" href=\"https://www.twitter.com/MSFT365Status\">https://www.twitter.com/MSFT365Status</a>&nbsp;for additional insights into widespread, active incidents.</div>",
  "Status": "Available"
}
```

`/api/posts/m365Consumer` adds `ServiceDisplayName` and `ServiceWorkloadName` per
entry and returned ten: Microsoft 365 (Consumer), Microsoft Copilot, Microsoft
Lists, Microsoft Teams Free, Microsoft To-Do, Microsoft Whiteboard, Office for the
web (Consumer), OneDrive, Outlook.com, Phone Link.

The `Status` field is a closed enum. The bundle declares it:

```
t.EBSPostStatusStrings = { Available:"Available", ServiceDegradation:"Service degradation",
                           Operational:"Operational", Unknown:"Unknown",
                           ServiceRestored:"Service restored" }
```

So the observed `"Available"` and `"Operational"` are two of five known values, and
an unrecognised sixth is possible. The module already has the right habit for that
(`ServiceHealthService.HumanizeStatus`, `Services/ServiceHealthService.cs:307-312`,
falls through to a camel-case split rather than hiding an unmapped value).

`Message` is HTML and carries an anchor with `target="_blank"`. That is the same
class of payload the module's existing sanitizer was built for, and the existing
`PostProcessNode` handler already forces `target`/`rel` on anchors
(`Services/ServiceHealthService.cs:375-382`).

**Cadence.** `LastUpdatedTime` is a per-request server clock, not a change marker:
three requests 20 seconds apart returned 17:28:01, 17:29:01, 17:29:01 local.
`InternalDateTime` is the real upstream refresh stamp and held steady at
`17:22:55` across all three. The page's own response header on the first fetch, ten
minutes earlier, carried `X_InternalDateTime: 09/18/2026 21:12:55 +00:00`, which is
`17:12:55` local, the same seconds value exactly ten minutes before. Two samples ten
minutes apart landing on the identical second is consistent with a fixed
ten-minute backend refresh. Labelled an assumption in section 13, but it matters:
`ServiceHealthService.CacheTtlSeconds = 600` (`Services/ServiceHealthService.cs:29`)
is already exactly ten minutes, so this source can ride the existing cache with no
added staleness relative to the source itself.

**Authentication.** None. No cookie, no bearer token, no CORS preflight (there is no
`Access-Control-Allow-Origin` header at all, which is irrelevant here because the
call is made server side, not from the operator's browser).

**Stability caveat, stated plainly.** These `/api/posts/*` paths are the SPA's own
backing endpoints. Microsoft does not document them as a contract. They can be
renamed or reshaped without notice, and the hash in the bundle filename shows the
app is redeployed routinely. That is the real cost of this integration and section 7
prices it. Note that `/api/feed/mac` and `/api/feed/ppac` are marginally more
committed surfaces, because the shipped UI has a visible "RSS feed" button bound to
them (`rssFeedButton`, `rssIcon`, `rssText` in the bundle), but they cover only two
of the four surfaces and carry the same single item in a more awkward format.

### 2.2 An iframe is impossible, with evidence

`https://status.cloud.microsoft/` returns:

```
X-Frame-Options: DENY
Content-Security-Policy: ... frame-ancestors 'self'; ...
```

Both independently forbid framing from this app's origin. The embedded-iframe option
is not "fragile", it is unavailable. It is struck from the options list in section 7
on that basis.

### 2.3 The owner's premise is correct, but the split is narrower and sharper than "two incident lists"

Verified from two independent sources.

**Microsoft's own documentation.** "How to check Microsoft 365 service health"
(`https://learn.microsoft.com/en-us/microsoft-365/enterprise/view-service-health`),
verbatim:

> If you're unable to sign in to the admin center, you can use the
> [service status page](https://status.cloud.microsoft) to check for known issues
> preventing you from logging into your tenant.

**The endpoint's own self-description**, from the live `/api/posts/mac` payload
above, verbatim:

> This site is updated when service issues are preventing tenant administrators from
> accessing Service health in the Microsoft 365 admin center.

**And the Graph side is explicitly tenant-scoped.** The `serviceAnnouncement`
resource reference
(`https://learn.microsoft.com/en-us/graph/api/resources/serviceannouncement`)
describes both collections this module reads as, verbatim, "A collection of service
health information **for tenant**" and "A collection of service issues **for
tenant**".

So the split is real, and it is this:

- Graph `serviceAnnouncement` is the **tenant admin plane**. It is rich (per-service
  status, per-incident post timelines, impact descriptions) and it is exactly what
  this module already renders well.
- `status.cloud.microsoft` is a **thin out-of-band availability signal**, published
  on infrastructure deliberately independent of the tenant admin plane, precisely so
  that it still answers when the tenant admin plane does not.

The concrete gap, by class:

1. **The admin plane itself.** When the Microsoft 365 admin center or the
   service-communications API behind Graph is degraded, the Graph board cannot report
   it. It will either throw (and the module renders its red `sh-error` banner with a
   Graph HTTP code, which tells an L1 nothing about whether Microsoft knows) or serve
   a stale cached snapshot that looks green. `/api/posts/mac` is the only surface
   that says "the thing you are looking at is the thing that is broken". This is the
   single highest-value item and it is a direct hit on the owner's complaint.
2. **Azure platform.** `/api/posts/azure` plus the separate Azure RSS feed at
   `rssfeed.azure.status.microsoft` cover Azure platform incidents. Tenant
   `serviceAnnouncement` does not carry them. An Azure Front Door or regional
   networking event that degrades a Microsoft 365 workload can be posted on the Azure
   side before, or instead of, anything appearing under a Microsoft 365 workload name.
3. **Power Platform admin center availability.** `/api/posts/ppac`. Its own message
   says the M365 admin center is where Power Platform service health lives, so this
   endpoint is specifically about the PPAC surface being reachable, not about Power
   Platform workloads.
4. **Consumer services.** `/api/posts/m365Consumer`, ten services, none of which
   exist in a tenant `serviceAnnouncement` response: Outlook.com, consumer OneDrive,
   Teams Free, To-Do, Whiteboard, Phone Link and so on.

**Where the premise needs correcting.** The public page is not a second, fuller
incident list. Today it carries **one short HTML sentence per surface** plus a status
word. It will never show an Exchange Online incident that Graph is missing, because
it does not carry per-workload Microsoft 365 incidents at all. Anyone expecting
"more incidents" from this will be disappointed; what they get is "an independent
answer to whether Microsoft's own admin surfaces are up", which is a different and
genuinely missing signal. Section 4 sets expectations accordingly, and open question
1 puts this in front of the owner before any code is written.

### 2.4 What the module fetches today

`Services/ServiceHealthService.cs:82-83` declares exactly two endpoints and nothing
else:

```csharp
private const string HealthOverviewsEndpoint = "/admin/serviceAnnouncement/healthOverviews";
private const string IssuesEndpoint = "/admin/serviceAnnouncement/issues?$filter=isResolved%20eq%20false";
```

They are fetched by `FetchServicesAsync` (`:145-175`) and `FetchIssuesAsync`
(`:177-195`), started as two tasks and awaited together at `:119-121`:

```csharp
var servicesTask = FetchServicesAsync(client);
var issuesTask = FetchIssuesAsync(client);
await Task.WhenAll(servicesTask, issuesTask);
```

There is no third collection, no `messages` (Message Center) read, and nothing
non-Graph. So nothing in section 2.3's gap list is duplicated by what the board shows
today.

## 3. Goal

An operator looking at the Service Health board must be able to tell, without
leaving the page, whether Microsoft's own public status surface is currently
reporting a problem with the admin plane, with Azure, or with the Power Platform
admin center. The board must not be able to read "all green" while that public
surface is reporting trouble.

## 4. Non-goals

- **No redesign.** `docs/ServiceHealth-Plan.md` binds the appearance and stays
  binding: no charts, no gradients, no new card geometry, no restyle of the existing
  summary cards, service grid or incident list. `.agents/state.md:150-152` records
  the ruling. This change adds one compact strip built from CSS classes and colour
  semantics the page already has.
- **Not a second incident feed.** Per section 2.3, the public source carries one
  sentence per surface. This plan does not promise, and must not be implemented as,
  a richer incident list.
- **No background polling service, no notifications, no history store.** All three
  are already non-goals of `docs/ServiceHealth-Plan.md` section 2 and stay so. The
  new source is fetched on the same on-demand path as the Graph data.
- **No change to the Graph fetch, the Graph error contract, the cache TTL, the
  authorization check, or the load-feedback shape** beyond the one `Task.WhenAll`
  arity change analysed in section 8.
- **No consumer services on the board** under this plan's recommendation. See open
  question 2.
- **No Message Center (`serviceAnnouncement/messages`) read.** That is a different
  ask with a different audience and needs its own plan.

## 5. Recommendation in one sentence

Fetch the three admin-surface JSON endpoints (`mac`, `ppac`, `azure`) server side as
a third task inside the existing `Task.WhenAll`, sanitize their HTML through the
module's existing sanitizer, hang the result off `ServiceHealthStatus`, and render it
as one compact strip directly above the four summary cards, with the whole fetch
built so it can never throw and never block the board.

## 6. Design

### 6.1 Where the code goes

A new private method on the existing `ServiceHealthService`, not a new service class.
Reasons, in order: the cache, the TTL, the `_cacheLock` critical section and the
`Task.WhenAll` all already live in that class, and splitting the fetch out would mean
either duplicating the cache story or threading a second service through it;
`.agents/repo-guidance.md` Code Change Discipline says "Prefer established local
patterns over new abstractions"; and `_httpClientFactory` is already a field on the
class (`Services/ServiceHealthService.cs:19`), currently used only to build the Graph
client at `:70`.

### 6.2 New types

```csharp
public sealed class PublicStatusBoard
{
    public bool Reachable { get; init; }
    public string? Unreachable { get; init; }      // operator-facing reason, null when Reachable
    public List<PublicStatusEntry> Surfaces { get; init; } = [];
    public DateTimeOffset? UpstreamUpdated { get; init; }   // InternalDateTime, most recent
}

public sealed class PublicStatusEntry
{
    public string SurfaceName { get; init; } = "";   // "Microsoft 365 admin center" etc, ours, not theirs
    public string Status { get; init; } = "";        // raw upstream token
    public string StatusText { get; init; } = "";    // through HumanizeStatus
    public bool IsHealthy { get; init; }
    public string ContentHtml { get; init; } = "";   // sanitized Message; see 8.1 for why this name
}
```

`ServiceHealthStatus` gains one property:

```csharp
public PublicStatusBoard PublicStatus { get; init; } = new();
```

Defaulted to a new instance, not null, so every existing test that constructs a
`ServiceHealthStatus` by object initialiser keeps compiling and the page never has to
null-check it.

`IsHealthy` is computed in the service, not the page, and follows the module's
existing fail-loud rule (`ServiceHealthEntry.IsHealthy`,
`Services/ServiceHealthService.cs:485`): only `Available` and `Operational` count as
healthy; `ServiceDegradation`, `Unknown`, `ServiceRestored` and any sixth value
Microsoft adds tomorrow read as not-healthy. A status nobody has mapped must never be
quietly counted as green.

### 6.3 The fetch

```csharp
private const string PublicStatusBaseUrl = "https://status.cloud.microsoft";
private static readonly (string Path, string SurfaceName)[] PublicStatusSurfaces =
[
    ("/api/posts/mac",   "Microsoft 365 admin center"),
    ("/api/posts/azure", "Azure"),
    ("/api/posts/ppac",  "Power Platform admin center")
];
```

`FetchPublicStatusAsync(HttpClient client)` issues the three GETs, deserializes each
with `System.Text.Json` (the module's existing idiom, and
`.agents/repo-guidance.md` / Constitution "Use structured parsers/APIs for structured
data"), maps each into a `PublicStatusEntry`, and passes every `Message` through
`Sanitize` (section 8.1).

Per-surface failures aggregate rather than blanket-fail (Known Failure Class 2): a
surface whose GET fails or whose body will not parse is still listed, with
`IsHealthy = false`, `StatusText = "Unknown"` and a short reason in place of content.
It is never silently dropped and never rendered green. The board is `Reachable =
false` only when all three fail, which is the "the whole public source is
unreachable" case and is what drives the link-out fallback in 6.5.

### 6.4 Wiring into the existing concurrent fetch

`Services/ServiceHealthService.cs:119-121` becomes:

```csharp
var servicesTask = FetchServicesAsync(client);
var issuesTask = FetchIssuesAsync(client);
var publicStatusTask = FetchPublicStatusAsync(publicStatusClient);
await Task.WhenAll(servicesTask, issuesTask, publicStatusTask);
```

All three start before any is awaited, so the public call adds no wall-clock time as
long as it finishes inside the Graph round trip. It is inside the existing
`_cacheLock` critical section and touches no shared mutable state, so it widens no
race the class does not already own, by the same argument the comment at `:117-118`
already makes for the two Graph tasks.

The client is a new named `HttpClient`, registered in `Program.cs` beside the
existing two (`Program.cs:121` `ServiceNow`, `:128` `MicrosoftGraph`):

```csharp
builder.Services.AddHttpClient("PublicStatus")
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(5);
        client.DefaultRequestHeaders.Add("Accept", "application/json");
    });
```

Five seconds, not the thirty the other two clients use, and the reason is section 9.

### 6.5 What renders, and where

One strip, in `Components/Pages/ServiceHealth.razor`, inside the existing
`else if (status != null)` branch (`:71`), immediately before
`<div class="sh-summary">` (`:76`). It is not a separate tab and not a separate
section lower down, and that is a deliberate answer to the "one board or two"
question: the owner's complaint is that the board *looks green during an incident*,
and a contradicting signal parked behind a tab or below the fold does not fix a board
that looks green. It has to be where the eye already lands, above the four summary
cards.

Shape: a heading line plus one row per surface. Each row is the surface name, a
`<span class="sh-status-badge status-@...">` using the badge element the page already
uses at `:167`, and the sanitized sentence. Reuses the existing `status-healthy`,
`status-warning`, `status-error`, `status-unknown` colour classes from
`Components/Pages/ServiceHealth.razor.css`. New CSS is layout only (one new class for
the strip, one for a row) and must reference only the tokens the file already uses:
`--ui-surface`, `--ui-line`, `--ui-fg`, `--ui-fg2`, `--ui-fg3`, `--ui-warn`,
`--ui-warn-bg`, `--ui-danger`, `--ui-danger-bg`, `--ui-info`. No literal hex, no
gradient. See 8.4 and 8.5 for the tests that enforce both.

It renders on every load, not only on trouble. A strip that appears only when
something is wrong is indistinguishable from a strip that failed to load, and "we
checked Microsoft's public page and it is clear" is information the operator needs in
order to trust the green board above it.

When `Reachable` is false, the strip renders a single line saying the public status
source could not be read, with the reason, and an anchor to
`https://status.cloud.microsoft` carrying `target="_blank" rel="noopener noreferrer"`
so the operator can check it by hand. That anchor is a literal in the `.razor`
markup, not sanitized third-party content, and so does not interact with 8.1.

Additionally, the existing `loadError` branch (`:55-62`) gains the same static
link-out line. That branch is the "Graph failed entirely" case, and it is precisely
the case where Microsoft's public page is most likely to have the answer. It costs
one anchor and needs no data. Making the *fetched* public board survive a Graph
failure would require changing `GetStatusAsync`'s deliberately fail-loud contract
(`Services/ServiceHealthService.cs:85-90`) and is open question 4, not part of this
plan.

## 7. Options considered, and their price

| Option | Verdict |
| --- | --- |
| **Undocumented JSON endpoints, server side** (recommended) | Real structured data, no browser, no credentials, fits the existing cache and `Task.WhenAll` exactly. Price: the endpoints are not a published contract and can change without notice. Mitigated by the never-throw design in section 9, which degrades to the link-out line rather than breaking the board, so a silent upstream change costs an operator one visible "unavailable" line, not an outage of the page. |
| `/api/feed/mac` and `/api/feed/ppac` RSS | Slightly more committed surface (a visible RSS button in Microsoft's shipped UI binds to them), but covers two of four surfaces, returns 400 for `azure` and `m365Consumer`, carries the same single item, and would add an XML parse path the repo does not otherwise have. Worse trade. Recorded as the documented fallback if Microsoft retires the JSON. |
| Scraping rendered HTML | Would require a headless browser, since the served HTML is 2058 bytes with an empty `<div id="root">`. Not proposed. |
| Embedded iframe | **Impossible.** `X-Frame-Options: DENY` and CSP `frame-ancestors 'self'`, both observed on the live response. Struck. |
| Link-out only | Honest and free, and it is a legitimate answer when data is not machine-readable. It is not the right answer *here*, because the data is machine-readable. It is retained as the degraded mode (6.5) rather than as the whole feature. |
| Do nothing | Leaves a real signal gap that Microsoft's own documentation tells administrators to go elsewhere for. |

## 8. Guard tripwires this change collides with

This is the section most likely to be got wrong. Six existing tests constrain the
shape of this change; two of them fail on a correct implementation and must be
deliberately updated, and the rest must be satisfied by construction rather than by
editing them.

### 8.1 `Page_MarkupStringIsOnlyEverUsedOnSanitizedFields` - satisfy by naming

`ExchangeAdminWeb.Tests/ServiceHealthPageTests.cs:33-49` matches every
`(MarkupString)expr` in the page source and asserts the expression ends in
`ImpactDescriptionHtml`, `ContentHtml` or `ValueHtml`.

The public `Message` is HTML, so the strip renders it with `MarkupString`. **Do not
extend the allow-list.** Name the model property `ContentHtml`, as section 6.2 does.
It is the semantically correct name anyway (it is a post body, exactly like
`ServiceIncidentPost.ContentHtml`, `Services/ServiceHealthService.cs:537`), and it
makes the tripwire cover the new surface by construction without being weakened.

The sanitizer path, named as required: `ServiceHealthService.Sanitize`
(`Services/ServiceHealthService.cs:392-393`), backed by the static `Sanitizer` built
in `CreateSanitizer()` (`:345-385`) from `Ganss.Xss.HtmlSanitizer` - a cleared
allow-list of 27 tags, four attributes (`href`, `title`, `colspan`, `rowspan`),
`http`/`https` schemes only, no CSS properties, no data attributes, and a
`PostProcessNode` handler (`:375-382`) that forces `target="_blank"` and
`rel="noopener noreferrer"` onto every anchor. Sanitizing happens in the service,
never in the page, so the page cannot later be edited into rendering a raw field -
the comment at `:335-341` states that rule and this change must not break it. The
live `/api/posts/mac` payload in section 2.1 is a `<div>` containing an `<a
target="_blank" href="https://...">`, all of which this allow-list already passes
correctly.

### 8.2 `TheGraphCollectionsAreFetchedConcurrently` - **must be updated, and here is why that is not bypassing a guard**

`ExchangeAdminWeb.Tests/ServiceHealthServiceTests.cs:528-543` asserts, after
stripping comments:

```csharp
Assert.Contains("Task.WhenAll(servicesTask, issuesTask)", source, StringComparison.Ordinal);
Assert.DoesNotContain("await FetchServicesAsync", source, StringComparison.Ordinal);
Assert.DoesNotContain("await FetchIssuesAsync", source, StringComparison.Ordinal);
```

The first assertion is an exact substring. Section 6.4 changes the call to
`Task.WhenAll(servicesTask, issuesTask, publicStatusTask)`, so the literal no longer
appears and **this test fails on a correct implementation.**

`AGENTS.md` forbids bypassing a failed check unless it is proven not load-bearing.
This guard *is* load-bearing, so it is not being bypassed; it is being widened to
cover a third task. The property it protects is "no fetch is awaited serially ahead
of another", and the updated assertions preserve that property over a strictly larger
set:

```csharp
Assert.Contains("Task.WhenAll(servicesTask, issuesTask, publicStatusTask)", source, StringComparison.Ordinal);
Assert.DoesNotContain("await FetchServicesAsync", source, StringComparison.Ordinal);
Assert.DoesNotContain("await FetchIssuesAsync", source, StringComparison.Ordinal);
Assert.DoesNotContain("await FetchPublicStatusAsync", source, StringComparison.Ordinal);
```

Three assertions before, four after, and the new one closes the exact hole this
change would otherwise open. The guard proof in section 11 requires mutating to a
serial `await FetchPublicStatusAsync(...)` and watching the updated test fail.

The tempting alternative - keeping the old literal intact by writing
`await Task.WhenAll(servicesTask, issuesTask); var publicStatus = await publicStatusTask;`
- is rejected. It happens to preserve concurrency, but it reads as an accident, the
next person to touch it will "tidy" it into a serial await, and it leaves the guard
blind to the third task. Do not do it.

### 8.3 `TheModuleVersionWasBumpedForTheLoadFeedbackChange` - **must be updated**

`ExchangeAdminWeb.Tests/ServiceHealthPageTests.cs:426-433` asserts
`Assert.Equal("1.3.2", module!.Version)`. Bumping the module version is mandatory
(section 10), so this literal must move to the new version in the same commit as the
bump. Updating it *is* the test's purpose, not a bypass. Rename it to
`TheModuleVersionWasBumpedForThePublicStatusChange` at the same time so the name
still describes what it guards.

### 8.4 `Page_AuditsBothTheSuccessAndFailurePaths` - satisfy by not adding a third call

`ServiceHealthPageTests.cs:70-76`:
`Assert.Equal(2, Regex.Matches(source, @"Audit\.LogLookupAction").Count)`. Exactly
two call sites, no more.

Therefore: **do not add an audit call for the public fetch.** Fold its outcome into
the `extra` dictionary of the existing success call at
`Components/Pages/ServiceHealth.razor:366-373`, which already carries `Refresh`,
`Services` and `OpenIssues`, by adding one key such as
`["PublicStatusReachable"] = status.PublicStatus.Reachable`. The failure call at
`:379-380` is the Graph-failed path and is untouched.

### 8.5 `Styles_UseThemeTokensRatherThanLiteralColours` and `Styles_CarryNoGradient` - satisfy by construction

`ServiceHealthPageTests.cs:121-135` asserts every hex literal in
`ServiceHealth.razor.css` equals `#4fc3f7` (the one deliberate header-icon accent).
Any new colour must be a `var(--ui-*)` token; see the list in 6.5.
`ServiceHealthPageTests.cs:137-145` asserts the word "gradient" appears nowhere in
that file, case-insensitively. Both pass if the strip is built from existing tokens
and existing status classes, which is what 6.5 specifies.

### 8.6 `Catalog_ServiceHealth_DeclaresGraphSecretConfigField` - the reason there is no new config field

`ExchangeAdminWeb.Tests/ModuleCatalogTests.cs:143-149` does
`var field = Assert.Single(module.ConfigFields);`. Adding any second `ConfigFields`
entry to the ServiceHealth descriptor **fails this test**. Section 10.2 argues on the
merits that the URL should be a constant rather than config; this test is independent
corroboration that the descriptor's single-field shape is deliberate and pinned.

### 8.7 The three load-feedback tripwires - untouched by construction

`OnInitializedAsync_DoesNotAwaitTheHealthServiceDirectly`
(`ServiceHealthPageTests.cs:382-394`),
`TheInitialLoadRunsFromOnAfterRenderAsyncBehindAOneShotGuard` (`:396-412`) and
`LoadAsyncCallsStateHasChangedInItsFinallyBlock` (`:414-424`) all pass unchanged,
because **the page gains no new fetch at all**. The public status arrives as a
property of the object `LoadAsync` already awaits at
`Components/Pages/ServiceHealth.razor:365`
(`status = await HealthService.GetStatusAsync(forceRefresh)`). There is no new
`await` anywhere in the page, no new field, no change to `OnInitializedAsync`, no
change to `OnAfterRenderAsync`, and no change to `LoadAsync`'s `finally`.

This is the whole reason the fetch belongs in the service and not in the page. A new
serial `await` in `OnInitializedAsync` would reintroduce exactly the bug
`docs/ServiceHealthLoadFeedback-Plan.md` fixed on 2026-09-18, and tripwire 1 would
correctly fail it.

One substring hazard to note for the implementer: tripwire 1 asserts
`Assert.DoesNotContain("HealthService", body)` over the comment-stripped body of
`OnInitializedAsync`. Do not introduce any identifier containing the substring
`HealthService` into that method. Nothing in this plan does.

`Page_MirrorsTheOriginalDashboardStructure` (`:104-119`) also passes unchanged: it
asserts the presence of `sh-summary`, `sh-services-grid`, "Current Issues" and the
two no-results strings, and asserts exactly one
`private RenderFragment<ServiceIncident> IncidentCard` and exactly one
`@IncidentCard(issue)`. The strip adds none of those and must not add a second
incident render fragment.

## 9. Failure isolation

**The requirement:** if `status.cloud.microsoft` is unreachable, slow, or returns
something unparseable, the existing Graph board must still render normally.

**Why that is not free.** `FetchServicesAsync` and `FetchIssuesAsync` both *throw* on
failure - `throw new InvalidOperationException(...)` at
`Services/ServiceHealthService.cs:149` and `:181`. `await Task.WhenAll(...)` rethrows
the first faulted task's exception. That propagates out of `GetStatusAsync` into
`LoadAsync`'s `catch (Exception ex)` at `Components/Pages/ServiceHealth.razor:375-381`,
which sets `loadError` and renders the red `sh-error` banner instead of the board. So
a public-status fetch written in the same throwing style would take the entire board
down whenever a public Microsoft web page was having a bad minute. That is the exact
opposite of what is wanted.

**The mechanism.** `FetchPublicStatusAsync` is **total**: its entire body is wrapped
in `try { ... } catch (Exception ex) { ... }`, the catch logs at `Warning` via the
existing `_logger` and returns a `PublicStatusBoard { Reachable = false, Unreachable
= <short reason> }`, and the method has no `throw` on any path. The returned task can
therefore never fault, so `Task.WhenAll` can never observe a fault from it, so
`GetStatusAsync` cannot throw because of it. Per-surface failures are handled one
level in, as described in 6.3, so one bad surface does not cost the other two.

**Why this is not a Known Failure Class 3 violation.** Invariant 3 requires
authorization and enablement stores to fail closed. This is neither: it is a
read-only advisory display with no write, no gate and no decision hanging off it.
Failing closed here would mean hiding a working Graph board because a public web page
was down, which is strictly worse for the operator. It does *not* fail open in the
dangerous sense either: an unreachable source renders as an explicit "could not read"
line with a link-out, never as blank and never as green. That is the correct reading
of Known Failure Class 2 - an unreachable source must not aggregate into a healthy
report.

**Timeout.** The 5-second cap on the `PublicStatus` client (6.4) exists because of the
"must not regress the load-feedback fix" constraint. The three public GETs run
concurrently with the two Graph calls, so in the normal case they cost nothing. The
worst case is that the public host hangs while Graph is fast, in which case the board
waits up to 5 extra seconds on a cold cache, once per 10-minute cache window. Five
seconds is the honest number to argue about; open question 3 puts it to the owner.
Note that the spinner is on screen for the whole of it - that is the point of the
2026-09-18 fix and it is not regressed, because no new work was added ahead of the
first render.

**Ordering.** There is no try/catch/finally state-write hazard here (Known Failure
Class 1) because the only state write is `_cached = status` at
`Services/ServiceHealthService.cs:133`, which is already downstream of the `await
Task.WhenAll` and is unreachable if that await throws. Adding a non-throwing task to
the `WhenAll` does not move it onto a failure path.

## 10. Caching, cadence and versioning

### 10.1 Caching

No new cache. The public board is part of `ServiceHealthStatus`, which is what
`_cached` holds (`Services/ServiceHealthService.cs:23, 133`), so it is covered by the
existing `CacheTtlSeconds = 600` and by the double-checked lock at `:95-110`. One
consequence to state explicitly: the app makes at most **three extra outbound
requests per 10 minutes per instance**, plus three more each time an operator presses
Refresh (`forceRefresh: true` bypasses the cache at `:92-98`, and must continue to
refresh all three sources together - a Refresh that updated only the Graph half would
be a lie).

Section 2.1 measured the upstream refresh at about ten minutes, which is the same
number, so riding the existing TTL adds no staleness relative to the source.

### 10.2 Where the URL lives, and why that is environment-neutral

`https://status.cloud.microsoft` is a `private const string` in
`Services/ServiceHealthService.cs`, alongside the two existing endpoint constants at
`:82-83`. It is **not** a `ConfigFields` entry.

`.agents/repo-guidance.md` invariant 7 forbids a source file naming "an ADI domain,
host, OU, group or address as behaviour", and forbids resting a safety argument on
this environment's shape. Neither applies. `status.cloud.microsoft` is Microsoft's
single global public status host: it is byte-identical for every tenant, every
forest, every customer and every deployment of this application, and it carries no
information about ADI. It is the same class of constant as
`"/admin/serviceAnnouncement/healthOverviews"` at `:82` and the Graph base URL inside
`GraphTokenClient`, both of which are already constants for the same reason. The
invariant is about environment facts, and a global vendor endpoint is not one.

Making it configurable would be actively worse on three counts: it would invite a
per-deployment override of a value that has no per-deployment meaning; it would add a
config key an operator must set before the module works, breaking the property that
this change requires no new configuration; and it would fail
`Catalog_ServiceHealth_DeclaresGraphSecretConfigField` (8.6). The constant must be
pure ASCII, per the repo's ASCII lint; it is.

### 10.3 Versioning

`docs/ProjectConstitution.md` "Deployment And Versioning", verbatim:

> - Shared infrastructure changes bump the base app version.
> - Module-scoped behavior changes bump that module's version in `ModuleCatalog`.

This is a module-scoped behaviour change. **`Modules/ModuleCatalog.cs`, ServiceHealth
`Version` 1.3.2 -> 1.4.0.** Minor, not patch: a new data source is a new capability,
not a fix. **No base app bump**; `ExchangeAdminWeb.csproj` `<VersionPrefix>` stays
2.21.0.

The one judgement call is the new named `HttpClient` registration in `Program.cs`.
Position taken: that is module plumbing, not shared infrastructure - it is consumed
by exactly one service, nothing outside ServiceHealth can observe it, and it is the
same kind of registration the `ServiceNow` client already is. So no base bump.
Recorded as open question 5 because it is a defensible call rather than an obvious
one, and because a wrong answer here is a Constitution violation rather than a
preference.

The descriptor's `Description` text (`Modules/ModuleCatalog.cs`, currently "...read
from the Microsoft Graph service announcements API.") should be extended to name the
public status source, in the same commit as the version bump. No test asserts that
string, checked against `ModuleCatalogTests.cs`.

## 11. Slices

One session each, one commit each, each with its own guard proof, per
`.agents/repo-guidance.md` Earned Practices and Token Budget. The suite must be green
at every commit boundary, which is why the version bump and its assertion move
together in slice 3 rather than being split.

**Slice 1 - the service, no UI.**
`Services/ServiceHealthService.cs`: the two new model types, `FetchPublicStatusAsync`,
the constants, the `PublicStatus` property on `ServiceHealthStatus`, the
`Task.WhenAll` arity change. `Program.cs`: the named `PublicStatus` HttpClient.
`ExchangeAdminWeb.Tests/ServiceHealthServiceTests.cs`: the updated
`TheGraphCollectionsAreFetchedConcurrently` (8.2) and the new tests from section 12.
Extend the internal test seam at `Services/ServiceHealthService.cs:45-48` with an
optional `Func<Task<PublicStatusBoard>>? publicStatusFactory = null` parameter,
defaulting to one that returns an unreachable board - optional, so all ~45 existing
tests in that file keep compiling untouched. Nothing renders yet.

**Slice 2 - the page and the stylesheet.**
`Components/Pages/ServiceHealth.razor`: the strip above `sh-summary`, the link-out in
the `loadError` branch, the one new key in the existing success audit `extra`
dictionary. `Components/Pages/ServiceHealth.razor.css`: the two new layout classes.
`ExchangeAdminWeb.Tests/ServiceHealthPageTests.cs`: the new page tripwires from
section 12.

**Slice 3 - version and records.**
`Modules/ModuleCatalog.cs`: `Version` 1.3.2 -> 1.4.0 and the `Description` line.
`ExchangeAdminWeb.Tests/ServiceHealthPageTests.cs`: the version assertion and rename
(8.3). This plan's `Status:` header. `.agents/state.md` and `.agents/token-log.md`
per the usual paperwork. The work stream is not "done" until this lands.

## 12. Tests

`ExchangeAdminWeb.Tests/ServiceHealthPageTests.cs:7-14` states the standing caveat and
it applies here too: there is **no bUnit harness in this repo**, nothing can render
the page, and every page-level test below is a source-text tripwire, not behavioural
coverage. A green suite is not proof the strip renders. Section 14 is where the real
evidence comes from.

Every scanner added must strip comments before matching, per the lesson recorded in
`.agents/state.md` from the migration button-gating work and reused by the existing
helpers `StripComments` (`ServiceHealthPageTests.cs:352-357`) and `MemberBody`
(`:362-380`) - reuse those, do not write new ones.

**Service tests (slice 1), real behaviour against a stubbed HTTP handler:**

1. All three surfaces returning 200 produce three entries, in the declared order,
   with the surface names this module chose.
2. `Status` `"Available"` and `"Operational"` are healthy; `"ServiceDegradation"`,
   `"Unknown"`, `"ServiceRestored"` and an invented `"somethingNew"` are not. This is
   the fail-loud rule and it is the one most likely to be got wrong.
3. The `Message` HTML is sanitized: feed the live `mac` payload from section 2.1 and
   assert the `<div>` and the anchor survive with `target="_blank"` and
   `rel="noopener noreferrer"`, and feed a `<script>` payload and assert it is gone.
4. One surface returning 500 leaves the other two intact, lists the failed one as
   not-healthy with a reason, and leaves `Reachable` true.
5. All three failing yields `Reachable = false` with a non-empty `Unreachable`.
6. **`FetchPublicStatusAsync` never throws** - drive it with a handler that throws
   `HttpRequestException`, one that times out, and one that returns malformed JSON,
   and assert `GetStatusAsync` still returns a populated board in every case. This is
   the section 9 guarantee and it is the single most important test in this plan.
7. `TheGraphCollectionsAreFetchedConcurrently`, updated per 8.2.

**Page tripwires (slice 2):**

8. The strip's `MarkupString` use is covered by the existing
   `Page_MarkupStringIsOnlyEverUsedOnSanitizedFields` with no edit to that test - add
   an assertion that the page source contains at least one `(MarkupString)` whose
   expression ends in `ContentHtml` and that it is inside the strip markup, so
   deleting the strip does not silently pass.
9. The strip is rendered above `sh-summary`: assert the index of the strip's class
   name in the page source is less than the index of `"sh-summary"`. Ordering in
   source is not ordering on screen, so this is a weak guard and the comment on it
   must say so; check 14.2 is the real evidence.
10. The page still contains exactly two `Audit.LogLookupAction` call sites (already
    guarded by `Page_AuditsBothTheSuccessAndFailurePaths`; no new test needed, but the
    implementer must run it before committing).

**Guard proof**, per `.agents/repo-guidance.md`: for each of the three mutations
below, revert, confirm the named test fails, restore, confirm byte-identical by
SHA256, confirm the suite is green.

- A: change `Task.WhenAll(servicesTask, issuesTask, publicStatusTask)` to a serial
  `await FetchPublicStatusAsync(...)` after the two-task `WhenAll`. Expect test 7 to
  fail on its new `DoesNotContain("await FetchPublicStatusAsync")` assertion.
- B: remove the `catch` from `FetchPublicStatusAsync` so it propagates. Expect test 6
  to fail.
- C: map `"Unknown"` to healthy. Expect test 2 to fail.

Note the mutation-probe restore trap recorded in memory and in
`docs/ServiceHealthLoadFeedback-Plan.md:170-172`: `Copy-Item` preserves the old
timestamp, MSBuild skips the rebuild and the probe then tests the mutated binary.
Touch each restored file.

## 13. Verification

Run before claiming any slice complete:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`

No PowerShell is touched by any slice, so `Invoke-ScriptAnalyzer` and
`Invoke-Pester tests/ps` are not applicable; say so rather than silently skipping.
CI (`.github/workflows/ci.yml`) runs the same build, format and test gates on
`windows-latest` and is an additional gate, not a replacement.

## 14. Manual acceptance checklist

Nothing in this repository reaches a rendered page, so these are the only evidence
that an operator actually sees the feature. All require a dev deploy
(`./tools/deploy-pipeline.ps1 -Dev`, from an elevated shell).

1. Open Service Health with a cold cache. A spinner appears immediately and stays up
   for the whole load - the 2026-09-18 load-feedback behaviour must be unchanged.
2. The strip renders **above** the four summary cards, and the four cards, the service
   grid and the Current Issues list are unchanged in position and appearance.
3. The strip lists three surfaces with a status badge each, and the badge colours
   match the rest of the board's vocabulary.
4. The sentence Microsoft publishes renders as formatted text, with its embedded link
   opening in a new tab and not navigating the admin console away from itself.
5. Press Refresh. Both halves update together; the button disables and re-enables.
6. Navigate away and back inside the 10-minute window. The page renders from cache
   with no long wait and no stuck spinner.
7. Switch themes across at least the default and one dark theme. No unreadable text,
   no hard-coded light-theme colour in the strip.
8. **Failure mode.** Block egress to `status.cloud.microsoft` on the dev host (a
   `hosts` entry pointing it at `127.0.0.1` is the cheapest way) and reload with a
   cold cache. The Graph board must render completely and normally; the strip must
   show the "could not read" line with a working link-out; no red `sh-error` banner;
   the page must not be measurably slower than the 5-second cap. Remove the `hosts`
   entry afterwards.
9. As a user without Service Health access, browse to `/service-health`. The redirect
   to `access-denied` still happens and no outbound call to `status.cloud.microsoft`
   is made (the `OnAfterRenderAsync` guard at
   `Components/Pages/ServiceHealth.razor:349-350` is what prevents it).
10. Confirm the sidebar shows ServiceHealth at 1.4.0 after slice 3.

## 15. Assumptions

- **Upstream cadence is about ten minutes.** Inferred from two `InternalDateTime`
  samples ten minutes apart landing on the same second, plus three samples 20 seconds
  apart showing no movement (section 2.1). Not confirmed by documentation. If it is
  actually faster, the only cost is that the board can be up to 10 minutes behind the
  public page, which is the same staleness the Graph half already has.
- **The `/api/posts/*` paths are not a contract.** Read out of a minified bundle and
  verified live, not from documentation. Section 9's never-throw design is what makes
  this acceptable rather than reckless.
- **The `Status` enum has five known values.** Read from `EBSPostStatusStrings` in the
  bundle. A sixth is handled by falling through to not-healthy.
- **No outbound proxy is required.** No proxy setting appears in any `appsettings*.json`
  in the repo, checked 2026-09-18. If the deploy hosts reach the internet through a
  proxy that the existing Graph traffic uses implicitly, this will too; if they use an
  allow-list, `status.cloud.microsoft` may need adding. Manual check 8 would surface it
  as a permanent "could not read" line rather than as an outage. Flagged, not verified.
- **Everything about the rendered page.** No test here renders anything; section 14 is
  the evidence of record, exactly as it was for
  `docs/ServiceHealthLoadFeedback-Plan.md`.

## 16. Open questions for the owner

Each answerable in one line.

1. Section 2.3 found the public page carries one sentence per surface, not a second
   incident list - it will never show an Exchange incident Graph is missing. Is
   "independent signal that Microsoft's own admin surfaces are up" still what you
   want, or does this change the ask?
2. Include the ten consumer services (`/api/posts/m365Consumer`: Outlook.com, Teams
   Free, Phone Link and so on), or leave them out as irrelevant to this tool's
   audience? Recommendation: leave out.
3. Cap the public fetch at 5 seconds, accepting that a hung public host can add up to
   5 seconds to a cold load once per 10-minute window? Recommendation: yes; say a
   different number if 5 is wrong.
4. When the Graph half fails outright, the current design shows only a static
   link-out, because the fetched public board is lost with the exception. Worth a
   follow-up plan to make the public board survive a Graph failure, or is the link-out
   enough? Recommendation: link-out is enough for now.
5. Does adding one named `HttpClient` to `Program.cs` count as a shared-infrastructure
   change requiring a base app version bump? Recommendation: no, it is module plumbing
   observable only by ServiceHealth.
6. Three slices, three sessions, three commits as laid out in section 11 - approved as
   scoped?
