# Agent State

First place to read for current repo state. Keep it short; update it when important repo facts
change. Resolved work lives in the plan/decision/incident docs, not here — this file records only
what is live: current versions, in-flight work, what to do next, blockers, and open gaps.

## Now

**Deployed versions are owned by the single `Deployed:` entry below** -- as of 2026-08-05, dev and
prod both run `2.5.5`. The per-work-stream `NOT DEPLOYED` / `ON DEV` / `not on prod` notes inside
the older entries below record where that stream stood *when it landed*; they are history, not
current state, and are not maintained. Read the `Deployed:` entry, never them.

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

- **IN FLIGHT: make the remaining uncovered lines testable. ONE OF THREE FILES DONE.**
  Owner ruling 2026-08-05, verbatim: *"I don't care what's left. I do not want to have to deal with
  this in the future. make it work."* That is a standing instruction to finish the job, not to
  report on it again.
  **`SectionAccessGroupDirectory` DONE 2026-08-05 (`6642587`): 0% -> 98%**, security-critical
  coverage **66.0% -> 72.9%** locally (943/1294). Not yet confirmed on CI, and the floor is
  deliberately NOT raised until a green CI run reports a real figure.
  **Remaining uncovered, measured at `6642587`:**

  | lines | % | file |
  |---|---|---|
  | 171 | 63% | `Services/ProtectedPrincipalService.cs` |
  | 148 | 46% | `Services/PermissionValidator.cs` |
  | 31 | -- | four files at 80-98%, small remainders |

  **Nearly all of it is one shape: code that calls PowerShell to reach AD or EXO.** Biggest single
  blocks: `PermissionValidator.TryExpandGroupAsync` (70), `EnsureInitializedAsync` (27),
  `ValidateSelfGrantAsync` (21); `ProtectedPrincipalService.CheckTransitiveGroupMembership` (53),
  `ResolveViaActiveDirectory` (35), `ResolveProtectedGroupDn` (29). Tests cannot reach any of it
  without a domain-joined host with RSAT.
  **The fix is a seam over the PowerShell calls**, then tests against a fake. Same move already
  used three times here (`MailboxPermissionOutcome`, `CalendarFolderIdentity`,
  `SectionAccessDirectoryReading`) -- but those extracted PURE logic beside the I/O, which is
  cheap. This is the harder version: abstracting the I/O itself.
  **`ISectionAccessDirectoryCommands` (`6642587`) is the worked example to copy for the other two.**
  Its load-bearing shape: the command result carries rows and error as INDEPENDENT values, because
  a cmdlet that emits rows AND reports an error proved nothing about how many objects exist --
  collapsing them lets a partial failure read as a confident single answer. Rows stay nullable so
  the null-pipeline-row guard remains reachable. The service keeps its public constructor (DI
  unchanged) and takes a FACTORY on an internal one, preserving the session-per-lookup lifetime.
  **The remaining two are harder than this one was:** both sit on live request paths and both need
  a `PSCredential` (the Delinea directory-read secret) rather than the app-pool identity, so the
  seam has to carry credentials without widening who can see them.
  **RISK, and why this is not routine test work:** these are the live authorization paths that
  decide who may modify protected mailboxes. They reached PROD on 2026-08-04 and their manual
  checks have never been run. `sidf-1` was exactly this failure mode -- a change near this code
  locked every admin out of the page needed to repair it, caught only by review.
  **Constraints agreed before stopping:** pure extraction with no behaviour change; the existing
  authorization suites must pass UNMODIFIED as the proof (editing them signals a behaviour change
  and is a stop); one commit per piece so any single step is revertible; nothing considered done
  until CI is green.

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

- **MessageTrace null-pipeline-row NRE — FIXED in repo 2026-07-29; on dev in `2.3.31`, NOT on
  prod (prod is `2.3.30`, still carrying the defect).**
  Plan `docs/MessageTraceNullRow-Plan.md` Status: Implemented. Live prod symptom: an EXO
  summary search failed with `Object reference not set to an instance of an object.` (banner
  doubled because `:348` and `:423` both format the same string). Root cause at
  `Services/MessageTraceService.cs:386`: the `Get-MessageTraceV2` pipeline returned a
  collection containing a **null element** and the loop dereferenced it; the `?.` chain guarded
  the property and its value but not `msg` itself. Latent since `b70b59d` (2026-06-04), NOT an
  MT-detail regression; data-dependent (the detail export emailed successfully the same day).
  Same defect class fixed in all four mapping loops (`:277`, `:314`, `:384`, `:501`) — every
  `GetProperty*` helper takes a non-nullable `PSObject` and dereferences `.Properties`.
  MessageTrace module `1.2.0 -> 1.2.1`, no base app bump. 830 tests green; non-vacuity proven
  per guard against the exact production exception.
  **OPEN:** live re-run of the failing search (now possible on dev, which has the fix), then
  promote to prod. **OPEN (OQ-1, non-blocking):** why EXO emits a null row at all is
  undiagnosed; the guard is correct regardless.

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
- **Deployed: dev `2.5.5`, prod `2.5.5`** -- both re-verified from the assembly 2026-08-05
  (`FileVersion 2.5.5.0`, both DLLs written 2026-08-04 23:52 and byte-identical to each other).
  Repo is level with both. `wwwroot/app.css` hashes identical across dev, prod and the repo, so
  the button-state fix and the reworked palettes are confirmed live on both instances rather than
  a stale file surviving the robocopy mirror.
  **This supersedes the earlier "dev `2.5.3`, prod `2.5.2`, prod carries the flat first-cut
  palettes" record, and it falsifies backlog item -0.9 (deploy `2.5.5` to dev): the deploy
  happened. The eyeball check of a disabled submit button is still unrun.**
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
- **2026-07-21 landed slices** (ff443ca, c2e2f6f, 502dd0e, 8c6f83f, 9dd39cd, b978362, 71d1daa)
  archived verbatim: `docs/history/state-archive.md` (Archived 2026-07-29).
- **AccountLockoutRemediation: TURNED OFF by owner** (2026-07-21). Does not work in this environment:
  WinRM reaches only ~5 of 38 domain controllers (HTTP 400 / Access denied / unreachable); permanent
  (owner: "won't be changed"). Discovery hides unreachable DCs (looks like "no lockouts found"); sweep
  silently drops the ~33 it can't reach. Owner disabled the module (runtime enablement, no code change).
- **Toolkit bug filed:** roethlar/AgentGovernanceBootstrap#7 -- completing a tracked item should
  auto-update the state record, not gate it behind an owner ask.

## Next up (prioritized)

Live backlog only. Items need an approved plan before code unless noted.

**-1. Make the remaining uncovered lines testable** (owner: *"make it work"*, 2026-08-05).
   Details, the per-file split and the risk constraints are in `## Now`. No plan needed -- the
   owner ruled it directly. This is the next code task.
   **`SectionAccessGroupDirectory` is DONE (`6642587`).** Next: `PermissionValidator` (46%), then
   `ProtectedPrincipalService` (63%) -- both on live request paths, both credential-carrying, so
   they are the harder half. Copy the `ISectionAccessDirectoryCommands` shape.

**-0.9. Eyeball a disabled submit button on dev** (accent, not blue). No plan needed -- it is the
   verification step for work already landed. **The deploy half is done:** dev and prod both run
   `2.5.5` as of 2026-08-05 (re-verified from the assembly), so only the look is outstanding.

**-0.4. PROD carries four months of unvalidated work** -- now through `2.5.5`, deployed
   2026-08-04, and its manual checks have never been run. Highest-consequence single check:
   `ANALOG\ExchangeWebAdmins` can still open Admin Settings (the `sidf-1` lockout scenario,
   hardest to recover from). See item 0 below for the consolidated list.

0. **Work through `docs/DevValidation-2.3.34.md` on dev (owner, Monday 2026-08-03).** The
   single consolidated checklist for everything that reached dev unvalidated -- four work
   streams' manual checks, ordered by consequence rather than by plan. Sections A-B are the
   protection controls and the reported L1/L2 friction; A1 (alias-addressed protected user
   is denied) is the GAP 4 regression test and must be re-run on prod after promotion.
   Nothing in it has been run. It copies no reasoning: each item cites its source plan.
1. **Live-validate the Bulk Job Runner (owner-deferred, 2026-07-20).** Runs from the dev instance
   against real PROD AD/Exchange (the only tenant there is; both instances on this server point at
   it). The runner *logic* is already covered by xUnit without a live run -- lifecycle (FIFO queue,
   cancel, recycle->Interrupted via `Initialize_FlipsOrphanedNonTerminalJobsToInterrupted`), per-row
   failure aggregation, completion notification (all variants), and the protected-principal block on
   **both** Finder and Type paths (`ConferenceRoomBulkProcessorTests`, closes GAP 3). What stays
   unvalidated until a live run: the Blazor UI (submit/progress/reconnect) and an actual EXO/AD room
   write. Do not close out until performed.
2. **Live-validate the ConferenceRooms PP gate + GM-3 self-service groups** — both landed and
   codex-reviewed; the only outstanding work on each is live/UI validation from the dev instance
   against PROD (`docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md`,
   `docs/SelfServiceGroupManagement-Plan.md`). Foldable into the same live session as item 1.
3. **Module packaging/import — DEFERRED (owner, 2026-07-22)** as low-value/high-cost. Not to be
   worked on or raised as next; no plan. End-state direction retained only as history in
   `.agents/decisions.md` (2026-07-22 deferral, refining 2026-06-29 & 06-18).
4. **AccountLockout user-notification — PARKED with the module (owner, 2026-07-22).** The whole
   `AccountLockoutRemediation` module is disabled/deferred (unusable in this environment); the
   user-notification question is parked with it and will be decided only if the module is picked
   back up. Not to be worked on or raised as next.

Landed items 2, 5 and 6 of the previous numbering (single-room Finder PP gap, GM-3 task set, ASCII
sweep + lint gate) are archived: `docs/history/state-archive.md` (Archived 2026-07-30).

Ops track (not engineering): configure ConferenceRooms AD `DelineaSecretId` in the prod instance
(gates CR-1 in prod); `deploy.ps1` native `-PlanOnly` (workaround: `deploy-pipeline -PlanOnly`).

## Blockers / open gaps

- **OPEN — AccountLockoutRemediation not yet exercised on dev** (owner deferred, 2026-06-29). Run
  the package's own Manual Validation steps (live 4740 read, WinRM, quser/logoff parsing, real
  dry-run+logoff, protected-block) when ready. Gates the rule-4 user-notify decision above.
  Note the module is currently disabled by the owner as unusable in this environment (see `## Now`).
- **All known protected-principal *coverage* gaps CLOSED (in repo):** GAP 1 (`M365GroupManagementService`,
  2026-06-29), GAP 2 (`MigrationService`, 2026-06-30), GAP 3 (ConferenceRooms Finder bulk,
  2026-07-02), and the single-room Finder page path (2026-07-21, commit 2a97d09 — consolidated
  into `ConferenceRoomProtectionGate`). Every mutating module routes through the gate. Governing
  rule: `.agents/decisions.md` 2026-06-29 + Constitution §Protected Principals. This closes
  *which callers are gated*; GAP 4 below is a defect in *what the gate resolves*.
- **GAP 4 — FIXED IN REPO 2026-07-30 (`2.3.33`) and NOW DEPLOYED to both instances** (dev and
  prod both at `2.5.5`, re-verified from the assembly 2026-08-05; the earlier "dev `2.3.32`, prod
  `2.3.30`, still live on both" basis is falsified). **The fix is unproven in the field: its
  regression check has never been run on either instance** -- see the bottom of this entry.
  The defect: protected principals were reachable by
  secondary SMTP alias (found 2026-07-30, verified against live AD).
  `ProtectedPrincipalService.ResolveViaActiveDirectory`
  (`Services/ProtectedPrincipalService.cs:290-291`) queries only
  `(|(userPrincipalName=)(mail=)(sAMAccountName=))` — never `proxyAddresses` — and
  `MatchesIdentity` (`:438-465`) does not carry aliases in its candidate set. All 4 protected
  `user` rows in prod `protected_principal` carry 3 secondary aliases each; the CEO row
  `vincent.roche@analog.com` also answers to `VRoche@O365.analog.com`,
  `VRoche@analog.mail.onmicrosoft.com`, `Vincent.Roche@exchange.analog.com`. The app's exact
  filter returns 0 matches for the alias; the same filter plus `(proxyAddresses=smtp:...)`
  returns 1.
  - **Masked in MailboxPermissions**, where 0 matches means `null` means blanket denial
    (`Services/PermissionValidator.cs:124-131`) — the denial is currently the only control.
  - **Live in ConferenceRooms and GroupManagement**, which treat `NotFound` as *not protected*
    and allow (`Services/ConferenceRoomProtectionGate.cs:56-58`,
    `Services/GroupManagementService.cs:44-50`).
  - **Binding consequence:** relaxing `NotFound` to "allow" anywhere without simultaneously
    broadening resolution converts the masked bypass into a live one. Recorded here because it
    constrains any future work in this area, not only the fix below.
  - **Fix landed** (`docs/ProtectedPrincipalResolution-Plan.md`, Implemented 2026-07-30, commits
    `6faa92d`..`0eca01e`): resolution now falls back to Exchange, which returns the canonical
    primary address, so the alias case stops resolving `NotFound`. The `NotFound`-allows rule in
    ConferenceRooms and GroupManagement is deliberately unchanged -- the bypass closes because
    the alias no longer reaches it. The AD filter was **not** broadened to `proxyAddresses` (plan
    Non-Goals: two mechanisms for one job).
  - **Regression check, not yet run:** an alias as target must be denied citing the CEO user
    rule. Required on dev, then again on prod after promotion.
- **MailboxPermissions denies cloud-only mailboxes and mail-enabled groups — FIXED IN REPO
  2026-07-30 and DEPLOYED to both instances** as of 2026-08-04 (`2.5.5`, re-verified 2026-08-05;
  the earlier "still live on prod (`2.3.30`)" basis is falsified). **Unverified in the field --
  the L1/L2 friction has not been re-tested since the deploy.** Reported by the owner
  2026-07-30 as L1/L2 support friction. 16 prod denials 2026-06-30..2026-07-30 over 7 targets; 4
  were permanent under the AD-only filter (`Jabil.support@analog.com`,
  `sporting.tickets@analog.com` cloud-only; `adspstaff@analog.com`, `globalevents@analog.com`
  mail-enabled groups), 2 were AD-sync timing and self-resolved, 1 was malformed input. Not a
  regression from 2.3.32 — the code last changed functionally 2026-05-29. Fixed by the same work
  as GAP 4; the malformed-input case now gets an accurate not-found message rather than the
  outage-sounding one. **Open (plan OQ-2, non-blocking):** whether
  `sporting.tickets@analog.com` and `Jabil.support@analog.com` should be administratively
  reachable at all, or are artifacts of an unfinished decommission. Worth an answer before
  treating the friction as fully closed.

## Verification

- Commands are owned by `.agents/repo-guidance.md` (Verification) — read them there, not here.
  Deploy-host dependency for the ops scripts: `sqlite3.exe` on PATH.
- **Non-vacuous rule:** a change shipping with a new test must be proven — revert the fix, see the
  test fail, restore. Full policy: `AGENTS.md` (Verification) and `.agents/repo-guidance.md`.
  Per-work-stream manual-check lists live in each `docs/*-Plan.md`, consolidated for the
  outstanding ones in `docs/DevValidation-2.3.34.md`.

## Findings (environment / CI — still live)

- CI is real: it fails on real problems. Trust it. (`.github/workflows/ci.yml`, `windows-latest`.)
  Note: `dotnet format --verify-no-changes` treats analyzer *warnings* as fatal, so a stray
  warning (not just a failing test) reddens build-test. This bit master 2026-07-20..07-21: the
  Bulk Job Runner (`971555f`) left an xUnit1051 warning that kept build-test red for ~13 commits
  until fixed in `8c6f83f` (2026-07-21). Lesson: run the format check locally, not just the tests.
- On local macOS, a missing Windows COM DLL can nondeterministically drop xUnit collections (totals
  vary) — trust the failure *list*, not the total. `windows-latest` CI is unaffected. macOS builds
  need `-p:EnableWindowsTargeting=true`; Pester needs `pwsh` +
  `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec`.
- On this Windows dev box, `sqlite3.exe` is on PATH via winget; Pester runs under `pwsh`.
- **RSAT IS installed on this dev box** (verified 2026-07-31: importing the `ActiveDirectory`
  module in a bare runspace succeeds with no errors), so
  `ADDirectorySearchService.IsAvailable` is **true** here and **false** on CI
  (`windows-latest`). Any test written as `if (!svc.IsAvailable) { ...assert... }` therefore
  **silently skips locally and only really runs on CI** -- it passes whether or not the code is
  correct. `ADDirectorySearchServiceTests.cs:100`, `:111`, `:121` are written that way. Assert
  AD-dependent logic through a pure function instead (see
  `ADDirectorySearchService.ClassifyOutcome`); a slice-1 non-vacuity probe caught this pattern
  passing with the fix reverted.
  - Corollary: a test that skips when a fixture is missing must use `Assert.SkipWhen`, never a
    bare early `return` -- a silent return is indistinguishable from a pass. Caught again in
    slice 3, where a live OU test searched for the literal `"OU="` expecting to match every DN
    (AD does not substring-match `distinguishedName`), got zero rows, and passed green with the
    mapping code deliberately broken. `ExchangeAdminWeb.Tests/ADDirectoryLiveTests.cs` is the
    pattern to copy: probe real name fragments to discover a fixture, skip loudly if none.
  - **SWEPT 2026-07-31: no `IsAvailable`-gated silent skips remain.** All three in
    `ADDirectorySearchServiceTests` (`:100`, `:111`, `:122`) now use `Assert.SkipWhen` and
    report as skipped here rather than passing. Note the asymmetry that remains by design:
    those three assert the fail-soft contract and so run on CI and skip on this box, while
    `ADDirectoryLiveTests` does the reverse. **Neither file alone proves the service works** --
    the pure-function tests are what hold on every host.
  - What live tests are FOR: pure functions cannot prove a PowerShell property name is right.
    `Properties["DisplayName"]` where the cmdlet returns `Name` compiles, passes every unit
    test, and yields an empty string at runtime. That class of bug needs a real directory.
- `deploy.ps1` still lacks a native `-PlanOnly` (deferred with owner visibility;
  `deploy-pipeline -PlanOnly` covers the prod dry-run requirement).

## Active sources

- `AGENTS.md` — process/behavioral contract (Prime Invariants first).
- `docs/ProjectConstitution.md` — highest engineering authority.
- `.agents/decisions.md` — durable decisions (most recent: 2026-07-31, protected-principal admin
  input validated under the app-pool identity rather than the Delinea directory-read secret).
- Active plans: `docs/BulkJobRunner-Plan.md` (Implemented, live validation pending);
  `docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md` (Implemented 2026-07-21,
  live/UI validation pending); `docs/MessageTraceDownloadLink-Plan.md` (Implemented 2026-07-29,
  all four slices landed; **on dev as `2.3.31`, 9 manual post-deploy checks not run**);
  `docs/OperatorEmailResolution-Plan.md` (**Implemented 2026-07-29** -- app `2.3.32`; now on both
  instances via `2.5.5`; 8 manual post-deploy checks not run; implementation openreview not
  obtained);
  `docs/ProtectedPrincipalResolution-Plan.md` (**Implemented 2026-07-30** -- app `2.3.33`; now on
  both instances via `2.5.5`; 6 manual post-deploy checks not run; no independent review);
  `docs/ProtectedPrincipalInputValidation-Plan.md` (**Implemented 2026-07-31**, all four slices
  landed -- the plan file says so; the "Approved, not started" reading here was stale);
  `docs/SectionAccessSidStorage-Plan.md` (**Implemented 2026-08-03**, all 4 slices landed -- the
  plan file says so; the "slice 1 of 4" reading here was stale. No open owner gates);
  `docs/CoverageRatchetRepair-Plan.md` (**Implemented 2026-08-05**, all three slices);
  `docs/AdminUIRedesign-Plan.md` (**In progress** -- manual checks unrun).
- **Plan-status drift, unresolved (flagged 2026-07-30, owner ruling needed):** three plans still
  carry a pre-landing `Status:` although code evidence says they shipped —
  `docs/BlockedSendersLoadTiming-Plan.md` (Approved; deferred load is live at
  `Components/Pages/BlockedSenders.razor:169`, module `1.0.2`), `docs/Comms10kReplaceUx-Plan.md`
  (Approved; module is at the plan's target `1.0.4`, commit `5e0c19e`), and
  `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md` (Approved -- In progress; implemented by
  `430305a`, module now `2.3.0` vs the plan's `2.0.12`). Not corrected in this sweep: marking a
  plan Implemented is a completion claim, and the ConferenceRooms one may be genuinely partial.
- Review loop finding pp-finder-1: implemented and committed (`.agents/review/index.md`).
- Review loop findings ppv-1..4 (2026-07-31): all four fixed and committed; see `## Now` and
  `.agents/review/index.md`. Dispatch artifacts (prompt, schema, raw verdict) are tracked at
  `.agents/review/ppvalidation.*` so the pass is reproducible.

## Unrecorded repo memory

- None known. Engineering rules → `docs/ProjectConstitution.md`; module contract →
  `docs/AdminModuleSpec.md`; work-stream history → `docs/*-Plan.md` + git log.

