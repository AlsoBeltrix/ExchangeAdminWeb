# aud-a56: A failed JSONL rotation drops every row until rotation succeeds, and the audit writer never recreates its folder

**Severity**: MEDIUM/HIGH - audit suppression once the file reaches its size limit. (1) `JsonlFileAppender`
runs the rotation under the lock before the write; a throw from the rotation's `File.Move` (no delete right
on the folder; another process holding the full file open without delete sharing, such as a SIEM tailer or
an editor) fails the write, and from then on every row to that file is dropped until a rotation succeeds -
by default once the file reaches 50 MB. (2) `JsonlLogService` creates `<LogRoot>\ExchangeAdminWeb` once, in
its constructor; if the folder is deleted while the app runs, every audit and trace write fails until a
restart (the extended log recreates it on every entry).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; in progress (plan written)
**Branch**: `worktree-agent-a7b48a52dcf67eb2b` (from `a7300dee`)
**Commit**: -
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

## Guard proof

Pending.
