# rev-b3: a protected OU rule is evaluated by its DN label for group write targets, so renaming or moving the OU drops protection

**Severity**: HIGH - fail-open. For group write targets `CheckOuMatches(..., ouIdsByLabel: true)` compares the target's DN
against the OU row's stored DN label. Protect an OU by objectGUID, then rename or move it: its groups keep their identities but
gain new DNs, the label no longer matches, and Group Management's membership write proceeds (`Set-ADGroup`).
**Status**: Admitted; awaiting fix on branch
**Lines** (master `145f513f`): `Services/ProtectedPrincipalService.cs:514`, `:2001-2005`; `Services/GroupManagementService.cs:671`, `:739`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review B, range `5c4c2970..82a67e2f`, dispatched 2026-11-06 from master `145f513f`; report `.agents/review/rev-b.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Evaluate OU rules for group targets by ancestor objectGUIDs (read the target's parent chain once, as the user path already
does), refusing on an incomplete read. `TheWriteTargetCheck_ComparesAnOuGuidRowByItsLabel` pins the defect and must change;
the existing OU-rename test for user membership is the model.
