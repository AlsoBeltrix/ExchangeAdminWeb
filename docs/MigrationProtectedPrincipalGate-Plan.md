# Migration Protected-Principal Gate — Plan

Status: Implemented (2026-06-30)
App version at draft: 2.3.27 (unchanged — module-scoped change)
Module: `Migration` (Version `1.1.3` → `1.2.0`)
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.

## Problem / Goal

`MigrationService.CreateMigrationBatchAsync` issues `New-MigrationBatch` (both
`ToCloud` and `ToOnPrem`) over a list of target mailboxes with **no
protected-principal validation** — the last remaining gap (GAP 2) from the
2026-06-29 protected-principal sweep (`.agents/state.md`). A migration batch is a
mutating action against the target identities, so per the 2026-06-29 decision
("protected principals are off-limits to every mutating module, no carve-outs")
each target must be gated before any write.

Goal: gate every migration target through the protected-principal check before
`New-MigrationBatch` runs, on **both** the single and bulk creation paths, with
audit and admin notification on exclusions.

## Owner decision driving the design (2026-06-30)

When a batch contains a protected principal among its targets:

- It **must NOT fail silently.**
- One protected principal **must NOT block the whole batch.**
- **Filter out** the protected principals and **clearly and directly explain to
  the operator why** they were excluded.

So: the batch is created for the non-protected targets; excluded targets are named
with the reason, surfaced prominently in the UI, audited, and included in the
admin notification. The single-batch path is the degenerate case — if the one
target is protected, zero remain, so nothing is created and the operator is told
plainly that the sole target was excluded as protected.

## Non-Goals

- No change to eligibility logic, CSV parsing, batch monitoring, or any of the
  other `MigrationService` methods (start/stop/complete/remove/resume). Only batch
  *creation* gains the gate.
- Not a new module; extends existing `Migration`.
- No cloud-identity protection extension. The check resolves against on-prem AD;
  a cloud-only target that AD cannot resolve returns `NotFound` and is treated as
  not protected — the same accepted limitation documented for GroupManagement and
  M365GroupManagement (`docs/M365MemberOwnerManagement-Plan.md` Key Decision). For
  a ToOnPrem (move-back) batch the targets are by definition cloud mailboxes, so
  this limitation is most relevant there; it is an accepted, documented gap, not a
  defect, and matches existing posture.

## Scope of change

### 1. Service — `Services/MigrationService.cs`

- Inject `ProtectedPrincipalService` (constructor + field), mirroring
  `GroupManagementService` (`Services/GroupManagementService.cs:9-24`). Confirm it
  is DI-registered before `MigrationService` in `Program.cs` (it is a registered
  singleton used by GroupManagement / M365GroupManagement / ADAttributeEditor).
- Add a private `CheckProtectedAsync(string identity)` that mirrors
  `GroupManagementService.CheckProtectedAsync` (`Services/GroupManagementService.cs:36-68`)
  verbatim in behavior: resolve via `ResolveWithStatusAsync`; **fail closed** on
  `Unavailable`/`Ambiguous`; on a resolved principal call `CheckAsync` and fail on
  `CheckFailed` or `IsProtected`; block on any exception. Returns `null` when the
  target is clear to migrate, or a `PermissionResult.Fail(...)` describing why.
- Change `CreateMigrationBatchAsync` so that **before** building the CSV /
  `New-MigrationBatch` call it partitions the incoming target list:
  - For each target, run `CheckProtectedAsync`. A non-null result ⇒ excluded.
  - Build two lists: `allowedEmails` and `excluded` (each carrying email +
    reason string).
  - If `allowedEmails` is empty (every target excluded, incl. the single-target
    case), **create nothing** and return a `PermissionResult` that is *not*
    success, whose message names the excluded principals and the protection
    reason. No `New-MigrationBatch` is invoked.
  - Otherwise create the batch for `allowedEmails` only, then return a result that
    reports success **and** the excluded list (so the page can show "created for N,
    excluded M: …"). Exact result shape: extend the returned `PermissionResult`'s
    `Detail`/message to carry the excluded summary, or add an `ExcludedTargets`
    field — decide at implementation against the existing `PermissionResult` /
    `MigrationBatchResult` shape (smallest change that lets the page render it
    clearly). This is a behavior detail, not a scope change.
  - Fail-closed ordering invariant: the protection partition runs and is fully
    decided **before** any side effect (CSV build, `New-MigrationBatch`,
    auto-start, auto-complete). A protected target can never reach the write path.
    (Known Failure Class #1 — side-effect ordering.)
- The gate runs the same way regardless of `MigrationDirection`; both `ToCloud`
  and `ToOnPrem` branches sit downstream of the single partition step.

### 2. Page — `Components/Pages/Migration.razor`

- Both creation handlers (`CreateSingleMigrationBatch` ~`:804`,
  `CreateMigrationBatch` ~`:920`) already re-authorize (`MigrationCreate`), require
  a ticket, audit, and send an admin notification — keep all of that.
- Render the excluded-target information returned by the service **clearly and
  directly** (owner requirement): a distinct, non-dismissable warning block above
  the result, e.g. "N mailbox(es) were excluded from this batch because they are
  protected principals: <list>. They were not migrated. Escalate to an
  administrator outside this tool if migration is required." Not a subtle inline
  note — it must be impossible to miss.
- For the single path, if the one target was excluded, the result block states
  plainly that nothing was created and why.
- No change to eligibility display, CSV upload, or batch monitoring UI.

### 3. Audit — `Services/AuditService.cs` (+ call sites)

- On any exclusion, write an audit row recording the denied/excluded
  principal(s) and reason, in addition to the existing `LogMigrationBatch` row for
  what was actually created. Reuse the existing migration audit helpers; add a
  denial/exclusion row via the same pattern other modules use for protected-
  principal refusals (audit the denial, do not mask it). Confirm exact helper at
  implementation; do not invent a bespoke logger.

### 4. Notifications — `Services/EmailService.cs`

- The admin notification already fires on batch creation. Extend it so the
  excluded principals + reason are included in the notification body (the existing
  `SendAdminNotificationAsync` `details` overload carries arbitrary data — use it;
  do **not** build a bespoke mailer, per the 2026-06-29 mandatory-notifications
  decision). Per that decision, a protected-principal exclusion is a
  security-relevant event and admins must see it.
- No affected-user notification (the excluded user's access did not change; this
  is a refusal, not a permission grant).

### 5. Tests — `ExchangeAdminWeb.Tests/`

New `MigrationService` tests (NSubstitute, mirroring
`GroupManagementServiceTests.cs`):

- Bulk batch with a mix of protected + clean targets ⇒ protected ones excluded,
  batch created for the rest, excluded list reported.
- Bulk batch where every target is protected ⇒ no `New-MigrationBatch` invoked,
  non-success result naming the excluded principals.
- Single protected target ⇒ nothing created, clear refusal.
- Protection check `Unavailable`/`Ambiguous` ⇒ that target excluded (fail closed),
  not migrated.
- Applies to both `ToCloud` and `ToOnPrem` directions (at least one test each, or
  parameterized).
- Each new test proven **non-vacuous** (revert the gate, see the test fail,
  restore) per AGENTS.md Verification.

> Test seam note: the `New-MigrationBatch` PowerShell hop is not unit-tested (no
> new Graph/PS seam is introduced). Tests assert the partition decision and that
> the write path is not reached for excluded targets; the actual cmdlet call is
> covered by manual dev-deploy validation. If the current `MigrationService`
> shape has no seam to observe "was `New-MigrationBatch` invoked", introduce the
> minimal seam needed to test it (e.g. extract the allowed-list decision into a
> testable method) without changing runtime behavior.

### 6. Version — `Modules/ModuleCatalog.cs`

- Bump the `Migration` module `Version` (behavior change to a single module ⇒
  module-version bump only). **App `<VersionPrefix>` does NOT change** — no
  shared/app-wide code is touched. Confirm the current `Migration` version at
  implementation and increment per the two-rule policy
  (`docs/ProjectConstitution.md` §Deployment And Versioning).
- Update `ModuleCatalogTests.cs` if it pins the `Migration` version.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release` then
  `dotnet test ExchangeAdminWeb.slnx` (target `.slnx`; bare `dotnet test` runs
  zero tests).
- `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore` and
  `git diff --check HEAD`.
- New tests proven non-vacuous (revert/restore).
- Manual (deferred to dev deploy; state clearly if not run): create a batch whose
  CSV includes a protected principal — confirm it is excluded, the warning block
  is clearly shown, the rest of the batch is created, the admin email lists the
  exclusion, and audit rows exist for both the creation and the exclusion. Repeat
  for a single protected target (nothing created, clear refusal).

## Resolved decisions

1. **Batch handling on protected target** (owner, 2026-06-30): filter out
   protected principals, create the batch for the rest, and clearly/directly
   explain the exclusion. Never silent; one protected target never blocks the
   whole batch. Single-target-protected ⇒ nothing created, plain refusal.
2. **Protection scope**: reuse the on-prem-AD check as-is; cloud-only `NotFound`
   gap accepted and documented (consistent with GroupManagement /
   M365GroupManagement).
3. **Notifications**: admin notification includes exclusions; no affected-user
   notification (refusal, not a permission change).
4. **Fail-closed**: `Unavailable`/`Ambiguous`/exception ⇒ exclude that target.

## Commit slices (one per landed slice, per AGENTS.md Git Safety)

1. Service: inject `ProtectedPrincipalService`, add `CheckProtectedAsync`,
   partition targets in `CreateMigrationBatchAsync` + service tests.
2. Page: clear excluded-targets warning UI + audit row for exclusions +
   notification body extension.
3. Module version bump + catalog test update + docs (this plan → Implemented,
   `.agents/state.md` GAP 2 closed, `.agents/decisions.md` if a durable decision
   is warranted).
