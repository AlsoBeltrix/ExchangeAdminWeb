# aud-a51: The JSONL lock file needs delete rights and reports a permanent access failure as lock contention

**Severity**: HIGH - audit suppression under an ACL drift. `JsonlFileAppender` opens `<active>.lock` with
`FileOptions.DeleteOnClose`, so every audit, trace and extended-log write needs delete rights on the log
folder. Where the app pool identity may create, write and append but not delete, or any persistent
`UnauthorizedAccessException` comes back from the lock open, every write waits the full 2 s bound and is
dropped, and the logged exception is `JsonlLockTimeoutException` "Another writer held the lock..." - no
writer is contending and the real cause is permissions. `AuditService.WriteAuditEvent` calls the writer
synchronously on the circuit path, so each affected row also stalls the operator for 2 s before it is lost.
The test lock holders do not use `DeleteOnClose`, so the edge is uncovered.
**Status**: Merged `eb141b30`; closed on guard proof; codex review pending
**Branch**: `worktree-agent-ac1038e1d7ece4ae5` (from `132d6e97`)
**Commit**: `3277a185` (fix, tests, README, record); comments `c2d6215d`; plan `5ed8a5a8`
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (attr-batch-14, 2026-10-22)

## Evidence

At `7aeb19e0`: `Services/JsonlFileAppender.cs` around line 101 (the lock open with `DeleteOnClose`; the
retry treating every failure alike; the timeout exception's text); `Services/AuditService.cs`
`WriteAuditEvent` (`_log.Write(evt)` synchronous); `ExchangeAdminWeb.Tests/JsonlWriterLockTests.cs` (the raw
lock holders).

## Approach (to be written by the implementing agent before code)

- The lock does not require deletion: open a stable `<active>.lock` exclusively with `FileShare.None` and
  no `DeleteOnClose`; the held handle, not the file's existence, is the lock, so a stale lock file is
  harmless. Delete best-effort after release (ignore failure).
- The retry distinguishes contention (sharing violation) from a permanent access failure: an
  `UnauthorizedAccessException` (and any non-sharing IO error) is reported at once as "the log folder
  refused the lock file: <reason>" through the aud-a28z recovery path, with no 2 s wait; the timeout
  exception keeps its contention text for a real sharing violation.
- The operator-facing text (README "Shared log root") names the rights the folder needs (create, write,
  append; delete optional).
- Tests: a lock open that throws `UnauthorizedAccessException` is dropped at once with the permissions text
  (no bound wait); a sharing violation still waits and times out with the contention text; the stand-in lock
  holder matches the appender's open mode. Mutations at the call site: `DeleteOnClose` again; the
  access-failure branch folded back into the retry.

*Amended 2026-10-22 by the implementing agent, from measurements taken before any code (Plan, evidence 2 and 6):*
- "Reported at once" means after a 100 ms grace, never after the 2 s contention bound. An instance still on the aud-a47
  build deletes the lock file on close, and a writer that opens it in that moment is refused access for a few
  milliseconds. Treating that refusal as permanent would drop a row under contention and blame the folder.
- "Delete best-effort after release" is a move to a unique name and a delete of that, not a plain delete. A plain delete
  leaves the lock's name delete-pending for a moment, and a writer that opens it then is refused access - the very
  refusal this finding stops treating as contention. A move is refused with a sharing violation while another writer
  holds the file, so it never removes a held lock.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"). Admitted from codex attr-batch-14 (`132d6e97`) and folded into this
agent's scope by the coordinator. One commit `fix(audit-log): ... (aud-a51)`, first of three on branch
`worktree-agent-ac1038e1d7ece4ae5` (from `132d6e97`), before aud-a48 and aud-a49. It changes the appender that every audit,
trace and extended-log write ends in, so the base app version is due; the supervisor owns the bump.

Evidence, re-read at `132d6e97`:
1. `Services/JsonlFileAppender.cs`:
   - `Lock` (91-122) opens `File.OpenHandle(lockPath, OpenOrCreate, ReadWrite, FileShare.None, FileOptions.DeleteOnClose)`
     (101). `DeleteOnClose` asks for delete access to the lock file.
   - Its retry (103-112) treats a sharing or lock violation and an `UnauthorizedAccessException` alike. Both are retried
     until the wait (2 s) has passed. Then comes `JsonlLockTimeoutException` (116-117), whose text is "Another writer held
     the lock ..." (154).
   - `Append` (47-84) takes the lock first. Both writers append only through it: `JsonlLogService.AppendLine` (133-145)
     and `ExtendedLogService.AppendLine` (332-344).
2. Measured on this host (session scratchpad `lockprobe/AclProbe2.ps1`), with a deny-Delete ACE for the probe's own
   account on a folder and on what is created in it:
   - A new lock file opened with `DeleteOnClose` is created, and the close deletes it: the creator is granted the access
     it asks for.
   - A lock file that already exists and is not held refuses that open with `UnauthorizedAccessException`, every time. A
     plain `FileShare.None` open without `DeleteOnClose` opens it.
   - A held lock file refuses a `DeleteOnClose` contender with `UnauthorizedAccessException` too, not with a sharing
     violation: the access check comes before the share check. A plain contender gets the sharing violation.
   - So in such a folder an existing lock file stalls every audit, trace and extended write for the 2 s bound and drops
     it, reported as another writer's lock. The file can be left by a copy or restore of the folder, by a tool, or by a
     writer that may not delete it. The retry cannot tell, because in such a folder contention is refused the same way.
3. `Services/AuditService.cs` `WriteAuditEvent` (466-520) calls `_log.Write(evt)` (491) on the caller's thread, so the 2 s
   is the operator's.
4. The test holders, `JsonlWriterLockTests.HoldTheLock` (245-254) and `ExtendedLogServiceTests.HoldTodaysLock` (538-548),
   open a `FileStream` with `FileShare.None` and no `DeleteOnClose`. No test met the appender's own open against an
   existing file.
5. `deploy.ps1` 401 and the installer 907 grant each instance Modify `(OI)(CI)`, which allows delete. The edge is an ACL
   that drifts from that, or a folder set up by hand.
6. Measured for the release (session scratchpad `lockprobe/LockProbe2.ps1`): six threads contended for one lock file for
   3 s, each counting what its opens met.
   - `DeleteOnClose`, as merged: 143 access refusals, the longest unbroken run 5.1 ms.
   - Close, then `File.Delete`: 576 refusals, the longest run 40.7 ms. A delete leaves the name delete-pending for a
     moment, and an open in that moment is refused access.
   - Close, then a move of the file to a unique name and a delete of that: 0 refusals. A move of a held file is refused
     with a sharing violation, and a move never leaves the lock's name pending.
   - Close and no delete: 0 refusals.

**Rule.** The lock is the open handle, not the file. Taking it needs create, read and write in the log folder, never
delete. Contention - another writer's handle - is waited for up to the bound and reported as contention. A refusal - the
folder will not let this account create, read or write the lock file - is reported as the folder's refusal. It comes
after a 100 ms grace for a third party's delete in flight, never after the 2 s bound and never as contention.

1. `Services/JsonlFileAppender.cs`:
   - The lock open is `File.OpenHandle(lockPath, OpenOrCreate, ReadWrite, FileShare.None)`, with no `DeleteOnClose`.
   - The held lock is a private `HeldLock : IDisposable`. Its `Dispose` closes the handle, then removes the file best
     effort: a move to `<lock>.<32 hex>`, then a delete of that.
     - Any failure is ignored. Either another writer holds the file by then, or the folder does not allow deleting.
     - The file then stays, empty and harmless, and the next writer opens it.
   - The retry:
     - A sharing or lock violation is retried as now, until the bound, then `JsonlLockTimeoutException`, its text
       unchanged.
     - An `UnauthorizedAccessException` is retried only until `DefaultAccessRefusalGrace` (100 ms) has passed since the
       first refusal of an unbroken run; a sharing violation ends the run. Then comes new `JsonlLockRefusedException`.
     - Any other `IOException` (a missing folder, a device error) is a `JsonlLockRefusedException` at once.
     - Anything else propagates, as now.
   - New `internal sealed class JsonlLockRefusedException : IOException`. It carries `LockPath`, with the refusal as its
     inner exception. Its message: "The log folder refused the lock file <path>: <reason> The record was not written.
     The account this instance runs as must be able to create files in the log folder and to read and write the files
     there; it does not need to delete them."
   - `Append` takes the grace as a parameter after `lockWait`.
2. `Services/JsonlLogService.cs`: new test seam `internal TimeSpan AccessRefusalGrace` (default
   `JsonlFileAppender.DefaultAccessRefusalGrace`), passed to `Append`. Its catch, the aud-a28z recovery path, is unchanged:
   a refused lock is `FileWriteFailed`, reported with the exception attached.
3. `Services/ExtendedLogService.cs` `AppendLine` passes `JsonlFileAppender.DefaultAccessRefusalGrace`. Its consumer's catch
   is unchanged: dropped, counted, reported.
4. `README.md`, "Shared log root":
   - The lock is the open handle. The file is removed after a write where the folder allows it, and left in place where
     it does not.
   - A lock file the folder refuses is not a wait. The write is dropped at once, and its report says the folder refused
     the lock file.
   - The rights each instance's account needs on `<LogRoot>\ExchangeAdminWeb`: create files, and read and write the files
     there, the other instance's included. The lock does not need delete, but rotation does. The scripts' Modify covers
     all of it.
   - Both instances run this build.
5. Comments after the mutation round, checked code-identical.
6. No file another agent owns needs a change. `AuditService.WriteAuditEvent`'s synchronous write (491) now costs a refused
   lock file the 100 ms grace instead of the 2 s bound. A live contender still costs up to the bound, aud-a47's known gap,
   unchanged.

Tests:
- `ExchangeAdminWeb.Tests/JsonlWriterLockTests.cs`:
  - `AFolderThatLetsAWriterWriteButNotDelete_StillTakesTheLock_AndWritesEveryRow` (audit, trace).
    - The test denies its own account Delete on the log folder and on what is created in it, with an inheritable deny
      ACE removed at the end. It leaves an unheld lock file in place.
    - Three rows: all `Written`, no error, well within the bound, and the lock file still there.
  - `ALockFileTheFolderRefuses_DropsTheRowAtOnce_AndSaysTheFolderRefusedIt`.
    - A directory stands where the lock file belongs, so every open of it is refused access, as an ACL refuses one. The
      bound is 5 s.
    - The write returns `FileWriteFailed` in under 2 s, and no line is written.
    - One error: its exception a `JsonlLockRefusedException` naming the lock file, with an `UnauthorizedAccessException`
      inside, and not the contention text.
    - With the directory gone, the next row is written.
  - `AnAccessRefusalThatClearsWithinTheGrace_IsRetried_AndTheRowIsWritten`.
    - The grace is set to 30 s, and the directory is in place. The write is still waiting at 300 ms.
    - Once the directory is removed, the row is `Written`, with no error.
  - `HoldTheLock` takes the lock through `JsonlFileAppender.Lock`, so the stand-in for the other instance opens and
    releases it as the writers do.
  - Unchanged: `AWriterThatCannotTakeTheLockInItsBound_...` (a held lock still costs the bound and names the contention),
    and the rest.
- `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs`:
  - `TheConsumer_InAFolderThatLetsItWriteButNotDelete_WritesEveryEntry`, through the shared deny helper.
  - `HoldTodaysLock` through `JsonlFileAppender.Lock`.

Mutations at the call site (md5 before and after, the named failing test, restored, touched, rebuilt, md5 matched, green):
- M1 `DeleteOnClose` again in the lock open.
- M2 the access-failure branch folded back into the retry: an `UnauthorizedAccessException` waited for to the bound and
  reported as `JsonlLockTimeoutException`.
- Extra E1 no grace: an access refusal reported at the first.

Files:
- `Services/JsonlFileAppender.cs`
- `Services/JsonlLogService.cs`
- `Services/ExtendedLogService.cs`
- `ExchangeAdminWeb.Tests/JsonlWriterLockTests.cs`
- `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs`
- `README.md`
- this record

## Fix

- `Services/JsonlFileAppender.cs`:
  - `Lock(path, wait, accessRefusalGrace)` opens `File.OpenHandle(lockPath, OpenOrCreate, ReadWrite, FileShare.None)`, with
    no `DeleteOnClose`, and returns a private `HeldLock`.
  - `HeldLock.Dispose` closes the handle and calls `RemoveIfFree(lockPath)`. That moves the file to
    `<lock>.<Guid "N">` and deletes the moved file, each in its own try; a failed move ends it, and a failed delete is
    ignored.
  - The retry:
    - A sharing or lock violation clears the refusal run and, once the wait has passed, throws `JsonlLockTimeoutException`
      with the last violation inside, its text unchanged.
    - An `UnauthorizedAccessException` starts a refusal run, or continues it. Once the run is
      `accessRefusalGrace` old, it throws new `JsonlLockRefusedException`.
    - Any other `IOException` throws `JsonlLockRefusedException` at once.
    - Anything else propagates.
  - New `DefaultAccessRefusalGrace` (100 ms).
  - New `internal sealed class JsonlLockRefusedException : IOException`, carrying `LockPath`, with the refusal inside. Its
    message is "The log folder refused the lock file <path>: <the refusal's message> The record was not written. The
    account this instance runs as must be able to create files in the log folder and to read and write the files there".
    The plan's last clause ("it does not need to delete them") was dropped: rotation does need delete, and the sentence
    read as though nothing did.
  - `Append` takes `accessRefusalGrace` after `lockWait` and hands it to `Lock`.
- `Services/JsonlLogService.cs`: new `internal TimeSpan AccessRefusalGrace` (default the appender's), passed to `Append`.
- `Services/ExtendedLogService.cs` `AppendLine`: passes `JsonlFileAppender.DefaultAccessRefusalGrace`.
- `README.md`, "Shared log root":
  - The lock is the open handle, removed after the write where the folder allows it and left otherwise.
  - A contended write is still the 2-second drop, and a lock file the folder refuses is dropped at once, the error saying
    so.
  - The folder rights each instance's account needs, with delete needed for rotation and not for the lock; the scripts'
    Modify covers them.
  - Both instances on the same build, since the first build with the lock needs delete to open a lock file already
    there.

Tests:
- `ExchangeAdminWeb.Tests/JsonlWriterLockTests.cs` +4:
  - `AFolderThatLetsAWriterWriteButNotDelete_StillTakesTheLock_AndWritesEveryRow` (audit, trace)
  - `ALockFileTheFolderRefuses_DropsTheRowAtOnce_AndSaysTheFolderRefusedIt`
  - `AnAccessRefusalThatClearsWithinTheGrace_IsRetried_AndTheRowIsWritten`
  - Helpers:
    - `HoldTheLock` takes the lock through `JsonlFileAppender.Lock`, and `HeldFiles` holds any `IDisposable`.
    - `LockPaths` gives this minute's and the next minute's lock files.
    - `internal static DenyDeleting(folder)` adds a deny ACE for the process's own account (`Delete` and
      `DeleteSubdirectoriesAndFiles`, container and object inherit) and removes it on dispose.
- `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs` +1: `TheConsumer_InAFolderThatLetsItWriteButNotDelete_WritesEveryEntry`.
  `HoldTodaysLock` takes the lock through `JsonlFileAppender.Lock`; new `TodaysLockPaths`; `HeldFiles` holds any
  `IDisposable`.

Caller-visible changes, by design:
- A lock file that already exists opens without delete rights, so a folder that grants create, read and write keeps
  every audit, trace and extended row.
- A lock file the folder refuses costs a write 100 ms, not 2 s. The drop's report carries `JsonlLockRefusedException`,
  whose message says the log folder refused the lock file and names the rights, in place of "Another writer held the
  lock".
- A missing log folder or a device error at the lock is reported the same way, wrapped, instead of as the raw exception.
- A lock file stays beside its log file where the folder will not let it be deleted.
- Contention is unchanged: the wait, the drop, and `JsonlLockTimeoutException` with its text.

## Guard proof

Reproduction:
- M1 is the lock open as merged at `132d6e97`, `DeleteOnClose` back in. Against a lock file already there in a folder that
  denies delete, it was refused every time, and every row was dropped: `FileWriteFailed` for all three rows of each
  stream, and `[]` where the consumer wrote `["first", "second"]`.
- M2 is the merged retry's handling of that refusal. It waited out the whole bound, 5.02 s against the test's 5 s, and
  reported a `JsonlLockTimeoutException`.

Session-local driver (session scratchpad `mutate/mutate.ps1`, spec `mutate/round-a51.json`, log `mutate/a51-mutations.log`,
messages from the TRX files by `mutate/trx-messages.ps1`):
- For each mutant: the file's exact bytes saved and its MD5 taken, and one literal replacement that must match exactly
  once.
- A Release build of the solution with 0 errors required, then the filter run with a TRX logger.
- The exact bytes are restored in a `finally`, the mtime advanced so MSBuild rebuilds, and the MD5 re-checked.
- The filter (192 tests) is aud-a47's:
  `FullyQualifiedName~JsonlWriterLockTests|FullyQualifiedName~AuditWriterThrowFreeTests|FullyQualifiedName~AuditServiceTests|FullyQualifiedName~UsageTelemetryServiceTests|FullyQualifiedName~ExtendedLogServiceTests|FullyQualifiedName~AuditExtraChannelTests|FullyQualifiedName~AuditCategoryFilingTests|FullyQualifiedName~EventLog`
- Every mutant built with 0 errors, and each restore matched its original MD5. After the round, a rebuild on the restored
  bytes and the filter: 192/192 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 `DeleteOnClose` again in the lock open | `JsonlFileAppender.cs`, `DAA28E10B524854EF664C0B53297C84D` | `48BD4096848B29B477BAA01F2103D0AE` | 3 of 192: `AFolderThatLetsAWriterWriteButNotDelete_...` audit and trace - all three rows `FileWriteFailed` where `Written`; `TheConsumer_InAFolderThatLetsItWriteButNotDelete_...` - `[]` where `["first", "second"]` |
| M2 the access-failure branch folded back into the retry (an `UnauthorizedAccessException` waited for to the bound, then `JsonlLockTimeoutException`) | `JsonlFileAppender.cs`, `DAA28E10B524854EF664C0B53297C84D` | `C8A0C0C3871569AA4DDE19CC65D0BB1A` | 1 of 192: `ALockFileTheFolderRefuses_...` - 00:00:05.022 where at most 2 s |
| extra E1 no grace (an access refusal reported at the first) | `JsonlFileAppender.cs`, `DAA28E10B524854EF664C0B53297C84D` | `871D7A1C019176E0281039B7DE5C7B73` | 1 of 192: `AnAccessRefusalThatClearsWithinTheGrace_...` - "the writer gave up at the first refusal" |

E1 failed nothing else. Under it `TwoWritersOnSeveralThreads_WithInterleavedFailures_...` stayed green: six threads
contending, with any access refusal reported at once and its row dropped. So in that run the move-then-delete release left
no refusal for a contender to meet, as the probe measured. One run, not a proof.

Unchanged through the round:
- `JsonlLogService.cs` `1B0D9E1F006E6B43131A60C3FA4EEA90`
- `ExtendedLogService.cs` `7123153E13E841B7C5619887C4F93098`
- `JsonlWriterLockTests.cs` `F9C2E00B152B93757FC9FD399F899ABA`
- `ExtendedLogServiceTests.cs` `4B7B5AB12D39289F4E4C887E045985CE`

The code comments are written after all three rounds, in their own commit, so the MD5s above are this commit's bytes.

Not run, by design: two app pools on a real IIS host under accounts that differ, and a folder whose ACL was set by
`icacls`. The tests' deny ACE is the same kind of entry an ACL drift leaves, on a temp folder, for the test's own
account.

## Known gaps

- *Assumption, not probed*: a log root on an SMB share, as aud-a47's record says.
- An instance still on the aud-a47 build opens the lock with `DeleteOnClose`. In a folder without delete rights it cannot
  open a lock file this build leaves there, so its writes time out as before until it runs this build. Both instances
  on the same build, as the README says.
- An access refusal is retried for 100 ms before it is reported. A refusal that outlasts that while a third party
  deletes the lock file is reported as the folder's refusal and the row is dropped. The longest such run measured was
  5.1 ms, with six threads all opening the lock as the aud-a47 build does (Plan, evidence 6). A mix of the two builds
  was not probed.
- A refused lock file costs each write 100 ms, on the operator's thread for an audit write, until the folder is fixed.
  Each write reports it.
- A writer killed while it holds the lock leaves the lock file in place. The next writer opens it.
- The release's move can leave `<lock>.<32 hex>` behind if its delete fails. No log name or glob matches it.
- Rotation still needs delete: in a folder without it, a file that reaches its size limit cannot be rotated, and every
  write to it fails, reported. That is not this finding's defect; it is listed as a candidate in the hand-back.

## Comments after the round

Written after all three findings' rounds, in their own commit (`c2d6215d`), with aud-a48's and aud-a49's. Checked by
`check-comment-only.ps1` (session scratchpad `mutate/`), on aud-a47's definition: a file's code lines are every line
neither blank nor a comment (`//`, `///`, a one-line Razor `@* *@`). They must be identical, in order, between `6372c392`
and `c2d6215d`, line by line and by the MD5 of the code lines.
- Its negative controls, the three fix commits, each fail on every code file they change: 5, 2 and 4 files.
- Run on aud-a47's own comments commit (`543f5d5f..ab666f85`), it reproduces that record's published file and code-line
  MD5s for all five files.

This record's files:
- `Services/JsonlFileAppender.cs` `DAA28E10B524854EF664C0B53297C84D` -> `2081391684DC8E21680C92D40ADA1D29`: 132 code lines,
  their MD5 `2F000C7938038CABEE029AA535892E21` on both sides. 31 lines were added and 3 rewritten (34 in, 3 out):
  - the class's aud-a51 remark: the lock is the handle;
  - the grace and why 100 ms;
  - `Append`'s two bounds;
  - the refusal branch and the at-once branch;
  - the handle, and why the release moves before it deletes;
  - the refusal exception.
  - The rewritten lines are `Lock`'s summary, which named the file deleted on close.
- `Services/JsonlLogService.cs` `1B0D9E1F006E6B43131A60C3FA4EEA90` -> `5EE3D98CD0862C4D4BEF868A60383EA7`: 167 code lines,
  their MD5 `CE5AEBFFC3E69E61452301E7C479E350` on both sides. 6 lines were added: the refused lock in `WriteToFile`'s
  comment, and the grace seam.

## Branch-tip gates - 2026-10-22 (`c2d6215d`: the plan, the three fixes and the comments)

These gates are this branch's, shared by aud-a48's and aud-a49's records.
- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: exit 0, 0 errors, 19 warnings. That is the baseline taken
  at `132d6e97` before any change: CS8604, CS8602, CS0649 and CS0414 in `GroupManagement.razor`, `SelfServiceGroups.razor`,
  `HeaderAnalysisService.cs`, `AccountLockoutRemediationService.cs`, `MigrationReportExportProcessor.cs`,
  `RiskyUsers.razor`, `MessageTrace.razor` and `Comms10kApprovedSetTests.cs`. None is in a file this branch touches, and
  NuGet audit raised nothing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug): exit 0.
  - 6,139 passed / 3 skipped / 0 failed, 6,142 total, 6m05s.
  - The three skips are the known AD ones, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`.
  - Nothing hung, and no Sequence file was written.
  - The fifteen new cases are this branch's: aud-a51 five, aud-a48 six, aud-a49 four. The base was not run separately;
    6,127 is expected at `132d6e97`.
  - The full suite was run at the tip only. Each fix commit's own bytes ran its round's filter green before it was made:
    192, 160 and 511 tests.
- `ClickGate*` 316/316 and `ProgressRegistryTests` 35/35, each run alone (Release).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0. These were run on each fix commit's and the comments commit's
  staged bytes before it was made, and again on this records commit. The plan commit, records only, had
  `git diff --check`.
- Not run: `Invoke-ScriptAnalyzer` and `Invoke-Pester tests/ps` - no PowerShell changed.

No AD, EXO, Graph, DHCP or IIS call, no real module, no Delinea, SMTP, push or deployment. The stand-ins were:
- real JSONL writers and a real extended log over temp folders;
- raw handles and directories as the other instance and as a refused lock file;
- a deny ACE for the test's own account on a temp folder;
- real config stores whose reads the tests can fail.

No coder self-review verdict is claimed.

## Closeout

Fixed on branch `worktree-agent-ac1038e1d7ece4ae5`, not merged, not pushed. This is shared infrastructure, the appender
every audit, trace and extended write ends in, so the base app version is due. The bump is the supervisor's, at merge,
because `ExchangeAdminWeb.csproj` was outside this brief.

No file another agent owns was needed, so nothing is left for a follow-up under that heading. `Services/AuditService.cs`'s
synchronous write (491) now costs a refused lock file the 100 ms grace, not the 2 s bound. Moving the write off the
operator's thread would remove what a live contender still costs, but that is aud-a47's known gap and a design change
of its own.

## Codex review

Pending.
