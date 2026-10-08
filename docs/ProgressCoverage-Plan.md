# Progress Coverage - Plan

Status: **APPROVED 2026-10-07 ("go"), IN PROGRESS.** S1 (the registry) has landed and **S2 IS
COMPLETE - all six silent writes report.** S3 (the nine partials) is under way; the first three,
`IntuneDevices.ExecuteActionAsync`, `RiskyUsers.ExecuteActionAsync` and
`MailboxPermissions.SubmitSingle`, have landed. **The live
gap list is
`ExchangeAdminWeb.Tests/ProgressRegistry.cs`, not this document** - the counts here were
correct at drafting and have moved twice since.

---

## Exec summary

*This is the only part of this plan the owner reads or is bound by
(`.agents/decisions.md` 2026-09-30, 2026-10-01). Everything below it is agent-facing.*

**What it does.** You reported one page where the progress bar stays idle. Checking all 33
pages found 41 places with the same problem - including six where an operator starts a change
that writes to AD, Exchange or Intune and the bar shows nothing at all. This fixes them, and
replaces the test that was supposed to prevent them.

**What it gets you.** Operators stop guessing whether a slow operation is running, stuck, or
already finished. The worst case today is Cloud Password Reset: the whole irreversible reset
runs with the bar idle, and because one step blocks the page for about thirty seconds, the
screen cannot update even to say so. That is the one most likely to make somebody click twice.

**Why it needs a plan rather than a quick fix.** The test meant to catch this checks each page
for *one* progress call anywhere in the file. Blocked Senders passes it today while the write
beside it is silent. Worse, the list of pages it checks is maintained by hand, so three pages
that report nothing at all are not checked at all. Fixing the pages without fixing that leaves
the next one free to ship the same way.

**What it costs.** Several sessions, one fix per commit. Twelve pages are tied to a
line-numbered registry from earlier work, so each of those needs a careful re-anchoring pass
on top of its edit - that is the bulk of the effort, and it is why the cheap pages go first.
Each module touched gets its version bumped; the base app version does not move.

**Biggest risk.** Every check this adds reads source code, not a running screen. There is no
way to test rendered UI in this repo, which is exactly how three defects slipped through
Emergency Disable two days ago. A page can satisfy every test here and still look wrong to
you, so the manual pass at the end is the real proof, not a formality.

**One thing that should change your answer.** One fix is not about progress at all. Cloud
Password Reset freezes the page for ~30 seconds during the reset, and a bar cannot draw on a
frozen page - so that one has to be moved off the UI thread or wrapping it achieves nothing
visible. That is a behaviour change to the most sensitive operation on these pages, and it is
inside what you would be approving.

**What approving authorises:** S1 through S5 below. It does NOT authorise adding timeouts or
cancellation to any service, fixing the unbounded query on Admin Bulk Jobs, or approving any
new click-gating tier - each is named as out of scope and needs its own go.

---

## Why this is not just "wire up Blocked Senders"

The operator reported one page. The reason it is worth a plan rather than a one-line fix is
that **the test that was supposed to prevent this cannot see it.**

`ExchangeAdminWeb.Tests/GlobalProgressWiringTests.cs:389`
`AnAdoptedModuleReportsItsWorkToTheStatusFrame` is a `[Theory]` over 24 hard-coded page names.
Per page it asserts exactly three things:

1. the page contains `IActivityProgress Progress`;
2. it contains at least one `using var X = Progress.Begin(`;
3. every `X` begun is `Completed` somewhere in the file.

`BlockedSenders.razor` is in that list and **passes today** while its only mutating operation -
`ConfirmUnblock`, a live Exchange Online write - reports nothing. One reporting call anywhere
in the file satisfies the guard for the whole page.

So the sweep that closed queue item 24 was measured by a per-FILE check and recorded as
per-OPERATION adoption. The gap is not that pages were missed; it is that the definition of
"adopted" was never the thing anyone wanted.

**There is a second hole, and it is the one that lets a whole page escape.** The `[Theory]`
list is hand-maintained. A page that is simply absent from it is never checked at all - and
the survey found three that inject `IActivityProgress` nowhere in the file:
`AdminSettings.razor`, `AdminBulkJobs.razor` and `ExchangeOnlineConfig.razor`. `AdminSettings`
runs an AD staleness sweep of N serialized lookups, each able to block for 30 seconds, with
the bar idle throughout.

Both holes have the same cause and the same fix: **the covered set must be derived from the
filesystem, not from a list someone remembers to update.** Enumerate every page under
`Components/Pages`; a page is then either covered or carries a written exemption, and
forgetting is not one of the options.

**This is the same failure class as `edl-3`'s first guard and the `>=` counting test**
(`.agents/token-log.md`, 2026-10-05): an assertion that is true of the code as written, standing
in for an invariant it does not enforce. Fixing only the pages leaves that intact and the next
silent operation ships the same way.

## Survey

Three parallel classification passes over all 33 pages under `Components/Pages` that make a
service call, one shared rubric, disjoint page sets. **198 operator-initiated operations examined.** An operation is operator-initiated if it is reached from `@onclick`, `@onsubmit`,
`@onkeydown`, or from `OnInitializedAsync`/`OnAfterRenderAsync` as an initial load.

| Verdict | Count | Meaning |
|---|---|---|
| SILENT, MUTATING | **6** | writes to AD, Exchange, Graph or Intune with the bar idle |
| SILENT, READ | **26** | slow remote or disk read, unreported |
| PARTIAL | **9** | reports, but the window misses slow work it still does |
| REPORTS / N/A | 156 | correct, or fast and local |

**The six silent writes, worst first:**

1. `CloudPasswordReset.ExecuteResetAsync:336` - the entire irreversible password reset.
2. `OutOfOffice.SetOof:260` - resolve, read, live EXO mailbox write, two emails.
3. `ADAttributeEditor.ConfirmSave:443` - auth, allowlist, AD write, email.
4. `ConferenceRooms.SetSingleRoomType:1207` - ticket, protection gate, `Set-Place` write.
5. `BlockedSenders.ConfirmUnblock:276` - the reported case.
6. `ExchangeOnlineConfig.SaveExoConfig:237` - save then a synchronous pooled-runspace teardown.

**Three pages inject `IActivityProgress` nowhere at all:** `AdminSettings.razor`,
`AdminBulkJobs.razor`, `ExchangeOnlineConfig.razor`. None is in the adopted list, so no
existing test looks at any of them.

### Group A - 11 pages, 77 operations

`SILENT+MUTATING` 3, `SILENT+READ` 2, `PARTIAL` 6. (The surveyor also returned
`Migration.StartReportExport:2682` as SILENT; the reviewer corrected it - it enqueues a
durable job whose progress `BulkJobService` already renders, so it is Exempt, not a defect.
The counts here are the corrected ones.)

These are the heaviest pages and most of them are correct. `Migration` (4,070 lines, 20
operations) and `GroupManagement` report everything they should. The gaps are concentrated.

**The three silent mutating operations:**

| Page | Operation | Note |
|---|---|---|
| CloudPasswordReset | `ExecuteResetAsync:336` | ticket, protection, fresh Graph resolve, inline `DeriveDestination`, the PATCH and the delivery email - the whole irreversible reset, bar reads Idle |
| ConferenceRooms | `SetSingleRoomType:1207` | ticket, protection gate, `Set-Place` write; `OperationTrace` only |
| ADAttributeEditor | `ConfirmSave:443` | auth, allowlist check, AD write, email; no activity at all |

**`CloudPasswordReset.ExecuteResetAsync` is the worst finding in the whole survey, and it is
not only a reporting gap.** The same page's own comment at :297-309 documents
`DeriveDestination` as a ~30-second blocking forest search that freezes the circuit unless
pushed to `Task.Run`. `LookupAsync` was fixed for exactly that on 2026-10-02.
`ExecuteResetAsync` calls it **on the renderer thread, without even that** - so during the
least-undoable operation in the app the bar is idle because the circuit cannot repaint at all.

`.agents/token-log.md` (2026-10-02) records this being left open deliberately at the time:
"`ExecuteResetAsync` on the same page reports nothing at all despite doing the PATCH and the
send". It was named and deferred, not missed.

**Two documented-intent violations - the comment says one thing, the code does another:**

- `IntuneDevices.ExecuteActionAsync:941`. Its comment at :984-986 states the activity is
  "completed in the finally AFTER the admin notification ... the bar must not read Idle while
  an email is still in flight". `Complete()` is at :1216; the two emails are at :1229 and
  :1247.
- `RiskyUsers.ExecuteActionAsync:816` has the identical ordering with no comment.

`ConferenceRooms.SetupSingleRoom:988` does it the way that comment describes, so the correct
pattern exists in-repo.

**Other PARTIALs:** `MailboxPermissions.SubmitSingle:350` and
`CalendarPermissions.SubmitSingle:346` both open the activity after a ServiceNow HTTP call and
a mailbox validation; `ConferenceRooms.SetupSingleRoom:895` opens inside `onAllowed`, after the
ticket check, protection gate and room read; `ADAttributeEditor.PerformSearch:291` completes at
:313 before the protection check at :329.

**Silent reads:** `ConferenceRooms.HandleFinderCsvUpload:1005` and `HandleTypeCsvUpload:1286` (per-row Exchange
lookups during preview).

**`ConferenceRooms` has the widest spread on one page** - two silent CSV preview handlers doing
N Exchange lookups each, one fully silent mutating write, one PARTIAL - while its two bulk
`Apply` paths are correctly wrapped.

**A scoping question the surveyor raised rather than decided:** if admin-email latency is
considered out of scope, `IntuneDevices` and `RiskyUsers` drop from PARTIAL to REPORTS and the
PARTIAL count falls from 9 to 7. This plan does NOT treat it as out of scope, because
`IntuneDevices` has a written comment promising the opposite - a page whose comment and code
disagree is a defect regardless of which behaviour is preferred.
### Group B - 11 pages, 68 operations

`SILENT+MUTATING` 2, `SILENT+READ` 19, `PARTIAL` 1.

**The two mutating ones:**

| Page | Operation | Note |
|---|---|---|
| OutOfOffice | `SetOof:260` | resolves the target, reads current OOF state, writes a live EXO mailbox config and sends two emails - no activity at any point, while the page's READ path at :233 reports correctly |
| BlockedSenders | `ConfirmUnblock:276` | the reported case, independently confirmed |

The surveyor rates `SetOof` worse than the reported bug: `ConfirmUnblock` at least lights the
bar via its trailing refresh on success, so the operator sees something late. `SetOof` never
lights it at all.

**Whole pages that report nothing:**

- `AdminSettings.razor` injects `IActivityProgress` nowhere. `OnAfterRenderAsync:546` fires an
  AD staleness sweep, and `SweepExistingEntriesAsync:771` makes N serialized
  `ADSearch.ValidateExists` calls **each able to block 30 seconds**. The four
  `AddPp*` handlers (:700-707) all reach the same unreported AD path.
- `AdminEventLog.razor` - six unreported operations, though NOT a wholly unreported page: the
  reviewer corrected an earlier wording here, and its undo paths (`ShowUndoPreview:839`,
  `ExecuteUndo:888`) do report. `LoadEvents:486` synchronously scans and
  parses every JSONL log in the date range; `LoadUsage:1271` runs three SQLite aggregates.
  Every entry point to them (`OnInitializedAsync`, `OnDateRangeChanged`, `ShowEventsView`,
  `ShowUsageView`) is silent.

**Others:** `MfaReset.ListMethods:176` (Graph), `EmergencyDisable.PerformLookup:293` (AD plus
Exchange-fallback resolve - in the module shipped two days ago),
`LicensingUpdates.RunPreview:270` (Delinea creds, a 2-minute AD throttle wait, per-row AD
resolve).

**`PARTIAL`: `MfaReset.ExecuteReset:226`.** Its `Begin` is at :382, but
`ResolveWithExchangeFallbackAsync` runs before it - an Exchange round trip this codebase's own
comments document as 10-15 seconds - and its email tail sits in a `finally` with no following
reported activity.

**Four pages share one unreported CSV-download shape** (`BlockedSenders:239`,
`DhcpAuthorization:199`, `NamedLocations:525`, `AdminEventLog:1115`). No backend call, only a
JS-interop byte push, so cost scales with row count. Classified SILENT because the house
pattern elsewhere (`ConferenceRooms:1145`, `MessageTrace:1177`) does wrap CSV export.

**The classification rule the surveyor applied, worth adopting as this plan's definition:**
ServiceNow ticket validation, the authorization re-check and the trailing notification email
sit outside the `Begin`/`Complete` window on nearly every mutating handler in this codebase;
that counts as REPORTS, not PARTIAL. PARTIAL is reserved for a genuinely slow remote call
outside the window.

**`NamedLocations` (:363, :451) is the reference implementation** - `Begin` before the `try`,
`Complete` in the `finally` after the email. It is the only page that gets the full shape
right, and the fix should copy it rather than invent one.

**`ModuleConfig.razor` is correct as it stands** and must not be "fixed": its single activity
covers `OUService.GetOrganizationalUnits`, the page's only remote call. Every `Save*` there is
a local write to the config store, which is why they are N/A rather than SILENT. (The
surveyor called these JSON file writes; the reviewer corrected it - config moved to SQLite
under `docs/SqliteConfigStore-Plan.md`. The N/A verdict is unchanged, the reason is not.)
### Group C - 11 pages, 53 operations

`SILENT+MUTATING` 1, `SILENT+READ` 5, `PARTIAL` 2.

| Page | Operation | Verdict | Note |
|---|---|---|---|
| ExchangeOnlineConfig | `SaveExoConfig:237` | SILENT, MUTATING | saves config then `ExoPool.DrainPool` tears down pooled runspaces |
| Comms10k | `DownloadFull:211` | SILENT, READ | resolves the full 10k-member AD group and builds a CSV |
| Comms10k | `LoadPreview:202` | SILENT, READ | AD credential fetch plus group query |
| Comms10k | `HandleFileUpload:234` | SILENT, READ | parses a browser upload; limit allows ~500k rows |
| Comms10k | `ValidateEmails:296` | PARTIAL | a remote ServiceNow ticket call happens BEFORE the `Begin` |
| MessageTrace | `OnAfterRenderAsync:661` | SILENT, READ | AD operator-email resolve behind a documented 30-second lock |
| MessageTraceReports | `Download:144` | PARTIAL | `Complete` at :159, the base64 transfer at :163 |
| AdminBulkJobs | `ToggleDetails:208` | SILENT, READ | `GetRows` has no `LIMIT`; a 10k-row job blocks the render thread |

**Two pages inject `IActivityProgress` nowhere at all:** `ExchangeOnlineConfig.razor` and
`AdminBulkJobs.razor`. Neither is in the adopted list, so no existing test notices.

Everything else in this group reports correctly, including several good patterns worth not
breaking: `TrueLastLogon.SearchAsync` uses `Steps(2)` across its DC and cloud halves, and
`MessageTrace.DownloadSelectedDetails` completes AFTER its download rather than before - the
exact opposite of `MessageTraceReports.Download`, which is what made that bug visible.

**A correction to this plan's own earlier draft, from the surveyor:** background bulk jobs are
NOT invisible. `Components/Shared/GlobalProgress.razor` renders active jobs for the submitter
via `BulkJobService`, a channel separate from `IActivityProgress`. Enqueues are honestly fast
and the long part is visible by that other route, so `Comms10k.ExecuteReplace` and
`MessageTrace.EmailSelectedDetails` are correct as they stand.

**Judgement calls flagged rather than hidden:** `HandleFileUpload` is a browser-side stream
parse, so "slow" depends on file size; `SaveExoConfig`'s cost is a synchronous `DrainPool`
teardown that was not measured. Both are reported as gaps on shape, not on a timing receipt.

**`AdminBulkJobs.ToggleDetails` is not only a progress gap.** An unbounded `GetRows` on the
render thread is a page-deadening hazard in its own right; a progress bar on it would be
honest about a problem that should not exist. Named here, fixed only as a reporting gap, and
recorded as a separate defect candidate.
## The guard, and why it is the centre of the work

Fixing the pages is mechanical. The decision that needs owner sign-off is what replaces the
per-file check.

**Proposed: a `ProgressRegistry`, in the shape of `ClickGateRegistry` but keyed by METHOD NAME
rather than line number, and enumerated from the filesystem rather than by hand.**

The page set comes from `Directory.GetFiles(Components/Pages, "*.razor")`. A page absent from
the registry fails the test; it cannot be silently uncovered. This is the part that would have
caught `AdminSettings`, which no current test looks at.

Every operator-initiated operation on a covered page is enumerated with one of:

- `Reports` - **and the entry must NAME the slow or mutating call it covers**, e.g.
  `BlockedSenderSvc.UnblockSenderAsync`.
- `KnownGap` - a defect recorded with its line, so the suite is green on the repo as it is
  today and each fix is "move this entry to `Reports`".
- `Exempt` - with a written reason, the way `ClickGateRegistry` requires a `RefusalMechanism`.
  "Local and fast", "enqueues a job `BulkJobService` already renders", "pure navigation".

The test fails on five conditions:

1. a `Reports` entry whose named call does not appear in the method;
2. a `Reports` entry where `Progress.Begin` does not DOMINATE the named call - the activity
   must open before it in source order;
3. a `Reports` entry where `Complete` or the end of the `using` scope precedes the named call;
4. an operation handler on a covered page that is in none of the three lists;
5. an `Exempt` entry with an empty reason.

**Conditions 1-3 are the reviewer's correction and they are the difference between this guard
working and this guard being theatre.** The first draft of this plan said the check could
credit "this method, or something it directly calls". Codex pointed out that
`ConfirmUnblock` calls `LoadBlockedSenders`, which HAS a `Progress.Begin` - so the
reported defect, unchanged, would have passed the very guard written to catch it. Naming the
covered call and asserting source order is what closes that. A helper's activity is credited
only when an entry says explicitly that the helper is the operation.

**Condition 4 must look for more than three attribute names.** The survey's rubric counted
`@onclick`, `@onsubmit`, `@onkeydown`, AND lifecycle loads - and real gaps use shapes outside
all of those: `InputFile` `OnChange` (`ConferenceRooms.razor:144`, `:331`,
`Comms10k.razor:91`), `@bind:after` (`AdminEventLog.razor:98`), and
`OnAfterRenderAsync` reaching slow work (`AdminSettings.razor:546-556`). A scanner that only
knows the three click attributes would let a new slow upload or bind-after refresh in
silently. Discovery must cover the same surface the survey did, and an inline lambda either
classifies as pure local UI or gets its method extracted and registered.

**Keyed by method name, deliberately.** `ClickGateRegistry` is line-keyed, which is why any
page edit invalidates every entry below it and needs the manual re-anchor procedure in
`.agents/playbooks/clickgate-reanchor.md` - a procedure with five named traps and no
automation. A name-keyed registry survives every edit that does not rename the method, so this
work does not inherit that cost and does not add a second line-keyed artefact to the repo.

**What it cannot do, stated plainly rather than discovered later:**

- It cannot prove an activity actually RENDERS. There is no bUnit harness here; every guard in
  this plan is a source-level scan. Three defects found in Emergency Disable on 2026-10-05
  were all presentation-layer and all invisible to this suite.
- Source-order dominance is a WEAKER claim than runtime coverage. The guard proves the
  activity opens before the named call lexically; it cannot prove the call is reached on the
  path that opened it, nor catch an early no-op `Begin`/`Complete` pair sitting above an
  unreachable branch. `CloudPasswordResetReportsItsDirectoryLookupInsteadOfFinishingBeforeIt`
  (`GlobalProgressWiringTests.cs:502`) remains the precedent for a bespoke per-page assertion
  where that matters, and the registry does not replace it.
- It cannot follow slow work several calls deep. An entry names ONE covered call; an
  operation whose cost is spread across a chain is registered against the first one and the
  rest is unproven.

## Cost drivers

- **12 pages are ClickGate line-pinned**: Migration (4070 lines), ConferenceRooms, SelfServiceGroups,
  IntuneDevices, GroupManagement, M365GroupManagement, DefenderEndpointDevices,
  MailboxPermissions, CalendarPermissions, TrueLastLogon, NamedLocations, DhcpAuthorization.
  Adding an activity adds lines, which invalidates every line-keyed entry below it. Each such
  page costs a re-anchor pass on top of the edit, by the playbook, never by hand-computed offset.
- The remaining pages are unpinned and cost only the edit and its test.
- Slicing therefore follows the pin, not the module: unpinned pages first (cheap, and they
  prove the registry), pinned pages one per slice.

## Scope and slice order

41 defects: 6 silent writes, 26 silent reads, 9 partials. **One finding or fix per commit**
(`.agents/repo-guidance.md`), so the groupings below are slice THEMES, not single commits.
Each ClickGate-pinned page carries its own re-anchor pass and its own proof.

**S1 - the registry and its test, no page edits.** Build `ProgressRegistry` enumerated from
the filesystem, seeded with the survey's verdicts, and every current gap entered as
`KnownGap` with its line reference. The suite goes green on the state of the repo as it is
today. This is the slice that makes every later one cheap and provable: a page is fixed by
moving its entry from `KnownGap` to `Reports` and watching the test demand the call.

Doing it first also means the gap list stops living in this document, which goes stale, and
starts living in a file CI reads.

**S2 - the six silent writes, one commit each, highest risk first.** Not one slice: repo
guidance is one finding or fix per commit, and several of these pages are ClickGate-pinned. These are the operations where an operator can
start an irreversible change and see nothing. `CloudPasswordReset.ExecuteResetAsync` leads,
and carries the one piece of non-reporting work this plan does take on: moving
`DeriveDestination` off the renderer thread. A bar cannot paint on a frozen circuit, so
wrapping it without that fix would produce a guard that passes and an operator who still sees
nothing - the exact vacuous outcome this plan exists to stop.

**S3 - the nine partials, one commit each.** Mostly moving a `Begin` earlier or a `Complete`
later.
`NamedLocations:363,451` is the reference shape: `Begin` before the `try`, `Complete` in the
`finally` after the email. `IntuneDevices` and `RiskyUsers` are included because their
comments promise the behaviour their code does not have.

**S4 - the three unreported pages.** `AdminSettings`, `AdminBulkJobs`, `ExchangeOnlineConfig`
inject nothing. `AdminSettings` first: its staleness sweep makes N serialized AD lookups, each
able to block 30 seconds.

**S5..Sn - the remaining silent reads, unpinned pages first.** Each ClickGate-pinned page is
its own slice with its own re-anchor pass.

### Registered as exempt, not fixed

- `ModuleConfig.razor`'s `Save*` handlers - local config-store writes, correctly N/A today. Entered as
  `Exempt` with that reason so a later agent does not "fix" them.
- Enqueue handlers (`Comms10k.ExecuteReplace`, `MessageTrace.EmailSelectedDetails`,
  `Migration.StartReportExport`) - see the corrected note below.

## Deliberately NOT in scope

- **Timeouts and cancellation.** `BlockedSenderService.UnblockSenderAsync` takes no
  `CancellationToken` and its service has no timeout - recorded in `.agents/state.md` as the
  one confirmed live instance of the page-deadening hazard. A progress bar on a hung operation
  is still a hung operation. It is a real problem and a different one; folding it in would make
  the blast radius larger than the bug.
- **Background job visibility - and an earlier draft of this plan was WRONG about it.** This
  section originally called it an out-of-scope product question. The survey falsified that:
  `Components/Shared/GlobalProgress.razor` already renders active jobs for the submitter via
  `BulkJobService`, a channel separate from `IActivityProgress`. Enqueues are honestly fast
  and the long part IS visible, by that other route. So enqueue handlers are correct as they
  stand and are registered `Exempt` with that reason - not deferred, not a gap.
- **`AdminBulkJobs.ToggleDetails:208` as a performance defect.** `GetRows` has no `LIMIT`, so
  a 10,000-row job blocks the render thread. This plan gives it a progress call, which is
  honest reporting of a problem that should not exist. The missing `LIMIT` is a separate
  defect and is recorded as a candidate, not fixed here.
- **Re-tiering any ClickGate page.** Tiers 2-4 remain unapproved
  (`docs/ClickGatingAudit-Plan.md`); re-anchoring an existing registry is not approving new
  gating work.

## Versioning

Per-module behaviour change on each page touched, so each module's `Version` in
`Modules/ModuleCatalog.cs` bumps in the slice that changes it. The base app version moves only
if shared code changes - the registry and its test live in the test project, which is not
shipped, so on current scope the base app version does NOT move. Re-check if a slice ends up
touching a shared component.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`
- `dotnet test ExchangeAdminWeb.slnx --filter "FullyQualifiedName~ClickGate"` green before any
  pinned-page slice is called done - a registry pointing at wrong lines still passes some of
  its tests, so a partial green is not done.
- Each slice proves its guard non-vacuous: delete the activity, watch the registry test name
  that method, restore, confirm green. `touch` the file after restoring (MSBuild skips the
  rebuild on a preserved mtime - this has produced false results here before).
- Dispatch codex per slice.

## Manual acceptance - owner or operator

Nothing here proves the bar renders. Per fixed page: run the operation and confirm the bar
shows it running and clears afterwards; confirm it does not report complete while the page is
still working.

## Owner gate

Go or no-go on the registry design and the slice order. The survey's gap list is reported, not
negotiated - those are defects or they are not.

---

## Plan review, 2026-10-07

`openreview`, pins `dd57684..1eb14e7`.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier (`fallback` alias;
effort `max` re-probed 2026-10-06 and still rejected by the gateway).
Raw output: `.agents/review/progress-plan.result.json`.

**Verdict: acceptable with changes.** Four findings, all four applied above.

1. **HIGH - the registry needed call-anchored proof.** The draft would have credited "this
   method, or something it directly calls", and `ConfirmUnblock` calls `LoadBlockedSenders`,
   which has an activity - so the reported defect would have passed the guard written to
   catch it. Entries now name the covered call and assert `Begin` dominates it.
2. **HIGH - handler discovery was narrower than the survey's own rubric.** It cited
   `InputFile OnChange`, `@bind:after` and lifecycle paths already carrying gaps. Discovery
   widened to match.
3. **MEDIUM - three survey corrections**, all applied: `Migration.StartReportExport` is a
   background-job enqueue and is Exempt, not a defect (total 42 -> 41);
   `AdminEventLog` does report its undo paths and was overstated as wholly unreported;
   `ModuleConfig`'s `Save*` are config-store writes, not JSON file writes.
4. **MEDIUM - S2 and S3 were too broad**, against the one-finding-per-commit rule. Split.

It spot-checked the survey as asked: verified `CloudPasswordReset.ExecuteResetAsync`,
`OutOfOffice.SetOof` and `MfaReset.ExecuteReset` as classified, and tried and failed to
falsify `NamedLocations.SaveLocation`/`DeleteLocation` and `TrueLastLogon.SearchAsync`. The
survey's shape holds; its three errors were all in the direction of overstating the problem.
