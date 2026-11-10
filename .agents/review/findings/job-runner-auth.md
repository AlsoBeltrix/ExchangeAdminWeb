# job-runner-auth: Message Trace detail and Migration report export jobs run with no enablement or authorisation check

**Severity**: MEDIUM - a queued job of either kind kept running, and Message Trace mailed its result, after the module
was disabled or the operator's access was removed. Both jobs only read (delivery detail; migration reports), nothing is
written to Exchange or the directory, and the operator was authorised when the job was queued; that is why it is not
HIGH.
**Status**: Merged to master at 5896bae9 (2026-11-10) on guard proof (MEDIUM, no review round); master gates green (8997 xUnit, 19 warnings, format/diff-check/ASCII clean)
**Branch**: `fix/p0-3-job-runner-authorization` (worktree `D:\source\ExchangeAdminWeb-wt\p0-3`, from master 2250b66e)
**Commit**: 0805ef11 (runner check, the four processors' refusal, tests), 5005cbf9 (Message Trace enqueuer), b275a4fd
(Migration enqueuer), 3a01e139 (Comms-10k private copy removed), 7047690e (Conference Rooms private copy removed),
29f923f3 (README), 7859d929 (versions) | **Implementer**: Claude (supervised agent)
**Reviewer**: Fable 5.2 audit 2026-11-10, confirmed by Gemini 4 Argon (High) through agy; orchestrator triage
**Plan**: `docs/SharedPiecesAndSlop-Plan.md` slice 0.3 (defect #1, pattern B4)

## Evidence (master 2250b66e)

- `Services/Jobs/MessageTraceDetailJobProcessor.cs` and `MigrationReportExportProcessor.cs` read neither the
  enablement store nor section access, and their enqueuers (`MessageTrace.razor` `EmailSelectedDetails`,
  `Migration.razor` `StartReportExport`) set no `AuthSnapshotJson`.
- `Comms10kReplaceProcessor.AuthorizationDenial` and the first block of `ConferenceRoomBulkProcessor.ProcessRowAsync`
  each did the check for themselves, differently (table below).
- The contract: `docs/BulkJobRunner-Plan.md` (option (a)): a job carries a snapshot taken at submit and is re-checked
  per row; a disabled module fails closed.

## Fix (as landed)

- **The check lives once**: `Services/Jobs/JobAuthorization.cs` (`IJobAuthorization.Check(job)`), called by the runner
  (`BulkJobService.ExecuteRowsAsync`) before every row of every job. It refuses when: the job's module is not in the
  catalog; the snapshot is missing or unreadable; the snapshot is for a section other than the module's main permission;
  the module (or its parent) is disabled or enablement cannot be read; no group captured in the snapshot is still an
  allowed SID for the section, or section access cannot be read; a store throws.
- A refused row goes to the processor's new `RefuseRow(job, rowIndex, refusal)` and never to `ProcessRowAsync`. The
  member has no default, so a processor cannot be written without saying what its refusal looks like. A check or a
  `RefuseRow` that throws is a Failed row (the runner's existing per-row catch); the row's work does not run.
- The interface exists for one test double: the runner's own mechanics tests run processors that belong to no catalog
  module (`ModuleCatalog` is sealed and fixed).
- `MessageTrace.razor` and `Migration.razor` keep the principal the page was authorised for and save
  `JobAuthorizationSnapshot.Capture(principal, section, current groups)` on the job when they queue it. Sections:
  `MessageTrace`, and `MigrationCheck` (the Migration module's main permission).
- Both private copies are deleted. Comms-10k's one re-check inside the replace stage, immediately before the clear,
  now asks the runner's check through the same `IJobAuthorization`.
- App code: +300 / -133 lines (`git diff --numstat 2250b66e 7859d929`, tests, README and version lines excluded).

## The two former private copies, and how each module's behaviour was kept

| | Comms-10k (before) | Conference Rooms (before) | Now |
|---|---|---|---|
| Order | snapshot, section, enablement, access | enablement, ticket, snapshot + access | runner: snapshot, section, enablement, access. Conference Rooms' `RefuseRow` still says "disabled" first, then the missing ticket, then the access |
| Snapshot section must match | yes, exact case | not checked | checked for every module |
| Current groups filtered to usable SIDs | yes | no | filtered for every module |
| A store that throws | audited refusal "could not be verified" | escaped: unaudited Failed row "row N" with the exception text | audited refusal in both (Conference Rooms: "Authorization denied.") |
| Row message | five distinct messages | "Conference Rooms is currently disabled." / "Authorization denied." | unchanged, each module's own |
| Audit | one `Comms10k_Replace` row per job (first refusal), outcome RefusedBeforeAnyChange | one `ConferenceRooms_Set*_Bulk` row per refused row; access detail "Authorization denied (captured snapshot lacks access)." | unchanged |
| Row target | source file name | the row's room address | unchanged |
| Mail | completion notification, outcome RefusedBeforeAnyChange | completion notification, not-success with the counts | unchanged |
| Job end state | Completed, refused rows Failed, the runner goes on to the next row | same | unchanged |
| Re-check inside a row | once, before the clear | none | unchanged |

Differences that could not be kept exactly with one check, all for a job row the application cannot write (both
enqueuers always save a snapshot for their own section holding only SIDs, and no queued job survives a restart):

1. Conference Rooms, snapshot missing AND module disabled: was "disabled", is now "Authorization denied." (the
   runner reads the snapshot first, as Comms-10k did).
2. Conference Rooms, snapshot captured for another section: the row ran if a group matched; it is now refused.
3. Conference Rooms, a snapshot or a section holding a group name rather than a SID: matched by name; now refused.
4. Conference Rooms, a store that throws: now an audited refusal instead of an unaudited failed row. The stores
   catch their own read failures (unreadable enablement reads as disabled, unreadable section access as no groups),
   so this is reached only by a fault outside them.

Each moves toward refusing. They are listed for the owner because rule 1 of the plan says an agent does not pick.

## What the two newly covered jobs do when refused (new behaviour, the defect's fix)

- **Migration report export**: the row is Failed with "Exchange Migration is currently disabled. The report was not
  fetched." or "Authorization denied. The report was not fetched.", and one `ExportUserReport` audit row is filed
  Failed for the mailbox. Nothing is fetched or stored for it. Reports stored by earlier, allowed rows stay.
- **Message Trace detail export**: the row is Failed with "Message Analysis is currently disabled." or
  "Authorization denied.". At completion a job with any refused row writes no export file and sends no mail - not
  the "ready" mail, and not the "could not be stored" notice, whose text would be untrue. The job record gets
  "export refused: <reason>", the `MessageTrace_DetailExport` audit row is filed Failed with that text, and
  Downloadable Reports lists the job as "Not produced" instead of "Expired". Rows fetched before the refusal are
  not delivered.

## Jobs queued before this change

None reaches the check. Start-up marks every Queued or Running job Interrupted (`BulkJobService.InitializeAsync`,
unchanged), and the deploy restarts the app, so a job queued by the old build with no snapshot is Interrupted with
"Interrupted by application restart." and its module's completion hook, exactly as before this change. If such a
row did reach the runner it would be refused on every row with the messages above, visible on the job record.

## Tests

- Runner mechanics (`BulkJobServiceTests`, stub check): a refused job runs no row and counts every row Failed; a
  job refused after its first row runs none after it; a check or a refusal that throws is a Failed row.
- Through the real runner over the real check and real stores, per processor: disabled mid-run, access removed
  mid-run, enablement store unreadable, section store unreadable, snapshot missing / unreadable / for another
  section, and an allowed job running every row. `MessageTraceDetailJobProcessorTests` (8),
  `MigrationReportExportProcessorTests` (8, the processor's first behaviour tests), `ConferenceRoomBulkProcessorTests`
  (8), `Comms10kRefusalTests.TheRunnerStopsAJobThatMayNoLongerRunBeforeItsReplaceStage` (5) with the existing
  `C2TheRunnerStillAdvancesAfterDeniedResolveWithOneSpecificAuditAndNotification` (no snapshot).
- Enqueuers (`JobSnapshotAtQueueTests`, 6): each page's handler is run and the queued job read back - the snapshot
  names the section and the operator's group and the real check accepts it; an operator no allowed group
  authorises gets a snapshot the check refuses; a page holding no principal queues nothing.
- `MessageTraceExportListingTests.ARefusedJob_IsNotProduced_NotExpired`.

Replaced one for one (same file, the processor no longer refuses by itself, so the row is run as the runner runs
it: the real check, then `RefuseRow` or `ProcessRowAsync`):

| Deleted | Replacement |
|---|---|
| `Comms10kRefusalTests.C2EveryStageRequiresTheOriginalExactSectionGrantAndEnabledModule` (17 cases) | `TheRunnersCheckRefusesEveryStageWithoutTheOriginalExactSectionGrantAndEnabledModule` (17 cases) |
| `Comms10kRefusalTests.C2RestoredConfigurationCannotOverwriteTheFirstAuthorizationRefusal` (2) | `RestoredConfigurationCannotOverwriteTheRunnersFirstRefusal` (2) |
| `ConferenceRoomBulkProcessorTests.AuthSnapshotLacksAccess_FailsClosed_NotApplied` | same name, through the check, exact message and audit row |
| `ConferenceRoomBulkProcessorTests.ADisabledModule_StopsTheJob_AndSaysSo` | same name, through the check, exact message and audit row |
| `ConferenceRoomBulkProcessorTests.EitherTheChildOrItsParentBeingOff_StopsTheJob` (2) | same name, through the check |
| `ConferenceRoomBulkProcessorTests.WithBothOn_TheRowStillReachesTheDirectory` | same name, through the check |

Added beside them: `ARefusedRowOfAJobWithNoTicket_SaysTheModuleIsOff_OrElseTheTicket` (2), which pins Conference
Rooms' order. Fixture groups in `ConferenceRoomBulkProcessorTests` are SIDs now ("AllUsers" before).

## Guard proof (one part reverted, targeted run of 800 tests, restored)

| Reverted | Failed | Tests that failed |
|---|---|---|
| The runner's call to the check (`ProcessRowAsync` called directly) | 30 | `BulkJobServiceTests` 4; `Comms10kRefusalTests` 5; `ConferenceRoomBulkProcessorTests.Runner_*` 7; `MessageTraceDetailJobProcessorTests.Runner_*` 7; `MigrationReportExportProcessorTests.Runner_*` 7 |
| The check's enablement test | 20 | Comms-10k 10 (incl. `C2TheFinalClearGateFollowsAllPreparation` 3, `C2RevocationDuringAwaitedCredentials...` 1); Conference Rooms 6; Message Trace 2; Migration 2 |
| The check's access test | 24 | Comms-10k 15; Conference Rooms 3; `JobSnapshotAtQueueTests` 2; Message Trace 2; Migration 2 |
| The check's section match | 6 | Comms-10k 3; one `Runner_AJobWithNoUsableSnapshot_IsRefusedOnEveryRow` case in each of Conference Rooms, Message Trace, Migration |
| The check's missing-snapshot refusal | 10 | Comms-10k 4; `Runner_AJobWithNoUsableSnapshot_IsRefusedOnEveryRow` 2 each in Conference Rooms, Message Trace, Migration |
| Message Trace enqueuer's snapshot | 2 | `JobSnapshotAtQueueTests` (accepts; refuses) MessageTrace rows |
| Migration enqueuer's snapshot | 2 | `JobSnapshotAtQueueTests` (accepts; refuses) Migration rows |
| Message Trace: a refused job delivers nothing | 7 | `MessageTraceDetailJobProcessorTests.Runner_AJobThatMayNoLongerRun...` 4, `Runner_AJobWithNoUsableSnapshot...` 3 |
| Reports listing: the refused marker | 1 | `MessageTraceExportListingTests.ARefusedJob_IsNotProduced_NotExpired` |
| Comms-10k: the re-check before the clear | 8 | `C2RevocationDuringAwaitedCredentialsRefusesBeforeActualDispatch` 2, `C2TheFinalClearGateFollowsAllPreparation` 6 |
| Conference Rooms: ticket before access in `RefuseRow` | 1 | `ARefusedRowOfAJobWithNoTicket_SaysTheModuleIsOff_OrElseTheTicket(disabled: False)` |
| Migration: the refused row's audit | 7 | `MigrationReportExportProcessorTests.Runner_*` 7 |

Removing each private copy with nothing in its place (before the tests were moved) failed the 19 Comms-10k cases
listed as deleted above. Restored: all pass (full gates in the hand-back).

## Left alone, and why

- **The completion hook of a job that never ran a row** - Interrupted at start-up, or cancelled while queued - is
  not behind the check: it is not a row. For Message Trace that hook still writes an export of the selected
  messages' summary lines marked "Not processed" and mails the link, as it did before. Not part of this slice's
  stated fix; named here for the owner.
- No refusal at the page for an operator whose fresh snapshot holds no group: the job is queued and the runner
  refuses it. The plan asks for a snapshot at queue, not a new page refusal.
- The snapshot is taken from the principal the page was authorised for at load (as Conference Rooms takes it),
  against the section's groups at queue time. Both handlers stay synchronous, so no click window opens.
- The three scoped protected-principal exemptions, every other module, and what each processor does for an allowed
  row: untouched. The ticket check stays in Conference Rooms' row work.
- Finding IDs were stripped from the comments of the methods edited (`ConferenceRoomBulkProcessor.ProcessRowAsync`
  and its class summary; the Comms-10k check) and nowhere else. `ModuleCatalog` versions were bumped with no
  history comment added.
- Downloadable Reports' text for "Not produced" ("the job did not finish, so no export was written") is reused
  for a refused job; the reason is on the job record and in the audit row, not in that hover text.
