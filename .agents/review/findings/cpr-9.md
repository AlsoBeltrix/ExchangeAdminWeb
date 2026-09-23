# cpr-9: The write derived its destination from stale preflight data

**Severity**: HIGH - the password can be mailed to the owner of an employee ID the account no
longer carries, or the delivery mode can silently change from "email it to Jo" to "show it on
screen", neither of which the operator confirmed.
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: the commit that completes this record

## Evidence

`LookupAsync` stored a `CloudPasswordTarget` from the preflight Graph read. `ExecuteResetAsync`
then called `ResetService.DeriveDestination(target)` on that cached object, and
`DeriveDestination` reads `target.EmployeeId`.

**So only half the derivation was fresh.** The AD lookup ran at confirm time; the employee ID it
looked up came from a Graph read that could be arbitrarily old. The plan requires the whole
derivation at the moment of reset.

**My own guard asserted this property and passed anyway.** `The_destination_is_re_derived_at_write
_time_not_taken_from_preflight` checked that `DeriveDestination` was called inside the handler,
which was true of the defective code. A test that confirms the shape of a call while the thing it
is meant to guarantee is false is worse than no test: it is a green light on the wrong claim.

## Predicted observable failure

An operator previews account A, whose employee ID resolves to Jo. Someone corrects the account's
employee ID - or the owner's mailbox changes - and the operator then confirms. The module PATCHes
the account and mails the password to Jo, who is no longer the owner, and the audit records the
old employee ID as the reason.

The sharper variant: the AD lookup succeeds at preflight and fails at confirm. A reveal-permitted
operator who confirmed "this will be emailed to Jo" instead gets the password on screen.

## Approach

Re-read the account from Entra before the write, re-run the scope checks with it, and derive from
the fresh object. Then compare the result against what the operator actually confirmed - both the
address and the delivery mode - and **stop** if either changed, rather than proceeding with a
destination nobody approved.

Stopping rather than silently continuing is the point. The operator is shown the new state and can
confirm again.

## Files changed

- `Components/Pages/CloudPasswordReset.razor` - the re-read, the re-derivation and the
  changed-destination abort.
- `ExchangeAdminWeb.Tests/CloudPasswordResetWritePathTests.cs` - the strengthened guard and two new
  ones.

## Guard proof

Mutation: revert to `DeriveDestination(target)` on the cached object. Three tests fail, including
the strengthened original - which the defective code had passed before it was strengthened.

## Coder dispute

None.

## Known gaps

The re-read closes the window to the width of the handler itself; it cannot close it entirely,
because the directory can change between the re-read and the PATCH. That residue is inherent to a
distributed write and is not worth a lock.

## Reviewer comments

Round 1, Change review over `3b29e5f^..3b29e5f`. Same dispatch as `cpr-8`.

## Closeout

Fix and this record land with cpr-8, cpr-10 and cpr-11 in one commit.
