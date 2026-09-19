# Service Health Public Status Plan

Status: Draft

Revision: 3 (2026-09-18). Three codex rounds, all returned **unsound**; ten findings
total, all verified against the file and all accepted. Round 1 reshaped the source
design, the slicing, the timeout model and the test seam. Round 2 found that the `mac`
fallback defeated this plan's own no-silent-green rule. Round 3 found two cheap-pass
holes that survived a full per-assertion audit, because both lived in the
*relationship between* tests rather than inside any one of them. Sections 18, 19 and
20 carry the three review records.

**Read 5.1 and 5.1.1 first**, then **13.1**. The rule that green requires positive
affirmation is what the design hangs on; 5.1.1's predicate split is what stops the
rest of the plan from quietly undermining it - which is exactly what Revision 1 did;
and 13.1's two-part verification rule, with its four completed quadrant tables, is
what stops the suite from certifying an implementation that gets each predicate right
in the cases someone wrote down and wrong in the one they did not.

Owner: Michael
Repository base: `337e07b`; Revision 0 at `16ebdf4`, Revision 1 at `f6530e3`,
Revision 2 at `09699b9`
Base app version: **leave it unchanged, whatever it currently is** (2.21.1 as of 2026-09-19; it was 2.21.0 when this was drafted and moved under `3c21270`). No base bump is proposed or permitted by this plan - bump ServiceHealth's module version only.
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

### 5.1.1 Three predicates, kept apart on purpose

Revision 1 stated the rule above and then, forty lines later, used "healthy" as the
trigger for the `mac` fallback. Those are not the same question, and conflating them
punched a hole straight through 5.1 - codex round 2 finding 1, accepted in full and
recorded in section 18. The fix is to name the predicates separately and never let one
stand in for another:

| Predicate | Means | Decides |
| --- | --- | --- |
| `Answered(response)` | HTTP 200, expected content type, body parsed, a status value present and non-empty at the expected location | **whether the fallback fires** |
| `Known(token)` | the token is one of the five values in `EBSPostStatusStrings` (2.1) | whether the token can be interpreted |
| `Healthy(token)` | `Known(token)` **and** the token is `Available` or `Operational` | **whether the row renders green** |

Three rules follow, and they are the whole of the fallback contract:

1. **The fallback fires if and only if `!Answered(primary)`.** Not on unhealthy, not
   on unknown. "The source answered" and "the source answered healthy" are different
   questions and only the first one is the fallback's business.
2. **A recognised token is final.** If the primary answered with any of the five known
   tokens, that token is the row, and nothing may override it. RSS saying
   `ServiceDegradation` renders a degraded row even when the JSON post would have said
   `Available`, because the whole point of the primary is that it is the primary.
3. **An unrecognised-but-present token is also final, and is not-healthy.** See 5.1.2.

`Healthy` is never the default, is never inferred from the absence of bad news, and is
never reachable through a path that began with a worse answer.

### 5.1.2 Why an unrecognised token does not trigger the fallback either

Codex's finding listed "unrecognised token" among the conditions that should fall
back. This plan deliberately goes one step stricter and does **not** fall back on it.
This is a divergence from the reviewer's exact wording, not from the finding, and the
reason is that following it literally would reopen a smaller version of the same hole:
primary returns `Wibble`, we do not know what `Wibble` means, the fallback returns
`Available`, and the row goes green off a source that had just told us something
unrecognised. That is green inferred from ignorance, which 5.1 exists to forbid.

So a present-but-unrecognised token counts as `Answered`. The row renders not-healthy,
surfaces the raw token so an operator can see what arrived, and does not consult the
fallback. The cost of being wrong in this direction is one spurious "Unknown" row; the
cost of being wrong in the other direction is a green board during an outage. Test 3b
pins it.

The consequence that matters: **no upstream change - path rename, shape change,
outage, silent deprecation, or a new status word - can produce a green strip.** Drift
degrades visibly by construction, because health is never the default and is never
inferred from the absence of bad news. Section 12's drift catalogue is a `[Theory]`
over ten concrete drift shapes, including the real HTML-under-200 body measured in
section 2.1.1, and it asserts not-healthy for every one - paired with an affirmative
control in the same theory, so an implementation that simply hard-codes not-healthy
fails it. That test, not a comment, is the proof.

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

`IsHealthy` is computed in the service, not the page, and is exactly the `Healthy`
predicate of 5.1.1 - never the `Answered` one. It is `true` only when a token was
actually extracted **and** that token is `Available` or `Operational`. It is `false`
for `ServiceDegradation`, `Unknown`, `ServiceRestored`, any sixth value Microsoft adds
tomorrow, and for the empty string that every failure path produces. It is never
defaulted to `true` anywhere, and the property has no setter that the page can reach. This is the same fail-loud shape the module already
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
and returns a `PublicStatusBoard`. Per surface it first evaluates `Answered` through
three gates, and only then classifies what it got. The two stages are kept apart in
the code exactly as they are kept apart in 5.1.1, because collapsing them is the
defect codex round 2 found.

**Stage 1 - `Answered`. All three gates must pass, in order:**

1. **Transport gate.** The response must be HTTP 200. A non-success status ends it.
2. **Content-type gate.** The response `Content-Type` must start with
   `application/json` for a JSON source or contain `xml` for an RSS source. This is
   the cheap, explicit defence against the measured HTML-under-200 drift shape in
   2.1.1, and it fires before any parser is handed the body.
3. **Extraction gate.** The body must parse (`System.Text.Json` for JSON,
   `System.Xml.Linq.XDocument` for RSS - both in the BCL, no new package) and must
   yield a **present, non-empty** status value at the expected location. Note what
   this gate does *not* do: it does not look at what the value says. Extracting
   `ServiceDegradation` passes this gate exactly as `Available` does.

**Stage 2 - classification. Not a gate; it cannot send anything to the fallback:**

- `Healthy` - the token is `Available` or `Operational`. Green row.
- Known and not healthy - `ServiceDegradation`, `Unknown`, `ServiceRestored`.
  Not-green row carrying that token's text.
- Present but unrecognised - not-green row, raw token surfaced verbatim so an operator
  can see what actually arrived (5.1.2).

**The fallback trigger, stated once and precisely.** For `mac`, the JSON post is
consulted **if and only if the RSS primary failed stage 1** - that is, `!Answered`.
Transport failure, wrong content type, unparseable body, or a missing or empty status
value. Nothing in stage 2 can trigger it. Concretely: RSS returning
`ServiceDegradation` renders a degraded row and the JSON post is **never requested**,
even though it would have said `Available`. Revision 1 got this wrong and would have
rendered that case green; test 3b and probe G exist solely to keep it wrong-proof.

**"iff" means all five failure classes, not just the transport one.** The fallback
must fire on a non-200 *and* on a 200 with the wrong content type *and* on an
unparseable body *and* on a missing status element *and* on a present-but-empty one.
Revision 2 asserted this in prose but tested only the non-200 shape, so an
implementation keyed on `!response.IsSuccessStatusCode` passed the whole suite - codex
round 3 finding 1. The production cost of that shortcut is precisely the scenario this
plan was built around: `/api/feed/mac` drifts to the measured 200-plus-HTML-shell
shape (2.1.1), `/api/posts/mac` is still sitting there saying `Available`, and the row
degrades to Unknown instead of using the fallback and flagging it in `SourceLabel`.
Test 4 is now a theory over all five classes and probe I is the regression guard.

The resulting row is labelled with whichever source answered (`SourceLabel`), so an
operator can see on the board that the primary path has stopped working even while
the signal is still arriving. That label is the early warning that a code change is
needed, and it is what stops the fallback from hiding drift instead of surviving it.

Per-surface failures aggregate rather than blanket-fail (Known Failure Class 2): a
surface that never reached `Answered` is still listed, with `IsHealthy = false`,
`StatusText = "Unknown"` and its reason in `Degraded`. It is never silently dropped.
`Reachable = false` only when no surface reached `Answered`, which drives the
whole-strip link-out in 6.5. Note the deliberate asymmetry: `Reachable` tracks
`Answered`, never `Healthy`, so a board where both surfaces report degradation is
`Reachable = true` with two red rows - which is the correct reading, since the source
is working perfectly and the news is simply bad.

That asymmetry is load-bearing and was untested until Revision 3. `Reachable =
Surfaces.Any(s => s.IsHealthy)` passed Revision 2's entire matrix, because nothing
exercised the quadrant where every surface answers and none is healthy - codex round 3
finding 2. Under that implementation the strip would announce "the public status
source could not be read" at the exact moment it had been read and was reporting
trouble, inverting the message precisely when the feature is supposed to earn its
place. Test 6b covers both uncovered quadrants and probe J is the regression guard.

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
  exceed five seconds no matter how many requests it makes. Test 9 - rewritten in
  12.3.1, because Revision 1's version could not tell this model from the broken one -
  exercises the fallback sequence, which is the only path where the two disagree.
  Note the `mac` fallback is deliberately *inside* the budget:
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
| **RSS primary where it carries a status token, JSON where it does not, labelled per row** (chosen) | `mac` from `/api/feed/mac` with `/api/posts/mac` as fallback; `azure` from `/api/posts/azure` alone. Buys two independent paths to the only signal that answers the owner's question, and the per-row source label turns a silent fallback into a visible warning. Price: two parsers (both BCL, no package) and a fallback path that production will rarely exercise, which is why section 12 tests 3b and 4 drive it as a differential pair - one proving it fires when the primary is unusable, the other proving it cannot fire merely because the primary said something unwelcome. |
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
`<VersionPrefix>` is left **untouched**. Do not write a literal here: it was 2.21.0 at drafting and is 2.21.1 as of 2026-09-19, and an implementer following a stale literal would DOWNGRADE it.

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
updated `TheGraphCollectionsAreFetchedConcurrently` (8.2) and service tests 1 to 11,
including 3b, 4b and 6b, from section 12.3. `ExchangeAdminWeb.Tests/ServiceHealthPageTests.cs`:
the version assertion and rename (8.3), in this same commit. The seam change is
described in 12.1; it is
**not** a `Func<Task<PublicStatusBoard>>`. Nothing renders yet, and that is fine - the
version now truthfully says the module's behaviour changed.

**Slice 2 - the page and the stylesheet.**
`Components/Pages/ServiceHealth.razor`: the strip above `sh-summary`, the link-out in
the `loadError` branch, the one new key in the existing success audit `extra`
dictionary. `Components/Pages/ServiceHealth.razor.css`: the two new layout classes.
`ExchangeAdminWeb.Tests/ServiceHealthPageTests.cs`: page tripwires 12 to 14 from
section 12.4, both of the new ones written to fail probe H (strip deleted). Bump `Version` 1.4.0 -> 1.4.1 and move the assertion with it: this slice changes
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

### 12.2 The standing rule every assertion below has been put through

Codex has now found the same class of defect three rounds running: round 1, a seam
that let every test pass without running the fetcher; round 2, one assertion satisfied
by code that already exists in the file and another satisfied by a sentinel `-1`;
round 3, two predicates that a wrong implementation could satisfy across the whole
suite. The repo has a recorded precedent in the migration button-gating work, where
the guard proof's first mutation was literally the pre-fix code because the tripwires
accepted it.

So this is a standing rule for this plan, not a one-off fix. It has two parts, both
stated in full in 13.1:

> **Part 1, per assertion.** Name the cheapest broken implementation that makes it
> pass. If the answer is "delete the feature", "change nothing", "hard-code one value"
> or "never call the thing under test", the assertion is decoration and must be
> strengthened before it is written.
>
> **Part 2, per predicate.** Then take each predicate the design depends on and name
> the cheapest implementation of *that predicate* which satisfies the whole suite.
> Enumerate its input quadrants; the holes are the ones nothing exercises.

Part 2 exists because Part 1 is not sufficient: it was applied to all fourteen tests
in Revision 2 and two defects still got through, both of them living in the
*relationship between* tests rather than inside any one of them. The four completed
quadrant tables are in 13.1.

Every test below carries its Part 1 answer explicitly in the third column. Three
assertions were rewritten in Revision 2 because their honest answer was "delete the
feature", and three more cases were added in Revision 3 from the Part 2 sweep; all are
flagged in the table.

### 12.3 Service tests (slice 1)

All driven through `GetStatusAsync` against the extended `StubHandler`.

| # | Test | Cheapest broken implementation that would pass - and what stops it |
| --- | --- | --- |
| 1 | **Round-trip, not shape.** Both surfaces answer affirmatively with a *distinctive* token per surface; assert the two entries carry those tokens, the declared order, the surface names, the source labels, **and** that each path's request counter is exactly 1. | Returning two hard-coded entries and never issuing a request. Stopped by the counters and by asserting the token came back from the body. Revision 1's version asserted only names and order and would have passed a fully stubbed-out fetcher. |
| 2 | **Token mapping, both polarities in one `[Theory]`.** `Available` and `Operational` healthy; `ServiceDegradation`, `Unknown`, `ServiceRestored`, `somethingNew`, `""` not-healthy. | `IsHealthy => false`. Stopped only because the healthy cases are in the *same* theory. Splitting the polarities into two tests would reintroduce the cheap pass. |
| 3 | **The drift catalogue.** Ten responses, all asserting `IsHealthy == false` and a non-empty `Degraded`: 404; 400; 500; **200 carrying the 2058-byte SPA HTML shell** (the measured drift shape, 2.1.1); 200 JSON with `Status` renamed to `state`; 200 JSON with an unknown token; 200 RSS with `<status>` absent; **200 RSS with `<status></status>` present but empty**; 200 with an empty body; 200 with truncated JSON; well-formed body served as `text/html`. **Plus an affirmative control row in the same theory asserting healthy.** | `IsHealthy => false`. Stopped by the affirmative control, which Revision 1 omitted - without it this whole catalogue is passed by a constant. The present-but-empty row was added in Revision 3: the extraction gate at 6.3 requires a **present, non-empty** value, and nothing exercised the empty half of that conjunction. |
| 3b | **The fallback cannot launder an unhealthy primary.** RSS primary answers `ServiceDegradation`; JSON fallback is stubbed to answer `Available`. Assert the row is **not-healthy**, `SourceLabel` names the RSS primary, and the **JSON path counter is 0**. A second case: primary answers an unrecognised `Wibble`; assert not-healthy, raw token surfaced, JSON counter still 0. | Never implementing the fallback at all. Stopped by pairing with test 4, which *requires* the fallback to fire; the two are a differential pair and neither is sound alone. **This is the codex round 2 finding 1 guard.** |
| 4 | **The fallback fires on every `!Answered` class, and is labelled.** A `[Theory]` with one row per stage-1 failure class, each making the RSS primary fail that way while the JSON fallback returns an affirmative post: **(a)** non-200; **(b)** 200 carrying the SPA HTML shell (wrong content type); **(c)** 200 `application/rss+xml` with an unparseable body; **(d)** 200 well-formed RSS with `<status>` absent; **(e)** 200 well-formed RSS with `<status></status>` empty. Every row asserts the mac row is **healthy**, `SourceLabel` names the fallback, primary counter 1 and fallback counter 1. Plus the negative control: primary affirmative, fallback counter **0**. | **Falling back only on non-200** and treating (b) to (e) as terminal Unknown. Revision 2's version used case (a) alone, so the whole class passed - codex round 3 finding 1. Stopped by rows (b) to (e), each of which fails against a transport-only trigger, and by probe I. |
| 4b | **A failing fallback does not invent an answer.** RSS primary returns 500 and the JSON fallback also fails (200 HTML shell). Assert the mac row is not-healthy, **both** counters are 1, and `Degraded` records that both sources were tried rather than only the last one. | Reporting only the fallback's reason, or silently rendering the row as if the primary had never been attempted. Stopped by the both-counters and both-reasons assertions. Added in Revision 3 from the quadrant sweep: nothing covered "primary unusable **and** fallback unusable". |
| 5 | **Content-type gate fires before the parser.** The identical valid-JSON body served once as `application/json` (healthy) and once as `text/html` (not-healthy). | `IsHealthy => false`. Stopped because the same body must produce opposite results, so only the gate can explain the difference. |
| 6 | **Per-surface isolation.** `mac` affirmative, `azure` 500: `mac` healthy, `azure` not-healthy with a reason, `Reachable` true, **and the mac fallback counter is 0**. Both failing: `Reachable` false with non-empty `Unreachable`. | Blanket-failing the board on any error (Known Failure Class 2). Stopped by the mixed case. The added fallback-counter assertion stops a fallback mis-wired to fire on *any* surface's failure rather than on the mac primary's - which every other test in this table would have accepted. |
| 6b | **`Reachable` tracks `Answered`, never `Healthy`.** A `[Theory]` over the two uncovered quadrants: **(a)** every surface answers and **none is healthy** (mac `ServiceDegradation`, azure an unrecognised token); **(b)** one surface answers unhealthily and the other never answers (mac `ServiceDegradation`, azure 500). Both rows assert `Reachable == true`, `Unreachable == null`, both surfaces still listed with their tokens, and the mac fallback counter 0. | **`Reachable = Surfaces.Any(s => s.IsHealthy)`** - codex round 3 finding 2. It satisfies every other test in this table, and it inverts the strip's message at the exact moment the feature earns its place: the board would say "the public status source could not be read" when in fact it was read perfectly and the news was bad. Stopped by quadrant (a), which no earlier test reached. Probe J pins it. |
| 7 | **Sanitizer.** The live `mac` payload from 2.1 on a not-healthy response: `<div>` and anchor survive with `target="_blank"` and `rel="noopener noreferrer"`. A `<script>` payload: gone. Asserted on the model's `ContentHtml`. | `ContentHtml => ""`. Stopped by the survives-intact half. |
| 8 | **Concurrency.** Both handlers block on a shared `TaskCompletionSource` completed only once the handler has seen **both** requests, with a short test-level timeout. Assert both rows healthy. | Serializing the two surfaces. The first request never completes, the budget cancels it, both rows come back not-healthy, and the healthy assertion fails. |
| 9 | **One shared budget across the whole method, including the fallback.** See 12.3.1 - rewritten this round. | See 12.3.1. |
| 10 | **The catch cannot fault the board.** An `ILogger` whose `Log` throws, plus a handler that throws `HttpRequestException`. Assert `GetStatusAsync` returns, the Graph half is fully populated, `PublicStatus.Reachable` is false, **and the logger was actually invoked at least once**. | Never calling the logger at all, which would make probe B a no-op. Stopped by the invocation-count assertion. |
| 11 | `TheGraphCollectionsAreFetchedConcurrently`, updated per 8.2. | It is a source scan and is inherently weak; test 8 is its behavioural counterpart. Kept for the `DoesNotContain("await FetchPublicStatusAsync")` clause, which no behavioural test covers. |

#### 12.3.1 Test 9, rewritten: the budget test must exercise the fallback sequence

Codex round 2 finding 2, accepted. Revision 1's test 9 had both handlers delay past
the budget and asserted the whole call returned within one budget. With `mac` and
`azure` running **concurrently**, per-request five-second timeouts satisfy that too,
so the test could not tell the correct model from the broken one.

The discriminator is the **sequential** path, which is the `mac` fallback:

- `mac` primary: delays until just under the budget, then returns 500 (so `!Answered`
  and the fallback is required to run).
- `mac` fallback: would answer affirmatively, but only after a further delay.
- `azure`: answers immediately, so it cannot mask the timing.

With one shared budget the fallback is cancelled, total elapsed is approximately one
budget, and the `mac` row is not-healthy with a cancellation reason. With per-request
timeouts the fallback gets a fresh five seconds and total elapsed is approximately
two budgets. Assert **elapsed < 1.5 budgets** and that the `mac` row is not-healthy.
The assertion is on elapsed time, because that is the only thing the two models
disagree about.

Use a short budget in tests - inject the budget as a constructor parameter or an
`internal` field rather than hard-coding five seconds - so this test costs
milliseconds, not seconds. A ten-second unit test will be deleted by the next person
who touches the suite.

### 12.4 Page tripwires (slice 2)

Both of the tripwires Revision 1 proposed here were passed by deleting the feature.
Codex round 2 finding 3, verified against the file and accepted on both counts.

| # | Test | Cheapest broken implementation - and what stops it |
| --- | --- | --- |
| 12 | **Scoped to the strip, not the file.** Extract the strip's markup block by its class name first, assert the block was found, then assert *that block* contains a `(MarkupString)` whose expression ends in `ContentHtml`. | **Deleting the strip entirely.** Revision 1 asserted only that the *page* contained such an expression - and `Components/Pages/ServiceHealth.razor:296` already contains `@((MarkupString)post.ContentHtml)` in the incident timeline, verified in the live file this round. The assertion was satisfied before a line of the feature was written. Stopped by extracting the block and failing if it is absent. |
| 13 | **Presence before ordering.** `stripIndex = source.IndexOf(<strip class>)`, `summaryIndex = source.IndexOf("sh-summary")`; assert `stripIndex >= 0`, assert `summaryIndex >= 0`, *then* assert `stripIndex < summaryIndex`. | **Deleting the strip entirely.** `IndexOf` returns `-1` for a missing class and `-1 < summaryIndex` is true, so Revision 1's bare ordering comparison passed with no strip at all. Stopped by the two presence assertions. `sh-summary` occurs exactly once in the file, verified this round, so the index is unambiguous. Ordering in source is still not ordering on screen - manual check 14.2 remains the real evidence, and the test's comment must say so. |
| 14 | The page still contains exactly two `Audit.LogLookupAction` call sites. | Already guarded by the existing `Page_AuditsBothTheSuccessAndFailurePaths`; no new test, but the implementer must run it before committing. |

### 12.5 Guard proof

Per `.agents/repo-guidance.md`: for each mutation, revert, confirm the named test
fails, restore, confirm byte-identical by SHA256, confirm the suite is green.

| Probe | Mutation | Must fail |
| --- | --- | --- |
| A | `Task.WhenAll(servicesTask, issuesTask, publicStatusTask)` becomes a serial `await FetchPublicStatusAsync(...)` | test 11 |
| B | Remove the nested try/catch around the logging call | test 10 |
| C | Map `"Unknown"` to healthy | test 2 |
| D | Delete the content-type gate | tests 3 and 5 - specifically the HTML-under-200 case, the drift shape actually measured in production |
| E | Serialize the two surfaces (`await macTask; await azureTask;`) | test 8, on its barrier timeout |
| F | Give each request its own timeout instead of the shared budget | test 9, on elapsed time |
| **G** | **Trigger the fallback on `!Healthy` instead of `!Answered`** - that is, restore Revision 1's exact logic | **test 3b.** This probe is the codex round 2 finding 1 regression guard, and it is the one to run first, because the mutation is a plausible thing for a future reader to "simplify" the code back into |
| **H** | **Delete the public-status strip from the page entirely** | **tests 12 and 13.** Under Revision 1's assertions this mutation passed both, which is what finding 3 caught. If it passes either one after the slice-2 work, the tripwire is still decoration |
| **I** | **Trigger the fallback on non-200 only** - `if (!response.IsSuccessStatusCode)` in place of `if (!answered)` | **test 4, rows (b) to (e).** Codex round 3 finding 1. Under Revision 2's single-shape test 4 this mutation passed the entire suite, while in production a drifted `/api/feed/mac` returning the HTML shell would degrade to Unknown instead of using the fallback that was sitting right there |
| **J** | **`Reachable = Surfaces.Any(s => s.IsHealthy)`** | **test 6b, quadrant (a).** Codex round 3 finding 2. This mutation passed Revision 2's whole matrix and would have told the operator the public source could not be read at the precise moment it had been read and was reporting trouble |

Probes D through J each exist because the corresponding claim was prose-only, or the
corresponding assertion was satisfiable without the feature, in an earlier revision.
**A claim in this plan with no probe against it should be treated as not yet proven.**
Probes G and H are the standing regression guards for the two defects that got
furthest before being caught; probes I and J are the guards for the two that survived
a full per-assertion audit and were only caught by the predicate-level sweep in 13.1.

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

### 13.1 Standing rule: price every assertion before writing it

A green suite is not evidence. Across two review rounds this plan has shipped three
assertions that a broken implementation would have passed - a seam that skipped the
fetcher entirely, a `MarkupString` check already satisfied by
`Components/Pages/ServiceHealth.razor:296`, and an ordering check that `IndexOf`
returning `-1` satisfied with the feature deleted. The repo has prior form here: the
migration button-gating guard proof's first mutation was the pre-fix code, because the
tripwires accepted it.

So, as a gate on the test list rather than advice:

> **Before writing any assertion, name the cheapest broken implementation that makes
> it pass. If the answer is "delete the feature", "change nothing", "hard-code one
> value", or "never call the thing under test", the assertion is decoration.
> Strengthen it, or pair it with an assertion of the opposite polarity, before it goes
> in.**

Two structural habits fall out of it and both are already applied in section 12:

- **Both polarities in the same test.** A catalogue of negative cases is passed by a
  constant. Test 3 carries an affirmative control for exactly this reason.
- **Differential pairs for optional behaviour.** Anything that "sometimes happens"
  needs one test that it *does* under condition X and another that it *does not*
  under condition Y, or a no-op implementation passes. Tests 3b and 4 are that pair
  for the fallback.

#### Part 2: the per-assertion pass is not sufficient, and here is the proof

Revision 2 applied the rule above to all fourteen tests and it caught five real holes.
Two defects survived it anyway, and codex round 3 found both. The reason they survived
is structural rather than careless, and it is the lesson actually worth recording:

> **Both defects lived in the relationship *between* tests, not inside any one of
> them.** Test 4 was sound on its own - it proved the fallback fires. What let the
> wrong implementation through was the *absence* of the other failure classes. Test 6
> was sound on its own. What let it through was an *uncovered quadrant*. A rule that
> asks its question one assertion at a time is structurally incapable of finding
> either.

So the rule has a second half, and it runs after the per-assertion pass:

> **Take each predicate the design depends on. Ask what the cheapest implementation of
> *that predicate* is which satisfies the entire suite. Enumerate the predicate's
> input quadrants and mark which test reaches each one. The holes are the quadrants
> nothing exercises.**

The difference in practice: part 1 asks "what breaks this test?", part 2 asks "what
breaks this *concept* while passing every test?". Only the second finds a predicate
that is right in the three cases you wrote down and wrong in the fourth you did not.

#### The completed quadrant tables

Four predicates carry this design. Every quadrant below now names the test that
reaches it; the three marked **new in R3** were empty when codex round 3 arrived, and
two of them are the findings.

**`Answered(response)`** - gates the fallback and feeds `Reachable`.

| Quadrant | Reached by |
| --- | --- |
| 200, right content type, parses, status present | 1, 3 control, 4 control, 5 |
| non-200 | 3, 4(a) |
| 200, wrong content type | 3, 5, **4(b) new in R3** |
| 200, right content type, unparseable | 3, **4(c) new in R3** |
| 200, parses, status element absent | 3, **4(d) new in R3** |
| 200, parses, status present but empty | **3 and 4(e), both new in R3** |

The last row was empty in *both* directions - the extraction gate requires a
"present, non-empty" value and nothing exercised the empty half of that conjunction.
That one was found by this sweep, not by the review.

**`Healthy(token)`** - gates the green row.

| Quadrant | Reached by |
| --- | --- |
| `Available` / `Operational` | 2, 3 control |
| known unhealthy (`ServiceDegradation`, `Unknown`, `ServiceRestored`) | 2, 3b(a), 6b |
| present but unrecognised | 2, 3b(b), 6b(a) |
| absent / empty | 2, 3 |
| healthy-looking body behind a wrong content type | 5 |

Sound before this round. Test 5 is what stops `Healthy` being computed by sniffing the
body independently of the gates.

**`Reachable(board)`** - gates the "could not read" line.

| any answered? | any healthy? | Expected | Reached by |
| --- | --- | --- | --- |
| yes | yes | true | 1, 6 |
| partly | yes | true | 6 |
| **yes** | **no** | **true** | **6b(a) new in R3** |
| **partly** | **no** | **true** | **6b(b) new in R3** |
| no | no | false | 6, 10 |

Row 3 is codex round 3 finding 2. `Any(IsHealthy)` satisfies rows 1, 2 and 5, which
was the whole of Revision 2's coverage.

**"fallback fired"** - gates the second request.

| Primary state | Expected | Reached by |
| --- | --- | --- |
| Answered + healthy | no fallback | 4 control |
| Answered + known unhealthy | no fallback | 3b(a) |
| Answered + unrecognised | no fallback | 3b(b) |
| !Answered, transport | fallback | 4(a) |
| **!Answered, other four gates** | **fallback** | **4(b)-(e) new in R3** |
| !Answered, fallback also unusable | both tried, row unhealthy | **4b new in R3** |
| a *different* surface failed | no mac fallback | **6, assertion added in R3** |

Row 5 is codex round 3 finding 1. Rows 6 and 7 came out of this sweep: nothing covered
"both sources unusable", and nothing stopped a fallback mis-wired to fire on any
surface's failure.

The implementer must record, in the slice's commit message or the review packet, the
cheapest-broken-implementation answer for any assertion they add that is not already
priced in section 12 - and, if they add or change a predicate, its completed quadrant
table.

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

## 19. Revision 2 - codex review round 2, 2026-09-18

Reviewer: codex, second pass over Revision 1 as committed at `f6530e3`.
Verdict: **unsound**, three findings.

Closed and not revisited, per the coordinator: the nullable-logger reading, the seam
replacement, the slice-1 version bump, and section 16 as honest scoping.

All three citations were checked against the committed file before any edit.
**All three verified exactly as cited. All three accepted; none rejected.** One
carries a deliberate divergence in the *fix*, flagged below and in 5.1.2.

| # | Sev | Finding | Disposition |
| --- | --- | --- | --- |
| 1 | HIGH | The `mac` fallback defeats the plan's own no-silent-green rule | Accepted. Predicates split in 5.1.1; fallback trigger rewritten in 6.3. |
| 2 | MED | Test 9 passes the broken timeout model | Accepted. Rewritten around the fallback sequence in 12.3.1. |
| 3 | MED | Two slice-2 tripwires pass with the strip deleted | Accepted on both counts, both independently verified. Rewritten in 12.4. |

**Finding 1 - verified and accepted.** The file said, at the cited lines: gate 3
required "a **recognised healthy token**. Anything else is not-healthy" (:473), the
fallback ran "**only** when the primary fails one of those gates" (:475), and
`ServiceDegradation`, `Unknown` and `ServiceRestored` were listed as recognised but
not healthy (:435). Those three sentences compose exactly as codex said: RSS returns
`ServiceDegradation`, gate 3 rejects it, the fallback fires, JSON says `Available`,
and the row renders green while the primary public source is reporting trouble - the
precise outcome 5.1 exists to make unreachable, produced by the mechanism added to
make the design robust. A self-inflicted hole, and the worst kind, because the
mechanism that opened it was the one advertised as the safety feature.

The diagnosis - a conflation of two predicates - was correct and is the fix. Section
5.1.1 now names three predicates in a table and states the fallback contract as three
numbered rules; 6.3 is restructured into an explicit `Answered` stage of three gates
and a separate classification stage that cannot reach the fallback at all. The former
gate 3 is now an *extraction* gate that deliberately does not look at what the value
says.

**The sweep codex asked for was done**, over 5.1, 6.2, 6.3 and 6.4. Two further
instances of the same wording were found and fixed: 6.3's "any gate that does not pass
ends that surface as not-healthy" (a gate failure ends it as not-*answered*, which is
then classified), and 6.2's `IsHealthy` paragraph, which cited "5.1 in full" as though
one predicate covered both jobs. The reviewer's instinct that "if it happened once in
wording it will have happened twice" was right.

**One deliberate divergence, in the fix and not the finding.** Codex listed
"unrecognised token" among the conditions that should fall back. This plan does not
fall back on it - see 5.1.2. Following the wording literally reopens a smaller version
of the same hole: primary says `Wibble`, we cannot interpret `Wibble`, fallback says
`Available`, row goes green off a source that had just reported something unknown.
That is green inferred from ignorance. A present-but-unrecognised token therefore
counts as `Answered`, renders not-healthy with the raw token surfaced, and does not
consult the fallback. Stricter than asked, in the same direction as the finding. Test
3b's second case pins it; if the owner or the reviewer prefers the literal reading,
this is a one-line change to 5.1.2 and one case in 3b.

**Finding 2 - verified and accepted.** Test 9 at :1050-1053 required only that both
handlers delay past the budget and the call return within one budget. Since `mac` and
`azure` run concurrently, per-request timeouts satisfy that identically - the test
could not distinguish the model 6.4 specifies from the model it was written to
forbid. The discriminator is the one genuinely *sequential* path, the `mac` fallback,
and 12.3.1 now builds the test on it: primary delays then fails so the fallback is
required, fallback would answer but only after further delay, azure answers
immediately so it cannot mask the timing. Shared budget gives about one budget
elapsed; per-request gives about two. The assertion is on elapsed time, because that
is the only thing the two models disagree about. Probe F fails on it. The budget is
also made injectable so the test costs milliseconds - a ten-second unit test gets
deleted by the next person through.

**Finding 3 - verified independently on both counts, and accepted.**
`Components/Pages/ServiceHealth.razor` was re-read this round rather than recalled:
`MarkupString` appears at :262, :273 and **:296**, and :296 is
`<div>@((MarkupString)post.ContentHtml)</div>`. So Revision 1's test 12 - "the page
contains a `(MarkupString)` expression ending in `ContentHtml`" - was satisfied before
a single line of this feature existed. Test 13 compared `IndexOf` results with no
presence check, and `IndexOf` returns `-1` for an absent class, so `-1 < summaryIndex`
passed with no strip on the page. Both rewritten in 12.4: test 12 extracts the strip
block by class name and fails if the block is absent before asserting anything about
its contents; test 13 asserts `stripIndex >= 0` and `summaryIndex >= 0` before
comparing. `sh-summary` was confirmed to occur exactly once in the file, so the index
is unambiguous. Probe H - delete the strip - is now a standing mutation that both must
fail.

**The pattern, named in the plan as the coordinator asked.** Three rounds, three
instances of the same class: an assertion that a broken implementation satisfies.
Round 1's seam skipped the fetcher; round 2's tripwires were satisfied by pre-existing
code and by a sentinel value. New section 13.1 makes the check a standing gate -
*name the cheapest broken implementation that passes this assertion, and if the answer
is "delete the feature", "change nothing", "hard-code one value" or "never call the
thing under test", it is decoration* - and section 12.3 now carries that answer for
every single test in a dedicated column.

**Running that audit found three more cheap passes that codex did not flag**, all
fixed in this revision:

- **Test 1** asserted only entry names, order and source labels, so a fetcher that
  returned two hard-coded entries and issued no HTTP at all would have passed. Now
  asserts distinctive tokens round-tripped from the stubbed bodies plus per-path
  request counters.
- **Test 3**, the drift catalogue, asserted not-healthy across ten cases - passed
  wholesale by `IsHealthy => false`. Now carries an affirmative control row inside the
  same theory.
- **Test 10** would have been passed by an implementation that never calls the logger,
  which would also have made probe B a no-op. Now asserts the logger was actually
  invoked.

Test 5 (content-type) was also restructured to serve the identical body under two
content types, so only the gate can explain the differing results.

**Nothing was rejected this round either.** Eight findings across two rounds, eight
accepted.

## 20. Revision 3 - codex review round 3, 2026-09-18

Reviewer: codex, third pass over Revision 2 as committed at `09699b9`.
Verdict: **unsound**, two MEDIUM findings.

Confirmed closed by the reviewer and not revisited: round 2's three findings are all
shut in the text; versioning is module-scoped only; no Constitution or module-contract
violation; no unrecoverable first-deploy state. **The Revision 2 divergence was
upheld** - the stricter reading of an unrecognised token (answered, not-healthy, no
fallback) was judged correct, so 5.1.2 stands as written.

Both citations were checked against the committed file before any edit.
**Both verified exactly as cited. Both accepted; neither rejected.** Ten findings
across three rounds, ten accepted.

| # | Sev | Finding | Disposition |
| --- | --- | --- | --- |
| 1 | MED | Test 4 proves the fallback fires on transport failure only, not on every `!Answered` class | Accepted. Test 4 is now a theory over all five classes; probe I added. |
| 2 | MED | `Reachable` can be implemented as `Any(IsHealthy)` and pass the whole matrix | Accepted. Test 6b added over both uncovered quadrants; probe J added. |

**Finding 1 - verified and accepted.** `Answered` is three gates at :517-528 and the
fallback is stated as iff stage 1 failed, explicitly naming wrong content type,
unparseable body, and missing or empty status (:538-541) - but test 4 at :1115 used a
single shape, RSS returns 500, and probe G at :1174 only mutated `!Healthy` versus
`!Answered`. So `if (!response.IsSuccessStatusCode)` passed the entire suite while
violating the iff contract. The production consequence is the exact scenario this plan
was built around and is worse for being so: `/api/feed/mac` drifts to the measured
200-plus-HTML-shell shape, `/api/posts/mac` is still answering `Available`, and the
row degrades to Unknown rather than falling back and flagging it in `SourceLabel`. The
one drift shape the plan has hard evidence for was the one the guard did not cover.
Test 4 is now a `[Theory]` with a row per stage-1 failure class - non-200, wrong
content type, unparseable, status absent, status empty - each asserting a healthy row,
the fallback source label, and both counters at 1. Test 3b is unchanged and remains
the opposite-polarity proof.

**Finding 2 - verified and accepted.** 6.3 separates the concepts explicitly at
:551-557 and 6.5 renders a single "could not read" line when `Reachable` is false
(:668-672), but nothing pinned it: test 2 (:1112) is token polarity, 3b (:1114) is row
health and counters, and test 6 (:1117) covered only one-healthy-one-failed and
both-failed. `Reachable = Surfaces.Any(s => s.IsHealthy)` satisfies all three. The
uncovered quadrant is the one that matters - every surface answers and none is
healthy - where the broken implementation tells the operator the public source could
not be read at the precise moment it was read and was reporting trouble. That inverts
the strip's message exactly when the feature is supposed to earn its place, which
makes it worse than the feature simply being absent. Test 6b covers that quadrant and
the mixed one (one answered-unhealthy, one unanswered), asserting `Reachable` true,
no unreachable line, rows still listed, and no fallback for the answered primary.

**The lesson, and why it is the real finding.** Revision 2 applied the
cheapest-broken-implementation rule to all fourteen tests and it caught five holes.
These two survived it, and the reviewer's diagnosis of why is correct and worth more
than either finding: **both defects lived in the relationship between tests rather
than inside any one of them.** Test 4 was sound on its own; the absence of the other
failure classes is what let the wrong implementation through. Test 6 was sound on its
own; an uncovered quadrant is what did it. A rule that asks its question one assertion
at a time is structurally incapable of finding either.

Section 13.1 now has a Part 2 that runs after the per-assertion pass: take each
predicate the design depends on, ask what the cheapest implementation of *that
predicate* is which satisfies the whole suite, and enumerate its input quadrants to
find the ones nothing exercises. Four completed quadrant tables are recorded there -
`Answered`, `Healthy`, `Reachable`, and "fallback fired" - each naming the test that
reaches every quadrant. Both findings are the worked examples.

**Applying Part 2 turned up three more gaps that codex did not flag**, all closed in
this revision:

- **`Answered`, present-but-empty status value.** The extraction gate requires a
  "present, non-empty" value and *nothing exercised the empty half of that
  conjunction* - not as a drift case and not as a fallback trigger. It was the only
  quadrant empty in both directions. Added to test 3 and as row (e) of test 4.
- **"fallback fired", both sources unusable.** Nothing covered primary unusable *and*
  fallback unusable. An implementation could report only the fallback's reason, or
  render the row as though the primary had never been tried. New test 4b asserts both
  counters at 1 and both reasons recorded.
- **"fallback fired", triggered by a different surface's failure.** A fallback
  mis-wired to fire on any surface's failure rather than on the mac primary's would
  have passed every test in the table. Closed for free by adding a
  mac-fallback-counter-zero assertion to test 6's existing mixed case.

**Nothing was rejected this round either.**
# Revision 4 - codex round 4, 2026-09-19. NOT consensus; re-parked.

Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard. Capability proof
passed. Verdict **unsound**, four findings (2 MEDIUM, 2 LOW).

**Round 3's two findings are closed for response-shaped failures**, and the `Reachable =
Any(IsHealthy)` mutation is closed by test 6b and probe J. No Constitution or module-contract
deviation, no unrecoverable first-deploy state, versioning module-scoped only.

**Section 16 was re-affirmed as honest scoping, not an escape hatch** - which is the answer to the
proportionality question put to this round after three more rounds of apparatus growth. But the
same paragraph carries the condition: **open question 1 must be answered before implementation,
and if the owner wanted a second incident list this should collapse to the link-out.**

**One finding fixed immediately, because it is a hazard this run created.** The plan said the base
app version is and stays 2.21.0. It is **2.21.1** since `3c21270`, the shared autocomplete fix - so
an implementer following the literal would have DOWNGRADED `VersionPrefix`, `AssemblyVersion` and
`FileVersion`. Both places now say leave it untouched and name the reason rather than a number.

**Three findings recorded and NOT fixed, because the plan is parked and they are pre-implementation
work:**

1. **MEDIUM - the fallback proof still omits no-response primary failures.** Test 4's theory covers
   only response-shaped failures: non-200, wrong content type, unparseable, absent status, empty
   status. A thrown request or an immediate cancellation is not covered, so an implementation could
   fall back on all five response shapes and treat a throw as terminal Unknown while passing the
   suite. Needs theory rows where the primary throws and where it is cancelled with budget
   remaining, each asserting the fallback fired.
2. **MEDIUM - `Reachable` still does not prove a fallback-sourced answer counts.** No test asserts
   `Reachable == true` in the quadrant where the primary failed, the fallback answered, and the
   other surface also failed - so `Reachable` could be computed before the fallback and the page
   would render "could not read" while holding a healthy fallback row.
3. **LOW - present-but-empty status is closed for RSS but not for JSON.** No case proves an empty
   JSON status is `!Answered`; the existing empty-string case is a token-mapping test that would
   also pass if empty were treated as answered-but-unhealthy.

**Status: still `Draft`, still parked, and four rounds is where it stops.** Each round has found
real defects, but rounds 2, 3 and 4 found them in the verification apparatus rather than the
design, and the apparatus is now roughly 1800 lines specifying two status tokens - one of which
carries the value. The three findings above are cheap **once it is known the feature is wanted**,
and wasted otherwise. **Answer open question 1 first.**
