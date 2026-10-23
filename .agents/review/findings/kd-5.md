# kd-5: Conference Rooms - CancelJob cancels a running bulk job and audits nothing

**Severity**: MEDIUM - an audit gap on a mutating operator action. `Components/Pages/ConferenceRooms.razor` `CancelJob` (about 837) cancels a running bulk job with no audit row, while `RemoveJob` audits. The Constitution requires audit on every mutating action.
**Status**: Open - released by the owner's 2026-10-23 evening ruling (`.agents/decisions.md`); approved under the 2026-10-21 go
**Branch**: -
**Commit**: -
**Reviewer**: implementer (recorded in `.agents/state.md` "Known defects", re-verified 2026-10-23 as of `d881ea58`)

## Evidence

As above. Trigger: an operator cancels a bulk job. Predicted failure: no audit row; a stakeholder cannot see who stopped the job.

## Approach (to be written by the implementing agent before code)

- `CancelJob` writes the audit row the `RemoveJob` shape writes (operator, job id, ticket if the page carries one, result), including the refused and failed outcomes; page is line-keyed.
- Mutation: the audit call removed.

## Guard proof

Pending.