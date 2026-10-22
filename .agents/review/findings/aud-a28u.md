# aud-a28u: The "AuditWritten" trace step reads Success even when the writer dropped the record

**Severity**: LOW/MEDIUM - audit honesty. Since aud-a28q the JSONL writer swallows a record it cannot
write (an unserialisable value, an IO failure) and logs one error; `AuditService.WriteAuditEvent` then
still runs its "AuditWritten" trace step as Success and its usage-telemetry call, because the writer
returns no outcome. The operation trace therefore says an audit row was written when none was, and a
reviewer reconciling trace to audit finds a step with no row.
**Status**: Fixed on branch; not merged, not pushed; awaiting review
**Branch**: `worktree-agent-a906aa98bce748ee5`
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a28q/psl-7/a26o hand-back (`6fc94167`), with
codex attr-batch-11's MEDIUM folded in (`c3bc0c24`; the "Folded in" section at the end of this record). One commit
`fix(audit): ... (aud-a28u)`, first of two on branch `worktree-agent-a906aa98bce748ee5` (from `6fc94167`; aud-a26r
follows). Shared infrastructure - the writer every audit and trace call ends in - so a base app version change; the
supervisor owns the bump. Evidence re-read at `6fc94167`:

- `Services/JsonlLogService.cs`: `Write` (35-42), `WriteAudit` (44) and `WriteTrace` (46) return void and all end in
  `WriteToFile` (72-95), whose catch (89-94) logs one error and returns: nothing tells a caller the record was dropped.
  That catch's `_logger.LogError(...)` (91-93) is unguarded, so a logger that throws takes its exception out of the
  write (the fold-in).
- `Services/AuditService.cs` `WriteAuditEvent` (463-513): `_log.Write(evt)` (482), then the "AuditWritten" step with
  `result: "Success"` whatever became of the row (483-491), the implicit scope completed on the audit's own result
  (493-495), and `_usage?.RecordAction(...)` (509-512), whose comment (502-506) counts the action "whether or not its
  row could be written".
- `Services/OperationTraceService.cs` `Write` (141-177) ends in `_log.WriteTrace(evt)` (176): the trace writer is the
  same `WriteToFile`, with no recovery path of its own, so the one guard in that catch is the trace writer's as well.
- `ExchangeAdminWeb.Tests/UsageTelemetryServiceTests.cs` `AuditService_HookIsLastInWriteAuditEvent` (466-483), a source
  guard: `_log.Write(evt);` present, `_usage?.RecordAction(` after it and the method's last statement.

**Rule.** The trace says whether the audit row was written, and nothing about writing it - nor about reporting that it
could not be written - throws into the caller. A row the writer drops is a Failed "AuditWritten" step that says why,
and is not counted as a usage action; the operation's own outcome is the operation's (Constitution, Auditing And
Tracing: "Audit failure must not make an already-completed operation appear to have failed. Log audit failures
separately").

1. New public enum `JsonlWriteOutcome` beside the writer: `Written`, `NotSerializable`, `FileWriteFailed`. `Write`,
   `WriteAudit` and `WriteTrace` return it. `WriteToFile` keeps the serialised line in a local declared before the try
   and returns `Written` once the line is written and its stream closed. The catch decides by how far the write got,
   never by the exception's type: no line yet (the null filter or the serialiser failed) is `NotSerializable`; a line
   (choosing, rotating, opening, writing or flushing the file failed) is `FileWriteFailed`.
2. Fold-in: the catch's `_logger.LogError(...)` moves inside a nested try whose catch takes any exception and does
   nothing - a logger that throws has nowhere to report its own failure - and the write returns its outcome either
   way. The message and its fields are unchanged.
3. `WriteAuditEvent`: `outcome = _log.Write(evt);` (the source guard's literal stays). The step is "Success" when
   `Written`, else "Failed" with `details.reason` "the audit row was not written: the record could not be serialised"
   or "...: the log file could not be written"; its three audit fields are unchanged. The implicit scope still
   completes on the audit's result. The telemetry call runs only when `Written` - an `if` around it, still the method's
   last statement - because a usage count is a claim that an audited action happened. Its comment is corrected in the
   comments commit.
4. `OperationTraceService.Write` is unchanged: a trace record the writer drops is the writer's logged error, and there
   is no step about a step.
5. Comments after the mutation round, checked code-identical.

Tests, `ExchangeAdminWeb.Tests/AuditWriterThrowFreeTests.cs` (the real writer over its temp folder and the capturing
logger; for the count, a telemetry sink with `Enabled` and `Enqueue` overridden over a temp config store):
- `TheWriter_SaysWhetherItWroteTheRecord`: the audit and the trace stream, each written / handed a value it cannot
  serialise / its file held open by another handle with no sharing - `Written` / `NotSerializable` /
  `FileWriteFailed`, and a line only when written;
- `ARecordTheWriterDrops_IsAFailedAuditWrittenStep_IsNotCounted_AndTheOperationKeepsItsOutcome`: through
  `LogModuleAction`, an unserialisable value in `extra` and, separately, the audit file held - no throw, no audit line,
  the "AuditWritten" step Failed with its reason, the operation's complete event Success, no action counted, one error;
- `ARecordTheWriterWrites_IsASuccessfulAuditWrittenStep_AndIsCounted`: one line, the step Success with no reason, one
  action counted, no error;
- fold-in: `EveryPublicAuditAndTraceMethod_GivenALoggerThatThrows_StillReturns` - the aud-a28q tripwire's theory over
  the same methods, the capturing logger throwing once it has recorded - and
  `TheWriter_GivenALoggerThatThrows_StillReturnsItsOutcome` (audit and trace): no throw, `NotSerializable`, no line,
  and the next record written.

Mutations at the call site (md5 before and after, the named failing test, restored, md5 matched, touched, rebuilt,
green): M1 the writer's outcome ignored again in `WriteAuditEvent`; M2 the catch's logging without the nested guard
again; extras E1 the telemetry call unguarded again, E2 the catch's verdict fixed at `FileWriteFailed`.

Files: `Services/JsonlLogService.cs`, `Services/AuditService.cs`, `ExchangeAdminWeb.Tests/AuditWriterThrowFreeTests.cs`,
this record.

## Fix

- `Services/JsonlLogService.cs`: new public enum `JsonlWriteOutcome` (`Written`, `NotSerializable`, `FileWriteFailed`)
  after the class; `Write`, `WriteAudit` and `WriteTrace` return it. `WriteToFile` declares `string? json = null` before
  its try and returns `Written` after the lock block - the line written and its stream closed. The catch takes
  `json is null ? NotSerializable : FileWriteFailed`, makes its unchanged `_logger.LogError(...)` inside a nested
  `try { ... } catch (Exception) { }` (the fold-in), and returns that outcome.
- `Services/AuditService.cs` `WriteAuditEvent`: `JsonlWriteOutcome outcome;` before the try and
  `outcome = _log.Write(evt);`; the step's `result: outcome == JsonlWriteOutcome.Written ? "Success" : "Failed"`, its
  details from new private static `AuditWrittenDetails(evt, outcome)` - the three audit fields, and `reason` when the
  row was not written; the telemetry call under `if (outcome == JsonlWriteOutcome.Written)`, still the last statement.

Tests: `ExchangeAdminWeb.Tests/AuditWriterThrowFreeTests.cs` +31 - `TheWriter_SaysWhetherItWroteTheRecord` (6),
`ARecordTheWriterDrops_IsAFailedAuditWrittenStep_IsNotCounted_AndTheOperationKeepsItsOutcome` (2),
`ARecordTheWriterWrites_IsASuccessfulAuditWrittenStep_AndIsCounted`, and the fold-in's
`EveryPublicAuditAndTraceMethod_GivenALoggerThatThrows_StillReturns` (18, the tripwire's methods) and
`TheWriter_GivenALoggerThatThrows_StillReturnsItsOutcome` (4). The tripwire's call moved into a helper both theories
use, its assertions unchanged; new `CapturingLogger.Throws`, a `CountingTelemetry` sink and `HoldTheFile`; the
`Write` helper returns the outcome.

Caller-visible change, by design: `JsonlLogService.Write`, `WriteAudit` and `WriteTrace` return a `JsonlWriteOutcome`;
a caller that ignores it compiles and behaves as before. A row the writer drops leaves its "AuditWritten" trace step
Failed, `details.reason` saying why, and is not counted as a usage action; the operation's own trace result and the
caller's outcome are unchanged. A logger that throws while the writer reports a dropped record no longer takes its
exception out of the write. A record that writes is unchanged.

## Guard proof

No separate pre-fix reproduction: M1 is `WriteAuditEvent` as at `6fc94167` (no outcome to read - the step Success and
the action counted whatever became of the row) and M2 the catch as at `6fc94167` (its report unguarded), each put back
at its call site; each fails the tests named.

Session-local driver (`mutate.ps1` and `round-a28u.ps1`, session scratchpad `a28u-a26r-work`, log
`a28u-mutations.log`): the file's exact bytes saved, one literal replacement that must match exactly once, a Release
build of the solution with 0 errors required, the filter
`FullyQualifiedName~AuditWriterThrowFreeTests|FullyQualifiedName~AuditServiceTests|FullyQualifiedName~UsageTelemetryServiceTests`
(105 tests) run with a TRX logger, the exact bytes restored in a `finally`, the MD5 re-checked and the mtime advanced
so MSBuild rebuilds. Every mutant built with 0 errors; each restore matched its original MD5. After the round, a
rebuild on the restored bytes and the filter: 105/105 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 the writer's outcome ignored again (`_log.Write(evt); outcome = JsonlWriteOutcome.Written;`) | `AuditService.cs`, `DFF50B115BE7BB56F9A98F8C51775E35` | `E68ACC76448A577698F4C76E309E1973` | 2 of 105: `ARecordTheWriterDrops_IsAFailedAuditWrittenStep_IsNotCounted_AndTheOperationKeepsItsOutcome`, both causes - the step Success and the action counted |
| M2 (fold-in) the catch's report without its nested guard again | `JsonlLogService.cs`, `9B4D3FDC39F1DEFFFDBB219913C1033F` | `F0AE86B6D7CBDE35E43A25E964150B64` | 19 of 105: `EveryPublicAuditAndTraceMethod_GivenALoggerThatThrows_StillReturns` for the fifteen methods that carry a value (twelve audit methods, `BeginOperation`, `BeginRootOperation`, `Step`) and all four `TheWriter_GivenALoggerThatThrows_StillReturnsItsOutcome` cases - the provider's exception escapes the call |
| extra E1 the telemetry call unguarded again | `AuditService.cs`, `DFF50B115BE7BB56F9A98F8C51775E35` | `8383736B9EDE423319E60DB649B1DC06` | 2 of 105: the same theory's two causes - an action counted for a row the log does not have |
| extra E2 the catch's verdict fixed at `FileWriteFailed` | `JsonlLogService.cs`, `9B4D3FDC39F1DEFFFDBB219913C1033F` | `6C5A29BF76BC9D734259F0245D606B88` | 5 of 105: `TheWriter_SaysWhetherItWroteTheRecord` and `TheWriter_GivenALoggerThatThrows_StillReturnsItsOutcome` for the unserialisable value on both streams, and the drop theory's unserialisable cause (its reason names the file) |

Unchanged through the round: `AuditWriterThrowFreeTests.cs` `5966420409A15944CF952123E08F2F14`. The code comments are
written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count - none
  in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,716 passed / 3 skipped (the three known AD skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`)
  / 0 failed, 5,719 total, 5m23s; nothing hung, no Sequence file. Baseline at `6fc94167`: 5,685 / 3 / 0, 5,688 total,
  5m16s; the thirty-one new cases are this record's.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No AD, EXO, Graph, DHCP or IIS call, no real module, no Delinea, SMTP, push or deployment: a real writer over a temp
folder, a counting telemetry sink. No coder self-review verdict is claimed.

## Codex review

Pending.

## Folded in: the recovery logging in the writer's catch can itself throw (codex, attr-batch-11, 2026-10-22) - MEDIUM

`Services/JsonlLogService.cs` `WriteToFile` around line 91: after catching a serialisation or file-write
failure, the catch calls `_logger.LogError(...)`; a throwing `ILogger` provider or formatter path throws
out of the audit or trace write after the operation completed, reviving the aud-a28q class on the
recovery path. Guard the catch-block logging with a nested try/catch (or a non-throwing fallback) so no
failure while reporting a dropped record escapes `WriteToFile`; the same for the trace writer's recovery
path. Test: a logger whose `Log` throws does not make the write throw (the aud-a28q tripwire gains a
throwing-logger variant). Mutation at the call site: log without the nested guard again.
