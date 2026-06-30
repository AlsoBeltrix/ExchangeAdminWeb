# Migration Eligibility — Protected-Principal Flag — Plan

Status: Draft (awaiting owner approval)
App version at draft: 2.3.27 (unchanged — module-scoped change)
Module: `Migration` (Version `1.2.0` → `1.3.0`, proposed)
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.

## Problem / Goal

Today the **Check Eligibility** step (`MigrationService.CheckMigrationEligibilityAsync`,
single and bulk) asks only Exchange/AD questions and returns one verdict —
`Eligible` or `Ineligible`. It does **not** look at protected-principal status. The
protected-principal gate added for GAP 2 fires only later, at *batch creation*
(`CreateMigrationBatchAsync`), where a protected target is filtered out and reported
after the fact. So a protected-but-otherwise-fine user shows a green "Eligible," the
operator clicks **Create Migration Batch**, and only then learns it was excluded. The
eligibility screen and the gate disagree.

Goal: surface protected-principal status **at the eligibility check**, as a separate
axis from eligibility, so the operator sees it before clicking Create.

## Owner decision driving the design (2026-06-30)

Protected status is **orthogonal** to Exchange/AD eligibility — it does not change the
Eligible/Ineligible verdict:

- **Protected + eligible in Ex/AD** → still shown **Eligible**, flagged as a protected
  principal that must be escalated (cannot be migrated from this tool).
- **Protected + ineligible in Ex/AD** → still shown **Ineligible**, with the same
  protected/escalate flag plus the real ineligibility reason(s).

Create-button behavior differs by entry type:

- **Single-user entry:** a protected principal is treated, for the **Create Migration
  Batch** button, exactly like an ineligible user — the Create card/button does **not**
  appear. (The eligibility label still tells the truth; the action is suppressed.)
- **Group / bulk (CSV) entry:** **no change to the create flow.** The existing GAP 2
  gate already filters protected targets out at creation and reports them ("already
  decided"). The eligibility table simply *shows* the protected flag so it is visible at
  check time instead of only after Create.

## Non-Goals

- No change to the Exchange/AD eligibility logic (cloud-mailbox / in-progress /
  AuxArchive / size-quota / excluded-AD-group rules) or the verdict it produces.
- No change to `CreateMigrationBatchAsync` or the GAP 2 partition/exclude/report
  behavior. The batch-creation gate stays the last-line safety net.
- No second denial audit row or admin alert at *check* time. Check is a read; the GAP 2
  denial audit + admin notification already fire at *create* time and are unchanged. The
  existing eligibility-check audit detail is extended to record protected status.
- No change to bulk create-card gating or the `EligibleUsers` count.

## Design

### 1. Model (`Models/MigrationModels.cs`)

Add to `MigrationEligibilityResult` (backward compatible, defaults preserve current
behavior):

- `bool IsProtected { get; set; }` — true when the target is a protected principal **or**
  protection could not be verified (fail-closed).
- `string? ProtectionNote { get; set; }` — operator-facing reason (the message from the
  protection check, e.g. "This mailbox is a protected principal…" or the
  unavailable/ambiguous reason).

`Status` is left exactly as the Ex/AD logic set it. `IsProtected` is a separate axis.

### 2. Service (`Services/MigrationService.cs`)

Reuse the **existing** protection logic — no new check is written. The existing private
`CheckProtectedAsync(string identity)` returns `null` when allowed and a
`PermissionResult.Fail(...)` when protected / Unavailable / Ambiguous / CheckFailed /
exception (fail-closed). That is exactly the shape needed.

Add a small **testable seam** mirroring the existing `PartitionByProtectionAsync`
pattern:

```csharp
internal async Task ApplyProtectionFlagAsync(
    MigrationEligibilityResult result,
    Func<string, Task<PermissionResult?>>? checker = null)
{
    checker ??= CheckProtectedAsync;
    var block = await checker(result.EmailAddress);
    if (block != null)
    {
        result.IsProtected = true;
        result.ProtectionNote = block.Message;   // Status is NOT changed
    }
}
```

Call it from `CheckMigrationEligibilityAsync` after the Ex/AD work completes (after the
size block, before `return result`), for both directions. `CheckBulkMigrationEligibility-
Async` already loops over `CheckMigrationEligibilityAsync`, so bulk is covered for free.

Fail-closed is inherited: when protection can't be verified, `CheckProtectedAsync`
returns a Fail, so `IsProtected` becomes true with the reason — operator is told to
escalate. Matches the GAP 2 gate's posture.

### 3. UI (`Components/Pages/Migration.razor`)

**Single-user tab:**
- In the result alert, when `singleResult.IsProtected`, show a prominent
  protected/escalate block (the `ProtectionNote` + "Migrating a protected principal is
  not permitted in this tool. Escalate to an administrator outside this tool if migration
  is required."), in both the Eligible and Ineligible branches.
- Suppress the Create card: change
  `@if (singleResult.Status == MigrationStatus.Eligible)` (line ~119) to
  `@if (singleResult.Status == MigrationStatus.Eligible && !singleResult.IsProtected)`.

**Bulk tab:**
- In the results table "Reasons / Notes" column, when `item.IsProtected`, add a visible
  protected marker (e.g. a "⚠ Protected — will be excluded, escalate" line) alongside any
  existing reasons. No change to the row's Eligible/Ineligible styling, to `EligibleUsers`,
  or to the Create card / button. Create flow stays as GAP 2 left it.

### 4. Audit (single-user check, `Migration.razor` ~line 808)

Append protected status to the existing eligibility-check audit detail string
(`IsProtected` + `ProtectionNote`) so the read is recorded. No new audit *event* type and
no denial row at check time.

## Versioning

Module-scoped behavior change → bump `Migration` `Version` `1.2.0` → `1.3.0` in
`Modules/ModuleCatalog.cs` (new visible behavior: protected flag + single-user create
suppression). App `<VersionPrefix>` unchanged (no shared/app-wide change). Per the
two-rule versioning invariant (AGENTS.md #6). **Note:** the open versioning-rule blocker
(`.agents/state.md`) concerns *new modules*, not module-version bumps on an existing
module, so it does not apply here.

## Tests (`ExchangeAdminWeb.Tests/MigrationServiceProtectedPrincipalTests.cs`)

Add unit tests for `ApplyProtectionFlagAsync` via the `checker` seam (same approach as
the existing `PartitionByProtectionAsync` tests — `CheckMigrationEligibilityAsync` itself
runs live EXO and is not unit-testable):

1. checker returns `null` (allowed) → `IsProtected == false`, `ProtectionNote == null`,
   `Status` unchanged.
2. checker returns `Fail("…protected…")` on an otherwise-**Eligible** result →
   `IsProtected == true`, `ProtectionNote` set, `Status` **stays Eligible** (proves the
   orthogonal-axis requirement).
3. checker returns `Fail("…protected…")` on an **Ineligible** result → `IsProtected ==
   true`, `Status` **stays Ineligible**, original ineligibility reason(s) preserved.
4. checker returns a Fail representing Unavailable/Ambiguous (fail-closed) → `IsProtected
   == true` with that reason.

Each proven non-vacuous (revert the `ApplyProtectionFlagAsync` call/assignment, confirm
the test fails, restore).

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release` then `dotnet test ExchangeAdminWeb.slnx`.
- `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore`,
  `git diff --check HEAD`.
- Manual (dev): single-user check of a protected principal that is otherwise eligible →
  shows **Eligible** + protected/escalate flag, **no Create button**; bulk CSV containing
  a protected principal → row shows protected marker, create still excludes+reports it.

## Slices (one commit each)

1. Model fields + service `ApplyProtectionFlagAsync` + wire into
   `CheckMigrationEligibilityAsync` + unit tests.
2. UI (single-user flag + create suppression; bulk table marker) + single-user check
   audit detail.
3. Module version bump `1.3.0`, plan → Implemented, `.agents/state.md` +
   `.agents/decisions.md` records.
