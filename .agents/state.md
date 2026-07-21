# Agent State

First place to read for current repo state. Keep it short; update it when important repo facts
change. Resolved work lives in the plan/decision/incident docs, not here — this file records only
what is live: current versions, in-flight work, what to do next, blockers, and open gaps.

## Now

- **App version `2.3.28`** (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`).
- **Deployed:** prod is on `2.3.27`, validated good (owner, 2026-06-29). `2.3.28` (Bulk Job Runner)
  is **deployed to dev** (owner, 2026-07-20; `D:\inetpub\ExchangeAdminWebDev`) but **NOT yet
  manually validated** and **NOT in prod**. This session's commits are NOT yet deployed to dev.
- **No code change in progress.** Working tree clean; `master` pushed to both remotes and
  **CI green** (as of `71d1daa`) -- first green since 2026-07-20.
- **OPEN for next session (owner to choose):** (1) implement the Approved log-root plan
  `docs/RemoveHardcodedLogRoot-Plan.md`, or (2) dev-deploy `2.3.28` (now on green master). Neither
  started.
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
- **AccountLockoutRemediation: does NOT work in this environment.** Live-tested this session:
  WinRM reaches only ~5 of 38 domain controllers (HTTP 400 / Access denied / unreachable);
  confirmed permanent (owner: "won't be changed"). Discovery hides unreachable DCs (looks like
  "no lockouts found"); sweep silently drops the ~33 it can't reach. Owner decided the module is
  unusable here and will be turned OFF. NOT yet actioned -- decision pending: leave disabled
  (default) vs remove from catalog. See Next-up.
- **Toolkit bug filed:** roethlar/AgentGovernanceBootstrap#7 -- completing a tracked item should
  auto-update the state record, not gate it behind an owner ask.

## Last work stream — Bulk Job Runner (DONE, pending dev validation)

`docs/BulkJobRunner-Plan.md` (Status: Implemented) · `.agents/decisions.md` 2026-07-02.
App `2.3.27`→`2.3.28`; ConferenceRooms module `2.1.0`→`2.2.0`.

ConferenceRooms bulk apply (Finder/Type CSV) now runs as a durable server-side job (separate
`config/exchangeadmin-jobs.db`, never promoted). Self-pumping singleton runner (not a hosted timer);
single active job + FIFO queue; startup flips non-terminal jobs to Interrupted (no resume); always
cancellable; per-row failure aggregation; completion email fires from the job. Off-circuit auth =
option (a) (capture the authorization decision at submit, re-check per row via shared pure
`GroupMembershipChecker`). Protected-principal gate enforced in-job per row on **both** Finder and
Type bulk paths (closes GAP 3). Deploy scripts warn (not block) on active jobs before recycle
(`tools/JobStateWarning.psm1`). ~671 xUnit + 65 Pester green (as of `9d26b5f`); build/format/diff-check
clean; each slice codex-reviewed with findings fixed before commit.

**Next action:** run manual validation on dev (below) — the UI and end-to-end job lifecycle are not
covered by automated tests. (Dev deploy done 2026-07-20.)

## Next up (prioritized)

Live backlog only. Items need an approved plan before code unless noted.

0. **Implement log-root fail-fast** -- `docs/RemoveHardcodedLogRoot-Plan.md` (Status: Approved).
   Remove hardcoded `E:\WWWOutput` from `ExtendedLogService`/`JsonlLogService`/
   `EmergencyDisableService`; add a startup guard that fails to start if `Audit:LogRoot` is unset.
   Ready to implement; owner to confirm doing it now vs later.
0b. **Turn off AccountLockoutRemediation** -- owner decided it is unusable here (WinRM reaches ~5
   of 38 DCs, permanent). Decision pending: leave disabled (default; reversible) vs remove from
   catalog (code change, needs a small plan). Not started.

1. **Manual-validate the Bulk Job Runner on dev — DEFERRED (owner, 2026-07-20).** No test data and
   no dev/QA tenant for AD or Exchange, so a real end-to-end run is not possible here. The runner
   *logic* is already covered by xUnit without a live tenant — lifecycle (FIFO queue, cancel,
   recycle→Interrupted via `Initialize_FlipsOrphanedNonTerminalJobsToInterrupted`), per-row failure
   aggregation, completion notification (all variants), and the protected-principal block on **both**
   Finder and Type paths (`ConferenceRoomBulkProcessorTests`, closes GAP 3). What stays unvalidated
   until a real tenant run: the Blazor UI (submit/progress/reconnect) and an actual EXO/AD room
   write. Revisit when a controlled prod run is possible; do not close out until then.
2. **Single-room Finder protected-principal gap** — **DONE** (2026-07-21, commit 2a97d09;
   `docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md` Implemented). Consolidated the
   module PP check into one `ConferenceRoomProtectionGate` (C2-G). Only remaining follow-up is
   live-tenant/UI validation (deferred, no dev tenant).
3. **Module packaging/import** — needs `docs/ModulePackaging-Plan.md` written + approved. End state
   (owner, 2026-06-29): UI `.zip` upload, no full rebuild; precompiled-vs-runtime still open. First
   leg = module contract / self-registration seam. See `.agents/decisions.md` 2026-06-18 & 06-29.
4. **AccountLockout user-notification** — OPEN, gated on real testing: decide whether a logged-off
   user is notified, after the module is actually exercised on dev (`.agents/decisions.md`
   2026-06-30).
5. **GM-3 self-service group management** — needs its own plan; depends on M365 work (done).
6. **ASCII cleanup sweep + enforcement lint** -- **DONE** (2026-07-21). Scope narrowed by owner to
   code/logging only (`.cs`/`.ps1`/`.psm1`); docs, `.razor` UI, and `EmailService.cs` email emoji
   excluded. (a) Sweep landed commit `c2e2f6f` (329/329 char swaps, 77 files, 672 tests green).
   (b) CI gate `tools/Test-AsciiOnly.ps1` wired into `.github/workflows/ci.yml` `powershell` job,
   non-vacuity proven. See `.agents/decisions.md` 2026-07-21.

Ops track (not engineering): configure ConferenceRooms AD `DelineaSecretId` in the prod instance
(gates CR-1 in prod); `deploy.ps1` native `-PlanOnly` (workaround: `deploy-pipeline -PlanOnly`).

## Blockers / open gaps

- **None blocking current work.**
- **CLOSED (2026-07-21, commit 2a97d09) — single-room Finder protected-principal gap.** The
  single-room Room Finder page path (`ConferenceRooms.razor` `SetupSingleRoom` →
  `SetRoomMetadataAndListAsync`) previously wrote with no PP gate. Fixed by consolidating the
  module's protected-principal check into one `ConferenceRoomProtectionGate` (C2-G
  guarded-execution helper): page Finder+Type and each bulk row route through `GuardThenRunAsync`;
  the write runs only when the gate clears; the two prior near-duplicate inline checks were removed.
  672 tests pass; non-vacuity verified. Plan Implemented
  (`docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md`). **Live-tenant/UI validation not
  run** (no dev tenant) — deferred to a future controlled run, same as the Bulk Job Runner.
- **OPEN — AccountLockoutRemediation not yet exercised on dev** (owner deferred, 2026-06-29). Run
  the package's own Manual Validation steps (live 4740 read, WinRM, quser/logoff parsing, real
  dry-run+logoff, protected-block) when ready. Gates the rule-3 user-notify decision above.
- **Prod BlockedSenders version uncertainty:** the two BlockedSenders fixes (`17910f3`→1.0.1,
  `cde778f`→1.0.2) are module bumps, not app bumps, so "prod = app 2.3.27" does not confirm prod
  includes them. Confirm the prod build commit if BlockedSenders behaviour matters in prod.
- **All known protected-principal gaps CLOSED:** GAP 1 (`M365GroupManagementService`, 2026-06-29),
  GAP 2 (`MigrationService`, 2026-06-30), GAP 3 (ConferenceRooms Finder bulk, 2026-07-02), and the
  single-room Finder page path (2026-07-21, commit 2a97d09 — consolidated into
  `ConferenceRoomProtectionGate`). No known open PP gap remains. Governing rule:
  `.agents/decisions.md` 2026-06-29 + Constitution §Protected Principals.

## Verification

- **Code:** `dotnet build ExchangeAdminWeb.slnx -c Release` then `dotnet test ExchangeAdminWeb.slnx`
  (always target the `.slnx`; bare `dotnet test` runs zero tests). Add
  `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore` and
  `git diff --check HEAD` where practical.
- **PowerShell:** `Invoke-ScriptAnalyzer -Path . -Recurse` (CI fails on Error severity only) and
  `Invoke-Pester tests/ps`. Deploy-host dependency for the ops scripts: `sqlite3.exe` on PATH.
- **Non-vacuous rule:** a change shipping with a new test must be proven — revert the fix, see the
  test fail, restore. Full policy + manual-check list: `.agents/repo-map.json`, `AGENTS.md`.

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
- `deploy.ps1` still lacks a native `-PlanOnly` (deferred with owner visibility;
  `deploy-pipeline -PlanOnly` covers the prod dry-run requirement).

## Active sources

- `AGENTS.md` — process/behavioral contract (Prime Invariants first).
- `docs/ProjectConstitution.md` — highest engineering authority.
- `.agents/decisions.md` — durable decisions (most recent: Bulk Job Runner, 2026-07-02).
- `.agents/repo-map.json` — automated verification map.
- Active plans: `docs/BulkJobRunner-Plan.md` (Implemented, pending dev validation);
  `docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md` (Implemented 2026-07-21, pending
  live-tenant validation). No plan is currently `In progress`.
- Review loop finding pp-finder-1: implemented and committed (`.agents/review/index.md`).

## Unrecorded repo memory

- None known. Engineering rules → `docs/ProjectConstitution.md`; module contract →
  `docs/AdminModuleSpec.md`; work-stream history → `docs/*-Plan.md` + git log.
