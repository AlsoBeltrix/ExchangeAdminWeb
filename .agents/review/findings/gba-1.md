# gba-1: Bulk plan weakened the per-write authorization invariant

**Severity**: HIGH - a batch of AD writes could continue under a page-level
authorization result taken before the first write, so a revocation mid-batch would
not stop later rows; the Constitution's per-write rule is the module's real gate.
**Status**: Verified (plan revised; docs-only - no code exists yet)
**Branch**: -
**Commit**: lands in the same commit as this record and the plan revision; read it
from `git log -1 -- .agents/review/findings/gba-1.md`.

## Evidence

`docs/ProjectConstitution.md:28` - "Every mutating operation must re-check
authorization immediately before the write." `docs/GroupBulkActions-Plan.md` at
`2e89f7a`: section 4 row "Authorization re-check (once before the loop, page)" and
section 6's bulk handler pseudo-code "auth re-check once" followed by the sequential
loop; AC3 simultaneously claimed the per-member path was "byte-for-byte the single
path", which today (`GroupManagement.razor:418-425`, `SelfServiceGroups.razor:468-475`)
re-checks authorization inside every single-member handler.

## Predicted observable failure

An operator whose `GroupManagementOnPrem` (or `SelfServiceGroups`) access is revoked
after a 200-row batch starts keeps writing until the loop ends; the per-row audit
events record writes an unauthorized principal performed. A faithful implementation
of the plan as drafted would have produced this while every planned test stayed
green, because no planned test pinned where the check lived.

## What

Plan defect: the batch orchestration hoisted a per-write invariant to per-batch.

## Approach

Plan revised: the module authorization re-check moves INSIDE `RemoveOneAsync` /
`AddOneAsync`, the extracted per-member handlers both the single button and the bulk
loop call, so it runs immediately before every row's service call and a per-row denial
is audited as the single path audits it today. The batch handler keeps an upfront
check only as a UX shortcut (refuse early, audit the batch as failed) and the plan
says so. The `ClaimsPrincipal` handed to the service is the one the per-row check just
authorized, fetched inside the handler. New source guard
`<Page>_PerRowHandlers_RecheckAuthorization` pins the location.

## Files changed

- `docs/GroupBulkActions-Plan.md` - AC3, section 4 (two rows), section 6
  (extraction paragraph and pseudo-code), section 8 (new guard), section 10.

## Guard proof

Docs-only. The planned source guard's mutation (hoist the check above the loop ->
FAIL) bites at implementation (S2/S4). `git diff --check` clean on the fold commit.

## Coder dispute (if any)

None. The reviewer is right and the draft contradicted itself.

## Known gaps

None.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier
  (grade fallback; owner goal-directive 2026-09-03: "codereview with codex (default)")
Harness: codex-cli 0.152.1, `codex exec -s read-only`. Reviewed SHA `2e89f7a`, base
`f1bec06`, capability_ok true, verdict `acceptable_with_changes` (openreview;
material change 1). Dispatched 2026-09-03; envelope at
`.agents/review/gba.result.json`.
