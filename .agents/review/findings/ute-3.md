# ute-3: The plan contradicted its own session-row decision

**Severity**: MEDIUM - the per-visit "opened and left without acting" metric, the
reason event rows were chosen over counters, was described as answerable while the
design could only approximate it by time window, so another operator's action could
mark a session as "acted".
**Status**: Verified (plan revised; docs-only - no code exists yet)
**Branch**: -
**Commit**: lands in the same commit as this record and the plan revision; read it
from `git log -1 -- .agents/review/findings/ute-3.md`.

## Evidence

`.agents/decisions.md` (2026-09-04 entry): "Each row carries a random session id ...
so 'opened and left without acting' can be answered per visit." `docs/UsageTelemetry-Plan.md`
at `4af217e`, section 1: "Action rows carry no session id ... A per-circuit id is not
reachable from a singleton without threading a new parameter through every page";
AC2: `SessionSummary` inferred "acted" from any action in an opened module "within the
session's open window (first to last open + 30 minutes)".

## Predicted observable failure

Two operators open Group Management in the same half hour; one acts. Both sessions
report as "acted". `SessionsWithNoAction` reads lower than the truth, and the
decision's promise is not kept.

## What

Plan defect: a stated constraint ("not reachable from a singleton") that the
framework does not impose. `CircuitHandler.CreateInboundActivityHandler` (since
.NET 8) wraps every inbound circuit activity in the same async flow, which is exactly
what an `AsyncLocal` ambient needs; the repo already registers a scoped
`CircuitHandler` (`ClientInfoCircuitHandler`, `Program.cs:207`).

## Approach

Plan revised: `UsageSession.Current` is a static `AsyncLocal<string?>`; a scoped
`UsageSessionCircuitHandler` sets it to the circuit's session id around each inbound
activity (try/finally restores the prior value); the singleton `RecordAction` reads it.
Page-originated actions carry the id; only actions raised outside a circuit
(background jobs, startup) are null. `SessionSummary` joins exactly on the column. A
new test pins that a NULL-session action in the same module and minute does NOT mark
another session as acted, and another pins the handler's set-and-restore.

## Files changed

- `docs/UsageTelemetry-Plan.md` - section 1 (settled item rewritten), AC2, AC3, AC4,
  section 4 (new row), section 5, section 6 (`UsageSession`,
  `UsageSessionCircuitHandler`, Program.cs), S2, section 8, manual check 1, section 10.

## Guard proof

Docs-only. The planned `RecordAction_CarriesTheAmbientSession`,
`CircuitHandler_SetsAndRestoresCurrent` and
`SessionSummary_FlagsSessionsWithNoAction_ByExactSessionJoin` tests bite at S1/S2.
`git diff --check` clean on the fold commit.

## Coder dispute (if any)

None.

## Known gaps

The ambient is set for inbound circuit activities. Component lifecycle methods during
the initial render are not inbound activities; the tracker passes the session
explicitly there, so no action originates in that window anyway (actions come from
event handlers).

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier
  (grade fallback; same dispatch as ute-1)
Harness: codex-cli 0.152.1. Reviewed SHA `4af217e`, base `2938db7`, capability_ok
true, verdict `acceptable_with_changes` (material change 3). Dispatched 2026-09-04;
envelope at `.agents/review/ute.result.json`.
