# Agent State

First place to read for current repo state. Keep it short; update it when important
repo facts change.

## Now

- App version `2.3.9` (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`) — dev-ahead
  Phase-4 batch (security/correctness backlog). **Prod is still on `2.3.8`**; 2.3.9 has
  not been deployed.
- **INCIDENT DIAGNOSED — see Diagnostic Results in
  `docs/Incident-2026-06-12-DevConfigLoss.md`.** Server diagnostics (2026-06-12 PM)
  proved no config was lost: pre/post-deploy `appsettings.json` are SHA256-identical,
  the startup enablement write never fired (the 12:49 write was the owner's own Save on
  /admin-settings), and the real cause was commit `f7df81a` (legacy permissions made
  FailClosed) landing on a dev box with no `sectionaccess.json` and no legacy
  `Security:SectionAccess` — fail-closed sections bypass the AllowedGroups fallback by
  design. Dev recovery is confirmed healthy from logs (residual denials only for
  disabled LicensingUpdates/TestAccountPool, absent from prod's fragment — add groups
  before enabling them).
- **ALL incident fixes #1-#8 are complete** (incident doc Status: Remediated). #1:
  startup enablement writes removed (2.3.7). #3: corrupt-store probes + admin pages
  refuse to save over corrupt backing state (2.3.8). #4: deploys snapshot the whole
  config/ dir. #5: post-deploy config drift check warns loudly. #6: deploy.ps1 defaults
  target dev, fresh installs need -ConfirmFreshInstall. #7: duplicate import removed.
  #8: closed by diagnostics. Remaining: owner sign-off + task-20 manual verification.
- **2.3.8 was deployed to prod (2026-06-17).** Prod freeze over; the FailClosed change
  and all incident fixes #1-#8 are in prod. The §Deploy-notes alias check applied to that
  deploy. Deploy holds fully lifted. **Dev has since advanced to 2.3.9** (the Phase-4
  batch below), so dev is now ahead of prod again; 2.3.9 is not yet deployed.
- Work stream: `docs/ProdReadiness-Plan.md` (Approved) — phases 1-3 complete and signed
  off. **Task 20 (manual UI verification, AC13) PASSED 2026-06-17.** Phase 3 fully
  verified; **Phase 4 (cleanup backlog, AC15-AC16) is in progress.** Findings register:
  `docs/ProdReadinessReview-2026-06-12.md`. Current local suite: **467/467 xUnit at 2.3.9**.

## Active work — Phase 4 / AC16 (resume here)

**NEXT ACTION:** resume the ProdReadiness AC16 PowerShell false-success batch at item 3 —
**`test-delinea.ps1` secret-printing hardening**, one fix/commit with Pester in `tests/ps/`,
proven non-vacuous. Then finding-3 doc cleanup, risk-accepts, and the §10 close-out.
(Item 2, the promote-dev-to-prod rollback message, is DONE — commit 3afd771.)
(An out-of-stream bug, CR-1 Room Finder, was implemented this session — see Queued work;
it is NOT part of the 2.3.9 ProdReadiness batch and does not gate it.)

Phase 4 is the only open ProdReadiness work. AC15 (docs/state drift sweep) is done. AC16
(remaining medium findings, one fix per commit or risk-accept in plan §10) is mid-flight.

**Done this batch (all at app 2.3.9, dev-only, not in prod):**
- Deny-by-default fallback policy is now a true deny-all assertion (runtime-verified the
  Blazor framework endpoints still serve).
- `SectionAccessService.BuildFailClosedSet` no longer swallows exceptions (fail-closed).
- `SectionAccessService` cache now invalidates on fragment file change (fixes the
  repaired-config-still-renders-blank trap; also makes on-disk edits take effect without
  restart).
- GroupManagement protected-principal check moved into the service (was UI-only, '@'-gated).
- MailboxPermissions cloud Add/Remove now report partial success per right.
- AD attribute allowlist save fails closed over a corrupt store (mirrors Section Access).
- AD attribute allowlist pre-save corruption gate now reads disk fresh
  (`ADAttributeEditorService.IsAllowlistCorrupt`, mirrors `SectionAccessService.IsFragmentCorrupt`).
  Closes GPT finding 1 (High): the old gate called the cached `GetAllowlist()`, so a file that
  went corrupt within the 30s TTL after a valid load could still be overwritten. Behavioral
  test added (valid load → corrupt file → cache still valid but gate detects corruption),
  proven non-vacuous via temporary revert to the cached call.
- Comms10k pre-count no longer hits the ADWS 5000-member cap. `ExecuteReplaceAsync`
  computed the "(was M)" count with `Get-ADGroupMember`, which expands members and throws
  past `MaxGroupOrMemberEntries` (default 5000), crashing the replace on the large tactical
  DL the module exists to serve. Swapped to `(Get-ADGroup -Properties member).member.Count`
  (raw linked attribute via range retrieval, not capped; owner verified live ~6800 members).
  Direct-member count, which matches the flat DN list the replace writes. `GetMembersAsync`
  keeps `Get-ADGroupMember` by design (preview/export, separate concern). No automated test:
  inline live-AD runspace with no injection seam, and §2 forbids a testability refactor —
  manual-verification-only.
- icacls native exit codes now checked in `deploy.ps1` (via a `Set-AclChecked` helper) and
  `tools/Install-ExchangeAdminWeb.ps1` (inline). Failed ACL grants were silent
  (`icacls | Out-Null`, native exe so ErrorActionPreference doesn't catch) and falsely
  reported success. Three static-AST Pester tests guard each call site; proven non-vacuous.
  No app version bump (ops-script-only).
- Module version bumps applied per touched module (GroupManagement 2.0.1, MailboxPermissions
  1.0.2, ADAttributeEditor 1.3.4, Comms10k 1.0.1).

**Remaining AC16:**
- PowerShell false-success batch (each one fix/commit, each needs Pester in `tests/ps/`):
  ~~icacls exit codes~~ DONE; ~~promote-dev-to-prod rollback message~~ DONE (commit 3afd771);
  `test-delinea.ps1` secret-printing hardening.
- Finding 3 ([creds] SMTP/ServiceNow plaintext) is RESOLVED BY DECISION (2026-06-17,
  `.agents/decisions.md`) — NOT a code change. Residual doc cleanup only: plan task 24 is
  still Delinea-specific and contradicts the generalized "deployment PAM" decision; and
  `appsettings.json.sample` should note these fields come from the deployment PAM in prod.
- SSL-off finding: accepted-as-designed (non-finding).
- SQLite-obsoleted config-file findings: risk-accept in plan §10.
- **Close-out:** write all AC16 outcomes (fixes + risk-accepts + finding-3 doc cleanup)
  into `docs/ProdReadiness-Plan.md` §10, then close Phase 4.

**Two GPT review rounds this batch were addressed** (deny-all fallback, SectionAccess
cache, allowlist fail-closed all came from them). Each fix shipped with a guard test proven
non-vacuous via temporary revert.

## Findings

- CI is real now: a failing test fails the run (observed: run 27425132329 failed on
  38 Windows-only harness failures, fixed in aeed8f2). Trust CI.
- On macOS, a missing Windows COM DLL can nondeterministically drop xUnit test
  collections (total count varies); windows-latest CI is unaffected. Trust failure
  lists, not totals, on local macOS runs.
- Local macOS commands need `-p:EnableWindowsTargeting=true` and (for Pester)
  `pwsh` installed as a dotnet global tool with `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec`.
- deploy.ps1 still lacks a native -PlanOnly (deferred with owner visibility; plan
  review log round 5). deploy-pipeline -PlanOnly covers the prod dry-run requirement.

## Queued work (owner-requested 2026-06-12)

- **Config storage rethink — database instead of JSON fragments** (decision DEFERRED to
  week of 2026-06-15; Michael will decide; needs a plan doc before any implementation).
  Motivation: repeated config-file headaches (2026-06-12 incident class). Owner asks:
  (a) evaluate a database (SQLite vs SQL Server) for runtime config; (b) what else
  should move there (candidates: all config/ fragments — enablement, section access,
  module configs, module admins, protected principals, AD attribute allowlist, extended
  log level — plus audit/event log, operation traces, config change history, test
  account pool state; bootstrap settings stay in appsettings.json); (c) a quick
  "copy prod config to dev" / "copy dev config to prod" tool; (d) new modules and new
  app settings self-register idempotently at startup (INSERT-if-missing with defaults).
  Item (d) would RELAX the no-writes-at-startup rule (2026-06-12 owner direction) for
  non-destructive seeding only — if adopted, supersede that direction explicitly in
  `.agents/decisions.md`; until then the current rule stands. Interacts with the module
  packaging item below (module manifest table). Much of this week's deploy hardening
  (config backups, drift check, corrupt-store guards) becomes obsolete-by-design if
  this lands; plan should say what gets retired.
  **PLAN DRAFTED: `docs/SqliteConfigStore-Plan.md` (Status: Draft, awaiting Michael's
  review).** Decided: SQLite over SQL Express (decision 2026-06-12). Built from a
  six-dimension touchpoint audit + critic. Open questions for Michael consolidated in
  the plan's §9 (audit JSONL deferral, Phase F runtime-editable appsettings,
  Microsoft.Data.Sqlite vs EF Core, OnPremExchange:ServerUri stays in appsettings). No
  implementation until the plan flips to Approved.

- **Module developer guide — full audit & rewrite** (not just a drift-check):
  `docs/AdminModuleDeveloperGuide.md` + `docs/AdminModuleSpec.md` version header have
  drifted on more than config — FailClosed permissions, enablement semantics (no startup
  writes as of 2.3.7), descriptor surface, two-rule versioning, auth wiring,
  `validate-module-package.ps1`. Michael will farm out new-module development, so the guide
  must stand alone for a developer with no chat context. **Owner direction (2026-06-15):
  do the full rewrite AFTER the SQLite config swap lands** so it documents the final
  DB-backed world, not a moving target — captured as Phase E2 in
  `docs/SqliteConfigStore-Plan.md`. A pre-swap drift pass is fine if Michael needs to
  hand something off sooner, but the authoritative rewrite is gated on the swap. Coordinate
  with the module packaging plan so guide and plan agree on authoring→validate→install.
- **Module packaging/import**: a way to package modules and import them cleanly into
  the main app, preferably through the UI; if recompile is unavoidable that trade-off
  goes back to Michael. Needs a `docs/ModulePackaging-Plan.md` and approval before any
  implementation (modules are currently compiled in: descriptor in
  `Modules/ModuleCatalog.cs` + razor pages, so runtime import implies assembly loading
  vs a source-drop + rebuild pipeline — open architecture question for the plan).

- **Remove the TestAccountPool module entirely** (owner direction 2026-06-15: never
  activated, no one will miss it). Do this as its own work item next run, **one focused
  change**, build + test green before done. It is also the app's **only** `AddHostedService`
  — a self-running timer that mutates AD unattended (`"System"/"BackgroundWorker"`, no
  ticket/actor), which is the architectural oddity that prompted the removal. Touchpoints
  to delete/prune (mapped 2026-06-15):
  - `Services/TestAccountPoolService.cs`, `Services/TestAccountPoolCleanupWorker.cs`,
    `Components/Pages/TestAccountPool.razor`, `ExchangeAdminWeb.Tests/TestAccountPoolServiceTests.cs`
  - `Program.cs` lines ~84-85: the `AddScoped<TestAccountPoolService>` and
    `AddHostedService<TestAccountPoolCleanupWorker>` registrations.
  - `Modules/ModuleCatalog.cs`: the `Id = "TestAccountPool"` descriptor (~line 385).
  - `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs`: any case referencing the module
    (the fail-closed/versioning sweeps enumerate the catalog — confirm they pass after
    removal, no hardcoded count of modules).
  - `tools/Install-ExchangeAdminWeb.ps1`, `appsettings.json.sample`, `README.md`: drop
    TestAccountPool config seeding / docs references.
  - Leave the historical docs alone (`docs/Incident-*`, `docs/ProdReadiness*`) — they are
    history, not current state. **`docs/SqliteConfigStore-Plan.md`**: drop TestAccountPool
    from the §5B.1 example list and Phase B/E module-test lists once removed (or note it as
    removed) — and removing the app's only HostedService **simplifies §5B.1**, so revisit
    that hazard's framing (the scoped consumers `ADAttributeEditorService` remain, so the
    connection-factory constraint still stands, just with a smaller blast radius).
  - Base app version bump (module removal is an app-wide change). Audit log keeps any
    historical `TestAccountPool_*` entries — do not scrub those.

## Queued work — group management (owner-requested 2026-06-17)

Not part of the ProdReadiness work stream; queued for after it (or whenever Michael
schedules). Items 1 and 2 are bug fixes; item 3 is a new module needing its own plan.

- **GM-1 (bug): GroupManagement search is too fuzzy.** Searching for an exact group
  (owner's example: "IAM" → the IAM group) returns dozens of fuzzy matches instead of the
  intended group. Tighten the AD group search so an exact/near-exact name ranks first and
  the fuzzy fan-out is reduced. Scope is the on-prem AD GroupManagement module search path
  (not yet code-located — needs investigation of how that module queries AD). Owner has not
  specified exact ranking rules; confirm desired behavior before implementing.

- **GM-2 (bug): M365 group management does not work at all.** It finds no groups regardless
  of search term. Root-cause unknown — could be the Graph query, auth/scope, or result
  mapping. Needs investigation before any fix; treat as a real defect, not a tuning issue.

- **GM-3 (new module, needs its own plan — DECIDE LATER): self-service group management.**
  Owner direction, to be planned separately (`docs/SelfServiceGroupManagement-Plan.md` or
  similar; nothing built until approved). Requirements as stated by owner 2026-06-17:
  - Likely a **separate module**, not a change to the existing GroupManagement module.
  - **Do NOT preload** the groups a user can modify on page load — owner expects that to be
    very slow. Instead provide an explicit **"show the groups I manage" button** with a
    warning that it can take a long time to load.
  - Provide a **search field** for a specific group, like the AD GroupManagement module, but
    with the GM-1 fuzzy fixes applied, AND **restricted to only groups the user manages**
    (direct/first-order ownership or via group-based management permissions). Groups the
    user does not manage must not appear in results.
  - **Must reject any modification** to a group the user does not manage (enforce at the
    service/authorization layer, not just UI — UI hiding is not security; cf. the
    corrupt-store and re-check patterns already in this app).
  - Open questions for the plan: how "manages" is determined (managedBy/owner attribute vs
    a permissions model), on-prem vs M365 vs both, and how to make the "groups I manage"
    lookup tolerable (it is the expensive path the owner flagged). Depends on GM-1/GM-2
    being understood first, since it reuses the search path.

## Queued work — Conference Rooms (owner-requested 2026-06-17)

Not part of the ProdReadiness work stream; queued for approval/implementation when Michael
schedules it.

- **CR-1 (bug): Room Finder Apply failed all rows — IMPLEMENTED IN DEV 2026-06-17, pending
  live verification.** Bug report: `D:\BugReports\roomfinder`. **Root cause (confirmed from
  the operation trace + live tenant reproduction, NOT the draft's original guess):** the
  failure was at **Set-User (Step 2)**, not Set-Place. City/State/Country are on-prem-AD-
  mastered and dir-synced, so EXO `Set-User` rejects them ("being synchronized from your
  on-premises organization"); `-ErrorAction Stop` made it fatal per row. The legacy
  `SetupRoomFinder.ps1` worked because it set those via on-prem `Set-User`, not EXO.
  **Fix:** write City(`l`)/State(`st`)/Country(`c`/`co`/`countryCode`) on the on-prem object
  via `Set-ADUser`, resolved by userPrincipalName (==email here; assert exactly one) and
  written by objectGUID. Country triple needs the ISO numeric .NET can't supply, so a new
  hardcoded `Services/IsoCountryCodes.cs` (249 entries, integrity-tested) provides it; `co`
  uses `RegionInfo.EnglishName` to match existing rooms. AD cred from PAM via a new
  ConferenceRooms `DelineaSecretId` ConfigField. Module 2.0.6 → 2.0.7. 467 tests green;
  guard tests proven non-vacuous. Plan: `docs/ConferenceRooms-RoomFinderMetadataApply-Plan.md`
  (Status: Implemented in dev). **Owner TODO before it works in prod:** configure the
  ConferenceRooms AD `DelineaSecretId` in the deployed instance, then live-verify apply.
  This fix is NOT part of the ProdReadiness 2.3.9 batch — separate module change.

## Blockers

- None. (Prod freeze ended; 2.3.8 shipped to prod 2026-06-17. Task 20 PASSED.)

## Deploy notes (before the FailClosed change reaches prod)

- Confirm each alias — MailboxPermissions, CalendarPermissions, MigrationCheck,
  MigrationCreate, MigrationManage, OutOfOffice — has a non-empty group list in the
  deployed `config/sectionaccess.json`. Owner reports per-module groups are in use,
  so this should be a no-op check.

## Verification

- Code changes: `dotnet build ExchangeAdminWeb.slnx -c Release` then
  `dotnet test ExchangeAdminWeb.slnx`. Add `dotnet format ExchangeAdminWeb.csproj
  --verify-no-changes --no-restore` and `git diff --check HEAD` where practical.
- PowerShell changes: `Invoke-ScriptAnalyzer -Path . -Recurse` and
  `Invoke-Pester tests/ps`.
- Full policy and the manual-check list live in `.agents/repo-map.json` and `AGENTS.md`.

## Active Sources

- `AGENTS.md`
- `docs/ProjectConstitution.md` (highest engineering authority)
- `docs/ProdReadiness-Plan.md` (active work stream)
- `docs/ProdReadinessReview-2026-06-12.md` (findings register)
- `.agents/repo-map.json`
- `.agents/decisions.md`

## Unrecorded Repo Memory

- None known. Engineering rules live in `docs/ProjectConstitution.md`; module contract
  in `docs/AdminModuleSpec.md`; work-stream history in `docs/*-Plan.md`.
