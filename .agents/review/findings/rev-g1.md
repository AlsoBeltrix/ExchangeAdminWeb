# rev-g1: the parent-folder ACL walk does not stop at a UNC share root

**Severity**: MEDIUM - false refusal, not fail-open. `Get-ParentChainExposure` (rev-f1) advances with
`Split-Path -Parent` until the parent is empty; for `\server\share\App` PowerShell gives `\server` as the parent of
the share root, `Get-Acl` on it fails, and the walk throws "unreadable parent". Every caller (deploy plan and real
run, promotion, cutover, installer) then refuses an app folder on a network share whose share and app ACLs are safe.
Drive roots stop correctly.
**Status**: Merged 6422a3a8; closed by codex review H 2026-11-09 (`.agents/review/rev-h.result.txt` CLEAN; admitted from codex review G, `.agents/review/rev-g.result.txt`)
**Branch**: `fix/rev-g1` (worktree `D:\source\ExchangeAdminWeb-wt\rev-g1`, from master `871222c4`)
**Commit**: `031fc4b9` (`fix(rev-g1): the parent-folder ACL walk stops at the root of the volume, the share itself on a network path`) | **Implementer**: Claude (supervised agent)
**Reviewer**: codex (Portkey) / configured route / xhigh
**Range reviewed**: 636295cd..dde1b7fb (merge rev-f)

## Evidence (master dde1b7fb)

- `tools/ToolFolderAcl.psm1:169` `while ($parent)`; `:171` `Get-Acl -LiteralPath $parent`; `:212`
  `$parent = Split-Path -Parent $parent`; `:175` throws on an unreadable parent.
- Reached at `:243-245` (existing folder) and `:288` (not-yet-created folder); surfaces as a plan refusal at
  `tools/SqliteConfigBackup.psm1:112-127` or a real-run throw at `:151-154`.

## Fix shape (reviewer's proposal; verify first)

Stop the walk after processing the filesystem root from `[System.IO.Path]::GetPathRoot(...)`, which for a UNC path
is `\server\share\`. Pester guard: a UNC app path reads `\server\share` and never asks `Get-Acl \server`
(stand-in `Get-Acl` records the paths asked).

## Review G closure summary

rev-e1, rev-f2, rev-f3 CLOSED; rev-f1 PARTLY (this record is the remainder). No other new observable defect.

## Verified real (master 871222c4)

In pwsh 7 on the dev host, string-only (no network): `Split-Path -Parent '\server\share'` and
`Split-Path -Parent '\server\share\'` both answer `\server`; `Split-Path -Parent '\server'` answers `\`;
`Split-Path -Parent 'D:\'` answers empty. `[System.IO.Path]::GetPathRoot('\server\share\App\Sub')` answers
`\server\share` (no trailing separator under .NET); for `D:\a` it answers `D:\`. With the fix reverted, the new
share tests show the walk asking for `\server` (recorded by the stand-in) and then for `\`, whose real `Get-Acl`
fails: "The permissions of \, a folder above \server\share\App\Sub, cannot be read" - the false refusal the
finding describes. The `\server` step alone would fail the same way on a real host (it is not a folder).

## As built

- `tools/ToolFolderAcl.psm1`: new private `Get-AclWalkParent -Path`, which returns nothing when the path, trailing
  `\` or `/` trimmed, equals its own `GetPathRoot` (case-insensitive), and `Split-Path -Parent` otherwise. Used in
  three places: the step of `Get-ParentChainExposure`'s loop (the root is read, then the loop ends), the first
  parent in `Get-ToolFolderWriteExposure` (an app folder that IS a share root or a drive root gets no walk above it;
  its own owner and grants are still read by `Get-ObjectWriteExposure`), and the nearest-existing-ancestor search in
  `Get-ToolFolderInheritedWriteExposure` (it no longer probes `\server` with `Test-Path` when the share is
  missing; it reaches the root, finds nothing, and throws "No folder above ... exists" as before). Module and
  function comments name the share as the walk's end on a network path.
- Fail closed kept: the share root is read exactly as any parent - an untrusted owner or replacement grant there
  is still a `ParentOwner`/`ParentGrant` finding, and an unreadable share root still throws.
- Drive behaviour unchanged: `D:\x` still walks to `D:\` and stops (before, `Split-Path` answered empty there).
- Help: `docs/ConfigDbOperations.md` (the "folders above the app folder" paragraph) now says the walk ends at the
  share itself on a network path, read but not gone past. Refusal wording unchanged; README unchanged (its pointer
  goes to that paragraph).
- Tests: new Describe in `tests/ps/ToolFolderAcl.Tests.ps1` (12 tests). Every read of `\server...` (and of the
  stand-in drive `Q:`) is answered by an in-memory `DirectorySecurity` from a filtered `Get-Acl` mock that records
  the paths asked; the root-itself and not-yet-created cases also stand in `Test-Path`/`Get-Item`/`Get-ChildItem`
  for `\server...`. No real UNC path is touched.

## Guard proof (`tests/ps/ToolFolderAcl.Tests.ps1`, rev-g1 Describe, 12 tests)

| Mutation | Failing tests |
| --- | --- |
| Fix reverted: the four `Get-AclWalkParent` calls back to `Split-Path -Parent` (helper left defined) | 4 of 12: "reads every folder above a share app folder up to the share itself", "still refuses a share root owned by a principal outside the trusted set", "judges an app folder that is the share root itself", "judges a folder not created yet on a share" (each throws "The permissions of \, a folder above ..."). The 7 helper rows and the drive row pass, as they test the helper and unchanged drive behaviour |
| Helper's root check disabled (always `Split-Path -Parent`) | 6 of 12: the `\server\share` and `\server\share\` helper rows (got `\server`) and the same four walk tests |
| Helper stops one step early (returns nothing when the PARENT is the root, so the root is never read) | 7 of 12: helper rows `\server\share\App`, `\server\share\App\`, `D:\App`; the share walk (no `\server\share` read), the share-root owner refusal (no finding), the not-yet-created share case ("No folder above ... exists"), the drive walk (no `Q:\` read) |
| Restored | 12 of 12; file 54 of 54; `tests/ps` 676 of 676 |

## Assumptions not verified live

- No real network share was used. Assumption: `Get-Item` on a real `\server\share\App` returns a `FullName` whose
  root `GetPathRoot` names as `\server\share`, and `Get-Acl` on `\server\share` (with or without the trailing
  separator) reads the share's root folder ACL. Not checked against a real share.
- Long-path forms (`\?\UNC\server\share\...`, `\?\D:\...`) were not exercised; the scripts do not produce them.
