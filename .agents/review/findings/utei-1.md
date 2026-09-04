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

TBD - fix commit will fill this in.

## Files changed

TBD

## Guard proof

TBD

## Coder dispute (if any)

None. Verified against the cited lines.

## Known gaps

TBD

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
  (owner dispatch: goal "finish the in-flight work"; codereview over the telemetry range)
Harness: codex-cli 0.153.2, `codex exec -s read-only`, generation half over
`e33b201~1..dea899b`, verdict `findings` (4), capability_ok true, both SHAs echoed.
Dispatched 2026-09-04; envelope at `.agents/review/ute-impl.result.json`.
