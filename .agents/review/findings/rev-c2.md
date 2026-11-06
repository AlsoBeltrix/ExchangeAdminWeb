# rev-c2: Replace with the same principal does not revoke its enforcement ignore

**Severity**: MEDIUM - an ignore the operator meant to undo survives. The repository's Replace removes the old row, adds the
picked one and keeps ignores that still name a row by identifier. Ignore a missing group, restore it, then Replace the row
by picking the restored group: same SID, so the Missing ignore survives, although the pane says an ignore is "undone by
Replace or Remove". If the group disappears again, enforcement suppresses the refusal.
**Status**: Admitted; awaiting fix on branch
**Lines** (master `14a94b50`): `Services/Storage/ProtectedPrincipalRepository.cs:285-289`, `:370`, `:436-439`; `Components/Pages/AdminSettings.razor:1184`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review C, range `82a67e2f..fe3b86b8`, dispatched 2026-11-06 from master `14a94b50`; report `.agents/review/rev-c.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Inside the replacement transaction delete the ignores that match the replaced row, whether or not the replacement keeps the
identifier. `AReplace_PutsThePickedObjectInTheRowsPlace_AndTakesTheOldRowsIgnores` needs the same-SID case.
