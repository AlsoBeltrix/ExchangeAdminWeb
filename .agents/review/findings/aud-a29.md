# aud-a29: revoked Bulk Jobs access stays usable through an already-open page

**Severity**: HIGH - an operator whose access has been removed can still cancel and delete
jobs, across every module, for as long as their tab stays open.
**Status**: Verified and fixed; review pending
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 29 of the 2026-10-16 audit, verified against the code.

`Components/Pages/AdminBulkJobs.razor` decides authorization exactly once, in
`OnInitializedAsync`: the `[Authorize(Policy = "AdminBulkJobs")]` attribute gates navigation,
and a re-check at `:164` gates the first render. Neither runs again.

A Blazor Server circuit outlives that decision. `CancelJob` and `RemoveJob` called
`BulkJobs.CancelJob(job.Id)` and `BulkJobs.DeleteJob(job.ModuleId, job.Id)` with **no
authorization between the click and the service call**, so an operator who is removed from
the admin group keeps working until they reload or the circuit drops.

**The reach is the aggravating part, and it is deliberate by design.** The page's own comment
says these actions are "deliberately NOT module-scoped: crossing modules is this page's
purpose, and it is gated behind a fail-closed admin policy for exactly that reason." The gate
that justifies the reach was the one that stopped being applied.

## Predicted observable failure

An administrator is removed from the Bulk Jobs admin group - during an incident, or on leaving
the team. Their open tab continues to cancel and delete other modules' jobs. The audit trail
records each action as a success by a named operator who, at that moment, had no access.

## Approach

A `StillAuthorized` helper re-asks `AuthorizationService.AuthorizeAsync(user, "AdminBulkJobs")`
at the moment of the click, and both mutating handlers return early on failure. The policy is
the page's own, so this cannot drift from what the attribute enforces.

**The refusal is audited, not silently ignored.** An operator still clicking after their
access was removed is worth a record, and a mutating action that refuses without one is
indistinguishable from a bug to whoever investigates it. The operator is told to reload rather
than left looking at a button that does nothing.

Read-only paths are untouched: the finding is about mutation, and a stale read on a page
someone already had open is a different and much smaller question.

## Files changed

- `Components/Pages/AdminBulkJobs.razor`
- `ExchangeAdminWeb.Tests/AdminBulkJobsRevocationTests.cs`

## Guard proof

**Source scans, and that is a real limit.** This page has no test harness - the reflection
harness that drives a live component exists only for Comms-10k - so nothing here proves a
revoked operator is refused at runtime. What is pinned is the ordering the fix rests on.

Review of `aud-a31` showed scans of this shape passing against deliberately broken code, so
these bound each handler's body and check the ORDER of specific calls within it, not the
presence of a string somewhere in the file.

| Test | What it pins |
| --- | --- |
| `NoJobIsTouchedBeforeAccessIsRecheckedAtTheMoment` (2 cases) | the service call comes after the re-check, and the re-check returns out of the handler |
| `TheRecheckAsksThePagesOwnPolicyAndFailsClosed` | the page's own policy, both outcomes, and the audited refusal |

| Mutation | Result |
| --- | --- |
| delete the guard from `CancelJob` | FAIL - the `CancelJob` case; the `RemoveJob` case still passes, so it is specific |
| delete the refusal audit | FAIL - `TheRecheckAsksThePagesOwnPolicyAndFailsClosed` |

Page restored byte-identical (md5 verified), touched, rebuilt.

`AdminBulkJobs.razor` is `tier 4, not approved` in `ClickGateRegistry`, so it carries no
line-keyed entries and the added lines needed no re-anchor. Checked, not assumed.

Gate: Release 0 errors; suite 4336 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean. Module version 1.0.3 -> 1.0.4.

## Known gaps

- No runtime proof, per above.
- The same shape almost certainly exists on other long-lived admin pages that authorize once
  in `OnInitializedAsync` and then mutate. This fix covers Admin Bulk Jobs only; a sweep across
  the admin pages is separate work and is not claimed here.
- A circuit whose authentication state changes mid-session still renders the page; only the
  mutating actions refuse. Hiding or closing the page on revocation is a larger behaviour
  change and was not in scope.

## Closeout

Fixed directly on `master`. Independent review pending.
