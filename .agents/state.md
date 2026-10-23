# Agent State

Current work and blockers only. Rules live in `docs/ProjectConstitution.md` and
`.agents/repo-guidance.md`, decisions in `.agents/decisions.md`, and machine observations in
`.agents/machines.md`. Each plan owns its implementation and manual acceptance checklist.
Superseded descriptions are verbatim in `docs/history/state-archive.md` (newest section first;
the latest sweep is Archived 2026-10-23).

## Now

### Fix-everything work stream, 2026-10-21 to 2026-10-23 - REOPENED 2026-10-23 evening

**What it is.** Owner go of 2026-10-21, verbatim: "fix all the bugs that you know about and any
more you find while doing so", then "ALL the bugs includes ALL the bugs. those too. all of them.
... EVERYTHING." That approved every open follow-up in `.agents/review/index.md` recorded "not
approved" or "owner decision required" (`.agents/decisions.md` 2026-10-21). The stopping rule of
2026-10-23 bounds it (`.agents/decisions.md` 2026-10-23): the agents in flight land and are
codex-reviewed; of what codex raises, only HIGH and MEDIUM candidates are fixed; LOW candidates
are admitted and held for after the first dev deploy; then version bumps, state refresh, token
log and the hand-over of a deployable master.

**Arrangement.** Opus 5.5 implementation agents dispatched from a supervising Fable session,
each in its own worktree; one finding per commit; mutation proof per guard; a codex (gpt-5.5,
xhigh) defect review of each merged group. The findings and their status live in
`.agents/review/index.md` and `.agents/review/findings/`; this file does not copy their counts.
Each slice's arrangement and cost go in `.agents/token-log.md`.

**Master, as of `d881ea58`:** base app 2.35.0 (was 2.34.0); sixteen module versions bumped in
`Modules/ModuleCatalog.cs`, which owns which ones. The last gate results are in
`## Verification`.

**REOPENED 2026-10-23 evening.** Owner ruling (`.agents/decisions.md` 2026-10-23 evening, verbatim there): every
remaining LOW, MEDIUM and HIGH issue is resolved before any dev or prod deploy; the LOW hold and the three
never-dispatched items are released now. Work in progress: the `.agents/review/index.md` rows whose Status begins
"Open; released by the 2026-10-23 evening ruling" (the six held LOW records, `aud-a2e`, `cdt-36`, `aud-a29g`, and
`kd-1`..`kd-9`, promoted from this file's former "Known defects" list and the ClickGateRegistry comment blocker).
Arrangement unchanged (below). Fourteen stale `[~]` parent rows whose follow-ons were already complete were closed
as bookkeeping in the same pass. **NEXT ACTION while the stream is open:** the supervising session dispatches,
merges, codex-reviews and records; the owner deploys nothing until this block says closed again. The paragraph
that follows is the 2026-10-23 afternoon hand-over, kept for its gate results and checklist pointers.

**The stream was closed (2026-10-23 afternoon).** `aud-a76` landed (`03261f7c`, codex perm-batch-10 clean); every
merged finding is codex-reviewed (`.agents/review/index.md`). Final gates on master as of `cfccec59`:
Release build 0 errors / 19 warnings; full suite 6693 passed / 0 failed / 0 skipped; `dotnet format`,
`git diff --check` and the ASCII lint clean. **NEXT ACTION: the owner's first dev deploy and the checklist
below.** Deployment is the owner's; nothing in this stream deploys. The held LOW backlog and the three
never-dispatched items (`aud-a2e`, `cdt-36`, `aud-a29g`) are the index rows whose Status says "held by the
2026-10-23 stopping rule"; `aud-a2e` is an authorization gap (Known Failure Class 3) and is the first to take
up after the deploy. Handoff 2026-10-23, verified head `e314166a`, tree clean but for the owner's untracked
`docs/TacticalAdminAccounts-Plan.md` (not this stream's; left alone). Owner rulings after the hand-over: the Directory
Read Credential will be the privileged write secret (`.agents/decisions.md` 2026-10-23); the held backlog was asked
about, not released - no agent runs. Dev may deploy any green master; dev does not depend on prod (the audit-file lock
and the SQLite engine notes describe the dev/prod interval only).

### The owner's checklist around the first dev deploy

None of it is automatable, and each record owns its detail. Several records mark their item "dev
acceptance owed", while `.agents/decisions.md` 2026-10-12 makes manual acceptance production use
rather than owed work, so whether any of these gates a promotion to prod is the owner's call.

Before deploying:

- `aud-a60` / `aud-a69`: configure the Directory Read Credential in Admin Settings, Protected
  principals, first - without it every Mailbox or Calendar Permissions grant to a directory-held
  group is refused.
- `aud-a47`: dev and prod may share a log root only on this build, so both instances must run it.
- `cdt-41`: the SQLite engine moves to 3.53.3. Dev and prod share one config database and will
  run two engines until both are deployed.

The deploy and the host:

- The first dev deploy itself (`./tools/deploy-pipeline.ps1 -Dev`, elevated).
- ConfigDbTool AC7: the plan-mode runs, `deploy-pipeline.ps1 -Prod -PlanOnly` among them
  (`docs/ConfigDbToolInApp-Plan.md`, section 10).
- Remove the per-user `sqlite3.exe` (`aud-sqlite3-path`; the path is in `.agents/machines.md`).

After the deploy:

- `aud-a2d`: the grant and revoke paths its record lists.
- `aud-a29b`: a revocation takes effect while a circuit stays open.
- Conference Rooms: the room-list path in a second domain (`aud-a2j`).
- `aud-a26l`: manager and Boolean edits in the attribute editor.

Labelled live assumptions, each named in its record and proven only in production:

- `aud-a23h`, `aud-a23n`, `aud-a23r`, `aud-a23j`, `aud-a23o`, `aud-a54`, `aud-a55`: Exchange
  not-found shapes and `Get-MigrationUser` status values.
- `aud-a43`: no UPN claim under Negotiate.

**Held LOW backlog:** released 2026-10-23 evening; see the REOPENED paragraph above. No index row reads "held by
the 2026-10-23 stopping rule" any more.

**Open owner decisions:** whether `-NarrowPublishAcl` becomes the installer default
(`aud-operational-hardening`), which needs a dev install that exercises the modules. No index row
reads "OWNER DECISION" any more (the 2026-10-21 go approved them all; checked as of `d881ea58`).

## Carried forward

What is still live from blocks rotated to `docs/history/state-archive.md` (Archived 2026-10-23
and earlier). Each line names its owner; the derivations are in the archive.

### Standing owner instructions recorded only here

- **Do not probe Active Directory without a direct go** (owner, 2026-10-14).
- **The Migration module is closed by owner order** (2026-09-29). Work on it only for a prod
  blocker the owner names; his two named exceptions were the `@key` checkbox fix and queue item
  23 (both 2026-09-30), and the 2026-10-21 go covered its findings in the review index.
- **Do not rename the section-access aliases `MessageTrace` and `MessageTraceSearch`.** Each is a
  storage key in a fail-closed store, so a rename denies the groups granted against it. Operator
  wording changes through `ModulePermission.DisplayName`.
- **Emergency Disable's lockdown checkbox is a temporary escape hatch** until the stakeholder says
  what they want; then it goes away or becomes the settled default. The default lives in the one
  `LockdownDefaultArmed` constant (`.agents/decisions.md` 2026-10-05 and 2026-10-06;
  `docs/EmergencyDisableLockdownOU-Plan.md`).

### Engineering rules with no other home

- Item 24 page conversions take `[Inject] private IActivityProgress Progress` inside `@code`,
  never an `@inject` directive, which sits above every registered control and shifts all of them.
- Remove a module's spinner only after its operation reports to the status bar.
- Per-row indicators and the pre-authorization latch stay (a reading the owner has not ruled on).
- Never report page-load state from the browser: page loads are server-side
  (`Services/PageLoadTracker.cs`, `Middleware/PageLoadMiddleware.cs`), and
  `wwwroot/nav-progress.js` must not come back (`Components/App.razor` says why).
- A live defect parked in a registry entry or a plan note also gets a line in this file; queue
  item 23 shipped because only the registry recorded it.
- Never run two agents in one checkout; each gets its own worktree (2026-09-25 incident).

### Live residue of archived streams

- **Defender for Endpoint** (`docs/DefenderEndpointDevices-Plan.md`, S1-S7 implemented; the twenty
  findings of `docs/Comms10kDefenderRemediation-Plan.md` closed). No live result is recorded for
  its three environmental unknowns, each of which fails visibly: the forest read behind Site, the
  `SeenBy()` observer column, and whether an AD site is a physical locator at all (owner: it is
  not). The owner's 26,812-row export found Site the only location signal present for
  essentially every device; measure any proposed locator against that table (archived).
- **Circuit lifetime leak** (`docs/CircuitLifetimeLeak-Plan.md`, Implemented). The fix was
  codex-reviewed (`leak-1`, 2026-10-07; its recurrence guard repaired 2026-10-08). Unproven is
  that the leak is gone: that needs a worker with about 24 hours of uptime and one elevated shell
  (`.agents/machines.md`). `docs/ProductionMemory-Plan.md` is a Draft awaiting an owner go.
- **Comms-10k** (`docs/Comms10kBulkResolveScale-Plan.md` implemented; the stakeholder's test failed
  2026-10-01). Point 3, missing addresses, is settled by the exact-match ruling and the
  rejected-address CSV (`.agents/decisions.md` 2026-10-12 ruling 1; `a01767c`). Point 2, progress
  visibility, is queue item 24. Point 1, 1.5 hours against 15-20 minutes, needs its baseline
  first: "the previous version" may not be this app, and the module's own write measured 7.9s
  for 10,001 members. Its removed guard, removed locking and protected-principal exemption are
  owner rulings with tripwires (`.agents/decisions.md` 2026-09-30); do not reintroduce or
  re-litigate them.
- **Queue item 24, global progress** (functionally complete). Four typeahead spinners survive in
  `Components/Shared` (`ADGroupAutocomplete`, `ADIdentityAutocomplete`, `RecipientAutocomplete`,
  `TicketNumberInput`); raised with the owner 2026-10-02, unanswered. Only Message Trace's bulk
  detail download honours the progress cancellation token (`Components/Pages/MessageTrace.razor`
  `:1189`). Both re-verified 2026-10-23 as of `d881ea58`.
- **True Last Logon** (`docs/TrueLastLogon-Plan.md`, built and reviewed; its owner rulings live
  there). The cloud half has no recorded run against a real tenant; it needs `AuditLog.Read.All`
  consented behind its secret, and the module ships disabled. The live comparison is production
  use (`.agents/decisions.md` 2026-10-12).

## Next

- **After the hand-over** (the order between these groups is the owner's): the held LOW backlog
  (`## Now`); the new module the owner has waiting (`.agents/decisions.md` 2026-10-23); and three
  plans approved 2026-10-12 and not started, in his stated order (`.agents/decisions.md`
  2026-10-12 - scope is not a gate, so do not ask him to re-approve them):
  1. Click-gating tiers 2-4, the remaining 19 pages: `docs/ClickGatingAudit-Plan.md`; read its
     Revision 1 before touching a page.
  2. The verbosity sweep at full scope, all 39 files, S0-S10: `docs/VerbositySweep-Plan.md`. The
     copy is not free - 20-plus test files pin operator-facing wording, and every page is
     line-keyed by `ClickGateRegistry` and `ProgressRegistry`.
  3. A rewrite, not an amendment, of the Service Health public-status plan from the owner's ask
     (`.agents/decisions.md` 2026-10-12 ruling 4); `docs/ServiceHealthPublicStatus-Plan.md` is
     REJECTED.
- **Defender S8** (docs, version and the `DeviceNetworkEvents` fallback) is not started. Subnet,
  gateway, DHCP server and MAC vendor need a `DeviceNetworkInfo` join whose column names are
  unverified; `column_ifexists` in a request of its own is the safe way in.
- **Former "Known defects recorded only in this file":** promoted to `.agents/review/index.md` as `kd-1`..`kd-9`
  on 2026-10-23 evening (the DC "required audit event could not be generated" refusal is not a code defect and
  stays the owner's to chase; it is not a row).
- **Implementation codereviews recorded as never dispatched:** `docs/ServiceHealth-Plan.md`,
  `docs/GroupBulkActions-Plan.md` and `docs/AlphabeticalModuleOrdering-Plan.md`.
- **Coverage:** re-measure on green CI and ratchet per `.agents/review/coverage-floor.txt`;
  `PermissionValidator` still needs its own approved plan for the credential-carrying I/O seam.
- **Module development platform** (`docs/ModuleDevelopmentPlatform-Plan.md`, Draft revision 2):
  awaits approval of the full cutover, including its structural-only Migration exception.
- **Parked or deferred by the owner, no resumption authorised:** module packaging and import
  (`.agents/decisions.md` 2026-07-22); Account Lockout Remediation and its findings `aud-a5b` and
  `aud-a5c` (2026-10-19; its disabled state is in `.agents/machines.md`).
- **The owner's issue queue** is at the path in `.agents/machines.md`; it is his file and is never
  written to. Its open rows as this file last read them (2026-10-14; the archived queue table has
  each reading): 2 (the Service Health rewrite above), 4 (`## Blockers`), 5
  (`docs/MessageTraceMultiTenant-Plan.md`, feasibility only - his decision whether to proceed), 6
  (containerize, on hold; `## Blockers`), 8 (Defender, above), 9 and 25 (above), 10 and 11 (Cloud
  Password Reset, `## Blockers`), 17 (True Last Logon), 20 (Comms-10k) and 24 (item 24).

## Blockers

- **Cloud Password Reset (queue 10 and 11) is blocked on the owner**, and nothing works until he
  creates a dedicated Entra app registration (`User.Read.All`, `User-PasswordProfile.ReadWrite.All`
  and `RoleManagement.Read.Directory`, admin-consented; never another module's registration) and
  its Delinea record (`Tenant ID`, `Application ID`, `Client Secret`, no checkout workflow), enters
  that id as the module's `GraphDelineaSecretId`, then deploys, enables the module (it ships
  disabled) and grants section access. `docs/CloudPasswordReset-Plan.md` owns the checklist; the
  cases that matter most are two people sharing an employee ID and the cleared force-change case.
  Residuals: the survey exports' disposition (none is in the workspace, re-checked 2026-10-23),
  and survey registration `16866221-3e2b-4ea5-8aae-157b043b6d7c`, recorded as holding unnecessary
  `Directory.ReadWrite.All`, `User.ReadWrite.All`, `Group.ReadWrite.All` and
  `UserAuthenticationMethod.ReadWrite.All` grants; revocation needs separate authority.
- **Queue 6, containerize: the SPN and gMSA work is the owner's AD team's.** A container runs
  Kestrel, so Windows Authentication needs a gMSA, a credential spec and SPNs in the forest
  (`deploy.ps1:444` removes the IIS Negotiate provider today to avoid that). Where "elsewhere" is
  remains unanswered and decides whether the AD, on-prem Exchange and DHCP work can run at all
  (`.agents/decisions.md` 2026-09-21).
- **Unsettled, the owner's to reconcile** (no rule was changed):
  - `.agents/decisions.md` 2026-10-01, no deployments until item 24 is done, was never restated or
    lifted, though both instances were redeployed during it and the 2026-10-23 rule hands over a
    deployable master.
  - The 2026-07-31 protected-principal input-validation rationale relies on this deployment's read
    visibility, while the 2026-09-11 rule (Architectural Invariant 7) rejects environmental safety
    arguments.
  - The 2026-10-12 manual-acceptance ruling against records that mark items "dev acceptance owed";
    `aud-a26l` says its check blocks the attribute editor's deploy.
  - `mde-9` changed a duplicate-row behaviour that `mde-3` recorded as deliberate. It serves his
    every-rejection-reported ruling, but he was never asked.
  - Queue 4 is owner-marked DONE, yet `MessageTraceSearch` is still inert: declared at
    `Modules/ModuleCatalog.cs:500` and consulted by no page, handler or policy, while
    `docs/MessageTracePermissionSplit-Plan.md` reads APPROVED and IN PROGRESS with slices 2-4
    unstarted. Re-verified 2026-10-23 as of `d881ea58`.
  - `ExchangeAdminWeb.Tests/ClickGateRegistry.cs:75` stale tiers 2-4 wording: now index row `kd-9`.
- **SharedConfigDb record flags:** the decision says SQLite `data_version` / next read; the
  implementation uses the store's change token with a throttle. The additive-only rule has
  operative homes in the decision, guidance and test but not in the Constitution.
  `docs/SharedConfigDb-Plan.md` section 9; no ruling inferred.
- **Doc and plan status drift, re-verified 2026-10-23 as of `d881ea58`** (docs, outside a drift
  sweep's reach; do not silently relabel; `docs/RepoAudit-Remediation-Plan.md` was relabelled
  IMPLEMENTED 2026-10-23 by the stream and is no longer in drift):
  `docs/BlockedSendersLoadTiming-Plan.md` and `docs/Comms10kReplaceUx-Plan.md` read Approved and
  `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md` Approved / In progress, with completion
  unverified; `docs/AdminUIRedesign-Plan.md` reads In progress;
  `docs/MessageTracePermissionSplit-Plan.md` is above. `docs/AdminModuleSpec.md` and
  `docs/AdminModuleDeveloperGuide.md` both carry Version 2.0, last verified 2026-06-25, against
  the current base version.
- **Unscheduled M365 protection gap:** `Services/M365GroupManagementService.cs` calls
  `CheckProtectedAsync` only from the member and owner paths (`:255`, `:329`); `UpdateGroupAsync`
  (`:125`) and `DeleteGroupAsync` (`:143`) call neither, and protection configuration cannot
  identify a cloud-only group. Re-verified 2026-10-23 as of `d881ea58`. The owner excluded this
  module from the on-prem target work; no work approved.
- **Older questions:** `docs/MessageTraceNullRow-Plan.md` needs a live rerun (OQ-1, the upstream
  null-row cause undiagnosed); `docs/ProtectedPrincipalResolution-Plan.md` keeps OQ-2, whether the
  reported cloud-only mailbox targets should stay reachable.
- **Ops gaps:** ConferenceRooms' AD secret on prod was outstanding and has not been checked since;
  `deploy.ps1` still has no native `-PlanOnly` (Architectural Invariant 4), and the pipeline's dry
  run is the recorded workaround.

## Verification

Commands and mandatory guards are owned by `.agents/repo-guidance.md` and `AGENTS.md`: the
Release build, `dotnet test ExchangeAdminWeb.slnx`, `dotnet format ExchangeAdminWeb.slnx
--verify-no-changes --no-restore`, `git diff --check HEAD` and `tools/Test-AsciiOnly.ps1`; for
PowerShell, `Invoke-ScriptAnalyzer` and `Invoke-Pester tests/ps`. Host specifics (the xUnit thread
cap, the pinned Pester and PSScriptAnalyzer versions) are in `.agents/machines.md`.

Last results, each with its commit. The 2026-10-23 drift sweep changed records only and re-ran
none of them.

- Full suite as of `cfccec59`: 6693 passed / 0 failed / 0 skipped, as the supervising session
  reported it; Release build 0 errors / 19 warnings; format, diff-check and ASCII lint clean. The
  three former live-directory skips are stand-in passes since `psl-8`. The 19 warnings are down
  from 61 before `cdt-41`; the NuGet advisories are gone and NuGet audit is now a build error
  (`cdt-41`).
- Pester as of `7698c096`, `cdt-41`'s branch tip: 478 passed / 0 failed / 0 skipped. No `.ps1`,
  `.psm1` or `.psd1` file has changed since, as of `d881ea58`.
- PSScriptAnalyzer: 0 errors on each changed script in the ops-script slices; their records own
  the runs.

Not exercised by any gate: browser rendering (there is no bUnit harness; `.agents/decisions.md`
2026-10-01, "Rendered is not visible"), live writes, and the live forest and tenant. Both deployed
instances predate the stream; `.agents/machines.md` owns that receipt, re-measured 2026-10-23.
CI status is not recorded here, and per-finding status is `.agents/review/index.md`'s.

Method traps worth keeping in view (their derivations are archived):

- Restore a mutated file byte-exact, md5-checked, with a fresh mtime; a restore that keeps the old
  mtime lets MSBuild skip the rebuild, and the test then runs the mutated binary.
- Confirm a mutation applied before trusting its result: one that did not apply proves nothing,
  and one that passes may be an equivalent implementation rather than a weak guard.
- Grep build output for `error CS`; do not tail it.
- One unidentified single failure on 2026-10-19 (its name was lost to a `tail`) was never
  reproduced; treat it as unexplained, not absent.

## Active sources

- `AGENTS.md`, `.agents/repo-guidance.md`, and `docs/ProjectConstitution.md` govern work.
- `.agents/decisions.md` owns decisions; `.agents/machines.md` owns host facts.
- `.agents/review/index.md` and `.agents/review/findings/` own finding status and detail;
  `.agents/token-log.md` owns per-slice arrangement and cost.
- Referenced plans own scope, status and acceptance checklists.
- `Modules/ModuleCatalog.cs` owns module versions and enumeration;
  `ExchangeAdminWeb.csproj` owns the current base app version.
