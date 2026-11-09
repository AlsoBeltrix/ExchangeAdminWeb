# rev-c3: Verify on the Protected principals pane overwrites edits made while its directory reads are pending

**Severity**: MEDIUM - silent loss of an unsaved edit. The pane keeps Remove and pattern-Add enabled while Verify awaits
the directory; when Verify returns it calls `ShowRules`, `SetRulesBaseline` and `RecountProtected` unconditionally, and a
successful direct action does the same, so a pattern added or a row removed during the wait vanishes with the dirty count
cleared. Module Configuration already withholds the snapshot replacement in this situation.
**Status**: Merged 78c03e49; closed by codex review E 2026-11-09 (CLOSED, `.agents/review/rev-e.result.txt`)
**Branch**: fix/rev-abc
**Commit**: 05342eba
**Lines** (master `14a94b50`): `Components/Pages/AdminSettings.razor:309`, `:1334-1342`, `:1474-1483`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review C, range `82a67e2f..fe3b86b8`, dispatched 2026-11-06 from master `14a94b50`; report `.agents/review/rev-c.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Either disable the pane's editing handlers while Verify or a direct action runs (busy guard, as item 42 did elsewhere), or
keep edits made during the await and skip the snapshot replacement when the pane became dirty, as ModuleConfig does. Add the
interleaving test. Mind ClickGate re-anchoring if page lines move.

## As built (fix/rev-abc `05342eba`)

Verified real: the pane's Add and Remove stay enabled during `VerifyProtectedAsync`'s directory await and
`RunVerifyActionAsync`'s write; both then called `ShowRules`/`SetRulesBaseline`/`RecountProtected` unconditionally.
- The Module Configuration shape, not a busy guard: the page has no busy guard on the pane's edit handlers, and disabling
  them would change what an admin can do while a long Verify runs. After the Verify await, a pane that became dirty is not
  replaced: the findings are not shown and the pane says "This pane was edited while Verify ran ...". After an action's
  write, the same: the edit is kept, the report cleared, "This pane was edited meanwhile; reload it to see what is stored".
  The rules version is not advanced, so a Save then refuses on the version check rather than overwrite the action's write.
- README Verify paragraph says so. AdminSettings 1.3.2. No ClickGate re-anchor: AdminSettings.razor is not in the registry
  ("not started"), and every edit is below the registered autocomplete lines.

## Guard proof

| Test | Fails on the old code (page stashed) |
| --- | --- |
| `DirectoryObjectVerifyPageTests.APatternAddedWhileVerifyReads_IsKept_AndTheFindingsAreNotShownAgainstIt` (directory read held open, pattern added) | yes |
| `DirectoryObjectVerifyPageTests.AnEditMadeWhileAVerifyActionWrites_IsKept_AndThePaneIsNotReplacedWithTheWrittenRules` (action held open) | yes |
