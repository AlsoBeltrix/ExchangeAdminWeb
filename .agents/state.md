# Agent State

First place to read for current repo state. Keep it short; update it when important repo facts
change. Resolved work lives in the plan/decision/incident docs, not here — this file records only
what is live: current versions, in-flight work, what to do next, blockers, and open gaps.

## Now

**Review loop CLOSED 2026-08-31.** fsr-1 (`f6a4eb1`) and fsr-2 (`6f4d972`) both closed
on coder-side proofs under the same-day ruling: reviewer verification rounds are
CRITICAL-only and each needs an explicit owner go (`.agents/decisions.md`).

- **PROTECTED TARGETS ANSWER AT FIRST QUERY: IMPLEMENTED 2026-08-31 (owner go after
  testing on dev: refusal "should happen as soon as the group is queried; preferably
  protected groups won't show members either"). NOT DEPLOYED.** GroupManagement only:
  `CheckTargetProtectionAsync` runs at group selection - a non-servicer gets the
  refusal immediately and the panel (Load Members, add box, member table) never
  renders; the member read itself gates server-side as the fail-closed backstop; a
  servicer sees a Protected badge and works normally. Write-path gates unchanged
  (defense in depth). Self-service untouched per the same day's AC4 ruling.
  `GroupManagement 2.6.0`, no base bump. 7 new tests (behavioral via the seamed
  harness + tripwires), probe: both gates neutered, 5 tests failed, restored.
  **NEXT: rides the next dev deploy; then select a protected group as a non-servicer
  and confirm the immediate refusal with no member list.**
  **Two adjacent PRE-EXISTING gaps found while reading, NOT fixed (survey is not
  authorization): (1) `ResolveGroupForWrite` LDAP-filters sam/name/mail with no
  -Server, so a WINROOT group likely cannot be written to (or now, checked) from the
  admin module - its resolve fails local-only; (2) `GetMembersAsync` reads the group
  by DN with no -Server either, so opening a WINROOT group's members likely fails too.
  Both predate today; owner decides whether to schedule.**

- **GROUP SEARCH FOREST SCOPE: IMPLEMENTED 2026-08-31 (owner go: "make this search work
  first"). ON DEV since 2026-08-31 (third same-day deploy) and VERIFIED live: the
  "domain admins" search returns rows from BOTH domains with the Domain column (AD and
  WINROOT), checked browser-side after the owner's deploy.** Group Management's group
  search previously queried only the app
  credential's home domain and showed no domain per row - the owner hit it validating
  protected targets on dev ("Domain Admins" ambiguous, other domains unreachable). The
  search now queries the forest global catalog (same success-only-cache and ":3268"
  guard as `ADDirectorySearchService.ResolveGlobalCatalog`, fail-soft to local) and
  results carry a Domain column. `GroupManagement 2.5.0`, no base bump. 7 new tests
  (pure DomainLabel + wiring tripwires), non-vacuity probed (wiring reverted, tripwire
  failed, restored). **The owner deferred replacing the search with an autocomplete
  ("not sure about autocomplete yet") - noted, not planned.**
  **NEXT: nothing - dev check done 2026-08-31. Prod promotion is the owner's call.**

- **EVENT LOG CSV TICKET: IMPLEMENTED 2026-08-27 (owner go the same day). ON DEV since
  2026-08-31 (the `2.10.0` deploy); NOT on prod.**
  `docs/EventLogCsvTicket-Plan.md` (S1 `d54b33f`, S2 the plan-closing commit). Stored
  audit/trace `ticket` field appended as the ninth CSV column, named `Ticket`; no
  ServiceNow lookup, no on-screen column, no filter. Module `AdminEventLog`
  `1.0.3` -> `1.1.0`, no base app bump. Six new tests, mutation-proven; full suite
  1707/0/3.
  **NEXT: run the plan's four manual checks (section 8) on dev - unblocked 2026-08-31.**

- **TOKEN BUDGET: IMPLEMENTED 2026-08-27 (owner go the same day; S1-S4 all landed).**
  Baseline correction `b2b2887` first (the plan's August figures had counted transcript lines,
  not billed requests); S1 tool + `transcript-root:` entry in `.agents/machines.md` `fb2a44b`;
  S2 Pester (17 tests, four-mutation non-vacuity proof) `be97436`; S3 baseline + log `fe1c7bf`;
  S4 is the Token Budget section in `.agents/repo-guidance.md`, landed in the commit that set
  the plan's status to Implemented.
  `docs/TokenBudget-Plan.md` (`c2ae60c`, revised `5b54222`). Owner request 2026-08-14: a
  token-budget-friendly implementation approach for September, with usage tracking built in.
  Canonical detail is in the plan; do not duplicate it here.
  **D1 RULED 2026-08-14, AMENDED by the owner 2026-08-27: Fable 5 implements end to end,**
  one fresh session per slice; codex/GPT-5.5 reviews, Gemini reserved as an owner-dispatched
  third harness. Rationale and the Sonnet fallback live in `.agents/decisions.md` (2026-08-27).
  **D2 WITHDRAWN - `.agents/playbooks/drift.md` already owns it.** Reducing this file is the
  sweep's first checklist item, and it rotates `## Now` entries **verbatim** to
  `docs/history/state-archive.md` rather than rewriting them. Invoke with `playbook drift`, or
  `catchup` which offers it. It was put to the owner as a decision in error - the owner caught
  it. **A push-status paragraph was deleted from this file at the same time**, per the same
  playbook's 2026-07-11 deleted-on-sight ruling. **Size, the volatile figure this argument rests
  on:** 138 KB / ~51,800 tokens (roughly 18% of every request) when the plan was written; 58 KB
  after the 2026-08-14 drift sweep. Re-measure rather than quoting either number.
  **Measured baselines: CORRECTED 2026-08-27 - the 2026-08-14 figures counted transcript
  lines, not billed requests** (one request writes 1-5 identical-usage JSONL lines). Canonical
  corrected numbers live in the plan's "Correction 2026-08-27" under Measured baseline
  (`b2b2887`), and in `.agents/token-baseline.json` once S3 lands; do not quote the old
  7,876-request / ~$2,311 figures. Per-request unit facts stand: a re-prime at 280K context
  costs ~$1.75, the cache TTL is 5 minutes, idle gaps are billed.
  **Haiku 4.5 is permanently disqualified** - 200K context, 72.3% of requests exceed it. Do not
  re-propose. Sonnet 4.6 and every older Opus are strictly dominated on price and capability.
  **NEXT: regenerate `.agents/token-baseline.json` on or after 2026-09-01** (one tool run,
  commit the diff) so the full August is captured; the protocol section in
  `.agents/repo-guidance.md` then governs how the September queue is implemented.

- **INTUNE DEVICES MODULE: PLAN DRAFTED AND REVIEWED, AWAITING OWNER GO, NO CODE.**
  `docs/IntuneDeviceManagement-Plan.md` (`6aef9e3`, revised through `74c36b9` - the plan file's
  own last content revision; commits after it touch this file and the review records, not the
  plan).
  Owner request 2026-08-14: *"we need to plan a module for managing intune devices, pulling
  device details, and deleting"*, then *"review the plan with codex"*.
  New module `IntuneDevices`, Microsoft Graph v1.0 Intune device management. Independent of
  the three plans below - no shared code, no ordering constraint.
  **D1 IS RULED (owner, 2026-08-14): all three destructive actions, at two permission tiers.**
  *"options for all of the above with different permission levels for 1 and 2+3. Two permission
  levels."* Delete the Intune record sits behind `IntuneDevicesDelete`; Retire and Wipe share
  `IntuneDevicesPrivileged`; read sits behind the main `IntuneDevices` permission. All three
  fail closed. **I asked this one before drafting because it decides which Graph permissions
  the app registration needs**, and those are three genuinely distinct scopes.
  **The distinction the question existed to surface, and it is the operator-facing one:**
  Intune "Delete" removes the management record only. Company data stays on the device until it
  next checks in, and if it never checks in, forever. The Entra ID device object survives all
  three actions - Microsoft's own guidance is to remove it as a separate step.
  **All endpoints are v1.0**, verified against Microsoft Learn 2026-08-14, so
  `GraphTokenClient`'s hardcoded base URL needs no change and no NuGet package is added.
  **D2 IS RULED (owner, 2026-08-14) and the ruling is a standing design rule for this module,
  not just an answer:** *"anything that can be an option should be an option. do not build in
  restraints. make email the user an option."* **All three fixed alternatives I offered were
  rejected as written, because each hardcoded one answer.** Notifying the primary user is now
  three config fields (one per action) setting a deployment default, plus a per-action checkbox
  the operator can change at the moment of acting, plus an audit record of what actually
  happened. Defaults: off for delete, on for retire and wipe.
  **The same ruling reopened something the plan had closed off unasked one revision earlier.**
  `idm-2`'s fix pinned the wipe flags to a fixed body and put operator choice out of scope -
  exactly the built-in restraint the owner removed. Every parameter Graph accepts on `wipe` is
  now an operator control. The half of `idm-2` that survives is the half that was right: the
  body is always explicit and asserted, defaults are a full reset, and the audit names the exact
  flag set so a `keepUserData` wipe and a full reset are distinguishable afterwards.
  **The trap this ruling creates, pinned in the plan:** `EmailService` gates every affected-user
  send on an app-wide `_notifyUsers` switch (`EmailService.cs:176-180`) which outranks anything
  the module sets. A ticked box on a deployment with user notifications off must say so on
  screen and in the audit, or the control is decorative - the unreachable-capability shape from
  the other direction.
  **A SECOND openreview pass covered the post-ruling half** (`codex`, same pair, over
  `6aef9e3..236b91b`): `acceptable_with_changes`, two findings, **both durable-record hygiene and
  neither touching the plan's substance** - `idm-4` (this file anchored the work five commits
  stale and carried a commit count that rots on every commit) and `idm-5` (the idm-1..3 records
  kept placeholder SHAs). Both fixed. **No part of this plan is unreviewed now**, and the
  destructive half the second pass was commissioned to scrutinise came back sound.
  **D3 IS RULED (owner, 2026-08-14): removing the Entra ID device object is in, as an option.**
  *"yes, add it as an option."* New slice S5, own granular permission
  `IntuneDevicesEntraDelete`, checkbox beside each Intune action plus a standalone action,
  defaulted off. **Its own permission rather than riding `IntuneDevicesPrivileged` because
  `Device.ReadWrite.All` is a DIRECTORY scope covering every device object in the tenant** - the
  widest grant in the module, and not an Intune scope at all.
  **The trap in that slice, verified against Learn before it was written:**
  `managedDevice.azureADDeviceId` is the Entra **`deviceId`**, not the directory **object id**,
  and `DELETE /devices/{id}` wants the object id - Learn's own `device: get` example shows both
  GUIDs on one device. The path form would 404 against a device that is present and fine. The
  plan pins the alternate-key form `DELETE /devices(deviceId='...')`, which takes what the
  module has. This is the same class as the three review findings and was caught the same way.
  **Ordering pinned as Known Failure Class 2:** the Intune action runs first, `azureADDeviceId`
  is captured BEFORE it (a deleted Intune record cannot be read for it afterwards), and the two
  outcomes are reported and audited separately. The half-finished case is the one an operator
  must be able to see.
  **One external prerequisite, not code: a dedicated Entra app registration** with
  `DeviceManagementManagedDevices.Read.All`, `.ReadWrite.All`, `.PrivilegedOperations.All` and -
  after D3 - `Device.ReadWrite.All`, admin-consented, plus its own Delinea secret. **Do not
  collapse the four scopes** - an app registration that never receives
  `PrivilegedOperations.All` cannot wipe a machine even if the code is wrong, and one without
  `Device.ReadWrite.All` cannot touch the directory. That is the point of the split.
  **openreview `codex` (`gpt-5.5-dzs` @ xhigh, grade fallback) over `b868e5c..6aef9e3`:
  `acceptable_with_changes`, THREE findings, all admitted, all folded in** -
  `.agents/review/findings/idm-{1,2,3}.md`, all `[x]` in `.agents/review/index.md`. A fourth
  material change (`$select` at the request boundary, OData escaping) was adopted outside
  intake as correctness inside scope.
  **All three findings were the plan asserting something about code it had not read.** idm-1
  (HIGH): `DeleteAsync` and `PostNoContentAsync` return a bare bool, so the plan's required
  "403 vs 404 vs 5xx are distinct outcomes" was unwritable against the client it named.
  idm-2 (HIGH): the plan said wipe sends no body "which is a full factory reset" - an inference
  stated as verified fact, on the one action that destroys a machine. idm-3 (MEDIUM): AC8
  required a `ProtectedServicer:IntuneDevices` grant that no operator could ever create,
  because `ModuleConfig.razor:650-657` is a hardcoded list this module was not on.
  **idm-3 is the THIRD time this repo has nearly shipped a capability its operators could not
  reach**, after `ppsvc-1` and `pgwt-1`.
  **idm-1 changed a different decision, which is the part worth carrying forward.** Its fix
  adds slice S0 to `GraphTokenClient`, so **the base app version now bumps** - and that
  withdrew the cost argument D2 had been resting on. D2 had been framed to the owner as "no
  email is cheaper because it avoids a base bump"; after S0 that is simply untrue, and D2 is
  now a question about what the affected person should be told and nothing else.
  **NEXT: a go to implement, starting at S0.** No owner decision blocks the start - D1 and D2
  are ruled and D3 has a working default. Versions when the work lands: new module
  `IntuneDevices 1.0.0`, and the **base app version DOES bump** (S0 touches `GraphTokenClient`
  and S6 touches `EmailService`) - the one case so far where adding a module coincides with a
  shared change, so both versioning rules fire on the same work.

- **MIGRATION SIZE CHECK: FIXED AND DEPLOYED 2026-08-13** (`b4029c6`). Migration `1.7.0` ->
  `1.7.1`. No base app bump (module-scoped behaviour only).
  **WHICH ENVIRONMENTS was never stated by the owner. Measured 2026-08-14 instead: BOTH.** Each
  host's `ExchangeAdminWeb.dll` was written 2026-08-13 16:52:57, dev and prod alike - see the
  `Deployed:` entry under `## Blockers`. That is assembly-timestamp evidence, not a module-version
  read. **The app version is unchanged at `2.8.1`, so the sidebar cannot tell this build from the
  previous one**; the only way to confirm which host carries the fix from inside the app is the
  Migration module version reading `1.7.1` in Module Config.
  **The deploy also carried everything else outstanding at the time**, including the Migration
  batch selection and favicon work already on both hosts and the five Risky Users PLAN commits
  (docs only, no code).
  Owner: *"it blocks users whose combined mail + archive size > 100GB, but that's wrong.
  both can be up to 99GB. not combined."* Then: *"fix the size check to be per mailbox,
  not total archive+primary. each mailbox needs to be 99gb or less. combined can be
  whatever."*
  **The rule now lives in ONE place** - `MigrationEligibilityResult.MailboxExceedsQuota` /
  `ArchiveExceedsQuota` / `ExceedsQuota` in `Models/MigrationModels.cs`. The service sets the
  sizes and reads those properties rather than doing its own arithmetic, so the page badge and
  the eligibility verdict cannot drift apart. `CloudQuotaGB` default is now `99` in BOTH places
  that carried `100` (`ModuleCatalog.cs` DefaultValue and the `MigrationService.cs:35` fallback).
  **A STORED CONFIG VALUE STILL WINS AND WAS NOT CHECKED.** If dev or prod has `CloudQuotaGB`
  set in `config/exchangeadmin.db`, it overrides the new default and a stored `100` would allow <!-- lint: allow (owner ruled leave-it, 2026-07-27: runtime config DB is intentionally created outside source control) -->
  a 100 GB mailbox. Read it on both hosts and set it to 99 if present.
  **9 new tests in `MigrationQuotaTests.cs`, proven non-vacuous**: reverting the model to the
  combined rule fails 6 of the 9; restored, all 9 pass. 1701 passed / 0 failed / 3 skipped
  (pre-existing AD skips), build + format + `git diff --check` + ASCII clean.
  **What this fix deliberately did NOT touch, because the go named the size check only:** the
  size-lookup failure path at `MigrationService.cs:185-189,201-208` still marks a user
  **Ineligible** when the on-prem size cannot be read. Since size is not a criterion in the
  source script at all, that is arguably a second invented block - an on-prem connection hiccup
  currently reads to the operator as "this user cannot migrate". Unraised as work; owner's call.
  **Provenance finding worth keeping: the size gate has no basis in the source script.**
  `D:\source\scripts\Exchange\CheckMigrationElligibility.ps1` checks five things - migration in
  progress, already a cloud mailbox, `SEC_ITAR_USERS` membership, not-a-cloud-mailbox on
  move-back, and `AuxArchive` on move-back. All five are implemented. There is NO size check in
  it, and no plan or decision in this repo ever asked for one; it appears in
  `docs/MigrationEligibilityProtectedFlag-Plan.md:48` as an already-existing fact. The owner
  kept the gate and corrected its arithmetic rather than removing it.
  **The one check the script has that the module does not: an on-prem AD account precondition.**
  The script wraps its whole body in `if ($ntid.SamAccountName)` after a `Get-Recipient`, so a
  mail contact, mail user, distribution group or cloud-only object gets no verdict at all. The
  module has no `Get-Recipient` and no SamAccountName gate - it uses the address it is given -
  so those objects receive a normal Eligible/Ineligible answer. Recorded, not worked.
  **Also recorded, not worked: `ExchangeServiceBase.cs:605-609` looks the user up in AD by
  `UserPrincipalName -eq <the email address typed in>`, where the script used SamAccountName.**
  Where a UPN differs from the primary SMTP address the lookup returns nobody, `:614` returns,
  and the excluded-group check is silently skipped for that person - a fail-open on the only
  compliance-motivated rule in the script. The owner confirmed 2026-08-13 that ITAR users ARE
  currently excluded, which is consistent with this: it works for everyone whose UPN matches
  their email and silently misses everyone else. Unproven either way. To settle it, run the
  eligibility check on a `SEC_ITAR_USERS` member whose UPN is not their primary SMTP address.
  **NEXT: nothing on this stream.** Deploy is the owner's call and carries the earlier
  Migration/favicon work with it.

- **RISKY USERS MODULE: PLAN DRAFTED AND REVIEWED, AWAITING OWNER GO, NO CODE.**
  `docs/RiskyUsersModule-Plan.md` (`a2c4c77`, revised through `a00f250`).
  Owner request 2026-08-12: *"explore adding a module to this app that can managed Risky
  Users in Azure"*, then *"plan it, review the plan with codex, and add it to the list of
  things to implement."*
  New module `RiskyUsers`, Entra ID Protection via Graph v1.0. Independent of the two
  group plans - no shared code, no ordering constraint either way.
  **Two external prerequisites, neither of them code. One is settled, one is not.**
  (1) **Microsoft Entra ID P2 - SATISFIED, owner-confirmed 2026-08-12.** The API requires
  it and errors without it. **I raised it as a thing to verify and the owner's answer was
  that asking was the mistake: *"that's why I asked module. if we didn't have risky users
  to manage, I wouldn't building manage risky users module."*** The request for the
  module was itself the evidence. Do not re-open this. (2) A **dedicated app
  registration** with `IdentityRiskyUser.Read.All` (plus `.ReadWrite.All` only if D1
  takes the write phase), admin-consented, and its own Delinea secret. **This one is
  genuinely outstanding** and blocks S2.
  **All endpoints are v1.0**, so `GraphTokenClient`'s hardcoded base URL
  (`Services/GraphTokenClient.cs:16`) needs no change and no NuGet package is added.
  Verified against Microsoft Learn 2026-08-12, not from memory.
  **D1 IS RULED (owner, 2026-08-12): remediation IS in scope.** *"yes, manage means
  manage, not read-only view."* S5-S7 are live slices, and
  `IdentityRiskyUser.ReadWrite.All` is a required permission rather than a conditional
  one. **My read-only-first recommendation was offered twice and declined twice** - the
  word "manage" was in the original request and was reaffirmed when the narrower option
  was put explicitly. Slice ORDER is unchanged (S1-S4 before S5-S7, because the write UI
  attaches to rendered rows); what changed is that shipping the read phase alone is not a
  finished deliverable. Do not re-propose the split.
  **D2 RULED 2026-08-31 (owner: "it should be logged, but not alert emailed"): reads
  audit, never alert-email.** Recorded in `.agents/decisions.md` 2026-08-31; AC17
  asserts the audit-only shape. The pre-ship gate is cleared - the module waits only
  on an implementation go and the owner-side Entra app registration.
  **The design constraint that shaped the plan: risky users are CLOUD identities.** The
  repo's group, OU and SamAccountName protection rules all evaluate from an on-prem DN
  and structurally cannot match a cloud-only principal (Constitution, Protected
  Principals, final bullet). `MfaReset` hit this exactly - its catalog comment records
  that before `1.2.0` an AD-only lookup reported every cloud-only user as "no AD object"
  and skipped the check, leaving protection "close to inert" in a Graph module whose
  normal input is a cloud identity. The write phase must use the `MfaReset.razor:262-364`
  two-branch shape, and can improve on it: `riskyUser.id` IS the Entra object id, which
  `MfaReset` never has.
  **Two API-shape traps the plan pins.** The three action endpoints take `userIds` as an
  ARRAY and return one bare `204` for the whole batch with no per-user body - Known
  Failure Class 2 written into the API itself, so this module calls them one user per
  request. And `GraphTokenClient` prepends a base URL to a relative path while
  `@odata.nextLink` is ABSOLUTE, so lists past the 500 cap are unreachable; the plan
  makes truncation visible rather than teaching the shared client to page, because that
  would be a base-app-version change to a file two other modules use.
  **openreview `codex` (`gpt-5.5-dzs` @ xhigh, grade fallback) over `d877294..a2c4c77`:
  `acceptable_with_changes`, THREE findings, all admitted, all folded in** -
  `.agents/review/findings/ru-{1,2,3}.md`, all `[x]` in `.agents/review/index.md`.
  **ru-1 (HIGH) is the one worth reading: the plan argued correctly that this module
  meets the alerting clause, then listed D2 as "Blocks nothing".** An interim default and
  a shipped answer one line apart in the same document. D2 is now a pre-ship gate carried
  in three places. ru-2 (MEDIUM): S1 registered `AddSingleton<RiskyUsersService>()` while
  S2 introduced the type, so the first commit would fail CS0246 - a slice boundary drawn
  on conceptual grouping instead of compilation order. ru-3 (MEDIUM): the test plan
  pointed at `GraphTokenClientTests.StubHandler`, which is `private sealed`, and never
  mentioned that the descriptor breaks the hardcoded module and alias counts at
  `ModuleCatalogTests.cs:16,109` - the plan reasoned about tests to ADD and not about
  tests the change BREAKS.
  **All three would have been paid for during implementation, not before it.** Two of
  them are compile and test failures on the very first commit.
  **NEXT: a go to implement, starting at S1.** No owner decision blocks the start - D1 is
  ruled and D2 is a pre-ship gate, not a start gate. Two things are outstanding but
  neither stops S1-S4: the app registration plus its Delinea secret (blocks S2's first
  live call, not the code), and D2 (blocks marking anything `Implemented`).
  Versions when the work lands: new module `RiskyUsers 1.0.0` if S1-S7 land before any
  deploy, else `1.1.0` for the remediation half; base app version UNCHANGED (adding a
  module does not bump it).

- **NESTED GROUP MEMBERSHIP: IMPLEMENTED 2026-08-27 (owner goal-directive the same day). ON
  DEV since 2026-08-31 (the `2.10.0` deploy); NOT on prod.** S1 `386e8d2`, S2 `695e73f`, S3 `4fc9d3d`, S4 `3f2ab21`, S5a `ba3b6c8`,
  S5b `8c4042c`, S5c `a014068`; S6 is the commit that set this status. Versions: app `2.9.0`,
  `SelfServiceGroups 1.4.0`, `GroupManagement 2.3.0`. Range reviews (codex gpt-5.6-sol@xhigh,
  per-major-item): S1+S2 clean; S3+S4 raised gmn-4/gmn-5 (both MEDIUM, fixed and verified);
  S5a-S6 raised gmn-6..gmn-9 (two HIGH - including a real resolved-USER protection bypass in
  the new write paths - and two MEDIUM; all four fixed one commit each, `0b4b72e` `b8379dc`
  `dc503e1` `1c47d64`, all verified by codex with independent guard proofs). Every review
  loop on this stream is CLOSED. The plan's manual checks are NOT run - they need a deployed
  instance.
  `docs/GroupMemberNesting-Plan.md` (`074bfdb`, revised through `c7897d1`).
  Owner report 2026-08-11: *"group self-management module needs to handle nested groups.
  when trying to add a group to a group, nothing resolves."*
  **Not a defect - `SelfServiceGroups` is user-only in four places by construction**
  (typeahead `ObjectKind="User"`; `AdOwnershipFilter.cs:97` `objectCategory=person`;
  `IsMemberOfGroup` on `Get-ADUser`; `GroupMemberClassifier` removable=user-only). The
  operator saw *"did not match exactly one user"*, which reads as a typo rather than a
  scope limit.
  **Owner rulings D1-D5, all in the plan (canonical there):** self-service NEVER adds a
  group (ITSD ticket instead) and says so up front; it MAY remove one behind a warning
  that re-adding needs a ticket; `GroupManagement`, being admin-audienced, gets full
  group add/remove; the shared protection blind spot is closed rather than worked around;
  the servicer override for `GroupManagement` needs no code.
  **The find that made this more than a UX change: `ProtectedPrincipalService.cs:747`
  runs `Get-ADUser` to ask whether a target sits inside a protected group.** Hand it a
  group DN and AD returns zero rows with no error, which `:761` records as "no match" -
  a silent ALLOW, not a fail-closed refusal. Harmless today because nothing can target a
  group; live the moment the admin module can. **This is the repo's fail-closed rule
  inverted in a shared file, and it was found by reading the call, not by any test.**
  **D5 corrects a premise in the owner's own request.** The servicer override for
  `GroupManagement` already exists - `GroupManagementService.cs:16,84` and
  `ModuleConfig.razor:655`. What is missing is a granted group, which is a Module Config
  action, not code. No module has a `ProtectedServicer:GroupManagement` row today.
  **openreview `codex` (`gpt-5.5-dzs` @ xhigh, grade fallback) over `618235e..074bfdb`:
  `acceptable_with_changes`, THREE findings, all admitted, all folded in** -
  `.agents/review/findings/gmn-{1,2,3}.md`, all `[x]` in `.agents/review/index.md`.
  **All three were the same shape and it is worth naming: a correct goal wired to a
  mechanism that cannot reach it.** gmn-1 (HIGH): S1 made the protection check
  group-aware, but `GroupManagementService.CheckProtectedAsync` filters the group out one
  call earlier via a user-only resolver, so the fix landed below the layer that drops the
  target - AC13 would have failed with every S1 test green. gmn-2 (HIGH): the cycle
  guard's LDAP filter asked the MIRROR of its own stated question, so it would refuse
  legitimate adds and allow real cycles, and it sat in the page while the write is in the
  service - the exact page-only shape `GroupManagementService.cs:36-38` records this
  module already shipping and being bypassed. gmn-3 (MEDIUM): the picker returned a bare
  sAMAccountName while group search is deliberately forest-wide, so a chosen WINROOT group
  could resolve to its ANALOG namesake.
  **None of the three would have been caught by implementing the plan faithfully - a
  faithful implementation is what produces them.** Reviewing the plan before writing code
  is what made them cheap.
  **D6 closed the last open question and the plan is APPROVED.** Owner: *"same as for
  users"* - a group member notifies on the existing `NotifyAffectedUser` predicate with no
  class check added; the group's `mail` is the address, and no `mail` means no
  notification, exactly as for a user. **I had raised this as a fork with a recommendation;
  the owner's response was that it was ceremonial and did not need their focus.** The
  reusable rule: where an existing predicate already answers the question, applying it is
  the work - a fork is only warranted when the options genuinely diverge.
  **2026-08-28 validation finding, FIXED - on dev since 2026-08-31, not on prod: both member
  listings faulted on a cross-domain nested member.** `Get-ADGroupMember` makes ADWS resolve every member
  server-side and faults the whole read when a member sits in another forest domain the module
  credential cannot chase - `Organization Management` (WINROOT), nested in `ExchangeWebAdmins`
  since 2026-05-12, broke the self-service member list on dev (ADWS `GetADGroupMemberFault`;
  the same read succeeds under an operator identity, so it is credential/chase-dependent).
  **NOT a 2.9.0 regression: the failing code is byte-identical in 2.8.1** - the nesting
  validation simply pointed the list at a nested-group case for the first time. Both group
  modules now read the group's `member` attribute (the Comms10k pattern, which also lifts the
  cmdlet's 5000-object cap) and resolve each member routed to its own domain; an unresolvable
  member degrades to a DN-named read-only row rather than failing the list.
  `GroupManagement 2.3.1`, `SelfServiceGroups 1.4.1`, no base bump.
  **Review loops CLOSED 2026-08-28.** lst-1..3 and pgwt-4..9 all fixed and verified -
  the seven substantive fixes ACCEPTED guard-confirmed in an OWNER-RUN interactive codex
  round (`.agents/review/manual-verify.*`) after the headless workspace-write sandbox
  fault (still recorded in `.agents/machines.md` - probe before the next headless
  verification dispatch). pgwt-3 remains declined at intake
  (`.agents/review/pgwt-3.contested.md`), owner-overrulable any time.
  **Listing fix VERIFIED on dev 2026-08-31** (browser-driven, owner-attended):
  ExchangeWebAdmins lists 13/13 members including the WINROOT `Organization Management`
  nested group, in BOTH group modules. **NEXT: the remaining nesting manual checks** (real
  nested add/remove on a throwaway group - owner's, it writes AD - and the cross-domain
  picker case). All review loops closed - gmn-4 through gmn-9 fixed and verified.

- **PROTECTED ON-PREM GROUPS AS WRITE TARGETS: IMPLEMENTED 2026-08-28 (owner go the same
  day: "continue with the next task"). ON DEV since 2026-08-31 (the `2.10.0` deploy); NOT
  on prod.** S1 `2984df0`, S2 `1217e14`, S3
  `1f8f863`, S4 `645bd37`; versions app `2.10.0`, `GroupManagement 2.4.0`,
  `SelfServiceGroups 1.5.0`; suite 1829/0/3, every slice non-vacuity-probed, M365
  verified untouched over the range. Canonical detail (slices, the target-gate rule set,
  the AC6/AC8 reconciliation recorded before code) lives in the plan's Status and
  Revision 2026-08-28 sections.
  **AC4 REVERSED 2026-08-31 (owner ruling during dev validation, `.agents/decisions.md`):
  self-service is never gated by Protected Group Targets - owners always edit owned
  groups there. S3's gate and its test file removed, `SelfServiceGroups 1.6.0`, ON DEV
  since 2026-08-31 (second same-day deploy, verified from the live page: app 2.10.0,
  SelfServiceGroups 1.6.0). The GroupManagement admin gate stands - it guards the app's
  privileged credential, the real boundary. The owner then populated Protected Group
  Targets on dev with real groups that STAY (2026-08-31) - the feature is live in
  anger on dev; prod still has none of this.**
  **NEXT: dev manual checks - browser-driven 2026-08-31 (owner-attended): listing fix
  verified in BOTH modules (13/13 incl. the cross-domain group); admin refusal checks
  interrupted by the owner mid-run and superseded by the AC4 ruling; Event Log CSV
  checks not run. The `ADEXNLQ_Users` test row was removed from Protected Group
  Targets by the owner the same day - dev settings are back to the pre-test state.**
  `docs/ProtectedGroupWriteTarget-Plan.md` (`503c1a8`, revised `7c5f8a6`,
  scope narrowed after).
  **Found by the owner reading the nesting plan, and it is the larger hole of the two.**
  **The group modules protection-check the MEMBER being added or removed and never the
  GROUP being written into.** Protection stops you touching a protected person and does
  nothing to stop you granting an ordinary person protected access. **An operator with
  `GroupManagementOnPrem` can add any unprotected account to `Domain Admins`** with
  `Domain Admins` listed as protected and no gate firing
  (`GroupManagementService.cs:253,304`; the page delegates and pre-checks nothing by
  design, `GroupManagement.razor:271-276`). Self-service has the same shape, gated on DACL
  ownership only.
  **SCOPE IS ON-PREM ONLY. Owner, 2026-08-11: *"we're not touching the cloud groups
  module."*** An earlier draft covered `M365GroupManagement`; **that was scope I added
  unasked** while surveying which modules shared the defect, and the owner removed it -
  *"who's talking about o365 groups and why? that wasn't part of my prompt."* **The rule
  it earns: a survey that finds more instances of a defect is not authorization to fix
  them.** Report them and let the owner choose. It cost a third of a review pass and a
  plan section that had to be cut.
  **UNSCHEDULED, NOT IN ANY PLAN, recorded so it is not lost: a protected M365 group can
  be renamed or DELETED outright.** `M365GroupManagementService.UpdateGroupAsync:125` and
  `DeleteGroupAsync:143` have no protection gate of any kind; the page gates on a ticket
  number only (`M365GroupManagement.razor:286`). Adding an OWNER to a protected M365 group
  is ungated too (`:255,276`). **And it cannot be fixed by config alone: an M365 group
  cannot be marked protected at all** - both admin pickers are AD-only
  (`AdminSettings.razor:144,172`) and `AddValidatedAsync:660` refuses anything
  `ADSearch.ValidateExists` cannot resolve. Nobody is working this.
  **Hard dependency on the nesting plan's S1.** Without the group-aware check and the DN
  self-match, every gate this plan adds returns "not protected" for a group and the whole
  change is inert while appearing to work. AC6 pins it: reverting S1 must make a test here
  fail.
  **openreview `codex` over `2eedaa9..503c1a8`: `acceptable_with_changes`, two findings,
  both admitted** - `.agents/review/findings/pgwt-{1,2}.md`. pgwt-1 (HIGH) was entirely
  about M365 and is **mooted by the scope cut**; its record is kept because the gap is real
  and unowned, and the criterion it earned survives as AC7. pgwt-2 (MEDIUM) applies
  unchanged: the plan reused a DN-only resolver, and `CheckPatternMatches:612-613` returns
  at its first line when `SamAccountName` is empty - so a group protected by `adm-*` would
  read as unprotected with every `Groups`-list test green.
  **The review's recommendation mattered more than its findings: settle the target identity
  model BEFORE implementation.** **T0: a separate Protected Targets list that reinterprets
  nothing already stored.** Re-reading the existing `Groups` list as target protection would
  make every broadly-listed group unmanageable the moment the build deploys - the `sidf-1`
  shape. AC8 is the anti-lockout criterion.
  **NEXT: owner go.** No open question in the plan.

## Next

**THE PAUSE IS LIFTED. Owner, 2026-08-27: the token budget was reset early, so the
2026-09-01 restart date no longer applies. The queued plans below start on a normal
per-plan owner go, as ever.** (History: work was paused 2026-08-12 because the August
AI budget was ~90% spent. Two owner-directed exceptions were taken during the pause:
the Migration size-check fix, 2026-08-13, and the Event Log CSV Ticket implementation,
2026-08-27, both in `## Now`.)

**What "ready to go" means here, and all of it is FREE of AI budget** - four items, all
owner-side, none of them needing an agent:

1. ~~A go on `docs/ProtectedGroupWriteTarget-Plan.md`~~ - **DONE: implemented 2026-08-28**
   (see `## Now`); nothing owner-side remains on it except the deploy-time manual checks.
2. **A D2 ruling on `docs/RiskyUsersModule-Plan.md`** - do risky-user reads alert
   administrators? Three options are written out in that plan's `## Owner decisions`.
   It is a pre-ship gate, so leaving it unruled would stall the work at the very end
   rather than the start.
3. **The Risky Users Entra app registration** plus its Delinea secret
   (`IdentityRiskyUser.Read.All` and `.ReadWrite.All`, admin-consented). Blocks the
   first live Graph call. S1-S4 can be built and tested without it.
4. **The Intune Devices Entra app registration** plus its Delinea secret
   (`DeviceManagementManagedDevices.Read.All`, `.ReadWrite.All`, `.PrivilegedOperations.All`
   and `Device.ReadWrite.All`, admin-consented). Blocks the first live Graph call, not the
   build. Keep the four scopes distinct. The fourth is a directory scope, wider than the other
   three, and is the one to weigh before consenting.

With those done, the remaining plans are cold-startable at any time with no
conversation needed: `docs/RiskyUsersModule-Plan.md` at its S1 and
`docs/IntuneDeviceManagement-Plan.md` at its S0 - independent of everything else and of
each other.

**Queue status (corrected 2026-08-28, pgwt-9):**

1. `docs/GroupMemberNesting-Plan.md` - **IMPLEMENTED 2026-08-27** (see `## Now`); manual
   checks ride the next deploy. Do not restart.
2. `docs/ProtectedGroupWriteTarget-Plan.md` - **IMPLEMENTED 2026-08-28** (see `## Now`);
   manual checks ride the next deploy. Do not restart.
3. `docs/RiskyUsersModule-Plan.md` - **Scope settled, awaiting a go to implement.** New
   module, independent of 1 and 2. D1 ruled (remediation in scope); D2 open but a
   pre-ship gate, not a start gate. Start at S1. See the entry in `## Now`.
4. `docs/IntuneDeviceManagement-Plan.md` - **Draft, reviewed, awaiting a go. D1, D2 and D3 all
   ruled; no owner decision is outstanding.** New module, independent of 1-3. Start at S0.
   See the entry in `## Now`.
5. `docs/TokenBudget-Plan.md` - **Draft, awaiting a go. D1 ruled, D2 withdrawn; no owner
   decision is outstanding.** Not a feature: how the other four get implemented, plus
   `tools/Get-TokenUsage.ps1` and a tracked baseline. Independent of 1-4 and worth landing
   first, since it changes what the others cost. Start at S1. See the entry in `## Now`.

All of the above are docs-only so far.

*Push status is deliberately not recorded here.* Git owns it and sessions check it live
(`git ls-remote origin master` against `git rev-parse HEAD`) - `.agents/playbooks/drift.md`,
2026-07-11 ruling. Successive revisions of this paragraph recorded a count, then a sha, each
stale within hours; the rule is that the fact does not belong in a state file at all.

**Do not re-derive the reviewer transport.** `.agents/review/harnesses.local.json` is a
current cache hit for `codex-cli 0.147.0`; both openreview passes this session ran clean
through it. The `refresh_token ... revoked` line on stderr is documented noise on the
API-key path and did not affect either run (exit 0, `capability_ok: true` both times).

**Migration batch selection is DONE, accepted, and deployed to both** (app `2.8.0` / Migration
`1.7.0` at the time; the full record is archived in `docs/history/state-archive.md`, Archived
2026-08-14). Nothing outstanding on it.

**Dev and prod are level** -- the version is owned by the `Deployed:` entry under `## Blockers`.
Everything below is the protected-principal stream, which is code-complete, deployed, and now
configured; only its manual checks remain on it.

**The servicer group is `ANALOG\ExchangeWebAdminsExecSupport`** (SID
`S-1-5-21-8915387-325452579-1788637320-710891`), read from the live `section_access` table in both
`config/exchangeadmin.db` files on 2026-08-11. It holds three `ProtectedServicer:` grants - <!-- lint: allow (owner ruled leave-it, 2026-08-11: untracked environment database file) -->
`MailboxPermissions`, `CalendarPermissions`, `OutOfOffice` - and, after the owner closed the gap
below on 2026-08-11, the matching module grants for all three plus `CalendarPermissionsOnPrem` and
`MailboxPermissionsOnPrem`. Re-read from both live stores after the change: every servicer grant
now has its module grant behind it, identically on dev and prod. The other nine servicer-capable
module ids
(`ADAttributeEditor`, `EmergencyDisable`, `MfaReset`, `Comms10k`, `GroupManagement`,
`M365GroupManagement`, `LicensingUpdates`, `Migration`, `SelfServiceGroups`) have no
`ProtectedServicer:` row anywhere; recorded as scope, not oversight.

1. **Owner: the group has the CalendarPermissions servicer grant but no CalendarPermissions module
   grant.** A member who is not also in `ExchangeWebAdmins` or `ExchangeWebPerms` cannot open that
   page, so that servicer grant is unreachable. Either add the module grant or drop the servicer
   row - as configured it is a grant that nobody can use. Group membership was not checkable from
   the app host (no AD cmdlets), so whether any member is affected is unverified.
   **The owner ruled 2026-08-11 that the gap itself is a UI problem, not a config one: a servicer
   grant conveying no module access must be visible where the grant is made.** Done in `46c8257`
   (app `2.8.1`, deployed to both 2026-08-11) - Module Config now states it in the standing warning,
   flags each affected row with a "no module access" badge, and raises a callout when any row is
   flagged. The
   check compares stored SIDs and does not expand nested membership, so it can flag a group that
   reaches the module through another; the wording is conditional for that reason, and every
   genuinely stranded grant is still flagged.
   **The config gap that prompted it is CLOSED** (owner, 2026-08-11): Exec Support now holds
   `CalendarPermissions`, and the on-prem pair as well. Nothing should be badged today - which also
   means the badge has never been seen firing on a real row, so its rendering is unproven.
2. **DEFERRED to real prod use by the owner, 2026-08-11 - not a task anyone is waiting on. The
   load-bearing manual check: a member of the servicer group acts on a protected principal, the
   action SUCCEEDS, and the audit record names the group that permitted it.** Nothing automated
   proves the capability works end to end - every guard is either a source-level tripwire or a
   decision tested in isolation. Worth doing on a module with a page gate (AD Attribute Editor,
   where the operator should also see the override banner) AND a batch module (Migration or
   Licensing), since those took different implementation shapes.
   **What deferring costs, so a later reader can weigh it:** the first real exercise of this
   capability will be someone doing actual work under time pressure, and if the chain is broken they
   meet a refusal then, not in a test. That is the owner's accepted trade, not an oversight.
3. The inverse, and just as important: **an operator NOT in the group is still refused** on the
   same target, and the refusal is audited.
4. Also unverified on a real run: the per-target notes in a batch. A Migration batch mixing a
   protected-and-serviced target with an ordinary one should produce one note NAMING that target,
   not a batch-level "something was serviced".

**A caution that survived this whole work stream and still applies:** a green suite in this repo
says nothing about what an operator sees. There is no bUnit harness, so no test renders a page -
and every one of the three review findings lived in a page or a call site, in code whose commit
message claimed it worked.

The owner decision that WAS outstanding - which group gets the servicer grant - is settled:
`ANALOG\ExchangeWebAdminsExecSupport`, on three modules, live in both config databases, each with
its module grant behind it. What is still unknown is **who is in it**, which is an AD question this
host cannot answer, and whether the capability works for a real member - check 2, deferred to prod
use.

**With that, nothing on this work stream is waiting on anyone.** Checks 2-4 are deferred by owner
decision, not queued. Treat the stream as closed unless a real prod run turns something up.

## Blockers

None live. The queued plans are waiting on an owner go, which is a gate, not a blocker -- see
`## Next`.

**Deployed versions are owned by the single `Deployed:` entry below.** The older per-work-stream
entries that used to sit here recorded where each stream stood *when it landed*; they are
history, not current state, and were never maintained. They are archived verbatim in
`docs/history/state-archive.md` (Archived 2026-08-14). Their unrun manual checks are owned by
their own `docs/*-Plan.md` files and consolidated for the older streams in
`docs/DevValidation-2.3.34.md`; two of them -- `docs/OperatorEmailResolution-Plan.md` and
`docs/ProtectedPrincipalResolution-Plan.md` -- also record that no independent implementation
review was ever obtained.

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

- **MessageTrace null-pipeline-row NRE — FIXED in repo 2026-07-29 and now on BOTH instances.**
  **Basis corrected 2026-08-14:** this entry read "on dev in `2.3.31`, NOT on prod (prod is
  `2.3.30`, still carrying the defect)", which is falsified -- both hosts run `2.8.1`
  (`Deployed:` entry above), so the promotion this entry was waiting on happened.
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
  **OPEN:** live re-run of the failing search, now possible on either instance. The "then promote
  to prod" half of this item is done. **OPEN (OQ-1, non-blocking):** why EXO emits a null row at
  all is undiagnosed; the guard is correct regardless.

- **App version:** owned by `<VersionPrefix>` in `ExchangeAdminWeb.csproj` -- read the number
  there, never from here. The per-release history of that number is archived verbatim in
  `docs/history/state-archive.md` (Archived 2026-08-14).
- **Deployed: dev `2.10.0` (DLL written 2026-08-31 08:42), prod `2.8.1` (unchanged,
  2026-08-13 16:52:57)** -- re-verified from both assemblies 2026-08-31; push also verified
  (both remotes at local HEAD). **The 2026-08-31 deploy was DEV ONLY.** Dev `2.10.0` carries
  over prod: nesting (app 2.9.0 work), the Event Log CSV ticket column, the cross-domain
  member-listing fix with the lst-1..3 review fixes, and the protected write-target feature
  with the pgwt-4..9 review fixes. Prod promotion is the owner's call after the dev manual
  checks.
  **That timestamp is the 2026-08-13 Migration size-check deploy, and it is on BOTH hosts** --
  the question `## Now` records the owner as never having answered. This is assembly-timestamp
  evidence only; the Migration module version was not read off either host, and the app version
  is unchanged from the 2026-08-11 `2.8.1` build, so nothing in the sidebar distinguishes them.
  **`2.8.1` carries, over `2.8.0`:** the Module Config servicer-grant warning (`46c8257`) -- the
  editor states that a servicer grant conveys no module access, badges any servicer group with no
  direct grant on the module's main permission, and raises a callout when a row is flagged. No
  authorization decision changed.
  **Verifying a Razor page change from the deployed DLL needs care, and a naive string probe lies
  three ways:** assembly literals are UTF-16 (a UTF-8 read finds nothing), `-match` is
  case-sensitive against the wrong encoding, and Razor splits literal markup at every `@expression`
  -- so a sentence interpolating `@module.DisplayName` is never one contiguous string. Probe short
  fragments that sit between expressions (`badge bg-danger`), and compare the deployed DLL against
  a LOCAL build of the same commit rather than against expectations. Method names are compiled away
  entirely and prove nothing either way.
  **Superseded deployed-version records (`2.8.0` and earlier) are archived verbatim** in
  `docs/history/state-archive.md` (Archived 2026-08-14).
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
   **NO LONGER "the next code task" - resolved by the owner 2026-08-14 into two halves with
   different fates.** The item predated the four-plan queue and the pause and was never
   re-prioritised against them; the `drift` sweep flagged the contradiction.
   `SectionAccessGroupDirectory` is DONE (`6642587`, via `docs/CoverageRatchetRepair-Plan.md`).
   The remaining two files split cleanly, and the split is the ruling:

   **(a) `ProtectedPrincipalService` (63%) - ROLLED INTO THE QUEUED PLANS. Not separate work.**
   All four queued plans modify and test it (verified 2026-08-14 by reading each plan), and
   `docs/GroupMemberNesting-Plan.md` S1 changes this exact file at `:747`. Doing a standalone
   coverage refactor first would collide with S1 rather than help it. Coverage rises as a
   by-product; **after those plans land, re-measure on a GREEN CI run and raise the ratchet** -
   a one-line diff, per the instructions inside `.agents/review/coverage-floor.txt`.

   **(b) `PermissionValidator` (46%) - the real remaining gap, and nothing in the queue closes
   it.** No queued plan touches the file, and it is one of the paths the coverage floor gates
   (`tools/Test-CoverageFloor.ps1:72`). It is credential-carrying and on a live request path, so
   the work is the `ISectionAccessDirectoryCommands` seam extraction again.
   **Not small, and it needs its own plan** - `docs/ProjectConstitution.md` requires a written
   plan for authorization changes, and this is the authorization core. Queued **behind** the four
   plans; it is not "next" and must not be re-labelled as such without an owner ruling.
   Not startable on the August remainder: the comparable `SectionAccessGroupDirectory` work
   landed on 2026-08-05, a $395 day.

**-0.9. Eyeball a disabled submit button on dev** (accent, not blue). No plan needed -- it is the
   verification step for work already landed. **The deploy half is done:** both instances are
   deployed (version owned by the `Deployed:` entry under `## Blockers`), so only the look is
   outstanding.

**-0.4. PROD carries months of unvalidated work** -- through the current deployed build (version
   owned by the `Deployed:` entry under `## Blockers`), and its manual checks have never been
   run. Highest-consequence single check:
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
5. **Remove the redundant sidebar Home link** (owner, 2026-08-27). `NavMenu.razor:14` (the brand
   link, `Application:Name` falling back to "Admin Portal") and `NavMenu.razor:23` (the `Home`
   `NavLink`) both target `href=""` -- two controls, one destination. Owner's call is to drop
   `Home` and keep the brand link. Shared layout, so this is a base app version bump and no
   module bump. UI-only; no plan required beyond an owner go, but note the repo has no bUnit
   harness, so nothing automated will prove the sidebar still renders -- eyeball it on dev.
   Also check `nav-home` CSS for a rule that becomes dead.
6. **CSV export for five modules that have none** (owner, 2026-08-27): `DhcpAuthorization`
   (`ModuleCatalog.cs:467`) exports the current authorized-server list; `NamedLocations` (`:430`),
   `BlockedSenders` (`:265`), `BitLockerRecovery` (`:484`) and `Migration` status (`:174`) export
   their result sets, with the export offered only when results are present. Needs a plan: five
   modules is five module version bumps, and if the export goes through a shared helper it is a
   base app bump as well. Two things the plan must settle before code -- whether these reuse the
   Event Log CSV path (see `docs/EventLogCsvTicket-Plan.md`, implemented 2026-08-27) or each
   module rolls its own, and whether a BitLocker recovery-key export is allowed to contain the
   keys at all. Interacts with item 7: whatever ticket field BitLocker gains should appear in its
   export.
7. **Mandatory Ticket field on BitLocker search** (owner, 2026-08-27). `BitLockerRecovery`
   (`ModuleCatalog.cs:484`) must require a ticket number before the search runs and before any
   result is displayed. Needs a plan: this is an access/audit control on the most sensitive read
   in the app, so `docs/ProjectConstitution.md` governs it, and the plan must say whether the
   ticket is validated for shape only or looked up, and confirm it reaches the audit record.
   `M365GroupManagement.razor:286` already gates on a ticket number and is the existing pattern
   to read first. Module version bump; base app bump only if the gate is factored out for reuse.

Landed items 2, 5 and 6 of the previous numbering (single-room Finder PP gap, GM-3 task set, ASCII
sweep + lint gate) are archived: `docs/history/state-archive.md` (Archived 2026-07-30).

Ops track (not engineering): configure ConferenceRooms AD `DelineaSecretId` in the prod instance
(gates CR-1 in prod); `deploy.ps1` native `-PlanOnly` (workaround: `deploy-pipeline -PlanOnly`).

## Blockers / open gaps

- **OPEN — AccountLockoutRemediation not yet exercised on dev** (owner deferred, 2026-06-29). Run
  the package's own Manual Validation steps (live 4740 read, WinRM, quser/logoff parsing, real
  dry-run+logoff, protected-block) when ready. Gates the rule-4 user-notify decision above.
  Note the module is currently disabled by the owner as unusable in this environment (see
  `## Blockers`).
- **All known protected-principal *coverage* gaps CLOSED (in repo):** GAP 1 (`M365GroupManagementService`,
  2026-06-29), GAP 2 (`MigrationService`, 2026-06-30), GAP 3 (ConferenceRooms Finder bulk,
  2026-07-02), and the single-room Finder page path (2026-07-21, commit 2a97d09 — consolidated
  into `ConferenceRoomProtectionGate`). Every mutating module routes through the gate. Governing
  rule: `.agents/decisions.md` 2026-06-29 + Constitution §Protected Principals. This closes
  *which callers are gated*; GAP 4 below is a defect in *what the gate resolves*.
- **GAP 4 — FIXED IN REPO 2026-07-30 (`2.3.33`) and NOW DEPLOYED to both instances** (the
  deployed version is owned by the `Deployed:` entry under `## Blockers`; the earlier "dev
  `2.3.32`, prod `2.3.30`, still live on both" basis is falsified). **The fix is unproven in the field: its
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
  2026-07-30 and DEPLOYED to both instances** as of 2026-08-04 (the deployed version is owned by
  the `Deployed:` entry under `## Blockers`; the earlier "still live on prod (`2.3.30`)" basis is
  falsified). **Unverified in the field --
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
- Host-local tool facts (`sqlite3.exe` on PATH, Pester/PSScriptAnalyzer versions, which shell the
  suites run under) live in `.agents/machines.md`, not here.
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
  `docs/OperatorEmailResolution-Plan.md` (**Implemented 2026-07-29** -- app `2.3.32`; on both
  instances since 2026-08-04; 8 manual post-deploy checks not run; implementation openreview not
  obtained);
  `docs/ProtectedPrincipalResolution-Plan.md` (**Implemented 2026-07-30** -- app `2.3.33`; on
  both instances since 2026-08-04; 6 manual post-deploy checks not run; no independent review);
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
- Review loop findings ppv-1..4 (2026-07-31): all four fixed and committed; see
  `docs/history/state-archive.md` (Archived 2026-08-14) and `.agents/review/index.md`. Dispatch artifacts (prompt, schema, raw verdict) are tracked at
  `.agents/review/ppvalidation.*` so the pass is reproducible.

## Unrecorded repo memory

- None known. Engineering rules → `docs/ProjectConstitution.md`; module contract →
  `docs/AdminModuleSpec.md`; work-stream history → `docs/*-Plan.md` + git log.

