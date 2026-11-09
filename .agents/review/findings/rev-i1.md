# rev-i1: a folder finding prints generic rights as a raw number

**Severity**: LOW - wording only, not fail-open. An inherit-only entry carrying GENERIC_READ | GENERIC_WRITE |
GENERIC_EXECUTE | DELETE (0xE0010000) was reported as "holds -536805376 (inherited) on <folder>":
`FileSystemRights` has no names for the generic bits, so `[string]$rule.FileSystemRights` printed the mask as a
signed integer. The classification was already right (`$script:ContentChangingRights` and `$script:ReplacementRights`
read the mask and include GENERIC_ALL / GENERIC_WRITE; DELETE is in both).
**Status**: Merged 350c63b5 (message-only fixes; no review round, owner verifies on the next plan run)
**Branch**: `fix/rev-i` (worktree `D:\source\ExchangeAdminWeb-wt\rev-i`, from master `79af56e7`)
**Commit**: `754852f4` (`fix(rev-i1): a folder finding names generic rights instead of printing the mask as a number`) | **Implementer**: Claude (supervised agent)
**Reviewer**: owner (live plan run 2026-11-09)
**Range reviewed**: live `deploy-pipeline.ps1 -Dev -PlanOnly` output after rev-f1/rev-g1 (master 79af56e7)

## Evidence (master 79af56e7)

- `tools/ToolFolderAcl.psm1` `Get-ObjectWriteExposure` and `Get-ParentChainExposure`: `Rights = [string]$rule.FileSystemRights`.
- The owner's plan line: "NT AUTHORITY\Authenticated Users holds -536805376 (inherited) on <parent>". -536805376 is
  0xE0010000. Windows splits an inherited generic entry into an effective entry (named rights) and an inherit-only copy
  that keeps the generic bits; the not-yet-created-folder read (`-InheritableOnly`) reports the inherit-only copy.

## Verified real

With the fix reverted at the two call sites, the new integration test gets "holds -536805376 (inherited)" instead of the
named rights (guard proof row 1).

## As built

- `tools/ToolFolderAcl.psm1`: new private `ConvertTo-RightsText -Mask <int64>`: GenericRead, GenericWrite,
  GenericExecute, GenericAll for bits 31-28, then the `FileSystemRights` name of the remaining bits
  (`[System.Enum]::ToObject`, which accepts unnamed bits where a PowerShell cast throws), or `0x<hex>` when they have no
  name. Used for every Grant and ParentGrant finding (owner findings keep their fixed text). Decisions still come from
  the mask, never from the text.
- Normal masks print as before ("Modify, Synchronize", "FullControl").
- Help: no doc or README quotes this wording; none changed.
- Tests: new Describe in `tests/ps/ToolFolderAcl.Tests.ps1` (7 tests): six formatter rows (0xE0010000 ->
  "GenericRead, GenericWrite, GenericExecute, Delete"; GenericAll; Modify, Synchronize; FullControl; GenericWrite with
  DeleteSubdirectoriesAndFiles; an unnamed bit -> "0x1000000"), and an in-memory `Q:` folder whose ACL is the SDDL
  `(A;OICIIOID;GRGWGXSD;;;S-1-5-11)`: a not-yet-created child yields exactly one Grant with those rights (the
  content-changing classification pinned) and the exact formatted line.

## Guard proof (`tests/ps/ToolFolderAcl.Tests.ps1`, rev-i1 Describe, 7 tests)

| Mutation | Failing tests |
| --- | --- |
| Fix reverted at both call sites (`[string]$rule.FileSystemRights`, helper left defined) | 1 of 7: the inherit-only generic entry test |
| Generic bits not named | 4 of 7: the 0xE0010000, GenericAll and GenericWrite+DC rows, and the integration test |
| Classification weakened: DELETE and the generic bits dropped from `$script:ContentChangingRights` | 1 of 7: the integration test (no finding) |
| Restored | 7 of 7; file 61 of 61 at this commit |

The first run of the new rows also caught a defect in the draft (a PowerShell cast of an unnamed bit to
`FileSystemRights` throws); the helper uses `[System.Enum]::ToObject` for that reason.
