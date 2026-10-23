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

## Fix

- `Services/ExtendedLogService.cs`: new private `ReportSafely(Action<ILogger>)` - the report in a try whose catch takes
  any exception. Every report goes through it: `SetLevel`'s (then `throw;` as before), `Write`'s two, `Dispose`'s, the
  consumer's and the legacy import's. New field `_droppedEntries` and public `DroppedEntryCount` (`Interlocked.Read`);
  the consumer's catch and `Write`'s closed-queue branch increment it before they report. `MinimumLevel` holds the
  watcher check and its reload in a try with an empty catch and returns the level in effect.
- `Services/AuditService.cs`: `GetString` reads through new private static `TextOf` - null and a string as they are,
  anything else its `ToString()` in a try, and "(unreadable <TypeName>)" when that throws.
- `Services/JsonlLogService.cs`: `Write` routes by `TextField(evt, "eventType")`. `WriteToFile` writes, inside its lock,
  through new private `AppendLine(logPath, json, stream)`: `new FileStream(logPath, FileMode.OpenOrCreate,
  FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete, bufferSize: 1)`; `start = file.Seek(0, SeekOrigin.End)`;
  `repair = start > 0 && !EndsWithNewline(file)` (the last byte read back); the line,
  `Encoding.UTF8.GetBytes((repair ? NewLine : "") + json + NewLine)`, handed to the internal seam `WriteLineBytes`
  (default `file.Write(line)`), whose failure runs `TruncateTo(file, start, ...)` - `SetLength` in a try, its own
  failure a warning - and `throw;`; after a repaired write, a warning naming the stream and path. The catch's error
  report carries `OperationId`, `User`, `Ip`, `Target`, `Ticket` and `Result`, each through `TextField`; it and both
  warnings go through a private `ReportSafely` of the same shape as ExtendedLogService's.

Tests: `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs` +6 - `ExtendedLogService_GivenALoggerThatThrows_StillReturns`
(four causes), `TheConsumer_GivenAnEntryItCannotWrite_AndALoggerThatThrows_DropsAndCountsIt_AndWritesTheNext`,
`SetLevel_WhenTheStoreRefusesIt_AndTheLoggerThrows_ThrowsTheStoresFailure`; `CreateService` takes a logger; new
`CapturingLogger` and `HoldTodaysLogFile`. `ExchangeAdminWeb.Tests/AuditWriterThrowFreeTests.cs` +13 -
`AValueWhoseToStringThrows_UnderAFieldTheAuditReads_IsMarkerText_AndTheRowIsWritten` (seven fields),
`TheWriter_GivenAnEventTypeWhoseToStringThrows_WritesTheRecord_AsAudit`,
`ALineThatFailsHalfWritten_IsTakenBack_SoTheFileHoldsExactlyTheRowsReportedWritten` (both streams),
`ATailADeadProcessLeft_IsEndedFirst_SoTheNextRowParsesOnItsOwnLine_AndTheRepairIsReported` (both streams),
`ADroppedRowsReport_NamesWhoDidWhat_WhenTheTraceIsDroppedToo`; helpers `Row`, `ReadAsTheEventLogDoes`, `TraceText`,
`LogFiles` (which `Lines` now uses), `ThrowingToString` and `CapturingLogger.Entries`.

Caller-visible change, by design: a logging provider that throws no longer takes its exception out of
`ExtendedLogService.Write`, `IsEnabledFor`, `CurrentLevel`, `Dispose` or its constructor, and no longer ends the
extended log's consumer; `SetLevel` still throws when the store refuses the level, with the store's exception. An entry
the consumer cannot write, or a closed queue refuses, is counted in `DroppedEntryCount`. A caller-supplied value whose
`ToString` throws is recorded in the trace and the step's details as "(unreadable <TypeName>)" and the audit row is
written. A record whose line fails part-way is taken back out of the file, and a file that ends mid-line is ended
before the next record, with a warning; each record the writer reports `Written` is then a line the event log parses.
A dropped record's report names its operation id, user, ip, target, ticket and result. The JSONL bytes of a record
that writes are unchanged.

## Guard proof

No separate pre-fix reproduction: M1, M2, E1 and E5 put each call site back as at `18530b58` (M1 keeps the new count
before the report, as the fix has it); M3, M4, E2, E3 and E4 each take out one new guard. Each fails the tests named.

Session-local driver (session scratchpad `mutate/mutate.psm1` and `mutate/round-a28z.ps1`, log
`mutate/a28z-mutations.log`): the file's exact bytes saved, one literal replacement that must match exactly once, a
Release build of the solution with 0 errors required, the filter
`FullyQualifiedName~AuditWriterThrowFreeTests|FullyQualifiedName~AuditServiceTests|FullyQualifiedName~UsageTelemetryServiceTests|FullyQualifiedName~ExtendedLogServiceTests|FullyQualifiedName~AuditExtraChannelTests|FullyQualifiedName~AuditCategoryFilingTests|FullyQualifiedName~EventLog`
(169 tests) run with a TRX logger, the exact bytes restored in a `finally`, the MD5 re-checked and the mtime advanced
so MSBuild rebuilds. Every mutant built with 0 errors; each restore matched its original MD5. After the round, a
rebuild on the restored bytes and the filter: 169/169 green. The round was run twice with the same results: first on
the tests as written, then - after `dotnet format` re-indented the four `case` blocks of the new theory, a
whitespace-only change (`git diff -w` empty) - on this commit's bytes, which the table gives.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 the consumer's report unguarded again (`_logger.LogError(ex, "Failed to write extended log entry");`) | `ExtendedLogService.cs`, `C41261D66DBFE976A6ECCEBDC54F13F1` | `A5C5FF213024B81E16743A96F258933B` | 1 of 169: `TheConsumer_GivenAnEntryItCannotWrite_AndALoggerThatThrows_DropsAndCountsIt_AndWritesTheNext` - the drop is counted, then the report's exception faults the consumer and the next entry is never written (the file stays empty) |
| M2 the raw `ToString` again (`value?.ToString()` in GetString) | `AuditService.cs`, `28DE23A4840ECB241DEBA386E928001C` | `6E56497E094CC8E9518878F82033DBF8` | 7 of 169: `AValueWhoseToStringThrows_UnderAFieldTheAuditReads_IsMarkerText_AndTheRowIsWritten`, all seven fields - the value's exception escapes `LogModuleAction` |
| M3 the truncate-back removed (`TruncateTo(file, start, logPath, stream);` dropped from AppendLine's catch) | `JsonlLogService.cs`, `DDA5A7DBC0246CB4277D6FB2E2F974CB` | `81F4D9E529FDEE07F8FC1F4457FE7E88` | 2 of 169: `ALineThatFailsHalfWritten_IsTakenBack_SoTheFileHoldsExactlyTheRowsReportedWritten`, both streams - the half line stays, one line the event log cannot parse (the tail repair still starts the next row on its own line) |
| M4 the identity fields dropped from the report (aud-a28u's message and arguments again) | `JsonlLogService.cs`, `DDA5A7DBC0246CB4277D6FB2E2F974CB` | `0436F6BEF89D687F7F69A9FBA5F26650` | 1 of 169: `ADroppedRowsReport_NamesWhoDidWhat_WhenTheTraceIsDroppedToo` - the report carries no operation id |
| extra E1 Write's detail-factory report unguarded again | `ExtendedLogService.cs`, `C41261D66DBFE976A6ECCEBDC54F13F1` | `FD109D51B6F585C4E53D054E42EDB0FC` | 1 of 169: `ExtendedLogService_GivenALoggerThatThrows_StillReturns(cause: "a detail factory that throws")` |
| extra E2 MinimumLevel's guard removed | `ExtendedLogService.cs`, `C41261D66DBFE976A6ECCEBDC54F13F1` | `7A187EAEB0ADD7AED90E6ED41EC2F37E` | 1 of 169: `ExtendedLogService_GivenALoggerThatThrows_StillReturns(cause: "an unreadable change token")` - the provider's exception escapes `Write` through the watcher |
| extra E3 the tail repair removed (`var repair = false;`) | `JsonlLogService.cs`, `DDA5A7DBC0246CB4277D6FB2E2F974CB` | `6A314E45615DFAA0E4E891E83B270D0E` | 2 of 169: `ATailADeadProcessLeft_IsEndedFirst_SoTheNextRowParsesOnItsOwnLine_AndTheRepairIsReported`, both streams - the next row is appended onto the cut one |
| extra E4 the consumer's drop not counted | `ExtendedLogService.cs`, `C41261D66DBFE976A6ECCEBDC54F13F1` | `C931F796FDE7E4E6CC2DA4060F13CA4E` | 1 of 169: `TheConsumer_GivenAnEntryItCannotWrite_AndALoggerThatThrows_DropsAndCountsIt_AndWritesTheNext` - "the consumer never counted the entry it could not write" |
| extra E5 the writer's eventType by `ToString` again | `JsonlLogService.cs`, `DDA5A7DBC0246CB4277D6FB2E2F974CB` | `CA88237F41161FF473E8B93872991982` | 1 of 169: `TheWriter_GivenAnEventTypeWhoseToStringThrows_WritesTheRecord_AsAudit` |

Unchanged through the round: `ExtendedLogServiceTests.cs` `D9DB9CCCC5E8130BBBE85B3ECF300935`,
`AuditWriterThrowFreeTests.cs` `274A15772A06889E9667EEDD1311B26A`. The code comments are written after the round, in
their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count (36
  NU1903, 4 NU1902, 12 CS8604, 5 CS8602, 3 CS0649, 1 CS0414) - none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,858 passed / 3 skipped (the three known AD skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`)
  / 0 failed, 5,861 total, 6m51s; nothing hung, no Sequence file. The nineteen new cases are this record's (the base
  was not run separately: 5,842 expected at `18530b58`).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0 (after the re-indent the guard proof
  names). `git diff --check HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No AD, EXO, Graph, DHCP or IIS call, no real module, no Delinea, SMTP, push or deployment: a real writer and a real
extended log over temp folders, a substitute config store where a store failure is the cause. No coder self-review
verdict is claimed.

## Part 5 - plan (approved by the owner's EVERYTHING go)

Folded in from aud-a23l part 3 by the supervisor (`3a948129`; the last "Folded in" section below), under the owner's
2026-10-21 go ("fix all the bugs ... EVERYTHING"). aud-a28z and aud-a26u had landed on this branch when it arrived, so
it is its own commit after cdt-41 rather than in the aud-a28z commit. Evidence at `18530b58` (`b16898dc` is its
ancestor, so the folded section's line numbers hold):

1. `Services/AuditService.cs` `LogMigrationCheck` (111-136) files `["result"] = status == "Eligible" ? "Success" :
   "Ineligible"` (127). Its callers are `Components/Pages/Migration.razor` 1989-1990 (`singleResult.Status.ToString()`:
   Eligible, Ineligible or, since aud-a23h, Undetermined) and the catch's 2007-2008 (`"Error"`), so a check that could
   not decide and a check that threw are both filed as an ineligibility.
2. `Components/Pages/AdminEventLog.razor` 91-96: the Result filter offers All, Success, Failed and Ineligible;
   `ApplyFilters` (879-889) compares each entry's Result with the choice; the badge (264) shows any result but Success
   and Failed as a warning.

**Rule.** An eligibility check's result says what the check found - Success for eligible, Ineligible for ineligible,
Undetermined for anything else - so a check that could not decide, or threw, is never filed as a verdict against the
mailbox, and the event log can filter for it.

1. `LogMigrationCheck`: `["result"] = status switch { "Eligible" => "Success", "Ineligible" => "Ineligible", _ =>
   "Undetermined" }`. The `status` field keeps the caller's word ("Undetermined", "Error"). The usage count's success
   flag is unchanged: any result but "Failed" counts as a success, Ineligible and Undetermined alike.
2. `AdminEventLog.razor`: `<option value="Undetermined">Undetermined</option>` after Ineligible. NotYetConverted in
   ClickGateRegistry, so no key moves.
3. `Components/Pages/Migration.razor` is not touched (another agent's file): both its calls already pass the status as
   text.

Tests, `ExchangeAdminWeb.Tests/EventLogSectionsTargetTests.cs` (its real audit writer, and the page's own ParseAuditLine
and ApplyFilters by reflection): for "Undetermined" and "Error", the check's row carries result "Undetermined", and the
page's Result filter finds it under Undetermined and not under Ineligible; and the Result select offers Undetermined - a
source tripwire over the page's markup, comments blanked, as the undo panel's close buttons are pinned.

Mutations at the call site (as the other rows): M5 the two-way ternary again; extra E6 the option removed.

Files: `Services/AuditService.cs`, `Components/Pages/AdminEventLog.razor`,
`ExchangeAdminWeb.Tests/EventLogSectionsTargetTests.cs`, this record. Versions: no new bump - AuditService is shared (the
base app version this record already makes due) and the filter is the Admin Event Log's (its module version, due from
aud-a26u).

## Part 5 - fix and guard proof

- `Services/AuditService.cs` `LogMigrationCheck`: `["result"] = status switch { "Eligible" => "Success", "Ineligible" =>
  "Ineligible", _ => "Undetermined" }`.
- `Components/Pages/AdminEventLog.razor`: `<option value="Undetermined">Undetermined</option>` after Ineligible
  (1,604 lines -> 1,605; NotYetConverted, no key moves).
- `ExchangeAdminWeb.Tests/EventLogSectionsTargetTests.cs` +3:
  `AnEligibilityCheckThatDidNotDecide_IsFiledUndetermined_AndTheResultFilterFindsItThere` ("Undetermined", "Error") and
  `TheResultFilter_OffersUndetermined`; helper `ShownUnderResult`.

Caller-visible change, by design: a migration eligibility check that could not decide, or that threw, is filed with
result "Undetermined" (its `status` still the caller's word) instead of "Ineligible", shown with the warning badge as
before, and the Admin Event Log's Result filter offers Undetermined. Eligible and Ineligible checks read as before.

Same driver (session scratchpad `mutate/round-a28z5.ps1`, log `mutate/a28z5-mutations.log`), the filter
`FullyQualifiedName~EventLog|FullyQualifiedName~AuditServiceTests|FullyQualifiedName~AuditWriterThrowFreeTests|FullyQualifiedName~UsageTelemetryServiceTests|FullyQualifiedName~AuditCategoryFilingTests|FullyQualifiedName~Migration`
(717 tests). Both mutants built with 0 errors; each restore matched its original MD5; after the round, 717/717 green.
Each mutant is byte-identical to its file before this part (M5 to `AuditService.cs` at `1756aeb4`, E6 to
`AdminEventLog.razor` at `2904ec9b`), so each failure is also the part's reproduction.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M5 the two-way ternary again (`status == "Eligible" ? "Success" : "Ineligible"`) | `AuditService.cs`, `C887255FCD235FE71BB3C2F039F6E6C5` | `28DE23A4840ECB241DEBA386E928001C` | 2 of 717: `AnEligibilityCheckThatDidNotDecide_IsFiledUndetermined_AndTheResultFilterFindsItThere`, both statuses - filed "Ineligible" |
| extra E6 the Undetermined option removed | `AdminEventLog.razor`, `BFFA1A8F58E108547BCAD80871218075` | `2FA498D8922B42348E0097157BB83C1A` | 1 of 717: `TheResultFilter_OffersUndetermined` |

Unchanged through the round: `EventLogSectionsTargetTests.cs` `BF44A5227A58A74E42A6C77A4EFAD9C5`.

Gates, 2026-10-22, this part's commit's tree (cdt-41 below it): `dotnet build ExchangeAdminWeb.slnx -c Release
--no-incremental` 0 errors, 21 warnings (cdt-41's baseline), none in a touched file; `dotnet test ExchangeAdminWeb.slnx
--blame-hang --blame-hang-timeout 10m` (Debug) exit 0, 5,865 passed / 3 skipped (the three known AD skips) / 0 failed,
5,868 total, 6m09s, nothing hung - cdt-41's tree plus these three cases; `ClickGate*` 316/316 and
`ProgressRegistryTests` 35/35 alone (Release); `dotnet format ... --verify-no-changes --no-restore`, `git diff --check
HEAD` and `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

## Comments after the round

Written after every round - parts 1 to 4, part 5, and aud-a26u's - in their own commit, with aud-a26u's comments.
Checked by `check-comment-only.ps1` (session scratchpad): a file's code lines - every line neither blank nor a comment
(`//`, `///`, a one-line Razor `@* *@`) - identical in order between the commit before and the comments, line by line and
by the MD5 of the code lines; its negative control, part 5's own commit (`efbea61b` -> `d54da14e`), fails on both its
files. Across the six code files the comments add 84 lines and change 5 (`git diff --numstat`). This record's files:

- `Services/ExtendedLogService.cs` `C41261D66DBFE976A6ECCEBDC54F13F1` -> `20D8F3624774523348AF9A1FE3DB86FD`: 280 code
  lines, their MD5 `CB16B1DBC1350779D2AC0A53FB9847C8` on both sides; 23 lines added - the drop count and its summary,
  why each report is guarded and where it ran (the caller's thread, the consumer, the constructor, `SetLevel`'s rethrow),
  why the level check is guarded, the closed queue, and the dispose path the consumer's catch now leaves unreached.
- `Services/AuditService.cs` `C887255FCD235FE71BB3C2F039F6E6C5` (part 5's commit) -> `DA8CB3BE11FBAC38975231237E02D020`:
  456 code lines, MD5 `7EB321D8D1049DA098B537BDD5B2CB8D`; 7 added - why a field is read as guarded text, and
  `LogMigrationCheck`'s three results.
- `Services/JsonlLogService.cs` `DDA5A7DBC0246CB4277D6FB2E2F974CB` -> `AA75748C6EAB95C12A0450B9C1A787D8`: 193 code lines,
  MD5 `C493803BD8E8712CF2EB4CCC87CFDDB1`; 28 added, 3 changed - the string-only routing, the half-line defect and its
  take-back, the report's identity fields, the seam, `AppendLine`'s one-line rule with its one-writer assumption, the
  repair and the truncation, `ReportSafely`. Two comments the fix made untrue are corrected: aud-a28q's "never half a
  line in the JSONL", true until now only for a record that could not be serialised, is said to hold since this record;
  and `TextField`'s "never another field" - the report now names six more, string-only.

The tests carry their comments from their own commits.

## Known gaps

- `DroppedEntryCount` is shown on no page yet: it is the counter a health surface or a later line reads, and the page
  that shows the extended log level (`Components/Pages/AdminSettings.razor`) is outside this brief. Until one shows it,
  a drop the provider could not report is visible to code only.
- The count covers a failed write and a closed queue. A full queue's `DropOldest` - the bounded channel discarding its
  oldest entry when writes outpace the disk - is not counted: pre-existing, and the consumer no longer dies to cause it.
- `ConfigChangeWatcher.LogOnce` (`Services/Storage/ConfigChangeWatcher.cs` 120) stays unguarded for the other cache-hit
  readers - `ADAttributeEditorService` (183), `PermissionValidator` (47), `ProtectedPrincipalService` (211),
  `UsageTelemetryService` (79): with the change token unreadable and a provider that throws, `HasChangedSince` throws
  into their callers. ExtendedLogService guards its own call; one guard in `LogOnce` would cover them all. A candidate.
- The extended log's own file writes (`ProcessQueueAsync`, a `StreamWriter` over a `FileMode.Append` stream) can leave
  a half line as the JSONL writer could, and the Admin Event Log's `ParseExtendedLine` then drops the merged line.
  Diagnostics, not audit; a candidate.
- *Assumption, not probed*: one writer per JSONL file. The lock serialises this process's writes; a second process
  appending to the same file (two instances sharing one audit log root) between `start` and a truncation would lose its
  row, as interleaved appends could already corrupt each other.
- The truncation is best effort. When `SetLength` fails too, the half line stays and the next write's tail repair
  starts its own row on a new line; the half line is then one unparseable line, which the event log drops as before.
- Each write reads the file's last byte back, so the append handle opens for reading as well: a handle another process
  holds with write sharing but no read sharing would now refuse the open. This repo's readers (the event log, the
  extended log) share read, write and delete.
- The marker text names the value's type, not its value; the audit row itself carries what System.Text.Json writes for
  the value (its public properties - for a type with none, `{}`).
- Part 5: rows already written stay as written - an undetermined check logged before this build still reads
  "Ineligible", its `status` field ("Undetermined", "Error") the way to tell it apart. And the Migration page's catch
  still passes "Error" as the check's status (`Components/Pages/Migration.razor` 2007-2008, another agent's file); it is
  now filed Undetermined, as the result its page shows.

## Codex review

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

## Folded in: LogMigrationCheck files Undetermined under result "Ineligible" and the Admin Event Log filter has no Undetermined option (aud-a23l part 3, 2026-10-22) - LOW

(5) `Services/AuditService.cs` `LogMigrationCheck` (around line 127 at `b16898dc`):
`["result"] = status == "Eligible" ? "Success" : "Ineligible"` files every non-Eligible status - `Undetermined`
(aud-a23h) and the page catch's "Error" - under result "Ineligible". Fix: a third result for an undetermined
check (`status switch { "Eligible" => "Success", "Ineligible" => "Ineligible", _ => "Undetermined" }`), so
neither "Undetermined" nor "Error" is filed as an ineligibility. `Components/Pages/AdminEventLog.razor` lines
91-96 (the Result filter's options All, Success, Failed, Ineligible): add an "Undetermined" option; the badge
at 264 already shows any result other than Success and Failed as warning. Test: an Undetermined check's row
carries result "Undetermined" and the filter finds it. Mutation at the call site: the two-way ternary again.
