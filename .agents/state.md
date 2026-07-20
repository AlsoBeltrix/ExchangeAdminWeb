# Agent State

First place to read for current repo state. Keep it short; update it when important repo facts
change. Resolved work lives in the plan/decision/incident docs, not here — this file records only
what is live: current versions, in-flight work, what to do next, blockers, and open gaps.

## Now

- **App version `2.3.28`** (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`).
- **Deployed:** dev and prod were both on `2.3.27`, validated good (owner, 2026-06-29). `2.3.28`
  (Bulk Job Runner) is **built and committed but NOT yet deployed** and **NOT manually validated**.
- **No code change is in progress.** The last work stream (Bulk Job Runner) is complete pending
  manual validation.

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

**Next action:** deploy `2.3.28` to dev and run manual validation (below) — the UI and end-to-end
job lifecycle are not covered by automated tests.

## Next up (prioritized)

Live backlog only. Items need an approved plan before code unless noted.

1. **Manual-validate the Bulk Job Runner on dev** (no plan needed — it's validation). Cover:
   submit a bulk CSV → close the tab → reopen and see the job still running/finished; recycle the
   app pool mid-job → job shows Interrupted (not stuck Running); cancel a running job; queue a
   second job while one runs; confirm the completion admin email; confirm a protected room is
   blocked on **both** Finder and Type bulk paths.
2. **Single-room Finder protected-principal gap** — OPEN, small, needs owner go (see Blockers /
   Open gaps). One-line fix, but outside the Bulk Job Runner's approved scope.
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

Ops track (not engineering): configure ConferenceRooms AD `DelineaSecretId` in the prod instance
(gates CR-1 in prod); `deploy.ps1` native `-PlanOnly` (workaround: `deploy-pipeline -PlanOnly`).

## Blockers / open gaps

- **None blocking current work.**
- **OPEN — single-room Finder has no protected-principal check (found 2026-07-02).** The
  single-room Room Finder page path (`ConferenceRooms.razor` `SetupSingleRoom` →
  `SetRoomMetadataAndListAsync`) goes straight from `ReauthorizeAsync` to the write with no PP
  gate. Single-room **Type** does gate. Same class as the now-closed GAP 3, but outside the Bulk
  Job Runner's (bulk-only) approved scope, so flagged not fixed. Fix = add the existing page-local
  `CheckProtectedPrincipalAsync` before the write. Low practical risk (rooms are non-person
  mailboxes rarely protected). Needs a one-line owner go.
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
- **All protected-principal sweep gaps CLOSED for the bulk/module surfaces:** GAP 1
  (`M365GroupManagementService`, 2026-06-29), GAP 2 (`MigrationService`, 2026-06-30), GAP 3
  (ConferenceRooms Finder bulk, 2026-07-02). The only remaining PP gap is the single-room Finder
  path above. Governing rule: `.agents/decisions.md` 2026-06-29 + Constitution §Protected
  Principals.

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
- Active plans: `docs/BulkJobRunner-Plan.md` (Implemented, pending dev validation). No plan is
  currently `In progress`.

## Unrecorded repo memory

- None known. Engineering rules → `docs/ProjectConstitution.md`; module contract →
  `docs/AdminModuleSpec.md`; work-stream history → `docs/*-Plan.md` + git log.
