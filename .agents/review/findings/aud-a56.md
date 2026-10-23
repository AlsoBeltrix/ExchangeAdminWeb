# aud-a56: A failed JSONL rotation drops every row until rotation succeeds, and the audit writer never recreates its folder

**Severity**: MEDIUM/HIGH - audit suppression once the file reaches its size limit. (1) `JsonlFileAppender`
runs the rotation under the lock before the write; a throw from the rotation's `File.Move` (no delete right
on the folder; another process holding the full file open without delete sharing, such as a SIEM tailer or
an editor) fails the write, and from then on every row to that file is dropped until a rotation succeeds -
by default once the file reaches 50 MB. (2) `JsonlLogService` creates `<LogRoot>\ExchangeAdminWeb` once, in
its constructor; if the folder is deleted while the app runs, every audit and trace write fails until a
restart (the extended log recreates it on every entry).
**Status**: Complete; merged `3da510cb`; codex attr-batch-16 raised one MEDIUM, admitted as aud-a65
**Branch**: `worktree-agent-a7b48a52dcf67eb2b` (from `a7300dee`)
**Commit**: `0ccdc758` (fix, tests, README, record); comments `173613a7`; plan `c3960fe9`
**Reviewer**: implementer (surfaced by the aud-a51/a48/a49 agent, 2026-10-22, candidates 1 and 2 of its hand-back)

## Evidence

At `81809833`: `Services/JsonlFileAppender.cs` around line 75 (`rotateIfFull()` under the lock);
`Services/JsonlLogService.cs` around 205 (the rotation's `File.Move`) and 32 (the folder created in the
constructor); `Services/ExtendedLogService.cs` around 403 and 311.

## Approach (to be written by the implementing agent before code)

- A rotation that fails does not fail the write: the row is appended to the current file (over the size
  limit) and the rotation failure is reported once per failure streak through the aud-a28z recovery path,
  naming the file and the reason; the next write tries the rotation again. Nothing is dropped for a
  rotation failure.
- The appender ensures the folder exists under the lock before each write (cheap `Directory.CreateDirectory`),
  so a deleted folder is recreated; a folder that cannot be created is the aud-a51 refusal.
- Tests: a rotation whose move throws, then rows still written and parseable; the folder removed between
  writes, the next row written. Mutations at the call site: rotation failure failing the write again; the
  constructor-only folder creation again.

*Amended 2026-10-23 by the implementing agent, from the code read before any change (Plan, evidence 4 and 5):*
- "Under the lock" is read as "in the appender, on every write, ahead of the lock". The lock file lives in the log folder,
  so the folder has to exist before the lock can be taken. A folder that cannot be created is reported as the aud-a51
  refusal, at once.
- "The next write tries the rotation again" needs a rotation that loses nothing when it is retried. The rotation as
  merged deletes the oldest rotated file and moves the others on before it moves the full file. When that last move
  fails, every retry deletes one more rotated file. The rotation becomes gap-aware: it deletes the oldest only when every
  place is taken, so a retry deletes nothing.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"). Admitted from the aud-a51/a48/a49 hand-back, candidates 1 and 2.
One commit `fix(audit-log): ... (aud-a56)`. It is the first of three on branch `worktree-agent-a7b48a52dcf67eb2b` (from
`a7300dee`), before aud-a57 and aud-a58. It changes the appender that every audit, trace and extended-log write ends in,
so the base app version is due; the supervisor owns the bump.

Evidence, re-read at `a7300dee`:
1. `Services/JsonlFileAppender.cs` `Append` (62-100) takes the lock (74), then calls `rotateIfFull()` (75) with no guard.
   A rotation that throws leaves `Append` before the line is written.
2. `Services/JsonlLogService.cs`:
   - `WriteToFile`'s catch (103-121) turns that throw into `FileWriteFailed`: the row is dropped and reported.
   - `RotateIfFull` (182-188) rotates once the active file reaches its limit. The limit is `Audit:MaxFileBytes`, else
     `Audit:MaxFileMB`, by default 50 MB. It runs on every write, so while the rotation fails every row to that file is
     dropped.
3. `RotateLogFiles` (190-206) deletes the oldest rotated file, moves each of the others one place on, and moves the active
   file to place 1 last (205).
   - A move is refused while another process holds that file open without delete sharing, such as a SIEM tailer or an
     editor.
   - A move and a delete are refused in a folder that denies this account delete.
4. Read from the code, not in the record: retrying that rotation destroys rotated files.
   - When the last move fails, the oldest file is already deleted and the others have moved on. Place 1 is empty.
   - The next write finds the file still full and rotates again. It deletes the new oldest - a file the first attempt
     kept - and moves the rest on.
   - So each retry deletes one more rotated file until none is left. Before this fix each retry was also a dropped row.
   - With the row kept and the rotation retried on each write, "Nothing is dropped for a rotation failure" holds only if
     the rotation itself loses nothing on a retry.
5. The folder:
   - The constructor creates `<LogRoot>\ExchangeAdminWeb` (32), and nothing else does.
   - The lock file lives in that folder. Once the folder is gone, `JsonlFileAppender.Lock`'s open meets
     `DirectoryNotFoundException` and refuses at once (139-143).
   - So every audit and trace row is dropped, and reported, until a restart.
6. `Services/ExtendedLogService.cs`:
   - The consumer creates the folder before each entry (311).
   - Its rotation (380-404) has the same shape as the JSONL writer's, through the same appender. A failed rotation
     therefore drops every extended entry too, and counts it.
   - The constructor creates the folder as well (66).

**Rule.** A rotation never costs a row. A rotation that fails leaves the row in the full file, past its limit. It is
reported once per failure streak, naming the file and the reason, and the next write tries again. A retry deletes nothing
a completed rotation would have kept. And a write never fails because its folder was removed: it is created again, and a
folder that cannot be created is the folder's refusal.

1. `Services/JsonlFileAppender.cs`:
   - `Append` gains a callback `rotationFailed` (`Action<Exception>`), after `rotateIfFull`. `rotateIfFull()` runs in a
     try whose catch hands the exception to that callback and goes on. The line is then written to the file as it is.
   - Before the lock, `Append` creates the file's folder (`Directory.CreateDirectory`). A failure there is a
     `JsonlLockRefusedException` naming the lock file, with the cause inside: the aud-a51 refusal, at once.
   - New `internal static void Rotate(string activePath, int places, Func<int, string> rotatedPath)`, both writers'
     rotation:
     - It finds the first free place and moves on only the rotated files below it.
     - It deletes the oldest, at `places`, only when every place is taken.
     - Its moves never overwrite.
     - A rotation that failed at its last move leaves place 1 free, so a retry moves only the active file and deletes
       nothing.
2. `Services/JsonlLogService.cs`:
   - `RotateLogFiles` becomes `JsonlFileAppender.Rotate(activePath, _maxFilesPerPeriod - 1, ...)`.
   - `AppendLine` passes `rotationFailed`, which reports one warning per stream per failure streak through
     `ReportSafely`, the exception attached. It names the stream, the file and the reason, and says that the event was
     written to the file past its limit and that each write tries the rotation again.
   - The streak is a `HashSet` of streams, read under `_lock`. It ends when a write's `rotateIfFull` returns: the file
     was rotated, or was under its limit.
   - The constructor's folder creation stays.
3. `Services/ExtendedLogService.cs`:
   - `RotateLogFiles` goes through `JsonlFileAppender.Rotate`.
   - `AppendLine` passes the same report for the extended file, once per streak, kept in a field only the one consumer
     touches.
   - The consumer's own `Directory.CreateDirectory` (311) goes: the appender now creates the folder for both writers.
4. `README.md`, "Shared log root" and "Rotation": a rotation that fails keeps the write and is logged once per streak,
   and a deleted log folder is created again. The sentence "a file that reaches its size limit cannot be rotated and the
   writes to it fail" is no longer true and is replaced.
5. Comments after the mutation round, checked code-identical.

Tests:
- New `ExchangeAdminWeb.Tests/JsonlWriterRotationTests.cs`, over a real writer on a temp root with `Audit:MaxFileBytes`
  1024:
  - `AFullFileThatCannotBeRotated_StillTakesEveryRow_AndTheFailureIsReportedOnce`, for audit and trace, by cause:
    - the full file held open without delete sharing;
    - the folder denying this account delete (aud-a51's `DenyDeleting`).
    - Three rows are each `Written` and parse, with no error and one warning that names the file.
  - `AFailureStreak_EndsWhenARotationSucceeds_AndTheNextFailureIsReportedAgain`.
  - `ARotationThatFailedAtItsLastMove_DeletesNothingMoreWhenItIsRetried`:
    - Rotated files 1 and 2 are in place (`Audit:MaxFilesPerPeriod` 3), and the full file is held. Three rows are
      written, the hold is released, and a fourth row rotates the file.
    - Every row a completed rotation keeps is still there: the full file's rows and the three at place 1, the old place
      1's at place 2.
  - `ALogFolderRemovedWhileTheAppRuns_IsCreatedAgain_AndTheNextRowIsWritten` (audit, trace).
  - `ALogFolderThatCannotBeCreated_IsTheFoldersRefusal_AtOnce_AndTheNextRowIsWrittenOnceItCanBe`: a file stands where the
    folder belongs.
- `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs`:
  - `TheConsumer_GivenAFullFileItCannotRotate_WritesEveryEntry_AndReportsItOnce`.
  - `TheConsumer_GivenItsFolderRemoved_CreatesItAgain_AndWritesTheNextEntry`.
- Unchanged: `Write_RotatesActiveLog_WhenFileExceedsConfiguredLimit` and the lock tests.

Mutations at the call site (md5 before and after, the named failing test, restored from the index, touched, rebuilt,
md5 matched, green):
- M1 the rotation failure failing the write again (`rotateIfFull()` unguarded in `Append`).
- M2 the constructor-only folder creation again (`Append`'s folder step removed).
- Extra E1 the old rotation again in `Rotate` (delete the oldest, move every place on).
- Extra E2 the report on every failed write (the streak check removed).
- Extra E3 the streak never ending (reported once per writer).

Filter: aud-a51's (`FullyQualifiedName~JsonlWriter` now also matches the new file).

Files:
- `Services/JsonlFileAppender.cs`
- `Services/JsonlLogService.cs`
- `Services/ExtendedLogService.cs`
- `ExchangeAdminWeb.Tests/JsonlWriterRotationTests.cs` (new)
- `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs`
- `README.md`
- this record

## Fix

- `Services/JsonlFileAppender.cs`:
  - `Append` takes `rotationFailed` after `rotateIfFull`.
    - Before the lock it calls new private `EnsureFolder(path)`: `Directory.CreateDirectory` of the file's folder. An
      `IOException` or `UnauthorizedAccessException` there is thrown as `JsonlLockRefusedException(LockPathFor(path), ex)`.
    - Under the lock, `rotateIfFull()` sits in a try whose catch calls `rotationFailed(ex)`. The append then goes on
      unchanged.
  - New `internal static void Rotate(string activePath, int places, Func<int, string> rotatedPath)`:
    - It finds `free`, the first place below `places` with no file, or `places` itself.
    - It deletes the file at `free` only if one is there, which happens only when every place is taken.
    - It moves places `free - 1` down to 1 each one on, then the active file to place 1. No move overwrites.
    - `places` below 1 throws.
- `Services/JsonlLogService.cs`:
  - New `_rotationFailureReported` (`HashSet<LogStream>`, under `_lock`).
  - `AppendLine`'s `rotateIfFull` runs `RotateIfFull`, then removes the stream from the set.
  - Its `rotationFailed` adds the stream. When it was not there, it reports through `ReportSafely`: `LogWarning(ex, "Could
    not rotate the JSONL {Stream} file {Path}, which has reached its size limit: {Reason} The event was written to it, past
    the limit. Each write tries the rotation again; this is not reported again until a write finds the file rotated or
    under its limit")`.
  - `RotateLogFiles` is `JsonlFileAppender.Rotate(activePath, _maxFilesPerPeriod - 1, index => GetRotatedLogPath(activePath,
    index))`.
- `Services/ExtendedLogService.cs`:
  - New `_rotationFailureReported` (a `bool` only the consumer touches), with the same reset and report for the extended
    file ("The entry was written to it ... until an entry finds the file rotated or under its limit").
  - `RotateLogFiles` goes through `JsonlFileAppender.Rotate`.
  - The consumer's `Directory.CreateDirectory` is gone; the appender creates the folder.
- `README.md`:
  - "Shared log root": the rotation sentence says the writes land in the full file and the failure is logged.
  - "Rotation": two new bullets, on a rotation that fails and on the folder created before every write.

Tests:
- `ExchangeAdminWeb.Tests/JsonlWriterRotationTests.cs` (new), +9:
  - `AFullFileThatCannotBeRotated_StillTakesEveryRow_AndTheFailureIsReportedOnce`: audit and trace, by each of the two
    causes.
  - `AFailureStreak_EndsWhenARotationSucceeds_AndTheNextFailureIsReportedAgain`.
  - `ARotationThatFailedAtItsLastMove_DeletesNothingMoreWhenItIsRetried`.
  - `ALogFolderRemovedWhileTheAppRuns_IsCreatedAgain_AndTheNextRowIsWritten`: audit and trace.
  - `ALogFolderThatCannotBeCreated_IsTheFoldersRefusal_AtOnce_AndTheNextRowIsWrittenOnceItCanBe`.
  - Helpers: `Writer` (`Audit:MaxFileBytes` 1024), `FillPastTheLimit`, `RotatedFile`, `Obstruct`, `Hold`, `Rows` and
    `RowsIn` (parsed as the Admin Event Log parses a line), and a `CapturingLogger` with `Errors` and `Warnings`. The
    folder-denies-delete cause reuses aud-a51's `JsonlWriterLockTests.DenyDeleting`.
- `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs`, +2:
  - `TheConsumer_GivenAFullFileItCannotRotate_WritesEveryEntry_AndReportsItOnce`.
  - `TheConsumer_GivenItsFolderRemoved_CreatesItAgain_AndWritesTheNextEntry`.

Caller-visible changes, by design:
- A rotation that fails no longer drops the row. The row is written to the full file, past its size limit, and is reported
  `Written`. One warning per failure streak names the stream, the file and the reason. Every later write tries again.
- A rotation that fails part-way deletes nothing more on its retries. Rotated files are kept in order with a gap where a
  rotation stopped, and readers already skip a place with no file.
- The log folder is created before every audit, trace and extended write. A folder that cannot be created is a
  `JsonlLockRefusedException`, at once.
- An extended entry whose folder cannot be created is reported with that exception, where the raw exception was before.
  It is still dropped and counted.

## Guard proof

Reproduction:
- M1 puts back the merged call site, `rotateIfFull()` with no guard.
  - Every row to a full file the rotation cannot move was `FileWriteFailed`, with either cause and on both streams.
  - Every extended entry to such a file was dropped.
- M2 is the merged folder handling: only the constructor creates the folder. With the folder removed while the writer
  lives, each next row was `FileWriteFailed`, and the extended entry was dropped.
- E1 is the merged rotation, moved unchanged into `Rotate`. Its retries deleted the file at place 2, the rows a completed
  rotation keeps.

Session-local driver (session scratchpad `mutate/mutate.ps1`, spec `mutate/round-a56.ps1`, log `mutate/a56-mutations.log`):
- For each mutant: the file must equal its staged version, and its MD5 is taken. One literal replacement must match exactly
  once.
- A Release build of the solution with 0 errors is required. Then the filter runs with a TRX logger, and the failing tests
  are read from the TRX.
- In a `finally`, the file is restored from the index (`git checkout --`), its mtime advanced so MSBuild rebuilds, and its
  MD5 re-checked.
- The filter (219 tests) is aud-a51's, with `JsonlWriterLockTests` widened to `JsonlWriter`:
  `FullyQualifiedName~JsonlWriter|FullyQualifiedName~AuditWriterThrowFreeTests|FullyQualifiedName~AuditServiceTests|FullyQualifiedName~UsageTelemetryServiceTests|FullyQualifiedName~ExtendedLogServiceTests|FullyQualifiedName~AuditExtraChannelTests|FullyQualifiedName~AuditCategoryFilingTests|FullyQualifiedName~EventLog`
- Every mutant built with 0 errors, and each restore matched its original MD5. After the round, a rebuild on the restored
  bytes and the filter: 219/219 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 the rotation failure failing the write again (`rotateIfFull();` with no try in `Append`) | `JsonlFileAppender.cs`, `DC89243CFA1B0E03A10E2D13AAF76E02` | `959220AE2812B5FBBC1DE06128EF9D2A` | 7 of 219: `AFullFileThatCannotBeRotated_...`, all four cases - three `FileWriteFailed` where `Written`; `AFailureStreak_...` and `ARotationThatFailedAtItsLastMove_...` - `FileWriteFailed` at the first held write; `TheConsumer_GivenAFullFileItCannotRotate_...` - "first" and "second" missing after the twelve fillers |
| M2 the constructor-only folder creation again (`EnsureFolder(path);` removed from `Append`) | `JsonlFileAppender.cs`, `DC89243CFA1B0E03A10E2D13AAF76E02` | `6871C2FEBF3BE3EFE281E9BC2CC0B2D3` | 4 of 219: `ALogFolderRemovedWhileTheAppRuns_...`, audit and trace - `FileWriteFailed` where `Written`; `ALogFolderThatCannotBeCreated_...` - the row after the file is removed `FileWriteFailed`; `TheConsumer_GivenItsFolderRemoved_...` - `[]` where `["after the folder went"]` |
| extra E1 the merged rotation again in `Rotate` (delete the oldest, move every place on, overwrite) | `JsonlFileAppender.cs`, `DC89243CFA1B0E03A10E2D13AAF76E02` | `D34D2D29AC65E142E40922E828689D9A` | 1 of 219: `ARotationThatFailedAtItsLastMove_...` - "exchangeadmin_20261023.2.jsonl is not there": the second held write's retry deleted it |
| extra E2 the report on every failed write (`... .Add(stream) \|\| true`) | `JsonlLogService.cs`, `162259BFE9B0D338E9A6EBBB2531FF83` | `7AD40BC44474C78D3F73DA5552D572CD` | 6 of 219: `AFullFileThatCannotBeRotated_...`, all four cases - three warnings where one; `AFailureStreak_...` - two where one; `ARotationThatFailedAtItsLastMove_...` - three where one |
| extra E3 the streak never ending (`_rotationFailureReported.Remove(stream);` removed) | `JsonlLogService.cs`, `162259BFE9B0D338E9A6EBBB2531FF83` | `C7F1997FF649AC41891D7D530D8AB30F` | 1 of 219: `AFailureStreak_...` - one warning where two: the failure after a successful rotation went unreported |

E2 failed nothing in the extended log's tests, whose streak is its own field. Its extended test asserts one warning, and
passes under every mutant but M1.

Unchanged through the round:
- `ExtendedLogService.cs` `156155DAA88DA1DCEE3653625A0A490E`
- `JsonlWriterRotationTests.cs` `3359972DFE7FC7936D89496FFD11DA55`
- `ExtendedLogServiceTests.cs` `29CF5665284725B9B7F94716C5EDBFD1`

Any later comment edit is in its own commit, checked code-identical, so these MD5s are this commit's bytes.

Not run, by design:
- a real SIEM tailer or editor - the held handle is the same open such a program makes;
- a folder whose ACL was set by `icacls` - the deny ACE is aud-a51's, for the test's own account, on a temp folder;
- a log root on an SMB share.

## Comments after the round

The fix's comments were written before the round and are in the bytes measured above. After all three rounds, one comment
the fix made untrue was corrected in its own commit: `JsonlWriteOutcome.FileWriteFailed`'s summary in
`Services/JsonlLogService.cs` named "rotated" among the causes of a drop. It now names the folder and the lock, and says
that a rotation that fails does not drop the record.

Checked by `check-comment-only.ps1` (session scratchpad `mutate/`):
- A file's code lines are every line that is neither blank nor a comment (`//`, `///`, a one-line Razor `@* *@`). They
  must be identical, in order, between the fix commit and the comments commit.
- `JsonlLogService.cs` `162259BFE9B0D338E9A6EBBB2531FF83` -> `9998476073D515F780865B3B59328CC5`. It has 169 code lines on
  both sides, their MD5 `734B236DC600D707E0551B77504352A3`. 4 lines in, 1 out.
- Negative control: the fix commit itself (`c3960fe9` -> `0ccdc758`) fails the check on the same file, 167 code lines
  against 169.

## Known gaps

- The first attempt of a rotation that fails at its last move has already deleted the oldest rotated file and moved the
  others on, as a completed rotation does. A full file that stays unrotatable then sits beside one rotated file fewer
  than the limit allows, until it can be moved. The retries delete nothing more.
- While a rotation keeps failing, the full file grows without bound, one row past its limit at a time. The warning is
  the operator's cue, and is given once per streak.
- A streak ends when a write finds the file rotated or under its limit. A write to a new day's file ends the streak, so
  that file, if it fills and cannot be rotated either, is reported in its own right.
- *Assumption, not probed*: another process creating a file at a rotated place between `Rotate`'s check and its move.
  The move would fail, since moves no longer overwrite, and the failure is reported like any other.
- `EnsureFolder` runs before the lock. A folder removed between it and the lock's open is the aud-a51 refusal for that
  row, at once, and the next row creates the folder again.
- The extended log's consumer still counts a folder it cannot create as a dropped entry, as before. Only the exception
  it reports is different.

## Branch-tip gates - 2026-10-23 (`173613a7`: the plans, the three fixes and the comments)

These gates are this branch's, shared by aud-a57's and aud-a58's records.
- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: exit 0, 0 errors, 19 warnings.
  - That is the baseline taken at `a7300dee` before any change: CS8604, CS8602, CS0649 and CS0414 in
    `GroupManagement.razor`, `SelfServiceGroups.razor`, `HeaderAnalysisService.cs`, `AccountLockoutRemediationService.cs`,
    `MigrationReportExportProcessor.cs`, `RiskyUsers.razor`, `MessageTrace.razor` and `Comms10kApprovedSetTests.cs`.
  - None is in a file this branch touches, and NuGet audit raised nothing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug): exit 0.
  - 6,344 passed / 3 skipped / 0 failed, 6,347 total, 6m55s.
  - The three skips are the known AD ones, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`.
  - Nothing hung, and no Sequence file was written.
  - The thirty-seven new cases are this branch's: aud-a56 eleven, aud-a57 six, aud-a58 twenty. The base was not run
    separately; 6,310 is expected at `a7300dee`.
  - The full suite was run at the tip only. Each fix commit's own bytes ran its round's filter green before it was made:
    219, 218 and 206 tests.
- `ClickGate*` 316/316 and `ProgressRegistryTests` 35/35, each run alone (Release).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
  - The format check and `git diff --check` ran on each fix commit's staged bytes before it was made, with a per-file
    non-ASCII scan of its code files.
  - All three ran on `173613a7`. `git diff --check` and the ASCII check ran again on this records commit.
  - The plan commit, records only, had `git diff --check`.
- Not run: `Invoke-ScriptAnalyzer` and `Invoke-Pester tests/ps` - no PowerShell changed.

No AD, EXO, Graph, DHCP or IIS call, no real module, no Delinea, SMTP, ServiceNow, push or deployment. The stand-ins were:
- real JSONL writers and a real extended log over temp folders;
- raw handles as the other program holding a full file;
- a deny ACE for the test's own account on a temp folder;
- real config stores whose reads the tests can fail;
- the Admin Event Log's and the Home page's own render trees, driven by reflection;
- a substitute HTTP client factory that is never called.

No coder self-review verdict is claimed.

## Closeout

Fixed on branch `worktree-agent-a7b48a52dcf67eb2b`, not merged, not pushed. The appender every audit, trace and
extended-log write ends in is shared infrastructure, so the base app version is due. The bump is the supervisor's, at
merge: `ExchangeAdminWeb.csproj` was outside this brief.
