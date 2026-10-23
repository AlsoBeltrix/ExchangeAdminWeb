# aud-a23q: Migration - an authorization denial on a single batch or user action, or on either create, shows nothing and writes no audit row

**Severity**: MEDIUM - audit of denied actions (Constitution: every denied mutating attempt is audited and
visible). `ExecuteBatchAction` and `ExecuteUserAction` return silently on an authorization denial: no
message, no audit row, while the bulk executors audit a "_Denied" row. The two creates
(`CreateSingleMigrationBatch`, `CreateMigrationBatch`) show nothing on a denial or on a guard return.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: `worktree-agent-ac3fd1dd1db4f9c1d` (forked at `5eac8b25`)
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `3a948129`. One commit `fix(migration): ...
(aud-a23q)` on branch `worktree-agent-ac3fd1dd1db4f9c1d` (forked at `5eac8b25`), after aud-a23o and before aud-a23p (both
edit the page). Module-scoped: Migration (the bump is the supervisor's). `Components/Pages/Migration.razor` in place: 4377
lines before and after, each changed line replaced by one line; none of them registered (the registered lines run to
1540) and no registered snippet changed; no handler, lifecycle method or `Complete(` added. Line numbers at `5eac8b25`.

1. **A denial is shown, audited and ends the activity with its text.** The four silent `return;`s after a failed
   authorization - `CreateSingleMigrationBatch` 2034 and `CreateMigrationBatch` 2175 (MigrationCreate), `ExecuteBatchAction`
   3302 and `ExecuteUserAction` 3366 (MigrationManage) - each become one statement on that line that sets the page's banner
   (`singleCreateResult`, `createResult`, `batchActionResult`) to a failure, sets aud-a23m's local (`createFailure`,
   `actionFailure`) to the same text so the `finally`'s existing `Complete` reports it, and returns. The text: "Not
   authorized to <action>; nothing was done." - <action> is "<audit action> on <target>" for the two executors and "create a
   migration batch" for the creates.
   - The audit row is the bulk executors' (3534-3541): `Audit.LogMigrationAction(actor, ip, "<audit action>_Denied",
     target, false, ticket, errorDetail: "<policy> authorization denied")`, plus `extra` `mutationAttempted: false` (the
     aud-a28 refusal convention); a failed audit write logged and swallowed, as theirs is, so it cannot replace the denial.
     Written through new `MigrationService.RecordDenial` (internal static; the page passes its audit service, logger, actor
     and address) so each line stays one statement. The creates' audit action is "CreateMigrationBatch_<direction>_Denied",
     their target the mailbox checked (single, "(no eligibility check)" when none) or the batch name typed ("(bulk batch)"
     when none).
2. **A guard return says why nothing happened** - `CreateSingleMigrationBatch` 2036-2037 and `CreateMigrationBatch`
   2177-2178: the same shape, the text from new `MigrationService.SingleCreateRefusal` / `BulkCreateRefusal` (pure, the
   guards' own conditions, in their order): no eligibility check has been run; the check did not find the mailbox eligible
   (its status named) / no row of the file is eligible; no ticket number - each "...; nothing was created." Not audited:
   nothing was attempted (the bulk executors' "nothing to do" rule, 3500-3506).
3. Unchanged: every authorized path; the bulk executors.

**Tests** - new `ExchangeAdminWeb.Tests/MigrationDenialTests.cs`, the page's handlers over `MigrationStubExchange` (its
`Page` gains a policy to deny; the recording progress channel, aud-a23i; the audit file read back), fakes only:
- **the finding**: each of the four denials - the banner's text, exactly one "_Denied" row (action, target, ticket, result
  Failed, error "<policy> authorization denied", `mutationAttempted` false), the action never called / no
  `New-MigrationBatch`, the activity (false, the text). Before: no banner, no row, the activity (false, "Nothing was
  done." / "Nothing was created.").
- each guard return (single: no check, an ineligible check, an undetermined check, no ticket; bulk: no check, none
  eligible, no ticket) - its text, no audit row, nothing created, the activity (false, the text);
- a denial whose audit write throws (no audit service) - the banner still says it, nothing escapes;
- controls: an authorized action still runs and audits as before.

**Mutations at the call site** (md5 before/mutated/after, failing tests named, restored, touched, rebuilt, green): Q1 (the
record's) one silent return restored - `ExecuteUserAction`'s; Q2 (the record's) the audit row dropped at one site -
`ExecuteBatchAction`'s. Extras: Q3 the single create's denial silent again; Q4 a create guard return silent again; Q5
`mutationAttempted` dropped from `RecordDenial`.

**Gates** as the brief, plus `ClickGate*`/`ProgressRegistry*` green and `wc -l` 4377; comments after the mutation round.

## Guard proof

Pending.
