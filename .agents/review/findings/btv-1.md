# btv-1: ValidateTickets switch fails open on unparseable values

**Severity**: HIGH - a mistyped security switch silently downgrades to presence-only
while the operator believes ServiceNow validation is enforced.
**Status**: Verified (plan revised; docs-only - no code exists yet)
**Branch**: -
**Commit**: lands in the same commit as this record and the plan revision; read it
from `git log -1 -- .agents/review/findings/btv-1.md` (never inferred, per idm-5).

## Evidence

`docs/BitLockerMandatoryTicket-Plan.md` (pre-fix, at `533c1fe`): AC4 read "An
unparseable `ValidateTickets` value behaves as Off - the `PreventSelfGrant`
convention (`PermissionValidator.cs:62`)", and design step 3 folded
absent/blank/unparseable/false into one Accepted branch. The field is free text in
Module Config - `Modules/ModuleConfigField.cs:3-8` has no Boolean field type - so
`yes`, `on`, `enabled`, or a typo of `true` are reachable operator inputs.

## Predicted observable failure

An admin sets `ValidateTickets` to `yes` believing validation is now enforced;
every non-blank ticket is accepted with no ServiceNow call, and nothing on screen
or in the audit says validation was skipped. A silent downgrade of a control the
operator believes is on - the same decorative-control class as idm-3.

## What

Plan defect. The planned parse rule treated a mistyped security switch the same as
an unset one, borrowing the `PreventSelfGrant` convention from a flag that is a
behavior preference, not a validation gate.

## Approach

Plan revised, not code (none exists): absent/blank stays Off - unset is not a
mistype; any NON-EMPTY value `bool.TryParse` rejects returns Unavailable with a
message naming the invalid value, so the search refuses until the setting is
fixed. The divergence from the `PreventSelfGrant` convention is recorded in the
plan as deliberate. The reviewer's alternate remedy (new Boolean `ConfigFieldType`
+ checkbox rendering) was not adopted: it is Module Config UI scope the plan does
not need, and the fail-closed parse achieves the safety property.

## Files changed

- `docs/BitLockerMandatoryTicket-Plan.md` - AC4, design step 3, failure-behavior
  table (new row), test table (`UnparseableSwitchUnavailable`), AC12, Review log.

## Guard proof

Docs-only. The revised plan specifies `UnparseableSwitchUnavailable` with its own
non-vacuity mutation ("treat unparseable as Off -> FAIL"), which bites at
implementation time. `git diff --check` clean on the fold commit.

## Coder dispute (if any)

None. Admitted on the evidence; only the checkbox half of the remedy declined as
out-of-scope (recorded above and in the plan's Review log).

## Known gaps

None.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier
  (grade fallback; owner-named dispatch: "have codex review all the plans")
Harness: codex-cli 0.150.1, `codex exec --json` with output schema.
Reviewed SHA `533c1fe3ae93f7444455646365f4e48d11d4d5e2`, base
`a9b0ebc3e28b12e19977550aefefe9bd8edf6db7`, capability_ok true, verdict
`acceptable_with_changes` (openreview; this finding also appeared as material
change 1). Dispatched 2026-08-31; envelope at `.agents/review/plans2.result.json`.
