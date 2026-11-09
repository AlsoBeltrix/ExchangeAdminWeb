# rev-f1: the item-12 folder verdict does not check the folders above the app folder

**Severity**: HIGH - security exposure. The elevated `db` tool run refuses when the app folder or anything under it
can be changed by an untrusted principal, but the check starts at the app root and recurses downward. A principal
with replacement rights on a PARENT folder (create/delete child, non-inheritable) can swap the app folder for another
between the verdict and the elevated `dotnet ... ExchangeAdminWeb.dll db ...` call. The module's own comment records
the gap: "Not checked: the folders ABOVE the app folder."
**Status**: Admitted 2026-11-09 from codex review F (`.agents/review/rev-f.result.txt`); awaiting a slice
**Reviewer**: codex (Portkey) / configured route / xhigh
**Range reviewed**: 341750c0..59522ca6 (merge sweep-g)

## Evidence (master 59522ca6)

- `tools/ToolFolderAcl.psm1:35-37`: the "Not checked" comment.
- `tools/ToolFolderAcl.psm1:162-178`: `Get-ObjectWriteExposure -LiteralPath $root ...` then
  `Get-ChildItem -LiteralPath $root -Recurse ...`; nothing walks upward.

## Failure scenario

App folder narrow; its parent grants an untrusted principal non-inheritable create/delete-child rights or ownership.
Verdict: safe. The principal renames the app folder and drops a folder with a planted `ExchangeAdminWeb.dll` in its
place; the elevated run loads it.

## Fix shape (reviewer's proposal; verify first)

Walk the parent chain to the volume root and include each parent's replacement rights (delete-child, write-DAC,
write-owner, take-ownership, untrusted owner) in the verdict, for existing and not-yet-created app folders; fail
closed on an unreadable parent. Tests: a parent with a non-inheritable grant to an untrusted principal refuses, naming
the parent; the item-12 tests today cover only inherited Modify on the app folder.
