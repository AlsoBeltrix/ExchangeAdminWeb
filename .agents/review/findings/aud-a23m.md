# aud-a23m: Migration - an audit or notification failure replaces a determined eligibility verdict, and the page's writes complete their activity as succeeded on failure

**Severity**: MEDIUM - Constitution: an audit failure must not make an already-completed operation appear
to have failed. (1) The catch in `CheckSingleEligibility` wraps the audit write and the notification as
well as the check, so a failed audit or notification turns a determined verdict into an error. (2) The
Migration page's write actions (create, approve, stop, remove and the rest) still complete their progress
activity `true` on failure; aud-a23i fixed only the loads.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: `worktree-agent-a03d6bd111b571e5a` (forked at `b16898dc`)
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23h/a23i agent, 2026-10-22, candidates 5 and 6 of its hand-back)

## Evidence

At `ab5361c8`: `Services/MigrationService.cs` `CheckSingleEligibility` (the try's extent);
`Components/Pages/Migration.razor`, every write action's `Complete(true)` (line-keyed by
`ClickGateRegistry`, 4377 lines, edit in place only; `ProgressRegistry` tests pin the activity shapes).

## Approach (to be written by the implementing agent before code)

- The verdict is determined first; the audit write and the notification run after it in their own guarded
  steps (the aud-a28q writers never throw; the notification's failure is logged and the verdict stands).
- Each write action completes its activity with its outcome, the aud-a23i load shape; the
  `ProgressRegistry` tests gain the write actions.
- Tests through `MigrationStubExchange` and a throwing notification stand-in; mutations at the call site:
  the audit back inside the verdict's try; one `Complete(true)` restored.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `fffdc451`. One commit `fix(migration): ...
(aud-a23m)` on branch `worktree-agent-a03d6bd111b571e5a` (forked at `b16898dc`), after aud-a23j. Module-scoped:
Migration (the bump is the supervisor's). Page only: `Components/Pages/Migration.razor`, edited in place - 4377
lines before and after, no registered line (they run to 1540) or snippet touched, every `Complete(` the one its
handler had, in its `finally`. Line numbers at `b16898dc`.

1. **Part 1, `CheckSingleEligibility` (1960-2015).** The verdict is what the service returned (or, for a throw before
   it returned, the page catch's Undetermined, aud-a23h). The audit write (1989-1990) and the admin notification
   (1993-1996) each become their own guarded step, on the lines they and the blank and comment lines around them hold:
   a throw from either is logged (`Logger`) and the verdict, its reasons and the activity's outcome stand. The page
   catch's own audit (2007-2008) is guarded the same way, so nothing escapes the handler. What is audited and notified
   is unchanged. (aud-a28q made the writers throw-free; the guard is the page's own, as the Constitution asks: "Audit
   failure must not make an already-completed operation appear to have failed"; "Notification failure must not change
   or mask the backend operation result".)
2. **Part 2, every write completes its activity with its outcome** - the aud-a23i load shape, from a local set where
   the outcome is known, so nothing a later await does can change it:
   - `CreateSingleMigrationBatch` (2017-2091) and `CreateMigrationBatch` (2161-2237): `createFailure`, "Nothing was
     created." until the service returns, then the result's message on a failure and null on success; the catch's
     message on a throw; `activity.Complete(createFailure is null, createFailure)` (2089, 2235). **Found while
     planning:** both send their admin notification outside any guard (2077-2080, 2223-2226), so a notification that
     throws reaches the outer catch and REPLACES the created batch's result with a failure - the part-1 defect on the
     write side, and it would make the activity disagree with the page. Each notification gets its own guard, as
     `ExecuteBatchAction`'s already has (3316-3325), on the lines it and the blank line above it hold.
   - `ExecuteBatchAction` (3289-3343) and `ExecuteUserAction` (3345-3406): `actionFailure`, "Nothing was done." until
     the action returns, then the result's message on a failure, null on success; the catch's message on a throw;
     3341, 3404.
   - `ExecuteBulkBatchAction` (3509-3668) and `ExecuteBulkMailboxAction` (3678-3817): `bulkFailure`, "Nothing was
     done." for the returns that do nothing (no list loaded); "Authorization denied." on the denial; null for "Nothing
     to do" (a success, D2(a)); the run's summary when any item was refused, null when none was; the catch's message;
     3665, 3815.
   - Not changed: what each handler shows, audits or notifies; the comment at 1555-1562 (the writes "still pass
     true") is corrected in the comments commit.
3. **Tests** - new `ExchangeAdminWeb.Tests/MigrationVerdictAndWriteOutcomeTests.cs`, the page's handlers over
   `MigrationStubExchange` with the recording progress channel (aud-a23i), fakes only. The stub gains: a recording
   and a throwing `EmailService` (its `SendAdminNotificationAsync` is virtual); a protection stand-in (a
   `ProtectedPrincipalService` whose two virtual lookups answer from a table, with a real
   `ProtectedPrincipalServicerService` holding no servicer group) so a create can succeed; stub `Stop-MigrationBatch`
   and `New-MigrationBatch`'s full parameter set. A throwing audit is stood in for by no `AuditService` at all - its
   methods are not virtual and its writer swallows its own failures (aud-a28q), so a call on none throws, as any throw
   out of it would.
   - **part 1**: an eligible and an undetermined answer whose audit throws, and whose notification throws - the
     verdict, its reasons and the activity outcome are the service's; the notification still sent after a failed audit;
     no "Could not be determined: <the audit's or the mail's error>"; nothing escapes. A check that fails before the
     service returns, with an audit that throws - Undetermined, nothing escapes.
   - **part 2**: each of the six writes failing - not succeeded, with its failure text; succeeding - succeeded; the bulk
     "Nothing to do" - succeeded; a create whose notification throws - its result kept, succeeded.

**Mutations at the call site** (md5 before/mutated/after, failing tests named, restored, touched, rebuilt, green):
M1 (the record's) the audit back inside the verdict's try (its own guard removed); M2 (the record's) one
`Complete(true)` restored - `ExecuteUserAction`'s. Extras: M1b the notification's guard removed; M2b-M2f each other
write's `Complete(true)` restored; M2g the single create's notification guard removed.

**Gates** as the brief, plus `ClickGate*`/`ProgressRegistry*` green and `wc -l` 4377; comments after the mutation round.

## Guard proof

Pending.
