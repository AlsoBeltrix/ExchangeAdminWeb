# aud-a23q: Migration - an authorization denial on a single batch or user action, or on either create, shows nothing and writes no audit row

**Severity**: MEDIUM - audit of denied actions (Constitution: every denied mutating attempt is audited and
visible). `ExecuteBatchAction` and `ExecuteUserAction` return silently on an authorization denial: no
message, no audit row, while the bulk executors audit a "_Denied" row. The two creates
(`CreateSingleMigrationBatch`, `CreateMigrationBatch`) show nothing on a denial or on a guard return.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23j-n agent, 2026-10-22, candidate 5 of its hand-back)

## Evidence

At `09d3bacb`: `Components/Pages/Migration.razor` around 3301-3302 and 3365-3366 (the silent returns),
2033-2037 and 2174-2178 (the creates' denial and guard returns); the bulk executors' "_Denied" audit rows
for the shape - line-keyed by `ClickGateRegistry`, 4377 lines, edit in place only.

## Approach (to be written by the implementing agent before code)

- Each denial writes the same "_Denied" audit row the bulk executors write (action, actor, target,
  `mutationAttempted: false`) and shows "Not authorized to <action>; nothing was done." in the page's
  banner; a guard return that is not a denial says why nothing happened.
- The activity (aud-a23m) completes not-succeeded with that text.
- Tests through the page's reflection driver with a denying authorization stand-in and the audit capture;
  mutations at the call site: one silent return restored; the audit row dropped at one site.

## Guard proof

Pending.
