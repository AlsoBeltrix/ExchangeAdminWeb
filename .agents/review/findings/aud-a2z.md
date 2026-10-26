# aud-a2z: the synced room list's re-read checks objectGUID and DN but not that mail still carries the list address

**Severity**: LOW - correctness only. `Services/ConferenceRoomService.cs` about 2770 (after aud-a2e): the group re-read before the on-prem add confirms objectGUID and DN but not that `mail` still holds the room-list address, so with a lagging global catalog a room can be added to a group that no longer carries the list.
**Status**: In progress - admitted 2026-10-23 evening from the aud-a2e agent's hand-back; inside the released scope
**Branch**: `fix/a2e` (worktree `D:\source\ExchangeAdminWeb-wt\a2e`), continuing from `fefb3e6a`
**Commit**: -
**Reviewer**: implementer (aud-a2e agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `3e43b35b`. Trigger: the list's address is moved off the group between resolution and add on a lagging GC. Predicted failure: membership written to a group that is no longer the room list; the audit row names the list the operator saw.

## Approach

Written before the code, against `ebd66b91` (the aud-a2e commit, merged as `3e43b35b`). The brief: the re-read also
compares `mail` (case-insensitive) with the resolved list address and refuses on a mismatch with "Nothing was written.";
a stand-in that answers a changed mail; mutation: the comparison removed.

**Where the binding stops being checked.** `Services/ConferenceRoomService.cs`: a synced list's on-prem group is found by
the list's primary address alone (aud-a2k) - `ResolveAdGroupGuid` searches the forest's global catalog with
`Get-ADGroup -Filter "mail -eq '<address>'"` (`:2715-2716`) - and the one match is then re-read in its own domain by
`ReadAdGroupInItsDomain` (`:2751`), because a catalog is a lagging copy (aud-a2j). That re-read (`:2758-2762`) asks for the
default property set only, and what it confirms (`:2770`) is the objectGUID and DN the catalog returned. It never asks
whether the group still carries the address it was found by. A catalog behind an address move - the address taken off
group A and put on group B, the catalog not caught up - answers A; A's own domain confirms A's objectGUID and DN; and the
preflight (`:2212`) and the add (`:2347`) both bind the list to A and write the room into it.

**The change**, confined to the re-read:

- it asks for `mail` as well (`-Properties mail`; outside the default set);
- after the objectGUID/DN check, the `mail` its own domain answers is compared with the list's address, trimmed and
  case-insensitively, as an address is matched. Not equal - another address, or none - refuses: "does not carry the
  list's address ... in its own domain ... Refusing to write.", naming the list, the address and what the domain holds.

The refusal comes back through `ResolveAdGroupGuid` to both callers unchanged in shape: the preflight, before any write,
ends it "Nothing was written." (`:2223`, its resolver-refusal path) and the row writes nothing; the add, after Set-Place,
returns it as it is - the room is not added on-prem, and the message does not claim nothing was written (aud-a2r's rule
for refusals after Set-Place). Nothing else moves: the snapshot (aud-a2e) keeps the list's address as its mail, which the
comparison has now proven the group holds.

**Files to change.** `Services/ConferenceRoomService.cs` (`ReadAdGroupInItsDomain` only);
`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` (the stand-in re-read answers `mail` when asked for it, from a new
`DirectoryGroupMailInItsDomain` when a test moves it, else the address the search matches); a new
`ExchangeAdminWeb.Tests/ConferenceRoomListGroupAddressTests.cs`; `Modules/ModuleCatalog.cs` (2.15.0 -> 2.16.0); this
record.

**Mutations planned.** The comparison removed; the comparison made case-sensitive; the re-read's `-Properties mail`
dropped.

## Files changed

- `Services/ConferenceRoomService.cs` - `ReadAdGroupInItsDomain` asks for `mail` and compares it with the list's address
  after the objectGUID/DN check, refusing on a mismatch.
- `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` - the stand-in re-read answers `mail` when asked for it, from
  `DirectoryGroupMailInItsDomain` where a test moves the address, else the mail the catalog search matches.
- `ExchangeAdminWeb.Tests/ConferenceRoomListGroupAddressTests.cs` - new.
- `Modules/ModuleCatalog.cs` - Conference Rooms `2.16.0`.

## Guard proof

| Test (`ConferenceRoomListGroupAddressTests`) | What it pins |
| --- | --- |
| `AGroupWhoseOwnDomainNoLongerCarriesTheListsAddress_IsRefusedBeforeAnyWrite` | the finding: the catalog still matches the group by the list's address, its own domain has moved it elsewhere, the objectGUID and DN check out - and the row refuses before any write, naming the list, the address searched and what the domain holds, logged once; no membership read is even made |
| `AGroupWhoseOwnDomainHoldsNoMail_IsRefusedBeforeAnyWrite` | the address absent refuses too, worded for it |
| `TheOnPremAdd_DoesNotAddTheRoomToAGroupTheListsAddressHasLeft` | the add's own path, called directly as the row calls it after Set-Place: no `Set-ADGroup`, nothing recorded written, and the refusal does not claim nothing was written |
| `TheListsAddressInAnotherCase_IsStillTheListsGroup` | the direction the rule must not break: an address matched ignoring case is the same address, and the room is added as before |

**Mutations**, each at its call site, applied by a script that refuses unless the old text occurs exactly once and the
md5 changes; rebuilt; the named tests run; restored from the saved bytes with a fresh mtime; rebuilt; run again.
`ConferenceRoomService.cs` md5 before = restored = `9e88fe06a807edb24574d3df3eda5886` for all three.

| # | Mutation | md5 mutated | Named tests | Mutated / restored |
| --- | --- | --- | --- | --- |
| Z1 | the comparison removed - the re-read confirms the objectGUID and DN and nothing else | `aa9893eb2116e3e4039ee6d598380149` | the three refusals | FAIL (3) / PASS (3) |
| Z2 | the comparison made case-sensitive | `c00b435e0b96d358c66fdac543f6f4f1` | `TheListsAddressInAnotherCase_IsStillTheListsGroup` | FAIL / PASS |
| Z3 | the re-read stops asking for `mail`, so the comparison can never see it | `c31724a5e4d6c85d53d20e899cf621b9` | the finding's refusal and the case control | FAIL (2) / PASS (2) |

## Coder dispute

None.

## Known gaps

**What the comparison does and does not prove.** It proves the group its own domain holds at that DN still carries the
address the catalog matched it by. It does not make the binding immutable: Exchange exposes no attribute tying a synced
list to its on-premises group, so the address remains the binding (aud-a2k), and an address moved between this read and
the write is outside what any read can see. The window is now one read wide instead of a catalog replication interval.

**Source-level and fake-driven, not live.** *Assumption, unverified (no live call):* that `Get-ADGroup -Properties mail`
returns the attribute under the name `mail`, as the other reads in this file assume. If it did not, every synced-list
row would refuse ("its mail there is not set") - loudly, on the first dev check, and in the refusing direction.

**Who this refuses that was written before - operator-visible, refusing direction:** a synced room list whose on-prem
group no longer carries the list's address in its own domain. The likely cause is a global catalog that has not caught
up with an address move; the refusal says so and names both addresses, and a re-run after replication succeeds.

## Reviewer comments

Candidate raised by the aud-a2e implementer in its hand-back; admitted by the supervisor 2026-10-23 evening.
Implemented under the owner's 2026-10-21 go, released by the 2026-10-23 evening ruling, by an Opus implementation agent
on `fix/a2e`. Independent review pending.

## Closeout

Fixed on branch, not merged, not pushed. Module-scoped: Conference Rooms `2.16.0`. No shared infrastructure changed, so
this does not move the base app version.

## Reviewer comments (supervisor, cr-batch-10)

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base ebd66b91 head 26e8cd9b (merged 3bfdc1af); 2026-10-26; capability proof succeeded (AGENTS.md read, git show --stat 26e8cd9b). One candidate, admitted as kd-68 (against aud-a2ab). Codex confirmed clean: the missing-list flow returns before Set-Place or any creation; the bulk path maps the refusal to a failed ROW, not a failed batch; `ReadRoomList` treats only a structural not-found as absence; the mail comparison is trimmed and case-insensitive and refuses an unset mail; the ambiguity text is centralised on every service Ambiguous path. Artefacts: `.agents/review/cr-batch-10.result.txt`, `.stream.log`, `prompt-cr-batch-10.md`.
