# cpe-2: The module plan lists a `CloudPasswordReset_Preview` audit action that nothing emits; the preflight read writes no audit record

**Severity**: LOW, pre-existing - a published audit action that does not exist, and a read path that leaves no audit trace
where the Constitution describes this app's reads as all auditing.
**Status**: Open - logged, not approved
**Branch**: -
**Commit**: the commit that adds this record (S4 of `docs/CloudPasswordResetEmployeeId-Plan.md`, branch `feat/cpr-employee-id`)
**Reviewer**: implementer (found while drafting the plan, section 14; recorded at its S4)

## Evidence

At `714c4128`:

- `docs/CloudPasswordReset-Plan.md` about line 630, "Audit and notification", lists the actions
  "`CloudPasswordReset_Preview`, `CloudPasswordReset_Execute`, `CloudPasswordReset_Revealed` and
  `CloudPasswordReset_DeliveryFailed`".
- No `.cs` or `.razor` file in the repo contains `CloudPasswordReset_Preview`. The page's `LookupAsync` - the preflight that
  reads the account from Entra and, since `1.2.0`, may read the directory twice to fill in an employee ID - writes no audit
  event on any path.
- `docs/ProjectConstitution.md`, Notifications: "this app's reads expose only data already visible in AD / the address book
  and all of them audit".

The events are forwarded to Splunk, so a dashboard built from the plan's list would search for an action that never
arrives.

## Approach

Either remove `CloudPasswordReset_Preview` from the module plan's list and say the preflight is not audited (and why), or
audit the preflight. Adding an action is an audit change: by the refusal enum's rule it goes into the plan's "Audit fields,
and Splunk" table in the same commit, and the Constitution routes audit changes through a written plan. Owner decision; not
authorised.
