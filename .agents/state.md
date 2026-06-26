# Agent State

First place to read for current repo state. Keep it short; update it when important
repo facts change. Resolved work lives in the plan/decision/incident docs, not here.

## Now

- App version `2.3.25` (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`).
- **Final whole-branch review DONE (2026-06-26, app 2.3.24→2.3.25).** SDD review of the
  whole `e8b155c~1..HEAD` range (3 streams: EXO retry, SQLite store, guide+validator),
  one `reviewer` subagent per stream + cross-cutting pass. All three verdicts SHIP; build
  clean, format clean, `git diff --check` clean. One security-relevant finding fixed before
  done (owner: "fix before done"):
  - **Fail-closed parity fix (commits `cb4b984`, `eed13f4`):** during the one-time
    JSON→SQLite legacy import, `SectionAccessService` and `ModuleEnablementService` failed
    *open* when the DB write threw (e.g. SQLite busy) — they fell through to the permissive
    appsettings/`EnabledByDefault` path instead of denying. The two sibling authorization
    stores (`ProtectedPrincipal`, `ADAttributeEditor`) already guarded this. Wrapped both
    `ImportIfMissing` calls so a DB-write throw fails closed and leaves the legacy file for
    the next startup to retry. Two new tests, each proven non-vacuous. 521/521 xUnit green.
  - **Deferred review nits — DONE (commit `940a125`):** `AdminModuleSpec.md` section-access
    location corrected to each module's config page (`/module-config/{ModuleId}`) +
    "regardless of stored state" DB-era wording; `IConfigStore.cs`/`ConfigChangeToken.cs`
    comments corrected to state the change token is advisory and NOT consulted by the
    TTL-caching readers (they accept ≤30s staleness, plan-permitted). Wiring the readers to
    the token remains an unscheduled future option, not a defect.
- **PowerShell 5.1 ASCII fix for ops scripts (commit `46acddc`, 2026-06-26).** The SQLite
  Phase D deploy scripts had em dashes (U+2014) / section signs (U+00A7) in comments/strings
  and are UTF-8 *without BOM*. Windows PowerShell 5.1 (required by `deploy.ps1` — the IIS
  `WebAdministration` provider won't load under PS7) reads BOM-less files as ANSI, mangling
  those chars into cascading parse errors; PS7 (UTF-8 default) was unaffected, so it stayed
  latent until the first 5.1 dev deploy this session. Fixed six files: `deploy.ps1`,
  `tools/SqliteConfigBackup.psm1` (imported by deploy.ps1 — would have broken the deploy even
  after fixing deploy.ps1 alone), `tools/promote-dev-to-prod.ps1` (prod promote),
  `tools/Install-ExchangeAdminWeb.ps1`, and two Pester files — all now pure ASCII, verified
  parsing under a simulated 5.1 ANSI read; Pester 59/59 green. **This bug would also have hit
  the prod cutover** (prod on 2.3.11 has never run these scripts); now cleared.
- **CR-BUG-1 (EXO pool dead-runspace) FIXED** (`docs/ExoDeadConnectionRetry-Plan.md`,
  Status: *Implemented*, app 2.3.23→2.3.24). The pool auto-retries a dead EXO session once on
  a fresh borrow, gated to read-only + single-write ops (opt-in `allowRetry`, default off);
  the 7 multi-write delegates keep discard-and-fail so a committed write is never repeated.
  All 10 pool callers route through one `ExoConnectionPool.RunWithRetryAsync` helper (incl.
  `PermissionValidator`).
  - **Review #2 (2026-06-26, app 2.3.24):** retry trigger narrowed — DISCARD on any
    connection error (broad), but RETRY only on the pre-cmdlet "must call Connect-ExchangeOnline"
    signature (`IsRetriablePrecheckError`), so a single write whose session drops *after*
    Exchange accepted it is never re-submitted. Also excluded git-ignored `_not_for_github\`
    from csproj compile globs (was breaking local builds; not part of the bug fix).
- **SQLite config store work stream COMPLETE** (`docs/SqliteConfigStore-Plan.md`, Status:
  *Implemented*). All Phases A–E done (app 2.3.21, 2026-06-24). 508/508 tests green.
  - Phase E note: all service test rewrites were completed inline during Phases B-D;
    Phase E delivered the docs sweep (Constitution, AGENTS.md, AdminModuleSpec.md,
    example JSON retired) and version bump to 2.3.21.

- **Module Developer Guide Rewrite COMPLETE** (`docs/ModuleDeveloperGuideRewrite-Plan.md`,
  Status: *Implemented*, app 2.3.22, 2026-06-25). Re-verified + rewrote
  `docs/AdminModuleDeveloperGuide.md` and `docs/AdminModuleSpec.md` against the SQLite-era
  codebase, and added `<ModuleVersion />` validator enforcement (Error `PAGE009` in
  `tools/validate-module-package.ps1` + execution test `tests/ps/ValidatorChecks.Tests.ps1`,
  proven non-vacuous). A Codex review pass was folded in (one-pass, owner-approved): Graph
  credential key corrected to `GraphDelineaSecretId` in spec + guide, guide host baseline →
  2.3.22, this state block repaired. 508/508 xUnit + 59/59 Pester green.
  - Final whole-branch review now DONE (see top of Now). Branch is review-complete; do not
    push prod yet — see Blockers.

## Blockers

- None blocking current work.
- **Deferred (owner direction 2026-06-18):** prod deploy of the SQLite-era build is held
  until the work queue clears — do not push to prod until then. Sub-TODO that gates CR-1
  in prod: configure the ConferenceRooms AD `DelineaSecretId` in the deployed instance.
- **Deployed versions (confirmed by owner 2026-06-26):** dev is now on **`2.3.25`**
  (deployed this session, after the PS 5.1 fix below); prod is on **`2.3.11`** — entirely
  pre-SQLite, so its eventual cutover to 2.3.25 will run the FULL JSON→SQLite legacy import
  in one shot on first startup (the path the fail-closed parity fix hardens). Still
  re-confirm on the box immediately before any prod deploy.

## Verification

- Code: `dotnet build ExchangeAdminWeb.slnx -c Release` then
  `dotnet test ExchangeAdminWeb.slnx`. Add `dotnet format ExchangeAdminWeb.csproj
  --verify-no-changes --no-restore` and `git diff --check HEAD` where practical.
  (Always target the `.slnx`; bare `dotnet test` runs zero tests.)
- PowerShell: `Invoke-ScriptAnalyzer -Path . -Recurse` and `Invoke-Pester tests/ps`.
  Deploy-host dependency for the ops scripts: `sqlite3.exe` on PATH.
- When a change ships with a new test, prove it non-vacuous (revert the fix, see the test
  fail, restore). Full policy + manual-check list: `.agents/repo-map.json`, `AGENTS.md`.

## Findings (environment / CI — still live)

- CI is real: a failing test fails the run. Trust it.
- On local macOS, a missing Windows COM DLL can nondeterministically drop xUnit
  collections (totals vary) — trust the failure *list*, not the total. `windows-latest`
  CI is unaffected. macOS builds need `-p:EnableWindowsTargeting=true`; Pester needs
  `pwsh` + `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec`.
- `deploy.ps1` still lacks a native `-PlanOnly` (deferred with owner visibility;
  `deploy-pipeline -PlanOnly` covers the prod dry-run requirement).

## Known issues (pre-existing, NOT SQLite-caused)

- **MFA Reset stranded legacy config key.** The Graph Delinea secret was renamed
  `DelineaSecretId` → `GraphDelineaSecretId` in the catalog. The `ModuleConfig` page only
  renders the new key, but `MfaResetService` reads `GraphDelineaSecretId ?? DelineaSecretId`.
  Environments configured before the rename hold the value under the OLD key, so the page
  shows blank while the service still works via fallback. Confirmed blank in prod
  (pre-SQLite) and dev → pre-existing. The SQLite import copies every key verbatim, so it
  neither fixes nor worsens it. Proper fix (deferred, not requested): one-time key rename
  then retire the service-side fallback.

## Queued work (forward-looking — no other doc home)

These have no plan doc yet; do not start without the noted plan/approval.

- **Module packaging/import.** Direction set 2026-06-18 (`.agents/decisions.md`): `.zip`
  package + validator, rebuild-to-install, runtime upload deferred. Needs
  `docs/ModulePackaging-Plan.md` written + approved before any implementation.
- **GM-1 (bug): GroupManagement search too fuzzy.** Exact group name (e.g. "IAM") returns
  dozens of fuzzy matches. Tighten on-prem AD group search so exact/near-exact ranks first.
  Search path not yet code-located. Confirm desired ranking with owner before implementing.
- **GM-2 (bug): M365 group management finds no groups at all.** Root cause unknown (Graph
  query / auth-scope / result mapping). Investigate before any fix; treat as a real defect.
- **GM-3 (new module, needs own plan — DECIDE LATER): self-service group management.**
  Owner direction 2026-06-17, plan separately (`docs/SelfServiceGroupManagement-Plan.md`),
  nothing built until approved. Key requirements: likely a separate module; do NOT preload
  the user's manageable groups (explicit "show groups I manage" button with a slow-load
  warning); search restricted to groups the user manages with GM-1 fixes applied; reject
  any modification to non-managed groups at the service/authorization layer (UI hiding is
  not security). Open: how "manages" is determined, on-prem vs M365 vs both, making the
  lookup tolerable. Depends on GM-1/GM-2 being understood first.

## Recently completed (pointers only — full detail in the named docs)

- **CR-BUG-1 EXO pool dead-runspace auto-retry** — `docs/ExoDeadConnectionRetry-Plan.md`
  (Implemented, app 2.3.23, commit `39ce87a`).
- **SQLite Phases A–D** — `docs/SqliteConfigStore-Plan.md` + git log (`e8b155c`..`cf837e8`).
- **ProdReadiness work stream** — COMPLETE, plan Status **Implemented**
  (`docs/ProdReadiness-Plan.md` §10 round 17, `a5ab6aa`); all AC1–AC16 met.
- **2026-06-12 dev config-loss incident** — Remediated
  (`docs/Incident-2026-06-12-DevConfigLoss.md`); real cause `f7df81a` FailClosed on a dev
  box lacking `sectionaccess.json`. Much of the deploy-hardening it produced is
  obsoleted-by-design by the SQLite store.
- **TestAccountPool module removed** (app 2.3.10), **Conference Rooms** RoomListOU removal
  + partial-apply reporting (`8d4f0d6`, module 2.0.10) — decisions in `.agents/decisions.md`.

## Active Sources

- `AGENTS.md`
- `docs/ProjectConstitution.md` (highest engineering authority)
- `docs/SqliteConfigStore-Plan.md` (active work stream)
- `.agents/decisions.md`
- `.agents/repo-map.json`

## Unrecorded Repo Memory

- None known. Engineering rules: `docs/ProjectConstitution.md`; module contract:
  `docs/AdminModuleSpec.md`; work-stream history: `docs/*-Plan.md`.
