# aud-a47: Two app instances writing one JSONL audit file can overwrite or truncate each other's rows; the writer is not single-writer by construction

**Severity**: HIGH - loss of mandatory audit evidence for successful admin actions. Dev and prod use the
same `Audit:LogRoot` default/sample root (`deploy.ps1`, `tools/deploy-pipeline.ps1`), so both instances
write `<root>\ExchangeAdminWeb\exchangeadmin_YYYYMMDD.jsonl`. The aud-a28z writer opens with
`FileShare.ReadWrite | FileShare.Delete`, seeks to EOF, writes on a read/write handle and on failure
truncates back to the pre-write length. With two writers: A records its start offset, B appends and returns
`Written`, then A writes at the stale offset over B's row, or A fails and `SetLength(start)` truncates B's
completed row; B's trace still says AuditWritten Success and the Admin Event Log later misses or drops the
row. The `db` command-line mode exits before logging, so the second writer is another web instance. The
aud-a28z record states "one writer per JSONL file" as an unprobed assumption; nothing enforces it.
**Status**: Fixed on branch, closed on guard proof; not merged (plan approved under the owner's 2026-10-21 "EVERYTHING" go; folded into the aud-a45/a46 agent's scope)
**Branch**: `worktree-agent-a42d5cbfa41a7a483` (from `2df48c36`)
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (attr-batch-13, 2026-10-22)

## Evidence

At `2df48c36`: `Services/JsonlLogService.cs` around line 136 (open mode, the EOF seek, the truncate-back,
the tail repair); `deploy.ps1` line 14 and `tools/deploy-pipeline.ps1` line 52 (the shared log root);
`Program.cs` (`db` mode exits before logging); `.agents/review/findings/aud-a28z.md` Known gaps ("one
writer per JSONL file").

## Approach (to be written by the implementing agent before code)

Amended 2026-10-22 by the implementing agent, with the coordinator's go: **an exclusive lock file, not a named
mutex**. The first version of this section asked for a `Global\` named mutex derived from the file's full path. It
was rejected for three reasons:
1. A mutex created with the default DACL denies a second instance running as another account. `deploy.ps1` takes
   `-ServiceAccount` per pool and the installer keeps each pool's own identity, so the two can differ. Failing closed
   would then drop every row that instance writes, and the only fix is a DACL that grants some broad group.
2. With such a DACL, any local user can open and hold the predictable `Global\` name, or create it first. Every
   audit write on the host then times out. That is an audit-suppression vector open to an unprivileged account.
3. It is safe only if every writer runs on one host and names the root by the same path. That is an
   environment-shape argument (Invariant 7).

- Single-writer by construction. Every writer of a JSONL file takes the file's lock before it looks at the file.
  The lock is `<active file>.lock` beside it, opened with `FileShare.None` and `DeleteOnClose` and retried within a
  bounded wait. The writer holds it through the size check and rotation, the offset read, the tail repair, the write
  and any truncate-back.
- A wait that runs out is a dropped row, reported through the aud-a28z recovery path. A row is never written at an
  offset read without the lock.
- Why the lock holds:
  - Access to it is the log folder's own ACL. Only a principal that can write the log can contend for it, and such a
    principal could destroy the log already.
  - The file system enforces it across processes, accounts, hosts and path spellings.
  - The OS releases it when a dead process's handles close.
- The extended log's consumer (aud-a46) uses the same helper.
- Readers never take the lock. The Admin Event Log and the extended log's reader open files by name with full
  sharing and repair nothing. The lock file is not a log, and no enumeration of the log folder picks it up.
- Document in the operator-facing setup (README deployment section and the deploy sample) that dev and prod may
  share a log root only because the writer serialises across processes. If the owner prefers distinct roots, that
  is a deploy-config change, not code.
- Tests: two writers in one process on one file through the helper. The lock is the file system's, so one process
  exercises it: a second handle is refused whichever process holds the first.
  - Interleaved failures: the file parses to exactly the rows reported Written.
  - A wait that times out is a dropped row, and its report names the lock wait as the cause.
  - Mutations at the call site: the lock removed; the offset read before the lock again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"). Admitted from codex attr-batch-13 (`edbaa138`) and folded into
this agent's scope by the coordinator. The coordinator chose the lock file over the mutex for the reasons above.

Commits on branch `worktree-agent-a42d5cbfa41a7a483` (from `2df48c36`): one `fix(audit-log): ... (aud-a47)`, first of
three, before aud-a45 and aud-a46. Shared infrastructure - the writer every audit and trace call ends in - so a base
app version change; the supervisor owns the bump.

Evidence, re-read at `2df48c36`:
1. `Services/JsonlLogService.cs` `WriteToFile` (82-119) holds `lock (_lock)` (92-96) for its path selection and
   append. `_lock` is one instance's monitor, and nothing serialises another instance.
   - `GetWritableLogPath` (202-210): `File.Exists`, `FileInfo.Length`, then `RotateLogFiles` (212-228, deletes and
     moves).
   - `AppendLine` (134-157): opens `FileMode.OpenOrCreate`, `FileShare.ReadWrite | FileShare.Delete` (136), takes
     `start` at the end (137), reads the last byte back for the repair (138, `EndsWithNewline` 160-164), writes once
     through `WriteLineBytes` (143), and on failure runs `TruncateTo(start)` (147, 167-179).
   - With two instances, A's `start` can be stale by the time it writes (B's row overwritten), or its truncate-back
     can cut B's completed row. Either way B reports `Written`.
2. The second writer is real:
   - `deploy.ps1` 14 and `tools/deploy-pipeline.ps1` 52 default both instances to `E:\WWWOutput`, and
     `appsettings.json.sample` 17 to the same.
   - `deploy.ps1` 397-400 grants each instance's service account Modify `(OI)(CI)` on `<LogRoot>\ExchangeAdminWeb`,
     and `tools/Install-ExchangeAdminWeb.ps1` 907 grants the pool's own identity the same. So every writer may create,
     write and delete files in the folder, and the identities may differ.
   - `Program.cs` 12-16: the `db` mode exits before any logger exists.
3. `Services/ExtendedLogService.cs` `ProcessQueueAsync` (253-274) writes `exchangeadmin_YYYYMMDD_extended.jsonl` under
   the same root, with a `StreamWriter` over `FileMode.Append`, which takes no lock. It moves onto the helper in
   aud-a46.
4. Readers:
   - `Components/Pages/AdminEventLog.razor` `ReadLinesShared` (1406-1412) and `ExtendedLogService.GetEntries`
     (182-220) open by name, read-only, `FileShare.ReadWrite | FileShare.Delete`, and repair nothing.
   - No production code enumerates the log folder. `MessageTraceExportStore` sweeps its own `MessageTraceExports`
     subfolder by a regex (174-176); the other enumerations are other folders.
   - The tests list `exchangeadmin_*.jsonl`, which a `.jsonl.lock` name does not match.

**Rule.** A JSONL file has one writer at a time, whatever process or account it runs in. A writer takes the file's lock
before it looks at the file and holds it until its record is written or taken back. A writer that cannot take the lock
within its bound drops the record through the aud-a28z recovery path, its report naming the lock wait. A row is never
written at an offset read without the lock. Readers never take the lock.

1. New `Services/JsonlFileAppender.cs`, `internal static class JsonlFileAppender`. It is the aud-a28z one-line append,
   moved out of the JSONL writer so the extended log can share it, with the lock added. Its parts:
   - `DefaultLockWait` (2 seconds).
   - `LockPathFor(path)` (`path + ".lock"`).
   - `Append(path, json, lockWait, rotateIfFull, writeLineBytes, repaired, truncationFailed)`, in this order:
     1. Take the lock.
     2. Run the caller's size check and rotation.
     3. Open the file as `AppendLine` did, take `start` at the end, and do the tail-repair check.
     4. Write the line once through the seam; on failure, truncate back to `start` (best effort, its failure handed to
        `truncationFailed`) and rethrow.
     5. After a repaired write, call `repaired`.
     6. Release the lock.
   - Steps 3 to 5 are `AppendLine`, `EndsWithNewline` and `TruncateTo` moved verbatim. The two reports stay with the
     writer, as callbacks, so their text, arguments and order are unchanged.
   - The lock is `File.OpenHandle(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
     FileOptions.DeleteOnClose)`.
   - Retried every 1 ms, doubling to 16 ms, until the wait runs out:
     - a sharing or lock violation (another holder);
     - `UnauthorizedAccessException`, which is what a contender meets while the holder's close is deleting the file.
   - Then it throws a new `JsonlLockTimeoutException : IOException` whose message names the lock file and the wait,
     with the last refusal as its inner exception.
   - Anything else (a missing folder, a bad path) propagates at once.
   - The record's name for the file is a pure function of the date and the stream. Everything that reads or changes
     the file system - the existence and size check, rotation, the open, the offset, the repair, the write, the
     truncation - runs under the lock.
2. `Services/JsonlLogService.cs`:
   - `GetWritableLogPath` splits into `ActivePath(stream)` (the name, as before) and `RotateIfFull(activePath)` (its
     existence and size check and `RotateLogFiles`, unchanged).
   - `WriteToFile`, inside its `lock (_lock)`, sets `logPath = ActivePath(stream)` and calls `JsonlFileAppender.Append`.
     `RotateIfFull` and the seam are passed in, and the repair and truncation warnings go as callbacks through
     `ReportSafely`, word for word.
   - New `internal TimeSpan LockWait` (default `JsonlFileAppender.DefaultLockWait`), a test seam.
   - The catch is unchanged. A lock that is not taken is `FileWriteFailed`, its report the aud-a28z one with the
     timeout exception attached, and its path now the active file rather than "(unresolved)".
3. Docs:
   - README, "4. Deploy to IIS", a new "#### Shared log root (dev and prod on one server)". Two instances may share one
     `Audit:LogRoot` only because each writer takes a lock file beside the log file before touching it. A write that
     cannot take it within 2 seconds is dropped and logged as a failed audit write, never written over the other
     instance's row. The lock is held across accounts, hosts and path spellings. An instance on an older build takes
     no lock, so both instances must run this build. Distinct roots are a deploy-config choice.
   - "Audit Logging" points to that subsection.
   - `appsettings.json.sample` gets an `_AuditNote` (the sample's note convention) saying the same in a sentence.
4. Comments after the mutation round, checked code-identical.

Tests - new `ExchangeAdminWeb.Tests/JsonlWriterLockTests.cs`. Two real `JsonlLogService` instances over one temp root
stand in for the two web instances: each has its own in-process lock, so only the file lock serialises them. There is
a capturing logger that records each entry's exception, no network, and no AD.
- `TwoWriters_OneFailingHalfWrittenWhileTheOtherWaits_LeaveExactlyTheRowsReportedWritten` (audit, trace).
  - Writer A's seam writes half its line, starts writer B's write on another thread, gives it 500 ms, then throws.
  - B waits for the lock and writes after A's take-back.
  - Result: the file parses to exactly the rows reported Written, and B's row is one of them.
- `ARowThatLandsWhileAWriterWaitsForTheLock_IsNeitherOverwrittenNorTakenBack` (a writer that then writes; a writer that
  then fails half-written).
  - The test holds the lock as the other instance would, and starts a writer.
  - While the writer waits, the test appends a row of its own and lets go.
  - Result: the other instance's row survives, whole and parseable, and the waiting writer's row is written after it
    or taken back cleanly.
- `AWriterThatCannotTakeTheLockInItsBound_DropsTheRow_SaysTheLockWasTheCause_AndTheNextRowIsWritten`.
  - The lock is held by a raw handle, standing in for a live, hung writer in another process, and the writer's bound
    is 300 ms.
  - Result: the write returns `FileWriteFailed` after waiting at least its bound and well within 10 s, not forever,
    and no line is written.
  - One error is logged: the aud-a28z report, with a `JsonlLockTimeoutException` naming the lock file.
  - Once the handle is released, the next row is written. Coordinator caution (1).
- `TheLockIsNeitherNeededToReadNorReadAsALog`.
  - While the lock is held, the Admin Event Log's own `ReadLinesShared` (by reflection) reads the file.
  - The folder's `exchangeadmin_*.jsonl` listing and `GetAuditLogPaths`/`GetTraceLogPaths` do not include the lock
    file.
  - After a write the lock file is gone. Coordinator caution (2).
- `TwoWritersOnSeveralThreads_WithInterleavedFailures_LeaveExactlyTheRowsReportedWritten`.
  - Two writers, three threads each, twenty rows per thread.
  - Every fifth write by writer A fails half-written through its seam.
  - Result: the file parses to exactly the rows reported Written.

Mutations at the call site (md5 before and after, the named failing test, restored, md5 matched, touched, rebuilt,
green):
- M1 the lock removed (the lock's `using` taken out of `Append`).
- M2 the offset read before the lock (the open and the end seek moved above the lock).
- Extra E1: a wait that runs out writes anyway (the timeout returns no lock instead of throwing).

The aud-a28z rows' tests (`ALineThatFailsHalfWritten_...`, `ATailADeadProcessLeft_...`,
`ADroppedRowsReport_NamesWhoDidWhat_...`) are in the round's filter and must stay green unchanged.

Files:
- `Services/JsonlFileAppender.cs` (new)
- `Services/JsonlLogService.cs`
- `ExchangeAdminWeb.Tests/JsonlWriterLockTests.cs` (new)
- `README.md`
- `appsettings.json.sample`
- this record

## Fix

- New `Services/JsonlFileAppender.cs`:
  - `internal static class JsonlFileAppender` with `DefaultLockWait` (2 s), `LockPathFor(path)` (`path + ".lock"`) and
    `Append(path, json, lockWait, rotateIfFull, writeLineBytes, repaired, truncationFailed)`.
  - `Append` takes `Lock(path, lockWait)` first, then runs `rotateIfFull()`. Then comes aud-a28z's append, moved
    verbatim from `JsonlLogService.AppendLine`: the open, `start` at the end, the tail-repair check, one write through
    the seam, the take-back on failure (`TruncateTo`, its failure handed to `truncationFailed`) and rethrow, and
    `repaired()` after a repaired write.
  - `Lock` is `File.OpenHandle(lockPath, OpenOrCreate, ReadWrite, FileShare.None, FileOptions.DeleteOnClose)`.
    - A sharing or lock violation (HRESULT low word 32 or 33) and `UnauthorizedAccessException` are retried with a pause
      of 1 ms doubling to 16 ms until the wait has passed. Anything else propagates.
    - It then throws new `internal sealed class JsonlLockTimeoutException : IOException`. That exception carries
      `LockPath`, with the last refusal as its inner exception and a message naming the lock file and the wait.
- `Services/JsonlLogService.cs`:
  - `GetWritableLogPath` is split into `ActivePath(stream)` and `RotateIfFull(activePath)`, each unchanged in logic.
  - `WriteToFile` sets `logPath = ActivePath(stream)` inside its lock.
  - `AppendLine` is now one call to `JsonlFileAppender.Append`. `RotateIfFull`, the `WriteLineBytes` seam and the two
    warnings - their text and arguments unchanged, through `ReportSafely` - are handed in.
  - `EndsWithNewline` and `TruncateTo` moved to the appender, and `using System.Text` went with them.
  - New `internal TimeSpan LockWait`.
- `README.md`:
  - New "#### Shared log root (dev and prod on one server)" under "4. Deploy to IIS": the lock, the 2-second bound and
    what a timed-out write becomes, that readers never take it, that both instances must run this build, and distinct
    roots as the deploy-config alternative.
  - The Audit Logging section points to it.
- `appsettings.json.sample`: `_AuditNote` beside `Audit`, saying the same in a sentence.

Tests: new `ExchangeAdminWeb.Tests/JsonlWriterLockTests.cs`, 7 cases:
- `TwoWriters_OneFailingHalfWrittenWhileTheOtherWaits_LeaveExactlyTheRowsReportedWritten` (audit, trace)
- `ARowThatLandsWhileAWriterWaitsForTheLock_IsNeitherOverwrittenNorTakenBack` (a waiting writer that then writes; one
  that then fails half-written)
- `AWriterThatCannotTakeTheLockInItsBound_DropsTheRow_SaysTheLockWasTheCause_AndTheNextRowIsWritten`
- `TheLockIsNeitherNeededToReadNorReadAsALog`
- `TwoWritersOnSeveralThreads_WithInterleavedFailures_LeaveExactlyTheRowsReportedWritten`

Caller-visible changes, by design:
- Every JSONL audit and trace write takes the file's lock first, so a second instance writing the same file waits its
  turn.
- A write that cannot take the lock within 2 seconds returns `FileWriteFailed`. Its "AuditWritten" step is Failed, as
  for any dropped row, and the aud-a28z report carries the `JsonlLockTimeoutException`.
- A failed rotation is reported under the active file's path instead of "(unresolved)".
- While a write holds its lock, `<file>.lock` exists beside the file, and it is deleted when the write releases it.
- A record that writes produces the same bytes as before.

## Guard proof

Reproduction: each failure below is the defect as the record states it.
- M1 is the writer with no cross-process lock, which is what `2df48c36` had - its monitor serialised one instance only.
  A second writer then reported `Written` and lost its row to the first writer's take-back (`["Before"]` where
  `["Before", "Other"]` was reported).
- M2 is a writer whose offset was read before it held the file. It wrote over the other instance's row, leaving a
  fragment the event log cannot parse, or took that row back with its own half line (`[]` where `["OtherInstance"]`
  was written).

Session-local driver (session scratchpad `mutate/mutate.psm1` and `mutate/round-a47.ps1`, log `mutate/a47-mutations.log`):
- For each mutant: the file's exact bytes saved, one literal replacement that must match exactly once, a Release build
  of the solution with 0 errors required, then the filter run with a TRX logger. The exact bytes are restored in a
  `finally`, the MD5 re-checked and the mtime advanced so MSBuild rebuilds.
- The filter (180 tests) is aud-a28z's plus this record's:
  `FullyQualifiedName~JsonlWriterLockTests|FullyQualifiedName~AuditWriterThrowFreeTests|FullyQualifiedName~AuditServiceTests|FullyQualifiedName~UsageTelemetryServiceTests|FullyQualifiedName~ExtendedLogServiceTests|FullyQualifiedName~AuditExtraChannelTests|FullyQualifiedName~AuditCategoryFilingTests|FullyQualifiedName~EventLog`
- Every mutant built with 0 errors, and each restore matched its original MD5. After the round, a rebuild on the
  restored bytes and the filter: 180/180 green. The aud-a28z rows' tests passed under every mutant.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 the lock removed (`using var held = Lock(path, lockWait);` taken out of `Append`) | `JsonlFileAppender.cs`, `A9CCC3F56780EDA3AE8571D1FF11B28E` | `40B699E93E52CF0CD6328F692AB8E91B` | 6 of 180: `TwoWriters_OneFailingHalfWrittenWhileTheOtherWaits_...` both streams - `["Before"]`, the other writer's Written row cut by the take-back; `ARowThatLandsWhileAWriterWaitsForTheLock_...` both shapes - "the writer did not wait for the file's lock"; `TwoWritersOnSeveralThreads_...` - rows reported Written missing from the file; `AWriterThatCannotTakeTheLockInItsBound_...` - `Written` while another held the lock |
| M2 the offset read before the lock (the open and the end seek moved above the lock) | `JsonlFileAppender.cs`, `A9CCC3F56780EDA3AE8571D1FF11B28E` | `2855DD792BDD091A74E94CFFCB456074` | 3 of 180: `ARowThatLandsWhileAWriterWaitsForTheLock_...` a waiting writer that then writes - one unparseable line, its row written over the other instance's; and one that then fails half-written - `[]`, the other instance's row taken back with the half line; `TwoWritersOnSeveralThreads_...` - rows reported Written missing |
| extra E1 a wait that runs out writes anyway (the timeout returns a no-op handle instead of throwing) | `JsonlFileAppender.cs`, `A9CCC3F56780EDA3AE8571D1FF11B28E` | `6770E8BF5851A486DFCCD3FEE38C3C82` | 1 of 180: `AWriterThatCannotTakeTheLockInItsBound_...` - `Written` without the lock |

Unchanged through the round: `JsonlLogService.cs` `A278D0DCDBFED6040E535B01DBBEA42B` and `JsonlWriterLockTests.cs`
`5D6C1C58E5D8AF5842621A7725025464`. The code comments are written after the round, in their own commit, so the MD5s
above are this commit's bytes.

Not run, by design: two app pools on a real IIS host, and a share reached over SMB. The test's two writers in one
process meet the same file-system refusal two processes do: a handle opened `FileShare.None` refuses the next one,
whichever process asks.

## Known gaps

- *Assumption, not probed*: a log root on an SMB share enforces the exclusive open across hosts as a local volume does.
  SMB share modes are enforced by the server, but no remote share was used here, and the README claims no more than
  accounts on one server.
- An instance on a build without the lock still writes unserialised. Between a dev deploy of this build and its
  promotion, prod's writes can still collide with dev's. Both instances must run this build, and the README says so.
- A principal that may create but not delete files in the log folder cannot open the lock (`DeleteOnClose` needs
  delete). Each of its writes would then wait out its bound and be dropped, reported. Every deploy path grants the
  pool Modify (`deploy.ps1` 400, the installer 907), which rotation already needs.
- `UnauthorizedAccessException` is retried because a contender meets it while the holder's close deletes the file. A
  folder that refuses the lock for good therefore costs each write its full 2-second bound before the drop, not an
  immediate one.
- The bound is a fixed 2 seconds, and the writer runs on the operator's thread inside its in-process lock. A
  contending instance costs at most that per write, and a hung one costs exactly that per write until it lets go.
