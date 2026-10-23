# kd-8: Self-Service Groups - a previous group's operation can publish its banner into another group's view

**Severity**: LOW - misleading UI. In `Components/Pages/SelfServiceGroups.razor` an operation started on group A completes after the operator has opened group B and sets the page banner, so B shows A's result. Not re-verified at the last state pass; the agent verifies before writing.
**Status**: Open - released by the owner's 2026-10-23 evening ruling (`.agents/decisions.md`); approved under the 2026-10-21 go
**Branch**: -
**Commit**: -
**Reviewer**: implementer (recorded in `.agents/state.md` "Known defects")

## Evidence

Locate the completion path that assigns the banner without comparing the group it ran for with the group now shown. Trigger: start an operation on A, open B before it finishes. Predicted failure: B shows "Added ... to A" or an error about A.

## Approach (to be written by the implementing agent before code)

- Completion publishes the banner only when its group is still the shown group (or scopes the banner per group); page is line-keyed.
- Mutation: the comparison removed.

## Guard proof

Pending.