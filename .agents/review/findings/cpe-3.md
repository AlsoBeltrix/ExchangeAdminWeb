# cpe-3: `FindUserByEmployeeId` is described as finding "the single enabled directory user", but nothing tests `Enabled`

**Severity**: LOW, pre-existing - a comment that promises a rule the code does not apply, or a rule that was meant and is
missing. Which of the two is not established.
**Status**: Open - logged, not approved
**Branch**: -
**Commit**: the commit that adds this record (S4 of `docs/CloudPasswordResetEmployeeId-Plan.md`, branch `feat/cpr-employee-id`)
**Reviewer**: implementer (found while drafting the plan, section 14; recorded at its S4)

## Evidence

At `714c4128`:

- `Services/ADEmployeeIdLookup.cs` line 31, the summary of `FindUserByEmployeeId`: "Find the single enabled directory user
  whose `employeeID` equals ...".
- The LDAP filter is `(employeeID=...)` alone (`BuildEmployeeIdFilter`). The per-domain loop requests `Enabled` (line 199,
  `"Enabled"` in `-Properties`), and nothing reads it: `SearchEveryDomainForOneUser` builds its `ADSearchResult` without it,
  and neither lookup's caller consults it.
- `docs/CloudPasswordReset-Plan.md`'s revision history, the sixth revision (2026-09-14), deleted "the corroboration and
  leaver rules, which are not coming back", and its slice notes removed `Enabled` from `ADSearchResult` because it "was needed
  for corroboration and the leaver rule". That points to the comment being stale; the comment itself does not say so.

S1 of the employee-ID plan moved this code into a shared loop and left the comment as found, because changing either side
was not authorised. The logon-name lookup's own summary makes no claim about `Enabled`; the plan's J9 records that the
fill-in deliberately does not check whether the owner's account is enabled.

## Approach

If the owner confirms no enabled rule is wanted, correct the comment (and stop requesting `Enabled`). If one is wanted, it
changes which reset succeeds - a behaviour change for the module, needing the owner's ruling and a plan. Owner decision;
not authorised.
