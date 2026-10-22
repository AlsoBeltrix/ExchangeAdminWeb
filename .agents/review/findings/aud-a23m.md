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

Built as the Plan. `Components/Pages/Migration.razor` is 4377 lines before and after: `git diff --numstat` 42/42, every
change in place, on lines past the last registered one (1540); no registered snippet changed, `ExpectedLineCount`
untouched; each handler's `Complete(` is the one it had, in its `finally`. The fix commit carries no new code comments.

- `CheckSingleEligibility`: the audit (1988-1990) and the notification (1992-1996) each in its own `try`, a throw
  logged (`Logger.LogError` / `LogWarning`); the page catch's audit (2007-2008) the same. Nothing else changed: what is
  audited and notified, and the outcome (aud-a23i), are as they were.
- `CreateSingleMigrationBatch`: `createFailure` "Nothing was created." (2028), the result's outcome once the service
  returns (2047); the notification in its own guard (2076-2080); the catch's message (2084); `Complete` (2089).
- `CreateMigrationBatch`: the same - 2169, 2193, 2222-2226, 2230, 2235.
- `ExecuteBatchAction`: `actionFailure` "Nothing was done." (3297), the action's outcome (3306), the catch (3336),
  `Complete` (3341). `ExecuteUserAction`: 3361, 3370, 3399, 3404.
- `ExecuteBulkBatchAction`: `bulkFailure` "Nothing was done." (3526) - the "no list loaded" return keeps it;
  "Authorization denied." (3533); null for "Nothing to do" (3562); the summary when a batch was refused, null when none
  was (3641); the catch (3660); `Complete` (3665). `ExecuteBulkMailboxAction`: 3692, 3699, 3726, 3785, 3810, 3815.
- `ExchangeAdminWeb.Tests/MigrationStubExchange.cs`: `Email(throws)` - a `RecordingEmail` whose
  `SendAdminNotificationAsync` records the action and, when told, returns a faulted task; `Protection` - per-target
  answers ("protected", "unavailable", "ambiguous", "failed", else clear; "*" for any other target) through
  `StubProtectedPrincipals` (the two virtual lookups) and a real `ProtectedPrincipalServicerService` with no servicer
  group; unset, the service has none, as before; stub `Stop-MigrationBatch`; `New-MigrationBatch`'s full parameter set.

New `ExchangeAdminWeb.Tests/MigrationVerdictAndWriteOutcomeTests.cs`, 6 tests (21 cases):

| Test | Pins |
| --- | --- |
| `AnAuditThatThrows_LeavesTheVerdictStanding_AndTheNotificationStillGoes` (eligible, undetermined) | **part 1, the finding**: with no audit service (its call throws), the verdict and its reasons are the service's, the notification is still sent, the activity outcome is the verdict's, nothing escapes |
| `ANotificationThatThrows_LeavesTheVerdictStanding_AndItsOneAuditRow` (eligible, undetermined) | **part 1**: a throwing notification - the verdict stands; one audit row, its status the verdict's (no second "Error" row) |
| `ACheckThatFailsBeforeTheServiceReturns_WithAnAuditThatThrows_IsStillUndetermined` | the page catch's own audit guarded: Undetermined, the sign-in reason, nothing escapes |
| `EveryWrite_CompletesItsActivityWithItsOutcome` (the six writes, each failing and succeeding) | **part 2, the finding**: a failed write completes its activity (false, the message the page shows); a succeeded one (true, null). Creates fail through the protection stand-in ("unavailable") and succeed through it (clear) |
| `ACreateWhoseNotificationThrows_KeepsItsResult` (single, bulk) | found while planning: the created batch's result stands, the activity succeeds, the notification was attempted |
| `ABulkRunWithNothingToDo_Succeeds` (batches, mailboxes) | "Nothing to do" is an answer (D2(a)): succeeded |

**Against the unfixed code: not runnable as a whole** - the tests read outcomes the handlers did not set apart, so each
behaviour was put back at its own call site instead; each revert fails the tests that pin it.

Mutations at the call site, by the same driver and protocol as aud-a23n's (`round-a23m.ps1`, session scratchpad), over
the 1110 tests of the same filter; every mutant kept the page at 4377 lines, so no `ClickGate*` pin failed for a
reason of its own. Page bytes (LF) before every mutation and after every restore: `417e270b66c2efa29d200edc351f9b94`.

| Mutation | md5 mutated | Failed (of 1110) | Restored, rerun |
| --- | --- | --- | --- |
| M1 (the record's): the audit back inside the verdict's try (its own guard removed) | `754d7546fd2e983b52c3cd51e12bb92c` | 2: `AnAuditThatThrows...` (both) - the verdict replaced, a throw out of the handler | equal; 1110/0 |
| M2 (the record's): `ExecuteUserAction`'s `Complete(true)` restored | `0a33fc842a900451dbd790a4070ec4bf` | 1: `EveryWrite...` (ExecuteUserAction, fails) | equal; 1110/0 |
| M1b: the notification's guard removed | `4811a8957a25e60a2698feeef7360550` | 2: `ANotificationThatThrows...` (both) | equal; 1110/0 |
| M1c: the page catch's audit unguarded | `c82a45c3088dde243089a33b7afdbc04` | 1: `ACheckThatFailsBeforeTheServiceReturns_WithAnAuditThatThrows...` | equal; 1110/0 |
| M2b: `ExecuteBatchAction`'s `Complete(true)` | `dddcbd0a037e30536f268adecebf4e03` | 1: `EveryWrite...` (ExecuteBatchAction, fails) | equal; 1110/0 |
| M2c: `ExecuteBulkBatchAction`'s | `8e3b258b4c18b44f0e2d9506e6266ef4` | 1: `EveryWrite...` (ExecuteBulkBatchAction, fails) | equal; 1110/0 |
| M2d: `ExecuteBulkMailboxAction`'s | `7d260fb2e78df81afc75ae2cf4064ea9` | 1: `EveryWrite...` (ExecuteBulkMailboxAction, fails) | equal; 1110/0 |
| M2e: `CreateSingleMigrationBatch`'s | `e7ad96a6b48937645a9a24c6bd7b7228` | 1: `EveryWrite...` (CreateSingleMigrationBatch, fails) | equal; 1110/0 |
| M2f: `CreateMigrationBatch`'s | `0e977b57be90a56d58daa3a3598f0071` | 1: `EveryWrite...` (CreateMigrationBatch, fails) | equal; 1110/0 |
| M2g: the single create's notification guard removed | `e0a5efa338787c6eb610f70c5e65ea4b` | 1: `ACreateWhoseNotificationThrows...` (single) | equal; 1110/0 |
| M2h: the bulk create's notification guard removed | `42f89aa81d701a82ab41bc05f2ee3397` | 1: `ACreateWhoseNotificationThrows...` (bulk) | equal; 1110/0 |

md5s (LF) of the other files at the fix commit: `MigrationVerdictAndWriteOutcomeTests.cs`
`a567b5aa30e455718c567c88b3fd411f` (the round ran on `f287491d470b9a3c327b7f2129ae78d1`; the format gate then
re-indented its `case` blocks - leading whitespace only, no test changed), `MigrationStubExchange.cs`
`2696ad6939a9cc183e403069dd91f842`.

On the fix commit's tree: Release build 0 errors; the 1110 focused tests 1110/0 (`ClickGate*` and `ProgressRegistry*`
among them); `dotnet format --verify-no-changes` exit 0; `git diff --check HEAD` exit 0; `wc -l` 4377.
