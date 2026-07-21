# Agent State

First place to read for current repo state. Keep it short; update it when important repo facts
change. Resolved work lives in the plan/decision/incident docs, not here — this file records only
what is live: current versions, in-flight work, what to do next, blockers, and open gaps.

## Now

- **App version `2.3.28`** (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`).
- **Deployed:** prod is on `2.3.27`, validated good (owner, 2026-06-29). `2.3.28` (Bulk Job Runner)
  is **deployed to dev** (owner, 2026-07-20; `D:\inetpub\ExchangeAdminWebDev`) but **NOT yet
  manually validated** and **NOT in prod**.
- **No code change is in progress.**
- **Last landed (2026-07-21):** single-room Finder protected-principal gap CLOSED — consolidated the
  ConferenceRooms PP check into one `ConferenceRoomProtectionGate` (C2-G guarded-execution helper);
  page Finder/Type + each bulk row route through it; module `2.2.0`→`2.3.0`; 672 tests pass,
  non-vacuity verified. Commits `2a97d09` (fix) + `747f7d6` (version/docs). Plan Implemented
  (`docs/ConferenceRoomsFinderProtectedPrincipalGate-Plan.md`). Live-tenant/UI validation NOT run
  (no dev tenant). Also: pure-ASCII rule adopted repo-wide (`9592f5d`/`550cfa4`) — cleanup+lint
  deferred (Next up #7).
- **9 commits unpushed on `master`** (from `8938da2` through `550cfa4`). No push done — needs a go.
- Bulk Job Runner (`2.3.28`) still deployed to dev, manual validation still deferred (Next up #1).
- Deploy earlier required a fix: `tools/JobStateWarning.psm1` em-dashes with no BOM broke the 5.1
  deploy import (Known Failure Class #6); fixed to pure ASCII in `8938da2`.
- **Untracked:** `.agents/review/*.prompt.txt|schema.json|result.json` (11 codex dispatch scratch
  files) left uncommitted pending owner commit-vs-clean decision.

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
4. **Versioning-rule fix** (docs-only; see Blockers) — record a `decision` that new modules do not
   bump the base app version, then fix Constitution §Deployment And Versioning + AGENTS.md #6.
   Tied to the module-packaging end state.
5. **AccountLockout user-notification** — OPEN, gated on real testing: decide whether a logged-off
   user is notified, after the module is actually exercised on dev (`.agents/decisions.md`
   2026-06-30).
6. **GM-3 self-service group management** — needs its own plan; depends on M365 work (done).
7. **ASCII cleanup sweep + enforcement lint** — deferred until things settle (owner, 2026-07-21).
   Two parts, one work stream:
   (a) Replace all existing non-ASCII characters (em-dashes, smart quotes, etc.) across the codebase
   with ASCII, per the 2026-07-21 pure-ASCII decision (`.agents/decisions.md`). Not a freebie: some
   are in audit strings that tests assert on, so audit-log text moves — update those tests in the
   same change. Find candidates with a non-ASCII byte scan across tracked files.
   (b) Add a CI/lint gate (owner, 2026-07-21) so the rule bites instead of rotting: a non-ASCII byte
   scan over tracked source that fails the build on any hit. Add it to `.github/workflows/ci.yml`
   (and, where practical, a local pre-check). Land the lint only AFTER the sweep is clean, or CI
   goes red immediately. Its own small plan + commit(s).

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
- **OPEN — versioning rule is wrong for new modules (owner, 2026-06-26; not yet fixed).** The rule
  (Constitution §Deployment And Versioning; AGENTS.md #6) bumps the base app version for any
  shared/app-wide change; owner: adding a *new module* should not bump the base app version — only
  the module's own `Version` moves. Ties to the deferred runtime-upload capability
  (`.agents/decisions.md` 2026-06-18). When actioned: record a `decision` and fix the Constitution
  + AGENTS.md #6 wording. Owner said "address later."
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

- CI is real: a failing test fails the run. Trust it. (`.github/workflows/ci.yml`, `windows-latest`.)
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
