# Risky Users -- Complete And Ordered Results

Status: **Draft, awaiting owner approval.** No code written. One open question (Q1)
is a genuine owner gate and blocks S3 only; S1 and S2 can proceed without it.

This is the plan `docs/RiskyUsersModule-Plan.md` section **S4a** said to write:

> Take S4a only if a real tenant is found to hold more than 500 risky users and the
> operators say the cap blocks them -- and then as its own plan, because it is not a
> Risky Users change.

Both halves of that trigger have now fired. The owner reported on 2026-09-24 that the
module "isn't finding risky users accurately or thoroughly", with a screenshot that
proves it against the Entra portal.

## The reported defect, and the evidence

Owner screenshot, 2026-09-24, dev (`ashbiamweb1.ad.analog.com/ExchangeAdminWeb`),
Risky Users v1.1.0, filter `Risk level = Any`, `Risk state = Any`,
`UPN contains = charles`:

- The page returned **3 rows** and displayed the warning
  `Showing the first 500; more exist. Narrow the filter.`
- The three rows were `Charles.Lee@analog.com` (medium / atRisk, last updated
  2021-12-18), `Gordon.Charles@analog.com` (low / atRisk, 2017-11-27) and
  `paul.charles@analog.onmicrosoft.com` (none / remediated, 2026-06-24).
- The Entra admin center, open beside it, showed **`Paul.Charles@analog.com`,
  High risk**, user id `ad9801cb-9ef4-4aaf-8e85-4b88555f3238`, with risk activity
  through September 2026. **That account does not appear in the module's results at
  all.**

A High-risk account missing from a risky-users list is the module's whole purpose
failing. The three rows that did appear are a mix of 2017, 2021 and 2026 records, which
is itself evidence for the ordering defect below.

## Root cause -- three defects, one design

All three are in `Services/RiskyUsersService.GetRiskyUsersAsync`
(`Services/RiskyUsersService.cs:75-113`).

### 1. The result set is one page, capped at 500, and paging was never implemented

`$top` is clamped to 500 (`ClampMaxRows`, `:187-193`) and `@odata.nextLink` is detected
but deliberately not followed (`:96-99`). The comment states the reason:
`GraphTokenClient.GetWithStatusAsync` (`Services/GraphTokenClient.cs:32-42`) prepends
`GraphBaseUrl` to whatever string it is handed, so an absolute continuation URL becomes a
malformed request. The module therefore cannot see past the first 500 risky users in the
tenant, and `Paul.Charles@analog.com` is past that boundary.

### 2. The UPN search runs client-side, AFTER the 500-row cut

`:107-108` filters `users` in memory once the page has already been fetched and
truncated. So `UPN contains` searches the arbitrary first 500 records, not the tenant.
This is why the banner's advice -- "Narrow the filter" -- is actively wrong for that
control: narrowing on UPN cannot recover a row the fetch never retrieved. (`Risk level`
and `Risk state` DO reach Graph as `$filter`, `:201-212`, so those two genuinely do
narrow the fetch.)

### 3. Nothing constrains WHICH 500 arrive

No `$orderby` is emitted, and Microsoft does not document a default order for this
collection. The 500 that arrive are whatever Graph returns first. The observed rows
(2017, 2021, 2026 in one three-row sample) confirm the order is not by recency, so the
500 are not the newest, not the highest-risk, and not reproducible. Sorting
(`SortRiskyUsers`, `:221-225`) is correct but operates only on that arbitrary sample.

## What was ruled out, and why

**Pushing the UPN match into `$filter` (server-side matching) is not available.** The
Microsoft Graph v1.0 reference for `GET /identityProtection/riskyUsers` states, verbatim:

> This method supports the `$filter` and `$select` OData query parameters to customize
> the query response. The maximum page size with `$top` is 500 objects.

It does not enumerate which properties or operators `$filter` accepts, and it documents
no `contains()` or `startswith()` support on this collection. A filter Graph rejects
returns 400, which S2 rule 1 correctly surfaces as a hard failure -- so building the UPN
search on an undocumented operator would trade "incomplete results" for "the module
stops working", which is worse. Paging is the only route to a complete answer.

The same doc confirms `$orderby` and `$count` are unsupported, so defect 3 cannot be
fixed by asking Graph for an order. It is fixed by retrieving everything and sorting
locally, which the module already knows how to do.

**This is not a licensing, consent or credential problem.** Reads are succeeding; rows
are being returned; the 403 path (`:175-177`) is not firing.

## Scope

In scope:

1. Teach `GraphTokenClient` to follow an absolute `@odata.nextLink`, with a host guard.
2. Make `RiskyUsersService.GetRiskyUsersAsync` retrieve the complete result set by
   following continuation links, under an explicit ceiling.
3. Replace the truncation notice and its wrong advice with honest, accurate states.
4. Re-point the tests that assert the old single-page behaviour, without weakening them.

Out of scope, and deliberately so:

- Any change to the write path (`ApplyActionAsync`), the ticket/confirm/audit/notify
  gates, or the history expander.
- The `RiskyUsers.razor` click-gating defect recorded in `.agents/state.md`
  (`ActionsDisabled` missing `historyLoading`, 4 of 7 buttons outside the gate). That is
  queue 9 tier 3, unapproved. Do not fold it in.
- Changing the default filter values. See Q1 note below.
- Any other module, even though S1 touches shared infrastructure they use.

## Constraints a cold implementer must honour

- **`GraphTokenClient` is shared.** `MfaResetService`, `M365GroupManagementService`,
  `NamedLocationsService`, `IntuneDeviceService`, `ServiceHealthService`,
  `CloudPasswordResetService` and others construct it. S1 must be purely additive: the
  existing relative-path behaviour must be byte-for-byte unchanged for every current
  caller, and `GraphTokenClientTests` must pass untouched.
- **Following a URL out of a response body while attaching a bearer token is an
  exfiltration route.** If Graph (or anything impersonating it) returns an
  `@odata.nextLink` pointing elsewhere, an unguarded client mails the access token to
  that host. `Services/DefenderApiClient.cs:226` (`TryResolveRequestUri`) is the in-repo
  precedent that already solves this and its doc comment at `:139-149` explains the
  reasoning; follow that shape. An absolute URL whose host is not the Graph host is
  refused, not sent.
- **Do not re-encode a continuation token.** Send the absolute nextLink unmodified.
  `M365GroupManagementService.cs:304-309` and `NamedLocationsService.cs:78-84` currently
  work around the client by string-stripping the base URL prefix; that happens to be
  lossless today but is fragile, and neither is in this plan's scope to change.
- **Known Failure Class 2 applies to the page loop.** A multi-page fetch that fails on
  page 7 has NOT produced a complete list. It must not return pages 1-6 as if it had.
  `NamedLocationsService.cs:57-68` has the right instinct (first-page failure throws,
  later-page failure logs and returns partial) but the wrong outcome for this module:
  here, a partial list of risky users is the exact defect being fixed. Any incompleteness
  must reach the operator.
- **An unbounded loop is not the fix either.** The tenant size is unknown and the
  Defender rebuild measured 113,102 devices where 40,000 was assumed. A ceiling is
  mandatory, and hitting it must be a refusal that names the action, not a silent cut --
  owner ruling 2026-09-21 on user-facing strings, and the `Truncated` contract this
  module already has.
- **Environment neutrality.** No source file names an ADI domain, host or account. The
  UPNs in this plan are evidence in a document, not behaviour.

## Design

### The fetch becomes a loop with three exits

```
page 1: GET /identityProtection/riskyUsers?$top=500&$select=...&$filter=...
while the response carries @odata.nextLink:
    if accumulated >= Ceiling      -> stop, report CeilingHit
    GET <absolute nextLink>
    if it fails                    -> throw (the list is incomplete; say so)
    accumulate
-> complete
```

Three distinguishable outcomes, never collapsed into each other:

| Outcome | Meaning | `Users` | Operator sees |
| --- | --- | --- | --- |
| Complete | Graph stopped offering pages | the full set | the sorted list, no notice |
| CeilingHit | more exist; the module stopped asking | **empty** | a refusal naming what to do |
| Failure | a page request failed | **empty** | the existing error alert |

**CeilingHit carries no rows, and the page branches on the outcome before it reaches the
table.** This is a correction from this plan's first draft, which called CeilingHit a
refusal in one paragraph and then said `RiskyUsers.razor` needed only re-wording because
`Truncated` kept its meaning. Those two statements contradict each other:
`RiskyUsers.razor:111` renders the table whenever `results.Count > 0` and `truncated`
only adds a warning above it, so keeping that shape would produce exactly what this plan
exists to remove -- an arbitrary subset of the tenant's risky users, rendered as a list,
with a caption. The in-repo precedent for a large-list refusal is Defender for Endpoint:
the refusal is a distinct outcome on the model (`Models/DefenderDeviceModels.cs:376`) and
`Components/Pages/DefenderEndpointDevices.razor:230` branches it away from the table
entirely. Follow that.

`RiskyUserPage` therefore gains an explicit outcome and, for CeilingHit, the reason text.
`Truncated` is either removed or derived from the outcome; it must not survive as an
independent flag that a future edit can set while rows are still rendered.

### The ceiling gets a NEW config key. Reusing `MaxRows` would ship a fix that does nothing.

`$top` becomes a constant 500 -- the documented maximum, and there is no reason to ask
for smaller pages. The ceiling then needs a config value, and the tempting move is to
redefine the existing `MaxRows` key from "page size" to "total ceiling".

**That would leave the defect in place on this deployment.** `MaxRows` is declared with
`DefaultValue: "500"` (`Modules/ModuleCatalog.cs:708`), and this module's configuration
was entered by the owner and saved on 2026-09-02. `ModuleConfigService.GetValue`
(`Services/ModuleConfigService.cs:60`) returns the STORED row, and a descriptor default
does not overwrite a stored value. So a redefined `MaxRows` would be read back as 500,
the ceiling would be 500, and the module would fetch one page and refuse -- the same
result as today, from code that is correct. The plan would have shipped and changed
nothing, and the symptom would look like the fix failing rather than like a stale config
row.

So: **add a new key, `MaxTotalRows`, `DefaultValue: "10000"`, and remove `MaxRows` from
the descriptor.** A new key has no stored row, so the descriptor default is what is read
on the first query after deploy. State in the commit that the orphaned `MaxRows` row is
deliberately left in the shared config database -- deleting a row from the shared store
is a data action needing its own authority, and an unread orphan is harmless.

The old description string ("Maximum risky users fetched per query (Graph caps at 500)")
does not survive either way; it describes behaviour that will not exist.

Ceiling default **10,000** (20 requests at 500/page) is a coder-side call, stated rather
than asked: an order of magnitude above the current cap, configurable, and the Defender
precedent is that a ceiling exists and refuses honestly rather than being sized by
guesswork. Manual check 1 measures the real number and the default can be revised on
evidence.

### Sorting finally means something

`SortRiskyUsers` is unchanged. It currently sorts an arbitrary sample; after S2 it sorts
the complete set, which is what makes `high` genuinely appear at the top.

## Slices

Each slice is one commit, builds and passes green on its own.

### S1 -- `GraphTokenClient` accepts an absolute URL (shared infrastructure)

`Services/GraphTokenClient.cs`. Add a resolution step to `GetWithStatusAsync` in the
`DefenderApiClient.TryResolveRequestUri` shape:

- A string starting with `/` keeps today's behaviour exactly: `GraphBaseUrl + endpoint`.
- An absolute URI is accepted **only** if its scheme is `https`, its host equals the
  Graph host, **and its path begins with the `/v1.0` base path** -- all compared ordinal
  case-insensitively against the components parsed out of `GraphBaseUrl`, never by
  substring or `EndsWith`, which `graph.microsoft.com.evil` would satisfy. It is then
  sent unmodified.
- The base-path clause is not belt-and-braces. Today this client is confined to
  `https://graph.microsoft.com/v1.0` by construction (`GraphTokenClient.cs:16,35`): no
  caller can reach `/beta` or any other Graph surface through it. A host-only guard would
  quietly widen that for every existing caller, which is a bigger change than the one
  being asked for. Matching the base path keeps the client's reach exactly as it is and
  adds only the ability to continue a query it already started.
- Anything else is refused before the token is acquired, returning
  `(null, HttpStatusCode.BadRequest)`. The refusal must not leak the rejected URL into a
  message an operator sees.

Tests in `ExchangeAdminWeb.Tests/GraphTokenClientTests.cs`: relative path unchanged;
absolute Graph URL sent verbatim (assert the request URI, including query string and
escaping, is identical to the input); a foreign-host absolute URL refused **and no
Authorization header ever constructed**; a `http://` Graph URL refused; a
lookalike-host URL refused.

**Amend `docs/AdminModuleDeveloperGuide.md:645-646` in this same commit.** It currently
states, flatly, that Graph endpoints passed to `GraphTokenClient` must start with `/` --
and four lines later, at `:649-650`, it requires authors to follow `@odata.nextLink`,
which is absolute. Both rules cannot be obeyed through this client, which is why two
services already hand-strip the base URL to satisfy them. S1 is what makes the pair
consistent, so the `/`-prefix rule must gain its exception here: a continuation URL
returned by Graph may be passed through unmodified, and nothing else absolute may.
Leaving the guide alone would ship code that contradicts written guidance on the line a
new module author reads first.

Base app version bump: `ExchangeAdminWeb.csproj` `<VersionPrefix>`, `AssemblyVersion`,
`FileVersion` `2.23.0` -> `2.24.0`. Shared infrastructure -- Constitution, Deployment And
Versioning. No module version changes in this slice. **Check the csproj's actual value at
implementation time rather than trusting this number**: another plan may land first, and
a literal implementer writing 2.24.0 over a higher value would downgrade three version
fields -- the exact trap `docs/ServiceHealthPublicStatus-Plan.md` was caught in.

Guard proof: delete the host comparison, confirm the foreign-host test fails, restore,
touch the file so MSBuild rebuilds (the restore-timestamp trap).

### S2 -- `RiskyUsersService` retrieves the complete set

`Services/RiskyUsersService.cs`.

- `$top` fixed at 500; `ClampMaxRows` becomes `ClampCeiling`, reading the new
  `MaxTotalRows` key, with a wider range and a 10,000 default. Keep the "unparseable or
  non-positive falls back to the default" behaviour; do not let a bad config value mean
  "unbounded". Add a test that the ceiling is read from `MaxTotalRows` and that a stored
  `MaxRows` value is NOT consulted -- that is the guard against the stale-row trap
  above.
- Loop as designed above. Every page after the first goes through the S1 absolute path.
- A failure on any page throws, carrying how many pages had succeeded. It must be
  impossible for a partially-fetched list to reach the caller as a success.
- The client-side `UpnContains` filter stays where it is (`:107-108`) but now runs over
  the complete set. The comment at `:105-106` must be rewritten -- it currently explains
  a decision whose consequence has changed.
- Keep the comment at `:96-99` honest: it currently states nextLink cannot be followed.

Tests in `ExchangeAdminWeb.Tests/RiskyUsersServiceTests.cs`, driven through the existing
`internal` `Func<Task<GraphTokenClient?>>` seam: three pages merge in order; a UPN match
that exists only on page 3 is returned (**this is the reported defect, and this test is
the one that must fail before the fix**); a mid-loop failure throws rather than returning
pages 1-2; the ceiling stops the loop and reports CeilingHit rather than throwing; a
single page with no nextLink reports Complete.

Existing tests that assert the old shape -- the `$top` clamp (old AC6) and
single-page truncation (old AC7) -- are **re-pointed, not deleted and not loosened**.
Name each one in the commit message with what it asserts now.

`Modules/ModuleCatalog.cs`: `MaxRows` removed, `MaxTotalRows` added; `RiskyUsers`
`Version` `1.1.0` -> `1.2.0`. No base app bump in this slice (S1 already did it).

### S3 -- the page tells the truth (blocked on Q1)

`Components/Pages/RiskyUsers.razor`.

- **Branch on the outcome before the table.** `:111` currently keys the whole result
  block on `results.Count == 0`. CeilingHit must reach its own branch, not the table's,
  following `Components/Pages/DefenderEndpointDevices.razor:230`.
- `:118-123` currently reads `Showing the first @requestedMax; more exist. Narrow the
  filter.` Replace with the CeilingHit refusal: what happened and what to do, per the
  2026-09-21 ruling -- no design rationale, no self-reference. The advice must be
  actionable and true, so it names Risk level and Risk state, which narrow the fetch, and
  not UPN contains, which does not.
- The Complete case renders no notice at all. A complete list must not carry a caveat.
- If Q1 comes back "cap what is rendered", add the render limit and its own notice here.

Tests: `ExchangeAdminWeb.Tests/RiskyUsersPageTests.cs` is source-level only -- no bUnit
harness exists in this repo. Assert the wording tripwires; do not report a green suite as
evidence the operator sees the fix. The manual checks below are that evidence.

## Acceptance criteria

- **AC1.** A tenant with more than 500 risky users returns more than 500 rows on an
  unfiltered query (up to the ceiling).
- **AC2.** A UPN substring matching a user beyond the first 500 returns that user. Run
  specifically against the reported case: `UPN contains = charles` returns
  `Paul.Charles@analog.com` at High risk.
- **AC3.** With no filter set, the first row is the highest-severity risky user in the
  tenant, not the highest-severity of an arbitrary 500.
- **AC4.** A failure on page N of M surfaces as an error with no rows rendered. The
  operator is never shown a short list as if it were complete.
- **AC5.** Reaching the ceiling renders a refusal that says more exist and names a filter
  that actually reduces the fetch, and renders **no table and no rows**. A partial set is
  never shown with a caption over it.
- **AC6.** A complete result set renders **no** truncation notice.
- **AC7.** An absolute nextLink is refused, with no bearer token sent, when its host is
  not the Graph host, when its scheme is not `https`, or when its path is outside `/v1.0`.
- **AC8.** Every existing caller of `GraphTokenClient` behaves identically.
  `GraphTokenClientTests` passes with no test modified.
- **AC9.** Audit behaviour is unchanged: every query is still logged, reads are still
  never alert-emailed (D2, 2026-08-31).

## Verification

Automated, per `.agents/repo-guidance.md`:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx` (baseline as of `bd29a9c`: 3064 passed / 0 failed /
  3 skipped)
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`

Guard proof per slice, with the restore-timestamp touch.

Manual, on dev, after deploy -- and **AC1-AC6 are reachable no other way**, since no test
in this repo renders a Razor page:

1. Unfiltered query. Record the total row count and the wall-clock time. **This is the
   first measurement of the tenant's real risky-user population and it decides whether
   the 10,000 default is right.**
2. `UPN contains = charles`. `Paul.Charles@analog.com` appears at High risk (AC2).
3. Confirm the top row is High risk and the list descends by severity (AC3).
4. Confirm no truncation notice appears when the fetch completes (AC6).
5. A per-row action still works end to end: Graph accepts, audit row written with ticket,
   admin notification received. S2 changes the read path only, but the page is shared.

## Versioning

- `ExchangeAdminWeb.csproj` `2.23.0` -> `2.24.0`, in **S1 only**. `GraphTokenClient` is
  shared infrastructure.
- `Modules/ModuleCatalog.cs` `RiskyUsers` `1.1.0` -> `1.2.0`, in **S2 only**.
- Two independent rules, each firing once. Verify by diff that S1 leaves
  `ModuleCatalog.cs` untouched and S2 leaves the csproj untouched.

## Open questions

**Q1 (owner, blocks S3 only).** Once the fetch is complete, the page renders every row it
retrieved. If this tenant holds several thousand risky users, that is several thousand
rows in one Blazor Server table -- the failure the owner already hit on Defender for
Endpoint ("Rejoining the server..."), where virtualised scrolling was rejected outright.
Two shapes:

- **(a) Render everything, ceiling refusal above the limit.** Simplest, matches what the
  module does today, and the severity sort puts what matters on the first screen. Risk:
  the circuit dies at some unknown row count and this plan will have moved the failure
  rather than fixed it.
- **(b) Fetch everything, render the top N by severity, say so.** The fetch stays
  complete so search and sort are correct tenant-wide; only the rendering is bounded, and
  the notice states the bound rather than implying the data is missing.

Recommendation: **(b)**, and it does not depend on measuring the tenant first -- (a)
gambles on a number nobody has, while (b) is correct at any size. But (a) is materially
less work and is defensible if step 1 of the manual checks comes back small.

**Q2 (not blocking, recommendation stated).** The default filter is `Any` / `Any`, so the
default view is every risk record the tenant has ever held, including remediated and
dismissed ones from 2017. Once severity sorting covers the complete set those sink to the
bottom, so this is no longer a correctness problem. Recommendation: leave the defaults
alone and revisit after the owner has used the fixed page. Not implemented in this plan.

## Review

`openreview codex (@azure-openai-eus2-global/gpt-5.5-dzs @ xhigh, fallback) over
cecddcc..dc79994: acceptable_with_changes`. codex-cli 0.154.0, 2026-09-24. Capability
proof passed both halves (read `AGENTS.md`; ran `git diff --stat` over the pins, exit 0).
Resolved model identity is not recoverable from the CLI envelope -- the Portkey gateway
obscures it -- so this records what was dispatched, per the playbook.

Three material changes, **all three admitted and folded into the text above**:

1. **CeilingHit was not really a refusal.** The draft called it one and then kept the
   rendering path that shows rows with a warning over them. Now a refusal outcome with an
   empty `Users`, and the page branches before the table.
2. **The fix would have shipped and done nothing on this deployment.** Redefining
   `MaxRows` from page size to total ceiling reads back the value stored on 2026-09-02:
   500. Now a new `MaxTotalRows` key with no stored row, plus a test that the old key is
   not consulted. This is the most valuable of the three -- it is a defect the automated
   gates and a dev deploy would both have passed, because the code would be correct and
   the configuration would be what made it useless.
3. **The URL guard was host-only, which widened the client.** Now scheme, host and the
   `/v1.0` base path must all match, keeping every existing caller's reach unchanged.

The reviewer endorsed the core approach: page with `$top=500` behind a guarded absolute
nextLink, filter UPN after the complete fetch, sort locally, three distinct outcomes.

**One reviewer claim was checked and is wrong in its detail, and checking it found
something the review missed.** codex cited `docs/AdminModuleDeveloperGuide.md:645` as
already requiring module authors to follow `@odata.nextLink`. That line says the
opposite: *"Graph endpoints passed to `GraphTokenClient` must start with `/`"*. The
nextLink rule is four lines below at `:649-650`. Both are in the same bulleted block, and
**they contradict each other**: an `@odata.nextLink` is absolute, so no module can obey
both through this client. That contradiction is the root of the defect this plan fixes,
and it is why `M365GroupManagementService.cs:304-309` and `NamedLocationsService.cs:78-84`
each hand-strip the base URL -- two authors independently working around the client to
satisfy both rules. S1 now carries the guide amendment that resolves it. Flagged per
AGENTS.md rather than silently chosen between.

## Related records

- `docs/RiskyUsersModule-Plan.md` -- the module plan. Its **S4a** authorised exactly this
  work as a separate plan; its **AC6** and **AC7** are superseded by this plan's AC5/AC6,
  and its S2 rules 2 and 3 are amended by S2 here. Update that plan's S4a section when
  this one lands.
- `docs/ProjectConstitution.md` -- Deployment And Versioning (the two-bump rule).
- `.agents/decisions.md` 2026-09-21 -- user-facing strings state what happened and what
  to do.
- `.agents/repo-guidance.md` Known Failure Class 2 -- success aggregation.
- `Services/DefenderApiClient.cs:139-149, 226` -- the absolute-URL precedent S1 follows.
