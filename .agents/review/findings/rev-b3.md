# rev-b3: a protected OU rule is evaluated by its DN label for group write targets, so renaming or moving the OU drops protection

**Severity**: HIGH - fail-open. For group write targets `CheckOuMatches(..., ouIdsByLabel: true)` compares the target's DN
against the OU row's stored DN label. Protect an OU by objectGUID, then rename or move it: its groups keep their identities but
gain new DNs, the label no longer matches, and Group Management's membership write proceeds (`Set-ADGroup`).
**Status**: Merged 78c03e49; closed by codex review E 2026-11-09 (CLOSED, `.agents/review/rev-e.result.txt`)
**Branch**: fix/rev-abc
**Commit**: 07005c0d
**Lines** (master `145f513f`): `Services/ProtectedPrincipalService.cs:514`, `:2001-2005`; `Services/GroupManagementService.cs:671`, `:739`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review B, range `5c4c2970..82a67e2f`, dispatched 2026-11-06 from master `145f513f`; report `.agents/review/rev-b.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Evaluate OU rules for group targets by ancestor objectGUIDs (read the target's parent chain once, as the user path already
does), refusing on an incomplete read. `TheWriteTargetCheck_ComparesAnOuGuidRowByItsLabel` pins the defect and must change;
the existing OU-rename test for user membership is the model.

## As built (fix/rev-abc `07005c0d`)

Verified real: `CheckWriteTarget` called `CheckOuMatches(..., ouIdsByLabel: true)`, a DN-suffix compare against the row's
stored label.
- `CheckWriteTarget` keeps its directory-free rules (users, patterns, group targets, unconverted rows) and no longer
  evaluates OUs; `CheckOuMatches`/`IsAtOrBeneath` are gone.
- New `virtual Task<ProtectedPrincipalResult> CheckWriteTargetAsync(target)`: `CheckWriteTarget`, then the ou_guid rows by
  `ProtectedOuMembership.Evaluate` (the user path's evaluator: the group's containers by DN in its own domain, objectGUIDs
  compared) in a warm session under the directory-read credential, inside `WithDirectoryReadCredentialAsync` (rev-a2) and
  a timed check. An incomplete read, no credential, the throttle, or a group with no DN refuses with
  `WriteTargetContainersNotRead` unless another rule matched.
- `ProtectedPrincipalServicing.ForWriteTarget` -> `ForWriteTargetAsync`, awaiting it; the four Group Management gate sites
  await it (the member-listing gate's `Task.Run` lambda became async).
- Cost: one directory read (plus the credential's operation fetch) per write-target check, only when ou_guid rows exist.
- Help: README Rename paragraph and the pane's "name changed" badge no longer describe a name fallback. GroupManagement 2.16.5.

## Guard proof

| Test | Fails on the old code / mutation (`CheckWriteTargetAsync` returns the directory-free answer) |
| --- | --- |
| `GroupWriteTargetOuTests.AGroupInAProtectedOu_StaysProtected_AfterTheOuIsRenamedOrMoved` (rename and move rows) | yes |
| `GroupWriteTargetOuTests.AGroupInAnotherOuThatTookTheStoredName_IsNotProtected` | old code protects it by label: yes |
| `GroupWriteTargetOuTests.AContainerReadThatFails_RefusesTheWrite_NamingWhy` | yes |
| `GroupWriteTargetOuTests.TheSharedGate_AsksTheFullCheck_WithItsOuRead` | tripwire |
| `ProtectedPrincipalIdentifierTests.TheWriteTargetCheck_NeverComparesAnOuGuidRowByItsLabel_AndRefusesWhenItCannotReadTheContainers` (replaces `..._ComparesAnOuGuidRowByItsLabel`) | yes |
| `ProtectedGroupWriteTargetTests.OuRule_IsNotComparedByItsLabel_InTheDirectoryFreePart` | yes |
