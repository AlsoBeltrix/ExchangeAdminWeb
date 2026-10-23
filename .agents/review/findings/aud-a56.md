# aud-a56: A failed JSONL rotation drops every row until rotation succeeds, and the audit writer never recreates its folder

**Severity**: MEDIUM/HIGH - audit suppression once the file reaches its size limit. (1) `JsonlFileAppender`
runs the rotation under the lock before the write; a throw from the rotation's `File.Move` (no delete right
on the folder; another process holding the full file open without delete sharing, such as a SIEM tailer or
an editor) fails the write, and from then on every row to that file is dropped until a rotation succeeds -
by default once the file reaches 50 MB. (2) `JsonlLogService` creates `<LogRoot>\ExchangeAdminWeb` once, in
its constructor; if the folder is deleted while the app runs, every audit and trace write fails until a
restart (the extended log recreates it on every entry).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
