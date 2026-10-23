# kd-5: Conference Rooms - CancelJob cancels a running bulk job and audits nothing

**Severity**: MEDIUM - an audit gap on a mutating operator action. `Components/Pages/ConferenceRooms.razor` `CancelJob` (about 837) cancels a running bulk job with no audit row, while `RemoveJob` audits. The Constitution requires audit on every mutating action.
**Status**: Merged to master `07f2d371`; codex defect review cr-batch-8 in flight
**Branch**: `fix/psl10-a2y` (worktree `D:\source\ExchangeAdminWeb-wt\psl10-a2y`, forked from master at `3e43b35b`)
**Commit**: the commit that adds this line (SHA in the implementer's hand-back)
**Reviewer**: implementer (recorded in `.agents/state.md` "Known defects", re-verified 2026-10-23 as of `d881ea58`)

## Evidence

As above. Trigger: an operator cancels a bulk job. Predicted failure: no audit row; a stakeholder cannot see who stopped the job.

## Approach (to be written by the implementing agent before code)

- `CancelJob` writes the audit row the `RemoveJob` shape writes (operator, job id, ticket if the page carries one, result), including the refused and failed outcomes; page is line-keyed.
- Mutation: the audit call removed.

### As written before the code (branch `fix/psl10-a2y`, forked from master `3e43b35b`; lines are the base's)

**Verified.** `Components/Pages/ConferenceRooms.razor` 837: `private void CancelJob(string jobId) => BulkJobs.CancelJob(jobId);`,
reached from the active-jobs row's Cancel/Remove button at 511 (`@onclick="() => CancelJob(job.Id)"`). No audit row, while
`RemoveJob` (923-945) audits `ConferenceRooms_RemoveBulkJob`: operator, IP, target the job id, success the delete's result,
the job's ticket, and old values (job type, status, submitter, submitted-at, total rows), in a catch-and-log try. And the page
could not audit the result: `Services/Jobs/BulkJobService.cs` 144 `public void CancelJob(string id) => TryCancelJob(id);`
drops the bool `TryCancelJob` computes - true when a queued job is finished as Cancelled or a running job's cancellation
is requested, false when it was neither (already finished, or gone).

**The change.**
1. `BulkJobService.CancelJob` returns that bool (`public bool CancelJob(string id) => TryCancelJob(id);`). No statement
   changes; its callers (this page, `AdminBulkJobs`, the tests) call it as a statement and compile unchanged. A shared file:
   the base version bump this needs is outside this brief and is named in the hand-back.
2. The page's button passes the RENDERED job, `CancelJob(job)` (511, in place) - the shape `RemoveJob` takes and the one
   `ClickGateRegistry`'s exemption for 511 prescribes ("If it ever needs the row, it must take the rendered BulkJob the way
   RemoveJob now does"): the ticket and old values come from what the operator saw, never from a lookup into a collection
   the job callback replaces. The registry entry's snippet, reason and condition say so.
3. `CancelJob(BulkJob job)` (837, in place, one line - the page's line-keyed convention, as `AuditFinderAction`'s body):
   cancels by the row's id; audits `ConferenceRooms_CancelBulkJob` in `RemoveJob`'s shape - operator, IP, target the job id,
   the job's ticket, the same old values - with success the cancel's result. Refused (false) is audited as failed with "The
   job was not cancelled: it had already finished, or it no longer exists."; a throw from the store is audited as failed with
   its message and then rethrown, so the page behaves as before. The audit runs in a `finally` and in its own catch-and-log,
   so a failed audit cannot change the outcome (Constitution, Auditing). The blank line above (836) becomes its one comment
   line, so the page keeps 1758 lines.
4. Tests, the new `ExchangeAdminWeb.Tests/ConferenceRoomPageTests.cs`: the real page's `CancelJob`, over a real
   `BulkJobService` on a temporary jobs database and the real audit read back from its file: a queued job (cancelled), a
   running job (cancellation requested), a finished job (refused), a job no longer there (refused), a store that fails
   (failed, the exception still thrown) - each one audit row with the operator, the job, its ticket and the result.
   Mutations: the audit call removed; the result taken as success whatever the cancel returned; the audit skipped on a throw.

## Files changed

Snapshot before repair: `3e43b35b` (the branch's base; the aud-a2y commits before this one do not touch these lines).

- `Services/Jobs/BulkJobService.cs` - `CancelJob` returns `TryCancelJob`'s bool (shared code; no statement changes).
- `Components/Pages/ConferenceRooms.razor` - 511 (`CancelJob(job)`), 836 (the comment line, was blank), 837 (`CancelJob(BulkJob)`,
  audited); 1758 lines before and after.
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` - the exemption at 511: its snippet, reason and condition.
- `ExchangeAdminWeb.Tests/ConferenceRoomPageTests.cs` - the kd-5 section; `ExchangeAdminWeb.Tests/BulkJobServiceTests.cs` -
  `CancelJob_SaysWhetherItTookEffect`.
- `Modules/ModuleCatalog.cs` - the Conference Rooms 2.8.0 note (the version moved with aud-a2y on this branch).
- This record.

**Operator-visible change.** Cancelling or dequeuing a Conference Rooms bulk job writes an audit row,
`ConferenceRooms_CancelBulkJob`: the operator and IP, the job's id as target, its ticket, its type, status as shown,
submitter, submission time and row count, and Success when the cancel took effect; Failed with "The job was not cancelled:
it had already finished, or it no longer exists." when it took none, or with the store's error when it failed (the page
then fails as it did before). Nothing else changes: the button, its label, the cancel itself.

## Guard proof

`ConferenceRoomPageTests` (the real page's `CancelJob`, over a real `BulkJobService` on a temporary jobs database, the audit
read back from its file):
- `CancellingAnActiveJob_IsAudited_AsDone` (queued, running): one `ConferenceRooms_CancelBulkJob` row - operator, IP, the job,
  its ticket, the rendered job's type, status, submitter, time and rows - Success, no error; the job Cancelled, or its
  cancellation requested.
- `ACancelThatTakesNoEffect_IsAudited_AsRefused` (finished by the click, gone): one row, Failed, "The job was not cancelled:
  ...", the rendered job's values; the store unchanged.
- `ACancelTheStoreFails_IsAudited_AsFailed_AndStillThrows` (a jobs database with no tables): the store's exception still
  leaves the handler; one row, Failed, its message as the error.
- `BulkJobServiceTests.CancelJob_SaysWhetherItTookEffect` (Queued, Running true; Completed, Cancelled, Interrupted, none
  false, the store unchanged): the shared result the audit rests on.

Each row: md5 fixed, one exact replacement at the call site, rebuilt, the filter run, the failures read; the fixed bytes back
with a fresh mtime, md5 equal, rebuilt, run again. `ConferenceRooms.razor` fixed = restored `3d22787bd4401a1057144b7d3169d92e`,
`BulkJobService.cs` `a564d3de351386fd93a6064949549747`. Filter `ConferenceRoomPageTests` (6), and for M5b
`BulkJobServiceTests|ConferenceRoomPageTests` (32). No whole-file revert row: the tests hand `CancelJob` the rendered row,
which the page as at `d70497d9` does not take, so a revert fails on the call's signature, not on the audit; M1 is the
revert's substance, at its call site.

| # | Mutation (call site) | md5 mutated | Failing | Restored |
| --- | --- | --- | --- | --- |
| M1 | the audit call removed (the brief's mutation) | `3a789c670d49de866d8e5d3455fe87b4` | 5 of 6: every kd-5 case - no row (the aud-a2y case passes) | PASS 6 |
| M2 | the audit's success the constant true, whatever the cancel returned | `d7a2f4743d6ab7aac6454acdfca1c3b2` | 3 of 6: both refused cases and the store failure, each recorded Success | PASS 6 |
| M3 | the audit after the try instead of in its finally - skipped on a throw | `faf4fa4d2512a6288181607c62fb70fe` | 1 of 6: the store failure, no row | PASS 6 |
| M4 | a refused cancel audited with no reason | `252ff9a95697dc7567e17e72b5d3a6b9` | 2 of 6: both refused cases | PASS 6 |
| M5 | `BulkJobService.CancelJob` answering true whatever `TryCancelJob` did | `55323514f5dc88bcfd5a58a7080835e3` | 2 of 6: both refused cases, audited Success | PASS 6 |
| M5b | the same, with the service's own pin | `55323514f5dc88bcfd5a58a7080835e3` | 6 of 32: `CancelJob_SaysWhetherItTookEffect`'s four false cases, the page's two refused cases | PASS 32 |

On the fix, before the round: `ConferenceRoomPageTests`, `BulkJob*`, `ClickGate*`, `ProgressRegistry*`, `AdminBulkJobs*`,
`Comms10kOwnedJob*` 442 of 442 (before the direct pin was added).

## Coder dispute

None. One note on the Evidence: the page could not have audited the result without the shared change - `CancelJob` returned
void - so "including refused and failed outcomes" needed `BulkJobService` to surface what it already computed.

## Known gaps

- Shared code: `BulkJobService.CancelJob`'s return type. Its other caller, `AdminBulkJobs.CancelJob`, still audits Success
  after any cancel, the result unread - the same defect there, outside this record (a candidate in the hand-back).
- The page's Cancel and Remove re-check nothing of the operator's authorization at the click (no `ReauthorizeAsync`, unlike
  the page's writes and `AdminBulkJobs`' `StillAuthorized`); outside this record (a candidate in the hand-back).
- No notification is sent for the cancel itself; the job's terminal state notifies the administrators as before (a
  queued cancel from `BulkJobService`, a running one from the runner's tail).
- A refused cancel shows nothing new on the page (as before); the audit is where it is recorded.

## Closeout

Fixed on branch `fix/psl10-a2y`, not merged, not pushed: the commit that adds this section (its SHA in the implementer's
hand-back). Conference Rooms' version moved on this branch with aud-a2y (2.7.0 -> 2.8.0; its note names kd-5). The
`BulkJobService` change is shared code: a base app version bump is due, outside this brief. Pending: the merge and codex
review.