# aud-a5: Lockout remediation can log off the wrong person's session

**Severity**: HIGH - a destructive action on the wrong account. It bypasses the
protected-principal guard and destroys unsaved work.
**Status**: Verified; module DEFERRED by owner ruling 2026-10-19 - see Closeout
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

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

## Approach

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

## Implementation - BUILT

Two halves, and both are needed.

**C# (`MapTargetsToSidsAsync`)** replaces every username in the machine map with the SID of
the directory object that username resolves to. It resolves through the same
`ResolveWithExchangeFallbackAsync` the guard uses, takes the resulting distinguished name, and
reads `objectSid` **from the domain that owns the object** - `GroupManagementService.ServerFromDn`,
the rule `aud-a3` established - rather than from whichever domain this host sits in.

It fails closed at every step. Unresolved, ambiguous, no DN, DN with no domain, lookup threw,
or a value that is not an `S-1-...` string: the user is dropped from the map and reported, and
the run continues for the others. There is deliberately **no fallback to the name** - that
fallback is the defect, and the owner's standing ruling covers it: inaccurate input is the
user's to resolve, not ours to guess around.

It runs on the **dry-run path as well as the destructive one**. A dry run that names the wrong
person's session is what an operator reads before deciding to execute, so it must be as
accurate as the logoff.

**PowerShell (`RemoteSessionScript`)** no longer matches the `quser` USERNAME column against
anything. It enumerates `Win32_Process` once per machine, takes each process's owner SID via
`GetOwnerSid`, discards machine/service/virtual identities (`S-1-5-18/19/20`, `S-1-5-80/82/83/90/96-*`)
and groups what is left by terminal-services session id. A session is acted on only when it has
**exactly one** remaining owner and that owner's SID is in the target set. `$quserName` survives
only as a display string.

A session whose owner cannot be established - no identifiable processes, or more than one
distinct owner - produces an `owner-unverified` row and is **refused**, never logged off. Such a
session may well be the target; refusing it is the only safe answer. If the CIM enumeration
fails outright, the machine reports `owner-query-failed` and logs nobody off.

One enumeration per machine rather than one per session: the per-session filter would fetch the
same data once per row.

## Files changed

- `Services/AccountLockoutRemediationService.cs`
- `ExchangeAdminWeb.Tests/AccountLockoutSessionIdentityTests.cs` (new)
- `ExchangeAdminWeb.Tests/AccountLockoutRemediationServicerTests.cs`
- `Modules/ModuleCatalog.cs` (module version 1.2.2 -> 1.3.0)

## Guard proof

**The script tests run the shipped script.** `RemoteSessionScript` is read off the service by
reflection and executed in a local runspace with `quser.exe`, `Get-CimInstance`,
`Invoke-CimMethod` and `logoff.exe` replaced by functions - PowerShell resolves a function ahead
of a native command, so the script under test is unmodified. These are behaviour tests, not
source scans, which matters more here than anywhere else in the repo: this is the module that
signs people out.

| Test | What it pins |
| --- | --- |
| `ASessionOwnedByTheSameNameInAnotherDomain_IsNotLoggedOff` | the defect itself |
| `TheTargetsOwnSession_IsLoggedOff` | the fix did not simply stop working |
| `TheRightSessionIsPickedOutOfSeveralSharingTheDisplayedName` | two `alex` sessions, only the right one goes |
| `ASessionWhoseOwnerCannotBeIdentified_IsRefusedAndReported` | fail closed, with a row |
| `ASessionWithSeveralDistinctOwners_IsRefusedRatherThanGuessedAt` | ambiguity refuses |
| `MachineAndServiceIdentitiesNeverCountAsASessionOwner` | SYSTEM and friends do not make every session ambiguous |
| `ADryRunReportsTheRightSessionAndLogsNobodyOff` | the report is as accurate as the action |
| `WhenSessionOwnersCannotBeReadAtAll_NothingIsLoggedOff` | CIM failure logs nobody off |
| `AResolvedTarget_IsCarriedAsItsSid_NotItsName` | the map carries SIDs |
| `TheSidIsReadFromTheDomainThatOwnsTheObject` | `-Server` comes from the target's own DN |
| `ATargetWithNoDirectoryObject_IsDroppedAndReported` | cloud-only identity cannot match a Windows session |
| `ASidLookupFailure_DropsTheTargetRatherThanFallingBackToTheName` | no name fallback |
| `AnythingThatIsNotASid_IsRejected` | three cases: null, empty, a bare name |
| `OneTargetFailing_DoesNotTakeTheOthersWithIt` | per-user, not blanket |

Three mutations, each caught by the test that should catch it:

| Mutation | Result |
| --- | --- |
| match the `quser` username against the target labels again | FAIL - `ASessionOwnedByTheSameNameInAnotherDomain_IsNotLoggedOff`: the other domain's alex is signed out |
| treat an unidentifiable session's owner as the target | FAIL - `ASessionWhoseOwnerCannotBeIdentified_IsRefusedAndReported` |
| fall back to the bare name when the SID read throws | FAIL - `ASidLookupFailure_DropsTheTargetRatherThanFallingBackToTheName` |

**The first mutation initially did not fail the cross-domain test, and that was a flaw in my
test, not in the mutation.** The test passed no display-name map, so nothing on the target's
side carried the string "alex" and a name-matching regression found no match - it would have
passed for the wrong reason. The test now supplies the display name, so name matching logs off
session 2 and the assertion breaks. Recorded because a guard that passes for the wrong reason
is worse than no guard.

A second real divergence surfaced while writing these: `$sidDisplayNames` arrives as a
`Hashtable` over remoting but as a `Dictionary[string,string]` in-process, and those do not
share a single-argument lookup method. `.Contains()` threw in-process. The script now indexes,
which works on both. Only running the script caught that; a source scan never would have.

Source restored byte-identical after each mutation (md5 `2f06cb8bf715b09668bb363e6c5fdf22`,
`md5sum -c` OK), touched, and rebuilt before re-running.

Gate: Release build 0 errors; `dotnet test` on the solution 4297 passed / 3 known AD skips /
0 failed (4281 before this slice, +16).

## Coder dispute

None.

## Known gaps

- **Nothing here has touched a real machine.** No real `quser`, no real WinRM hop, no real CIM.
  The stubs return what the real commands are documented to return; that is an assumption.
- **The `quser` session id to CIM session id join is by number, not by handle.** Both are the
  terminal-services session id, so this is sound in principle, but it is unverified against a
  live multi-session host.
- **A session with no processes at all cannot be identified** and is therefore refused. A
  genuinely idle disconnected session could in principle show up this way and be skipped where
  the old code would have logged it off. That is the fail-closed direction and is deliberate,
  but it is a behaviour change an operator may notice.
- The execute path now resolves each target three times (guard, guard re-read, SID). That is a
  cost, not a correctness problem, and it was preferred over threading the SID out of the
  guard's internals.
- `aud-a5b`, found while doing this: `RunLogoffAsync` **discards** `GuardTargetUsersAsync`'s
  blocked rows whenever at least one user survives the guard, so a refused protected principal
  vanishes from the operator's report. Separate finding, separate commit.

## Reviewer comments

Source: the 2026-10-16 audit, finding 5. Verified by the implementer before changing anything.
Independent review of the fix pending.

## Closeout

Fixed directly on `master`. Not pushed.

## Independent review, 2026-10-19 (codex-commercial / gpt-6-astra / max) - CHANGES REQUIRED

Four HIGH and one MEDIUM. The reviewer's summary is the fair one: "the bare-name comparison is
gone, but this fix does not guarantee that the person logged off is the requested,
protected-principal-checked person." Every one of the five was correct.

### 1. Partial CIM failures let another user's process stand as proof of ownership - FIXED

`GetOwnerSid` exceptions, non-zero return codes and empty SIDs were silently discarded. Windows
lets a user run a process under another account inside their own desktop, and `GetOwnerSid` is
documented to return access-denied. So: B owns session 2 and runs something as A; B's processes
fail to read, A's succeeds; the set is `{A}`; targeting A logs off B. **The "exactly one owner"
rule was doing less than it looked - it counted what could be read, not what was there.**

Unreadable owners are now counted per session, and a session with any unreadable owner is
refused as `owner-unverified` however few readable owners remain.

### 2. The owner snapshot was taken before the session list it was joined to - FIXED

Ownership was collected, then `quser` ran, then rows were joined to the snapshot by session
number. Windows reuses terminal-services session ids; a session ending in that gap could hand
its id to a new occupant whose `quser` row then matched the previous occupant's cached SID.

The enumeration now runs after `quser`. **This narrows the window rather than closing it** -
the snapshot is still not atomic with the logoff - but it no longer precedes the list it is
joined to.

### 3. The SID was not bound to the object that passed protection - FIXED

The worst of the five, because it is this wave's own defect class inside its own fix. The guard
resolved, re-read, compared immutable identity and approved **that object** - then passed on
only the username, and `MapTargetsToSidsAsync` resolved the username a third time and took
whatever object came back. A rename or an alias moving between the two meant one object passed
protection and a different object's SID became the destructive target.

**The record previously called that third lookup "a cost, not a correctness problem". That was
wrong, and the reviewer said so.**

The guard now returns the principal it approved; the SID step refuses with `identity-changed`
if the object it resolves is not that object. The SID read also returns the object's GUID, so a
replacement object reusing the DN between resolution and read is refused too.

### 4. Domain-qualified input still selects the wrong account - NOT FIXED, see `aud-a5c`

`NormalizeUser` strips `A\`, and the resolver then searches by bare name against the host's own
domain, so on a host in domain B, `A\alex` resolves to `B\alex`. The new code faithfully
obtains and matches **B's** SID. Reading from the domain of an already-wrongly-selected DN does
not repair the selection.

The reviewer also found that the discovery path discards event 4740's `TargetSid`, which is the
locked-out account's SID, present in the event schema. That is a better identity than anything
resolved from a name.

**This is the root the A5 design missed**, and this record's own claim that "the lockout event
gives a bare name and no domain, so the event stream cannot supply it" is wrong as written -
the event carries the SID. Filed as `aud-a5c`, not fixed here.

### 5. The empty-SID-map return dropped blank-source rows - FIXED

A regression this fix introduced: the new early return exited before `includeBlankSourceRows`
was appended, so a discovery run finding only events with no caller computer reported nothing
explaining itself. Row construction is now a shared helper used by both return paths.

### Guards added

| Test | What it pins |
| --- | --- |
| `AnUnreadableProcessOwnerMakesTheSessionUnverified_NotSomebodyElsesProof` | finding 1, with the RunAs counterexample |
| `AnUnreadableProcessInTheTargetsOwnSession_StillRefuses` | fail closed even when the readable owner IS the target |
| `SessionOwnershipIsReadAfterTheSessionList_NotBeforeIt` | finding 2's ordering, via recorded stub call order |
| `ATargetThatResolvesToADifferentObjectThanTheGuardApproved_IsBlocked` | finding 3 |
| `AnObjectThatReplacedTheTargetsDistinguishedName_IsBlocked` | finding 3 at the SID read |
| `TheGuardHandsOnTheObjectItApproved` | the guard actually supplies it |

Three mutations, each caught by the right test: dropping the unreadable-owner refusal fails
both finding-1 guards; dropping the guarded-object comparison fails
`ATargetThatResolvesToADifferentObject...`; moving the ownership read back above `quser` fails
the ordering guard. Source restored byte-identical (md5 `d0aa3ffda3eca2f2c015dbd99d2ea676`),
touched, rebuilt each time.

**Finding 5 has no guard.** It lives in `RunLogoffAsync`, which fetches a credential and
reaches a live directory before that line, so nothing can drive it. Stated rather than papered
over.

### Gate

Release 0 errors; format and `git diff --check` clean. **The suite was 4309 passed / 3 known AD
skips / 1 FAILED**, and the failure is not this change: it is
`DefenderEnrichmentCancellationTests.CanceledTokenLockWaiterCannotSendALaterRequest`, a
load-dependent intermittent that passes in isolation, predates this work and touches none of
it. It is the previously unidentified flake, now identified and recorded as `flake-1`.

Module version 1.3.0 -> 1.3.1.

**DEFERRED 2026-10-19 by owner ruling** (`.agents/decisions.md`): Account Lockout
Remediation has never been used and is parked, left registered and disabled. This finding is
not abandoned and stays accurate as handover - it is simply not to be worked. If the module is
ever enabled, this is live again.
