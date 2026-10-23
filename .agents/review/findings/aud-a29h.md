# aud-a29h: the revalidation off-switch warning says a removed operator keeps access "until it is reloaded", which is not what happens

**Severity**: LOW - wording that misleads during an incident. `Authorization/MembershipRevalidatingAuthenticationStateProvider.cs` about 238-240 and 247-249, and `README.md` about 873, say that with the off-switch set a removed operator keeps access "until it is reloaded". Under Kerberos a reload keeps the stale access until the ticket renews, and with the switch off page requests are not checked either (aud-a29g). Someone switching revalidation off during an incident would wrongly expect a reload to cut a removed operator off.
**Status**: Open - admitted 2026-10-23 evening from the aud-a29g agent's hand-back; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: implementer (aud-a29g agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `a5d12cc8`, lines as above. Trigger: the operator reads the startup warning or README while deciding to flip the switch. Predicted failure: a wrong operational expectation; no code path changes.

## Approach

- Reword both the warning and the README sentence to say what is true: with the switch off, nothing re-checks group membership - not a reload, not a page request - until the operator's Kerberos ticket is renewed or the app restarts. A test pins the warning text as the aud-a29b tests pin its presence.
- Mutation: the old wording restored.

## Guard proof

Pending.