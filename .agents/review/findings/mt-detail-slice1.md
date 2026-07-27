# mt-detail-slice1: MessageTraceDetail models (slice 1)

**Severity**: n/a — slice-landing review of a new-model-only slice
**Status**: Verified
**Branch**: `master` (committed directly per repo policy; no per-finding branch)
**Commit**: `ade48c1`

## Evidence
`Models/LookupModels.cs:40-68` — new `MessageTraceDetailEvent` and
`MessageTraceDetail` classes.

## Predicted observable failure
Slice-landing review: no behavior change yet (models only, no consumer). The
review confirms the shapes are correct and coherent for the downstream slices
(2-9) of `docs/MessageTraceDetail-Plan.md` and introduce no build break or ASCII
violation. Guard: `dotnet build ExchangeAdminWeb.slnx -c Release` (0 errors).

## What
First slice of the Message Analysis detail work stream. Adds two model types to
carry a message's full per-hop delivery trail: `MessageTraceDetailEvent` (one
normalized hop across on-prem `Get-MessageTrackingLog` and cloud
`Get-MessageTraceDetailV2`) and `MessageTraceDetail` (the trail for one
`MessageTraceResult`, fail-soft via an `Error` string).

## Approach
Data-model-only slice. Field names are normalized across the two asymmetric
backends; a field a backend does not supply is left empty. `MessageTraceDetail`
is fail-soft by design (on fetch failure `Error` is set, `Events` empty) so the
caller is never blanked. No service, UI, or job code touches these yet.

## Files changed
- `Models/LookupModels.cs:40-68` — added the two classes.
- `docs/MessageTraceDetail-Plan.md` — approved plan (added same commit).

## Guard proof
No test — data-model-only slice with no behavior. Verified by
`dotnet build ExchangeAdminWeb.slnx -c Release` (0 errors, 22 pre-existing
warnings). Downstream slices that consume these models carry their own tests.

## Coder dispute (if any)
None.

## Known gaps
The model field mapping (which backend field feeds `Detail`/`Source`) is asserted
from the plan, not yet exercised by slice 2's service code. Reviewer should grade
whether the shapes are adequate for both backends as described in
`docs/MessageTraceDetail-Plan.md`.

## Reviewer comments
`Reviewer: codex / gpt-5.5-dzs / xhigh / std` (codex-cli 0.145.0, `codex exec`,
default model/effort as the owner specified; the harness resolved default →
gpt-5.5-dzs at xhigh via portkey). Reviewed SHA `ade48c1`, base SHA `64c3b4c`.
2026-07-27T21:47Z. `guard_confirmed`: true, `capability_ok`: true.

Verdict: **accepted**, no comments. The reviewer independently read the repo,
ran `dotnet build ExchangeAdminWeb.slnx -c Release` (0 errors, 22 pre-existing
warnings), and confirmed the two model shapes are coherent for the downstream
slices with no build break, ASCII violation, or defect. Orchestrator-computed
acceptance: envelope exit 0, schema-valid payload, both SHAs match dispatch,
`guard_confirmed`/`capability_ok` literally true. (The `codex_login` token-refresh
ERROR lines in the transcript are auth-manager background noise; the run completed
via the portkey provider and returned a valid verdict.)
