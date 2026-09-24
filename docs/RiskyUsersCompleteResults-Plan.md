# Risky Users -- Complete Results, Findable Users, Identifiable Actions

Status: **Draft, awaiting owner approval.** No code written. Five slices. **No open owner
questions remain.** Q1 was ruled by the owner on 2026-09-24 (paginate the table), Q3 was
closed coder-side the same day, and Q2 is a recommendation rather than a gate.

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

## Root cause of the incomplete results -- three defects, one design

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
stops working", which is worse. Paging is the only route to a complete **list**.

**It is not the only route to a complete answer about one named person**, and an earlier
version of this section said it was. That was too strong, and the owner caught it by
asking whether user 10,001 is reachable at all. Substring search over a list and "is this
person risky?" are different questions, and the second has a direct answer that no ceiling
can affect. S5 adds it.

The same doc confirms `$orderby` and `$count` are unsupported, so defect 3 cannot be
fixed by asking Graph for an order. It is fixed by retrieving everything and sorting
locally, which the module already knows how to do.

**This is not a licensing, consent or credential problem.** Reads are succeeding; rows
are being returned; the 403 path (`:175-177`) is not firing.

## The second defect, reported 2026-09-24: the remediation buttons name nothing

Owner, with a screenshot of the Remediate column: *"these don't make sense. it's unclear
what specific, mapped to Microsoft's page, these options are."*

The three buttons read **Close as handled**, **This was the real user**, **Account was
breached**. The Entra admin center's own toolbar, visible in the owner's other screenshot
of the same session, reads **Dismiss user(s) risk**, **Confirm user(s) safe**, **Confirm
user(s) compromised**. Graph calls them `dismiss`, `confirmSafe`, `confirmCompromised`.

**Nothing rendered on the page connects the two vocabularies.** Not the button, not the
confirm bar (`RiskyUsers.razor:218` reuses the same `ActionLabel`), and not the tooltip --
`ActionConsequence` is a plain-English consequence sentence, not the Microsoft term, and
it is attached as a `title` attribute, so it does not appear on touch, does not appear for
a keyboard user, and did not appear in the owner's screenshot. An operator who reads
Microsoft's documentation, or looks at the same user in the Entra portal, or reads the
`riskDetail` value in the very next column, has no bridge back to these three buttons.

### This refines an owner ruling rather than contradicting one

The current wording is itself an owner ruling, recorded at
`docs/RiskyUsersModule-Plan.md:477-485` (2026-09-02, module `1.0.0` -> `1.1.0`): Microsoft's
vocabulary *"reads ambiguous, and this module is headed for L2 support desk staff."* That
reason still holds -- "Confirm user compromised" genuinely does not tell an L2 operator
that the account's sign-ins are about to be blocked.

So this is not "the plain wording was wrong". It is that **one string was made to do two
jobs and can only do one.** A label identifies which action this is; a sentence says what
it does. The 2026-09-02 change gave the label the sentence's job and demoted the sentence
to a tooltip, which is why the mapping disappeared.

Both jobs still need doing -- but not in the same place, and not on every row. The label
goes on the button, where it is repeated N times and must therefore be as short as the
distinction allows. The sentence goes to the confirmation step, where it appears once, for
the one action the operator actually chose. S4 builds on that split.

### The same defect, one column to the left

`RiskyUsers.razor:154` renders `riskDetail` raw: the owner's screenshot shows
`userPerformedSecuredPasswordReset`. Entra shows that same fact as a readable sentence.
This is the identical failure -- an API token shown where a person's word belongs -- and it
is in the same table, so it is in scope here. `riskLevel` and `riskState` stay as they are:
they are short, already readable, and the operator matches them against the portal's own
badges.

## Scope

In scope:

1. Teach `GraphTokenClient` to follow an absolute `@odata.nextLink`, with a host guard.
2. Make `RiskyUsersService.GetRiskyUsersAsync` retrieve the complete result set by
   following continuation links, under an explicit ceiling.
3. Replace the truncation notice and its wrong advice with honest, accurate states.
4. Re-point the tests that assert the old single-page behaviour, without weakening them.
5. Make each remediation action identifiable as the Microsoft action it performs, keeping
   the plain-English consequence the 2026-09-02 ruling asked for but moving it to the
   confirmation step, where it is shown once rather than on every row.
6. Render `riskDetail` as readable text, with the raw value preserved for anything
   unrecognised.
7. Add a direct single-user lookup, so a named person can be answered for regardless of
   how large the tenant is or where the fetch stopped.

Items 5 and 6 were added on the owner's instruction of 2026-09-24 (*"update the risky
users fix plan to make this better"*) after the completeness work was already reviewed.
They widen this plan beyond its original title, which is why the title changed. They share
the module, the page, the version bump and the deploy with items 1-4, so splitting them
into a second plan would buy nothing and cost a second deploy boundary.

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
  mandatory, and hitting it must be stated on screen, not cut silently -- owner ruling
  2026-09-21 on user-facing strings. Since the owner's ruling of 2026-09-24 that statement
  is a constraint notice over the rows, not a refusal that withholds them; see the Design
  section.
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
| Complete | Graph stopped offering pages | the full set | the sorted list, paged, no notice |
| CeilingHit | more exist; the module stopped asking | the rows fetched | the list, paged, under a constraint notice |
| Failure | a page request failed | **empty** | the existing error alert |

**CeilingHit shows its rows under a visible constraint notice. It is not a refusal.**
Owner ruling 2026-09-24: *"retrieving and working with 10,000 risky users in a web portal
is unmanageable anyway. make it a visible constraint notice."*

This reverses a change made earlier in this plan, and the reversal is the owner's, so it
stands. The earlier version made CeilingHit a refusal carrying no rows, on the Defender for
Endpoint precedent (`Models/DefenderDeviceModels.cs:376`,
`Components/Pages/DefenderEndpointDevices.razor:230`). **That precedent was set before this
plan had pagination, and pagination changes what the alternatives are.** Defender's choice
was between a refusal and dumping an unusable wall of rows; here the choice is between a
refusal and a paged, ordered, searchable list of everything the module did fetch. Throwing
10,000 retrieved users away to avoid overstating completeness is the same error as the two
render-capping options the owner rejected: protecting the operator from a caption by
denying them the data.

**What the notice must say, and the trap in it.** When the ceiling is hit the fetch stopped
early, so the rows held are an arbitrary subset of the tenant in Graph's unspecified order
-- and the severity sort then runs over *that subset*. **The first page is therefore the
highest-risk of a sample, not the highest-risk in the tenant.** A notice that says only
"showing 10,000, more exist" would let an operator believe the worst cases are on screen.
It must say the list is partial, that the order is partial with it, and name the two
controls that actually shrink the fetch. Proposed wording, for the implementer to finalise
within those constraints:

> Showing 10,000 risky users. The tenant has more, so this is a partial list and the
> highest-risk users may not be in it. Risk level and Risk state narrow the search itself;
> use them for a complete result.

**It must not say "narrow the filter".** That is the string this plan exists to delete:
UPN contains does not reduce the fetch, so the old advice was wrong, and repeating it
generically would reintroduce the defect in new words.

`RiskyUserPage` gains an explicit outcome and, for CeilingHit, the notice text. `Truncated`
is removed or derived from the outcome; it must not survive as an independent flag, because
two sources for "is this complete?" is how the two disagree later.

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

### S3 -- the page tells the truth, and pages

`Components/Pages/RiskyUsers.razor`.

- `:118-123` currently reads `Showing the first @requestedMax; more exist. Narrow the
  filter.` Replace with the CeilingHit constraint notice specified in the Design section:
  the list is partial, the order is partial with it, and Risk level and Risk state are the
  two controls that shrink the fetch. Per the 2026-09-21 ruling -- state what happened and
  what to do, no design rationale, no self-reference. **Do not reuse the words "narrow the
  filter"**; UPN contains does not reduce the fetch and that advice is the defect.
- The notice sits above the table and the table still renders. CeilingHit is a constraint,
  not a refusal -- only Failure branches away from the table.
- The Complete case renders no notice at all. A complete list must not carry a caveat.

#### Pagination (Q1, owner ruling 2026-09-24)

**The complete sorted set is held; 50 rows are rendered at a time.** Fetching and sorting
stay tenant-wide, so the UPN search and the severity order are still computed over
everything -- only the rendering is bounded. The row count line above the table keeps
reporting the full match count, not the page's.

Follow `Components/Pages/AdminEventLog.razor` (`:314-333`, `:454-461`, `:828-836`), which
already does this in this codebase: `currentPage`, `const int PageSize = 50`, a computed
`totalPages`, a `SetPage` that range-checks, and `Skip((currentPage - 1) * PageSize)
.Take(PageSize)` at the render.

**Copy its structure, not its pager markup.** `AdminEventLog:321` renders one numbered
button per page in a `@for` loop. At the event log's volumes that is fine; against a
10,000-row ceiling it is 200 buttons, which is a worse density problem than the one being
fixed. Render Prev / Next, "Page X of Y", and nothing else.

**Four pieces of state must reset when the page changes, and one must not:**

- `confirmUserId`, `confirmAction` and `actionTicket` -- **close the confirm bar.** It is
  rendered beneath its row (`:196-225`); page away and it is either orphaned or, worse,
  sitting under a different user. A half-typed ticket must not survive to a row it was not
  typed for.
- `expandedUserId` and `historyEntries` -- **collapse the history expander**, same reason.
- `actingUserId` -- **paging must be refused while an action is in flight**, not reset.
  The pager buttons take `ActionsDisabled` like every other control.
- `rowOutcomes` -- **must NOT be cleared.** It is keyed by user id, so an operator paging
  back should still see what happened to that row. Clearing it on page change would
  destroy the only per-row record of a completed action (AC10 of the module plan, Known
  Failure Class 2).

`currentPage` resets to 1 on every new query, alongside the existing per-query state
clear at `:398-412`.

Tests: `ExchangeAdminWeb.Tests/RiskyUsersPageTests.cs` is source-level only -- no bUnit
harness exists in this repo. Assert the wording tripwires, that `SetPage` clears the
confirm and expander fields, that it does **not** clear `rowOutcomes`, and that
`currentPage` is reset by the query path. Do not report a green suite as evidence the
operator sees the fix. The manual checks below are that evidence.

**Click gating.** `ExchangeAdminWeb.Tests/ClickGateRegistry.cs:84` records
`RiskyUsers.razor` as *"tier 3, not approved; has a partial ActionsDisabled already"*, so
the page is **not** a converted page and this slice does not convert it -- that remains
queue 9 tier 3 and unapproved. But the pager adds controls to a page whose
`ActionsDisabled` is already known-incomplete, so gate the new buttons on it explicitly
rather than leaving them ungated and assuming the eventual conversion catches them.

### S4 -- actions an operator can identify

`Components/Pages/RiskyUsers.razor`, `Services/RiskyUsersService.cs`,
`Modules/ModuleCatalog.cs`.

#### The rule this slice is built on

**A control repeated on every row carries only what differs between the three. Everything
shared belongs to the column header, the row, or the confirmation step -- each of which
says it once.**

An earlier draft of this slice failed that rule and is recorded here so it is not
rewritten later: it proposed labelling the buttons with Microsoft's full toolbar strings,
`Dismiss user risk` / `Confirm user safe` / `Confirm user compromised`, and rendering a
consequence sentence beside each. Two of the three then open with the same word, all three
contain "user", and the token that actually distinguishes them sits at the end where it is
scanned last -- multiplied by every row in the table. Owner, 2026-09-24: *"we do not need
the same long string on every button."*

Entra can afford those strings because its toolbar acts on a selection and appears once.
Copying them into a per-row group is a category error, not a mapping fix.

#### The design

| Graph | Button | Accessible name (`aria-label`, `title`) |
| --- | --- | --- |
| `dismiss` | `Dismiss` | Dismiss user risk |
| `confirmSafe` | `Safe` | Confirm user safe |
| `confirmCompromised` | `Compromised` | Confirm user compromised |

One word per button -- and it is precisely the word that differs in Microsoft's own three
labels, so scanning for it is *easier* than scanning three near-identical phrases, not
harder. The subject comes from the row, the verb category from the column header
`Remediate`, and the existing `aria-label="Remediate"` on the `btn-group`
(`RiskyUsers.razor:177`) already groups them.

The full Microsoft term rides along as the accessible name and the tooltip. That costs no
pixels, gives a screen reader the complete phrase, and keeps the `title` short enough to
actually read -- unlike today's, which is a two-clause paragraph.

**The consequence sentence moves to the confirmation step and appears once, for the one
action chosen.** That is where it belongs: the operator has stopped, is typing a ticket,
and is about to do something with consequences. `ConfirmPrompt`
(`RiskyUsers.razor:781-787`) already renders there and already names the action and the
user; it gains the full Microsoft term and one line of consequence. Today's
`ActionConsequence` strings are the right content in the wrong place -- keep them, trim
them to one clause, render them there instead of in a `title`.

Nothing else is added. No legend above the table, no help text, no second line on the
buttons. Those would be the same mistake at a different scale.

#### What this deliberately does not do

It does not restructure the Remediate column into a single control opening a panel. That
would also fix the density and would answer the Developer Guide's warning about
destructive actions in dense tables, but it is a page redesign, not a labelling fix, and
it overlaps the queue-14 discussion of exactly that hazard. Recorded as a known
alternative, not taken here.

**Four implementation hazards, each of which has already caused a defect somewhere in
this repo:**

1. **The three strings exist in two files and nothing enforces agreement.**
   `RiskyUsers.razor:759-763` (`ActionLabel`) and `Services/RiskyUsersService.cs:165-171`
   (`ActionDisplayName`) each hold their own copy, kept in step by a comment
   (`:160-163`) and nothing else. The service's copy is what lands in the **outcome
   message and the audit record**. Change one and not the other and the operator clicks
   one name while the audit trail records a different one. Either change both in the same
   commit or, better, give them one source. A test must assert they agree.
2. **`ExchangeAdminWeb.Tests/RiskyUsersPageTests.cs:188-191` pins the current three
   strings**, in a test named `ActionLabel_MatchesTheOwnerApprovedL2Wording`. Re-point it,
   and rename it -- a test whose name cites a superseded ruling will mislead the next
   reader into restoring the old strings.
3. **Two different "action name" strings exist and only one of them moves.**
   `AuditActionFor` (`RiskyUsers.razor:745`) produces `RiskyUsers_Dismiss` and friends,
   which `AuditService.cs:215` stores in the `action` column. Those are stable record
   keys: renaming them splits every existing audit record from its successors, so they do
   not change. What does change is the **display** string -- the button, the confirm bar,
   the service's outcome message, and `extra["Action"]` in the admin notification
   (`:728`). Do not let a rename sweep catch the identifiers, and do not conclude from
   AC13 that the audit column should read "Dismiss". Same constraint the 2026-09-02 ruling
   set, and it still holds.
4. **`riskDetail` needs a display map that fails open, not closed.** S2 rule 4 of
   `docs/RiskyUsersModule-Plan.md` is binding: Microsoft extends these enums and an
   unrecognised value must still render. Map the known values to readable text and fall
   back to **the raw value itself** for anything unmatched -- never to "Unknown", never to
   blank, never dropped. An unrecognised `riskDetail` shown raw is mildly ugly; an
   unrecognised `riskDetail` shown as "Unknown" is a lie about what Microsoft said.

Tests: `RiskyUsersPageTests.cs` is source-level only. Assert that the two label copies
agree; that every `RiskyUserAction` has a button label, an accessible name and a
consequence line; that **no two button labels share a word** -- the mechanical form of the
rule this slice is built on, and the one guard that would have caught the earlier draft;
that the accessible name for each action contains Microsoft's term; and that the
`riskDetail` map returns the input unchanged for an unknown value.

The last two guard real hazards. The others guard strings, and a source-level scan cannot
tell whether a label reads clearly to a person -- manual check 6 is the only thing that
can.

Guard proof: change one label copy and not the other, confirm the agreement test fails;
set two labels to share a word, confirm the no-shared-word test fails; feed the
`riskDetail` map an invented value, confirm it comes back unchanged and that deleting the
fallback makes the test fail. Restore and touch the files.

`Modules/ModuleCatalog.cs`: `RiskyUsers` `Version` `1.2.0` -> `1.3.0` -- the second of the
two module bumps this plan makes, recorded in `## Versioning`. Compute from the field at
implementation time, not from this document. No base app bump.

### S5 -- look one user up directly, so no ceiling can hide them

`Components/Pages/RiskyUsers.razor`, `Services/RiskyUsersService.cs`,
`Modules/ModuleCatalog.cs`.

#### Why this exists

The defect that opened this plan was *"is Paul.Charles@analog.com risky, and why is he not
here?"* Answering that by scanning a list is the wrong shape: the list can be capped, its
order is unspecified, and the substring match runs in memory. Answering it by **asking
Graph about that one user** cannot be capped, cannot be mis-ordered, and costs one or two
calls at any tenant size.

`GET /identityProtection/riskyUsers/{riskyUserId}` is v1.0, needs only
`IdentityRiskyUser.Read.All` -- **already held** -- and this module already addresses
individual risky users that way for the History expander
(`RiskyUsersService.cs:119`). `docs/RiskyUsersModule-Plan.md:86` already records the
endpoint. Microsoft documents that it accepts no OData query parameters, which is fine: it
is a direct get, not a query.

`riskyUserId` is the Entra object id. The owner's screenshot shows the Entra portal
labelling it "User ID" on the Risky User Details page, and S6 of the module plan already
populates `EntraObjectId` from `riskyUser.id`, so the two are the same identifier.

#### R1 -- reconnaissance, and it decides the implementation

The gap is turning a typed UPN into that object id. **Probe in this order; the first that
works wins, and the earlier ones cost nothing.** Run against the module's own credentials
before writing code -- this is the Defender R1 pattern, where an assumption about an
endpoint's behaviour was cheaper to measure than to design around.

1. `GET /identityProtection/riskyUsers?$filter=userPrincipalName eq '<upn>'`
   If this returns 200 with the user, **S5 is one call, needs no new permission, and
   nothing below applies.** Undocumented, hence a probe rather than a design.
2. `GET /identityProtection/riskyUsers?$filter=startswith(userPrincipalName,'<prefix>')`
   If 1 fails but this works, prefix search becomes server-side and the UPN box itself can
   stop being a post-fetch filter for the common case.
3. `GET /users/<upn>?$select=id` then `GET /identityProtection/riskyUsers/<id>`
   The guaranteed route. **It needs `User.Read.All` added to the Risky Users app
   registration and admin-consented -- an owner action, not a code change.** Do not reach
   for another module's registration to avoid it: `docs/AdminModuleDeveloperGuide.md:642`
   forbids falling back to another module's Graph config, and reuse is permitted only when
   the operator deliberately configures the same secret id on both.

Record which probe won, in the plan and in the code comment, so the next reader knows
whether the shipped shape was chosen or forced.

#### Three outcomes, and none of them is an error

The whole value of a direct lookup is a definitive answer, including a definitive negative.

| Result | Meaning | Operator sees |
| --- | --- | --- |
| Risky user returned | Graph holds a risk record | the single row, with its actions and History |
| 404 from the lookup | the user exists, Entra holds no risk record | "No risk record for this user." A clean no. |
| The UPN resolves to nobody | no such user | "No user matches that name." Different from the above. |
| Any other failure | the request failed | the existing error alert |

**404 must not render as an error and must not render as "no risky users found".** It is
the answer the operator came for. Conflating "Entra says this person is fine" with "the
query failed" or with "the list is empty" is the same failure class as S2 rule 1 and as the
four Cloud Password Reset findings where an unanswered question was read as a negative
answer -- except inverted: here a real negative must not be dressed up as a failure.

#### The control

One labelled input and a button, in its own row above the filter panel: **Look up a user**,
taking a full UPN. Its result replaces the table with a single-row result or one of the
messages above, and a Clear returns to the browse view.

It is deliberately separate from `UPN contains`. Those answer different questions -- "show
me matching rows in this list" versus "tell me about this person" -- and a single box that
guesses which one the operator meant from whether the text looks like a complete address
would be exactly the kind of cleverness that makes a tool untrustworthy. Two controls, two
labels, no mode switch.

#### What this does to the ceiling

It demotes it. The ceiling stops being the thing that hides a specific person and becomes
what it should have been: a bound on how much of the tenant the browse view will pull. The
constraint notice stays, because a partial list is still partial -- but the operator now
has a route that is never partial.

Tests: `RiskyUsersServiceTests.cs` through the existing `Func<Task<GraphTokenClient?>>`
seam. A 200 returns the user; **a 404 returns a distinct not-risky result, not null and not
an exception**; an unresolvable UPN is distinguishable from a 404 on the risky-user get;
any other status still throws. Page-side, assert the three outcomes reach three different
branches.

Guard proof: collapse the 404 branch into the failure branch and confirm the not-risky test
fails. That is the whole slice in one mutation.

`Modules/ModuleCatalog.cs`: `RiskyUsers` `1.3.0` -> `1.4.0`. No base app bump.

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
- **AC5.** Reaching the ceiling renders the fetched rows, paged, beneath a visible notice
  that says the list is partial, that the highest-risk users may not be in it, and names
  Risk level and Risk state as the controls that shrink the fetch. The notice does not
  contain the words "narrow the filter", and no row is discarded to produce it.
- **AC6.** A complete result set renders **no** truncation notice.
- **AC7.** An absolute nextLink is refused, with no bearer token sent, when its host is
  not the Graph host, when its scheme is not `https`, or when its path is outside `/v1.0`.
- **AC8.** Every existing caller of `GraphTokenClient` behaves identically.
  `GraphTokenClientTests` passes with no test modified.
- **AC9.** Audit behaviour is unchanged: every query is still logged, reads are still
  never alert-emailed (D2, 2026-08-31).
- **AC9a.** The table renders 50 rows at a time. Every fetched row is reachable by paging;
  none is discarded to bound the render. The count above the table reports the full match
  count, not the current page's.
- **AC9b.** Changing page closes any open confirm bar and history expander, preserves
  per-row outcomes, and is refused while an action is in flight.
- **AC10.** Each remediation button can be matched to its Entra admin center toolbar item
  without guessing. The full Microsoft term is the button's accessible name.
- **AC11.** No two remediation button labels share a word, and none repeats the subject or
  the verb category already supplied by the row and the column header.
- **AC12.** The consequence of the chosen action is **visible without hovering** at the
  confirmation step, once, for that action only. A tooltip alone does not satisfy this,
  and neither does a sentence beside every button on every row.
- **AC13.** Every operator-facing rendering of an action's name is the same string as the
  button the operator clicked: the confirm bar, the per-row outcome message from
  `RiskyUsersService`, and the `Action` entry in the admin notification
  (`RiskyUsers.razor:728`). These are display fields, and they are the ones that must
  agree with each other -- **not** the audit `action` column, which AC14 pins separately.
- **AC14.** The audit action identifiers `RiskyUsers_Dismiss`, `RiskyUsers_ConfirmSafe`
  and `RiskyUsers_ConfirmCompromised` (`RiskyUsers.razor:745`, stored by
  `AuditService.cs:215` in the `action` field) are byte-identical to today's. They are
  stable record keys and are deliberately NOT the button text; AC13 must not be read as
  requiring them to match it.
- **AC16.** A direct lookup of a named user returns that user's risk record regardless of
  tenant size, including when a browse query for the same name hits the ceiling and does
  not contain them. Run specifically against the reported case.
- **AC17.** A user with no risk record produces a clean negative -- "no risk record" --
  that is distinguishable from a failed request and from an empty list, and is not styled
  as an error.
- **AC18.** A UPN matching no user is distinguishable from a UPN matching a user who is
  not risky.
- **AC15.** `riskDetail` renders as readable text for known values, and an unrecognised
  value renders as its own raw string -- not blank, not "Unknown", not omitted.

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
3. Confirm the top row is High risk and the list descends by severity (AC3) -- and that it
   is the tenant's highest, not the first page's. Page to the last page and confirm the
   order still descends across the boundary; a sort applied per page instead of over the
   whole set looks identical on page 1.
3a. Open a confirm bar on a row, then change page. The bar closes and the typed ticket
   does not reappear anywhere (AC9b). Repeat with the history expander. Page back and
   confirm a completed row's outcome is still shown.
4. Confirm no truncation notice appears when the fetch completes (AC6).
5. A per-row action still works end to end: Graph accepts, audit row written with ticket,
   admin notification received. S2 changes the read path only, but the page is shared.
6. **Open the same user in the Entra admin center and in this module side by side, as the
   owner did on 2026-09-24, and check that each button can be matched to a toolbar item
   there without guessing** (AC10). This is the reported defect and the only test of
   whether S4 actually fixed it -- no automated check can tell whether a label reads
   clearly to a person.
7. Confirm the consequence sentence is readable without hovering, including by keyboard
   (AC12).
8. Compare the Risk detail column against the same user's Entra timeline entries (AC15).
   Find one value the map does not know -- or force one -- and confirm it renders raw
   rather than as "Unknown".
9. **Run R1's three probes before writing S5** and record which one won. This is the step
   that decides whether S5 needs a new app-registration permission at all.
10. Look up `Paul.Charles@analog.com` directly and confirm the High risk record comes back
   (AC16). Then look up a user who is certainly not risky and confirm the clean negative
   (AC17), and a UPN that matches nobody (AC18). The three must be visibly different.

## Versioning

- `ExchangeAdminWeb.csproj` `2.23.0` -> `2.24.0`, in **S1 only**. `GraphTokenClient` is
  shared infrastructure. **Read the csproj's real value at implementation time** -- another
  plan may land first, and writing a stale number downgrades three version fields.
- `Modules/ModuleCatalog.cs` `RiskyUsers`: **three module bumps, because three slices
  change module-scoped behaviour.** S2 `1.1.0` -> `1.2.0` (the result set changes). S4
  `1.2.0` -> `1.3.0` (the operator-facing actions change). S5 `1.3.0` -> `1.4.0` (a new
  capability). Each bump fires on its own slice; none is optional, because two deployed
  builds sharing a version number is worse than a wrong number. S1 and S3 change no module
  version -- S3 is the page rendering its service's existing outcome, under S2's bump.
- The two rules are independent. Verify by diff that S1 leaves `ModuleCatalog.cs`
  untouched, and that S2 and S4 each leave the csproj untouched.

## Open questions

**Q1 -- RULED by the owner, 2026-09-24: "neither? just page them."** Both offered shapes
were rejected. **The table is paginated.** Recorded in `.agents/decisions.md`.

The ruling is better than either option offered, and the reason is worth keeping: both of
mine threw rows away to protect the circuit. (a) refused above a limit, (b) rendered only
the top N -- in each case the operator could not reach a user the module had already
fetched. Pagination bounds what is *rendered* without bounding what is *reachable*. It is
also what the Entra portal does, so the module stops behaving unlike the thing it mirrors.

This is the same shape the owner leaned toward on Defender for Endpoint, where the
unanswered fork is recorded in `.agents/state.md` as *"make browsing filter-first and paged
(portal-style, 50 at a time)"* and virtualised scrolling was rejected outright. Two modules
now want the same answer; do not invent a second one here.

S3 carries it. Q1 is closed.

**Q3 -- CLOSED 2026-09-24, coder-side, on the owner's instruction to "do it correctly"
rather than to ask again.** The question had been which of two shapes to take: relabel in
place, or restructure the Remediate column into one control opening a panel. Relabelling
in place is what S4 does. The restructure is the better page and would also answer the
Developer Guide's warning about destructive actions in dense tables, but it is a redesign
rather than a labelling fix and it overlaps queue item 14, which exists to discuss exactly
that hazard. Recorded in S4 under "What this deliberately does not do".

**Q2 (not blocking, recommendation stated).** The default filter is `Any` / `Any`, so the
default view is every risk record the tenant has ever held, including remediated and
dismissed ones from 2017. Once severity sorting covers the complete set those sink to the
bottom, so this is no longer a correctness problem. Recommendation: leave the defaults
alone and revisit after the owner has used the fixed page. Not implemented in this plan.

## Review

Two rounds, both `codex (@azure-openai-eus2-global/gpt-5.5-dzs @ xhigh, fallback)`,
codex-cli 0.154.0, both `acceptable_with_changes`, six material changes, all six admitted
and folded in. Round 1 covered S1-S3; round 2 covered S4 and swept the whole plan.

### Round 2 -- `openreview` over `afdacaa..71c5390`, 2026-09-24

Capability proof passed both halves. The reviewer endorsed both designs -- guarded
nextLink paging with a separate ceiling and a hard failure rather than partial data, and
short per-row buttons with the Microsoft term exposed accessibly, consequences at the
confirmation step, `riskDetail` mapped with a raw fallback. All three findings were
internal inconsistencies this plan had accumulated while growing, and every one of them
would have misled a cold implementer:

1. **`.agents/state.md` still said three slices and "Open Q3, blocks S4"** after the plan
   moved to four slices and closed Q3. Fixed in state, not here.
2. **The Versioning section said the module bumps in S2 only**, while S4 also declared a
   bump. Both are real -- two slices change module-scoped behaviour -- so Versioning now
   records both, `1.2.0` at S2 and `1.3.0` at S4.
3. **AC13 and AC14 contradicted each other.** AC13 required the clicked button and the
   audit record to carry the same string; AC14 required the audit identifiers to stay
   byte-identical. Both cannot hold once the button reads `Dismiss` and the identifier
   reads `RiskyUsers_Dismiss`. AC13 now names the display fields it actually meant -- the
   confirm bar, the outcome message, and `extra["Action"]` in the admin notification -- and
   explicitly disclaims the audit `action` column, which `AuditService.cs:215` stores and
   AC14 pins.

### Round 1 -- `openreview` over `cecddcc..dc79994`, 2026-09-24

`acceptable_with_changes`. codex-cli 0.154.0, 2026-09-24. Capability
proof passed both halves (read `AGENTS.md`; ran `git diff --stat` over the pins, exit 0).
Resolved model identity is not recoverable from the CLI envelope -- the Portkey gateway
obscures it -- so this records what was dispatched, per the playbook.

Three material changes, **all three admitted and folded into the text above**:

1. **CeilingHit was not really a refusal.** The draft called it one and then kept the
   rendering path that shows rows with a warning over them. It was made a refusal with an
   empty `Users`, on the Defender precedent. **The owner overruled that on 2026-09-24**,
   after pagination landed: CeilingHit now shows its rows under a constraint notice. The
   finding was still correct -- the plan had said two contradictory things and had to pick
   one -- and picking the refusal was right at the time, because the plan then had no way
   to render 10,000 rows usably. Recorded rather than quietly rewritten, so a later reader
   does not "restore" the refusal from this section.
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
  and its S2 rules 2 and 3 are amended by S2 here. Its **Revision 2026-09-02** (the
  L2-plain action wording, `:477-485`) is refined by S4 here: the consequence sentences
  survive and become more visible, the labels do not. Update both sections when this
  lands, and record the 2026-09-24 ruling in `.agents/decisions.md` -- the 2026-09-02 one
  was never written there, which is part of why its reasoning was only discoverable from
  a code comment.
- **S2 rule 4 of `docs/RiskyUsersModule-Plan.md`** -- unknown enum values pass through,
  never dropped or allowlisted. Binding on S4's `riskDetail` map.
- `docs/ProjectConstitution.md` -- Deployment And Versioning (the two-bump rule).
- `.agents/decisions.md` 2026-09-21 -- user-facing strings state what happened and what
  to do.
- `.agents/repo-guidance.md` Known Failure Class 2 -- success aggregation.
- `Services/DefenderApiClient.cs:139-149, 226` -- the absolute-URL precedent S1 follows.
