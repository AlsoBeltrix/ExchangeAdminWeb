# rev-c3: Verify on the Protected principals pane overwrites edits made while its directory reads are pending

**Severity**: MEDIUM - silent loss of an unsaved edit. The pane keeps Remove and pattern-Add enabled while Verify awaits
the directory; when Verify returns it calls `ShowRules`, `SetRulesBaseline` and `RecountProtected` unconditionally, and a
successful direct action does the same, so a pattern added or a row removed during the wait vanishes with the dirty count
cleared. Module Configuration already withholds the snapshot replacement in this situation.
**Status**: Admitted; awaiting fix on branch
**Lines** (master `14a94b50`): `Components/Pages/AdminSettings.razor:309`, `:1334-1342`, `:1474-1483`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review C, range `82a67e2f..fe3b86b8`, dispatched 2026-11-06 from master `14a94b50`; report `.agents/review/rev-c.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Either disable the pane's editing handlers while Verify or a direct action runs (busy guard, as item 42 did elsewhere), or
keep edits made during the await and skip the snapshot replacement when the pane became dirty, as ModuleConfig does. Add the
interleaving test. Mind ClickGate re-anchoring if page lines move.
