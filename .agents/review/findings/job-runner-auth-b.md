# job-runner-auth-b: a job that never ran a row still delivered at completion, with no enablement or authorisation check

**Severity**: MEDIUM - a Message Trace detail-export job that was cancelled while queued, or found unfinished at
start-up, wrote an export of the selected messages' summary lines (sender, recipient, subject, status) and mailed the
link after the module was disabled or the operator's access was removed. Read-only data the operator was authorised
for when the job was queued, nothing written to Exchange or the directory; that is why it is not HIGH.
**Status**: Merged to master at 4d2bf4cf (2026-11-10) on guard proof (MEDIUM, no review round); master gates green (9045 xUnit, 19 warnings, format/diff-check/ASCII clean)
**Branch**: `fix/p0-b-phase0-mediums` (worktree `D:\source\ExchangeAdminWeb-wt\p0-b`, from master 4cb7942b)
**Commit**: db21fd3e (runner check at completion, Message Trace refusal, tests, README), 32a091a7 (versions) |
**Implementer**: Claude (supervised agent)
**Reviewer**: Fable 5.2 audit 2026-11-10, confirmed by Gemini 4 Argon (High) through agy; orchestrator triage
**Plan**: `docs/SharedPiecesAndSlop-Plan.md` slice 0.3b (the residual `job-runner-auth.md` named under "Left alone")

## Evidence (master 4cb7942b)

- `BulkJobService.InitializeAsync` marks every Queued or Running job Interrupted and calls each one's completion
  hook; `TryCancelJob` finishes a Queued job as Cancelled and calls its hook. Neither path runs a row, and the check
  (`IJobAuthorization.Check`) was called only before a row (`ExecuteRowsAsync`).
- `MessageTraceDetailJobProcessor.OnJobCompletedAsync` refused only when a row had been refused (`_refusal`). With
  no row run it built the CSV from the payload's summaries, each marked "Not processed (job did not reach this
  message).", saved it, and mailed the link.
- The same hole, smaller: a job whose every row was allowed and whose module was disabled between the last row and
  the completion step still delivered.

## Fix

- **The runner owns the call, so the check is there**: `BulkJobService.NotifyCompletedInScope` - the one place every
  completion hook is called from (the runner's terminal tail, the start-up sweep, the queued cancel) - asks
  `IJobAuthorization.Check(job)` and hands the answer to the hook:
  `OnJobCompletedAsync(BulkJob job, JobRefusal? refusal)`. The parameter has no default value, so a processor's hook
  cannot be written without receiving it. A check that throws is handed on as `JobRefusal.Unverifiable` (logged);
  the hook still runs.
- `MessageTraceDetailJobProcessor`: a job refused at a row or as it ends writes no file and sends no mail (neither
  the "ready" mail nor the "could not be stored" notice), appends "export refused: <reason>" to the job record,
  files the `MessageTrace_DetailExport` audit row Failed with "<n> message(s); export refused: <reason>", and is
  listed "Not produced" on Downloadable Reports. Reason text as in slice 0.3: "Message Analysis is currently
  disabled." for a disabled module, "Authorization denied." for everything else. A row's refusal, when there was
  one, is the reason given.
- An allowed job is unchanged: cancelled while queued or interrupted at start-up, it still writes the export with
  every message "Not processed" and mails the link.
- App code: +42 / -14 lines (`git diff --numstat 4cb7942b db21fd3e`, tests and README excluded).

## Every processor's completion hook

| Processor | What its completion does | Covered |
|---|---|---|
| Message Trace detail export | Writes the export file under the audit log root, mails the operator's recipients a link, files one audit row | Yes: refused means no file, no mail, the note, a Failed audit row |
| Migration report export | Nothing - it has no hook (the interface's no-op). Reports are stored by rows, which the runner already checks | Nothing to cover |
| Comms-10k replace | One admin notification to the configured administrators' address: the outcome of the replace (including "interrupted, the group may be empty or partial"). No file, no data to the operator | Receives the refusal and does not act on it: the mail is the administrators' record of what happened to the group, and a refused job's mail is today's behaviour (slice 0.3 table, "Mail: unchanged") |
| Conference Rooms bulk | One admin notification to the same address: counts and status. No file, no data to the operator | As Comms-10k |

Suppressing the two admin notifications for a refused job would change who gets mail for a reason this finding does
not state (plan rule 1), and for Comms-10k would hide an interrupted replace. Named for the owner.

## Behaviour that moves, all toward refusing

1. Message Trace job cancelled while queued or interrupted at start-up, module disabled or access removed or the
   job carrying no usable snapshot: delivered, now refused. This is the finding.
2. Message Trace job whose rows all ran allowed, refused as it ends (module disabled or access removed between the
   last row and completion): delivered, now refused. Same check, same place; pinned by a test.
3. A job queued by a build older than slice 0.3 (no snapshot) and interrupted by the deploy's restart: slice 0.3's
   record said it completes "exactly as before". It is now refused at completion: no export, "Interrupted by
   application restart. export refused: Authorization denied." on the job record. No such job exists once 0.3 has
   been deployed and restarted once.

## Tests

- Runner mechanics (`BulkJobServiceTests.TheCompletionHook_IsToldWhatTheCheckSaysOfTheJobAsItEnds`, 9 cases, stub
  check): for a job that ran, one cancelled while queued and one interrupted at start-up, the hook is given null
  when allowed, the refusal when refused, and Unverifiable when the check throws; the last two run no row.
- Through the real runner, the real check and real stores (`MessageTraceDetailJobProcessorTests`):
  `Runner_AJobThatNeverRanARow_AndMayStillRun_DeliversItsNotProcessedExport` (2: `CancelJob`; `InitializeAsync`) -
  one file of three "Not processed" lines, one "ready" mail, a Success audit row, the job message untouched;
  `Runner_AJobThatNeverRanARow_AndMayNoLongerRun_DeliversNothing` (6: each path x module disabled, access removed,
  no snapshot) - no file, neither mail, the note after the runner's own message, the Failed audit row;
  `Runner_AJobAllowedAtEveryRow_AndRefusedAsItEnds_DeliversNothing` (1).
- No test deleted. The existing direct calls of the hook in tests take the new argument (`null`), and two test
  processors the new parameter.

## Guard proof (one part reverted, targeted run of 979 tests, restored)

| Reverted | Failed | Tests that failed |
|---|---|---|
| The runner hands the hook no check result (`null`) | 13 | `TheCompletionHook_IsToldWhatTheCheckSaysOfTheJobAsItEnds` 6 (refused and throws, each of the three endings); `Runner_AJobThatNeverRanARow_AndMayNoLongerRun_DeliversNothing` 6; `Runner_AJobAllowedAtEveryRow_AndRefusedAsItEnds_DeliversNothing` 1 |
| Message Trace's hook ignores the check result | 7 | `Runner_AJobThatNeverRanARow_AndMayNoLongerRun_DeliversNothing` 6; `Runner_AJobAllowedAtEveryRow_AndRefusedAsItEnds_DeliversNothing` 1 |
| A check that throws at completion is rethrown instead of handed on as a refusal | 3 | `TheCompletionHook_IsToldWhatTheCheckSaysOfTheJobAsItEnds` 3 (throws, each ending) |

Restored: 979 of 979 pass (full gates in the hand-back).

## Left alone, and why

- The Comms-10k and Conference Rooms admin notifications (table above).
- A job whose processor cannot be built, or that no processor is registered for, has no hook to run; unchanged.
- Downloadable Reports' text for "Not produced" is reused, as in slice 0.3; the listing was not changed and has no
  new test (its marker test, `ARefusedJob_IsNotProduced_NotExpired`, matches the marker anywhere in the message).
- Comms-10k and Conference Rooms module versions: their hooks' signature changed, their behaviour did not.
- Finding IDs were stripped from the comments of the two methods edited (`NotifyCompletedInScope`,
  `MessageTraceDetailJobProcessor.OnJobCompletedAsync`) and nowhere else.
