# Agent State

First place to read for current repo state. Keep it short; update it when important
repo facts change. Resolved work lives in the plan/decision/incident docs, not here.

## Now

- App version `2.3.27` (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`).
- **Notifications now mandatory (decision 2026-06-29, docs-only).** Every mutating action
  → admin notification; every security-sensitive read → admin alert; every permission/access
  change → also notify the affected user. Always via the shared `Services/EmailService.cs`
  (no bespoke mailers). Canonical rule: `docs/ProjectConstitution.md` §Auditing And Tracing →
  Notifications; Guide/Spec point to it. `.agents/decisions.md` 2026-06-29 has the full record
  and names the superseded discretionary guidance. **Not yet enforced in code** — existing
  modules predate the rule and have not been audited for compliance; this was a guidance change
  only. A future task: sweep modules for missing notifications and consider validator coverage.
- **EmergencyDisable synced-user fix DONE (2026-06-29, commit `c1c80a3`; module 1.0.4→1.0.5,
  app version unchanged; `docs/EmergencyDisableSyncedUser-Plan.md`, Implemented).** For users
  synced from on-prem AD, `accountEnabled` is on-prem mastered and Entra rejects a direct Graph
  PATCH; the module already disables the AD master + revokes Entra sessions but then attempted
  the doomed PATCH and marked the whole op failed. Now reads `onPremisesSyncEnabled` in the
  existing pre-read; synced users skip the PATCH and record the Entra-disable step as SKIPPED
  (not failed); overall success accepts OK or SKIPPED for that step. Cloud-only users unchanged.
  Also added `GraphTokenClient.PatchWithStatusAsync` (status + sanitized error.code/message, no
  tokens/raw bodies) to surface the real Graph error on a genuine PATCH failure; `PatchAsync`
  kept as wrapper. +13 tests, non-vacuous verified.
- **BlockedSenders module added DONE (2026-06-29, commit `5e4172e`; module 1.0.0, app version
  unchanged — new-module bump deferred per owner; `docs/BlockedSenders-Plan.md`, Implemented).**
  New cloud-only module: view EXO blocked senders (`Get-BlockedSenderAddress`) and unblock one
  by address (`Remove-BlockedSenderAddress`). Rides the shared EXO pool via `ExchangeServiceBase`,
  `DependsOn ExchangeOnline`, no module credential. Split permissions: `BlockedSenders` (view,
  fail-closed) + `BlockedSendersUnblock` (write, fail-closed, re-checked before the cmdlet).
  Audit on list/unblock/denied, operation trace on the write, admin email notification on
  success/failure. Ticket + explicit confirmation required before unblock. **Catalog now 22
  modules / 31 configurable aliases** (was 21 / 29). 553/553 green (14 new), non-vacuous verified.
- **AccountLockoutRemediation module incorporated DONE (2026-06-26, app 2.3.26→2.3.27;
  `docs/AccountLockoutRemediation-Incorporation-Plan.md`, Status: Implemented; commits
  `0ca909a`, `2550c55`, + docs/version slice).** The validated package was spliced into the
  host tree (page/service/models/test/doc moved, catalog descriptor at SortOrder 780, DI
  line, catalog count tests 20→21 modules / 27→29 configurable aliases). Two compile errors
  the package shape-validator does NOT catch were fixed during incorporation (CS0136
  method-vs-block `message` collision → renamed `summary`; CS8030/CS9174 collection-expr
  ternary in `DiscoverPdcAsync` → `Array.Empty<string>()`). Added 10 service unit tests
  (throttle clamp proven non-vacuous; guard paths). Module ships at 1.0.0; 22 modules total
  now. 539/539 green; build/format/diff-check clean. **Manual validation deferred to dev
  deploy** (live 4740 read, WinRM, quser/logoff parsing, real dry-run+logoff, protected-block)
  — run the package's own Manual Validation steps. Staged copy under
  `_not_for_github/example_scripts/AccountLockoutRemediation/` left in place (gitignored).
- **Graph secret key migration DONE (2026-06-26, app 2.3.25→2.3.26;
  `docs/GraphSecretKeyMigration-Plan.md`, Status: Implemented; commits `2eb9c98`,
  `063964e`).** Resolves the "MFA Reset stranded config key" issue and the identical latent
  bug in two sibling Graph modules. Catalog-driven, idempotent startup migration
  (`ModuleConfigService.MigrateGraphSecretKeys()`, run in `Program.cs` after seeding) moves
  any value stranded under the renamed old key `DelineaSecretId` to `GraphDelineaSecretId`
  for every module that declares the new key (MfaReset, NamedLocations, M365GroupManagement,
  EmergencyDisable); on-prem modules using `DelineaSecretId` as their current key are
  untouched. The `?? DelineaSecretId` service fallback was then removed from all six read
  sites (MfaReset/NamedLocations/M365GroupManagement). Module versions →1.0.3 for the three
  changed; EmergencyDisable unchanged. 6 new tests, all proven non-vacuous; 527/527 green;
  build/format/diff-check clean. **Deployed to dev and verified by owner 2026-06-26:** the
  recovered Secret ID shows correctly on the module config page. Fully closed.
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

## Next up (prioritized — owner-ranked 2026-06-26)

Priority order for the open backlog. All items need an approved plan before code. Full
detail in the sections below.

1. **M365 member/owner management — DONE 2026-06-29** (module 1.1.0; commits `211c6eb`
   service+tests, `03c443a` UI; `docs/M365MemberOwnerManagement-Plan.md`, Implemented).
   Full add/remove of members and owners via Graph, each gated through the protected-principal
   check (fail closed) before any write. Admin notification only — no affected-user
   notification (decisions.md 2026-06-29). Closes GAP 1 principal-write surface. 578/578 green.
   **Manual validation deferred to next dev deploy** (real add/remove member+owner, protected
   refusal, admin email, audit rows).
2. **GM-1** — GroupManagement search too fuzzy (degraded; tighten exact/near-exact ranking).
   Now top of the active backlog. See Queued work.
3. **Module packaging/import** — needs `docs/ModulePackaging-Plan.md` written + approved.
4. **GM-3** self-service group management — needs own plan; depends on GM-1 + M365 work first.

Done 2026-06-26: **MFA Reset stranded config key** (`docs/GraphSecretKeyMigration-Plan.md`)
and **AccountLockoutRemediation module incorporation**
(`docs/AccountLockoutRemediation-Incorporation-Plan.md`) — see the Now section. The latter
still has manual validation pending its next dev deploy.

Separate track (gated by the prod-deploy hold, not engineering): ConferenceRooms AD
`DelineaSecretId` in prod (gates CR-1); `deploy.ps1` native `-PlanOnly` (workaround exists).

## Blockers

- None blocking current work.
- **OPEN — versioning rule is wrong for new modules (owner, 2026-06-26; not yet fixed).**
  Current rule (Constitution §Deployment And Versioning; AGENTS.md invariant #6) bumps the
  base app version for any "shared/app-wide" change, and this session bumped 2.3.26→2.3.27
  for *adding* the AccountLockoutRemediation module. Owner: **bumping the base app version
  for every new module is wrong.** End-state intent: modules distributed as `.zip` and
  uploaded via the web UI — installing a module must NOT require a recompile or an app
  version bump (only the module's own `Version` moves). This is the deferred
  runtime-upload/dynamic-load capability from `.agents/decisions.md` 2026-06-18; the
  versioning rule must change in step with it. Not yet actioned (owner: "address later").
  When actioned: record a `decision` ("new modules do not bump base app version") and fix
  the Constitution + AGENTS.md #6 wording. The 2.3.27 bump is already committed (`3e84d50`).
- **RESOLVED 2026-06-29 — protected principals are off-limits to every mutating module, no
  carve-outs.** (Recorded in `.agents/decisions.md` 2026-06-29; Constitution §Protected
  Principals updated with an explicit bullet.) The earlier owner position ("protection is only
  about granting permissions, not routine group management") was reversed: the end state is that
  no module may mutate a protected principal — account state, permissions, group membership,
  directory attributes, anything — across EmergencyDisable, AD Attribute Editor, Group
  Management, and the planned M365 member/owner feature. The guard binds to the *target* of the
  write; it must refuse, fail closed, and audit the denial. The on-prem `GroupManagementService`
  `CheckProtectedAsync` gate is therefore correct and stays. **Follow-up (read-only sweep,
  pending):** verify every mutating module actually routes its target through the
  protected-principal check before writing — confirm there is no module that writes without
  gating. Findings go below; any gap becomes its own planned fix.
  - **Sweep DONE 2026-06-29 (read-only audit; two gaps found, both verified in source).** Of
    14 mutating modules, 12 gate the write target through the protected-principal check
    (`ProtectedPrincipalService.CheckAsync` / `PermissionValidator.ValidateTargetMailboxAsync`
    / a local `CheckProtectedAsync`): EmergencyDisable, ADAttributeEditor (+Undo),
    GroupManagement, AccountLockoutRemediation, ConferenceRooms, LicensingUpdates, MfaReset,
    OutOfOffice, MailboxPermissions, CalendarPermissions, Comms10k. BlockedSenders targets a
    sender address (not a principal) — N/A.
  - **GAP 1 — `M365GroupManagementService` — CLOSED 2026-06-29 (principal-write surface).**
    The M365 member/owner management feature shipped (module 1.1.0; commits `211c6eb`,
    `03c443a`; `docs/M365MemberOwnerManagement-Plan.md`, Implemented). Member/owner add/remove
    now routes the target identity through an in-service protected-principal gate
    (`CheckProtectedAsync` → `ProtectedPrincipalService.CheckAsync`, fail closed on
    Unavailable/Ambiguous/CheckFailed) before any Graph write, mirroring GroupManagement.
    Group create/update/delete remain ungated **by design** (owner decision 2026-06-29:
    member/owner only, no protected-*group* gating; see `.agents/decisions.md`). Known
    accepted limitation: AD-based resolution treats a cloud-only NotFound as not protected.
  - **GAP 2 — `MigrationService` is UNGATED.** No protected-principal reference in the service
    or `Components/Pages/Migration.razor` (grep: zero matches). `New-MigrationBatch`
    (`Services/MigrationService.cs` ~:267/:277, both ToCloud and ToOnPrem) creates a batch over
    target mailboxes with no protected-principal validation on the batch members.
  - **Both gaps need an approved plan before any code** (mutating-module changes). Neither is
    yet scheduled; M365 gating folds naturally into the M365 member/owner plan, Migration is a
    standalone fix.
- **Deferred (owner direction 2026-06-18):** prod deploy of the SQLite-era build is held
  until the work queue clears — do not push to prod until then. Sub-TODO that gates CR-1
  in prod: configure the ConferenceRooms AD `DelineaSecretId` in the deployed instance.
- **Deployed versions (confirmed by owner 2026-06-26):** dev is *deployed* on **`2.3.26`**
  (Graph secret key migration ran; recovered Secret ID verified on the config page). **`2.3.27`**
  (AccountLockoutRemediation module) is built and committed but NOT yet deployed — deploy it
  to dev to run the module's manual validation. Prod is on **`2.3.11`** — entirely pre-SQLite,
  so its eventual cutover will run the FULL JSON→SQLite legacy import in one shot on first
  startup (the path the fail-closed parity fix hardens). Still re-confirm on the box
  immediately before any prod deploy.

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

- **Protected-principal gating gaps — 1 of 2 remaining (sweep 2026-06-29).** The
  2026-06-29 decision requires every mutating module to gate its write target through the
  protected-principal check; a read-only sweep found two modules that did not. GAP 1 is now
  closed (see below); GAP 2 remains. Address when the higher-priority bug queue clears; it
  needs an approved plan (mutating-module change).
  - **GAP 1 — `M365GroupManagementService` — CLOSED 2026-06-29.** Member/owner add/remove now
    gates the target through the protected-principal check before any Graph write (module
    1.1.0; commits `211c6eb`, `03c443a`; `docs/M365MemberOwnerManagement-Plan.md`). Group
    create/update/delete intentionally ungated (owner decision; see Now section + decisions.md).
  - **GAP 2 — `MigrationService` UNGATED.** No protected-principal reference in the service or
    `Components/Pages/Migration.razor`. `New-MigrationBatch` (`Services/MigrationService.cs`
    ~:267/:277, ToCloud + ToOnPrem) creates a batch over target mailboxes with no
    protected-principal validation. Standalone fix.
  - Full sweep result (12/14 modules already gated) is in the Now section.

- **MFA Reset stranded legacy config key — RESOLVED 2026-06-26 (app 2.3.26).** The Graph
  Delinea secret was renamed `DelineaSecretId` → `GraphDelineaSecretId`; environments
  configured before the rename held the value under the OLD key, so the config page showed
  blank while the service worked via a `?? DelineaSecretId` fallback. Fixed by a catalog-driven
  idempotent startup migration plus removal of the fallback — see the Now section and
  `docs/GraphSecretKeyMigration-Plan.md` (Status: Implemented). Code path verified by tests;
  deployed to dev and the recovered Secret ID confirmed on the config page (owner,
  2026-06-26). The same fix also cleared the identical latent bug in NamedLocations and
  M365GroupManagement.

## Queued work (forward-looking — no other doc home)

These have no plan doc yet; do not start without the noted plan/approval.

- **Module packaging/import.** Direction set 2026-06-18 (`.agents/decisions.md`): `.zip`
  package + validator, rebuild-to-install, runtime upload deferred. Needs
  `docs/ModulePackaging-Plan.md` written + approved before any implementation.
- **GM-1 (bug): GroupManagement search too fuzzy.** Exact group name (e.g. "IAM") returns
  dozens of fuzzy matches. Tighten on-prem AD group search so exact/near-exact ranks first.
  Search path not yet code-located. Confirm desired ranking with owner before implementing.
- **GM-2 (investigated 2026-06-26 — NOT the originally-reported bug).** Live test on dev
  (2.3.26) showed search *works*: it returns only Unified/M365 groups by design
  (`M365GroupManagementService.SearchGroupsAsync` filters
  `groupTypes/any(g:g eq 'Unified') and startsWith(displayName,...)`). The earlier
  "finds no groups at all" report was a synced **security** group (Source: Windows Server AD,
  not Unified) being correctly excluded — the grey "No M365 groups found" message, not the
  red HTTP-status error banner, confirms a clean 200 with empty `value`. Verified the query
  needs no `ConsistencyLevel: eventual`/`$count` per MS advanced-queries table (group
  `displayName`/`startsWith` and `groupTypes/any`/`eq` are both Default-supported). The
  failure-masking fix (`7048a3e`, app 2.3.5) is already in dev, so failures now surface as
  errors, not empty lists.
  **Member/owner management gap — BUILT 2026-06-29** (module 1.1.0; commits `211c6eb`,
  `03c443a`; `docs/M365MemberOwnerManagement-Plan.md`, Implemented). Full add/remove of both
  members and owners via Graph (`POST .../$ref`, `DELETE .../{id}/$ref`), each gated through
  the protected-principal check and fail closed before any write; admin notification only.
  Manual validation deferred to next dev deploy.
- **GM-3 (new module, needs own plan — DECIDE LATER): self-service group management.**
  Owner direction 2026-06-17, plan separately (`docs/SelfServiceGroupManagement-Plan.md`),
  nothing built until approved. Key requirements: likely a separate module; do NOT preload
  the user's manageable groups (explicit "show groups I manage" button with a slow-load
  warning); search restricted to groups the user manages with GM-1 fixes applied; reject
  any modification to non-managed groups at the service/authorization layer (UI hiding is
  not security). Open: how "manages" is determined, on-prem vs M365 vs both, making the
  lookup tolerable. Depends on GM-1/GM-2 being understood first.

## Recently completed (pointers only — full detail in the named docs)

- **AccountLockoutRemediation module** — incorporated 2026-06-26 (app 2.3.27),
  `docs/AccountLockoutRemediation-Incorporation-Plan.md` (Implemented). Manual validation
  (live AD 4740 read, WinRM, quser/logoff, real dry-run+logoff, protected-block) still
  pending its first dev deploy — run the package's own Manual Validation steps then.

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
