# aud-a28u: The "AuditWritten" trace step reads Success even when the writer dropped the record

**Severity**: LOW/MEDIUM - audit honesty. Since aud-a28q the JSONL writer swallows a record it cannot
write (an unserialisable value, an IO failure) and logs one error; `AuditService.WriteAuditEvent` then
still runs its "AuditWritten" trace step as Success and its usage-telemetry call, because the writer
returns no outcome. The operation trace therefore says an audit row was written when none was, and a
reviewer reconciling trace to audit finds a step with no row.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the psl-7/a28q/a26o agent, 2026-10-22, candidate 3 of its hand-back)

## Evidence

At `5e2b80f3`: `Services/JsonlLogService.cs` `WriteToFile` (returns void; the catch logs and returns);
`Services/AuditService.cs` `WriteAuditEvent` (the "AuditWritten" trace step and the telemetry call after
the write; the comment aud-a28q corrected).

## Approach (to be written by the implementing agent before code)

- `WriteToFile` (and `Write`/`WriteAudit`/`WriteTrace`) return a bool: written or dropped. `WriteAuditEvent`
  records the trace step as Failed with "the audit row was not written: <reason category>" when dropped,
  skips or marks the telemetry call, and the caller's outcome stays unaffected (Constitution: an audit
  failure never fails the operation). The aud-a28q tripwire stays green (nothing throws).
- Tests through `AuditWriterThrowFreeTests`: a dropped record yields a Failed "AuditWritten" step and no
  telemetry count; a written record yields Success as today. Mutation at the call site: ignore the writer's
  outcome again.

## Guard proof

Pending.

## Folded in: the recovery logging in the writer's catch can itself throw (codex, attr-batch-11, 2026-10-22) - MEDIUM

`Services/JsonlLogService.cs` `WriteToFile` around line 91: after catching a serialisation or file-write
failure, the catch calls `_logger.LogError(...)`; a throwing `ILogger` provider or formatter path throws
out of the audit or trace write after the operation completed, reviving the aud-a28q class on the
recovery path. Guard the catch-block logging with a nested try/catch (or a non-throwing fallback) so no
failure while reporting a dropped record escapes `WriteToFile`; the same for the trace writer's recovery
path. Test: a logger whose `Log` throws does not make the write throw (the aud-a28q tripwire gains a
throwing-logger variant). Mutation at the call site: log without the nested guard again.
