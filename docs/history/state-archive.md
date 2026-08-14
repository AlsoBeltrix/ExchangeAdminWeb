# State Archive

Landed and superseded `## Now` entries rotated out of `.agents/state.md` by the
state-hygiene sweep, kept verbatim for history. Newest first. The sweep is
`.agents/playbooks/drift.md`; the older sections below were written when `catchup`
still ran it inline.

> **Terminology correction (2026-07-28):** the archived text below repeatedly says
> "manual-validation-on-dev / no dev tenant." That wording is wrong and is retained only
> as verbatim history. There is no separate dev tenant by design: both the dev and prod
> instances run on this server and connect to the same live PROD AD/Exchange. The dev
> deploy proves the app loads; functional validation is done against real PROD data and
> can be run from the dev instance. Read every "no dev tenant" below as "live validation
> not yet performed."

## Archived 2026-08-14 (drift sweep)

Rotated out of `.agents/state.md` verbatim. The first two entries came from `## Now`
(both declared closed with nothing outstanding); the rest are the work-stream entries that
had drifted under `## Blockers`, which that section itself labelled history rather than
current state.

### Rotated from `## Now`

- **Migration Status batch selection: DONE, ACCEPTED, and DEPLOYED TO BOTH 2026-08-10** --
  *"looks fine. ran through a few checks, calling it good."* Two rounds; Migration `1.5.0` ->
  `1.6.0` -> `1.7.0`. **Dev and prod both run app `2.8.0`** (verified from both assemblies).
  `docs/MigrationBatchSelection-Plan.md`, decisions D1-D8.
  **Acceptance, not a completed check list.** Which of the plan's checks were run is unknown; the
  load-bearing ones (mixed selection per button, `CompletedWithErrors` actionable, deselect and the
  "queued" wording) are recorded as unverified. Anyone re-opening this should not read the owner's
  go as evidence those passed.
  **A VERSION ERROR the owner caught from the deployed page, and it is the recurring one.** Round 1
  set Migration `1.6.0`; three later commits changed Migration behaviour (`1ef7fae`, `2ff7d7f`,
  `52eb7e9`) and none bumped it again, so dev ran the round-2 code under the same module version as
  the build tested before any of it existed. **Two builds sharing one version is worse than a wrong
  number** -- the `2.5.1` failure repeating. What made it hard to see: the base app version WAS
  correctly bumped to `2.8.0` in the same window for the favicon, so the sidebar read right and the
  module version looked right by association. **The two versioning rules fire independently; a
  correct app bump is not evidence about the module.** Fixed to `1.7.0`.
  **Round 1 passed 4 of its manual checks and failed on the ACTION MODEL.** Ticking two batches and
  clicking Resume returned *"No batches to act on. Skipped 2: ... (Completed), ...
  (CompletedWithErrors)."*
  **`CompletedWithErrors` is a real Exchange batch status that appeared NOWHERE in this codebase.**
  Every status comparison on the page was an exact match against a hardcoded list, so such a batch
  could not be deleted, could not be resumed, was not swept by Clear Completed, and drew the
  unknown-status grey badge. **Pre-existing; the checkboxes only made it visible** - and made it
  worse in one way, since the operator now ticks a row and is told there is nothing to do, where
  before the button simply never rendered.
  **THE LESSON, and it generalises past this page: an exhaustive-looking status allowlist written
  from the statuses a developer has seen is a silent filter, not a safety rail.** No test could have
  caught it -- every test used the same status list the code did. It appeared THREE times in this
  one page (Delete, Resume, and the per-user Report button), each hiding something the operator
  wanted, and all three were found by using the app, not by review or tests.
  **Owner's action model (D3), which replaced mine:** Delete acts on ANYTHING ticked (Exchange
  decides; a refusal is that row's own named failure); Remove Completed acts on exactly `Completed`,
  never `CompletedWithErrors` -- *"a batch that finished with errors is not a batch that finished"*,
  and sweeping it destroys the evidence; Resume/Retry acts on anything idle but restartable.
  **D4: Resume eligibility is defined by EXCLUSION** -- everything except the working statuses
  (`Syncing`, `Starting`, `Stopping`, `Completing`, `Removing`) and `Completed`. That inversion is
  the fix's substance: an unanticipated status now defaults to VISIBLE and lets Exchange refuse it,
  rather than silently vanishing from the UI. **`Completed` had to be excluded explicitly -- the
  first draft of this rule excluded only the working statuses, and the owner caught it:
  idle-and-restartable is not the same as idle.**
  **D6: the standalone Clear Completed sweep is REMOVED.** All-or-nothing destruction sitting beside
  "Clear selection" sharing a word; owner: *"unacceptable."* Buttons are now Delete / Remove
  Completed / Resume-Retry / Untick all -- no two share a leading verb.
  **D7: Report is offered on every user row**, not only rows that already look broken.
  **D8, from a second dev pass: the selection did not clear after an action, and the result claimed
  work that had not happened.** *"it says it removed but didn't deselect. removal isn't instant and
  the message is unclear since the 'removed' entry is still there."*
  (a) `PruneSelection` only drops batches that have LEFT the table, and removal is asynchronous -
  the batch sits at `Removing` and stays ticked, inviting a second click on work already in flight.
  Batches Exchange ACCEPTED are now deselected; ones that FAILED stay ticked, because nothing was
  queued for them and retrying is the likely next move. A blanket `Clear()` would have taken the
  failures and the skips with it, which are the rows the operator still needs.
  (b) **The cmdlets return when Exchange ACCEPTS the request, not when the work is done**, so
  "Removed 1 batch(es)" rendered over a row still visible on screen. Verbs are now "Queued removal
  of" / "Queued restart of" plus a line saying Exchange finishes in the background. **The same lie
  lived in `MigrationService` for the single-row path** and is fixed there too - fixing only the
  bulk wording would have left it on the row that reported it.
  **The pattern across D3-D8 is one thing: this page kept ASSERTING states it had not verified** -
  that a status list was exhaustive, that a queued removal was a completed one. Both are the same
  error in different clothes, and both were found by an operator using the app.
  **What round 1 got right and should not be re-litigated:** checkbox mechanics, `BatchName`-keyed
  selection surviving a re-sort, the inline ticket field beneath both batch and user rows, and the
  single aggregating executor (per-batch audit inside the loop, audit failures as warnings, one
  notification per run). Checks 1, 4, 5 and 5b all PASSED on dev.
  **Also withdrawn: manual check 7** ("delete a batch in EAC, refresh, confirm it de-ticks"). It
  presumed a local store of migrations; `GetMigrationBatchesAsync` runs `Get-MigrationBatch` against
  Exchange on every load, so there is no cache to go stale. I wrote it on a caching reflex that does
  not apply here.
  **`mbs-1` (MEDIUM, `1ef7fae`) was found by a `codereview` pass over round 1** and is closed:
  per-user actions prompted for the ticket at the top of the table because the inline confirm
  matched a BATCH NAME while `StageUserAction` sets an EMAIL. The plan's D1 covered that case and
  the implementation delivered only the outer half. **23 guards, ten mutation probes and 1645 green
  tests all passed while it was broken, because none of them reads the plan.**
  Detail in `.agents/review/findings/mbs-1.md`; the rule it earns is that when an owner ruling and
  a later implementation note disagree, the ruling wins and the note is what gets corrected.
  Owner report 2026-08-10: *"the exchange migration status page needs checkboxes on each row to
  allow batch clear/delete and resume. the ticket number entry field for individual items needs to
  be closer to the actual button people hit ... because we routinely have ~50+ in-flight and the
  ticket number entry field is buried at the top of the table and UI doesn't make that obvious."*
  **Two independent defects, both only visible at 50+ rows.** (1) No multi-select anywhere in the
  status tab - the only bulk affordance is all-or-nothing `Clear Completed`. (2) `StageBatchAction`
  renders the ticket confirm bar ABOVE the table, so clicking Resume on row 47 puts the input
  off-screen while that row's buttons all go disabled - the visible feedback is that the buttons
  stopped working.
  **Owner rulings, both in the plan's Decisions section (canonical there, not duplicated in
  `.agents/decisions.md`):** D1 outer Batches table only, no checkboxes on the inner per-user table.
  D2(a) a bulk action acts on the eligible rows and NAMES every skipped row with its status - a
  skip is not a failure, skips are not audited (no write attempted), the bulk buttons stay enabled
  whatever the selection, and skipped rows stay ticked. D2(b), disabling the button on a mixed
  selection, was rejected as a return to acting one row at a time.
  **Slice 1 (`Services/MigrationBatchActionPlanner.cs`, 36 tests):** pure partition of a selection
  into eligible/skipped, selection pruning across a reload, and the SINGLE definition of which
  statuses each action permits - the per-row Resume/Delete buttons now read it too, because two
  copies of "which statuses may be deleted" is how a bulk action and a row button come to disagree
  about the same batch. Keyed on `BatchName`, never row index: the table re-sorts on every header
  click, so an index-keyed selection silently retargets to whatever now occupies that position.
  **Slice 2 (page):** checkbox column, select-all over loaded batches, selection toolbar, and
  `ExecuteBulkBatchAction` EXTRACTED from `ClearCompletedBatches` rather than written a second time
  - that code already had the subtle parts right (one audit event per batch inside the loop;
  audit-write failures as WARNINGS so an audit failure cannot make a completed removal look failed;
  one summary notification per run, not fifty). Clear Completed now routes through it, and a guard
  asserts it still does: if it forks again there are two sets of aggregation rules.
  **Slice 3:** the ticket confirm bar moved from above the table to directly beneath the acting
  row, as one `RenderFragment` rendered in two places. The top-of-table position survives for
  actions naming no single row (Clear Completed, both bulk actions) and for a row that has since
  disappeared - otherwise the bar becomes unreachable.
  **Slice 4:** Migration `1.5.0` -> `1.6.0`, README, this file.
  **Verification: 1642+ passed, 0 failed, build/format/`git diff --check`/ASCII clean. TEN
  mutations, each confirmed on disk before trusting the verdict and confirmed gone after** -
  checkbox removed, audit hoisted out of the loop, per-row Delete forking its own status chain,
  Clear Completed forking its own loop, a stale plan reused at confirm time, pruning dropped from
  the reload, the inline confirm reverted, the top-of-table fallback deleted. All caught.
  **Two of my own guards were wrong and only the probe found them - this is the reusable part.**
  (1) Disabling the membership check in `PruneSelection` left all 34 slice-1 tests GREEN, because
  every prune test happened to select every loaded row. That mutant ADDS unticked rows to the
  selection on the next reload, and the following Delete removes batches the operator never chose.
  **A test that only checks nothing was wrongly REMOVED from a set says nothing about what was
  wrongly ADDED to it.**
  (2) `GetBatchRowMarkup` sliced the row markup up to the first
  `@if (expandedBatch == batch.BatchName && batchUsers != null)` - text that also appears earlier
  inside the Details button - so the slice stopped short of the markup it covered and reported a
  real change as missing. Now brace-balanced. **A marker that occurs more than once is not a
  boundary.**
  **Environmental trap worth keeping: `Copy-Item` restoring a probe backup carries the BACKUP's
  timestamp, so MSBuild judged the DLL up to date and kept testing the mutant** - three tests
  "failed" against correct restored source. Reading the file back to verify the restore was not
  enough; the build has its own idea of current. Touch the file after any timestamp-preserving
  restore.
  **NEXT: nothing on this work stream.** It is accepted and closed on dev. The only outstanding
  item is a PROD deploy, which carries this plus the favicon and is the owner's call.

- **Protected-principal servicing: CODE COMPLETE at `80d2759`, DEPLOYED to dev and prod 2026-08-10
  as `2.7.0` (verified from both assemblies). All 15 modules, all 3 review findings fixed. 1586
  passed / 0 failed / 3 skipped, format clean. THE SERVICER GROUP NOW EXISTS AND IS CONFIGURED -
  `ANALOG\ExchangeWebAdminsExecSupport` (SID `S-1-5-21-8915387-325452579-1788637320-710891`),
  granted on dev and prod identically for `ProtectedServicer:MailboxPermissions`,
  `ProtectedServicer:CalendarPermissions` and `ProtectedServicer:OutOfOffice`. NO MANUAL CHECK RUN,
  so the capability is configured but unproven end to end.** That is the whole of what remains;
  see `## Next`.
  Owner: *"all of them. every place where a principal is protected we need to allow a priv
  group to act on them anyway."*
  **THE THREE FINDINGS, all fixed** - `.agents/review/findings/pps-{1,2,3}.md`, all `[x]` in
  `.agents/review/index.md`. All three were the same shape, which is now this repo's signature
  failure: **the service was right and the PAGE, or the call site, was wrong.**
  **pps-2 (`efd25ad`)**: `ADAttributeEditor.razor` blocked at lookup with no servicer consultation
  and hid the edit UI, so the serviced save gate was unreachable; undo preview took no principal
  while execute did. **This was the Emergency Disable two-gate shape I recorded during the work and
  then applied only to the six remaining SERVICES, never to the pages of the nine already done.**
  The rule it earns: *a page gate that hides the write UI is part of the authorization decision,
  not a display detail.* A serviced operator now also sees a banner - an override they cannot see
  is one they cannot decline.
  **pps-3 (`a345342`)**: the undo service evaluated `NoteFor(...) is null` inside a boolean and
  discarded the note on the ALLOW path; Emergency Disable kept its note in the operation trace and
  out of the audit event. **The helper returns a nullable note precisely so permission and record
  cannot be separated; a bare null test defeats that by design.** A guard now forbids that call
  shape across `Services/` and `Components/Pages/`, with one bounded exemption for the no-audit
  preview path.
  **pps-1 (`57ab7a5`)**: bulk CSV called the back-compat overload that can never service, so a
  servicer was allowed one row at a time and refused for the same mailbox in a CSV; and
  `ExecuteOnPrem` re-checked authorization but not protection after its confirmation dialog.
  **The reviewer named only Mailbox for the on-prem half - Calendar had the identical defect and
  was found by reading the pair rather than assuming they had diverged.**
  **Two caveats on the review, both load-bearing for whoever reads it next.** Its auth token was
  revoked mid-run (`refresh_token_invalidated`), so it **never produced a final consolidated
  report** - the findings were recovered from the reasoning trace in `.git/codex-review-out.txt`
  (untracked, may not survive) and each was verified against the current code before recording.
  Severities and proposed fixes are MINE, not the reviewer's. And it found these by treating the
  commit messages as CLAIMS to check: my own `e1547e7` message asserting these modules honour
  servicing is what made the gap visible.
  **Its analysis phase confirmed clean:** DI lifetimes (no captive dependency), ASCII, the opt-in
  list against real gates for all 15 modules, Migration's per-target notes surviving the
  audit-failure rewrap, Self-Service not letting the grant bypass ownership, Conference Rooms'
  deliberate null-principal bulk refusal, and Licensing's request-thread decision.
  Plan `docs/ProtectedPrincipalServicerAllModules-Plan.md`, revised after grok review (5 findings,
  3 HIGH, all folded in).
  **The commits, one module or slice each:** `b351005`/`1ba7d49`/`8fb0592` (audit `extra` channel,
  servicer service Scoped -> Singleton, Conference Rooms), `e1547e7` (seven modules at once: MFA
  Reset, Emergency Disable, Comms-10k, AD Attribute Editor + undo, Mailbox, Calendar, Out of
  Office), `c0fc79e` GroupManagement, `5f07fe8` M365GroupManagement, `037365c` SelfServiceGroups,
  `c745f78` Migration, `15c001d` AccountLockoutRemediation, `6f7f2ac` LicensingUpdates.
  **`Services/ProtectedPrincipalServicing.cs` is the shared helper every module uses** -
  `NoteFor(...)` returns the audit note or null-to-refuse, `Extra(...)` wraps it for an audit call.
  Returning a nullable note rather than a bool is deliberate: a caller cannot allow the action while
  forgetting to record why.
  **The invariants every gate holds, and what to check any future module against:** protection is
  evaluated FIRST and never weakened; fail-closed outranks servicing (unavailable / ambiguous /
  check-failed still deny, because they do not know whether the target is protected, so there is no
  refusal to override); a null acting principal REFUSES; the grant is PER MODULE; the note names
  both the authorising group and the rules overridden; and it travels in the audit event's `extra`,
  never `errorDetail`, which is written as null on success and would silently discard it.
  **Five shapes the plan did not predict, all generalisable:**
  (1) **Emergency Disable has TWO gates** - the page hides the write UI at lookup, the service
  blocks again at the write. Both must honour servicing or the servicer never reaches the button.
  (2) **`ValidateTargetMailboxAsync` serves THREE modules**, so it returns a `TargetValidation`
  record and takes an explicit `moduleId`; a borrowed id would let a Mailbox grant authorise
  Calendar or Out of Office.
  (3) **Migration's gate was a DELEGATE**, and per-target: `PartitionByProtectionAsync` returns the
  serviced notes as a LIST beside allowed/excluded, one per override, because a batch-level "some
  target was serviced" cannot say which - the question an audit exists to answer.
  (4) **Off-request-thread work cannot decide servicing.** LicensingUpdates' `ApplyChanges` runs
  under `Task.Run`, where the acting principal is not ambient and reading one would attribute the
  override to whoever owns the thread. The decision moved to `EvaluateProtectionAsync` on the
  request thread; `ApplyChanges` receives already-decided notes.
  (5) **A note keyed for one loop is not keyed for the other.** LicensingUpdates keys the apply loop
  on `PrincipalKey` (ObjectGuid when present) and the audit loop only ever sees a UPN, so a single
  view would have made the write with no record of who permitted it. Two views, both tested.
  **Three services needed an internal TEST SEAM** (`SelfServiceGroupService.CheckMemberProtectedAsync`,
  `AccountLockoutRemediationService.GuardTargetUsersAsync`,
  `LicensingUpdatesService.EvaluateProtectionAsync`): each sits behind a credential fetch and a live
  directory read, so the servicer path is unreachable from the public method in a test. The project
  already exposes internals to the test assembly; all three stay decisions with no side effects.
  **Every existing protection suite passes UNMODIFIED in substance** - the only edits were
  constructing a servicer service over a store with no grant (so it denies) and passing
  `actingUser: null`. That was the standing gate: editing one to accommodate a servicer path would
  mean a refusal quietly became an allow.
  **Non-vacuity proven per module by reverting the servicer path**, each time confirming the revert
  had actually landed before trusting the verdict and confirming it was gone after: GroupManagement
  2 of 6 fail, M365 4 of 8, SelfServiceGroups 1 of 6, Migration 2 of 8, AccountLockout 1 of 7,
  Licensing 3 of 8 - in every case exactly the allow-path tests, with the refusal tests still
  passing.
  **Slice 0a caught a real defect in itself:** nine methods gained the `extra` parameter and
  `LogLookupAction` was left without the merge - a parameter accepted and silently dropped.
  `AuditExtraChannelTests` found it. That is the most repeatable mistake in this work.

### Rotated from `## Blockers` (landed work-stream history)

- **The protected-principal SERVICER capability was unreachable for a day, and the export UI was
  unusable. Both fixed 2026-08-07. NOT DEPLOYED.**
  `docs/ProtectedPrincipalServicerAdminUI-Plan.md` and `docs/MessageTraceExportUX-Plan.md`, both
  Implemented, both reviewed clean by **grok** (`grok-4.5-build`, 0 findings) as plans before any
  code was written. MessageTrace `1.4.0 -> 1.4.1`.
  **SERVICER: marked IMPLEMENTED 2026-08-06 while no operator could reach it.** The service was
  registered, consumed by `BlockedSenderProtectionGate` and unit-tested - and nothing wrote the
  `ProtectedServicer:<moduleId>` key it reads, so no group could ever be granted it. Verified at
  the time of the fix: zero such rows in BOTH live config stores.
  **Rule this earns, and it generalises: a capability is not implemented until the person meant to
  use it can reach it.** Registered + consumed + unit-tested is not usable, and marking the plan
  Implemented on the first three hid the missing fourth. `docs/ProtectedPrincipalBreakGlass-Plan.md`
  is corrected in place rather than quietly re-marked.
  **The hazard that shaped the fix: `SaveSectionAccess` -> `SaveAll` -> `ClearAndInsert` REPLACES
  the whole section-access store.** `ModuleConfig.razor` is safe only because it reads every alias
  and writes the full map back, so the new editor JOINS that read-modify-write instead of adding a
  second save path. Two behavioural tests against a real SQLite store pin it - the failure would be
  silent destruction of authorization state, whose only symptom is a team quietly losing access.
  **Caught while implementing:** adding the alias to `policyAliases` for the save path also made the
  ORDINARY grant loop render it again - unwarned, captioned as plain module access, presenting a
  protection bypass as a normal grant. Excluded, and guarded.
  **EXPORT UX: nothing misbehaved and the feature was still unusable.** Two different exports (all
  results/summary CSV; max-50 per-message detail) shared one panel that named neither, a correct
  "export to get them all" pointed at the wrong control, and a correctly-disabled button gave no
  reason. Owner: *"it's unclear how to get anything... the download button doesn't work."*
  **Presentation was the defect** - no threshold, service or delivery mechanism changed.
  **`ppsvc-1` (HIGH) was found by the codex review of the landed diff, and it is the case for
  running that review even when a change looks safe.** On a server where section access has never
  been configured, `GetGroupsForSection` falls back to the legacy app-wide `Security:AllowedGroups`
  unless the section is fail-closed - and the fail-closed set is built from CATALOG POLICY ALIASES,
  which a `ProtectedServicer:` key deliberately is not. So the most privileged grant in the app
  defaulted to its widest audience.
  **Worse than the review stated, and checking rather than accepting is what found it:** the review
  located it at the admin page pre-populating the editor. `ProtectedPrincipalServicerService.Evaluate`
  reads the same method, so the bypass was live on an unconfigured store with **no admin
  involvement at all** and no stored row to find afterwards. The page-only remedy it also offered
  would have left the real hole open. Fixed in the service: any key under the servicer prefix is
  fail-closed by construction, prefix-matched because the keys are built per module at runtime.
  **What makes this worth recording: the commit had already been reviewed clean as a PLAN by grok,
  was written against a plan that named the storage hazard explicitly, and shipped with 8 passing
  guards.** The defect was in none of that - it was a pre-existing fallback in a file the diff never
  touched, reachable only because the new key was not the KIND of thing the existing fail-closed set
  knew about. A plan review cannot see that, and no test suite that never runs against an
  unconfigured store can either.
  **NEXT: deploy, then the manual checks.** Load-bearing: save a module's ordinary access and
  confirm a configured servicer grant SURVIVES (the whole-store-replace hazard, directly), and a
  servicer-group member actually unblocking a protected sender - the only end-to-end proof the
  capability does anything at all.

- **Message Analysis: the 90-day search reached dev/prod BROKEN in `2.6.0` and is repaired in
  `4b976e9`. MessageTrace `1.3.1 -> 1.4.0`. NOT DEPLOYED; 7 manual checks unrun.**
  `docs/MessageTraceHistoricalRetirement-Plan.md` Status: Implemented.
  **The owner found it by using the app.** Any range wider than 9 days still went to
  `Start-HistoricalSearch` and told the operator results would be emailed, while the chunked
  in-app search built in `03a9999` sat unused in the same build. And the emailed report is one
  those operators often cannot open: `Get-HistoricalSearch` returns only a portal `FileUrl`
  needing an interactive sign-in, which is the barrier the work existed to remove.
  **How it survived three passes: `72b8047` deleted the page branch correctly but rested on a
  false premise; `90486d2` reverted it WHOLE, taking the correct deletion with the bad premise;
  `03a9999` restored only the service half.** A revert that undoes two things and a repair that
  redoes one is not a shape diff review catches. The 1.4.0 version bump was lost the same way,
  so the catalog understated this module through the whole work stream.
  **1483 tests passed against the defect.** No bUnit harness exists, so no test can see which
  branch a Razor handler takes; the planner and chunking tests were all green and all irrelevant.
  `MessageTracePageRoutingTests` is the answer - a source-level tripwire, explicitly not
  behavioural coverage, because a reintroduced branch is exactly what a tripwire can see.
  **Its first cut was itself too weak and the probe caught it:** the day-count guard was scoped to
  `RunTrace`'s body, but the defect declared the comparison as a FIELD, so it passed with the
  defect reinstated. Now scoped to the whole page.
  **Rule this earns, and it generalises past this module: a plan marked Implemented on a green
  suite, in a repo where no test can render the page, states more than the evidence supports.**
  `docs/MessageTraceAccuracy-Plan.md` said exactly that in its own caveat and was still marked
  Implemented; it is now corrected in place.
  **NEXT: deploy and run the 7 manual checks.** Load-bearing: a 30-day search renders rows in the
  page with no email promise, and no gap or duplicate row at a chunk boundary - the second is
  invisible to every test, because the rows either side of a missing window look continuous.

- **BitLocker Recovery module INTEGRATED 2026-08-07. New module `BitLockerRecovery 1.0.0`, no base
  app bump (Constitution: adding a module does not bump the base version). NOT DEPLOYED; 12 manual
  checks unrun.** `docs/BitLockerRecoveryModule-Plan.md` Status: Implemented.
  Module count 24 -> 25, configurable policy aliases 33 -> 34. **1471 passed / 0 failed / 3 skipped**
  (the 3 skips are the pre-existing AD-unavailable tests), build/format/`git diff --check`/ASCII all
  clean. Guard proven non-vacuous: removing the descriptor fails 3 catalog tests.
  **Authored outside this repo** as an isolated package at
  `D:\source\scripts\BitLocker\ExchangeAdminWebModule` (it lives beside the `Export-BitLockerKey.ps1`
  task that writes the archive it reads). The package is the upstream; the host now carries a copy.
  **Read-only module, so no ticket, no protected-principal check, no confirmation dialog** -- those
  guard writes and this performs none. `FailClosed: true` and disabled by default anyway, because a
  recovery key decrypts a whole disk. **The REVEAL is the audited security event, not the search.**
  **Three defects were caught by review before integration, and the pattern is worth keeping: the
  package validator passed clean at every step while all three were live.** It is a shape checker,
  not a compiler.
  (1) A per-computer `PowerShell.Create()` inside the result loop, each re-importing the AD module --
  51 runspaces for a default search, 501 at the cap; now one `InitialSessionState` runspace per
  search, matching `ADDirectorySearchService`.
  (2) A failed live-AD lookup discarded successful ARCHIVE rows and returned a bare failure -- on a
  recovery call that is the worst possible direction to fail. Now returns the rows with a warning,
  the `MessageTraceResponse.IsPartial` shape.
  (3) `ExecutionPolicy` unqualified -- **the module did not compile at all**, found only by building
  it against the host DLL out-of-tree. `Microsoft.PowerShell.ExecutionPolicy` is the enum's real
  home, which is why all six existing host call sites write it fully qualified.
  A fourth (unguarded `Directory.Delete` in test teardown, failing 29 of 30 tests on held SQLite
  handles) surfaced in the same out-of-tree run; the host's own tests all wrap this in try/catch.
  **`codereview` generation pass over `81fd069..e39e18f` returned 2 findings, both real, both
  fixed** -- `blr-1` (HIGH, `53f3ac5`) and `blr-2` (MEDIUM, `61552d9`); see
  `.agents/review/findings/blr-*.md`.
  **blr-1 is the load-bearing lesson and it is the same shape as ppv-1 and sid-1: the guard was
  built correctly and the reasoning about what it covered was wrong.** The module took real care
  to keep a recovery key out of the audit record on the REVEAL path -- and then wrote one there
  from the SEARCH path, because the page audited the raw contents of a box that is *documented to
  accept a pasted 48-digit recovery key*. So the leak sat on the happy path, in a durable store
  readable by more people than may reveal a key, and it never tripped the `RevealRecoveryKey`
  event that exists to record exactly that disclosure. Three earlier review rounds over this same
  code missed it, including two of my own.
  blr-2: a live search that hit its result cap before finding a key rendered "searched
  successfully" -- the module's own fail-closed rule ("no key exists" and "I could not look" must
  not look alike) violated by the page, while both services reported truncation correctly.
  **DEPLOYED TO DEV by the owner 2026-08-07 and exercised. One defect found that way: `blr-3`,
  fixed.** Archive search, live AD fallback, and reveal all work; the archive holds 124,942 rows.
  **`blr-3` (HIGH): a SECOND search rendered "No recovery keys found ... searched successfully"
  for the whole time the live AD query was in flight**, then replaced it with the keys it had just
  said did not exist. `SearchAsync` emptied `results` but never cleared `searched`, so the
  zero-results branch rendered over emptied data. Live AD takes seconds - long enough for an
  operator on a recovery call to read a definitive "no key on file" and act on it.
  **The first search after a page load was always fine**, which is why it survived: the obvious
  manual test passes. Only the owner's second-search sequence exposed it.
  **Third instance of one pattern, and it is now the thing to watch in this repo: the service was
  correct and the PAGE was wrong** (`blr-2`, the MessageTrace historical branch, now `blr-3`). The
  page is the only part no test can see - no bUnit harness - so service correctness keeps proving
  nothing about what an operator gets. Source-level assertions are the only automation that
  reaches it.
  **A near-miss in the fix's own proof, worth remembering:** the first non-vacuity probe used
  `\r\n`-suffixed replacements that silently matched nothing, so 1 of 3 guards fired and the other
  two looked weak. Verifying the file contents AFTER the revert - rather than trusting the
  reverting script - showed the revert had not applied. **A non-vacuity probe that does not
  confirm its own revert landed can manufacture a false verdict in either direction.**
  **`blr-4` (MEDIUM) was CAUSED BY the blr-3 fix and found on PROD.** Suppressing the stale result
  left the results area blank for the seconds a live AD query takes, so the page read as hung.
  **Removing a wrong answer is only half a fix** - a blank region is not neutral, and an operator
  who thinks the app froze reloads mid-search. Fixed with an in-flight indicator, plus a forced
  render and `Task.Yield` before the work: `Microsoft.Data.Sqlite`'s `*Async` methods complete
  SYNCHRONOUSLY, so the handler can finish the whole archive query without ever yielding to the
  renderer, and an indicator that is never painted is not an indicator.
  **Two of blr-4's three guards were false coverage on the first cut**, and only the mutation probe
  exposed it: they matched `@if (isSearching)` on the Search button's own spinner and message text
  still sitting inside the disabled block, so both passed against the broken page. **A guard that a
  broken page satisfies is worse than no guard, because it reads as coverage.** Now anchored to the
  markup each condition gates. Second time in two days a probe caught a weak proof (see the `\r\n`
  revert that silently matched nothing, above) - **the probe is doing more work than the tests.**
  Module `1.0.0 -> 1.0.1` for blr-1/2/3: they are behaviour changes landing after the module first
  reached dev, so the version must distinguish the two builds.
  **The isolated package at `D:\source\scripts\BitLocker\ExchangeAdminWebModule` is now STALE.**
  The host copies of `BitLockerRecovery.razor` and `BitLockerRecoveryTests.cs` carry blr-1/2/3 and
  the package does not (the three service files are still identical). **The host is authoritative
  from here** - a future re-copy from that package would silently revert three fixes, one of them
  a cleartext-key leak into the audit log.
  **NEXT: the remaining manual checks.** Unrun: an archive-only (deleted-from-AD) machine shows
  `Archive only`; a broken archive path errors rather than showing an empty table; search by
  pasted 48-digit key redacts in the audit record (`blr-1`).

- **HANDOFF 2026-08-05, as of `e9ad05d`. Tree clean, CI green.**
  **Coverage 64.7% -> 66.0%** over the gated security-critical scope; the floor was raised with it
  (`b5df487`) -- the value itself lives in `.agents/review/coverage-floor.txt`, which owns it.
  CI went green at `fd8fa69` -- first success since 2026-07-30 -- after two unrelated
  defects were fixed: the host-dependent `"DA"` test (`506c2d4`) and the coverage dilution
  (`8d614b4`, `4daf5d9`).

- **CI WAS GREEN as of `fd8fa69`** (2026-08-05, run 31021097853) -- first success since
  2026-07-30. Both jobs passed. CI's own figures at that commit: `1321 passed, 0 failed,
  9 skipped`, coverage `65.1% (844 / 1296)`, floor satisfied. Two separate defects had to be fixed
  to get there: the host-dependent `"DA"` test (`506c2d4`) and the coverage dilution.
  **Volatile -- three commits have landed since (`a224c9a`, `03f7e31`, `be7c02a`, all docs and
  governance). Check the current run rather than trusting this line.**

- **Button states themed 2026-08-04, app `2.5.5`, NOT DEPLOYED.** `2.5.4` reached dev and the
  owner still saw blue buttons, with screenshots. **`2.5.4` was not wrong, it was incomplete** --
  and the screenshots carried the diagnosis: same Gruvbox page, checkboxes orange but submit
  button blue; and on M365 Group Management an *enabled* `btn-primary` rendered orange while
  *disabled* ones rendered blue. **Every blue button in every screenshot was DISABLED** (empty
  forms).
  Cause: Bootstrap 5.0 hardcodes `.btn-primary{background-color:#0d6efd}` instead of reading
  `--bs-primary`, and states its variants on **two-class selectors**
  (`.btn-primary.disabled, .btn-primary:disabled`, `:hover`, `:focus`, `:active`), which outrank a
  single-class override. So `2.5.4`'s rule won for a resting button and lost for every other
  state. Fixed by stating each state per variant at matching specificity; `!important` deliberately
  avoided.
  **Rule this earns: "the rule exists and reads a token" is not the same question as "the rule
  wins."** Specificity is invisible to every check that only greps for token usage -- which is
  what let this ship twice. `EveryBootstrapColourVariantOverridesItsDisabledState` and
  `NoBootstrapBrandColourIsHardcodedInOurStylesheet` now cover it.
  **Also worth keeping: a screenshot of a DISABLED control is not evidence about the enabled one,
  and vice versa.** The inconsistency in those images was the clue, not noise.

- **Export retention + admin bulk jobs view -- LANDED 2026-08-04, app `2.5.2`, new module
  `AdminBulkJobs 1.0.0`. NOT DEPLOYED.** `docs/AdminBulkJobs-Plan.md` Status: Implemented;
  **9 manual checks unrun.**
  **Version correction (owner caught it 2026-08-04).** These landed with NO base bump, leaving the
  repo reading `2.5.1` while dev and prod were already running a *different* `2.5.1` -- verified
  from both assemblies (`FileVersion 2.5.1.0`, dev written 19:13, prod 18:39). The new module
  correctly bumps nothing (Constitution: a new module does not bump the base version), but moving
  export retention in-process is shared app-wide startup behaviour and ships real behaviour change
  -- the app now deletes files it never deleted before -- so it earns a base bump. Now `2.5.2`.
  **Rule this cost: two builds carrying one version number is worse than a wrong number**, because
  during an incident nothing distinguishes them. Check the deployed assembly before assuming the
  repo is ahead.
  **DURABLE RULING, owner 2026-08-04: "there are and will be no scheduled tasks."** This supersedes
  `docs/MessageTraceDownloadLink-Plan.md` D1 and the fallback suggestion at
  `docs/FutureModules-Plan.md:308`; both are annotated in place. Unattended work in this app is a
  one-shot call in the `Program.cs` startup pass -- never a timer, never a hosted worker, never an
  external task.
  **What prompted it: export retention was documented for months and never performed.** Measured,
  not inferred: `schtasks /query` on this host returned **266 tasks, none belonging to this app**,
  while `MessageTraceExportStore` stated as fact that one deleted exports older than 30 days,
  `README` repeated it to operators, and **openreview F4 rejected a configurable retention key
  specifically to avoid disagreeing with that task.** Nothing enforced the window: exports
  accumulate forever, and past day 30 the reports page shows **Expired for a file still on disk** --
  wrong in the direction that matters. Exposure small by luck: 2 files, 0.6 KB each, 6 days old.
  **First fix was wrong and was withdrawn.** I shipped two PowerShell scripts plus a task
  installer, which reproduced the missing-external-dependency shape rather than removing it -- an
  install step someone must remember on every host forever, whose absence is invisible. The owner
  ruled it out; the scripts and their Pester file were deleted in the same commit that added
  `MessageTraceExportStore.PruneExpired`, called at startup beside the existing job-record prune.
  Records and the files they describe now expire by one mechanism.
  **The narrow scope is load-bearing: the export directory sits INSIDE the audit log root**, so the
  sweep matches an anchored filename pattern, is non-recursive, and uses an exclusive cutoff; most
  of the 11 tests assert what SURVIVES. It never throws -- retention must not be able to stop the
  app booting. The 30-day constant stays a constant, which reinstates F4's reasoning rather than
  overturning it.
  **`AdminBulkJobs` closes the gap the Conference Rooms scoping fix opened.** After that fix a
  *running* Message Analysis export was visible nowhere (`/message-analysis/reports` lists only
  terminal exports) and `GetActiveJobs()`/`GetRecentJobs()` had no caller. The new page at
  `/admin-bulk-jobs` is the legitimate home for both, with a **Module column** -- the column whose
  absence let a MessageTrace row pass as a Conference Rooms job. **`FailClosed: true`**, because
  aggregating every module's submitters, tickets and targets is exactly what the section-access
  boundary exists to prevent leaking. Cancel and Remove here are deliberately NOT module-scoped:
  crossing modules is the page's purpose, and both audit.
  **No base app bump, per the Constitution** ("Adding a new module does not bump the base app
  version"). A `2.5.2` bump was made and reverted on reading that rule.
  **ON DEV as of `2.5.3`. Checks 2 and 3 PASS on real data**, observed after the 22:24 restart:
  both real exports (6 days old, inside the window) survive, and all 87 `.jsonl` audit logs in the
  parent directory are intact -- that second one is the deletion the anchored pattern exists to
  prevent, now proven against a real audit tree rather than a temp fixture.
  **The deleting path has NOT run in production conditions:** no export on either instance has
  reached 30 days yet, so only the guards are proven live. Checks 4-9 (the admin page) unrun.

- **Conference Rooms bulk jobs panel -- ALL 6 SLICES LANDED 2026-08-04, app `2.5.1`,
  ConferenceRooms `2.3.2`, NOT DEPLOYED.** `docs/ConferenceRoomsBulkJobPanel-Plan.md` Status:
  Implemented; **10 manual checks unrun** (the panel is markup, so they are the only evidence).
  Reported as "an old test job with no way to do anything about it".
  **The reported row was not a Conference Rooms job.** Identified against the live dev jobs
  database, not inferred: it is a `MessageTrace_DetailExport` from 2026-07-29, the only row in
  `bulk_job` on that instance. It appeared because of two defects that hid each other -- the
  panel read `GetActiveJobs()`/`GetRecentJobs()`, both **unfiltered across every module**, and
  `JobKindLabel` was a two-way ternary that rendered anything not-Finder as "Room Type (bulk)".
  Had the label been honest the leak would have been obvious on sight.
  **The severe half was never reported and was found by reading the code:** the panel renders a
  Cancel button per active row and `CancelJob` takes only an id, so **a Conference Rooms operator
  could cancel a running Message Analysis export.** Submitter and ticket of another module's work
  also crossed a section-access boundary.
  **Owner rulings.** Jobs moved to their own tab (`"move the jobs to another tab. out of the main
  UI, because it's going to push the actual module down further and further"`) -- which withdrew
  the plan's D1 retirement gate entirely, since the shared-dismiss objection that made it hard to
  rule stops mattering once the panel is off the working surface. Plus a per-job Remove: hard
  delete, terminal-only (enforced in SQL -- deleting an active job would leave the runner holding
  a token for a missing row), module-scoped, and **audited**, which is what makes a hard delete
  acceptable since the audit log is a separate store.
  **Retention stayed at 30 days after a conflict was raised rather than implemented.** The owner
  asked for 90; `PruneFinishedBefore` DELETES terminal rows at 30 and the store is shared with
  Message Analysis, so a 90-day panel would show nothing between day 31 and 90. Owner ruled 30.
  **NEXT: deploy `2.5.1` to dev and run the 10 manual checks.** Check 1 (the reported row is gone)
  and check 6 (no Cancel offered for a running foreign job) are the load-bearing ones.

- **Theme support -- ALL 5 SLICES LANDED 2026-08-04, app `2.5.0`, NOT DEPLOYED.**
  `docs/ThemeSupport-Plan.md` Status: Approved (owner directive: *"add theme support properly.
  include 8-10 most popular themes in a dropdown selector that replaces the dark/light icon"*).
  Ten themes -- Light, OLED Black, Solarized Light/Dark, Dracula, Nord, Gruvbox Dark, Monokai,
  One Dark, Tokyo Night -- in a grouped `<select>` replacing the sun/moon toggle.
  **The bulk of the work was not the themes.** 61 rules in `app.css` keyed off the `dark` class
  rather than reading a token, so a third theme would have rendered light-mode cards, form fields
  and tables on a dark canvas. Slice 1 converted all of them; a theme is now pure data and the
  eleventh is a copy-paste. 12 tint tokens added so the coloured alerts and table rows stop being
  the hardcoded exception. **The `dark` class survives for exactly one rule, `color-scheme`,**
  which is not styling -- it is how the browser is told to paint scrollbars, native select popups
  and date pickers, which no stylesheet can reach.
  **`UiThemeCssTests` is the load-bearing guard and the reason this is safe to extend:** a theme
  block missing a token silently inherits Light's value, so a dark theme missing `--ui-fg` renders
  near-black on near-black -- an invisible page, not a crash and not a compile error. It also
  forbids any rule naming a theme or keying off `dark`, scanning the isolation stylesheets too, so
  it doubles as the guard against the `app.css`/`NavMenu.razor.css` mirror trap.
  **A real bug was caught by re-reading the rendered script, not by a test:** the JS lookup tested
  its map value for truthiness, but the value IS the isDark flag -- so every LIGHT theme read as
  unknown and fell back to the default. Solarized Light would have been unselectable with nothing
  failing. Fixed to a membership test; a regression test pins the C# twin to the same rule.
  Legacy `localStorage` value `dark` migrates to OLED, so an existing user sees no change.
  **NEXT: deploy `2.5.0` to dev and run the plan's 6 manual checks.** Check 2 (scrollbars and
  native selects follow the theme) is the one automation cannot reach at all.

- **Admin UI redesign — ALL 6 SLICES LANDED 2026-08-04; `2.4.0` DEPLOYED TO DEV by the owner and
  SEEN. Two defects reported from that first look, both fixed in `2.4.1` (in repo, NOT deployed).**
  `docs/AdminUIRedesign-Plan.md` Status: In progress (manual checks unrun). Owner rejected the
  existing UI outright — "it does not look like a professional app, it looks like a vibe coded
  toy... the important security group entry system is half-baked" — after seven mockup rounds
  were rejected for keeping the same materials. What was finally approved is **structural, not
  cosmetic**: tabbed panes each with their own scroll, so the page never grows with the group
  count; grants as aligned table rows, never chips; one save bar per page naming the dirty
  section. The three approved mockups are kept in `docs/mockups/` (`q1-tabbed.html`,
  `q2-adminsettings.html`, `q3-picker.html`); 15 rejected drafts deleted so there is
  no ambiguity about which is current.
  **Owner ruling D1: app-wide**, so all 22 pages' chrome changed and all 22 need a smoke pass.
  **Slices 1-2** (`01a5efc`, `a934e07`): token layer driving both themes with Bootstrap's `--bs-*`
  pointed at it — that indirection is what lets the ~20 unconverted pages follow the theme
  without markup changes. OLED dark (`#000` canvas, silver text, cyan accent). Nav rows 3rem ->
  1.9rem so all 22 modules fit. **Nav icons were a real fix, not a restyle:** all 24 SVGs are
  hardcoded `fill='white'` and vanish on a light sidebar, so each is now a CSS mask taking
  `currentColor`.
  **Slices 4-5** (`708a375`): both admin pages rebuilt. Eight Save buttons -> two save bars.
  **Deliberately NOT built:** the diagnostics tab from mockup q2 — new capability, not a redesign
  of existing capability, and its OQ-2 (live probes vs cached) is undecided.
  **Two bugs fixed rather than restyled around:**
  **B1** (`4489730`, `1b77dd4`) the picker could not see WINROOT — `Get-ADGroup` was issued with
  no `-Server`, so only the joined domain was searched; now targets the forest global catalog.
  Two further defects surfaced *while verifying that fix*, both invisible to unit tests: reading
  `GlobalCatalogs` off the returned PSObject yields empty, producing the server string `":3268"`
  which `Get-ADGroup` ACCEPTS while quietly serving locally; and `ResolveGlobalCatalog` cached its
  own FAILURES, so one transient `Get-ADForest` error would have pinned the picker to
  local-domain-only for the process lifetime — silently undoing B1 in production.
  **B2** (`857be64`) the app had **no unsaved-changes guard anywhere** (verified: no
  `beforeunload`, `NavigationLock` or `OnLocationChanging` in any component). `AdminPageDirtyState`
  is an extracted service with 14 tests because page fields cannot be tested here.
  **Two process lessons recorded in the plan, both earned:** a flaky live test was chased rather
  than muted and turned out to be hiding the real caching defect above (I attributed it to two
  wrong causes first); and scripted line-range edits silently deleted two whole markup blocks
  while the project **still built clean**, because absent Razor markup is not a compile error —
  caught only by diffing against pre-edit backups.
  **Slice 7 — post-deploy corrections (`e442df4`, `31142d9`, `d360cfe`), app `2.4.1`.** Neither
  defect was findable off-host; both needed a running instance.
  **(a) Every module in the nav read as greyed out / disabled.** Slice 2 migrated only the
  CSS-isolation copy in `NavMenu.razor.css`. **`wwwroot/app.css` carries a deliberate MIRROR of
  the same nav rules** — present because isolation scoping has been unreliable on published IIS —
  and it was never touched, so it still had the painted-white icons and the old rails. Worse,
  `--sidebar-bg` was never a token at all: hardcoded `#2c3345` light / `#1a1d2e` dark. The pane
  stayed a dark slate slab while its labels took the light token set's grey. Both copies now
  resolve to `--ui-nav-bg`. **Rule: any nav/shell rule edited in `NavMenu.razor.css` must be
  edited in the `app.css` mirror in the same change** — a green build proves nothing about which
  copy the browser used.
  **(b) The protected-principal lists had no row delineation.** The slice-5 note justified leaving
  them alone because they were "already row-per-entry rather than chips" — which conflated
  row-per-entry with row-*delineated*. They were borderless lines in one bordered box. Now the
  same `.adm-tbl` as the Access grants table. **The lesson is the reasoning, not the CSS: "not the
  thing the owner rejected" is not the same as "good."**
  **NEXT: deploy `2.4.1` to dev (`.\tools\deploy-pipeline.ps1 -Dev`, ELEVATED) and run the plan's
  manual checks.** Checks 1-6 runnable; 7-8 exercise the rebuilt panes. None has been run.

- **Section-access groups stored as SIDs — ALL 4 SLICES LANDED + REVIEWED 2026-08-03, app
  `2.3.35`, NOT DEPLOYED (dev is `2.3.34`, prod `2.3.30`).**
  `docs/SectionAccessSidStorage-Plan.md` Status: Implemented. A `codereview` generation pass
  over `b872861..0a50d01` returned **2 findings, both real, both fixed** — `sid-1` (HIGH,
  `54e762d`) and `sid-2` (MEDIUM, `019b814`); see `.agents/review/findings/sid-*.md`.
  **sid-1 is the load-bearing lesson and it contradicted my own slice-3 commit message:** I
  wrote that an unmigrated store "fails CLOSED" under exact comparison. It does not.
  `WindowsPrincipal.IsInRole` resolves NAMES as well as SIDs — measured,
  `IsInRole("Domain Users")` is **true** — so until the fix a deferred or halted migration left
  name rows authorizing exactly as before, with the cross-domain ambiguity intact, during
  precisely the window the migration exists to survive. Non-SID values are now discarded at all
  three comparison sites (handler, checker, job snapshot). Same shape as ppv-1: the guards were
  sound, the reasoning about what they guaranteed was not.
  **Frontier pass DONE 2026-08-04** over the whole range including those fixes: **1 more HIGH
  finding, `sidf-1` (`4f1de2b`)** — and it was a defect **the sid-1 fix introduced**. That fix
  filtered non-SID values on EVERY requirement, including the static `Security:AdminGroups` from
  appsettings, which no migration converts and which is deployed here as
  `ANALOG\ExchangeWebAdmins` (verified against the live prod file, not the sample). Deploying
  `2.3.35` would have denied every admin `/admin-settings` — the page needed to repair
  section-access fallout, so the failure removed its own remedy. The filter is now scoped to
  `ResolveDynamically`, the flag that distinguishes the migrated store from appsettings.
  **Frontier tier resolved (owner ruling):** the old pin `gpt-5.6-sol` 404s; codex at its default
  model is the strongest available here, so frontier = standard pair with `grade: fallback`, and
  effort `max` is rejected by this gateway (xhigh is the ceiling). A future escalation must halt
  to the owner rather than redispatch. Recorded in `.agents/review/harnesses.local.json`.
  **Still open (sidf-1 Known gaps):** `Security:AllowedGroups`/`AdminGroups` remain name-based, so
  the cross-domain ambiguity is closed for module access and **still open for admin access**.
  Pre-existing and an explicit plan Non-Goal, not a regression — but it wants its own decision.
  **NEXT: the plan's 6 manual post-deploy checks on dev — none run.** Authorization cannot be
  proven off-host and a mistake locks people out of every module, so dev first. Check 6 (app
  boots and authorizes from stored SIDs with AD unreachable) and check 3 (`winroot\Enterprise
  Admins` still reaches DhcpAuthorization) are the load-bearing ones.

- **BOTH protected-principal work streams — CODE COMPLETE + REVIEWED 2026-07-31, ON DEV as
  `2.3.34` (deployed by the owner 2026-07-31, verified from the assembly). Manual checks NOT
  run — owner checking Monday. Prod is still `2.3.30` and carries every defect below.**
  A `codereview` generation pass over `10d1593..521bb6e` (both streams, 10 commits, ~2950
  lines) returned **4 findings, all real, all fixed** — see `.agents/review/index.md` rows
  `ppv-1..4` and `.agents/review/findings/ppv-*.md`. **ppv-1 was HIGH and is the load-bearing
  lesson: the Exchange-fallback work closed the alias bypass for on-prem principals and
  REINSTATED it for cloud-only ones**, because `ResolveWithExchangeFallbackAsync` branched on
  address equality instead of the `ExistsOnPrem` flag that same work had introduced and never
  read. Both streams had been reported complete with "every guard proven non-vacuous" before
  the review found it — the guards were sound, the gap was in what was thought to test.
  Fixes: `a6927b2` (ppv-1), `0940964` (ppv-2, a DN with an escaped comma was mangled by
  `DOMAIN\` stripping), `49b134d` (ppv-3, Save mid-validation dropped the pending entry),
  `9a43455` (ppv-4, live tests reported PASSED not SKIPPED with no directory).

- **Protected-principal admin input validation — CODE COMPLETE 2026-07-31, all 4 slices on
  `master`; on dev as `2.3.34`, NOT yet exercised through the real page.**
  `docs/ProtectedPrincipalInputValidation-Plan.md` Status: Implemented. Owner asked why
  an O365 group cannot be added to protected principals; investigation found the real defect is
  that `Components/Pages/AdminSettings.razor:394-397` saves **any** typed string — the
  `ADIdentityAutocomplete` on Users and Groups (`:127`, `:149`) only suggests, and the add
  handlers never check that the value came from a suggestion. OUs (`:171`) have no picker at all.
  An unresolvable **user** or **OU** row silently matches nothing; an unresolvable **group** row
  is worse in a different way — `CheckGroupMembershipAsync` (`Services/ProtectedPrincipalService.cs:629-633`)
  fails **closed**, so it turns every check into a denial that reads as a directory fault.
  **Owner rulings:** *(a)* cloud-only objects are **non-protected by design**, so refusing an
  Entra-only group is correct behavior and Graph is a non-goal; *(b)* **D1** AD-unreachable
  refuses the Add with "try again later" (admin-only page, and nothing works without AD anyway);
  *(c)* **D2** validation runs under the **app-pool identity, not the Delinea secret** — least
  privilege, recorded in `.agents/decisions.md` 2026-07-31 **with its environment-scope limit**.
  Key design constraint: `ADDirectorySearchService.Search` is fail-soft (returns `[]` on
  unavailable, throttle timeout, exception, and short term alike), so reusing it would report a
  correct entry as a typo during an outage — a new `ValidateExists` carries an explicit
  Found/NotFound/Unavailable outcome, and exact-match filters replace the autocomplete's wildcard
  (`jdoe` must not match `jdoe2`; same reasoning `FindUserBySid` already documents at `:110-123`).
  **Slice 1 DONE** (`4aa310e`): `ADDirectorySearchService.ValidateExists` with
  Found/NotFound/Unavailable. Exact-match filters mirroring what the protection engine resolves;
  `DOMAIN\` prefix stripped with a trailing-backslash guard (stripping there leaves an empty
  term, and an empty exact-match filter matches EVERY object). 28 tests.
  **Slice 2 DONE** (`67f2412`): the three add-handlers gate on the outcome. Decision logic lives
  in `Services/ProtectedPrincipalEntryValidator.cs`, not the page (no bUnit harness — same reason
  `MessageTraceExportListing` exists). An accepted entry is stored in the directory's **canonical
  form** (DN for groups/OUs, UPN then mail for users) so the saved rule matches what the engine
  resolves rather than depending on which format was typed — this was not in the plan, added
  during implementation. 20 tests.
  **Slice 3 DONE** (`3f9cec8`): OU picker. `Search` gains an OU branch that is deliberately NOT
  part of `Any`. Also removed three dead keydown handlers — unreachable before this work began.
  **Slice 4 DONE**: already-saved rows that AD says do not exist get a "not in AD" badge, swept
  from `OnAfterRenderAsync` so N lock-serialized lookups never delay first paint. Versions bumped
  here: app `2.3.33 -> 2.3.34`, `AdminSettings 1.0.1 -> 1.0.2`.
  **OQ-2 CLOSED:** `Get-ADOrganizationalUnit` works under the app-pool identity here (verified
  directly), so the OU picker was not dropped.
  **The inverted-rule pair is the subtle part of this work stream:** a failed lookup REFUSES a
  new entry but stays SILENT about an existing one. Both follow from "a directory that did not
  answer is not evidence about the object", yet they point opposite ways — badging every row
  during an outage would read as "your protection rules have been lost". A test pins them apart
  so a later refactor cannot collapse them into one helper.
  **NEXT: the plan's 10 manual checks** — none run. Checks 4 (an O365-only group is refused) and
  7 (AD unreachable gives the retry message, NOT not-found) are load-bearing.

- **Protected-principal resolution via Exchange — CODE COMPLETE 2026-07-30, all 4 slices on
  `master`; NOT deployed (dev is `2.3.32`, repo is now `2.3.33`). 6 manual checks unrun.**
  `docs/ProtectedPrincipalResolution-Plan.md` Status: Implemented. Triggered by an owner
  report of L1/L2 friction: Mailbox Permissions denies cloud-only mailboxes and mail-enabled
  groups with "Protected-principal identity resolution is unavailable", which reads as an
  outage but is an affirmative AD miss. Investigation found a second, unreported defect — the
  alias bypass recorded as GAP 4 under Blockers, which is *live* in ConferenceRooms and
  GroupManagement. Design routes an AD miss through the existing
  `ExchangeIdentityResolver.ResolveToObjectIdAsync` (`Services/ExchangeIdentityResolver.cs:10-31`,
  already registered `Program.cs:173`, unused by the protection path) so Exchange returns the
  canonical primary address; that closes the alias hole and makes groups and cloud-only
  mailboxes resolvable in one change. **No open owner gates.** D1 (fall back to EXO) ruled go;
  D3 ruled "anywhere it's broken it needs to be fixed" — all three gated modules in scope.
  D2 and D4 were withdrawn as decisions on owner challenge: EXO-down is unobservable as a
  policy choice (MailboxPermissions writes via the same pool, GroupManagement never touches
  EXO), and the cloud-only branch relaxes nothing because a cloud-only mailbox cannot be an
  on-prem group member in the first place (`Services/GroupManagementService.cs:246-247` throws
  before the write). Both reduced to consequences of D1; reasoning kept in the plan so they are
  not revived as questions.
  **Slice 1 DONE** (`6faa92d`): `ResolvedRecipient` + `IIdentityResolver.ResolveRecipientAsync`
  on `ExchangeIdentityResolver`. The load-bearing property is that `null` means Exchange
  affirmatively reported no such recipient and nothing else -- a lookup that could not run
  throws, because a caller may allow on a null. `IsRecipientNotFound` and `MapRecipient` are
  `internal static` so that boundary is testable without a live EXO session. 18 tests.
  **Slice 2 DONE** (`76bfead`): `ProtectedPrincipalService.ResolveWithExchangeFallbackAsync`.
  Only `NotFound` falls through to Exchange -- Resolved/Ambiguous/Unavailable return exactly as
  AD produced them, so nothing that denies today starts allowing. An Exchange lookup that could
  not run returns `Unavailable`, never `NotFound`. The service is a singleton and
  `IIdentityResolver` is scoped, so the fallback opens its own DI scope; the scope factory is an
  optional ctor param (nine test files construct the service directly) and a null factory fails
  closed. 13 tests.
  **Slice 3 DONE** (`eb30786`): the cloud-only branch returns a Resolved principal with a **null
  DN** -- that is the point, since group/OU/pattern rules read an on-prem DN a cloud-only object
  cannot have. Those rules are inapplicable, not skipped, and the branch logs which ones were
  not evaluated because both degrade silently (`:582`, `:682`). Constitution edit landed here.
  **Slice 4 DONE** (`0eca01e`): all three gates switched; four distinct messages replace the
  blanket denial. Versions bumped: app `2.3.32 -> 2.3.33`, `MailboxPermissions 1.0.3 -> 1.0.4`,
  `ConferenceRooms 2.3.0 -> 2.3.1`, `GroupManagement 2.1.0 -> 2.1.1`. Both ConferenceRooms test
  fakes scripted `ResolveWithStatusAsync`, which the gates no longer call -- the full suite
  caught that as 4 failures; their overrides moved to the seam actually used.
  **974 tests green**, build/format/ASCII/`git diff --check` clean. Non-vacuity proven per guard
  by reverting each: Exchange-throw-is-Unavailable 1 failure, fall-through-on-any-status 2,
  alias re-resolution 3, fabricated cloud-only DN 1, dropped cloud-only address 2, blanket
  denial restored 2, ConferenceRooms gate back to AD-only 7, GroupManagement the same 2.
  **NEXT: the plan's 6 manual post-deploy checks on dev** -- none run. Check 3 is the
  load-bearing one (an alias as target must be **denied** citing the CEO user rule); it is the
  GAP 4 regression test and must be re-run on prod after promotion. **No independent review**
  has been obtained for this work.

- **MessageTrace export delivery: reports page + notification link — CODE COMPLETE 2026-07-29,
  ON DEV as `2.3.31`, not on prod.** Plan `docs/MessageTraceDownloadLink-Plan.md` Status:
  Implemented. Replaces the emailed zip attachment with a Downloadable Reports page inside the
  app; the email carries a link to that page, so an arbitrary notification recipient is safe (the
  data never leaves the login gate) and admins leave the trace-data path. Supersedes
  `docs/MessageTraceDetail-Plan.md` decisions 5 + 6. Owner rulings recorded in the plan:
  **D1** retention is out-of-process (a host scheduled task deletes exports older than 30 days;
  the app never deletes and must render a missing file as "expired"); **D2** the gate is the
  existing `MessageTrace` module policy with no per-user ownership check, and the ticket number is
  an audit prompt only, never an authorization control; **D3** delivery is a Razor page reusing the
  existing base64 + `downloadFile` JS blob mechanism — **no HTTP endpoint** (owner rejected the
  first draft's minimal-API premise; routing through a page also makes the ticket prompt real
  rather than an empty `?ticket=` in an emailed URL); **D4** (ruled 2026-07-29) the recipient box
  is pre-filled with the operator's own address, editable and clearable -- a default, not a floor,
  and never a required field. Base app `2.3.30 -> 2.3.31` + MessageTrace `1.2.1 -> 1.3.0`.
  All four decisions ruled, no open owner gates, plan approved by the owner 2026-07-29.
  **Slice 1 DONE** (`b007ad5`): `MessageTraceExportStore` -- export-path resolver, GUID-"N" jobId
  whitelist, traversal guard, pinned 30-day constant; 19 tests.
  **Slice 2 DONE** (`87941b0`): `MessageTraceExportListing` (page logic as a testable service --
  the repo has no bUnit harness), `Components/Pages/MessageTraceReports.razor` at
  `/message-analysis/reports`, and `GetFinishedByType` on `BulkJobRepository`/`BulkJobService`.
  Build/format/ASCII clean, 875 tests green; non-vacuity proven per guard by reverting each
  (ticket check 3 failures, Failed-vs-Expired 2, unfiltered limit 1, malformed payload 1).
  One fact recorded in code at the point of use: `<ModuleVersion />` resolves the descriptor by
  route and so renders nothing on the sub-route; kept per `docs/AdminModuleSpec.md` (PAGE009)
  rather than hand-rolling the lookup.
  **Slice 3 DONE** (`e4d2497`): the completion mail now carries a link to the reports page instead
  of the export. `EmailService.SendMessageTraceResultAsync` (no attachment, states the retention
  expiry and the ticket prompt) + new `SendMessageTraceFailureAsync`; `NormalizeRecipients`
  replaces `ResolveMessageTraceRecipients` and **adds nothing**, so the configured admin address is
  never merged into a trace-export recipient set (owner ruling). F3: `ResolveReportsUrl` returns
  null -- never a relative path -- when `Application:PublicBaseUrl` is unset or non-absolute, and
  the caller falls back to prose; it never fails the send. F1: the save result branches the
  notification and stamps the job record. New `BulkJobRepository.AppendMessage` /
  `BulkJobService.AppendJobMessage` -- additive, so a note can never erase a cancel/interrupt
  reason -- resolves the slice-2 inherited gap: `MessageTraceExportState.Failed` is now reachable
  in production. `Application:PublicBaseUrl` added to `Install-ExchangeAdminWeb.ps1` (blank
  default, environment-neutral), `deploy.ps1`, `promote-dev-to-prod.ps1` (empty leaves prod
  untouched), and `README.md`. Build/format/ASCII clean, **897 xUnit + 65 Pester green**;
  PSScriptAnalyzer adds one finding per touched script, each in a category already dominant there,
  none at Error severity. Non-vacuity proven per guard by reverting each: save-failure branch 4
  failures, no-relative-link rule 2, admin exclusion 6.
  **Slice 4 DONE** (`2f0b99c`): removed the two strings the redesign made false
  (`MessageTrace.razor` "never a typed-in address" + the zipped-report banner) and
  `DestinationDisplay()`, which merged the admin address into a display of who gets trace data;
  the page's `IConfiguration` injection went with it. Added the D4(a) recipient box -- pre-filled
  with the operator's claim address, freely editable, **valid when cleared**, no required-field
  validation -- backed by `EmailService.ParseRecipientInput` (comma/semicolon split, format-only
  validation accepting exactly what `NormalizeRecipients` keeps, no domain allow-listing). New
  `MessageTraceDetailJobPayload.Recipients` carries the set; **null** (a job enqueued before the
  box existed) falls back to the submitter, an **empty list** deliberately does not.
  `DownloadSelectedDetails()` untouched, so the reports page still lists emailed/bulk exports
  only. Versions bumped here: base app `2.3.30 -> 2.3.31`, MessageTrace `1.2.1 -> 1.3.0`.
  919 tests green; non-vacuity proven (cleared-box rule 1 failure, format validation 8).
  Follow-up `86f55ce`: corrected the `SaveFailedMarker` comment that still claimed nothing wrote
  the marker.
  **DEPLOYED TO DEV as `2.3.31` (owner, 2026-07-29).** Not on prod (prod stays `2.3.30`).
  **NEXT: run the plan's 9 manual post-deploy checks on dev.** None of them has been run.
  Highest-value ones: an 11+ message export arrives as a link with no attachment; the ticket
  prompt gates the download; an unwritable export dir yields the **failure** notice and a
  **Failed** row (not Expired) with the job still completing; with `Application:PublicBaseUrl`
  unset the mail contains prose and no bare `/message-analysis/reports`. The dev deploy also
  carried the MessageTrace NRE fix (`68bfd25`), so re-running the search that failed in prod is
  worth folding into the same session -- still prod-unfixed until `2.3.31` is promoted.
  **Independently reviewed 2026-07-29** (openreview, codex-commercial / gpt-5.6-sol / max, range
  `68bfd25..1e98eaf`): verdict **findings** (4), all repaired in the plan; record at
  `.agents/review/findings/mt-export-delivery-plan.md`. The one that mattered (F1, HIGH): removing
  the mail attachment makes the saved file the sole delivery, so `SaveToLogPath`'s swallowed catch
  would turn a disk-full failure into a "ready" email plus a row the page mislabels as Expired.
  The plan now branches the notification on the save result and separates **Failed** from
  **Expired**. Also: a non-blank ticket is now required at download (F2); an unset
  `Application:PublicBaseUrl` omits the hyperlink instead of emitting an email-unresolvable
  relative path, and the key is added to both config writers (F3); the global
  `Export:RetentionDays` key is dropped for a pinned 30-day constant -- it broke the
  Constitution's module-config rule and created a second retention truth that could drift from
  the host scheduled task (F4). Plus F5, coder-raised: the D3 rewrite had dropped the
  Constitution-conflict record about writing state into an externally-pruned directory; restored.
  **Non-blocking assumptions the owner may reverse:** OQ-3 (required ticket is this plan's reading
  of "requiring", not an explicit ruling) and OQ-4 (the Failed state is scope the owner did not
  request).
  **Deploy caveat:** `Application:PublicBaseUrl` is written only on a fresh install or with
  `-Force`, so an upgrade leaves the key absent unless it is set by hand on the host. Absent is a
  supported state -- the mail then carries prose and no hyperlink (that is manual check 9), not a
  broken relative link.

- **Operator email resolution from AD -- CODE COMPLETE 2026-07-29, ON DEV as `2.3.32`, not on
  prod. 8 manual checks not yet run.**
  `docs/OperatorEmailResolution-Plan.md` Status: Approved (owner, 2026-07-29). Owner reported on dev running `2.3.31`
  (2026-07-29, confirmed) that the new recipient box does not pre-fill. Pre-fill *is* the design
  (D4(a) of the download-link plan), so this is a bug. Root cause at
  `Components/Pages/MessageTrace.razor:610-613`: `userEmail` is read from `ClaimTypes.Email` /
  `email` / `ClaimTypes.Upn`, but the app is **Negotiate-only** (`Program.cs:38-39`) and a
  Kerberos/NTLM token carries the account name and group SIDs only -- none of those three claims
  exist, so `userEmail` is `""` on every request. Pre-existing since `e62ae73` (the page's
  original commit), **not** a slice-4 regression. Second broken consumer: historical search
  (`:322`, `:709-714`) hard-refuses with "Your authenticated email address is required...".
  Owner rulings in the plan: **D1** fix at the source by looking the address up in AD, not by
  patching the one box ("2nd"); **D2** read `mail`, fall back to `userPrincipalName` ("UPN is the
  same as email here") -- synthesizing `sam@domain` is explicitly rejected as a silent
  wrong-address failure; **D3** when the lookup yields nothing historical search keeps refusing,
  only the message changes to name the real cause ("if AD is unreachable, the whole app stops
  being relevant") -- no typed-address escape hatch. Design: new fail-soft singleton
  `Services/OperatorEmailResolver.cs` reusing `ADDirectorySearchService`'s pooled runspace, with
  an **exact case-insensitive `SamAccountName` filter** and `null` on 0 or 2+ matches --
  `Search` is a wildcard substring autocomplete query, so `jdoe` also returns `jdoe2` and taking
  the first row would eventually mail trace data to the wrong person; that is the plan's
  highest-value test. Planned bumps: app `2.3.31 -> 2.3.32`, MessageTrace `1.3.0 -> 1.3.1`.
  **Historical search has never been used** (owner) -- which is why a permanently blocking guard
  survived unreported -- but the owner ruled **keep it** (plan OQ-3, CLOSED): it is L2's only
  route to a beyond-realtime search without escalating to L3-4, so removal was considered and
  rejected and must not be re-raised. Consequence (plan OQ-2): everything past `:710` is
  unproven, not regressed; manual check 5 does not gate this plan, but **its outcome must be
  recorded here either way**, and a failure earns its own follow-up plan rather than being
  absorbed into this one. Plan commits `b7b9c94`, `3962904`, `186d4e0`.
  **Independently reviewed 2026-07-29** (openreview, codex-commercial / gpt-5.6-sol / max,
  range `64b211a..ace6230`): verdict **findings** (3), all accepted; record at
  `.agents/review/findings/operator-email-resolution-plan.md`. **F1 (HIGH) replaced the
  plan's central mechanism:** resolve by the authenticated **primary SID**
  (`ClaimTypes.PrimarySid`, which Negotiate does populate --
  `Components/Pages/SelfServiceGroups.razor:325`) through a bound `Get-ADUser -Identity`,
  mirroring `SelfServiceGroupService.ResolveCallerDn`
  (`Services/SelfServiceGroups/SelfServiceGroupService.cs:672-685`, whose doc comment
  already rejects samAccountName as an identity form by name). The samAccountName-through-
  autocomplete design fails four ways an exact-match post-filter cannot close, the worst
  being a cross-trust name collision returning exactly one confidently-wrong row. **F2
  (MEDIUM):** the resolver is `ResolveAsync` doing its work under `Task.Run` -- the shared
  AD lock can block 30s (`ADDirectorySearchService.cs:80`) and would freeze the circuit;
  a late result fills the recipient box only while it is still untouched. **F3 (LOW):** the
  deployed-version contradiction, flagged in the version block below rather than guessed.
  Also confirmed: `ADDirectorySearchService` is `sealed`, so the narrow test interface is
  required, not optional.
  **Slice 1 DONE** (`8594813`): `IOperatorDirectory` (one-member seam, needed because the AD
  service is sealed), `ADDirectorySearchService.FindUserBySid` (bound `Get-ADUser -Identity <sid>`
  on the existing pooled runspace, fail-soft, SID never logged -- it identifies the operator and
  the call runs on every page load), and `OperatorEmailResolver` (SID-format gate before any
  directory call, `mail` then UPN, `Task.Run`). 16 tests; non-vacuity proven per guard by
  reverting each: SID gate 8 failures, UPN fallback 3, SID pass-through 5, fail-soft catch 1.
  **Slice 2 DONE** (`928dd0a`): `MessageTrace.razor` reads `ClaimTypes.PrimarySid` and defers the
  lookup to `OnAfterRenderAsync`, so the AD service's 30s throttle lock never sits on the render
  path. The resolve is cached as a **`Task`, not a result**, so a Search click racing the deferred
  first resolve awaits the same AD call instead of issuing a second behind that lock. A
  `recipientTouched` flag (the box moved from `@bind` to explicit `value`/`@oninput` to observe
  first input) stops the late result overwriting a typed address. The historical-search guard
  awaits the same task rather than reading a possibly-unpopulated field, and its refusal now names
  Active Directory instead of blaming the operator's account (D3); guard structure unchanged.
  **Versions DONE** (`14f4ef1`): app `2.3.31 -> 2.3.32`, MessageTrace `1.3.0 -> 1.3.1`.
  Build/format/ASCII/`git diff --check` clean, **935 tests green**.
  **Implementation openreview ATTEMPTED TWICE, NOT OBTAINED (2026-07-29).** Both dispatches to
  `codex-commercial` (MCP) / `gpt-5.6-sol` / high over `55ec9af..14f4ef1` died on a 1800s silent
  transport abort, and the `codex` CLI fallback fails auth at the gateway (`Failed to refresh
  token` + missing `x-portkey-*` header). Per the playbook's fail-closed rule a missing envelope
  is not a clean pass, so **this code carries no independent review** -- re-dispatch when the
  harness is healthy, or the owner adjudicates shipping without it. Record:
  `.agents/review/findings/operator-email-resolution-plan.md`.
  **NEXT: the plan's 8 manual post-deploy checks** -- none run; page behavior is not
  unit-testable (no bUnit harness), so they are the only evidence this works. Check 7 is the
  load-bearing one: it confirms `ClaimTypes.PrimarySid` is actually populated on this deployment.
  Check 5 (historical search accepted rather than refused) is **exploratory, not a gate** (OQ-2)
  -- but its outcome must be recorded here either way.

- **MessageTrace per-message delivery-detail (MT-detail) — DONE, landed 2026-07-27.**
  Plan `docs/MessageTraceDetail-Plan.md` Status: Implemented; all 9 slices on `master`,
  each codex-reviewed accepted. MessageTrace module 1.1.1 -> 1.2.0, no base app bump.
  Full record archived: `docs/history/state-archive.md`. Live validation not yet performed
  (runs against real PROD EXO/on-prem AD, from the dev instance): detail drill-in, download/email/zip delivery,
  Blazor selection UI (enumerated in the plan).

- **GM-3 self-service group management (on-prem AD only) — task set DONE, landed 2026-07-27;
  two follow-ups landed 2026-07-28.**
  Plan `docs/SelfServiceGroupManagement-Plan.md` Status: Approved / on-prem only; all 6 tasks
  (plan section 7) complete and codex-reviewed. M365/delegated-Entra dropped 2026-07-22.
  Full record + the scope-narrowing and ACL-scan-drop history archived:
  `docs/history/state-archive.md`.
  - **2026-07-28 DACL-read fix (`28efc88`):** eligibility read was `Get-Acl AD:\<DN>`, which
    returned an empty `.Access` in the service runspace, fail-closed-excluding every group (page
    always showed "no groups"). Now reads `Get-ADGroup -Properties nTSecurityDescriptor`. Module
    1.1.0 -> 1.1.1.
  - **2026-07-28 member listing + AD picker (`a190664`,`fd16982`,`428a19e`,`80fe2a5`):** plan
    `docs/SelfServiceGroupsMemberListingAndPicker-Plan.md` (Approved 2026-07-28). Manage panel now
    lists current members with per-user Remove; member add box uses the shared
    `ADIdentityAutocomplete` (Option A: suggestions under app-pool identity, write stays isolated +
    re-validated — `.agents/decisions.md` 2026-07-28). Module 1.1.1 -> 1.2.0. Build/format/824
    tests green (17 new `GroupMemberClassifierTests`, non-vacuity proven).
  SelfServiceGroups module now at **1.2.0** (added at 1.0.0, no base app bump).
  Live validation not yet performed (runs against real PROD AD, from the dev instance): live AD
  add/remove, member list render + remove, audit write, admin + affected-user email, the Blazor
  page flow. Owner will deploy + test the DACL fix; a proper plan+review is the fallback if it
  does not resolve the "no groups" symptom.

### App version history

- **App version `2.5.5`** (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`, read there -- that file
  owns the number). `2.5.5` was the Bootstrap button-state fix, `2.5.4` the Bootstrap palette
  bridge + Dracula correction, `2.5.3` the theme palette rework.
  `2.5.2` was in-process export retention (shared app-wide startup behaviour, and it deletes files
  the app never deleted before). The `AdminBulkJobs` module landed in the same stretch and
  correctly bumps nothing -- Constitution: adding a module does not bump the base version.
  `2.5.1` was the Conference Rooms bulk jobs work (the repository/service reads are shared
  infrastructure); ConferenceRooms module `2.3.1 -> 2.3.2` with it. `2.5.0` was theme support --
  minor rather than patch, new user-visible capability app-wide. `2.4.1` was the two post-deploy
  UI corrections (`AdminSettings` module `1.1.0 -> 1.1.1` with it); `2.4.0` the app-wide admin UI
  redesign, bumped from `2.3.36`, which qualified group display names as `DOMAIN\Name`.
  Previously bumped from
  `2.3.34` for section-access SID storage (shared authorization path); `2.3.34` was
  protected-principal admin input validation; `2.3.33` (`0eca01e`) was
  Exchange-backed protected-principal resolution, `2.3.32` (`14f4ef1`)
  the operator-email resolver, `2.3.31` (`2f0b99c`) the MessageTrace
  export delivery redesign, `2.3.30` (`456e07c`) retired the `Security:ExcludedUsers` appsettings
  fallback and `2.3.29` (`3eac48a`) was the app-wide log-root fail-fast change.

### Superseded deployed-version records

  **Superseded: dev and prod both ran `2.8.0` from 2026-08-10 14:29:33 to 2026-08-11 11:13:20**
  (`favicon.ico` 21497 bytes on both, matching the repo). The paragraph below describes that build.
  **`2.8.0` carries, over `2.7.0`:** the Migration Status batch-selection work (Migration module
  `1.7.0` -- three bulk actions with distinct targets, no status allowlists, inline ticket entry,
  deselect-on-accept, queued-not-done wording) and the replacement favicon.
  **The owner found a module-version error on the first `2.8.0` dev deploy by reading it off the
  page:** the app version was correctly `2.8.0` while Migration still said `1.6.0`, three behaviour
  commits stale. **A correct app bump is not evidence about a module bump** -- the two rules fire
  independently, and the correct one made the wrong one look right by association. Fixed before
  this deploy.
  **Superseded record, kept for incident tracing: dev and prod both ran `2.7.0` from 09:35:16 to
  14:29:33 on 2026-08-10.** The paragraph below describes that build.
  **This is the first build where protected-principal servicing actually works.** `2.6.0`, which
  both instances ran until now, carried the same feature with all three review findings live: the
  page gate hid the capability in AD Attribute Editor, the undo path allowed overrides with no
  audit record, and bulk CSV refused what the single form allowed. **Nothing but the version number
  distinguishes those two builds, so an incident spanning 2026-08-07 to 2026-08-10 must check which
  was running.**
  **The capability grants nothing until a servicer group is configured** per module in Module
  Config, and no manual check has confirmed it end to end -- see `## Next`.
  **Supersedes the "dev `2.5.5`, prod `2.5.5`" record below** (2026-08-05, verified from the
  assembly at the time; its eyeball check of a disabled submit button remains unrun).
  Both instances had been four months behind on `2.3.34` until 2026-08-04 and now carry
  section-access SID storage, the `sidf-1` admin-lockout fix, the full UI redesign, ten themes, the
  module-scoped jobs panel, in-process export retention and the `AdminBulkJobs` page. That clears
  the long backlog recorded below -- **and none of those work streams' manual checks has been run
  on either instance.**
  **Prod is level with dev** (re-verified 2026-08-05; the earlier "one behind" note is stale).
  **Caution: version numbers alone cannot identify a build here.** Two different `2.5.1` builds
  shipped 34 minutes apart earlier the same evening, straddling commits. Hash the deployed file
  against the repo when it matters, as was done for `app.css` above.
  Deployed-version claims below this line predate 2026-08-04; treat the specific build numbers in
  them as history, not as current state.
  **Dev now carries, none of it validated against a live directory yet:** the operator-email
  resolver + MessageTrace export redesign + MessageTrace NRE fix (`2.3.31`-`2.3.32`),
  Exchange-backed protected-principal resolution (`2.3.33`), protected-principal admin input
  validation (`2.3.34`), and the four `ppv-*` review fixes.
  **Prod carries NONE of them.** Still live on prod: the GAP 4 alias bypass, the L1/L2
  cloud-only + mail-enabled-group denial friction, unvalidated admin input, and the
  MessageTrace null-row NRE. `2.3.30` was deployed to dev, validated, then promoted, and
  supersedes all prior deployed builds, so prod does carry the `2.3.28` Bulk Job Runner, the
  `2.3.29` log-root fail-fast, and the `2.3.30` ExcludedUsers-fallback retirement. Prior prod
  baseline was `2.3.27` (validated 2026-06-29). (Settles the conflict openreview F3 flagged:
  the "not deployed anywhere" claim was the stale half. OQ-4 in
  `docs/OperatorEmailResolution-Plan.md` is closed.)

## Archived 2026-08-05 (drift sweep)

### Coverage ratchet repair implemented (floor 65.06 -> 65.12)

- **Coverage ratchet repair IMPLEMENTED 2026-08-05** (owner approved; `8d614b4` slice 1,
  `4daf5d9` slice 3, `b5df487` slice 2). **CONFIRMED ON CI**, not just locally.
  Floor raised **65.06 -> 65.12**, taken from the CI figure in its own commit -- raising it in the
  same commit as the improvement would have set a floor from a number no CI run had produced.
  **The plan's "measure, do not assume" clause earned itself:** the three planned extractions
  landed at 64.9% -- better, still 3 lines short -- so two more were pulled from the same file
  rather than touching the floor. `SectionAccessDirectoryReading` is at 100%.
  **Slice 2 (raise the floor) is DELIBERATELY NOT DONE.** Local margin is 0.06 points (65.12 vs
  65.06) and CI's arithmetic differs, so raising it from a local number would be guessing -- which
  is how a floor becomes unreachable. Raise only after a green CI run reports a real value, in its
  own one-line commit.
  **Measurement error worth keeping:** an intermediate run was reported as "gate exit: 0" when the
  gate had failed -- `$LASTEXITCODE` read after a `Select-Object` pipeline carries the pipeline's
  code, not the script's. Caught only because the reported figure was arithmetically below the
  floor it claimed to clear. **Read a gate's verdict line, not an exit code taken through a pipe.**

### Coverage ratchet repair planned and reviewed

- **Coverage ratchet repair PLANNED and REVIEWED 2026-08-05 -- plan now In progress.**
  **The shortfall is SIX LINES** (1015/1569 = 64.69% against a 65.06 floor), measured before
  choosing a shape -- which ruled out treating it as a "write a big test suite" problem. Cause is
  dilution, not regression: `0e35e7b` added 49 lines to `Services/SectionAccessGroupDirectory.cs`,
  a file at **0/115**, so the ratio fell while the numerator stood still.
  That file is untested **by construction** -- every path opens a PowerShell runspace and imports
  `ActiveDirectory`. The plan applies the extraction the repo already used twice for this exact
  shape (`MailboxPermissionOutcome`, `CalendarFolderIdentity`): move the three pure decision points
  where a wrong answer is a silent authorization defect, leave the runspace calls alone.
  **openreview codex (gpt-5.5-dzs @ xhigh, grade `fallback`) over `506c2d4..62d84d9`:
  `acceptable_with_changes`; 3 findings, all ADMITTED after independent verification. Repair
  re-review over `62d84d9..e9ed249`: `best_approach`, no findings.**
  Two of those findings are worth carrying forward as habits, not just fixes:
  **(a) I claimed the unextracted fail-closed paths were "covered by the live tests where a host
  allows". They are not** -- no test constructs the real service, and no live-AD file mentions it.
  A plan asserting coverage that does not exist is worse than one admitting a gap, because it
  stops anyone looking. Corrected in place so the record shows the gap is accepted, not absent.
  **(b) The stale-coverage-report hazard was filed as "not observed"** -- it had already misfired
  earlier in the same session. It is now slice 3, a guard in the tool, because a procedure that
  relies on remembering an incantation is weaker than a check.
  Also self-caught: the plan estimated "20-25 lines" for the extraction; counting gives **15**, and
  extraction adds coverable declaration lines, so the margin over 6 is thinner than it read. Slice
  2 measures rather than assumes and names the next candidate if it falls short.
  **D1 (whether the gated scope should keep `ProtectedPrincipalService` at 62% and
  `PermissionValidator` at 46%, which will dilute it again) is open and blocks nothing.**
  **NEXT: owner approval, then slices 1-3.**

### CI test failure fixed and confirmed on CI

- **CI test failure FIXED and CONFIRMED ON CI 2026-08-05** (`506c2d4`, pushed in `0f67e62`).
  Run 31016572894: Test passes -- **1288 passed, 0 failed, 9 skipped** (the 9th skip is the new
  domain-only case, skipping loudly on the standalone runner exactly as designed). The `powershell`
  job passes.
  **`master` is still red, now solely on the coverage gate** -- `64.1% (817 / 1275)` vs the 65.06
  floor -- which is what the prediction below said would surface once Test stopped exiting first.
  **New fact the plan now carries: CI and the dev box do not measure the same denominator.** CI is
  1275 lines to the dev box's 1569, because live-directory tests skip there so their code is never
  instrumented. The shortfall is **13 lines on CI, 6 locally**; a local `Test-CoverageFloor.ps1`
  pass is therefore NOT evidence the gate will pass on CI.

### CI test failure diagnosed: the "DA" SDDL alias host dependency

- **CI test failure FIXED 2026-08-05.** `master` had been red since `ba9fe4f` (2026-08-04) on
  `SectionAccessGroupIdentityTests.RefusesSddlAliases(alias: "DA")`, and the record said the
  coverage gate was the cause. **It was not** -- the failing step is Test; the coverage gate never
  runs because Test exits first.
  **The test asserted an environment, not a behaviour.** `"DA"` is the only SDDL alias needing a
  joined DOMAIN to resolve: here it resolves and is refused as *"not canonical"*; on GitHub's
  standalone runner it throws and is refused as *"not a valid SID"*. Both are correct refusals, so
  the security property held the whole time -- only the wording assertion was environment-specific.
  Measured both branches before touching anything (`ZZ`/`QQ` throw here too, confirming the
  unresolvable path).
  Split into: the two aliases that resolve anywhere (`BA`, `WD`) in the original theory; a
  domain-only case using `Assert.SkipUnless`, which skips LOUDLY per the repo rule that a silent
  early return is indistinguishable from a pass; and a host-agnostic case asserting the part that
  actually matters for authorization -- `"DA"` is never a usable stored group value, whatever the
  reason string says. The skip probes by *resolving the alias*, not by asking the OS about domain
  membership: a joined machine can still fail to resolve it.
  **This file's own header already required these tests to be directory-free so they hold on CI.**
  The invariant was written down and the test still violated it -- the header was not enough on its
  own, which is why the host-agnostic assertion exists rather than only the skip.
  Verified the way the defect demanded -- by reproducing CI's condition, not by trusting a green
  run on a domain-joined box: pointing the probe at an alias that never resolves anywhere gives
  **54 passed, 1 skipped, 0 failed**, so the domain-only case skips and the host-agnostic case
  still holds. Non-vacuity separately proven by deleting the canonical-SID check, which fails all
  four alias tests including the new one.
  **NEXT once this lands: the coverage ratchet becomes the new CI failure** (64.7% vs 65.06). It
  was always failing; the Test step was simply reaching the exit first.

### Handoff snapshot at b106dce

- **HANDOFF 2026-08-05, as of `b106dce`. Tree clean, nothing in flight, nothing blocked.**
  **Repo `2.5.5`; dev `2.5.4`; prod `2.5.2`.** Dev and prod versions were verified from the
  assemblies during the session, not assumed -- do the same before trusting them again, and note
  that two *different* builds shipped as `2.5.1` earlier that evening, so hash `wwwroot/app.css`
  against the repo when it matters.
  **Immediate next action: deploy `2.5.5` to dev** (`.\tools\deploy-pipeline.ps1 -Dev`, ELEVATED)
  and look at a form with an empty required field -- the disabled submit button should carry the
  theme accent, not Bootstrap blue. That is the whole of what `2.5.5` changes.
  **Nothing is awaiting an owner ruling.** Everything below is either landed or queued work.
  **Session shape worth knowing:** the last four commits are one defect found four times in the
  same place -- themes looked flat (`2.5.3`), Bootstrap's palette was never bridged (`2.5.4`),
  then button *states* were still unbridged (`2.5.5`). Each round the verification answered a
  narrower question than the owner's eyes did. If a fifth round appears, suspect specificity or an
  unstyled Bootstrap class before suspecting the tokens.
  **Two probe failures this session, same shape:** a literal string replacement silently matched
  nothing and the probe reported PASS. Assert the file actually changed before believing a
  non-vacuity result.

### Bootstrap palette bridged + Dracula corrected (app 2.5.4)

- **Bootstrap palette bridged + Dracula corrected 2026-08-04, app `2.5.4`, ON DEV** (verified
  `FileVersion 2.5.4.0`, deployed 22:38; dev `app.css` hashed identical to the repo at that
  point). Superseded same day by `2.5.5` above.
  Owner after `2.5.3` reached dev: *"buttons and checkboxes are still blue on every theme. dracula
  isn't using green or yellow and doesn't match the dracula theme. that one I know well, and this
  is a bad implementation."* Both correct. `docs/ThemeSupport-Plan.md` slice 7.
  **Why blue survived a full theme rework -- the number is the finding:** this app uses **24
  colour-bearing Bootstrap classes and the theme layer restyled 3.** Checkboxes, switches, radios,
  spinners (159 uses), badges (43), `.text-*`/`.bg-*` utilities and the success/danger/warning
  buttons are all painted by Bootstrap from `--bs-primary: #0d6efd`, which nothing overrode --
  slice 1 mapped 8 `--bs-*` variables and stopped short of the semantic palette. Fixed at the
  variable layer, which repairs the other 21 classes at the source; explicit rules remain only
  where Bootstrap uses a shorthand that ignores the variable or bakes the colour into an SVG data
  URI (the switch knob).
  **Lesson worth keeping: a token layer only reaches what consumes it.** The tokens were correct
  and the themes were correct; the framework simply was not reading them. Verifying "the theme
  system works" on the pages we restyled proved nothing about the 21 classes we had not.
  **`-rgb` companions are a deliberate second copy** of each accent (Bootstrap needs raw triplets
  for alpha blends) and they already drifted during development -- Dracula's warn moved yellow ->
  orange and the triplet did not follow, which is near-invisible since only translucent overlays
  go wrong. `EveryRgbTripletMatchesItsHexToken` derives the expected value from the hex; they are
  also in `RequiredTokens` so a theme cannot omit them.
  **Dracula specifically:** the accent values were spec-correct, but the canvas `#1e1f29` was
  invented rather than the palette's own ANSI black `#21222c`, and the state tints were off-hue --
  danger tinted toward pink while its foreground was red, and warn used Yellow where Orange
  `#ffb86c` is the palette's warning colour.

### Theme palettes reworked (app 2.5.3)

- **Theme palettes reworked 2026-08-04, app `2.5.3`, ON DEV.** Deployed by the owner 22:24 and
  verified: dev assembly `FileVersion 2.5.3.0`, and dev `wwwroot/app.css` is **byte-identical to
  the repo** (SHA256 match), so the reworked palettes are genuinely live rather than a stale copy
  surviving the mirror. Owner rejected the first cut on sight after `2.5.1` reached dev: *"the
  themes aren't implemented optimally. I see really only two main colors."*
  `docs/ThemeSupport-Plan.md` slice 6.
  **The mechanism was fine; the VALUES were wrong** -- so this was a data-only fix with no rule
  touched, which is the token layer working as intended.
  Measured cause, three parts: (a) canvas/surface/header sat within ~12 luminance points, below the
  ~20 a step needs to read as a step, so cards did not sit on the page and table headers did not
  separate from rows -- every palette collapsed to background-plus-text; (b) the accent reached
  only links, the primary button, the focus ring and a tab underline, so the colour that makes a
  theme recognisable never appeared on a working page; (c) the sidebar shared the page background
  and read as empty margin. Spread is now 16-33, canvases use each project's own darker variant
  rather than one hue at four brightnesses, card headers carry a 2px accent rule, and the nav has
  its own tone.
  **Two new tests pin what was broken:** surface-separation guards, because the existing contract
  tests only checked tokens EXIST -- which says nothing about them being distinguishable. The
  canvas->surface guard caught Tokyo Night at 5.1 during development and forced a deeper canvas.
  **A false-passing probe is worth remembering:** the first non-vacuity attempt used a literal
  string replace that silently did not match, and reported PASS. Only the null-reference noise
  beside it gave it away. A probe that does not visibly change the file proves nothing -- assert
  the edit applied before trusting the result.

### Coverage ratchet failing - resolved 2026-08-05 (CI green, floor raised)

- **OPEN, PRE-EXISTING -- the coverage ratchet is FAILING and no one noticed.** 64.7% against a
  65.06 floor. **Not caused by the theme work:** measured in a scratch worktree at `6f89f1c` with
  no theme code and got `1015 / 1569`, identical. Cause traced to `0e35e7b` ("show section-access
  groups as DOMAIN\Name"), which added 49 lines to `Services/SectionAccessGroupDirectory.cs` --
  a **0%-covered** file -- after the floor was set at `9a66cf4`. Growing an uncovered file lowers
  the ratio; the gate is correct and is reporting a real regression.
  **The floor was NOT lowered** -- `.agents/review/coverage-floor.txt` says in terms that doing so
  converts the gate into decoration, which is finding tsr-1, already made once here.
  **Fix needs a seam:** `SectionAccessGroupDirectory` talks to live AD, so it cannot be tested as
  it stands -- same shape as the `MailboxPermissionOutcome` / `CalendarFolderIdentity` extractions
  in `docs/TestSuiteRemediation-Plan.md`. **CI on `master` is red until this is fixed.**

### Section-access groups stored as SIDs - slice detail

- **Section-access SIDs — slice detail (all landed 2026-08-03).**
  `docs/SectionAccessSidStorage-Plan.md` Status: Approved, no open owner gates. The defect: a
  bare group name does not identify a group, and both comparison sites
  (`GroupAuthorizationHandler:97-101`, `GroupMembershipChecker:38-45`) strip any `DOMAIN\`
  prefix and accept either form, so a foreign-domain same-named group is indistinguishable.
  Exposure measured, not assumed: of 10 trusts only **2 are BiDirectional**
  (`winroot.analog.com`, `maxim-ic.internal`), so the collision surface is 3 domains, not 10 —
  narrow, not zero, and it sits in the field deciding entry to privileged modules.
  **Two measurements constrain the whole work stream.** (a) The Windows token already carries
  **333 group SIDs**, never names — the app converts SIDs to names in order to compare names, so
  storing SIDs REMOVES a translation. `WindowsPrincipal.IsInRole` accepts a SID string directly.
  (b) Prod log 2026-08-03: **1687 authorizations via `user.IsInRole`, 0 via the claims path** —
  `ClaimTypes.Role` is never populated under Negotiate. `GroupMembershipChecker` is therefore
  dead in the live path but NOT dead code (the bulk job runner's off-circuit re-check needs it),
  so changing only the handler would leave the job runner comparing names against SIDs.
  **Slice 1 DONE**: `Authorization/SectionAccessGroupIdentity.cs` — pure, directory-free, so the
  decisions the migration depends on hold on CI too. Refuses SDDL aliases (`new
  SecurityIdentifier("BA")` SUCCEEDS and yields BUILTIN\Administrators; `"DA"` is an account SID
  passing every check but the round-trip), well-known SIDs via `IsAccountSid()` so no blocklist
  needs maintaining, and the bare domain SID (parses, round-trips, `IsAccountSid()` true, yet
  names a domain). 43 tests; non-vacuity proven per guard (6/3/1/1/1/2/3 failures on revert).
  **Two data facts pinned in code, both verified against live AD 2026-08-03:** the NetBIOS domain
  half is load-bearing — `Enterprise Admins` without `-Server` returns **0** matches, so the
  current normalization's stripping would turn a live cross-domain grant into an unresolvable
  row; and the lookup must query `sAMAccountName`, `cn` AND `name`, because
  `$KOO300-S3AMUVVBVMI1` is a sAMAccountName whose `cn` is `Employees-All`.
  **All 18 distinct prod values resolve to exactly one group** (58 rows: 46 `ANALOG\`, 1
  `winroot\`, 11 bare). There is no unresolved-row class in this data — the plan's D2 was
  withdrawn on owner challenge after the apparent exception turned out to be a probe bug
  (`Get-ADGroup -Filter` expands `$` as a PowerShell variable).
  **Slice 2 DONE** (`16ef8c0`): schema v6 (`group_display_name`), pure
  `SectionAccessSidMigrationPlanner`, `SectionAccessGroupDirectory` (AD + the NetBIOS->DNS
  crossRef mapping), `SectionAccessSidMigration` runner. Never blocks boot, never half-writes:
  one unconvertible row stops the whole write, and a directory failure propagates rather than
  becoming "no such group" — an outage must not send an admin to fix correct data. The AD
  lookup is a **separate service** from `ADDirectorySearchService` because that one is fail-soft
  by design and this one must throw.
  **Slice 3 DONE** (`f361281`): app `2.3.34 -> 2.3.35`. Reads `ClaimTypes.GroupSid` (which
  Negotiate populates) instead of `ClaimTypes.Role` (which it does not); normalization deleted.
  **Slice 4 DONE** (`0a50d01`): picker returns a SID with no name fallback, badges show names,
  free-typed text refused. `SaveAll` carries display names across its delete-and-reinsert —
  without that every admin save would blank names the idempotent migration would never restore.

## Archived 2026-07-30 (catchup sweep)

### Retire `Security:ExcludedUsers` appsettings fallback — complete code + host, both environments

- **Retire `Security:ExcludedUsers` appsettings fallback — DONE (code half), landed 2026-07-28.**
  Plan `docs/RetireExcludedUsersAppsettingsFallback-Plan.md` Status: Implemented;
  `.agents/decisions.md` 2026-07-28. Both readers (`PermissionValidator.GetConfiguredExclusions`,
  `ProtectedPrincipalService.GetLegacyExclusions`) no longer fall back to the invisible
  `Security:ExcludedUsers` appsettings array; exclusions come only from the DB protected-principal
  store + `MailboxPermissions/ExcludedUsers` module config. Base app `2.3.29 -> 2.3.30`.
  Commits `4dff069`(plan) `f5b329b`(slice1) `942dd10`(slice2) `5c7cc93`(slice3 tests)
  `c35f056`(slice4 docs) `456e07c`(version). Build/format/827 tests green; two new guard tests
  non-vacuity-proven (fallback restored -> both fail).
  **Slice 5 host cleanup DONE (owner, 2026-07-29):** the `Security.ExcludedUsers` block was
  removed from the deployed `appsettings.json` on both dev and prod (`PreventSelfGrant`,
  `AllowedGroups` left in place). The ExcludedUsers-fallback retirement is now fully complete,
  code + host, both environments.

### Log-root fail-fast — landed and validated in prod

- **Log-root fail-fast IMPLEMENTED** (2026-07-22, `docs/RemoveHardcodedLogRoot-Plan.md`).
  Hardcoded `E:\WWWOutput` fallback removed from all three services; startup guard aborts boot if
  `Audit:LogRoot` is unset/blank. Commits `fa40485` (helper + guard), `b14fce6` (services),
  `821a2f8` (docs), `3eac48a` (app version bump 2.3.28 -> 2.3.29). Build + all 676 tests green.
  **Deploy note:** the new build fails to start if `Audit:LogRoot` is unset; the target env's
  `appsettings.json` must set it before deploying `2.3.29`.
- **RESOLVED (2026-07-29):** `2.3.29`'s log-root fail-fast is now validated in prod — it ships
  inside `2.3.30`, which the owner deployed + validated + promoted to prod. The startup guard is
  inherently exercised: the app cannot boot without `Audit:LogRoot`, and it booted.

### Bulk Job Runner — landed; only live validation remains (tracked in `state.md` Next up)

`docs/BulkJobRunner-Plan.md` (Status: Implemented) · `.agents/decisions.md` 2026-07-02.
App `2.3.27`→`2.3.28`; ConferenceRooms module `2.1.0`→`2.2.0`.

ConferenceRooms bulk apply (Finder/Type CSV) now runs as a durable server-side job (separate
`config/exchangeadmin-jobs.db`, never promoted). Self-pumping singleton runner (not a hosted
timer); single active job + FIFO queue; startup flips non-terminal jobs to Interrupted (no
resume); always cancellable; per-row failure aggregation; completion email fires from the job.
Off-circuit auth = option (a) (capture the authorization decision at submit, re-check per row via
shared pure `GroupMembershipChecker`). Protected-principal gate enforced in-job per row on
**both** Finder and Type bulk paths (closes GAP 3). Deploy scripts warn (not block) on active jobs
before recycle (`tools/JobStateWarning.psm1`). ~671 xUnit + 65 Pester green (as of `9d26b5f`);
build/format/diff-check clean; each slice codex-reviewed with findings fixed before commit.
(Dev deploy done 2026-07-20.)

### Landed `Next up` items rotated out (2, 5, 6)

2. **Single-room Finder protected-principal gap** — **DONE** (2026-07-21, commit 2a97d09;
   `docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md` Implemented). Consolidated the
   module PP check into one `ConferenceRoomProtectionGate` (C2-G). Only remaining follow-up is
   live-instance/UI validation not yet performed (runs against PROD from the dev instance).
5. **GM-3 self-service group management (on-prem AD only) — DONE 2026-07-27.** All 6 tasks (plan
   section 7) landed and codex-reviewed; see the `## Now` pointer and this archive.
   Only follow-up is live validation, not yet performed (runs against PROD AD from the dev
   instance). Not next.
6. **ASCII cleanup sweep + enforcement lint** -- **DONE** (2026-07-21). Scope narrowed by owner to
   code/logging only (`.cs`/`.ps1`/`.psm1`); docs, `.razor` UI, and `EmailService.cs` email emoji
   excluded. (a) Sweep landed commit `c2e2f6f` (329/329 char swaps, 77 files, 672 tests green).
   (b) CI gate `tools/Test-AsciiOnly.ps1` wired into `.github/workflows/ci.yml` `powershell` job,
   non-vacuity proven. See `.agents/decisions.md` 2026-07-21.

### Closed blockers rotated out

- **CLOSED (2026-07-24) — ptk blocker + AD scan-sizing.** At the 2026-07-24 close ptk had been
  removed (server + the shell-blocking hook that forced AD calls through it), so the "ptk down is
  a STOP; no direct PowerShell fallback" rule no longer applied. (2026-07-28: ptk is available
  again this session — use it per global guidance when present.) The one AD read it had gated (a
  domain-wide `Get-ADGroup` count) was run directly: **41,368 groups** (now in the archive). Moot
  regardless: the scaled-back task-2 design (2026-07-24) dropped the domain-wide scan entirely.
  (The trailing fragment carried in this entry — the single-room Room Finder PP-gate description —
  was a paste artifact duplicating the PP-gaps entry; the canonical record is
  `docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md` and commit `2a97d09`.)
- **CLOSED (2026-07-30) — prod BlockedSenders version uncertainty.** The recorded doubt was that
  the two BlockedSenders fixes (`17910f3`→1.0.1, `cde778f`→1.0.2) are module bumps, not app bumps,
  so a prod app version could not confirm them. Resolved by direct evidence: prod runs `2.3.30`
  (deployed assembly `D:\inetpub\ExchangeAdminWeb\ExchangeAdminWeb.dll`, FileVersion `2.3.30.0`)
  and both commits are ancestors of the `2.3.30` version-bump commit `456e07c`
  (`git merge-base --is-ancestor`, verified 2026-07-30). Prod includes both fixes.

## Archived 2026-07-29 (catchup sweep)

### 2026-07-21 landed slices (all pushed + CI green)

- **This session landed (2026-07-21), all pushed + CI green:**
  - `ff443ca` -- decision+docs: new module does not bump base app version (Constitution +
    decisions.md + repo-guidance; resolved the long-open versioning exception).
  - `c2e2f6f` -- ASCII sweep of code/logging (77 `.cs`/`.ps1`, 329/329 char swaps). Scope narrowed
    by owner to code/logging only; docs, `.razor` UI, `EmailService.cs` email emoji excluded.
  - `502dd0e` -- ASCII CI lint gate `tools/Test-AsciiOnly.ps1` (excludes `EmailService.cs`), wired
    into `.github/workflows/ci.yml` powershell job.
  - `8c6f83f` -- fixed xUnit1051 warning that had reddened CI since 2026-07-20 (format check treats
    analyzer warnings as fatal).
  - `9dd39cd` -- state note recording that format-warning trap.
  - `b978362` -- fixed `ConferenceRoomProtectionGateTests` hardcoded `E:\WWWOutput` log path (was
    masked until format gate went green; failed only on CI, not the ADI dev box).
  - `71d1daa` -- Approved plan `docs/RemoveHardcodedLogRoot-Plan.md`.

## Archived 2026-07-28 (catchup sweep)

### MessageTrace per-message delivery-detail (MT-detail) — landed complete 2026-07-27

- **MessageTrace per-message delivery-detail (MT-detail) — COMPLETE 2026-07-27.**
  `docs/MessageTraceDetail-Plan.md` Status: Implemented. All 9 slices landed on
  `master`, each committed and codex-reviewed accepted (records
  `.agents/review/findings/mt-detail-slice1..9.md`, index rows mt-detail-slice1..9).
  Core defect fixed: the trace no longer collapses a message's per-hop trail; a
  per-row Details drill-in shows every event (on-prem never collapses, free;
  cloud costs one `Get-MessageTraceDetailV2` per click), plus checkbox selection
  with threshold-driven download (1-10) / off-circuit email (1-50, cap 50) of a
  zipped CSV to the authenticated mailbox + configured admins ONLY (never a typed
  address; exfiltration-gated), pre-zip CSV saved under
  `<AuditLogRoot>\ExchangeAdminWeb\MessageTraceExports\`. MessageTrace module
  `1.1.1 -> 1.2.0`; no base app bump (reuses bulk-job runner / audit root /
  admin-email config / EXO+on-prem creds). Slice-9 verification: build 0 errors,
  807/807 tests, format/diff-check clean, `MessageTrace.razor` pure ASCII.
  **Manual-validation-on-dev / deferred (no dev tenant):** live EXO/on-prem
  detail drill-in, download parity, email/zip delivery, and the Blazor selection
  UI — enumerated in the plan's "Manual validation still required" section.
  Reviewer transport = codex headless CLI per the standing 2026-07-27 owner
  ruling (see GM-3 note below). Commits `.prompt.txt`/`.result.txt` scratch left
  untracked pending the owner's commit-vs-clean decision.

### GM-3 self-service group management — on-prem AD, task set (plan section 7) COMPLETE 2026-07-27

- **GM-3 SCOPE NARROWED AGAIN 2026-07-22 → on-prem AD ONLY. M365/delegated-Entra DROPPED entirely.**
  (`.agents/decisions.md` "on-prem AD only"; plan `docs/SelfServiceGroupManagement-Plan.md` revised,
  Status: Approved / on-prem only.) Trigger: the delegated design forced the actor↔Entra binding
  decision (F1); the owner's real need was cross-identity (Windows on-prem login acting on an
  Azure-only -CLD account's groups), which is better served by the Microsoft portal. On-prem AD
  self-service is the value the portal doesn't give.
  - **DROPPED (moot now):** second auth scheme, Microsoft.Identity.Web/MSAL, token cache,
    actor↔Entra binding, `/me/ownedObjects`, dedicated Entra registration, task 0 (§6.8), delegated
    security-review gate. Codex F1/F2/F3/F4/F8/F12/F13 moot.
  - **RETAINED = the whole feature now:** on-prem ownership reverse-lookup (`managedBy` +
    `msExchCoManagedByLink`), fail-closed eligibility allowlist (F5), user-only add/remove with
    pre-write re-checks + protected-principal (F7, F9), injection-safe resolution (F11), audit +
    affected-user notify on on-prem security-group changes (F10). No background worker needed.
  - **Reverted in-progress delegated code (slice-1 steps 1-2 from commits `4be93a3`, `22c0510`):**
    removed the Microsoft.Identity.Web package, `Services/SelfServiceGroups/DelegatedEntra*`, and the
    `ISecretFieldsReader` seam on `DelineaService`. Build green at clean baseline.
  - **Revised on-prem task set (plan §7):** 1 on-prem reverse-lookup ✅ → 2 fail-closed eligibility ✅
    → 3 module descriptor + page skeleton ✅ → 4 list + in-list filter (AC9) → 5 member add/remove with
    pre-write re-checks + audit/notify → 6 verification/manual-validation note.
  - **Task 1 DONE + codex-reviewed** (commits `0afb2bf`, `9633fa7`, `1f65674`, `7922d42`):
    `Services/SelfServiceGroups/` — `ManageableGroup` (model, `CanManageMembers` defaults false),
    `AdOwnershipFilter` (pure injection-safe RFC 4515 LDAP filter, codex F11, 10 xUnit tests
    non-vacuous by exact-output assert), `SelfServiceGroupService.GetOwnedGroupsAsync` (resolves caller
    once by immutable SID via bound -Identity, then bound -LDAPFilter query; own module cred; throws on
    hard AD failure so page shows error not empty, AC8). Codex review fixes: F-high = SID provenance
    (only a genuine Windows SID accepted, alternate identity forms rejected, AC6) + SID tests; F-med =
    dropped silent 500-group cap (Known Failure Class #2). Codex F-med on `ResolveOwnerDisplay`
    SilentlyContinue KEPT as designed — it is the plan's F12 fan-out behavior (one owner's failed
    lookup shows the DN, not a whole-load failure; main query is still -Stop). Build + format +
    diff-check clean; 697 tests green. Live AD query is manual-validation-on-dev (no dev tenant).
  - **TASK 2 REDEFINED 2026-07-23 — owner rejected the plan's admin allowlist** ("any group manager
    can just open ADUC and manage the group themselves — an arbitrary security speedbump with no
    point"). New eligibility rule: **"show groups the user can actually update."** = the caller's SID
    (or a group SID they hold) has GenericAll / GenericWrite / WriteProperty-on-`member` / WriteProperty-
    on-all-props on the group. `managedBy` "manager can update membership" is just one such ACE, so this
    SUBSUMES task 1's manager lookup. SelfMembership (self-only) does NOT qualify. This diverges from the
    Approved plan and codex F5 — plan §6.3/task 2 and `.agents/decisions.md` MUST be updated before code.
  - **Discovery DONE (read-only, `tools/Discover-GroupMembershipDelegation.ps1`, UNCOMMITTED).** Answers
    "how do L1/L2 get edit rights when not in the manager field": on a 400-group sample across the two
    biggest OUs, after stripping inherited domain-admin noise + service accounts, **82 real delegations,
    almost all DIRECT ACEs naming individual people** (69 users, e.g. 9 groups for one person); the 13
    unresolved SIDs are deleted accounts. **ZERO helpdesk-group delegation.** So non-managers get rights
    via direct per-group ACEs on themselves — not via a group. `nTSecurityDescriptor` is NOT searchable
    by trustee, so there is no cheap per-user LDAP query for "groups I can edit"; you must read ACLs.
  - **Scan universe sized (2026-07-24):** domain-wide `Get-ADGroup -Filter *` = **41,368 groups**
    (~2x the two sampled OUs). At the discovery script's ~0.017s/ACL that is ~11.7 min single-thread /
    ~5.9 min at throttle=2. Owner ruled **no OU scope is viable** — the AD has grown since NT4.0 and any
    OU allowlist would be brittle and silently miss groups. So a global ACL scan would have to cover the
    full 41k.
  - **DESIGN SUPERSEDED / SCALED BACK (owner, 2026-07-24) — the global ACL-scan design is DROPPED.**
    The 41k full-domain scan (cached global map, single-flight lock, multi-minute blocking wait) is too
    expensive/risky for the value. Replaced with two cheap targeted lookups, no scan / no cache / no lock:
    1. **Passive list = managedBy-manager groups only.** Show groups where the caller is the declared
       `managedBy` manager AND "Manager can update membership" is on (the WriteProperty-on-`member` ACE
       granted to the manager). This is essentially what **task 1 already built** — task 1 becomes the
       list. NOT the broad "any editable group" rule; the 2026-07-23 ACE-based eligibility (GenericAll/
       GenericWrite/etc.) is NO LONGER the list rule.
    2. **On-demand single-group search.** User types a specific group name; resolve it and check whether
       the caller can manage its membership; if yes, return it (manageable); if no, return an error
       telling them to contact the IT Support Desk. Handles the case where a user knows they have rights
       (e.g. a direct per-group ACE, per the discovery finding) and knows the group name, without any
       domain-wide scan.
  - **Doc edits DONE (2026-07-24, commit `ec31788`):** scaled-back task-2 design written into
    `docs/SelfServiceGroupManagement-Plan.md` §6.3/task 2 (allowlist AND 2026-07-23 ACE-scan both
    replaced; F5 note + Status header updated) and the supersession recorded in `.agents/decisions.md`.
    The 41k scan-sizing work and the global-map design are history, kept above only as the rationale
    for dropping the scan.
  - **TASK 2 DONE + codex-reviewed** (`.agents/review/findings/gm3-task2-slice1.md`, `-slice2.md`):
    (a) list-time eligibility = manager-can-update-membership (WriteProperty-on-`member`), enforced per
    candidate via credentialed-drive DACL read (`GroupMembershipAce` pure classifier keys on rights BITS
    not the shared `member` schema GUID); (b) on-demand single-group search
    (`SearchManageableGroupAsync`, injection-safe RFC 4515, not-found/ambiguous/not-manageable all return
    the same "contact IT Support Desk" message). Slice-2 codex round caught + fixed a HIGH: the SID gate
    accepted SDDL aliases (BA/DA/SY/WD) — now requires canonical-SID round-trip (`e748e32`).
  - **TASK 3 DONE + codex-reviewed** (`ba22cf5` slice, `47f357a` review record;
    `.agents/review/findings/gm3-task3-slice1.md`): `SelfServiceGroups` module descriptor
    (`Modules/ModuleCatalog.cs`: Access-only FailClosed, Directory & Groups, SortOrder 165,
    EnabledByDefault=false, v1.0.0, on-prem `DelineaSecretId`), DI registration (`Program.cs`,
    Scoped), and `Components/Pages/SelfServiceGroups.razor` (`[Authorize]` + OnInitializedAsync
    re-check, `<ModuleVersion/>`, load button with required spinner + disabled state per plan 6.4,
    owned-groups table, on-demand single-group search box; caller SID from the PrimarySid claim, AC6).
    Adding a module did NOT bump the base app version. Count-guards updated (23 modules, 32 aliases),
    proven non-vacuous. codex verdict accepted, no material issue.
  - **TASK 4 DONE + codex-reviewed** (`f17f3de` slice; `.agents/review/findings/gm3-task4-slice1.md`,
    index row `gm3-task4-slice1`): in-list filter (AC9). `Services/SelfServiceGroups/ManageableGroupFilter.cs`
    (pure UI-free helper: case-insensitive SUBSTRING match across Name/SamAccountName/Description, blank
    term returns all, input order preserved) wired into `Components/Pages/SelfServiceGroups.razor` (filter
    input over the loaded list only — no directory round-trip; filtered-empty renders a message distinct
    from the AC8 load-failure error; `filterTerm` reset each load). 12 xUnit tests, proven non-vacuous
    (Contains->StartsWith fails 3, restore passes 12, `git diff --stat` empty). Full suite 728/728,
    build/format/ASCII-lint clean. codex-commercial (MCP transport, default model/effort) verdict accepted,
    no material issue. Review dispatched read-only/static-code-judgment-only: two prior dispatches (codex
    cli, then a first codex-commercial run) both died at ~30 min trying to run the test suite — the cli on
    the sandbox's blocked NuGet feed (isolated snapshot can't `dotnet restore`), the MCP run on the
    transport idle timeout during that silent test run. Runtime guard proof is the coder's job and was
    done; the reviewer's contribution is static judgment, which needs no build.
  - **TASK 5 IN PROGRESS** — member add/remove with pre-write re-checks + audit/notify (the ONLY mutation
    in the first cut). USER-ONLY members; resolve exactly one immutable member id; before each write
    re-check module permission + re-read group + re-check eligibility + re-check ownership by immutable
    id + ProtectedPrincipalService.CheckAsync on the affected member; fail-closed; serialize same-group
    ops; idempotent desired-state; post-write read-back reconciliation; NO background worker/outbox.
    Owner decisions settled B/B: notify = audit-first best-effort, no background worker (Decision 1);
    TOCTOU = accept + document the ms race, least-privilege write cred as backstop (Decision 2).
    - **Slice 5a DONE + codex-reviewed** (`08a2a53` slice, `1dac5d5` review; findings `gm3-task5-slice5a`):
      pure decision core `MembershipChangeReconciler` (idempotent desired-state + read-back
      reconciliation, 6 xUnit tests). Accepted, static-only, no material issue.
    - **Slice 5b DONE + codex-reviewed** (`6fd722f` slice; F1 fix `246a197`, F2 fix `5ef1b0d`; `46e4bb6`
      review record; findings `gm3-task5-slice5b`): live AD write path `SelfServiceGroupService.ChangeMemberAsync`
      — resolves the affected member ONCE (own cred, RFC 4515 person/user-only filter) into a
      ResolvedDirectoryPrincipal, checks + writes THAT principal (F1), reconciles the write even when the
      Invoke throws with a guarded read-back (F2). 740/740 tests, build/format/ASCII clean. codex-commercial
      (MCP, default) reopened on the slice commit for F1/F2, accepted after both one-per-commit fixes.
    - **Slice 5c DONE + codex-reviewed** (`164b83a` return metadata + `MembershipChangeResult` record + 8
      tests, `aafc13e` email method, `b461fed` page UI + version bump; `.agents/review/findings/gm3-task5-slice5c.md`,
      index row `gm3-task5-slice5c`): `Components/Pages/SelfServiceGroups.razor` Manage-members panel calls
      `ChangeMemberAsync` (add/remove USER by typed identity — no member-list method in first cut); page
      re-checks the SelfServiceGroups policy before the write (defense in depth, AC5); audit-first
      best-effort notify (owner decision B) — `Audit.LogModuleAction` FIRST (own try/catch, never masks),
      then best-effort `Email.SendAdminNotificationAsync`, then affected-user
      `Email.SendGroupMembershipUserNotificationAsync` gated by `MembershipChangeResult.NotifyAffectedUser`
      (success AND real change AND security group AND known address — AC10, Constitution Notifications).
      `ChangeMemberAsync` now returns `MembershipChangeResult` (PermissionResult + notify metadata from the
      SAME single member resolution — no F1 regression). SelfServiceGroups module 1.0.0 -> 1.1.0 (no base
      app bump). Build 0 errors, 748/748 tests, ASCII/format/diff-check clean; gate non-vacuity proven
      (IsSecurityGroup term inverted -> distribution test fails -> restore -> 8/8). Live add/remove+notify
      is manual-validation-on-dev (no dev tenant).
    - **REVIEWER TRANSPORT (2026-07-27):** the codex-commercial MCP reviewer CANNOT see un-pushed local
      HEAD — under a no-shell constraint it has no local file reader and falls back to the connected GitHub
      repo, so a local-only SHA returns `invalid`; without the constraint it runs `dotnet` and dies at the
      30-min idle timeout on the blocked NuGet feed. Owner ruling: use **codex headless CLI** for these
      reviews — `codex exec -s read-only --output-last-message <file> - < promptfile` (prompt via stdin;
      too long for an arg), run in background. It has direct read-only LOCAL repo access, so it reviews
      un-pushed commits fine. A recurring `Failed to refresh token` line in its log is benign (review still
      runs). Do NOT use `--skip-git-repo-check`. codex-cli 0.145.0 on this machine.
    - **Task 6 DONE (docs-only)** — verification + manual-validation note, the last task in the plan §7 set.
      Filled plan §9 traceability (AC->automated-vs-manual mapping) and bumped the plan Status/Last-verified
      header to `1920eb8 (2026-07-27) — task set COMPLETE`. Verification at that commit: build 0 errors,
      748/748 tests, ASCII lint + `dotnet format --verify-no-changes` + `git diff --check HEAD` all clean.
      Manual-validation-on-dev / deferred (no dev tenant): live AD add/remove, audit-record write, admin +
      affected-user email sends, and the Blazor page flow. No delegated security-review gate (M365 half
      dropped — no cloud tokens). **GM-3 task set (plan §7) is now COMPLETE.**
  - codex invocation notes: wrapper takes prompt as an ARG. The revised plan is now TOO LONG to pass
    as an arg (node "filename or extension is too long") — instead give codex a SHORT prompt telling
    it to Read the plan file itself (it has read-only repo access; this worked, task `bhqsbvopo`).
    Do NOT pipe `2>&1 | Tee-Object` — trips the wrapper's stdout-encoding requirement. Run in
    background (reasoning effort is "max", ~5-15 min/round, exceeds a 10-min foreground cap).
    Do NOT add `--skip-git-repo-check` (trips the safety classifier).
