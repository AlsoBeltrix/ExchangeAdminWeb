# rev-i2: the same entry is reported twice for a folder not created yet

**Severity**: LOW - wording only, not fail-open. `Get-ToolFolderInheritedWriteExposure` judges a not-yet-created
folder by the nearest existing ancestor twice - the rev-f1 parent walk (can the principal replace the new folder?) and
`-InheritableOnly` (would it hold a write right inside it?) - and an entry that applies to the ancestor and is
inheritable came back from both, so the plan line named "<group> holds FullControl on <parent>" twice.
**Status**: Merged 350c63b5 (message-only fixes; no review round, owner verifies on the next plan run)
**Branch**: `fix/rev-i` (worktree `D:\source\ExchangeAdminWeb-wt\rev-i`, from master `79af56e7`)
**Commit**: `4bdfe60f` (`fix(rev-i2): a folder not created yet names an entry on its parent once, not twice`) | **Implementer**: Claude (supervised agent)
**Reviewer**: owner (live plan run 2026-11-09)
**Range reviewed**: live `deploy-pipeline.ps1 -Dev -PlanOnly` output after rev-f1/rev-g1 (master 79af56e7)

## Evidence (master 79af56e7)

- `tools/ToolFolderAcl.psm1` `Get-ToolFolderInheritedWriteExposure` ended with both
  `Get-ParentChainExposure -FirstParent $ancestor ...` and `Get-ObjectWriteExposure -LiteralPath $ancestor ... -InheritableOnly`.
- The existing test "judges a folder not created yet by what it would inherit" pinned the duplicate: an `(OI)(CI)M`
  grant gave `ParentGrant|S-1-5-11` and `Grant|S-1-5-11`, commented "so it is named twice".

## Choice: merge, not suppress

Both reads are kept and run in full. A Grant from the inherited read that matches a ParentGrant on the same folder
(same Path, Sid, Rights text and Inherited) is folded into that ParentGrant, which gains `AlsoInherited = $true`, and
`Format-ToolFolderWriteExposure` then says "..., a folder above the app folder, so can replace it, and the new folder
would inherit it". Merging was chosen over dropping the inherited line because the two answers differ and whoever
narrows the parent's rights needs to know the entry also reaches the new folder; one line carries both. Every other
finding of either read is returned unchanged, so the refusal still names every principal and folder that caused it, and
it still refuses (the merged finding is a finding). An inherit-only generic copy on the same folder (the owner's
Authenticated Users case) has different rights and stays its own line. The existing-folder read
(`Get-ToolFolderWriteExposure`) is untouched: there the parent's entry and the app folder's inherited copy sit on
different folders and are two different findings.

## As built

- `tools/ToolFolderAcl.psm1` `Get-ToolFolderInheritedWriteExposure`: collects the parent-walk findings, folds matching
  inherited grants into them, then emits the parent findings and the unmatched grants (same order as before). Its
  description says so. `Format-ToolFolderWriteExposure` appends the clause when the property is present and true
  (checked through `PSObject.Properties`, so other objects are unaffected under strict mode).
- Help: no doc or README quotes this wording; none changed.
- Tests: the pinned-duplicate test now expects one `ParentGrant|S-1-5-11|True`; new Describe (3 tests): an untrusted
  `(OI)(CI)F` on the parent of a not-yet-created folder yields exactly one finding and the exact line, and
  `Resolve-ConfigDbTool -PlanOnly` still refuses; a parent with `(DC)` for one principal and `(OI)(CI)(IO)M` for another
  keeps both lines; an existing app folder under an `(OI)(CI)F` parent still yields the ParentGrant and the app folder's
  Grant, neither carrying AlsoInherited.

## Guard proof (`tests/ps/ToolFolderAcl.Tests.ps1`)

| Mutation | Failing tests |
| --- | --- |
| Merge disabled (`if ($false)`) | 2: the updated "judges a folder not created yet by what it would inherit" and "names an untrusted FullControl entry on the parent once" |
| Merged flag not printed | 1: "names an untrusted FullControl entry on the parent once" (line lacks the clause) |
| Every inherited grant suppressed (over-suppression, the fail-open shape) | 2: "keeps both lines ..." and the rev-i1 inherit-only generic entry test |
| Restored | file 64 of 64 |
