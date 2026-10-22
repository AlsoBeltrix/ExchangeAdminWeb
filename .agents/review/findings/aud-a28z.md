# aud-a28z: ExtendedLogService has the unguarded recovery logging aud-a28u removed from the JSONL writer, and a value whose ToString throws still escapes WriteAuditEvent

**Severity**: MEDIUM. (1) `ExtendedLogService`'s catches log with an unguarded `_logger` call: in `Write`
(the detail-factory catch) a throwing logger escapes into the caller after the operation completed (the
aud-a28q class), and in `ProcessQueueAsync` it would end the background consumer, after which nothing is
written to the extended log until a restart and no one is told. (2) Left from aud-a28q: a value whose
`ToString` throws, in a field `WriteAuditEvent` reads before handing the record to the writer, still
throws out of the audit call.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28u/a26r agent, 2026-10-22, candidates 1 and 5 of its hand-back)

## Evidence

At `172902c2`: `Services/ExtendedLogService.cs` `Write` (the detail-factory catch) and `ProcessQueueAsync`
(the consumer loop's catch); `Services/AuditService.cs` `WriteAuditEvent` (the fields read before the
writer: `SamName` and the others; which call `ToString` on a caller-supplied value).

## Approach (to be written by the implementing agent before code)

- `ExtendedLogService`: the catch-block logging sits in a nested try (the aud-a28u shape); the consumer
  loop never exits on a logging failure - it drops the item, counts it, and continues; a dropped item is
  visible (one counter the health surface or a later log line can show).
- `WriteAuditEvent` reads caller-supplied values through a guarded text conversion (the aud-a28q
  `TextField` shape) so a throwing `ToString` becomes marker text, never a throw.
- Tests: the aud-a28q tripwire's throwing-logger theory extended to `ExtendedLogService`; a consumer that
  survives a throwing logger; a record with a throwing `ToString` written with marker text. Mutations at
  the call site: the nested guard removed in `ProcessQueueAsync`; the raw `ToString` again.

## Guard proof

Pending.

## Folded in: a truncated JSONL tail swallows the next Written row; the dropped-row recovery log carries no identity (codex, attr-batch-12, 2026-10-22) - MEDIUM, MEDIUM

(3) `Services/JsonlLogService.cs` `WriteToFile` around line 88: a write that appends part of a line and
fails before the newline leaves a corrupt tail; the next successful audit row is appended onto it, the
writer returns `Written`, the "AuditWritten" step is Success and the action is counted, yet the Admin Event
Log parser drops the combined invalid line. Fix: capture the file length before appending and truncate back
to it on a write or flush failure (best effort, guarded), or detect and repair a non-newline tail before
the next append so each row starts on its own line; a repair is itself logged. Test: a stream that fails
mid-line, then a successful write, then the file parses to exactly the rows reported Written. Mutation at
the call site: the truncate-back removed.

(4) `Services/JsonlLogService.cs` around line 106: when a row is `NotSerializable` or `FileWriteFailed`
and the operation trace is unavailable or also drops, the surviving recovery log names stream, action,
category and path but not who did what: no operation id, actor, IP, target, ticket or result, so the audit
gap cannot be reconciled. Fix: the guarded recovery log includes those as string-only fields through
`TextField` (never the payload, never a raw `ToString`). Test: the recovery log line for a dropped row
contains the operation id and actor. Mutation at the call site: the identity fields dropped from the log.
