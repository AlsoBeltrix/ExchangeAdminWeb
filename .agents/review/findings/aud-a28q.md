# aud-a28q: The audit and trace writers are not throw-free

**Severity**: LOW/MEDIUM - every "never throws" helper added since aud-a28f (`LogAllowlistSaveRefused`,
`LogSaveRefused`, `LogUndoRefused`, `LogUndoNotRun`, the aud-a28h settings helpers) catches around the
audit call, but the writers themselves can throw before their own try: `JsonlLogService.WriteToFile`
serialises the record outside its try, so a value `System.Text.Json` cannot serialise throws out of
the audit call; `OperationTraceService.SamName(null)` throws on a null actor. A handler that calls an
audit or trace method directly (the success paths do) can therefore end with an exception instead of
its outcome, and the record is lost. The helpers' catches hide it on the refusal paths only.
**Status**: Fixed on branch; not merged, not pushed; awaiting review
**Branch**: `worktree-agent-aa41f72251493711c`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a26l/a26m/a28o agent, 2026-10-22, candidate 4 of its hand-back)

## Evidence

At `fee8a58b`: `Services/JsonlLogService.cs` `WriteToFile` (serialisation before the try);
`Services/OperationTraceService.cs` `SamName` (null actor). `git grep -n "LogAudit\|LogSettingsChange\|LogModuleAction" Components/Pages/*.razor Services/*.cs`
for the direct callers on success paths.

## Approach (to be written by the implementing agent before code)

- `WriteToFile`: serialise inside the try; on any failure log one error (with the record's action and
  category, never its payload) and return - an audit write never throws into a handler. The same for
  the trace writer; `SamName` tolerates null (returns "unknown", as the aud-a28g lookup refusal does).
- A tripwire test: every public method of `AuditService` and `OperationTraceService` completes without
  throwing when handed a record containing an unserialisable value and a null actor, and logs one
  error. Mutation at the call site: serialise outside the try again; `SamName` throws on null again.
- Keep the Constitution's rule: an audit failure is logged and never reported as a failed save (aud-a28f),
  and the operation's own outcome is unaffected.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a26l/a26m/a28o hand-back (`8f831a0d`).
One commit `fix(audit): ... (aud-a28q)`, first of three on branch `worktree-agent-aa41f72251493711c` (from
`89096ac9`; psl-7 and aud-a26o follow). Shared infrastructure - the audit and trace writers every module ends in -
so a base app version change; the supervisor owns the bump. Evidence re-read at `89096ac9`:

- `Services/JsonlLogService.cs` `WriteToFile` 66-86: the null filter and `JsonSerializer.Serialize` (68-69) run
  before the try (72), whose catch (82-85) covers only the file write. `Write` (35-42), `WriteAudit` (44) and
  `WriteTrace` (46) all end in it, and the only callers are `AuditService.WriteAuditEvent` (482) and
  `OperationTraceService.Write` (176).
- `Services/OperationTraceService.cs` `SamName` 205-206 dereferences its argument; the private `BeginOperation`
  (49-77), behind both public overloads (29-47), calls it on `actor` (67), so a null actor throws
  `NullReferenceException` out of `BeginOperation` before the operation exists.
- `Services/AuditService.cs` `SamName` 523-524 the same, on `performedBy`, in all thirteen public methods (63, 95,
  123, 154, 185, 213, 240, 270, 304, 343, 378, 413, 444).

**Rule.** An audit or trace call never throws into its caller. Whatever stops a record being written - a value the
serialiser cannot write, a null actor, the file - is logged once by the writer, naming the record's action and
category and never its payload, and the call returns; the operation's outcome is its caller's (Constitution,
Auditing And Tracing: "Audit failure must not make an already-completed operation appear to have failed. Log audit
failures separately"; aud-a28f).

1. `WriteToFile`: the filter and the serialisation move inside the try. The catch logs one error with the
   exception - "Failed to write a JSONL {Stream} event (action {Action}, category {Category}) to {Path}; the event
   was not written" - and returns. Action is the record's `action`; category its `category`, or a trace event's
   `module`, which is where a trace files it; each is taken only when it is a string, so no other field and no
   non-string's ToString reaches the application log. The path stays "(unresolved)" when the serialisation failed
   before a file was chosen. Nothing of that record is written, and the next record is written as normal.
2. `OperationTraceService.SamName` and `AuditService.SamName` take `string?`: null is "unknown", the actor
   `WriteStandaloneOperation` (112) and `WriteAuditEvent`'s fallback (471) already use. Every other identity reads
   as now.
3. `WriteAuditEvent`'s code is unchanged: its "AuditWritten" trace step and the usage-telemetry call already run
   after a write the writer swallowed (an IO failure). Its comment that the audit row "is already written" (502-507)
   becomes untrue for that case and is corrected in the comments commit.
4. Comments after the mutation round, checked code-identical.

Tests. New `ExchangeAdminWeb.Tests/AuditWriterThrowFreeTests.cs`, a real `JsonlLogService` over a temp folder with
a capturing logger, no network:
- the tripwire, a theory over every public method of `AuditService`, `OperationTraceService` and its
  `OperationScope`, found by reflection so a method added later is covered: each called with a null actor and, in
  every dictionary parameter, a value `System.Text.Json` cannot write (an object whose getter throws) beside a
  marker string. Each completes without throwing; the writer logs exactly one error, its Action and Category named
  and its text carrying neither the marker nor the value. A method with nothing that can carry a value
  (`LogConnectionPoolDrainFailed`; `OperationScope.Complete` and `Dispose`) completes and logs nothing. A parameter
  type the argument builder does not know fails the theory, so the author of the next method extends it;
- the writer itself: three values it cannot write (a throwing getter, a cycle, a `System.Type`), on an audit record
  and on a trace record - no throw, one error naming the action and the category (the trace's module), no line for
  that record, and the next record written;
- a null actor recorded as "unknown" in the audit record and in its trace operation.

Mutations at the call site (md5 before and after, the named failing test, restored, md5 matched, touched, rebuilt,
green): M1 the serialisation back before the try; M2 `OperationTraceService.SamName` throwing on null again; extra
E1 `AuditService.SamName` throwing on null again.

Files: `Services/JsonlLogService.cs`, `Services/OperationTraceService.cs`, `Services/AuditService.cs`, the new test
file, this record.

## Fix

- `Services/JsonlLogService.cs` `WriteToFile`: the null filter and `JsonSerializer.Serialize` are the try's first
  statements. The catch logs "Failed to write a JSONL {Stream} event (action {Action}, category {Category}) to
  {Path}; the event was not written" with the exception and returns; new private static `TextField(evt, key)`
  takes a field only when it is a string, so Action is the record's `action` and Category its `category` or, for a
  trace event, its `module`.
- `Services/OperationTraceService.cs` and `Services/AuditService.cs` `SamName(string? identity)`: null is "unknown".

Tests: new `ExchangeAdminWeb.Tests/AuditWriterThrowFreeTests.cs`, 26 cases -
`EveryPublicAuditAndTraceMethod_GivenANullActorAndAValueItCannotWrite_ReturnsAndLogsOneError` (18: the thirteen
audit methods, the trace service's three and the scope's two, found by reflection),
`TheTripwire_CoversTheMethodsThatExistToday` (so the theory cannot pass by finding nothing, and every parameter of
every method has an argument), `TheWriter_GivenARecordItCannotWrite_LogsOneErrorByActionAndCategory_WritesNoLine_AndWritesTheNext`
(6: a throwing getter, a cycle and a `System.Type`, each on an audit and a trace record) and
`ANullActor_IsRecordedAsUnknown_InTheAuditRecordAndInItsTrace`.

Caller-visible change, by design: an audit or trace call handed a value the serialiser cannot write, or a null
actor, returns instead of throwing; the record is not written and the application log has one error naming its
action and category. A null actor is recorded as "unknown". A record that writes is unchanged.

## Guard proof

No separate pre-fix reproduction: M1 is the writer as at `89096ac9` (serialisation before the try), M2 and E1 the
two `SamName`s as at `89096ac9`, each at its call site; each fails the tests named.

Session-local driver (`mutate.psm1` and `round-a28q.ps1`, session scratchpad `psl7-a28q-a26o-work`, log
`a28q-mutations.log`): the file's exact bytes saved, one literal replacement that must match exactly once, a
Release build with 0 errors required, the filter
`FullyQualifiedName~AuditWriterThrowFreeTests|FullyQualifiedName~AuditServiceTests|FullyQualifiedName~UsageTelemetryServiceTests`
(74 tests) run with a TRX logger, the exact bytes restored in a `finally`, the MD5 re-checked and the mtime
advanced so MSBuild rebuilds. Every mutant built with 0 errors; each restore matched its original MD5. After the
round, a rebuild on the restored bytes and the filter: 74/74 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 the serialisation back before the try | `JsonlLogService.cs`, `C4FB0EA878AB891FFCCE5D8B3959F07C` | `41B0054A84678F0A04413EC8D95240D4` | 21 of 74: the tripwire for every method that carries a value (the thirteen audit methods, `Step`, `BeginOperation`, `BeginRootOperation`) and all six writer cases - the exception escapes the call |
| M2 `OperationTraceService.SamName` throwing on null again (`identity!.Contains(...)`) | `OperationTraceService.cs`, `E299A9B96148E2FC1DB31858F2509199` | `C87363F25126FF29E042D438D7099288` | 5 of 74: the tripwire's `BeginOperation`, `BeginRootOperation`, `OperationScope.Complete` and `OperationScope.Dispose` (their scope is begun with a null actor), and `ANullActor_IsRecordedAsUnknown_...`. The audit methods pass: their own `SamName` hands the trace "unknown" |
| extra E1 `AuditService.SamName` throwing on null again | `AuditService.cs`, `F8248F532B71884A9323336DBBCB96C2` | `EF34D83A16854F928F09F38B38DC82BD` | 14 of 74: the tripwire's thirteen audit methods (`LogConnectionPoolDrainFailed`, which carries no value, included) and `ANullActor_IsRecordedAsUnknown_...` |

Unchanged through the round: `AuditWriterThrowFreeTests.cs` `3E353EBB1EE762805F07E8FDACF94A36`. The code comments
are written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count - none
  in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,578 passed / 3 skipped (the three known AD skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`)
  / 0 failed, 5,581 total, 4m59s; nothing hung, no Sequence file. Baseline at `89096ac9`: 5,552 / 3 / 0, 5,555 total,
  5m14s; the twenty-six new cases are this record's.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No PowerShell source, AD module, directory, Delinea, SMTP, push or deployment. No coder self-review verdict is
claimed.
