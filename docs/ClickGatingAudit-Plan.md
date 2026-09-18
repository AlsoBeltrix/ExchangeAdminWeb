# App-wide click gating: audit and remediation plan

Status: **Approved in part; design superseded by Revision 1 and BLOCKED on a re-scope decision.**
Queue item 9. Drafted 2026-09-18 against `3e19aef`.

**Approved scope, owner 2026-09-18: slice 0 + slice 1 + tier 1 only** - the scanner and shared
test harness, the two prerequisite stuck-flag fixes, and the nine pages that can execute a
destructive AD/Exchange/Graph write. Tiers 2, 3 and 4 are **not** approved; they are re-decided
once the shared harness makes the per-page cost a measurement rather than the estimate below.
Implementation must not exceed that scope.

> **Read Revision 1 (end of this document) before implementing anything.** An 11-page
> reconnaissance run on 2026-09-18 falsified several load-bearing claims in the design below,
> including the central risk statement and the prescribed remedy for non-button controls. The
> sections between here and Revision 1 are the original audit: its **counts stand and were
> reproduced by the committed scanner**, but its *design and estimate are superseded*.
> Tier 1 is blocked on a re-scope decision recorded in Revision 1.

This document is the deliverable the owner asked for: an audit of the whole app for the
defect class fixed on Mailbox Migrations, plus a plan and an effort estimate. **It contains
no code changes and authorises none.** The owner's queue wording is explicit that the
deliverable is "an audit, then plan to fix all of them and give the owner an idea of the
effort", not a sweep.

## The rule being audited against

`.agents/decisions.md`, 2026-09-17, owner ruling:

> it should not be possible to click any buttons until the system is ready for that button
> click to be definitively executed.

A control must be disabled whenever an asynchronous operation is running whose result could
invalidate that click, or be invalidated by it. Guarding a handler against re-entering
*itself* is not enough: a Blazor circuit stays interactive across every `await`, so any
control left live during a long operation can be clicked, accepted, and then silently
discarded or applied to state that has since been replaced underneath it.

`docs/MigrationButtonGating-Plan.md` is the worked precedent and the shape to copy.

## How the audit was produced

A scanner reads every `.razor` under `Components/Pages/` as text and classifies its click
targets. There is no bUnit harness in this repo, so nothing can render a page; this is the
same limitation the precedent plan records for its own tripwires, and it is inherited, not
introduced here.

The scanner is not committed yet - committing it is step 1 of the remediation below, where
it also gains the Pester coverage `.agents/repo-guidance.md` requires of new `.ps1` logic.
Its algorithm is recorded here so the numbers are reproducible:

1. **Comments are blanked, not deleted**, before any matching. Blanking preserves line
   numbers; deleting shifted every reported line in an earlier revision of the scanner.
   Stripping at all is mandatory - the precedent plan records two tripwires that failed
   against correct code because they read the words `await` and `finally` out of prose.
2. **Tag extraction is quote-aware.** A naive `<button.*?>` stops at the `>` inside a lambda
   `=>` and reads a truncated tag. The scanner walks each tag tracking quote state.
3. **An in-flight flag is detected structurally, never by name.** This repo's verbs vary too
   much for a name list (`actingDeviceId`, `isCsvProcessing`, `detailLoading`, `pendingOp`).
   A field qualifies when, inside *one* method that contains an `await`, it is both raised
   (`= true`, or `= <token>` for a nullable) and lowered (`= false` / `= null`) **in a
   `finally`**. The `finally` requirement is what separates a gate from a data field:
   nullable result and selection fields such as `batchActionResult` or `expandedBatch` are
   also raised and lowered in one method, but only on the happy path.

**Validation.** The scanner was calibrated against `Migration.razor`, whose correct answer
is independently documented. It reports exactly the eight in-flight flags the precedent plan
names, the three ungated buttons and the fourth narrow gate that are that plan's four
documented exemptions (`DownloadSampleCsv` :223, the `batchActionResult` dismiss :442,
`CloseUserReport` :784, `CancelPendingAction` :836), and the three tab anchors at :30, :33,
:36. Line numbers were checked against the current file. No page other than Migration was
used to tune it.

**What it cannot do.** It is text analysis, not semantic analysis: a tripwire against drift,
not a proof. Two known limits are visible in the results below - `expandedBatch` on Migration
is reported as a stuck flag when it is a data field that a gate expression happens to name,
and four similar false positives appear on `GroupManagement.razor`. Every finding below that
is stated as confirmed was confirmed by reading the file.

## Findings

### Scale

| measure | count |
| --- | --- |
| pages under `Components/Pages/` | 33 |
| clickable buttons (`@onclick` or `type="submit"`) | 273 |
| buttons with no `disabled` attribute at all | 108 |
| buttons gated on data only, naming no in-flight flag | 12 |
| buttons naming some but not all of their page's in-flight flags | 89 |
| **buttons not gated against concurrent work** | **209 of 273 (76%)** |
| non-button click targets, which `disabled` cannot reach | 7 |
| pages with a page-level busy predicate | 3 of 33 |

Migration accounts for 4 of the 209, and all four are its documented, deliberate exemptions.
So the remediable population is **205 buttons across 30 pages**.

The 89 "partial" figure is an upper bound on candidates, not a count of defects. It means
the control does not consult every in-flight flag on its page; whether a given pairing is
genuinely unsafe needs the per-page read that the precedent plan performed. The 108 with no
`disabled` at all and the 12 gated only on data are unambiguous.

### Only two other pages have any page-level predicate

`IntuneDevices.razor` and `RiskyUsers.razor` both define
`ActionsDisabled => isSearching || acting<X>Id != null`. This is the right shape and is worth
preserving, but neither is complete: IntuneDevices leaves `detailLoading` out of the
predicate and 5 of its 9 buttons outside the gate, RiskyUsers leaves `historyLoading` out
and 4 of 7 outside. They are partial precedents, not finished work.

Every other page guards each control against re-entering itself only - the exact blind spot
the ruling outlaws.

### Confirmed stuck-flag hazards, independent of the sweep

These are live defects now, not consequences of the proposed change, and they are
prerequisites: widening a flag that can stick into a page-wide gate turns a dead button into
a dead page. This is why the precedent work needed a separate commit 1.

- **`BlockedSenders.razor` `ConfirmUnblock`.** `isLoading = true` at :278; the only clears
  are at :305, :336 and :406, all post-await, none in a `finally`. The service call at :348
  and the notification at :391 are individually caught, so the common paths do reach :406.
  The exposure is the **uncaught** awaits at :290-291 -
  `GetAuthenticationStateAsync` / `AuthorizeAsync` - and the gate region that follows: a
  throw there leaves `isLoading` true for the life of the circuit. Confirmed by reading
  :276-410.
- **`MessageTrace.razor` detail load.** `detailLoading = true` at :976, cleared at :987 and
  :996, both post-await, no `finally`. Same shape; the precise exposure window needs the
  per-page read.

`LicensingUpdates.razor` and `GroupManagement.razor` also raise the flag, but on inspection
those hits are the data-field false positive described above.

### Non-button click targets

`disabled` has no effect on an anchor or a `div`. Migration's three tab links were handled
with an `if (IsBusy) return;` guard in the handler plus disabled-looking styling; the same
treatment is needed for:

- `AdminEventLog.razor` :232, :340
- `OutOfOffice.razor` :26, :29

### Per-page detail

Ranked by write risk, then by gap size. "Gap" is ungated + data-only-gated + partially
gated. "Writes" counts distinct mutating service calls on the page, excluding the
ubiquitous `AuthorizeAsync`.

| page | buttons | gap | flags | predicate | anchors | writes |
| --- | --- | --- | --- | --- | --- | --- |
| `Migration.razor` | 31 | 4 (all exempt) | 8 | `IsBusy` | 3 done | 9 |
| `MailboxPermissions.razor` | 11 | 8 | 1 | - | 0 | 9 |
| `CalendarPermissions.razor` | 11 | 8 | 1 | - | 0 | 9 |
| `M365GroupManagement.razor` | 19 | 19 | 4 | - | 0 | 8 |
| `ModuleConfig.razor` | 20 | 20 | 2 | - | 0 | 7 |
| `GroupManagement.razor` | 13 | 10 | 2 | - | 0 | 7 |
| `IntuneDevices.razor` | 9 | 5 | 3 | `ActionsDisabled` | 0 | 7 |
| `SelfServiceGroups.razor` | 20 | 20 | 6 | - | 0 | 6 |
| `NamedLocations.razor` | 11 | 11 | 2 | - | 0 | 6 |
| `ConferenceRooms.razor` | 14 | 14 | 2 | - | 0 | 5 |
| `DhcpAuthorization.razor` | 7 | 7 | 2 | - | 0 | 4 |
| `BlockedSenders.razor` | 6 | 2 | 1 | - | 0 | 3 |
| `OutOfOffice.razor` | 2 | 2 | 2 | - | 2 | 3 |
| `AdminSettings.razor` | 16 | 15 | 2 | - | 0 | 2 |
| `ADAttributeEditor.razor` | 6 | 6 | 2 | - | 0 | 2 |
| `MfaReset.razor` | 5 | 2 | 1 | - | 0 | 2 |
| `RiskyUsers.razor` | 7 | 4 | 3 | `ActionsDisabled` | 0 | 1 |
| `Comms10k.razor` | 8 | 4 | 1 | - | 0 | 1 |
| `EmergencyDisable.razor` | 4 | 3 | 2 | - | 0 | 1 |
| `LicensingUpdates.razor` | 5 | 3 | 1 | - | 0 | 1 |
| `MessageTrace.razor` | 13 | 13 | 4 | - | 0 | 0 |
| `AdminEventLog.razor` | 10 | 10 | 2 | - | 2 | 0 |
| `AccountLockoutRemediation.razor` | 6 | 6 | 0 | - | 0 | 0 |
| `AdminBulkJobs.razor` | 5 | 5 | 0 | - | 0 | 0 |
| `ConferenceRooms`/`MessageTraceReports` | 2 | 2 | 2 | - | 0 | 0 |
| `ServiceHealth.razor` | 4 | 3 | 1 | - | 0 | 0 |
| `BitLockerRecovery.razor` | 3 | 2 | 1 | - | 0 | 0 |
| `ExchangeOnlineConfig.razor` | 3 | 1 | 1 | - | 0 | 0 |
| `DelegationReport.razor` | 1 | 0 | 1 | - | 0 | 0 |
| `RecipientLookup.razor` | 1 | 0 | 1 | - | 0 | 0 |
| `AccessDenied`, `Error`, `Home` | 0 | 0 | 0 | - | 0 | 0 |

`AccountLockoutRemediation.razor` and `AdminBulkJobs.razor` report zero in-flight flags with
5-6 ungated buttons each: they have no concurrency state to consult at all. Both need flags
introduced before they can be gated. AccountLockoutRemediation is a disabled, parked module
(`.agents/state.md`), so it should be skipped, not fixed.

## Remediation plan

### The central design decision

The precedent shipped **seven page-specific tripwires** for one page. Copying that to 30
pages would mean roughly 200 near-identical test methods, which is unmaintainable and would
dominate the cost.

Instead: **one shared tripwire suite that iterates every page**, driven by a committed
registry. Each page contributes a single table entry naming its in-flight flags, its
predicate, and its deliberate exemptions with reasons. The suite then asserts, for every
page at once:

1. every clickable button's `disabled` consults the page's predicate, unless registered as
   an exemption;
2. the predicate names every in-flight flag the scanner finds on that page;
3. every in-flight flag is lowered in a `finally`;
4. single-flight handlers set their flag before their first `await`;
5. every non-button click target has a handler guard;
6. the exemption registry contains only registered entries, so widening it is a visible diff.

This inverts the economics: the expensive part is written once, and each page's cost drops
to the page edit plus one registry entry. It also makes the rule **self-enforcing for new
modules** - a new page with an ungated button fails the suite on day one - which is the
durable win and, in my judgement, worth more than the sweep itself.

The registry must be explicit rather than inferred. An inferred exemption is an oversight
that looks like a decision.

### Slices

One slice per session per `.agents/repo-guidance.md`, each landing as its own commit.

**Slice 0 - instrument and harness.** Commit the scanner as `tools/Get-ClickGateAudit.ps1`
with Pester coverage in `tests/ps/`, and build the shared tripwire suite plus the registry,
seeded with Migration, IntuneDevices and RiskyUsers as the already-partly-correct entries.
No page behaviour changes. Test-only and tooling-only, so no version bump.

**Slice 1 - prerequisites.** Fix the confirmed stuck flags in `BlockedSenders.razor` and
`MessageTrace.razor` so they are `finally`-cleared. These are defects in their own right and
land whether or not the rest of the plan proceeds. One commit each, per the one-fix-per-commit
rule; each bumps its own module's patch version.

**Slices 2-n - one page per slice**, in the tier order below. Each: add the predicate, make
every flag `finally`-clear, move late flag assignments before the first await, apply the gate
to every control, guard non-button targets, add the registry entry, prove a mutation bites,
bump that module's `Version` in `Modules/ModuleCatalog.cs`.

### Tiers, in the order I recommend

- **Tier 1 - destructive directory/Exchange writes (9 pages, ~102 buttons).**
  M365GroupManagement, SelfServiceGroups, GroupManagement, NamedLocations, ConferenceRooms,
  MailboxPermissions, CalendarPermissions, IntuneDevices, DhcpAuthorization. A mistimed click
  here executes a real write against production AD or Exchange, or lands on a row being
  replaced. This is where the precedent's "dangerous" argument applies unchanged.
- **Tier 2 - configuration writes (4 pages, ~45 buttons).** ModuleConfig, AdminSettings,
  ADAttributeEditor, ExchangeOnlineConfig. ModuleConfig and AdminSettings write the shared
  config database, so a discarded click is a setting the operator believes was saved.
  ModuleConfig has 15 fully ungated buttons, the worst single page in the app.
- **Tier 3 - single-action and notification modules (7 pages, ~36 buttons).** MfaReset,
  EmergencyDisable, LicensingUpdates, Comms10k, OutOfOffice, BlockedSenders, RiskyUsers.
  Smaller surfaces, mostly one operation each.
- **Tier 4 - read-only and reporting (8 pages, ~35 buttons).** MessageTrace, AdminEventLog,
  AdminBulkJobs, BitLockerRecovery, ServiceHealth, MessageTraceReports, DelegationReport,
  RecipientLookup. The cost of a discarded click is wasted operator time, not a wrong write -
  but MessageTrace is worth doing, because its queries are slow and it is 13-for-13 ungated,
  which is the "invites repeated clicks" complaint that produced queue item 7.

`AccountLockoutRemediation` is skipped: parked with a disabled module.

### Effort

Estimated in sessions, because that is the unit `.agents/repo-guidance.md` bills in.
The Migration precedent is the calibration point: 31 buttons, two commits, seven tripwires,
one prerequisite fix and a review round.

| slice | pages | sessions |
| --- | --- | --- |
| 0 - scanner + shared harness + registry | - | 2-3 |
| 1 - stuck-flag prerequisites | 2 | 1 |
| Tier 1 | 9 | 5-7 |
| Tier 2 | 4 | 3-4 |
| Tier 3 | 7 | 3-4 |
| Tier 4 | 8 | 2-3 |
| manual dev-deploy acceptance, one pass per tier | - | 4 owner passes |
| **total** | **30** | **16-22 agent sessions + 4 owner passes** |

Per-page sessions are below one-page-one-session because slice 0 front-loads the expensive
part; small pages batch two or three to a session where they share no state.

**If that is too much,** slice 0 plus tier 1 is 7-10 sessions and covers every page that can
execute a destructive write. That is the natural reduced scope, and the tiers are ordered so
stopping after any one of them leaves the app coherent.

**The estimate excludes** review dispatches, which are per `.agents/decisions.md` and the
owner's go, and the manual passes, which only the owner can run because nothing in this repo
reaches the rendered page.

### Risk this introduces

The same one the precedent names: a page-wide predicate turns a stuck flag from one dead
button into a dead page. Slice 1 and shared assertion 3 exist for that, and the precedent's
review found one live instance in code that was believed correct - so the hazard is
demonstrated, not theoretical. This audit found two more.

### Versioning

Each page slice bumps its own module's `Version` in `Modules/ModuleCatalog.cs`. Slice 0 is
tooling and tests only, so nothing bumps. **No base app version bump** unless a slice
introduces genuinely shared UI infrastructure - a shared predicate base class would qualify,
and the plan currently proposes none. Per `docs/ProjectConstitution.md` and
`.agents/repo-guidance.md` Versioning.

## Verification

Per `.agents/repo-guidance.md`, for every slice:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`
- `Invoke-ScriptAnalyzer -Path . -Recurse` and `Invoke-Pester tests/ps` for slice 0
- guard proof per slice: mutate a representative control back, confirm the shared suite
  fails naming that page, restore byte-identically (touch the file after restoring - see
  the mutation-probe timestamp trap)

## Manual acceptance

Per tier, needs a dev deploy. Nothing in this repo reaches the rendered page.

- [ ] On each changed page, start the slowest operation and confirm every control greys,
      including non-button click targets.
- [ ] Confirm every control comes back after the operation completes. A stuck predicate
      leaves the page permanently dead; this is the main hazard.
- [ ] On pages with a staged confirmation, confirm the Confirm button is still enabled while
      the confirmation is staged. This is the regression the codex review caught on the
      precedent, where all three then-proposed tests would have passed the broken shape.
- [ ] Force a failure mid-operation (disconnect, or act during an outage) and confirm the
      page recovers rather than deadening.

## Pending decisions

One, for the owner:

**How much of the app to fix.** Slice 0 + tier 1 (7-10 sessions) covers every page that can
execute a destructive write. All four tiers (16-22 sessions) covers all 205 buttons.
Recommendation: approve slice 0 + slice 1 + tier 1 now, and re-decide on tiers 2-4 once the
shared harness exists and the real per-page cost is measured rather than estimated.

## Provenance

Owner queue item 9, raised 2026-09-15: "audit the app for clicks allowed when the system is
not ready to process them, the class fixed in Mailbox Migrations, then plan to fix all of
them and give me an idea of the effort." Picked as the next item on 2026-09-18 when the owner
closed item 7 and said "pick next item from the updated list".

---

# Revision 1 - reconnaissance findings, 2026-09-18

Slice 0 part A landed (`f0658c0`: `tools/Get-ClickGateAudit.ps1` + 14 Pester tests). Before
building the shared harness, an 11-page read-only reconnaissance ran over slice 1 and tier 1:
one deep read per page, then two adversarial refutation lenses each (is the control-flow
reading factually right; would the proposed gate trap the operator), then a synthesis. 34
agents, no errors. Full per-page reports are reproducible from the run journal; the durable
conclusions are here, because only repo files are durable memory.

It falsified enough of the design above to stop implementation. Everything below is evidence
from reading the files, with line numbers; where two agents disagreed and I could not settle
it from the source, that is said rather than smoothed over.

## What was falsified

### 1. The central risk statement is wrong: there is no ErrorBoundary anywhere in the app

Verified: zero matches across `Components/`, `Services/` and `Program.cs`; `MainLayout` carries
only the stock `#blazor-error-ui` reload bar.

An exception escaping a Blazor Server event handler therefore **tears the circuit down**. It
does not leave a live page with a stuck flag. So the plan's headline hazard - "a stuck flag
turns one dead button into a dead page" - is not what most of the audit's stuck-flag findings
actually produce, and **shared assertion 3 as written does not close the risk it was written
for**: a `finally` lowers the flag, and the exception still kills the circuit.

The genuinely page-deadening paths are a smaller, different set:

- **A call with no timeout and no cancellation token.** `BlockedSenderService.UnblockSenderAsync`
  (`Services/BlockedSenderService.cs:48`) takes no `CancellationToken` and the file contains no
  timeout. An Exchange Online stall leaves the circuit alive with `isLoading` true indefinitely,
  and neither a catch nor a finally rescues it. This is the worked example of the real hazard.
- **A normal return that skips a lowering nested inside an `if`.** MessageTrace's `ToggleDetail`
  lowers `detailLoading` at 987 and 996, both inside `if (token == detailRequestToken)`.

Consequence for slice 1: the remedy is **a catch that converts the throw into a visible failure
result**, not the bare try/finally this plan proposed. On BlockedSenders that means mirroring
the existing 351-356 shape into a `PermissionResult.Fail`.

### 2. The prescribed remedy for non-button controls is actively dangerous

The plan says non-button targets get a handler guard. Five refutations found independently, on
five different pages, that this **corrupts data** on any control whose rendered state mirrors a
C# field - a checkbox `checked=`, a radio, a `@bind` select, an `InputFile`.

The mechanism: the handler refuses, so the backing field is unchanged, so the render diff emits
no correction, so **the browser keeps the operator's action while the server never took it**.

- MailboxPermissions and CalendarPermissions: writes a permission the operator visibly
  deselected (three `@bind` checkboxes, two radios, one `InputFile` each).
- NamedLocations: worse, and silent. `CountryCodePicker` latches `_initialized` on its first
  parameter push (verified 45, 49-56) and Apply is the sole `ValueChanged` path (79-85), so a
  refusing `bind:set` desyncs **permanently** and writes the OLD country set to Graph.

**Three mechanisms are needed where the plan has one**: the disabled attribute (the only safe
one for DOM-synced controls), a child-component `Disabled=` parameter, and the handler guard -
which is safe only where no rendered attribute mirrors server state.

### 3. The scanner cannot see the controls that matter most

It matches `@onclick` on a fixed tag list. Invisible to it: every `@onchange` checkbox and
radio, every `InputFile OnChange`, every `@onkeydown` Enter path, every `@bind` select, and
every child-component `Disabled=` parameter.

**The audit's app-wide figure of 7 non-button click targets is an `@onclick`-only count.**
Across these eleven pages alone the real number is roughly **thirty-five**. Every one must be
hand-enumerated into the registry, and nothing detects an omission. This is the single largest
correction to the estimate.

### 4. A page-wide predicate is provably wrong on two pages

- **SelfServiceGroups** - the only page whose adversarial verdict came back **unsound**, not
  "sound with corrections". Two mutually exclusive views (browse when `selected == null`,
  manage otherwise, switched at 31) with disjoint flag sets. Gate `BackToGroups` (181) and the
  operator is trapped in the manage view for every post-write member reload; exempt it and
  manage-view flags leak into a browse view with no spinner. `BackToGroups` (verified 507-515)
  resets none of `isChanging`, `isLoadingMembers` or `isResolving`.
- **ConferenceRooms** - its Bulk Jobs panel (462-628) is driven by a background `OnJobChanged`
  callback (748/756) that replaces collections while a form handler is mid-await. The form
  predicate has no authority there.

The registry therefore needs a **list** of scoped predicates, not one name.

### 5. Mechanically applying a predicate deletes safety preconditions

**`IntuneDevices.razor:403` is the one to know about.** Its `WipeNameConfirmed` clause (defined
685-687, used at exactly that one place) is **the only enforcement of the typed device-name
confirmation for a factory wipe anywhere in the codebase** - `ExecuteActionAsync` re-checks
authorization, ticket and protected principal, but never re-reads `wipeConfirmName`. A
mechanical rewrite of 403 to the predicate deletes the second key on the most destructive
action in the app, with no server-side backstop.

`NamedLocations` 191/200 and `MailboxPermissions` 88 carry ticket-number and form-validity
clauses in the same position. Clauses must be **OR-ed with** the predicate, never replaced by
it, and an assertion has to enforce that.

### 6. On six of eleven pages the sharpest defect is not a gating defect

A post-await read of a live form field. Gating narrows the window; only a **snapshot at handler
entry** closes it, and this plan has no slot for that obligation.

- MailboxPermissions and CalendarPermissions read `tabIndex` after awaits to choose Add versus
  Remove (verified MailboxPermissions 363, plus 426/434/443/455), so a tab click mid-write
  **revokes what the operator asked to grant**.
- MessageTrace reads `ticketNumber` at 983/1076/1081 after awaits while the ticket input at 286
  is not disabled by either flag; its own report calls this the sharpest defect on the page.
- NamedLocations and M365GroupManagement read a blanked ticket into the audit row.
- DhcpAuthorization's ungated dismiss nulls `operationResult`, which `AuthorizeServer`
  dereferences at 273 after the email await at 264 and `RemoveServer` at 348 after 339 - so the
  dismiss can NRE the handler into its own catch, which then **audits and emails a SUCCEEDED
  forest-level AD write as failed** and skips the confirming refresh.

### 7. Several pages need a flag introduced before a registry entry means anything

- `GroupManagement.SelectGroup` (verified 401-429) awaits at 419 and 420, owns no flag, and has
  a `try` at 417 with a catch at 424 and **no finally**. The new flag needs a finally created
  for it, not just a name - and a lowering written into that catch in the surrounding style
  would be conditional on `ReferenceEquals(selectedGroup, group)` at 426 and stick forever on a
  swapped selection.
- `DhcpAuthorization.DownloadCsvAsync` and MessageTrace's CSV export own no flag at all.
  DhcpAuthorization's has an early `return` at 183-184; a raise above that guard leaks the new
  flag true forever and, being in a page-wide predicate, kills the whole page.

### 8. Assertion 2 is circular with slice 1

The structural detector qualifies a field as an in-flight flag only when it is lowered in a
`finally` - which is exactly what slice 1 creates. The two flags the prerequisite slice exists
for are therefore not reliably visible to the detector that assertion 2 compares the registry
against. Those entries must be hand-listed until slice 1 lands.

### 9. Hand-written per-page inventories are not trustworthy as a registry seed

Demonstrated within the recon's own output: ConferenceRooms' report omitted a mutating write
button (`ApplyTypeCsv` at 339, verified present) while citing that line in three other
sections; M365GroupManagement reported 606 lines against a file of 669; DhcpAuthorization 363
against 362; CalendarPermissions put its `isLoading` raise on the opening brace rather than the
raise. **The registry skeleton must be generated by the scanner and then annotated**, and every
entry needs a line count and a snippet fingerprint, or exemptions silently re-point at the
wrong control.

## Unsettled, and it changes the estimate

**Can Blazor Server interleave event callbacks across an await?** Four refutations assert a
rendered `disabled` attribute is stale until a render round-trip, and describe concrete
double-dispatch writes (CalendarPermissions `Continue` executing `ExecuteOnPrem` twice,
ConferenceRooms two overlapping detail fetches). One asserts the single-threaded renderer
context orders a later click behind the earlier continuation, so the window does not exist.
Neither side cited file evidence, because it is a framework question, not a repo question.

It decides whether markup-only gating is ever sufficient:

- If interleaving is possible, **every page needs an in-handler entry guard in addition to the
  attribute**, and the estimate rises accordingly.
- If it is not, the tab-flip and checkbox-desync hazards several reports rank highest largely
  evaporate.

**Assumption this plan proceeds on, labelled as an assumption:** interleaving IS possible.
Blazor dispatches event callbacks on the circuit's synchronization context, which runs one work
item at a time but yields at an `await`, allowing a queued second event to be dispatched while
the first handler is suspended. The framework does render at the first incomplete await, so the
attribute does reach the browser - but a network round-trip later. That is not provable from
this repo and no test here can settle it; it should be confirmed empirically on a dev deploy
before tier 1 relies on it either way. The design is cheapest to keep robust both ways:
**attribute plus in-handler entry guard**, belt and braces.

## Revised design

The registry becomes a record per page with these fields. Each was forced by a specific page;
the forcing page is named so none can be dropped as speculative.

| field | forced by |
| --- | --- |
| `Predicates` (a **list** of scoped predicates) | SelfServiceGroups' two views; ConferenceRooms' jobs panel |
| `ViewExitResets` | SelfServiceGroups `BackToGroups` resets none of three flags |
| `FlagsToIntroduce` (+ `RequiresNewFinally`, `RaiseMustFollowEarlyReturn`) | GroupManagement `SelectGroup`; DhcpAuthorization `DownloadCsvAsync` |
| `ExcludedFields` (a **list**, with confirm-control line) | M365GroupManagement's three staged fields; ConferenceRooms' two triples; IntuneDevices' seven |
| `ScannerFalsePositives` | `expandedBatch`, `targetProtection`, `resolution`, `finderJob`, `typeJob` |
| `RaiseAfterAwaitAllowList` | BlockedSenders 170 raises deliberately after two awaits so the spinner renders |
| `PreTryAwaitAllowList` | `await Task.Yield()` between raise and try is the house idiom on every page |
| `GatedControl.PreserveClauses` | IntuneDevices 403 `WipeNameConfirmed` |
| `GatedControl.RendersOnlyWhen` | GroupManagement 179/219; NamedLocations 130 |
| `ExemptControl.Snippet` | ConferenceRooms 508/572 share a label and handler across two loops |
| `ExemptControl.BecauseOfControl` | ConferenceRooms' Bulk Jobs tab is exempt only because Cancel lives behind it |
| `ExemptControl.ConditionThatKeepsItTrue` | ConferenceRooms 145/332, exempt only while their handlers emit constants |
| `ExemptControl.PrerequisiteBeforeExemptionHolds` | DhcpAuthorization 40, safe only once 252/327 snapshot |
| `NonButtonTarget.Mechanism` (5-value enum) | the desync class in falsification 2 |
| `ForbiddenGuardSite` (+ `ForbiddenOnlyIf`) | six of eleven pages call a refresh helper while already busy |
| `RequiredGuardSite.MustPrecedeLine` | MessageTrace: a guard in the shared callee fires after the damage |
| `ExactlyOneGuardOf` | MailboxPermissions/CalendarPermissions `ConfirmOnPrem` vs `ExecuteOnPrem` |
| `SpinnerExpressions` (verbatim, do not simplify) | ConferenceRooms 127/314/160/341; NamedLocations 57; DhcpAuthorization 52 |
| `SharedComponents` | `ADIdentityAutocomplete`, nine sites across four pages |
| `PostAwaitLiveReads` | falsification 6 |

Eleven shared assertions replace the plan's original six. Each carries its own honest
limitation; the two worth repeating here because they bound what this suite can ever claim:

- Text containment proves an identifier **appears**, never that it appears in a disjunctive
  position: `disabled="@(IsBusy && false)"` passes every assertion in the suite.
- `ForbiddenGuardSitesCarryNoGuard` proves absence of the obvious guard shape, not absence of
  refusal.

### The shared-component problem is outside any page's blast radius

`ADIdentityAutocomplete` gates only its `<input>` (verified line 13, `disabled="@Disabled"`);
its suggestion rows are `<li @onmousedown="() => SelectResult(result)">` at 27-31 and consult
`Disabled` nowhere. It is instantiated at nine sites across four pages; `RecipientAutocomplete`
at several more. Closing this is **one change to a shared component's contract**, and it
reaches tier 2 pages the owner has not approved. It cannot be done page by page.

## Revised page ordering, easiest to hardest

Reordered from the original tier-1 list on measured structure, not on write count:

1. **DhcpAuthorization** (362 lines, 7 buttons) - the only tier-1 page with zero non-button
   targets, verified by an exhaustive census. Two clean flags, one staged field.
2. **NamedLocations** (501 lines, 11) - all eleven targets are buttons; the subtleties are the
   `CountryCodePicker` parameter and three data preconditions that must be OR-ed in.
3. **MailboxPermissions** (657 lines, 11) - six non-button controls needing the attribute, the
   exactly-one-guard constraint, an ungated autocomplete that selects the write target.
4. **CalendarPermissions** (647 lines, 11) - structurally the twin, sequenced after so the
   decisions are inherited rather than re-litigated.
5. **IntuneDevices** (1600 lines, 9) - already the best-behaved: `ActionsDisabled` exists, all
   three flags raise before first await and lower in a finally. Danger is concentrated in line
   403 and seven excluded staged fields.
6. **GroupManagement** (911 lines, 13) - needs a flag and a finally created inside `SelectGroup`;
   its autocomplete gap is a shared-component contract change, not a page edit.
7. **M365GroupManagement** (669 lines, 19) - three staged fields, four flags, one flag wired to
   no attribute at all, a Cancel at 216 that inverts the usual exemption heuristic.
8. **ConferenceRooms** (1463 lines, 14) - two halves with different staleness sources, a
   background callback, two parallel staged triples, per-row controls sharing label and handler.
9. **SelfServiceGroups** (1109 lines, 20) - hardest by a clear margin, and the only "unsound"
   verdict.

## Revised estimate

The original tier-1 estimate of 5-7 sessions assumed button gating against a flat registry. It
did not account for ~35 hand-enumerated non-button targets, three refusal mechanisms, flags
that must be introduced, view-scoped predicates, snapshot obligations, or a shared-component
contract change.

| slice | was | now |
| --- | --- | --- |
| 0A scanner + Pester | - | **landed** (`f0658c0`) |
| 0B shared reader, registry, 11 assertions | 2-3 | 3-4 |
| 1 prerequisites (now catch-shaped, not finally-shaped) | 1 | 2 |
| tier 1, 9 pages | 5-7 | **12-18** |
| shared component contract (reaches unapproved pages) | not costed | 1-2 |
| snapshot-at-entry obligations, 6 pages | not costed | 2-3 |
| owner dev-deploy acceptance | 1 pass | 2-3 passes |
| **approved scope total** | **8-11** | **20-29 sessions + 2-3 owner passes** |

## The decision this is blocked on

Tier 1 as now understood is roughly **two and a half times** what was approved, and about half
the new work is a different defect class from the one queue item 9 named.

Three ways forward:

- **A. Full depth, all nine pages.** 20-29 sessions. Everything above closed.
- **B. Buttons only, all nine pages.** Close to the original 8-11. Delivers exactly what was
  approved and registers the rest as known-unclosed. **The objection is specific, not
  squeamish:** it ships pages that look gated while their checkboxes still desync, which is
  worse than an obviously ungated page, because the operator now trusts the greying.
- **C. Full depth, fewer pages.** Take pages 1-4 of the revised ordering (DhcpAuthorization,
  NamedLocations, MailboxPermissions, CalendarPermissions) to a standard worth copying,
  measure, then re-decide with real numbers instead of a third estimate.

**Recommendation: C.** It is the only option that replaces an estimate with a measurement, the
first two pages are genuinely small, and pages 3-4 are the twins - so the expensive thinking is
done once and inherited. It also defers the shared-component change to the point where its
blast radius is understood, rather than dragging unapproved tier-2 pages in now.

## Two things worth acting on regardless of that decision

Both are live defects today, neither is caused by this work, and neither is fixed by it.

1. **`IntuneDevices.razor:403`** - `WipeNameConfirmed` is the only enforcement of the typed
   device-name confirmation on a factory wipe; `ExecuteActionAsync` never re-reads
   `wipeConfirmName`. A UI-only key on the most destructive action in the app.
2. **`BlockedSenderService.UnblockSenderAsync`** (`Services/BlockedSenderService.cs:48`) - no
   `CancellationToken`, no timeout. An Exchange Online stall deadens the Blocked Senders page
   with no recovery but a reload, and this is the one confirmed live instance of the hazard the
   whole sweep is about.
