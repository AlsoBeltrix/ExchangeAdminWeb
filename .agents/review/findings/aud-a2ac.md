# aud-a2ac: an ambiguous room-list lookup is treated as a missing list

**Severity**: MEDIUM - a fail-closed gap. `Services/ConferenceRoomService.cs` about 402 (`FindRoomList`) resolves the canonical and legacy names with `Get-DistributionGroup -ErrorAction SilentlyContinue` through `ExchangeServiceBase.InvokeOptional` (about 326), which returns the empty collection and clears the error stream on a non-terminating ambiguity or lookup error. The row then falls through to the legacy name or to `Exists=false` and can proceed to `Set-Place`, write a legacy list, or try to create a list instead of refusing. Trigger: an ambiguous identity (two groups answer the name) or a lookup error. Predicted failure: a write against the wrong list or a partial row.
**Status**: In progress - admitted 2026-10-23 evening from codex cr-batch-7 on aud-a2e; inside the released scope
**Branch**: `fix/a2e` (worktree `D:\source\ExchangeAdminWeb-wt\a2e`), continuing from `c38defa0`
**Commit**: -
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base eb017102 head ebd66b91 (merged 3e43b35b); 2026-10-23 ~20:0xZ; capability proof: AGENTS.md read and git show --stat ebd66b91 both succeeded

## Evidence

Codex cr-batch-7 finding 2 at `3e43b35b`.

## Approach

Written before the code, against `c38defa0` (this branch's aud-a2ab commit). The brief: resolve with `-ErrorAction Stop`,
treat only a confirmed ObjectNotFound as absence, and make any other error or an ambiguity a preflight refusal before any
mutation.

**What the empty collection means today.** `FindRoomList` asks for the canonical name and then the legacy name with
`-ErrorAction SilentlyContinue` through `InvokeOptional` (`Services/ExchangeServiceBase.cs:326`), whose `finally` clears
the error stream. Every failure therefore arrives as `Count == 0` and is read as "this list does not exist": an
ambiguous identity (Exchange's "matches multiple entries"), a throttled or failed lookup, a permission error. The row
then tries the legacy name and, failing that, reports `Exists = false`. Before aud-a2ab that meant Set-Place and a
created list; after it the row refuses, but for the wrong reason and with the wrong advice - "create the room list",
when the list exists twice over or the lookup simply failed. A legacy list can still be written to because the canonical
read failed rather than answered.

**The rule.** Only Exchange saying "no such object" is absence. The base already draws that line for the same reason and
from the error record's structure, never its text: `ReadUnlessNotFound` / `IsNotFound`
(`Services/ExchangeServiceBase.cs:373`, `:409`, from aud-a23h), which return the empty collection for a confirmed
ObjectNotFound and rethrow everything else.

- each name is read by one new private `ReadRoomList`: `Get-DistributionGroup -Identity <name> -ErrorAction Stop`
  through `ReadUnlessNotFound`. One object is the list; none is an affirmative absence and the search moves on to the
  legacy name, exactly as today; anything else throws;
- more than one object throws as well. Exchange refuses an ambiguous `-Identity` with an error rather than answering
  twice, so this is the defensive half of the same rule: two answers are not a list;
- the throw reaches the row at the resolve (`SetRoomMetadataAndListAsync`, the `ResolveRoomListTargetAsync` call), which
  is before Set-Place and before every other write. It is caught there and turned into a row refusal - the message names
  the list and what Exchange reported, ends "Nothing was written.", is logged once where it is decided, and carries a
  step of its own ("Room list resolution") so the operator can tell it from the preflight's refusals.

**Deliberately unchanged.** The preview's stray-city-list probe keeps `SilentlyContinue`: it exists to raise a warning
beside a row, writes nothing and decides nothing. `ResolveRoomListAsync` (the preview) shares `FindRoomList`, so an
ambiguous or failing lookup now throws there too; the page already catches it and falls back to the canonical name
(`Components/Pages/ConferenceRooms.razor:1212`), so the preview degrades as it did and the apply refuses.

**Files to change.** `Services/ConferenceRoomService.cs` (`FindRoomList`, the new `ReadRoomList`, one refusal builder,
and the try/catch at the resolve); `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` (a missing list reports
`-Category ObjectNotFound`, as Exchange does and as `IsNotFound` reads; two knobs - `AmbiguousLists`, a name the lookup
refuses as matching several recipients, and `ListsAnsweringTwice`, a name it answers twice); a new
`ExchangeAdminWeb.Tests/ConferenceRoomListLookupTests.cs`; `Modules/ModuleCatalog.cs` (2.14.0 -> 2.15.0); this record.

**Mutations planned.** The ambiguity branch collapsed back to absent (`ReadUnlessNotFound` back to `InvokeOptional` with
`SilentlyContinue`); the `Count > 1` throw dropped; the row's catch dropped, so the throw escapes instead of refusing.

## Files changed

- `Services/ConferenceRoomService.cs` - `FindRoomList` reads each name through the new `ReadRoomList`
  (`-ErrorAction Stop` + the base's `ReadUnlessNotFound`, and more than one answer throws);
  `RoomListNotResolvedRefusal`; the row catches the throw at `ResolveRoomListTargetAsync`, before every write, and
  refuses with a step of its own ("Room list resolution").
- `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` - a missing list is reported with `-Category ObjectNotFound`, as
  Exchange reports it and as `IsNotFound` reads it; `AmbiguousLists` and `ListsAnsweringTwice`.
- `ExchangeAdminWeb.Tests/ConferenceRoomListLookupTests.cs` - new.
- `Modules/ModuleCatalog.cs` - Conference Rooms `2.15.0`.

## Guard proof

| Test (`ConferenceRoomListLookupTests`) | What it pins |
| --- | --- |
| `AnAmbiguousRoomListName_IsRefusedBeforeAnyWrite_NotReadAsAMissingList` | the finding: Exchange's "matches multiple entries" refuses the row before any write, carries its words, never asks for the legacy name, and is logged once |
| `AnAmbiguousLegacyRoomListName_IsRefusedBeforeAnyWrite` | the same at the second name, which the search only reaches because the first was an affirmative absence |
| `ARoomListLookupThatCouldNotBeMade_IsRefusedBeforeAnyWrite` | any other failure - a throttle, a permission - is not absence either |
| `ARoomListNameAnsweredTwice_IsRefusedBeforeAnyWrite` | the defensive half: two answers are not one list |
| `AConfirmedNotFound_IsStillAbsence_AndTheSearchMovesOnToTheLegacyName` | the direction the rule must not break - an affirmative not-found is still an answer, and the legacy list is found and used as before |
| `BothNamesConfirmedNotFound_IsABuildingWithNoRoomList` | both absent is the aud-a2ab refusal, by its own reason and its own step - not this one |
| `TheHarness_ReportsAMissingListAsExchangeDoes_AndRefusesAnAmbiguousOne` | the stand-in itself, through `ExchangeServiceBase.IsNotFound`: a missing list reads as not-found, an ambiguous one never does, a twice-answered name returns two |

**Mutations**, each at its call site, each applied by a script that refuses unless the old text occurs exactly once and
the md5 changes; rebuilt; the named tests run; the file restored from the saved bytes with a fresh mtime; rebuilt; run
again. `ConferenceRoomService.cs` md5 before = restored = `b7087943c0dc81f5848edd9cf109ac1c` for all three.

| # | Mutation | md5 mutated | Named tests | Mutated / restored |
| --- | --- | --- | --- | --- |
| AC1 | the ambiguity branch collapsed back to absent (`SilentlyContinue` through `InvokeOptional` again) | `f10a787d94fe4fb66072d46091e86fcf` | the three refusals and the not-found control | FAIL 3 of 4, PASS 1 / PASS 4 |
| AC2 | the `Count > 1` throw dropped | `12c2a135b8ea0aa2af6b57149bf94568` | `ARoomListNameAnsweredTwice_IsRefusedBeforeAnyWrite` | FAIL / PASS |
| AC3 | the row's catch dropped, so the throw escapes instead of refusing | `d7e017bde02c06f4e0b6e14d7f4da66e` | the ambiguous and the failed-lookup refusals | FAIL (both) / PASS (both) |

Under AC1 the control (`AConfirmedNotFound_...`) keeps passing: the mutation makes every failure absence, and that test
asserts absence. It is the discrimination the set is built to show.

## Coder dispute

None.

## Known gaps

**Deliberately unchanged.** The preview's stray-city-list probe keeps `-ErrorAction SilentlyContinue`: it raises a
warning beside a preview row, writes nothing and decides nothing. An ambiguous city-named list therefore still goes
unmentioned, as before.

**The preview degrades rather than reports.** `ResolveRoomListAsync` shares `FindRoomList`, so an ambiguous or failing
lookup now throws there too; the page already catches it and falls back to the canonical name
(`Components/Pages/ConferenceRooms.razor:1212`), so such a row previews as "new" and the apply then refuses. Telling the
operator at preview time is a page edit, outside this finding's lines and on a file a second agent is editing in place.

**Source-level and fake-driven, not live.** *Assumption, unverified (no live call):* that Exchange Online's
`Get-DistributionGroup -Identity` reports "no such object" in one of the shapes `IsNotFound` reads - the ObjectNotFound
category, a reason ending `NotFoundException`, or such a name in the `FullyQualifiedErrorId`. The assumption fails
closed: if the module uses none of them, a Building with no room list is refused as "could not be resolved" instead of
"does not exist" - a wrong message on an already-refusing path (since aud-a2ab), never a write. The same assumption is
already carried by aud-a23h and aud-a23o for `Get-Mailbox` and the migration reads.

**Who this refuses that was written before - operator-visible, refusing direction:** a row whose building's list name
is ambiguous, or whose lookup fails for any reason other than an affirmative absence, is refused with nothing written.
Before, the canonical failure sent the row to the legacy name - which could be written to - and a failure of both sent
it on as a building with no list.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh`, cr-batch-7 finding 2 over `3e43b35b`. Implemented
under the owner's 2026-10-21 go, released by the 2026-10-23 evening ruling, by an Opus implementation agent on
`fix/a2e`. Independent review of the fix pending.

## Closeout

Fixed on branch, not merged, not pushed. Module-scoped: Conference Rooms `2.15.0`. No shared infrastructure changed -
`ReadUnlessNotFound` and `IsNotFound` are consumed as they are - so this does not move the base app version.