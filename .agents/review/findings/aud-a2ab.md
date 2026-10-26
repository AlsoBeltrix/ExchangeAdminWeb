# aud-a2ab: a room list the row creates is checked against Protected Group Targets only after Set-Place and New-DistributionGroup have run

**Severity**: HIGH - a protected-target rule is discovered only after the app has created the list and changed the room's metadata. `Services/ConferenceRoomService.cs` about 859 (`PreflightRoomListAsync` returns success for a missing list because "there is nothing to check") and about 2106 (the created list is checked after `New-DistributionGroup`); `ExchangeAdminWeb.Tests/ConferenceRoomListWriteTargetTests.cs` about 406 pins the partial outcome. Trigger: the Building has no room list and a protected-principal row names the list that will be created (for example by its primary SMTP address). Predicted failure: `Set-Place` runs, `New-DistributionGroup` runs, then the add is refused - a partial row, contrary to the aud-a2e record's "whole row refused before any write" (Known Failure Classes 1 and 3).
**Status**: In progress - admitted 2026-10-23 evening from codex cr-batch-7 on aud-a2e; inside the released scope
**Branch**: `fix/a2e` (worktree `D:\source\ExchangeAdminWeb-wt\a2e`), continuing from `ebd66b91`
**Commit**: -
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base eb017102 head ebd66b91 (merged 3e43b35b); 2026-10-23 ~20:0xZ; capability proof: AGENTS.md read and git show --stat ebd66b91 both succeeded

## Evidence

Codex cr-batch-7 finding 1 at `3e43b35b`.

## Approach

Written before the code, against `ebd66b91`. The brief: make the to-be-created list's identity deterministic and check
that intended target before ANY row write; if it cannot be determined before creation, fail closed and refuse the row;
keep the post-creation re-check; change the test at about 406 to assert refusal before `Set-Place`.

**The order today.** `SetRoomMetadataAndListAsync` resolves the list (`:671`), runs `PreflightRoomListAsync`, then writes
Set-Place (Step 1), City/State/Country, the timezone, and last the add. The preflight's first line returns success for a
list that does not exist (`:2106-2109`, "there is nothing to check before the writes"), so for a Building with no room
list every one of those writes runs, `AddToRoomListAsync` creates the list (`:859-869`) and only then checks it
(`:901-916`). A protected-target rule naming that list is found after the app has changed the room and created a group.

**The intended identity cannot be determined before creation, so the row refuses.** Not a choice between two workable
designs - the information does not exist app-side:

- the creation passes `-Name` and `-RoomList` and no `-PrimarySmtpAddress` (`:860-863`), so Exchange's email address
  policy assigns the address. Nothing in the app computes it, and no cmdlet is asked what a policy would assign;
- what the rules match on, a new cloud list has none of. Protected Group Targets are `objectGUID|DN`, the OU rules key on
  a DN and the pattern rules on a sAMAccountName - a cloud list has no on-premises object at all (aud-a2e, Known gaps) -
  and the user rows match the address or the Entra object id, both of them the policy's to assign at creation;
- so a check built from what IS deterministic, the canonical name, would be matched against no rule kind and would pass
  every time. A check that cannot fail is worse than none: it would report the gap closed.

The fail-closed branch of the brief therefore applies. `PreflightRoomListAsync`'s `!list.Exists` branch returns a refusal
instead of success: the row writes nothing, the message names the list that would have to be created and ends "Nothing
was written.", and it is logged once where it is decided, as every refusal on this path is.

**The post-creation re-check stays**, as the brief requires. Through the row it is now unreachable - no row reaches the
add with a list that does not exist - so it stands as the add's own guard and is proven by a direct call, the shape
`TheOnPremAddsRefusalOfARoomWithNoDirectoryObject_IsLoggedOnce` already has for a guard the row can no longer reach.

**Consequence, an owner call (Known gaps).** Room Finder apply no longer creates a building's room list. The operator
creates the list in Exchange Online and re-runs the row. Reversing this means accepting a write before the check - either
the row's metadata writes as today, or creating the list and then refusing - which is what the finding is.

**Files to change.** `Services/ConferenceRoomService.cs` (the `!list.Exists` branch and one refusal builder beside the
other two);`ExchangeAdminWeb.Tests/ConferenceRoomListWriteTargetTests.cs` (the test at `:406` asserts refusal before
`Set-Place`; a new direct-call test keeps the post-creation re-check proven);
`ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs` (its two missing-list tests, `:1166` and `:1183`, become
direct calls of the add - they pin the creation's own behaviour, not the row's);
`Modules/ModuleCatalog.cs` (Conference Rooms 2.13.0 on master -> 2.14.0; this branch's value comes from its own
aud-a2e commit, so the diff reads 2.7.0 -> 2.14.0); this record.

**Mutation planned.** The refusal removed, so the preflight returns success for a missing list again and the old partial
outcome returns.

## Files changed

- `Services/ConferenceRoomService.cs` - `PreflightRoomListAsync`'s `!list.Exists` branch refuses instead of returning
  success, logged once where it is decided; `RoomListDoesNotExistRefusal` beside the other two refusal builders; the
  add's target-selection comment records that its created-list check is now the add's own guard.
- `ExchangeAdminWeb.Tests/ConferenceRoomListWriteTargetTests.cs` - the test at `:406` is now
  `ABuildingWithNoRoomList_IsRefusedBeforeAnyWrite` (refusal before `Set-Place`, nothing written, logged once);
  `ABuildingWithNoRoomList_IsRefusedWithNothingProtectedAtAll` and `TheAdd_StillChecksAListItCreates_BeforeAddingTheRoomToIt`
  are new.
- `ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs` - `AListTheRowCreates_IsNamedByTheGuidTheCreationReturned`
  becomes `AListTheAddCreates_...` and, with `AListCreatedWithoutAGuid_IsNotAddedToByItsName`, drives the add directly;
  `NoListYet()` is now the missing-list target those calls pass. Their assertions are unchanged but for the partial flag,
  which belonged to the row.
- `Modules/ModuleCatalog.cs` - Conference Rooms `2.14.0`, one step past master's `2.13.0`.

## Guard proof

| Test (`ConferenceRoomListWriteTargetTests`) | What it pins |
| --- | --- |
| `ABuildingWithNoRoomList_IsRefusedBeforeAnyWrite` | the finding: the row refuses, `_h.Writes` is empty, no `New-DistributionGroup`, the message is the refusal and ends "Nothing was written." once, and the warning naming the room, the list and the reason is logged once |
| `ABuildingWithNoRoomList_IsRefusedWithNothingProtectedAtAll` | the refusal does not rest on a rule - an empty protection store refuses too, because the identity cannot be established at all |
| `TheAdd_StillChecksAListItCreates_BeforeAddingTheRoomToIt` | the post-creation re-check the brief asked be kept, driven directly: the creation runs, the check refuses, no `Add-DistributionGroupMember` |

**Mutation**, at the call site, applied by a script that refuses unless the old text occurs exactly once and the md5
changes; rebuilt; the named tests run; the file restored from the saved bytes with a fresh mtime; rebuilt; run again.

| # | Mutation | File, md5 before (= restored) | md5 mutated | Named tests | Mutated / restored |
| --- | --- | --- | --- | --- | --- |
| AB1 | the preflight's refusal removed - a missing list returns success again, so the row writes Set-Place and creates the list before the check (the behaviour the finding describes) | `ConferenceRoomService.cs` `9316a76e8360007d548d9431b5ba8e52` | `445ba26f895f59b6845251bdc92416df` | the three above | FAIL 2 of 3, PASS 1 / PASS 3 |

The one that passes under the mutation is `TheAdd_StillChecksAListItCreates_...`: it drives the add directly, so the
preflight's branch cannot reach it - the discrimination the guard set is meant to show.

## Coder dispute

None on the finding. One correction to its Severity line: the two line numbers are transposed - `PreflightRoomListAsync`'s
"nothing to check" return is at `:2106` and the created list is checked after `New-DistributionGroup` at about `:859-916`.

## Known gaps

**Owner call: Room Finder apply no longer creates a building's room list.** This is the cost of the fail-closed branch
the brief names, and it is a feature removal, not only a refusal:

- what the module cannot do: a Building whose canonical and legacy room lists are both absent is refused, where it used
  to create the list cloud-side and add the room (owner, 2026-06-18, `New-DistributionGroup` with no OU). The operator
  creates the list in Exchange Online and re-runs the row; the refusal says so by name;
- why no narrower rule was taken: the address is assigned by Exchange's email address policy at creation, and the
  address and the Entra object id are the only identifiers a protection rule can match a cloud list by, so no check made
  before the creation can fail. Refusing only when some rule exists would make the gap depend on configuration the
  operator cannot see, and a check on the canonical name alone would pass every time;
- what would reverse it: accepting a write before the check - either the row's metadata writes as today (the finding),
  or creating the list and refusing afterwards (a smaller write, still on a refusal path, Known Failure Class 1). Both
  are the owner's to choose; neither is taken here.

**The preview still offers to create.** `ResolveRoomListAsync` reports `exists: false` and the Room Finder preview marks
such a row with a "new" badge (`Components/Pages/ConferenceRooms.razor:204`), so the operator is told the list will be
created and the apply then refuses. Correcting the badge is a page edit, outside this finding's lines and on a file a
second agent is editing in place; recorded as a follow-up candidate rather than taken.

**Source-level and fake-driven, not live.** No Exchange call was made. *Assumption, unverified:* that
`New-DistributionGroup -RoomList` with no `-PrimarySmtpAddress` takes the address from the email address policy, which is
why the identity cannot be computed. If a deployment pinned the address another way, a narrower rule would become
possible; nothing here would then be unsafe, only stricter than it needs to be.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh`, cr-batch-7 finding 1 over `3e43b35b`. Implemented
under the owner's 2026-10-21 go, released by the 2026-10-23 evening ruling, by an Opus implementation agent on
`fix/a2e`. Independent review of the fix pending.

## Closeout

Fixed on branch, not merged, not pushed. Module-scoped: Conference Rooms `2.14.0` (master `2.13.0`). No shared
infrastructure changed, so this does not move the base app version.