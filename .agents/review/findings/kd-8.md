# kd-8: Self-Service Groups - a previous group's operation can publish its banner into another group's view

**Severity**: LOW - misleading UI. In `Components/Pages/SelfServiceGroups.razor` an operation started on group A completes after the operator has opened group B and sets the page banner, so B shows A's result. Not re-verified at the last state pass; the agent verifies before writing.
**Status**: Verified on branch; awaiting merge and codex review (released by the owner's 2026-10-23 evening ruling; approved under the 2026-10-21 go)
**Branch**: `fix/kd7-kd8` (worktree `D:\source\ExchangeAdminWeb-wt\kd7-kd8`, from master `3e43b35b`)
**Commit**: the kd-8 commit on `fix/kd7-kd8`, after `f0ce2431`
**Reviewer**: implementer (recorded in `.agents/state.md` "Known defects")

## Evidence

Locate the completion path that assigns the banner without comparing the group it ran for with the group now shown. Trigger: start an operation on A, open B before it finishes. Predicted failure: B shows "Added ... to A" or an error about A.

## Approach

Re-read at `3e43b35b`; every line below is that commit's.

### Verification of the record's claim

True at the code level, and the record's source says so: `ClickGateRegistry`'s SelfServiceGroups remarks ("Not fixed,
and not a gating problem") record that an operation still in flight when another group is opened publishes its
`changeResult` banner and its `bulkOutcome` table into the new group's view.

- Every completion publishes with no group test. `RemoveListedMember` (`SelfServiceGroups.razor:642-679`) at `:667`;
  `ChangeMember` (`:883-923`) at `:907`; `RemoveSelectedAsync` (`:815-881`) at `:841` and `:850` (its two early
  refusals) and `:863-868` (`bulkOutcome`, `bulkOutcomeTitle`, `changeResult`); `AddResolvedAsync` (`:1067-1135`) at
  `:1091`, `:1100` and `:1113-1117`. Each is after the handler's first await. The same handlers' reloads already test
  `ReferenceEquals(selected, group)` (`:677`, `:879`, `:921`, `:1133`); the publishes do not.
- What lets another group be opened meanwhile: Back to groups (`:205`) is ungated by design, and `SelectGroup`
  (`:542-549`) has no server-side refusal, so the only thing standing between a running operation and a second group's
  view is the Manage members gate (`disabled="@IsManageBusy"` at `:128`-`:129` and `:179`-`:180`) - markup.
- Reachability, stated as measured rather than as recorded. The registry says the gate "narrows the window to one round
  trip but cannot close it"; I could not construct that window from ordinary clicks. Every mutating handler raises
  `isChanging` synchronously, before its first await, and refuses when `selected` is null (`:644`, `:886`, `:822`,
  `:1072`), so every render that can show the Manage members buttons while an operation runs is computed with
  `IsManageBusy` true, and a Manage click can only be dispatched from such a render. The trigger is therefore a
  dispatch the markup gate does not stop - a programmatic click, the kd-4 / kd-7 class of markup-only enforcement -
  not a race between honest clicks. LOW stands; it is the server-side half of a markup-only gate.

### What changes

- One helper, `ShowFor(group, outcome, table = null, tableTitle = "")`, in `@code` above `LoadOwnedGroups`: it writes
  `changeResult` (and, for a batch, `bulkOutcome` / `bulkOutcomeTitle`) only while `ReferenceEquals(selected, group)`,
  and returns the outcome either way, so the activity still completes with it. Every publish listed above becomes
  `completed = ShowFor(group, ...)`, in place. The audit rows and admin emails are written before it and are untouched.
- Left as they are, deliberately: the input clears beside those publishes (`memberIdentity` `:909`, `selectedGuids`
  `:863`, `pasteText` / `resolution` `:1120-1121`). A group switch resets the per-group ones (`SelectGroup`,
  `LoadMembers`, `ClearBulkState`) and the other group's inputs are disabled for the whole operation, so they hold
  nothing of that group's to lose; and the paste text is not per group by design (`:509-510`).
- Not in scope, reported as a candidate instead: `LoadMembers` (`:566-608`) publishes `groupMembers` and
  `memberLoadError` and lowers `isLoadingMembers` with no test either, and THAT is reachable from ordinary clicks - two
  Manage members clicks on different groups inside one round trip.
- Line keys: every registered line is in markup at 398 or earlier and every edit is in `@code` below 453, so only
  `ExpectedLineCount` moves. The registry remarks paragraph that records the defect as unfixed is rewritten.
- Guard: `SelfServiceGroupsResultScopeTests` drives the four handlers by reflection, holds each at its first
  authorization re-check, switches groups through the page's own `BackToGroups` and `SelectGroup`, then releases it.
- Mutation: the comparison removed (`ReferenceEquals(selected, group)` -> `true` in `ShowFor`).
- Module `SelfServiceGroups` 1.11.3 -> 1.12.0; no base app bump.

## Files changed

- `Components/Pages/SelfServiceGroups.razor` - `ShowFor` above `LoadOwnedGroups`; the eight publishes in
  `RemoveListedMember`, `ChangeMember`, `RemoveSelectedAsync` and `AddResolvedAsync` go through it. 1211 -> 1244 lines,
  every added line below 453.
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` - SelfServiceGroups `ExpectedLineCount` 1211 -> 1244 (diff map: 19 line
  keys, none moved, none unmapped); the remarks paragraph that recorded this defect as "Not fixed" now records the fix.
  Nothing else in the entry changed: the dismiss at 300's ConditionThatKeepsItTrue still holds (no await between
  `ShowFor` assigning `changeResult` and the reload test reading it), and no handler reads `bulkOutcome`.
- `ExchangeAdminWeb.Tests/SelfServiceGroupsResultScopeTests.cs` - new.
- `Modules/ModuleCatalog.cs` - `SelfServiceGroups` 1.11.3 -> 1.12.0, with a version note.
- This record.

## Guard proof

`SelfServiceGroupsResultScopeTests` (12 cases) drives the four handlers by reflection over a real `AuditService`, a
substituted `EmailService`, a real `SelfServiceGroupService` with no credential (so `SelectGroup`'s member read fails at
once without a directory), and a substituted authorization service whose first answer is a task the test completes.
Each operation is held at that first re-check; the test then calls the page's own `BackToGroups` and
`SelectGroup(another group)`, and releases it. Six operations: `ChangeMember` and `RemoveListedMember` refused at the
held check; `RemoveSelectedAsync` and `AddResolvedAsync` each both ways - refused at their upfront check (the early
refusal publish), and let through it so their rows run, each refused at its own re-check, and the summary publishes the
banner and the table.

- `AnOperationThatFinishesAfterTheOperatorOpenedAnotherGroup_PublishesNothingIntoThatGroupsView` (6): the other group
  is still selected; `changeResult` and `bulkOutcome` are null; the operation's own Failed audit row for the group it
  ran for is there.
- `AnOperationOnTheGroupStillShown_PublishesItsOutcome` (6, the control): with no switch the banner is published - the
  batch's "of 1 member(s) were not ..." with its one-row table, or "Authorization denied."

| Mutation | Result |
| --- | --- |
| M1, the record's: the comparison removed - `ShowFor`'s `if (ReferenceEquals(selected, group))` -> `if (true)` (md5 `e8dcbfe0` -> `cc082f2f`) | FAIL, 6 of 12 - every switch case: `Assert.Null() Failure: Value is not null` (the left group's outcome in the new group's view); the six controls pass |
| M2: `AddResolvedAsync`'s summary reverted to its old direct publish (md5 -> `bc0633df`) | FAIL, 1 of 12 - that case alone, so each site is pinned on its own |

Restored byte-exact with a fresh mtime (md5 `e8dcbfe0`), rebuilt, rerun: 12 passed; with the SelfServiceGroups,
ClickGate, ProgressRegistry, GroupBulkActions and GroupMemberNesting groups, 464 passed.

## Coder dispute

None on the defect. On its reachability, see the Approach: the registry's "one round trip" window could not be
reproduced from ordinary clicks; the fix is the server-side half of a markup-only gate.

## Known gaps

- **Candidate defect, not fixed here** (different handler, needs the monotonic-token treatment `GroupManagement`'s
  `SelectGroup` uses, `GroupManagement.razor:449`, `:485`): `LoadMembers` (`SelfServiceGroups.razor:566-608`) publishes
  `groupMembers` (`:596`) and `memberLoadError` (`:601`) and lowers `isLoadingMembers` (`:605`) with no test that its
  group is still the one shown. Two Manage members clicks on different groups inside one round trip - both buttons are
  enabled in the same browse render - start two loads; whichever finishes last fills the member table, so the second
  group's view can list the first group's members (with Remove buttons acting on the second group), and the first
  load's `finally` re-enables the manage view while the second is still reading. One server-side line closes it and the
  op-banner case together: `if (IsManageBusy) return;` at the top of `SelectGroup`.
- The input clears beside the publishes are left as they are (Approach): they hold nothing of another group's when a
  switch happens.
- No live check: no directory call was made; rendering is not exercised (no bUnit harness).

## Reviewer comments

None yet (codex review pending, dispatched by the supervising session).

## Closeout

Pending: merge into master, codex review, branch deletion.
