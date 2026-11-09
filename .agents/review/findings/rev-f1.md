# rev-f1: the item-12 folder verdict does not check the folders above the app folder

**Severity**: HIGH - security exposure. The elevated `db` tool run refuses when the app folder or anything under it
can be changed by an untrusted principal, but the check starts at the app root and recurses downward. A principal
with replacement rights on a PARENT folder (create/delete child, non-inheritable) can swap the app folder for another
between the verdict and the elevated `dotnet ... ExchangeAdminWeb.dll db ...` call. The module's own comment records
the gap: "Not checked: the folders ABOVE the app folder."
**Status**: Merged dde1b7fb; PARTLY closed by codex review G 2026-11-09 (remainder rev-g1, UNC share root)
**Branch**: `fix/rev-f` (worktree `D:\source\ExchangeAdminWeb-wt\rev-f`, from master `454fa2b7`)
**Commit**: `a2116bd5` (`fix(rev-f1): the db tool's folder check reads every folder above the app folder`), README
pointer `3a6f02f6` | **Implementer**: Claude (supervised agent)
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

## Verified real (master 454fa2b7)

`tools/ToolFolderAcl.psm1:35-37` read "Not checked: the folders ABOVE the app folder", and `Get-ToolFolderWriteExposure`
began at `Get-ObjectWriteExposure -LiteralPath $root` and recursed down only; `Get-ToolFolderInheritedWriteExposure` read
only the nearest existing ancestor's inheritable entries.

## As built

- `tools/ToolFolderAcl.psm1`: new private `Get-ParentChainExposure` walks from the first parent to the volume root
  (`Split-Path -Parent` until empty). For each folder: an owner outside the trusted set is a `ParentOwner` finding; an
  allow entry outside the trusted set that applies to the folder itself (inherit-only skipped; inherited and explicit,
  inheritable or not, all count; CREATOR OWNER/GROUP skipped as before) carrying delete child, delete, change
  permissions, take ownership or generic all is a `ParentGrant` finding. A parent whose ACL or owner cannot be read
  throws "The permissions of <parent>, a folder above <folder>, cannot be read (...)".
- `Get-ToolFolderWriteExposure` runs the walk first, then the unchanged downward check. `Get-ToolFolderInheritedWriteExposure`
  (a folder not created yet) walks from the nearest existing ancestor up, then the unchanged inheritable check.
- `Format-ToolFolderWriteExposure` says "<principal> holds <rights> on <parent>, a folder above the app folder, so can
  replace it" / "is the owner of <parent>, a folder above the app folder, ...".
- Every caller (`Resolve-ConfigDbTool` action and plan mode, `JobStateWarning`, the installer's `-NarrowPublishAcl`
  read-back) already calls these before any `dotnet.exe` run, so the walk runs first on every path, `-PlanOnly`
  included; no caller changed.
- Tests: the suites' fixtures all sit under the temp directory, whose own ancestry is the host's (on this host other
  accounts hold Modify and Full control on `%TEMP%` and the profile). `tests/ps/Get-BuiltApp.ps1` gains
  `Use-TempAncestryStandIn`: a Pester mock in `ToolFolderAcl` that answers `Get-Acl` for the temp directory and its
  ancestors with a SYSTEM-owned ACL granting nothing, and passes every other read to the real cmdlet. Called from the
  root `BeforeAll` of the four suites that judge real folders (ToolFolderAcl, SqliteConfigBackup, JobStateWarning,
  MoveConfigDbToShared). Folders a test creates under temp are still read for real (real `icacls` grants).
- Docs: `docs/ConfigDbOperations.md` ("The folders above the app folder are checked too"), README deploy section (two
  pointers).

## Where the code goes beside the fix shape

- The brief listed "the pool identity" in the trusted set for parents. It is not trusted: the downward check does not
  trust it with write rights on the app folder (module description), and a pool identity that can delete or rename the
  app folder through a parent can change the code the elevated tool runs. ToolFolderAcl also has no pool identity to
  hand. Stricter, fail-closed.
- Write data and append data (create files / create folders) on a parent are not findings: they only add entries
  beside the app folder. The volume root's usual `Users: CreateDirectories` grant therefore passes.
- Not addressed: where a junction or symbolic link in the path leads (the folders read are the ones the path names).
  Stated in the module description and the operations doc.

## Assumptions not verified live

- That the dev and prod publish folders' parents (`D:\inetpub`, `D:\`) show no untrusted replacement rights. If the drive
  root grants Authenticated Users `(OI)(CI)(IO)M`, `D:\inetpub` inherits Modify (which includes delete) and every deploy,
  promotion and cutover will now REFUSE naming `D:\inetpub` until the owner narrows it. Not read on the host by this slice.
- That `Get-Acl` on a volume root and on a UNC share root reads without a privilege the elevated account lacks.

## Guard proof (`tests/ps/ToolFolderAcl.Tests.ps1`)

| Mutation | Failing tests |
|---|---|
| `tools/ToolFolderAcl.psm1` at `454fa2b7` (no parent walk) | 13 of 41: the five "non-inheritable grant on a parent" rows (DC, DE, WDAC, WO, M), "inherited grant on a parent, two folders up", "parent owned by a principal outside the trusted set", "throws, naming the parent", "folder not created yet by the folders above it", the three Resolve-ConfigDbTool rev-f1 rows, and the updated item-12 "folder not created yet" row |
| Restored | 41 of 41 pass |

The clean-chain row and the "write/append/read on a parent are not findings" rows pass both ways (they pin what must not
refuse).

## Deploy-time checks

- `deploy-pipeline.ps1 -Dev -PlanOnly`: the "App folder permissions" line is now a counted `!` refusal if any folder above
  the staging or publish folder lets an untrusted principal replace it, and names that folder and principal.