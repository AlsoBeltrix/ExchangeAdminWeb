# pgwt-7: Serviced notes vanish from failure audits after the override

**Severity**: MEDIUM - a servicer-authorised attempt on a protected target that later
fails (guard, AD write, read-back) audits WITHOUT the authorising group; a post-timeout
committed change can leave a protected-group mutation with no servicing note.
**Status**: In progress
**Branch**: `-` (default-branch mode)
**Commit**: (filled in after commit)

## Evidence

Both services compute `combinedNote` after the gates, but every post-gate
`PermissionResult.Fail(...)` return drops it; the pages copy `result.ServicedNote` into
the audit extra whenever it is non-empty, success or failure, so carrying it on failures
is sufficient. (The member-note flow had the same shape before this range; the range made
it apply to targets, so it is fixed here for both notes at once.)

## Predicted observable failure

Servicer clears the target gate; `Add-ADGroupMember` times out after committing at the DC;
read-back reports failure; the audit records a failed attempt on a protected group with no
record of who was authorised to touch it - the exact question an audit exists to answer.

## Approach

Every post-override result - success OR failure - carries the combined note:
`PermissionResult` gains a `WithServicedNote` helper, and both services' post-gate
failure returns (guards, write, read-back, exception wrappers inside the closure) route
through it. The pages need no change.

## Files changed

- `Models/PermissionResult.cs` - WithServicedNote
- `Services/GroupManagementService.cs`, `Services/SelfServiceGroups/SelfServiceGroupService.cs`
  - post-gate failure paths carry the note
- `ExchangeAdminWeb.Tests/ProtectedGroupWriteTargetTests.cs` - tripwire + pure test

## Guard proof

- `ProtectedGroupWriteTargetTests::PostGateFailures_CarryTheServicedNote` - revert fails,
  restore passes.

## Coder dispute (if any)

None.

## Known gaps

Failure paths BEFORE the target gate (credentials, resolution) legitimately carry no
note - nothing was overridden yet.

## Reviewer comments

`Reviewer: codex-commercial / gpt-5.6-sol / xhigh / standard` (owner standing dispatch),
generation pass over `8700531..5336072`, verdict `findings` (7), capability_ok true.
Verification round: NOT DISPATCHED - blocked by the workspace-write transport fault
recorded on lst-1.
