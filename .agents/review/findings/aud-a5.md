# aud-a5: Lockout remediation can log off the wrong person's session

**Severity**: HIGH - a destructive action on the wrong account. It bypasses the
protected-principal guard and destroys unsaved work.
**Status**: Open - designed, NOT implemented
**Branch**: -
**Commit**: -

## Evidence

Finding 5 of the 2026-10-16 repository audit, verified against the code.

The identity is stripped of its domain and then matched by bare name, end to end:

1. `Services/AccountLockoutRemediationService.cs:807` - `NormalizeUser` takes
   `LastIndexOf('\\')` and keeps the tail, so `A\alex` becomes `alex`.
2. Event 4740 carries no account domain anyway: `Properties[0]` is the bare `TargetUserName`
   and `Properties[1]` is the caller computer, which this service correctly reads as
   `CallerComputer`.
3. The bare names are grouped per machine and shipped to the remote script as
   `machineUserMap`.
4. `RemoteSessionScript:728` compares them against `quser`'s USERNAME column, which is also
   bare: `if ($userName.ToLowerInvariant() -notin $targetUsers) { continue }`.
5. `:741` then runs `logoff.exe $sessionId`.

So guarding `A\alex` logs off `B\alex`, or a local `alex`, on any machine in the map.

**The protected-principal guard does not save this.** It runs earlier, against a resolved
directory identity, and then hands a bare string to the machine. The account that is actually
logged off was never the account that was checked - the same "checked one object, acted on
another" root as `aud-a1`, `aud-a2b` and `aud-a7`.

## Predicted observable failure

Two domains in the forest each have an `alex`. An operator remediates a lockout for `A\alex`.
`B\alex`, logged on to the same machine, is signed out with unsaved work lost - and if `B\alex`
is a protected principal, the guard that exists to prevent exactly that never saw them.

## Approach - DESIGNED, NOT BUILT

**Match on SID. Nothing else joins cleanly**, and that is the whole reason this is written
down rather than half-built:

- The lockout event gives a bare name and no domain, so the event stream cannot supply it.
- AD resolution gives a DN, from which `GroupManagementService.ServerFromDn` yields a **DNS**
  domain (`child.example.com`).
- `quser` gives no domain at all, and `Win32_LoggedOnUser` gives a **NetBIOS** one (`CHILD`).

Comparing a DNS domain against a NetBIOS one needs another lookup, and comparing bare names is
the defect. The SID is the one form every side can produce and that cannot collide across
domains - the same lesson `aud-a1` recorded.

Shape:

1. The service **already resolves each flagged user** to a `ResolvedDirectoryPrincipal` at
   `:436`, immediately before building the allow-list. Read that principal's `objectSid` there
   (one `Get-ADUser -Identity <DN> -Properties objectSid` on the existing AD runspace).
   `ResolvedDirectoryPrincipal` carries no SID field today, so either add one or read it
   alongside.
2. Pass **SIDs**, not names, in `machineUserMap`.
3. In `RemoteSessionScript`, enumerate `Win32_LoggedOnUser` to get each logged-on account's
   SID, and log off only a session whose owner's SID is in the target set.
4. **Fail closed and say so.** If the owner's SID cannot be established - the CIM query fails,
   or no account matches the `quser` row - skip that session and emit a row with its own
   action (`owner-unverified`), never a silent `continue` and never a logoff. Refusing to act
   on a session is always safe; acting on the wrong one is not.

**Do not fall back to bare-name matching if the SID is unavailable.** That is the defect, and
the owner's standing ruling covers it: *"the onus is on the user to provide accurate data... We
do not guess."*

## Why this is not implemented here

Written down rather than rushed, deliberately:

- It is a **destructive action** on a path with no test harness - the logic lives in a
  PowerShell string embedded in C#, so nothing in this repo can execute it, and the guards
  would be source-level only.
- It needs a new directory read and possibly a new field on a widely used record type, which
  is wider than the three preceding Wave 1 slices.
- It arrived at the end of a long session. The repo's own token-budget rule is one slice per
  session, because late turns are where mistakes are made - and this is the slice where a
  mistake signs the wrong person out.

Everything needed to build it is above. It should be the first slice of a fresh session.

## Known gaps in the design itself

`Win32_LoggedOnUser` lists service and system logons as well as interactive ones, and lists a
user once per logon session, so the enumeration needs de-duplicating by SID before matching.

The `quser` row still supplies the session id to pass to `logoff.exe`; the SID set only decides
*whether* to act. Joining a `quser` session id to a CIM logon session is not direct, so the
design matches by unique account rather than by session id, and must refuse when a row's owner
is not uniquely identifiable.

Nothing here has run against a real machine.

## Reviewer comments

Source: the 2026-10-16 audit, finding 5. Verified by the implementer. No fix to review yet.

## Closeout

Not started. Next slice.
