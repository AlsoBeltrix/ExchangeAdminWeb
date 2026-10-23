# aud-a23n: Migration Approve - the read-back can report failure after a successful completion, and the pre-read ignores the batch's state before the partial writes

**Severity**: HIGH (part 1), MEDIUM (part 2). (1) `ApproveMigrationAsync` confirms completion by reading
the batch back and requiring `CompleteAfter` / `CompleteAfterUTC` to be set; if Exchange clears or stops
returning that scheduling field once `Complete-MigrationBatch` has been accepted, the approval is reported
as failed ("Completion ... was not confirmed") after the irreversible write succeeded, inviting a retry
(Known Failure Class 1: a post-side-effect false failure). The aud-a23i stub keeps the field set, so the
tests cannot see it; the record labelled this an unverified assumption. (2) The aud-a23i pre-read checks
only that a batch named after the mailbox exists, not its state: a batch already completed, failed or
syncing passes the guard, and the approval then fails at a later step after `Set-MigrationUser` /
`Set-MigrationBatch -CompleteAfter` already wrote.
**Status**: Complete; merged `09d3bacb`; codex mig-batch-7 raised two candidates, admitted as aud-a23r
**Branch**: `worktree-agent-a03d6bd111b571e5a` (forked at `b16898dc`)
**Commit**: `2249d828` (fix, tests, record); plan `f62462b9`; comments `97ca9448` (shared with aud-a23j, aud-a23m, aud-a23k, aud-a23l)
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (mig-batch-6, 2026-10-22, candidates 1 and 2)

## Evidence

At `b16898dc`: `Services/MigrationService.cs` around line 1138 (the read-back and its "not confirmed"
outcome) and around line 1013 (the pre-read checking `ownBatch.Count` only); the aud-a23i record's
labelled assumption about `CompleteAfter`.

## Approach (to be written by the implementing agent before code)

- Confirmation: a successful `Complete-MigrationBatch` is the completion; the read-back confirms against
  durable state (the batch `Status`, e.g. Completing/Completed/Synced-with-completion-requested, as the
  cmdlet reports it) and treats a missing scheduling field after an accepted command as "accepted; read-back
  did not confirm the schedule" detail on a SUCCESSFUL outcome, never a failure. Nothing in the code may
  assert what the live cmdlet returns: the detail text says what was and was not confirmed.
- Pre-read: inspect the batch state before any write; refuse before writing (nothing written) when the
  state is non-actionable (Completed, Failed, Removing, Stopped as the user chose), name the state; treat
  already-completed as its own outcome.
- Tests through `MigrationStubExchange` with a stub whose read-back omits `CompleteAfter`, and with batches
  in each non-actionable state; mutations at the call site: the schedule-field confirmation again as the
  success criterion; the `Count`-only pre-read again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `22088cf2` into the a23j-m agent's scope, first
in its order (HIGH). One commit `fix(migration): ... (aud-a23n)` on branch `worktree-agent-a03d6bd111b571e5a` (forked
at `b16898dc`; this record taken from `22088cf2`), before aud-a23j. Module-scoped: Migration (the bump is the
supervisor's; `Modules/ModuleCatalog.cs`, `ExchangeAdminWeb.csproj` not edited). No page change. Line numbers at
`b16898dc`, `Services/MigrationService.cs`.

1. **Part 2, the pre-read reads the batch's state** (`ApproveMigrationUserAsync`, 1013-1036). After the existence read
   and its count check, a batch of its own has its `Status` read before any write:
   - no status (blank or absent): refused - "<address> was not approved: its migration batch <name> reports no
     status, so whether it can be completed is not known. Nothing was changed.";
   - `Completed`: refused in its own words - "... is already Completed, so its migration has finished and there is
     nothing left to approve. Nothing was changed." - so it does not read as a fault;
   - `CompletedWithErrors`, `Completing`, `Failed`, `Removing`, `Stopped`, `Stopping`: refused naming the state -
     "... is <Status>, a state an approval does not complete a batch from. Nothing was changed.";
   - any other status: the writes run as today. Matching ignores case and surrounding space.

   Each refusal throws before the first `Set-MigrationUser`, so `RunAsync` returns it as the row's failure and both
   callers' existing audits record it Failed with that text. Why these states: the record's four (Completed, Failed,
   Removing, Stopped); their in-flight forms (Completing, Stopping - the batch planner's own "finishing now / being
   torn down" grouping, `NonSchedulableStatuses`); and CompletedWithErrors, which the owner ruled on 2026-09-25 has
   nothing to finalise until remediated (`MigrationBatchActionPlanner`). **Assumption, labelled:** these names are this
   app's reading of `Get-MigrationBatch`'s Status, with no live call to check them. An unlisted status proceeds, and
   Exchange's own refusal of a later step is still reported naming the step and what was applied (aud-a23h).
2. **Part 1, a successful Complete-MigrationBatch is the completion** (`ConfirmBatchSetToComplete`, 1116-1146). The
   read-back stays (the aud-a2l/a2p shape) but no longer decides the outcome. It becomes `DescribeCompletionReadBack`,
   which never throws and returns the Detail of a SUCCESSFUL result, saying what was and was not confirmed:
   - read, with a completion time (`CompleteAfter` or `CompleteAfterUTC`): "Exchange accepted Complete-MigrationBatch
     for its migration batch <name>; read back, the batch reports status <Status> with a completion time set.";
   - read, with none: "...; read back, the batch reports status <Status> and no completion time, so the read-back
     did not confirm the schedule.";
   - the read fails, or returns other than one batch: "...; reading the batch back failed (<reason>) / returned N
     batches, so its state since was not confirmed."

   No status is interpreted as "complete": the Detail reports what the cmdlet returned. The message is unchanged
   ("Approved skipped items and initiated completion for <address>."). A batch step that fails before it is still
   the row's failure, naming the step (aud-a23h, unchanged).
3. Not changed: the mailbox-level steps; a mailbox not in a batch of its own (no batch steps, as aud-a23h left it);
   `CompleteMigrationBatchAsync` and the batch toolbar.

**Tests.** `ExchangeAdminWeb.Tests/MigrationStubExchange.cs`: `Batch(...)` takes a status; two switches,
`CompleteClearsSchedule` (Complete-MigrationBatch removes the batch's `CompleteAfter`, so the read-back omits it) and
`CompletionStatus` (it sets the batch's Status). New `ExchangeAdminWeb.Tests/MigrationApproveConfirmationTests.cs`,
fakes only:
- **part 1**: a read-back that omits the schedule - success, the message, the Detail "...no completion time, so the
  read-back did not confirm the schedule.", all nine calls; a read-back that fails - success, "reading the batch back
  failed (...)"; a read-back with a changed status - the Detail names it.
- **part 2**: a batch in each refused state (a theory, one row lower-case) - failed, the state named, "Nothing was
  changed.", the existence read the only call; Completed - its own words, only the read; no status - refused, only
  the read; through the page's `ExecuteUserAction` - one audit row, Failed, the refusal as its error.
- The aud-a23h tests that pinned the old read-back (`ACleanApproval...`'s Detail, `AReadBackThatFails...`,
  `AWriteThatChangesNothing...`) take the new outcome: success, with the Detail.

**Mutations at the call site** (md5 before/mutated/after, failing tests named, restored, touched, rebuilt, green):
N1 (the record's (1)) the schedule field as the success criterion again - a read-back with no completion time throws
"was not confirmed"; N2 (the record's (2)) the Count-only pre-read again - the status check skipped. Extras: N1b a
failed read-back a failure again; N2b Completed refused in the generic words; N2c a batch with no status allowed.

**Gates** as the brief; comments after the mutation round.

## Guard proof

Built as the Plan. Line numbers are at the fix commit's bytes, which carry no new code comments (they come in their own
commit after the last finding's round). No page change.

- `Services/MigrationService.cs`:
  - `ApproveMigrationUserAsync` (999-): after the existence read and its count check, a batch of its own goes through
    `RefuseUnlessApprovable` (1038) before the first write; the Detail of a batch of its own is
    `DescribeCompletionReadBack` (1091-1092), the not-in-its-own-batch Detail unchanged.
  - `ApprovalRefusedBatchStatuses` (1117): CompletedWithErrors, Completing, Failed, Removing, Stopped, Stopping.
  - `RefuseUnlessApprovable` (1120): no status - "reports no status, so whether it can be completed is not known";
    Completed - "is already Completed, so its migration has finished and there is nothing left to approve"; a refused
    state (1135) - "is <Status>, a state an approval does not complete a batch from"; each "Nothing was changed.";
    ignoring case and surrounding space.
  - `DescribeCompletionReadBack` (1142), replacing `ConfirmBatchSetToComplete`: never throws; "Exchange accepted
    Complete-MigrationBatch for its migration batch <name>;" then what the read-back showed - "read back, the batch
    reports status <S> with a completion time set." / "... and no completion time, so the read-back did not confirm the
    schedule." / "reading the batch back failed (<reason>) / returned N batches, so its state since was not confirmed."
- `ExchangeAdminWeb.Tests/MigrationStubExchange.cs`: `Batch(name, completeAfter, status)` (status null leaves the
  property out); `Complete-MigrationBatch` honours `CompleteClearsSchedule` (removes `CompleteAfter`) and
  `CompletionStatus` (sets Status).

New `ExchangeAdminWeb.Tests/MigrationApproveConfirmationTests.cs`, 7 tests (17 cases); 3 aud-a23h tests changed:

| Test | Pins |
| --- | --- |
| `ACompletionWhoseReadBackOmitsTheSchedule_IsASuccess_SayingTheScheduleWasNotConfirmed` | **part 1, the finding**: Complete-MigrationBatch accepted, the read-back has no `CompleteAfter` - success, the message unchanged, the Detail "...status Synced and no completion time, so the read-back did not confirm the schedule.", all nine calls |
| `AReadBackThatShowsANewStatus_NamesItInTheDetail` | the status the read-back reports is named as read ("Completing"), with the completion time |
| `AReadBackWithNoStatusAndNoSchedule_IsStillASuccess_SayingSo` | "reports no status and no completion time" - still a success |
| `ABatchInAStateAnApprovalCannotCompleteFrom_IsRefusedBeforeAnyWrite` (Failed, Removing, Stopped, Stopping, Completing, CompletedWithErrors, " stopped ") | **part 2, the finding**: failed, the state named, "Nothing was changed.", the existence read the only call |
| `AnAlreadyCompletedBatch_IsItsOwnOutcome_AndNothingIsWritten` | Completed in its own words; only the read |
| `ABatchThatReportsNoStatus_IsRefused_AndNothingIsWritten` (absent, empty, blank) | refused, only the read |
| `ABatchInAnyOtherState_RunsEveryStep` (Synced, Syncing, SyncedWithErrors) | the control: every step, success |
| `ARefusalThroughThePage_IsOneFailedAuditRow_NamingTheState` | through `ExecuteUserAction`: failed, one `ApproveMigrationUser` audit row, Failed, the refusal as its error; only the read |
| `MigrationEligibilityAndApproveTests.ACleanApproval_...` (changed) | the Detail: "...reports status Synced with a completion time set." |
| `...AReadBackThatFails_IsStillASuccess_SayingItsStateWasNotConfirmed` (was `..._CouldNotBeConfirmed_...`) | a read-back that fails after an accepted completion: success, "reading the batch back failed (the batch is being updated), so its state since was not confirmed." |
| `...AWriteThatChangesNothing_IsStillASuccess_SayingTheScheduleWasNotConfirmed` (was `..._IsNotConfirmed`) | every write reports success, no completion time read back: success, the Detail says the schedule was not confirmed |

**Against the unfixed code: not runnable as a whole** - the stub's `Batch` and the tests' expectations changed with the
fix, so each behaviour was put back at its own call site instead, and each revert fails the tests that pin it.

Mutations at the call site, each by exact-text replacement asserted to match once (session scratchpad `mutate.ps1`,
`round-a23n.ps1`): md5 recorded, `dotnet build ExchangeAdminWeb.slnx -c Release`, the 1074 tests of
`Migration*|ClickGate*|ProgressRegistry*|ExchangeServiceBase*|AuditService*|GlobalProgressWiring*|BlockedSender*` run
with a TRX logger (failures read from it), the file's bytes restored in a `finally` and its mtime advanced so MSBuild
rebuilds, md5 re-checked equal, rebuilt, and the 1074 re-run green - after every mutation. Service bytes (LF) before
every mutation and after every restore: `8d3b984f4a57c7d6ed20ab104327fa97`.

| Mutation | md5 mutated | Failed (of 1074) | Restored, rerun |
| --- | --- | --- | --- |
| N1 (the record's (1)): a read-back with no completion time throws "was not confirmed" again | `4004f7d82889612cdc9448a55eaaa595` | 3: `ACompletionWhoseReadBackOmitsTheSchedule...`, `AReadBackWithNoStatusAndNoSchedule...`, `AWriteThatChangesNothing_IsStillASuccess...` - a false failure after the accepted write | equal; 1074/0 |
| N2 (the record's (2)): the Count-only pre-read again (the status check never runs) | `59531dfd70f6718196091e1768be8898` | 12: every `ABatchInAStateAnApprovalCannotCompleteFrom...` case (7), `AnAlreadyCompletedBatch...`, `ABatchThatReportsNoStatus...` (3), `ARefusalThroughThePage...` - the writes ran | equal; 1074/0 |
| N1b: a failed read-back a failure again | `0bd17122b43be88c8395ae4e3afb63d3` | 1: `AReadBackThatFails_IsStillASuccess...` | equal; 1074/0 |
| N2b: Completed refused in the generic words | `c93c8b2c611bda0f7defe0eb039173b1` | 1: `AnAlreadyCompletedBatch_IsItsOwnOutcome...` | equal; 1074/0 |
| N2c: a batch with no status allowed | `e1e3b5c8359c456059a299f5dfe3ac15` | 3: `ABatchThatReportsNoStatus...` (absent, empty, blank) | equal; 1074/0 |

md5s (LF) of the other files at the fix commit: `MigrationApproveConfirmationTests.cs`
`3edf139fbf5cd0586a6c0489160353ef`, `MigrationEligibilityAndApproveTests.cs` `539d499de1c9e308b9acbcd0311b525c`,
`MigrationStubExchange.cs` `8529829b2a84d3a48d3be367c0651cf6`.

On the fix commit's tree: `dotnet build ExchangeAdminWeb.slnx -c Release` 0 errors, 61 warnings (the baseline count);
the 1074 focused tests 1074/0; `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0;
`git diff --check HEAD` exit 0; `tools/Test-AsciiOnly.ps1` after `git add -A` exit 0. The full suite ran at the
branch tip (below).

## Known gaps

- **Assumption, labelled (no live call):** the refused-state names are this app's reading of `Get-MigrationBatch`'s
  Status. A state Exchange reports under another name proceeds, and a step it then refuses is reported naming the step
  and what was applied (aud-a23h) - the partial-write shape this finding narrows, not closes, for an unanticipated
  state.
- The read-back no longer gates anything: an approval whose read-back shows a batch neither completing nor scheduled is
  a success whose Detail says so. That is the point (Known Failure Class 1), and it means the operator reads the Detail.
- Unchanged from aud-a23h: a mailbox in a batch of many is approved without any batch step; a single-user batch given
  another name is treated the same way.

## Comments and branch-tip gates (shared by aud-a23n, aud-a23j, aud-a23m, aud-a23k, aud-a23l)

The canonical copy for all five records; the other four point here.

**Comments (`97ca9448`).** Written after every finding's mutation round, at the call sites the rounds proved
load-bearing; comments only. Checked by `check-comment-only.ps1` (session scratchpad `a23jklmn-work`): HEAD and the
tree are each reduced to their code - comment-only lines dropped, the trailing comments this commit adds (`// aud-a23...`
/ `@* aud-a23... *@`) cut off, blank lines dropped - and the reductions are identical for all four files. In the three
`.cs` files every changed line is a comment or blank (additions only: service +69, models +15, processor +2); on the
page each of the 31 changed lines is the line it was plus a trailing comment, except the eight-line progress-outcome
block (1555-1562), rewritten in its eight lines because it said the writes still complete true. The page stays 4377
lines and every registered line key holds. Code md5s (comment-stripped, unchanged by the commit): service
`5b51fca1a952bf9cca26f3953a31a49f`, models `939792f0bdd20efea523b26208f123e1`, processor
`55920a2736fa8ccbc0e403c89f5752a2`, page `3437265e43cdac1f615e7e17305c9fce`. Final file md5s (LF) at the tip:
`Services/MigrationService.cs` `f8446943a48cdd0cc689e4cc7d847b04`, `Models/MigrationModels.cs`
`370f6142b86f4607d8c533df6e182864`, `Services/Jobs/MigrationReportExportProcessor.cs`
`33519719d96bcbf064cbf8efdf6d357f`, `Components/Pages/Migration.razor` `2e1597a23246daf9ddd770055450d9c8`; the tests are
as their fix commits left them (`MigrationStubExchange.cs` `0d4ae546a9f59e95f2c2421090d0a25e`, last changed by
aud-a23k).

**Branch-tip gates (`97ca9448`, all five fixes and the comments; the records-only commit after it changes no code):**

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 59 warnings - two fewer than the 61 at
  `b16898dc`: the typed report outcome removed the two CS8604s for a nullable report text handed to the store's `Write`
  (the page's old 4051, the processor's old 86). The one warning in a touched file is the pre-existing CS8604 at
  `Services/Jobs/MigrationReportExportProcessor.cs` 104 (`ticketNumber: job.Ticket`), a line this branch did not change
  (it was 92).
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): Passed 5872,
  Skipped 3 (the known `ADDirectorySearchServiceTests ...WhenAdUnavailable_ReturnsEmpty` cases), Failed 0, Total 5875;
  5 m 35 s; "All tests finished running, Sequence file will not be generated." - no hang. This branch adds 83 cases (five
  new test classes) and renames four tests; the suite at `b16898dc` was not run, so 5792 there is derived, not measured.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0. `Components/Pages/Migration.razor`: 4377 lines.

No live Exchange, Active Directory, Graph, DHCP or IIS call was made: every Exchange cmdlet was a stub in an in-process
runspace, every AD cmdlet a stub in the directory stand-in, the protection check a stand-in, the mail a recording stand-in.
No PowerShell source changed, so PSScriptAnalyzer and Pester were not run. No coder self-review verdict is claimed.
Version bumps are the supervisor's: Migration, for all five (no base-app change - `ExchangeServiceBase.cs` was read, not
changed).

## Closeout

Fixed on branch `worktree-agent-a03d6bd111b571e5a`; not merged, not pushed.
