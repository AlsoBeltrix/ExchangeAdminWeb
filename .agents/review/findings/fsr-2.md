# fsr-2: Constitution and repo-guidance still mandate the self-service target gate

**Severity**: LOW - documentation authority drift, no runtime behavior wrong: the highest
engineering authority still states the rule the owner reversed on 2026-08-31, so a future
governance-compliant session could "fix" self-service by restoring the removed gate.
**Status**: In progress
**Branch**: `-` (default-branch mode)
**Commit**: (pending)

## Evidence
`docs/ProjectConstitution.md` (protected-principals rule: every writing module refuses
protected targets, no self-service carve-out) and `.agents/repo-guidance.md` Known Failure
Class #3 ("every mutating module must route its write target through the
protected-principal check before writing") both predate the 2026-08-31 ruling recorded in
`.agents/decisions.md` and the plan's Revision 2026-08-31. Authority order puts the
Constitution above both, so the stale sentence wins a naive read.

## Predicted observable failure
A fresh session applying the documented authority order treats SelfServiceGroupService's
intended behavior as a violation and re-adds the gate, re-breaking owner edits of owned
protected groups.

## What
The 2026-08-31 decision updated the plan but not the two higher/adjacent authorities that
state the old rule. This finding is a miss in that decision's "update affected guidance"
step.

## Approach
Amend the Constitution's protected-principals rule with the narrowly scoped self-service
exception (owner ruling 2026-08-31, pointer to `.agents/decisions.md`), align the
repo-guidance Known Failure Class #3 sentence, and add a regression tripwire pinning that
`SelfServiceGroupService` does NOT consult the write-target gate while RETAINING its
member-protection check.

## Files changed
- `docs/ProjectConstitution.md` - exception sentence + decision pointer
- `.agents/repo-guidance.md` - Known Failure Class #3 alignment
- `ExchangeAdminWeb.Tests/` - the no-gate/keep-member-check tripwire

## Guard proof
`ExchangeAdminWeb.Tests/ProtectedGroupWriteTargetTests.cs::SelfService_DoesNotConsultTheTargetGate_ButKeepsTheMemberGate`
- probed 2026-08-31: a `ForWriteTarget` marker inserted into SelfServiceGroupService FAILS
it (1/1 fail); restored, PASSES (1/1).

## Coder dispute (if any)
None - the finding is correct; the doc updates apply the owner's already-given ruling, and
the Constitution edit is flagged to the owner in the session report.

## Known gaps
None.

## Reviewer comments
`Reviewer: codex-commercial / gpt-5.6-sol / xhigh / standard` (inline, session-only; owner
dispatch "codereview codex gpt-5.6-sol xhigh"), generation pass over
`ac6face9c1025d9b6064102f3f0c43f8390618ef..3505e6707ccdecf41146913845f1275709ed1532`,
verdict `findings` (2), `capability_ok: true`, 2026-08-31. Transport notes: see fsr-1.
