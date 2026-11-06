# sweep-d-22: a late group load published into a newer selection on M365 Groups and AD Group Management (sweep item 22)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 22: `M365GroupManagement.razor:445-449`
assigned `selectedGroup = details` after an await with no token, so a late load switched the page back to the previous
group and the next member operation (`:710 var group = selectedGroup;`) acted on it; the same publish-after-await shape at
`GroupManagement.razor:516`, `:593-594`, `:717-718`, `:997-999`, `:1146-1148`. kd-19 had fixed it in Self-Service Groups
only.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-d` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-d`, from master `6ea8963b`)
**Commit**: `98fd5044` (`fix(sweep-d, item 22): ...`) | **Reviewer**: implementer (candidate)

## Evidence

- M365 `SelectGroup` had no entry guard and no token; the View buttons' `disabled="@IsBusy"` is one round trip stale.
- AD Group Management checked `ReferenceEquals(selectedGroup, group)` BEFORE each refresh's await and published AFTER it
  (`if (ReferenceEquals(...)) memberList = await ...`), and `LoadMembers` published unconditionally. The reference check
  also passes after A -> Search -> A on the same `GroupInfo` object.

## Approach (as built) - the kd-19 shape

- **M365 Groups.** `selectionToken`, advanced by every writer of the selection: `SelectGroup` (takes the next value),
  `SearchGroups` and a successful `DeleteGroup`. `SelectGroup` refuses while `IsBusy` (above the raise, so no flag is
  stranded) and compares the token after each of its three awaits before publishing `selectedGroup` (details),
  `members` and `owners`, and in its catch before `searchError`; `isLoadingDetails` is lowered unconditionally (one
  load at a time by the guard; nothing else raises it). `RunMemberOpAsync` captures the token with the group and
  publishes its post-write reload (and `refreshError`) only while it is current.
- **AD Group Management.** `selectionToken` (separate from `selectToken`, which owns `isSelecting`'s lowering) advanced
  by `Search` and `SelectGroup`; `IsCurrentSelection(group, token)` = token unchanged AND `ReferenceEquals` (so the Close
  button, a markup lambda that nulls the selection, is still covered without a markup edit). `SelectGroup` refuses
  while `IsBusy`; its protection answer, `LoadMembers`' list, and the AddMember, RemoveMember, RemoveSelectedAsync and
  AddResolvedAsync refreshes are compared after the await; the two bulk handlers' outcome tables are published only for
  the current selection.
- Other publish-after-await on selection state searched on both pages: M365 `SaveGroup` publishes no selection state;
  AD `ResolvePasteAsync` publishes `resolution`, which resolves pasted identities and does not depend on the group (left).
  `opResult` / `operationResult` are page banners, not selection state (left).
- Service reads the pages call are `virtual` (M365: SearchGroups, GetGroupDetails, GetMembers, GetOwners; AD:
  SearchGroups, CheckTargetProtection, GetMembers, AddMember, RemoveMember) so tests hold them open; five source
  tripwires updated to the new signatures.
- **ClickGate.** All edits are in `@code`, below every registered markup line; no entry moved. `ExpectedLineCount`
  updated (GroupManagement 1158 -> 1216, M365GroupManagement 842 -> 899); ClickGate suite green.
- No help change: the pages behave as their help already says. GroupManagement `2.16.2`, M365GroupManagement `1.9.1`
  (that bump also covers sweep-d-30).

## Where the code goes beside the fix shape

- None. The AD token is combined with the existing reference check rather than replacing it, for the Close button.

## Guard proof (`GroupPagesSelectionTokenTests`)

| Mutation | Failing tests |
|---|---|
| G1 M365 SelectGroup guard removed | `M365_ASecondView_WhileTheFirstGroupLoads_IsRefused` |
| G2 M365 details publish unchecked | `M365_ALateLoad_AfterASearch_DoesNotPutTheGroupBack(details)` |
| G3 M365 members publish unchecked | `M365_ALateLoad_AfterASearch_DoesNotPutTheGroupBack(members)` |
| G4 M365 RunMemberOpAsync reload unchecked | `M365_AMemberOperationsReload_AfterASearch_PublishesNothing` |
| G5 M365 Search does not advance the token | `M365_ALateLoad_...` (both), `M365_AMemberOperationsReload_...` |
| G6 AD SelectGroup guard removed | `Gm_AManageClick_WhileAMemberLoadRuns_IsRefused` |
| G7 AD reference check only (no token) | `Gm_ALateMemberLoad_AfterTheSameGroupIsPickedAgain_PublishesNothing`, `Gm_AWritesLateRefresh_...` (all four) |
| G8 AD LoadMembers publish unchecked | `Gm_ALateMemberLoad_...` |
| G9 AD AddMember refresh unchecked after the await | `Gm_AWritesLateRefresh_...(AddMember)` |
| G10 AD RemoveMember refresh unchecked after the await | `Gm_AWritesLateRefresh_...(RemoveMember)` |
| G11 AD bulk refreshes unchecked after the await | `Gm_AWritesLateRefresh_...(RemoveSelectedAsync, AddResolvedAsync)` |

`Gm_AWritesRefresh_StillPublishes_WhileItsGroupIsTheSelection` is the control (passes before and after).

## Gates

Run once for the branch at its tip; see the sweep-d hand-back. Not run: a browser double-click on a deployed page.
