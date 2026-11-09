# rev-g1: the parent-folder ACL walk does not stop at a UNC share root

**Severity**: MEDIUM - false refusal, not fail-open. `Get-ParentChainExposure` (rev-f1) advances with
`Split-Path -Parent` until the parent is empty; for `\server\share\App` PowerShell gives `\server` as the parent of
the share root, `Get-Acl` on it fails, and the walk throws "unreadable parent". Every caller (deploy plan and real
run, promotion, cutover, installer) then refuses an app folder on a network share whose share and app ACLs are safe.
Drive roots stop correctly.
**Status**: Admitted 2026-11-09 from codex review G (`.agents/review/rev-g.result.txt`); awaiting a slice
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
