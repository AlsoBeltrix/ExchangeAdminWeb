# aud-a23j: Migration - a failed excluded-group check leaves the user Eligible, and the group match is a substring match on the DN

**Severity**: MEDIUM - fail-open eligibility, the aud-a23h class on the AD step. When the excluded-group
check throws (AD unreachable, the group unresolvable), the eligibility check only adds a warning and the
user stays Eligible, so a batch can be created for a member of an excluded group. The match itself is a
substring test on the group DN, so a group whose DN contains the configured DN as a substring matches too.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23h/a23i agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `ab5361c8`: `Services/MigrationService.cs`, the excluded-group check inside the eligibility path (its
catch adds a warning; the `Contains` on the DN).

## Approach (to be written by the implementing agent before code)

- A failed excluded-group check yields `MigrationStatus.Undetermined` with "the excluded-group check could
  not be completed: <reason category>" - the aud-a23h shape; batch creation stays blocked.
- The group match compares DNs exactly (ordinal, ignoring case, as AD does) against the user's memberOf
  values, never a substring.
- Tests through `MigrationStubExchange` and the AD stand-in; mutations at the call site: the warning-only
  catch again; `Contains` again.

## Guard proof

Pending.
