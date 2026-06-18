# Agent State

First place to read for current repo state. Keep it short; update it when important
repo facts change.

## Now

- App version `2.3.10` (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`). **Prod is on
  `2.3.8`.** Dev is ahead and **none of 2.3.9 / 2.3.10 is in prod.**
- **Commit `bb94d17` (pushed to `master` 2026-06-18)** batched four changes (should have been
  one-per-change; batched in error — see governance note): (1) TestAccountPool removal → app
  `2.3.9`→`2.3.10`; (2) CR-2 preview phantom-type fix → ConferenceRooms `2.0.8`; (3) review
  Finding 1 (Room Finder AD preflight before Set-Place) → ConferenceRooms `2.0.9`; (4) review
  Finding 2 (GroupManagement page protected-principal gate removed, enforcement now solely in
  the service) → GroupManagement `2.0.1`→`2.0.2`.
- **UNCOMMITTED follow-up batch (Conference Rooms, 2026-06-18)** — done + verified locally,
  NOT yet committed, NOT yet deployed (the dev box that produced the live test still runs
  pre-`bb94d17` code). Two changes, ConferenceRooms `2.0.9`→`2.0.10`:
  1. **RoomListOU removed — room lists now created cloud-side with no `-OrganizationalUnit`.**
     The module creates room lists via EXO `New-DistributionGroup`; it was passing the on-prem
     `RoomListOU` path, which EXO rejects ("organizational unit not found") — the cause of the
     live all-rows-failed test. Removed the property, ConfigField, and example-config entry.
     Decision recorded in `.agents/decisions.md` (cloud-only room lists, accepted).
  2. **Partial-write now reported/audited** (`RoomOperationResult.Partial`, "PARTIAL" UI badge,
     audit detail). Resolves the GPT follow-up finding that a half-applied row (EXO written,
     later step failed) was reported as a plain "FAILED". Decision recorded.
     Also corrected the false "accepted" claim in `docs/CommitReview-2026-06-17.md`.
- **NEXT ACTION:** commit the follow-up batch (one ConferenceRooms commit), then push. Then the
  only open item is owner-operational — **deploy app `2.3.10` to prod** (run §Deploy-notes alias
  check first) and **redeploy dev** so it runs the committed code.
- **Governance note (owner, 2026-06-18):** commits were batched at end-of-session instead of
  per-change. Owner intends to strengthen governance so changes are committed as they land.
- Current local suite: **459/459 xUnit at app 2.3.10 / ConferenceRooms 2.0.10** (build clean,
  0 warnings; format clean). No automated test for the RoomListOU/partial paths (live
  EXO/AD runspace, no injection seam; AGENTS.md §2) — manual-verification-only.
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
  `docs/ProdReadinessReview-2026-06-12.md`. (The ProdReadiness batch was 467/467 at 2.3.9;
  current suite is now **459/459 at 2.3.10** after TestAccountPool removal + CR-2 — see §Now.)

## Active work — Phase 4 / AC16 (resume here)

**NEXT ACTION (authoritative pointer in §Now):** commit the two uncommitted working-tree
changes — TestAccountPool removal, then CR-2 — one logical change per commit. They are
already deployed to dev but not committed.

ProdReadiness work stream is COMPLETE — `docs/ProdReadiness-Plan.md` Status **Implemented**
(close-out §10 round 17, commit a5ab6aa). All AC1–AC16 met; every AC16-scoped register medium
is a fix or recorded risk-accept (SSL-off accepted-as-designed; GetGraphClientAsync superseded
by per-module GraphTokenClient; PSCredential DRY-only deferred; config case-sensitivity +
last-write-wins obsoleted-by-SQLite). No ProdReadiness work remains. Open owner-operational
items: **deploy app 2.3.10 to prod** (prod on 2.3.8; run §Deploy-notes alias check first) —
timing is the owner's call. After the commits, remaining engineering candidates: GM-1/GM-2
bugs, CR-1 live verification, or the SQLite/module-packaging plans awaiting Michael's review.

**ProdReadiness Phase 4 / AC16 is CLOSED (2026-06-17, commit a5ab6aa).** Plan Status flipped
to Implemented; close-out and risk-accept register in `docs/ProdReadiness-Plan.md` §10
round 17. AC15 (docs/state drift) and AC16 (register mediums) both complete; all AC1–AC16 met.

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

**AC16 — ALL DONE (close-out plan §10 round 17, commit a5ab6aa):**
- PowerShell false-success batch: ~~icacls exit codes~~ (060fc7f); ~~promote rollback
  message~~ (3afd771); ~~test-delinea secret printing~~ (5b6b74a). Complete.
- Audit category misfiling: ~~EmergencyDisable/LicensingUpdates/Comms10k file under own
  categories~~ (a5ab6aa), with AdminEventLog filter options + module bumps + non-vacuous
  source-scan tests.
- Finding 3 ([creds] SMTP/ServiceNow plaintext): RESOLVED BY DECISION 2026-06-17; doc
  cleanup DONE (12d8413).
- Risk-accepted in plan §10 round 17: SSL-off (accepted-as-designed; cleartext-password path
  is TestAccountPool's, queued for removal); GetGraphClientAsync 5-copy consolidation
  (superseded by per-module GraphTokenClient, 7ba76a9); PSCredential 8-copy factory (DRY-only,
  deferred); config case-sensitivity + last-write-wins (obsoleted-by-SQLite-swap).

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

- ~~**Remove the TestAccountPool module entirely**~~ **DONE 2026-06-17 (app 2.3.10).**
  Deleted the service, cleanup worker, razor page, and service tests; removed the two
  `Program.cs` registrations (including the app's only `AddHostedService`); removed the
  catalog descriptor; removed the now-orphaned `EmailService.SendTestAccountPasswordAsync`;
  pruned config seeds (`Install-ExchangeAdminWeb.ps1`, `appsettings.json.sample`) and the
  `README.md` section; updated `ModuleCatalogTests` counts (modules 21→20, aliases 28→27);
  bumped base app version 2.3.9→2.3.10. Forward-looking plan refs (`SqliteConfigStore-Plan.md`,
  `FutureModules-Plan.md`) flipped to past-tense. Build + test green (458/458). Historical
  docs (`Incident-*`, `ProdReadiness*`) keep their refs by design. Decision recorded in
  `.agents/decisions.md`.

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

- **CR-2 (display fix): Set-Room-Type CSV preview showed phantom "Standard" — DONE IN DEV
  2026-06-17 (uncommitted).** Surfaced when the owner uploaded a Room Finder buildings CSV
  (`ADLKRF_Buildings.csv`, no `Type` column) into the "Set Room Type" uploader: all 86 rows
  correctly failed with `Invalid room type: ''`, but the preview Type column rendered
  "Standard" for every failed row. Root cause: `RoomTypePreviewRow.Type` was a non-nullable
  `RoomType` enum, which defaults to its first value (`Standard`) when a failure branch never
  sets it. Fix: `Type` is now `RoomType?`; the razor cell renders `@(row.Type?.ToString() ??
  "—")`. Rows that genuinely parsed a type (incl. "Room not found" rows, which DID parse one
  via `BuildTypePreview`) still display it. Files: `Models/ConferenceRoomModels.cs`,
  `Components/Pages/ConferenceRooms.razor`. Module 2.0.7 → **2.0.8**. New guard test
  `ConferenceRoomCsvParseTests.RoomTypePreviewRow_DefaultType_IsNull_NotPhantomStandard`,
  proven non-vacuous (reverted nullability → failed with `Actual: Standard` → restored).
  Behavior unchanged — the file-vs-uploader mismatch is still a real failure; this only stops
  the preview misrepresenting it. Separate single change from CR-1; commit on its own.

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
