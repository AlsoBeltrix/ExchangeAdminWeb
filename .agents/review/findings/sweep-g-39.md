# sweep-g-39: -NarrowPublishAcl could not narrow anything (sweep item 39)

**Severity**: MEDIUM if relied on - sweep item 39: `tools/Install-ExchangeAdminWeb.ps1` ran `-NarrowPublishAcl` through
`Set-DirectoryAcl`, which uses `icacls /grant` without `:r`. That adds rights: the identity's earlier Modify stayed, and so did
the Modify the folder inherits. The switch shipped inside a "ci:" commit (c1ada75f) and has never run against IIS.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-g` (from master `145f513f`)
**Commit**: `3347a925` (`fix(sweep-g, item 39): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built)

- New `Set-NarrowPublishAcl`, run for the publish folder only with `-NarrowPublishAcl`; the default (no switch) still runs
  `Set-DirectoryAcl ... (OI)(CI)M`, unchanged. The open owner decision on making the switch the default is not touched.
- The intended ACL, documented in the script help and README: `BUILTIN\Administrators` (`*S-1-5-32-544`) Full control,
  `NT AUTHORITY\SYSTEM` (`*S-1-5-18`) Full control, the app pool identity Read and execute, each `(OI)(CI)`. Granted with
  `icacls /grant:r` first, then `icacls /inheritance:r`, so the folder is never left without the administrators. Each exit code
  is a throw. `logs\` and `config\` are granted Modify next, as before.
- Read back with `ToolFolderAcl`'s `Get-ToolFolderWriteExposure` (item 12's check: the folder and everything in it except
  `config\` and `logs\`); anyone else who can still change the code - an explicit grant left from before, a foreign owner - is
  named and the run refuses. An unreadable ACL refuses. An empty identity refuses before any `icacls` call.
- `-PlanOnly` prints the intended ACL (through `Invoke-PlanOrAction`) and changes and reads nothing.

## Where the code goes beside the fix shape

- Explicit grants other than the intended three are not removed; the read-back refuses on any that can change the code. Removing
  unknown principals silently was judged a larger change than the item.

## Assumptions not verified live

- That `icacls /inheritance:r` on the publish folder re-propagates the new inheritable grants to the files already in it (so the
  read-back sees them narrowed). If not, the read-back refuses and names the files - fail closed.
- That the app runs with Read and execute on its publish root (the reason the switch is opt-in): the owner's dev install with the
  switch, modules exercised, app log clean - the step the switch's help already asks for.

## Guard proof (`tests/ps/InstallerConfigBackup.Tests.ps1`, "sweep item 39")

`icacls` and the read-back are stand-ins; no ACL on the host is read or changed. Installer restored to the pre-fix version: 6 of
6 failed (five because the function does not exist; the AST wiring row behaviourally); restored, all pass.

| Mutation | Failing tests |
|---|---|
| Installer at the pre-fix version (`/grant` added to the ACL) | the grant order and `/grant:r`, the read-back refusal, the stop at a failed grant, the empty identity, the plan line, the wiring of `-NarrowPublishAcl` |

## Deploy-time checks

- Plan: `tools\Install-ExchangeAdminWeb.ps1 -NarrowPublishAcl -PlanOnly ...` prints "Narrow the ACL of ... to exactly: ...".
- A dev install with the switch: `(Get-Acl <publish>).Access` shows the three entries, none inherited; the app starts and its log
  is clean.
