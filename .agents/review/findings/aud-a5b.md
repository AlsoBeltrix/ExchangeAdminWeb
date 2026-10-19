# aud-a5b: Blocked lockout targets vanish from the report when any other target survives

**Severity**: MEDIUM - a protected principal the guard correctly refused is never shown to the
operator, who is told the sweep succeeded. The refusal is audited, so the record exists; what
is missing is the operator ever being told.
**Status**: DEFERRED by owner ruling 2026-10-19 - module parked; see Closeout
**Branch**: -
**Commit**: -

## Evidence

Found by the implementer while building `aud-a5`, in the method being edited.

`Services/AccountLockoutRemediationService.RunLogoffAsync`:

```csharp
var guard = await GuardTargetUsersAsync(realMachines, context, ticketNumber, action);
realMachines = guard.machineMap;
if (guard.rows.Count > 0 && realMachines.Count == 0)
{
    ... return new AccountLogoffResult(false, message, true, 0, 0, blocked, guard.rows);
}
```

`guard.rows` is referenced nowhere after that early return. The blocked rows therefore surface
**only** when every single target was blocked. Block one protected user out of three and the
operator sees two successful logoffs and no mention of the third.

`aud-a5`'s own SID-resolution failures are added to `allRows` and do not have this problem;
this is the older guard path beside it.

## Predicted observable failure

An operator sweeps three accounts, one a protected executive. The result reports two sessions
logged off and no failures. They believe all three were handled; the executive's session stays
up with nothing on screen saying why.

## Approach - NOT BUILT

Add `allRows.AddRange(guard.rows);` alongside the existing `allRows.AddRange(sidMapping.rows);`,
and keep the early return for the all-blocked case so its message stays specific. The failure
count already derives from `allRows`, so the summary corrects itself.

Guard: a two-user sweep, one protected and one not, asserting the protected user's blocked row
is present in the result and that the failure count includes it.

## Closeout

Not started. Separate commit from `aud-a5` by the one-finding-per-commit rule.

**DEFERRED 2026-10-19 by owner ruling** (`.agents/decisions.md`): Account Lockout
Remediation has never been used and is parked, left registered and disabled. This finding is
not abandoned and stays accurate as handover - it is simply not to be worked. If the module is
ever enabled, this is live again.
