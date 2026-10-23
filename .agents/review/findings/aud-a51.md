# aud-a51: The JSONL lock file needs delete rights and reports a permanent access failure as lock contention

**Severity**: HIGH - audit suppression under an ACL drift. `JsonlFileAppender` opens `<active>.lock` with
`FileOptions.DeleteOnClose`, so every audit, trace and extended-log write needs delete rights on the log
folder. Where the app pool identity may create, write and append but not delete, or any persistent
`UnauthorizedAccessException` comes back from the lock open, every write waits the full 2 s bound and is
dropped, and the logged exception is `JsonlLockTimeoutException` "Another writer held the lock..." - no
writer is contending and the real cause is permissions. `AuditService.WriteAuditEvent` calls the writer
synchronously on the circuit path, so each affected row also stalls the operator for 2 s before it is lost.
The test lock holders do not use `DeleteOnClose`, so the edge is uncovered.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; folded into the aud-a48/a49 agent's scope
**Branch**: -
**Commit**: -
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

## Guard proof

Pending.
