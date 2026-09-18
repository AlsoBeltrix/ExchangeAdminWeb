# Service Health Public Status Plan

Status: Draft

Revision: 1 (2026-09-18). Revision 0 was committed at `16ebdf4`, reviewed by codex and
returned **unsound** with five findings. All five were verified against the file and
all five were accepted; the source design, the slicing, the timeout model and the test
seam all changed as a result. Section 18 is the review record. Read section 5.1 first -
it is the rule the rest of the design hangs on.

Owner: Michael
Repository base: `337e07b`; Revision 0 committed at `16ebdf4`
Base app version at drafting: 2.21.0 (no base bump proposed)
ServiceHealth module version: 1.3.2 at drafting; 1.4.0 after slice 1, 1.4.1 after
slice 2
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
app is redeployed routinely. That is the real cost of this integration, and it is
what drove the redesign in Revision 1 (section 17).

### 2.1.1 What path drift actually looks like on this host

Measured, not guessed. Two different shapes, and only one of them is an error:

| Request | Result |
| --- | --- |
| `/api/posts/bogus` (unrecognised leaf) | **HTTP 400** |
| `/api/posts` (recognised prefix, no leaf) | **HTTP 200, `text/html`, 2058 bytes - the SPA shell** |

The second is the dangerous one. A route rename that leaves the path matching the
SPA's catch-all returns a successful response containing an HTML document. Any
implementation that treats "HTTP 200" as "the source answered" will feed an HTML
page to a JSON deserializer. That must be a not-healthy outcome, and section 12's
drift catalogue tests exactly this body.

### 2.1.2 The RSS surfaces carry the same positive status token

`/api/feed/mac` and `/api/feed/ppac` are not a lesser format. Each RSS `<item>`
carries a custom `<status>` element with the same enum value the JSON `Status`
property carries, plus the same `Message` in `<description>`. Parsed live:

```
/api/feed/mac  -> channel "Microsoft Admin Center Status",       item status=Available
/api/feed/ppac -> channel "Power Platform Admin Center Status",  item status=Available
```

So for `mac` and `ppac` there are **two independent paths to the identical signal**,
which is what makes the dual-source design in section 6.3 possible.

### 2.1.3 The Azure RSS feed signals health by silence, and is therefore unusable as a health source

`https://rssfeed.azure.status.microsoft/en-us/status/feed/` is a genuinely published
feed: it is declared as `window.azureRSSFeedLink` in the status page's bootstrap and
is linked from `azure.status.microsoft` itself. It is also the most contract-like
URL found in this entire investigation.

It is still the wrong source for this feature. Fetched twice on 2026-09-18, it
returned 577 bytes both times, and `Content.Contains("<item")` was **false**: a
well-formed RSS channel with a `lastBuildDate` and **zero items**. Azure was healthy
at the time, and `/api/posts/azure` concurrently said `"Status":"Available"`.

An empty channel means "no Azure issues". It is therefore indistinguishable from a
truncated response, a wrong-but-parseable response, a feed that has been emptied by
a bug, or a drifted URL that happens to return a valid empty channel. Reading health
out of it would mean **rendering green on an absence of evidence**, which is the one
outcome section 5 declares worse than not shipping. Azure's RSS stays as a link-out
target and is not parsed. `azure.status.microsoft/en-us/status/history/` was also
checked and carries only resolved Post Incident Reviews, so it is not a live source
either.

### 2.1.4 The `Message` bodies are boilerplate in the healthy state

`/api/posts/ppac` carries `"AuthoredDateTime": "2025-10-09T09:33:39+00:00"` - roughly
eleven months stale at the time of writing - and `/api/posts/mac` two days. Both are
generic sentences explaining what the site is for, not status text. The operator-
useful signal in the healthy state is the `Status` token alone; the `Message` only
becomes informative when something is wrong. Section 6.5 renders it accordingly, so
the board does not carry a permanent paragraph of Microsoft boilerplate.

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
reporting a problem with the Microsoft 365 admin plane or with Azure - and must never
be able to read green because that public surface could not be reached or understood. The board must not be able to read "all green" while that public
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
- **No consumer services on the board** (`/api/posts/m365Consumer`, ten services).
  See open question 2.
- **No Power Platform admin center surface** (`/api/posts/ppac`). Dropped in
  Revision 1. It reports whether the *Power Platform admin center UI* is reachable,
  and this application never touches that UI - it reads Microsoft Graph. Its own
  message (section 2.3) says Power Platform service health lives in the Microsoft 365
  admin center, which the Graph half of this board already covers. It is the surface
  whose post is eleven months stale (2.1.4). Carrying it would add a third row of
  near-zero signal to a board whose appearance is binding, and would be the only
  reason to build RSS parsing for a second surface. See open question 7.
- **No Message Center (`serviceAnnouncement/messages`) read.** That is a different
  ask with a different audience and needs its own plan.

## 5. The governing rule, and the recommendation

### 5.1 Green requires positive affirmation

This is the rule the whole design hangs on, and it is prior to any question about
data format.

**A surface renders healthy only when the fetch successfully parsed an explicit
healthy token out of a response of the expected content type. Every other outcome is
not-healthy.** Non-200, a 200 carrying `text/html`, an unparseable body, a missing
status field, an unrecognised token, a timeout, a cancelled request, an empty
document: all of them render as "Unknown" with a reason and a link-out. None of them
can render green.

The consequence that matters: **no upstream change - path rename, shape change,
outage, or silent deprecation - can produce a green strip.** Drift degrades visibly
by construction, because health is never the default and is never inferred from the
absence of bad news. Section 12's drift catalogue is a `[Theory]` over ten concrete
drift shapes, including the real HTML-under-200 body measured in section 2.1.1, and
it asserts not-healthy for every one. That test, not a comment, is the proof.

This is also why the Azure RSS feed is rejected in 2.1.3 and why a content-type gate
is specified in 6.3: both follow directly from the rule.

### 5.2 Recommendation in one sentence

Fetch **two** surfaces server side as a third task inside the existing
`Task.WhenAll`, under one shared five-second budget - `mac` from its RSS feed with
the JSON post as a fallback, `azure` from its JSON post alone - apply the
positive-affirmation rule of 5.1, sanitize any message text through the module's
existing sanitizer, hang the result off `ServiceHealthStatus`, and render it as one
compact strip directly above the four summary cards, built so the task can never
fault and never take the Graph board down.

### 5.3 What changed from Revision 0, and why

Revision 0 made the undocumented JSON endpoints the sole primary source for three
surfaces and left RSS in prose. Codex finding 1 (HIGH) rejected that, and it was
right. The trade was then reworked against evidence, with three consequences:

- **`mac` gets two independent sources**, because 2.1.2 established that its RSS
  feed carries the identical status token. It is the one surface that actually
  answers the owner's question, so it is the one worth the redundancy.
- **`azure` keeps JSON only**, because 2.1.3 established that its RSS affirms health
  by an empty channel, which 5.1 forbids as a health source. A single-source surface
  is labelled as such on the board.
- **`ppac` is dropped entirely.** See 4.


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
    public string SurfaceName { get; init; } = "";   // "Microsoft 365 admin center", ours, not theirs
    public string Status { get; init; } = "";        // raw upstream token, "" when none was parsed
    public string StatusText { get; init; } = "";    // through HumanizeStatus
    public bool IsHealthy { get; init; }             // see 5.1: true only on positive affirmation
    public string SourceLabel { get; init; } = "";   // "RSS feed" / "status API", shown on the row
    public string? Degraded { get; init; }           // why this row is not affirmative, null when it is
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

`IsHealthy` is computed in the service, not the page, and implements 5.1 in full. It
is `true` only when a token was actually parsed **and** that token is `Available` or
`Operational`. It is `false` for `ServiceDegradation`, `Unknown`, `ServiceRestored`,
any sixth value Microsoft adds tomorrow, and for the empty string that every failure
path produces. It is never defaulted to `true` anywhere, and the property has no
setter that the page can reach. This is the same fail-loud shape the module already
uses at `ServiceHealthEntry.IsHealthy` (`Services/ServiceHealthService.cs:485`),
tightened so that "we did not get an answer" and "the answer was bad" collapse to the
same visible outcome.

### 6.3 The fetch

Two surfaces, and `mac` has two sources:

```csharp
private const string PublicStatusHost = "https://status.cloud.microsoft";

// mac: the RSS feed is primary (2.1.2 - it carries the same <status> token and is the
// surface Microsoft exposes through a user-visible RSS button), the JSON post is the
// fallback. Two independent paths to the one signal that answers the owner's question.
private const string MacFeedPath = "/api/feed/mac";    // primary,  application/rss+xml
private const string MacPostPath = "/api/posts/mac";   // fallback, application/json

// azure: JSON only. Its RSS signals health by an empty channel, which 5.1 forbids (2.1.3).
private const string AzurePostPath = "/api/posts/azure";
```

`FetchPublicStatusAsync(HttpClient client, CancellationToken ct)` runs both surfaces
and returns a `PublicStatusBoard`. Per surface it applies the same three gates, in
order, and any gate that does not pass ends that surface as not-healthy with a short
reason:

1. **Transport gate.** The response must be HTTP 200. A non-success status ends it.
2. **Content-type gate.** The response `Content-Type` must start with
   `application/json` for a JSON source or contain `xml` for an RSS source. This is
   the cheap, explicit defence against the measured HTML-under-200 drift shape in
   2.1.1, and it fires before any parser is handed the body.
3. **Parse-and-affirm gate.** The body must parse (`System.Text.Json` for JSON,
   `System.Xml.Linq.XDocument` for RSS - both in the BCL, no new package), must
   contain a status value at the expected location, and that value must be a
   recognised healthy token. Anything else is not-healthy.

For `mac`, the fallback runs **only** when the primary fails one of those gates. The
resulting row is labelled with whichever source answered (`SourceLabel`), so an
operator can see on the board that the primary path has stopped working even while
the signal is still arriving. That label is the early warning that a code change is
needed, and it is what stops the fallback from hiding drift instead of surviving it.

Per-surface failures aggregate rather than blanket-fail (Known Failure Class 2): a
failed surface is still listed, with `IsHealthy = false`, `StatusText = "Unknown"`
and its reason in `Degraded`. It is never silently dropped. `Reachable = false` only
when every surface failed, which drives the whole-strip link-out in 6.5.

Both surfaces are issued **concurrently** and share one budget; see 6.4.

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

**One shared budget, not one per request.** This was codex finding 3 against
Revision 0, and it was a real defect: `HttpClient.Timeout` applies *per request*, so
three sequential five-second requests are a fifteen-second worst case, and nothing in
Revision 0's tests would have caught an implementation that fetched them one after
another. The budget is therefore a single `CancellationTokenSource`, created once,
covering every request the method makes including the `mac` fallback:

```csharp
using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
budget.CancelAfter(PublicStatusBudget);            // TimeSpan.FromSeconds(5), one const

var macTask   = FetchMacAsync(client, budget.Token);
var azureTask = FetchAzureAsync(client, budget.Token);
await Task.WhenAll(macTask, azureTask);
```

Two properties follow, and both are tested rather than asserted in prose:

- **Concurrency.** `macTask` and `azureTask` are both started before either is
  awaited, so the wall-clock cost is the slower of the two, not their sum. Test 8 in
  section 12 fails if an implementation serializes them.
- **Total, not per-request.** `budget` is cancelled five seconds after it is created,
  so the whole method - both surfaces, plus the `mac` fallback if it runs - cannot
  exceed five seconds no matter how many requests it makes. Test 9 fails if the
  budget is per-request. Note the `mac` fallback is deliberately *inside* the budget:
  a primary that burns 4.9 seconds before failing leaves the fallback 0.1 seconds and
  it will be cancelled, which is correct - the row degrades visibly rather than
  doubling the page's worst case.

The client's own `Timeout` is set to the same five seconds as a belt-and-braces
backstop, but the `CancellationTokenSource` is the mechanism that makes the claim
true; the client timeout alone is exactly the per-request trap this paragraph exists
to close.

The client is a new named `HttpClient`, registered in `Program.cs` beside the
existing two (`Program.cs:121` `ServiceNow`, `:128` `MicrosoftGraph`):

```csharp
builder.Services.AddHttpClient("PublicStatus")
    .ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(5);
    });
```

No default `Accept` header: the two sources want different media types, so each
request sets its own, which also keeps the content-type gate in 6.3 honest.

### 6.5 What renders, and where

One strip, in `Components/Pages/ServiceHealth.razor`, inside the existing
`else if (status != null)` branch (`:71`), immediately before
`<div class="sh-summary">` (`:76`). It is not a separate tab and not a separate
section lower down, and that is a deliberate answer to the "one board or two"
question: the owner's complaint is that the board *looks green during an incident*,
and a contradicting signal parked behind a tab or below the fold does not fix a board
that looks green. It has to be where the eye already lands, above the four summary
cards.

Shape: a heading line plus **two** rows, one per surface. Each row is the surface
name, a `<span class="sh-status-badge status-@...">` using the badge element the page
already uses at `:167`, and a small source label ("RSS feed" / "status API") so a
fallback that has quietly become the only working path is visible rather than hidden.
Reuses the existing `status-healthy`, `status-warning`, `status-error`,
`status-unknown` colour classes from `Components/Pages/ServiceHealth.razor.css`. New
CSS is layout only (one new class for the strip, one for a row) and must reference
only the tokens the file already uses: `--ui-surface`, `--ui-line`, `--ui-fg`,
`--ui-fg2`, `--ui-fg3`, `--ui-warn`, `--ui-warn-bg`, `--ui-danger`, `--ui-danger-bg`,
`--ui-info`. No literal hex, no gradient. See 8.4 and 8.5 for the tests that enforce
both.

**The message text renders only when the surface is not healthy.** Section 2.1.4
measured what these `Message` bodies contain in the steady state: generic boilerplate
about what the site is for, one of them eleven months old. Printing that permanently
above the summary cards would be a paragraph of noise on a board whose appearance is
binding, and `docs/ServiceHealth-Plan.md` section 1 sets the bar at "comprehensible
at a glance". A healthy row is therefore name, badge and source label only. The
sanitized `ContentHtml` appears on the not-healthy path, which is exactly when
Microsoft's wording carries information. The sanitizer requirement in 8.1 is
unchanged by this - the field still exists, still goes through `Sanitize`, and is
still the only thing the strip ever passes to `MarkupString`.

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

Revision 1 reopened this section against codex finding 1. The three source shapes the
coordinator asked to be worked properly are the first three rows.

| Option | Verdict |
| --- | --- |
| **RSS primary where it carries a status token, JSON where it does not, labelled per row** (chosen) | `mac` from `/api/feed/mac` with `/api/posts/mac` as fallback; `azure` from `/api/posts/azure` alone. Buys two independent paths to the only signal that answers the owner's question, and the per-row source label turns a silent fallback into a visible warning. Price: two parsers (both BCL, no package) and a fallback path that production will rarely exercise, which is why section 12 test 6 drives it explicitly rather than trusting it. |
| JSON primary everywhere with recorded risk acceptance plus RSS fallback | One parser on the happy path, but it makes the *undocumented* surface primary for the one row that matters, and the risk acceptance buys nothing that 5.1 does not already buy for free. Rejected: if both sources are going to be implemented anyway, the more contract-like one should be the one that normally answers. |
| Pure RSS for everything | Cannot cover `azure`: 2.1.3 measured its feed signalling health by an empty channel, which 5.1 forbids as a health source, and `/api/feed/azure` returns 400. Would reduce the feature to `mac` alone. Kept as the fallback shape if open question 3 says `azure` is not worth a single-source row. |
| The honest minimal version: link-out only | Genuinely on the table, and section 16 answers why it was not chosen: the `mac` status token is machine-readable, positively affirmed, and answers a question the current board cannot answer at all. It remains the degraded mode (6.5) and the correct answer if open question 1 comes back "no". |
| Undocumented JSON as sole primary for three surfaces | **Revision 0's recommendation. Withdrawn** under codex finding 1 (HIGH). It made an explicitly undocumented surface load-bearing for every row while leaving the more committed RSS surface in prose. |
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
(10.3), so this literal must move in the same commit as each bump - to `"1.4.0"` in
slice 1 and to `"1.4.1"` in slice 2. Updating it *is* the test's purpose, not a
bypass, and moving it with the bump rather than after it is what keeps the suite green
at every commit boundary. Rename it once, in slice 1, to
`TheModuleVersionWasBumpedForThePublicStatusChange`, so the name still describes what
it guards.

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

**The mechanism.** `FetchPublicStatusAsync` is wrapped end to end in
`try { ... } catch (Exception ex) { ... }`, and the catch returns a
`PublicStatusBoard { Reachable = false, Unreachable = <short reason> }`. Per-surface
failures are handled one level in, as described in 6.3, so one bad surface does not
cost the other.

**The catch block must itself be incapable of throwing.** Codex finding 5 (LOW)
raised this against Revision 0, which claimed the task "can never fault" while the
catch body called the logger. Checking it turned up something worse than the
hypothetical: `_logger` is declared
`private readonly ILogger<ServiceHealthService>? _logger;`
(`Services/ServiceHealthService.cs:16`) - **nullable** - and the internal test-seam
constructor at `:45-48` sets only `_graphClientFactory`, leaving `_logger` null. So
`_logger.LogWarning(...)` inside the catch is a `NullReferenceException` on every
seam-constructed instance. That is not a theoretical logging-provider failure; it is
a live bug Revision 0 would have shipped, and it would have faulted the task and
taken the Graph board down on exactly the path meant to protect it.

Required shape, therefore:

```csharp
catch (Exception ex)
{
    try { _logger?.LogWarning(ex, "Public status fetch failed"); } catch { /* never fault the board */ }
    return PublicStatusBoard.Unreachable(Describe(ex));
}
```

Null-conditional on `_logger`, and the logging call in its own nested try/catch so a
broken logging provider cannot escape either. `Describe(ex)` must be allocation-cheap
and must not itself call into anything that can throw.

**The claim, stated at the strength it can actually be proven.** With the above, the
returned task does not fault for any HTTP, TLS, DNS, timeout, cancellation,
content-type, XML, JSON or null-reference failure arising inside the fetch, so
`Task.WhenAll` cannot observe a fault from it and `GetStatusAsync` cannot throw
because of it. It is **not** claimed to survive asynchronous or process-fatal
conditions that no `catch` block defends against - `OutOfMemoryException`,
`StackOverflowException`, or a `ThreadAbort`-class unwind - and those are out of
scope here because the Graph tasks running alongside it have no defence against them
either. Section 12 test 10 proves the provable part with a deliberately throwing
logger.

**Why this is not a Known Failure Class 3 violation.** Invariant 3 requires
authorization and enablement stores to fail closed. This is neither: it is a
read-only advisory display with no write, no gate and no decision hanging off it.
Failing closed here would mean hiding a working Graph board because a public web page
was down, which is strictly worse for the operator. It does *not* fail open in the
dangerous sense either: an unreachable source renders as an explicit "could not read"
line with a link-out, never as blank and never as green. That is the correct reading
of Known Failure Class 2 - an unreachable source must not aggregate into a healthy
report.

**Timeout.** The 5-second cap exists because of the "must not regress the
load-feedback fix" constraint, and 6.4 specifies it as one shared
`CancellationTokenSource` rather than a per-request client timeout - that distinction
was codex finding 3 and it is the difference between a 5-second and a 15-second worst
case. The public GETs run concurrently with each other and with the two Graph calls,
so in the normal case they cost nothing. The worst case is that the public host hangs
while Graph is fast, in which case the board waits up to 5 extra seconds on a cold
cache, once per 10-minute cache window. Five seconds is the honest number to argue
about; open question 4 puts it to the owner. Note that the spinner is on screen for
the whole of it - that is the point of the 2026-09-18 fix and it is not regressed,
because no new work was added ahead of the first render.

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
consequence to state explicitly: the app makes **two extra outbound requests per 10
minutes per instance** - three in the rare window where the `mac` primary has failed
and the fallback runs - plus the same again each time an operator presses Refresh
(`forceRefresh: true` bypasses the cache at `:92-98`, and must continue to refresh
every source together - a Refresh that updated only the Graph half would be a lie).

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

This is a module-scoped behaviour change, and it lands in two behaviour-changing
slices, so it takes **two bumps**: `Modules/ModuleCatalog.cs` ServiceHealth `Version`
**1.3.2 -> 1.4.0 in slice 1** (new data source, new outbound calls - minor, not patch,
because a new source is a new capability) and **1.4.0 -> 1.4.1 in slice 2** (the strip
becomes visible - patch, because it renders data the module already had). Each bump
ships in the same commit as the behaviour it describes and as the assertion that pins
it, per codex finding 2. **No base app bump** at any point; `ExchangeAdminWeb.csproj`
`<VersionPrefix>` stays 2.21.0.

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
`.agents/repo-guidance.md` Earned Practices and Token Budget.

**The version bump ships in slice 1, not at the end.** Revision 0 deferred it to
slice 3 on the reasoning that the suite must be green at every commit boundary. Codex
finding 2 (MEDIUM) showed that reasoning was simply wrong: bumping `Version` and
updating the assertion that pins it are two edits in the *same* commit, so the suite
is green either way, and deferring meant slices 1 and 2 could deploy with changed
runtime behaviour - new outbound HTTP, new data on the board - while the sidebar
still read 1.3.2. `docs/ProjectConstitution.md:112-113` says module-scoped
**behaviour** changes bump the module version, and `.agents/repo-guidance.md:110-111`
repeats it; slice 1 changes behaviour, so slice 1 carries the bump. There was never a
trade here, only a mistake.

**Slice 1 - the service, the bump, no UI.**
`Services/ServiceHealthService.cs`: the two new model types, `FetchPublicStatusAsync`
with its three gates and shared budget, the constants, the `PublicStatus` property on
`ServiceHealthStatus`, the `Task.WhenAll` arity change. `Program.cs`: the named
`PublicStatus` HttpClient. `Modules/ModuleCatalog.cs`: `Version` 1.3.2 -> 1.4.0 and
the `Description` line. `ExchangeAdminWeb.Tests/ServiceHealthServiceTests.cs`: the
updated `TheGraphCollectionsAreFetchedConcurrently` (8.2) and service tests 1-10 from
section 12. `ExchangeAdminWeb.Tests/ServiceHealthPageTests.cs`: the version assertion
and rename (8.3), in this same commit. The seam change is described in 12.1; it is
**not** a `Func<Task<PublicStatusBoard>>`. Nothing renders yet, and that is fine - the
version now truthfully says the module's behaviour changed.

**Slice 2 - the page and the stylesheet.**
`Components/Pages/ServiceHealth.razor`: the strip above `sh-summary`, the link-out in
the `loadError` branch, the one new key in the existing success audit `extra`
dictionary. `Components/Pages/ServiceHealth.razor.css`: the two new layout classes.
`ExchangeAdminWeb.Tests/ServiceHealthPageTests.cs`: page tripwires 11-13 from section
12. Bump `Version` 1.4.0 -> 1.4.1 and move the assertion with it: this slice changes
what the operator sees, which is a module-scoped behaviour change on its own terms,
and the same rule that put a bump in slice 1 puts one here.

**Slice 3 - records only.**
This plan's `Status:` header, `.agents/state.md` and `.agents/token-log.md` per the
usual paperwork. No code, no version change. The work stream is not "done" until this
lands.

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

### 12.1 The test seam: drive the production fetcher, do not replace it

Revision 0 proposed injecting `Func<Task<PublicStatusBoard>>`. Codex finding 4
(MEDIUM) rejected that and it is the most important finding after finding 1, because
it is the exact failure this repo has already been bitten by - the migration
button-gating tripwires passed the bug. A factory seam that returns a finished
`PublicStatusBoard` means **no test ever executes the URL construction, the HTTP
call, the content-type gate, the JSON or XML parse, the token mapping, the fallback,
the sanitizer call or the catch block.** Every test in section 12 would pass against
production wiring that never worked, and mutation probe B would pass too, because the
mutated code would not run. Withdrawn.

Use the pattern the file already uses. `ServiceHealthServiceTests.CreateService()`
(`ExchangeAdminWeb.Tests/ServiceHealthServiceTests.cs:54-60`) builds a real
`StubHandler : HttpMessageHandler` (`:21-52`), wraps it in a real `HttpClient`, and
passes a real `GraphTokenClient`. The seam replaces only *credential acquisition* -
everything downstream of it is production code. Do the same here:

- Extend `StubHandler.SendAsync` (`:33-48`) with a branch on
  `request.RequestUri.Host == "status.cloud.microsoft"`, routing by `AbsolutePath` to
  a settable `Func<HttpResponseMessage>` per path, and counting hits per path the way
  `HealthRequests` and `IssuesRequests` already do at `:29-30`. Path-level counters
  are what make the fallback test and the concurrency test possible.
- Extend the internal seam constructor at `Services/ServiceHealthService.cs:45-48`
  with an optional `HttpClient? publicStatusClient = null` parameter. An `HttpClient`,
  not a factory of finished results. Optional, so all ~45 existing tests in the file
  keep compiling untouched; when null, the fetch is skipped and the board reports
  itself unreachable, which is the correct behaviour for a service constructed
  without one.
- Every test below then drives `GetStatusAsync` end to end and asserts on the
  `PublicStatus` it returns. The only thing faked is the socket.

The same applies to the `ILogger` in test 10: inject a real `ILogger` implementation
whose `Log` method throws, not a mock of the catch block.

### 12.2 Service tests (slice 1)

All driven through `GetStatusAsync` against the extended `StubHandler`.

1. Both surfaces returning a well-formed affirmative response produce two entries, in
   the declared order, with the surface names and source labels this module chose.
2. **Token mapping.** `"Available"` and `"Operational"` are healthy; a `[Theory]` over
   `"ServiceDegradation"`, `"Unknown"`, `"ServiceRestored"`, `"somethingNew"` and `""`
   asserts not-healthy. This is the fail-loud rule at its narrowest.
3. **The drift catalogue - the test that makes 5.1 real.** A `[Theory]` over ten
   responses, asserting `IsHealthy == false` and a non-empty `Degraded` for every one:
   HTTP 404; HTTP 400 (the measured shape for an unrecognised leaf); HTTP 500;
   **HTTP 200 carrying the 2058-byte SPA HTML shell** (the measured shape for a
   drifted-but-matching path, 2.1.1); 200 JSON with `Status` renamed to `state`;
   200 JSON with `Status` set to an unknown token; 200 RSS with the `<status>` element
   absent; 200 with an empty body; 200 with truncated JSON; and a well-formed body
   served with `Content-Type: text/html`. This is the single most important test in
   the plan and the direct answer to codex's non-negotiable: no upstream change can
   produce a green row.
4. **Content-type gate fires before the parser.** A body that *would* parse as valid
   JSON but arrives as `text/html` is not-healthy. Proves gate 2 of 6.3 is real rather
   than incidental.
5. **Per-surface isolation.** `mac` affirmative while `azure` returns 500 leaves `mac`
   healthy, `azure` not-healthy with a reason, and `Reachable` true. Both failing
   yields `Reachable = false` with a non-empty `Unreachable`.
6. **The fallback actually runs, and is labelled.** `mac` primary returns 500, `mac`
   fallback returns an affirmative JSON post: the row is healthy, `SourceLabel` says
   the fallback answered, and the path counters show the primary was attempted exactly
   once and the fallback exactly once. A second case: primary affirmative means the
   fallback path counter stays at **zero** - the fallback must not fire on the happy
   path.
7. **Sanitizer.** Feed the live `mac` payload from section 2.1 on a *not-healthy*
   response and assert the `<div>` and anchor survive with `target="_blank"` and
   `rel="noopener noreferrer"`; feed a `<script>` payload and assert it is gone.
   Asserted on the model's `ContentHtml`, so it proves the service sanitized rather
   than that the page did.
8. **Concurrency.** The `StubHandler` for both surfaces blocks on a shared
   `TaskCompletionSource` that is only completed once the handler has observed **both**
   requests arrive, with a short test-level timeout. A serialized implementation never
   delivers the second request, the first never completes, and the test fails on the
   timeout. This fails on the exact implementation codex finding 3 said would slip
   through Revision 0.
9. **One shared budget, not per-request.** Both handlers delay longer than the budget.
   Assert the whole `GetStatusAsync` call returns within a tolerance of one budget, not
   two, and that both rows are not-healthy with a cancellation reason. Fails if the
   implementation gives each request its own five seconds.
10. **The catch cannot fault the board.** Construct the service with an `ILogger` whose
    `Log` throws, and a handler that throws `HttpRequestException`. Assert
    `GetStatusAsync` still returns, the Graph half is fully populated, and
    `PublicStatus.Reachable` is false. Without the nested try/catch of section 9 this
    test fails, and with the Revision 0 nullable-logger bug it fails too.
11. `TheGraphCollectionsAreFetchedConcurrently`, updated per 8.2.

### 12.3 Page tripwires (slice 2)

12. The strip's `MarkupString` use is covered by the existing
    `Page_MarkupStringIsOnlyEverUsedOnSanitizedFields` with no edit to that test - add
    an assertion that the page source contains at least one `(MarkupString)` whose
    expression ends in `ContentHtml`, so deleting the strip does not silently pass.
13. The strip is rendered above `sh-summary`: assert the index of the strip's class
    name in the page source is less than the index of `"sh-summary"`. Ordering in
    source is not ordering on screen, so this is a weak guard and its comment must say
    so; manual check 14.2 is the real evidence.
14. The page still contains exactly two `Audit.LogLookupAction` call sites (already
    guarded by `Page_AuditsBothTheSuccessAndFailurePaths`; no new test needed, but the
    implementer must run it before committing).

### 12.4 Guard proof

Per `.agents/repo-guidance.md`: for each mutation, revert, confirm the named test
fails, restore, confirm byte-identical by SHA256, confirm the suite is green.

- A: change `Task.WhenAll(servicesTask, issuesTask, publicStatusTask)` to a serial
  `await FetchPublicStatusAsync(...)`. Expect test 11 to fail.
- B: remove the nested try/catch around the logging call. Expect test 10 to fail.
- C: map `"Unknown"` to healthy. Expect test 2 to fail.
- D: delete the content-type gate. Expect tests 3 and 4 to fail - specifically the
  HTML-under-200 case, which is the drift shape actually measured in production.
- E: serialize the two surfaces (`await macTask; await azureTask;`). Expect test 8 to
  fail on its timeout. This probe is what proves finding 3 is closed.
- F: give each request its own timeout instead of the shared budget. Expect test 9 to
  fail.

Probes D, E and F exist because the corresponding claims in Revision 0 were prose
only. A claim in this plan that has no probe against it should be treated as not yet
proven.

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
3. The strip lists two surfaces with a status badge and a source label each, and the
   badge colours match the rest of the board's vocabulary. In the healthy state there
   is **no** paragraph of Microsoft boilerplate under either row (2.1.4, 6.5).
4. Force the `mac` primary to fail (block `status.cloud.microsoft/api/feed/` only, via
   a proxy rule or by temporarily pointing the feed path at a dead host in a local
   build) and confirm the row still reports healthy but its source label now says the
   fallback answered. This is the only way to see, before an incident, that the
   fallback and its label both work.
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
10. Confirm the sidebar shows ServiceHealth at 1.4.0 after slice 1 and 1.4.1 after
    slice 2.
11. With everything healthy, confirm the strip shows two green badges and that this
    is *affirmative* - cross-check by opening `https://status.cloud.microsoft` in a
    browser and confirming it agrees. A green strip that the operator has never once
    seen disagree with the real page is a green strip nobody should trust.

## 15. Assumptions

- **Upstream cadence is about ten minutes.** Inferred from two `InternalDateTime`
  samples ten minutes apart landing on the same second, plus three samples 20 seconds
  apart showing no movement (section 2.1). Not confirmed by documentation. If it is
  actually faster, the only cost is that the board can be up to 10 minutes behind the
  public page, which is the same staleness the Graph half already has.
- **Neither `/api/posts/*` nor `/api/feed/*` is a documented contract.** Both were
  read out of a minified bundle and verified live. The claim that RSS is the *more*
  committed of the two is a judgement, not a fact: it rests on the shipped UI binding
  a user-visible "RSS feed" button to it (`rssFeedButton`, `rssIcon`, `rssText` in the
  bundle) and on RSS being conventionally a subscriber contract. Both come from the
  same application and the same deployment, so a controller-level rename would break
  both at once. The dual-source design in 6.3 buys independence from a single-path
  change, not from a wholesale redesign. Section 5.1 is what makes even a wholesale
  redesign safe, because it degrades to visible rather than to green.
- **The `Status` enum has five known values.** Read from `EBSPostStatusStrings` in the
  bundle. A sixth is handled by falling through to not-healthy.
- **The Azure RSS feed's empty channel means "no issues".** Inferred from a single
  correlated observation: zero items while `/api/posts/azure` concurrently reported
  `"Status":"Available"`. This assumption is *why* that feed is rejected as a health
  source in 2.1.3 rather than relied on, so a wrong inference here costs nothing.
- **What a non-healthy `Message` body looks like** was never observed - both surfaces
  were healthy throughout the investigation. The sanitizer path is therefore exercised
  in tests against a synthetic not-healthy payload and against the real healthy one,
  and manual check 4 is the first time anyone will see the real thing.
- **No outbound proxy is required.** No proxy setting appears in any `appsettings*.json`
  in the repo, checked 2026-09-18. If the deploy hosts reach the internet through a
  proxy that the existing Graph traffic uses implicitly, this will too; if they use an
  allow-list, `status.cloud.microsoft` may need adding. Manual check 8 would surface it
  as a permanent "could not read" line rather than as an outage. Flagged, not verified.
- **Everything about the rendered page.** No test here renders anything; section 14 is
  the evidence of record, exactly as it was for
  `docs/ServiceHealthLoadFeedback-Plan.md`.

## 16. Is this still worth building?

Asked directly by the coordinator, and it deserves a direct answer rather than an
implicit yes buried in a task breakdown.

**Yes, but narrowly, and essentially for the `mac` surface alone.** The honest
accounting:

**What the feature is actually worth.** Strip away the machinery and the deliverable
is two status tokens, of which one carries the value. `mac` answers a question the
current board *cannot answer at all*: "is the Microsoft 365 admin plane up, and
therefore can I trust the green board above?" Today, when Graph is degraded, this
module renders a red banner containing an HTTP status code, which tells an L1 nothing
about whether Microsoft knows. That is a real gap, it is the owner's stated complaint,
and it is closed by a single boolean from a source deliberately hosted away from the
failing plane.

**What it is not worth.** Revision 0 over-bought. Four surfaces became two, because
`ppac` reports on a UI this application never touches (section 4) and `m365Consumer`
is consumer services. The `Message` bodies turned out to be boilerplate (2.1.4), so
they are no longer rendered in the steady state. What remains is roughly one service
method, two parse helpers, a strip of markup, and the tests in section 12.

**The cost that nearly sank it.** Codex finding 1 was right that undocumented
endpoints make a poor primary dependency. That cost is now bounded rather than
eliminated: 5.1 guarantees any drift degrades to a visible "Unknown" instead of a
false green, 6.3 gives the one load-bearing surface two independent paths, and the
per-row source label turns a silent fallback into an early warning. The residual risk
is that Microsoft retires both `mac` paths at once, in which case the strip reads
"Unknown" with a link-out until someone ships a fix - strictly better than today's
absence of the row, and strictly better than a stale green.

**Where the answer flips to no.** If open question 1 comes back "I expected a second
list of incidents", the premise is wrong and the right deliverable is the link-out
alone: about ten lines of markup in the `loadError` branch and above the summary
cards, no service change, no new tests, no new outbound call. That is a legitimate
outcome and it should be taken if the signal is not what was wanted. Do not build
section 6 to avoid admitting the smaller thing was enough.

## 17. Open questions for the owner

Each answerable in one line.

1. Section 2.3 found the public page carries one sentence per surface, not a second
   incident list - it will never show an Exchange incident Graph is missing. Is
   "independent signal that Microsoft's own admin surfaces are up" still what you
   want, or does this change the ask? A "no" here means build the link-out only
   (section 16).
2. Include the ten consumer services (`/api/posts/m365Consumer`: Outlook.com, Teams
   Free, Phone Link and so on), or leave them out as irrelevant to this tool's
   audience? Recommendation: leave out.
3. Keep `azure` as a second, single-source row, or narrow to `mac` alone?
   Recommendation: keep it - it is the one gap class Graph genuinely cannot cover and
   it costs one extra request.
4. Cap the whole public fetch at one shared 5 seconds, accepting that a hung public
   host can add up to 5 seconds to a cold load once per 10-minute window?
   Recommendation: yes; say a different number if 5 is wrong.
5. Does adding one named `HttpClient` to `Program.cs` count as a shared-infrastructure
   change requiring a base app version bump? Recommendation: no, it is module plumbing
   observable only by ServiceHealth.
6. Three slices as laid out in section 11, with a version bump in each of the first
   two - approved as scoped?
7. Dropping `ppac` (section 4) - agreed, or do you want the Power Platform admin
   center row after all? Recommendation: drop it.
8. When the Graph half fails outright, the design shows only a static link-out,
   because the fetched public board is lost with the exception. Worth a follow-up plan
   to make the public board survive a Graph failure, or is the link-out enough?
   Recommendation: link-out is enough for now.

## 18. Revision 1 - codex review, 2026-09-18

Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard.
Verdict: **unsound**, five findings. Capability proof passed.

The review credited Revision 0's control-flow analysis as correct - the Graph throw
sites at `Services/ServiceHealthService.cs:149` and `:181`, the `Task.WhenAll` await
at `:121` and the `LoadAsync` catch at
`Components/Pages/ServiceHealth.razor:375-381` were all confirmed at the cited lines.
Every objection was to the design.

All five citations into this plan were checked line by line against the committed file
at `16ebdf4` before any edit was made, and all five were accurate. **All five findings
are accepted; none is rejected.** Two were found to be understated, and that is
recorded here because an understated finding is as much a part of the record as a
wrong one.

| # | Sev | Finding | Disposition |
| --- | --- | --- | --- |
| 1 | HIGH | Undocumented SPA endpoints as sole primary dependency | Accepted. Source design reworked. |
| 2 | MED | Slices not independently landable; version bump deferred | Accepted. Bump moved into slice 1. |
| 3 | MED | 5-second worst case claimed but not proven; per-request timeout | Accepted. Shared budget, two new probes. |
| 4 | MED | `Func<Task<PublicStatusBoard>>` seam makes the tests vacuous | Accepted. Seam replaced. |
| 5 | LOW | Totality claim stronger than the catch proves | Accepted, and **understated** - see below. |

**Finding 1.** Accepted, and it drove the shape of the whole revision. The trade was
reworked against fresh evidence rather than reflexively swapped to RSS, as instructed.
Three new measurements decided it: the RSS feeds for `mac` and `ppac` carry the same
`<status>` token as the JSON (2.1.2), so RSS is a genuine equal and not a lesser
format; the Azure RSS signals health by an **empty channel** (2.1.3), which cannot be
distinguished from a broken or truncated response and is therefore unusable under the
non-negotiable; and path drift on this host returns either HTTP 400 **or a 200
carrying the SPA's 2058-byte HTML shell** (2.1.1), the latter being the shape most
likely to be mistaken for success. Chosen shape: the coordinator's option 1 - RSS
primary where it carries a positive token, JSON where it does not, each row labelled
with the source that answered - narrowed to two surfaces by dropping `ppac`. The
non-negotiable is met not by the format choice but by section 5.1, which makes green
require positive affirmation, and it is proven by the ten-case drift catalogue (test
3) plus probe D, not by a comment.

**Finding 2.** Accepted without qualification. Revision 0's stated reason - keeping
the suite green at every commit boundary - was simply wrong, because the bump and the
assertion that pins it are two edits in the same commit. The bump now lands in slice
1, and slice 2 carries its own, because it too changes module-scoped behaviour.

**Finding 3.** Accepted. `HttpClient.Timeout` is per-request, so Revision 0's
"5-second worst case" was false for any implementation that issued its requests in
sequence, and nothing in its test list would have caught one. Replaced with a single
`CancellationTokenSource` covering every request including the fallback (6.4), plus
test 8 (fails on serialization, via a handler that will not release until it has seen
both requests arrive) and test 9 (fails if the budget is per-request), plus probes E
and F.

**Finding 4.** Accepted, and the most valuable finding after finding 1. The proposed
factory seam would have meant no test ever executed the URL construction, the HTTP
call, the content-type gate, the parse, the token mapping, the fallback, the sanitizer
or the catch block - and mutation probe B would have passed against mutated code that
never ran, which is precisely the failure mode this repo already paid for in the
migration button-gating work. Replaced with the pattern the file already uses at
`ExchangeAdminWeb.Tests/ServiceHealthServiceTests.cs:54-60`: a real
`HttpMessageHandler` stub behind a real `HttpClient`, faking only the socket (12.1).

**Finding 5 - accepted, and understated.** It was rated LOW on the hypothetical that a
throwing logging provider would fault the task. Checking it found something concrete:
`_logger` is declared nullable at `Services/ServiceHealthService.cs:16`, and the
internal seam constructor at `:45-48` never assigns it, so an unguarded
`_logger.LogWarning(...)` inside the catch is a `NullReferenceException` on **every**
seam-constructed instance. Revision 0 would have shipped a catch block that faulted
the task and took the Graph board down on the exact path written to protect it. Fixed
with a null-conditional call inside its own nested try/catch, the absolute "can never
fault" claim narrowed to the failure classes a `catch` actually defends against, and
test 10 added with a deliberately throwing logger.

**Nothing was rejected.** Had a finding been wrong it would be refuted here with exact
lines; none was.

**Other changes in this revision, not requested by the review.** The `Message` bodies
are no longer rendered in the healthy state, because 2.1.4 measured them as
boilerplate - `/api/posts/ppac` carried an `AuthoredDateTime` eleven months old.
`ppac` was dropped as a surface. Section 16 was added to answer "is this worth
building" explicitly rather than leaving it implied. Manual checks 4 and 11 were added
to exercise the fallback label and to sanity-check a green strip against the real
page. Open questions 3 and 7 are new.

**Still open.** The eight questions in section 17. Question 1 is the one that can
still turn this into a ten-line change, and it should be answered before slice 1
starts.
