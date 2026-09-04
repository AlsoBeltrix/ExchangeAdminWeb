# utei-1: Background job pump inherits the submitting browser's usage session

**Severity**: MEDIUM - usage action rows written by the bulk job pump carry the
session id of whichever browser circuit happened to start the pump, instead of null.
Once the pump is running, jobs submitted by OTHER operators drain on that same pump
and their audited actions are attributed to the first operator's session.
**Status**: Admitted
**Branch**: -
**Commit**: the commit that completes this record; read it from `git log -1 -- .agents/review/findings/utei-1.md`

## Evidence

`Services/UsageSessionCircuitHandler.cs:31-45` sets `UsageSession.Current` (an
`AsyncLocal`) for the duration of each inbound circuit activity.
`Services/Jobs/BulkJobService.cs:125-133` (`Enqueue`) is called from the page path and
calls `EnsurePump`; `Services/Jobs/BulkJobService.cs:260-272` starts the pump with
`_ = Task.Run(() => DrainQueueAsync(CancellationToken.None))` without suppressing
`ExecutionContext` flow. `Services/OperationTraceService.cs:22-27` already documents in
this repo that `AsyncLocal` flows across `Task.Run`. The pump is long-lived and drains
the FIFO queue for every operator. `Services/UsageTelemetryService.cs:116-123`
(`RecordAction`) reads `UsageSession.Current.Value` for every audited action.

## Predicted observable failure

Operator A submits a ConferenceRooms or MessageTrace bulk job from the browser. The
pump starts inside A's circuit activity and captures A's session id. Operator B
submits a job while that pump is still draining: B's per-row audits produce usage
rows stamped with A's session. The Usage summary reports A as having acted in a
module A never touched, and the "actions per open" ratio for A's visit is inflated.
Even with a single operator, a job that finishes long after the browser tab closed
is still counted inside that visit.

## What

`RecordAction`'s session is meant to be null for anything that did not originate in a
browser circuit. `Task.Run` propagates the ambient `AsyncLocal`, so the pump task -
and everything it awaits - inherits the session of whoever started it, permanently.

## Approach

`EnsurePump` now starts the pump task inside `ExecutionContext.SuppressFlow()`, so the
task captures no ambient state at all rather than a snapshot of whichever circuit
happened to enqueue first.

The fix is at the pump start rather than at the reader, and it suppresses the whole
execution context rather than clearing `UsageSession.Current` alone, for two reasons.
First, the leak is a property of how the pump is started, not of the usage session:
the pump is one long-lived loop shared by every operator, so ANY ambient value it
inherits from its accidental first caller is wrong for every job after the first.
Second, the repo already met this exact leak once - `OperationTraceService.cs:22-27`
documents that `AsyncLocal` flows across `Task.Run` and works around it downstream
with `BeginRootOperation`. Fixing the cause makes that workaround belt-and-braces
instead of load-bearing, and stops the next `AsyncLocal` anyone adds from repeating
the finding.

`SuppressFlow` throws if flow is already suppressed, so the call is guarded by
`ExecutionContext.IsFlowSuppressed()`. Nothing in the pump depends on flowed state:
`RunJobAsync` opens its own DI scope per job (`using var scope =
_scopeFactory.CreateScope()`), and the cancellation token is passed explicitly.

## Files changed

- `Services/Jobs/BulkJobService.cs` - `EnsurePump` starts the pump task with
  execution-context flow suppressed, with the reasoning recorded on the method.
- `ExchangeAdminWeb.Tests/BulkJobServiceTests.cs` - one test; `FakeProcessor` records
  the ambient session it sees per row.

## Guard proof

Test: `Pump_DoesNotInheritTheEnqueueingCircuitsUsageSession`
(`ExchangeAdminWeb.Tests/BulkJobServiceTests.cs`). It sets
`UsageSession.Current.Value` to a stand-in circuit id, submits a two-row job through
`Enqueue` (NOT `DrainQueueAsync` - every other test in the file calls the drain
directly and so never exercises how the pump task is started), waits for the job to
reach `Completed`, and asserts both rows saw a null session. It also asserts the
enqueueing flow still has its own session, so the fix cannot be satisfied by clearing
the caller's value.

Mutation probe (non-vacuity): with the `SuppressFlow` guard replaced by the pre-fix
`_ = Task.Run(() => DrainQueueAsync(CancellationToken.None));`, exactly this test
fails and the other 19 in the class pass. Restored from a copy outside the working
tree, not by `git checkout`, and the file was re-stamped
(`(Get-Item $path).LastWriteTime = Get-Date`) so MSBuild actually rebuilt - a
`Copy-Item` restore carries the backup's old timestamp and will otherwise leave the
mutated assembly in place.

## Coder dispute (if any)

None. Verified against the cited lines.

## Known gaps

Suppression happens where the pump is started. A future background loop started
elsewhere with a bare `Task.Run` from a circuit path would reintroduce the same class
of leak; nothing enforces the rule repo-wide. The narrower alternative - clearing
`UsageSession.Current` inside the task body - would have had the same gap and covered
less.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
  (owner dispatch: goal "finish the in-flight work"; codereview over the telemetry range)
Harness: codex-cli 0.153.2, `codex exec -s read-only`, generation half over
`e33b201~1..dea899b`, verdict `findings` (4), capability_ok true, both SHAs echoed.
Dispatched 2026-09-04; envelope at `.agents/review/ute-impl.result.json`.
