# aud-a28z: ExtendedLogService has the unguarded recovery logging aud-a28u removed from the JSONL writer, and a value whose ToString throws still escapes WriteAuditEvent

**Severity**: MEDIUM. (1) `ExtendedLogService`'s catches log with an unguarded `_logger` call: in `Write`
(the detail-factory catch) a throwing logger escapes into the caller after the operation completed (the
aud-a28q class), and in `ProcessQueueAsync` it would end the background consumer, after which nothing is
written to the extended log until a restart and no one is told. (2) Left from aud-a28q: a value whose
`ToString` throws, in a field `WriteAuditEvent` reads before handing the record to the writer, still
throws out of the audit call.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; in progress (plan below)
**Branch**: `worktree-agent-aca5b5b4f6295736c`
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the aud-a28u/a26r hand-back (`d886a1f8`),
with codex attr-batch-12's two MEDIUMs folded in (`3e1111eb`; the "Folded in" section at the end of this record,
parts 3 and 4 below). One commit `fix(audit,extended-log): ... (aud-a28z)`, first of three on branch
`worktree-agent-aca5b5b4f6295736c` (from `18530b58`; aud-a26u and cdt-41 follow). Shared infrastructure - the extended
log, the audit service's common path and the JSONL writer every audit and trace call ends in - so a base app version
change; the supervisor owns the bump. Evidence re-read at `18530b58`:

1. `Services/ExtendedLogService.cs`: `Write` (123-150) reports a failed detail factory with `_logger.LogError` in its
   catch (132-135) and a closed queue with `_logger.LogWarning` (148-149), both on the caller's thread, unguarded.
   `ProcessQueueAsync` (225-242), the one background consumer, reports a failed write in its catch (237-240)
   unguarded: a provider that throws there faults the task and ends the loop, so every later entry waits in the
   bounded queue until `DropOldest` discards it, and nothing says so. The file's other catch reports are unguarded
   too: `SetLevel` (99-103, which then rethrows - a provider that throws replaces the store's exception with its own),
   `Dispose` (219-222) and `ImportLegacyLevelIfPresent` (326-329, run by the constructor). And `MinimumLevel` (74-82),
   which `Write` reads first through `IsEnabledFor`, asks the change watcher, whose report of an unreadable change
   token (`Services/Storage/ConfigChangeWatcher.cs` `LogOnce`, 111-121) goes to this service's own logger (45),
   unguarded: a provider that throws there escapes `Write` as well.
2. `Services/AuditService.cs` `GetString` (543-544) is `value?.ToString()`. `WriteAuditEvent` reads category, action,
   user, ip, the target (`GetAuditTarget` 540-541: target, batchName, section), ticket and result through it - for the
   implicit operation (468-475), the operation's result (494), the step's details (521-528) and the usage count
   (512-516). `MergeExtra` (38-45) lets a caller's `extra` replace any of them with any object, so a value whose
   `ToString` throws escapes the audit call, before the write or after it. The writer has the same read:
   `Services/JsonlLogService.cs` `Write` (35-41) routes a record by `evt.GetValueOrDefault("eventType")?.ToString()`
   (37), outside `WriteToFile`'s try (both callers set a string there today).
3. `Services/JsonlLogService.cs` `WriteToFile` (75-117) writes the line through a `StreamWriter` over a buffered
   `FileStream` opened `FileMode.Append` (88-90). A failure after part of the line reached the file (a full disk, a
   write that fails between buffer flushes) leaves a tail with no newline; the next record is appended onto it and
   reported `Written` - the step Success, the action counted - and the Admin Event Log, which parses each line with
   `JsonDocument.Parse` (`Components/Pages/AdminEventLog.razor` `ParseAuditLine` 696-700, its catch dropping the line),
   drops the combined line. A process that dies mid-write leaves the same tail.
4. The catch's report (104-113) names stream, action, category and path, but not who did what: when the trace is
   unavailable or drops as well, nothing ties the missing row to an operation or an actor.

**Rule.** No logging failure escapes the extended log into a caller or ends its consumer, and an entry the consumer
cannot write is dropped, counted, and the next one written. A caller-supplied value is read as text without letting a
`ToString` throw into the audit call. Every record the writer reports `Written` is a line of its own that the event log
parses; a record it drops is reported with who did what - string fields only, never the payload.

1. ExtendedLogService: new private `ReportSafely(Action<ILogger>)` - the report inside a try whose catch takes any
   exception and does nothing (aud-a28u's shape: a provider that throws has nowhere to report its own failure). Every
   report in the file goes through it: `Write`'s two, the consumer's, `SetLevel`'s (which still rethrows the store's
   exception), `Dispose`'s and the legacy import's. The consumer counts a failed entry dropped, reports it and takes
   the next one; `Write` counts an entry a closed queue refuses. New public `DroppedEntryCount` (`Interlocked`): the
   entries that passed the level and were not written - the one counter a health surface or a later line can show.
   `MinimumLevel` holds its watcher check in a try; a failure there serves the level in effect, the watcher's own
   policy for a failed read.
2. AuditService `GetString` reads through new private static `TextOf`: a string is itself and null is null; anything
   else is its `ToString()` inside a try, and a throw is the marker text "(unreadable <TypeName>)" - the type's name,
   never the value. JsonlLogService `Write` routes by `TextField(evt, "eventType")`, string-only as the catch names a
   record; a non-string eventType, which no caller passes, is filed as audit - as the event log's parser files one.
3. WriteToFile appends through new private `AppendLine`: an unbuffered `FileStream` (`FileMode.OpenOrCreate`,
   `FileAccess.ReadWrite`, the same sharing, buffer size 1 - nothing held back that a later flush could append after a
   truncation), its length taken at the end before writing (`start`), and the whole line - UTF-8 with
   `Environment.NewLine`, the bytes `StreamWriter.WriteLine` wrote - in one write, through an internal seam
   `WriteLineBytes` (default `file.Write(bytes)`). On a failure the file is truncated back to `start` (`SetLength`, best
   effort; a truncation that fails is reported through a guarded warning) and the exception goes on to the catch, which
   reports the drop as now. Before writing, a file whose last byte is not a newline - the tail a failed truncation or a
   dead process left - gets one first, so the record starts on its own line; the repair is reported through a guarded
   warning naming the stream and path.
4. The catch's report adds the record's `operationId`, `user`, `ip`, `target`, `ticket` and `result`, each through
   `TextField`: "Failed to write a JSONL {Stream} event (action {Action}, category {Category}, operation {OperationId},
   user {User}, ip {Ip}, target {Target}, ticket {Ticket}, result {Result}) to {Path}; the event was not written".
   Never the payload, never a raw `ToString`.
5. Comments after the mutation round, checked code-identical.

Tests.
- `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs` - its `CreateService` takes a logger; a capturing logger that
  throws once it has recorded, as aud-a28u's. The throwing-logger theory, by cause, since each path's failure is
  induced its own way (the aud-a28q tripwire's dictionary values reach nothing here): a detail factory that throws -
  `Write` returns and the entry is written without its detail; a closed queue - `Write` returns, the entry counted;
  an unreadable change token - `Write`, `IsEnabledFor` and `CurrentLevel` return; a store that refuses the level -
  `SetLevel` throws the store's exception, not the logger's; a legacy level file that cannot be imported - the
  service is constructed. And the consumer: its file held with no sharing and the logger throwing - the entry dropped
  and counted, the consumer alive, the next entry written.
- `ExchangeAdminWeb.Tests/AuditWriterThrowFreeTests.cs`: a value whose ToString throws under each field WriteAuditEvent
  reads (category, action, user, ip, target, ticket, result) - no throw, the row written, the trace carrying the
  marker, no error; the writer handed an eventType whose ToString throws - no throw, written; a line that fails half
  written (the seam writes half and throws), on each stream, then two that write - the file parses line by line, as
  the event log reads it, to exactly the rows reported Written, the row before the failure intact; a tail a dead
  process left (a file ending mid-line) - the next row parses on its own line and the repair is reported; a row
  dropped with the trace file held too - its report names the operation id, user, ip, target, ticket and result,
  and not the payload's marker.

Mutations at the call site (md5 before and after, the named failing test, restored, md5 matched, touched, rebuilt,
green): M1 the consumer's report unguarded again (ProcessQueueAsync); M2 the raw `ToString` again (GetString); M3
the truncate-back removed (AppendLine's catch); M4 the identity fields dropped from the report; extras E1 Write's
detail-factory report unguarded again, E2 MinimumLevel's guard removed, E3 the tail repair removed, E4 the consumer's
drop not counted, E5 the writer's eventType read by ToString again.

Files: `Services/ExtendedLogService.cs`, `Services/AuditService.cs`, `Services/JsonlLogService.cs`,
`ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs`, `ExchangeAdminWeb.Tests/AuditWriterThrowFreeTests.cs`, this record.

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
