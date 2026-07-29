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
