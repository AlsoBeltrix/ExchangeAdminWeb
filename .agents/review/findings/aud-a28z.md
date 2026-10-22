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
