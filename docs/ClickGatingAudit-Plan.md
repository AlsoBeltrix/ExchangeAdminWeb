# App-wide click gating: audit and remediation plan

Status: **Approved in part, in progress.** Queue item 9. Drafted 2026-09-18 against `3e19aef`.

**Approved scope, owner 2026-09-18: slice 0 + slice 1 + tier 1 only** - the scanner and shared
test harness, the two prerequisite stuck-flag fixes, and the nine pages that can execute a
destructive AD/Exchange/Graph write. Tiers 2, 3 and 4 are **not** approved; they are re-decided
once the shared harness makes the per-page cost a measurement rather than the estimate below.
Implementation must not exceed that scope.

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
