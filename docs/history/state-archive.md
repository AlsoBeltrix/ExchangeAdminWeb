# State Archive

Landed and superseded `## Now` entries rotated out of `.agents/state.md` by the
`catchup` hygiene sweep, kept verbatim for history. Newest first.

> **Terminology correction (2026-07-28):** the archived text below repeatedly says
> "manual-validation-on-dev / no dev tenant." That wording is wrong and is retained only
> as verbatim history. There is no separate dev tenant by design: both the dev and prod
> instances run on this server and connect to the same live PROD AD/Exchange. The dev
> deploy proves the app loads; functional validation is done against real PROD data and
> can be run from the dev instance. Read every "no dev tenant" below as "live validation
> not yet performed."

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
