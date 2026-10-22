# cdt-34: the installer resolves a relative -PublishPath or -LogRoot against the process's start directory, not where the operator is

**Severity**: LOW. `tools/Install-ExchangeAdminWeb.ps1` makes `-PublishPath` and `-LogRoot` absolute
with `[System.IO.Path]::GetFullPath`, which resolves a relative path against the PROCESS's current
directory. PowerShell does not move that when the location changes (`Set-Location`/`cd`), so an
operator who changes to a folder and passes `-PublishPath site` (or types a relative path at the
prompt) gets an instance installed, ACLed and bound in IIS under whatever directory the PowerShell
process started in - typically their profile folder, or `C:\Windows\System32` in an elevated
console. The banner prints the resolved path before anything changes, so it is visible, but nothing
stops it. Found while reviewing the installer for this batch; not in the codex verdict.
**Status**: Merged to master; codex group review pending
**Branch**: `worktree-agent-a80bd818fc511a34f` (worktree `agent-a80bd818fc511a34f`)
**Commit**: `1f77f13` (merged)
**Reviewer**: implementer (found while fixing codex / gpt-5.5-dzs / xhigh, ops-batch-3, 2026-10-21)

## Evidence

`tools/Install-ExchangeAdminWeb.ps1` at `0772c2a`:

```
211: function Assert-SafeDirectoryPath {
...
218:     $full = [System.IO.Path]::GetFullPath($Path)
...
223:     return $full.TrimEnd('\', '/')
...
716: $PublishPath = Assert-SafeDirectoryPath $PublishPath "PublishPath"
717: $LogRoot = Assert-SafeDirectoryPath $LogRoot "LogRoot"
```

Everything after 716-717 - the banner, the ACLs, `appsettings.json`, the IIS application's folder,
the staging folder, the mirror - uses these absolute values.

**Mechanism: no error at all**, reproduced under Windows PowerShell 5.1.20348: a `-PlanOnly` run of
the real installer from a `powershell.exe` whose location had been changed to a temporary folder
(`<location>`) and whose process directory was the one it was started in (`<process-cwd>`), with
`-PublishPath site -LogRoot logs`:

```
PWD=<location>
ProcessCwd=<process-cwd>
  Publish     : <process-cwd>\site
  Log root    : <process-cwd>\logs
PLAN Grant IIS AppPool\<alias> (OI)(CI)M on <process-cwd>\site
PLAN Grant IIS AppPool\<alias> (OI)(CI)M on <process-cwd>\logs
...
  !  Plan only. No changes were made.
```

The same holds under PowerShell 7 (`[Environment]::CurrentDirectory` stays put across
`Set-Location` there too; checked with the same two lines).

## Plan (approved by the owner's EVERYTHING go)

In `Assert-SafeDirectoryPath`: resolve the value against PowerShell's current location first, with
`$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)` (no wildcard
expansion, no requirement that the path exist, available in Windows PowerShell 5.1), then normalise
with `GetFullPath` as before. An absolute path comes out as it did; the drive-root refusal is
unchanged.

Assumptions, recorded:

- Only the installer's two directory parameters are affected: `-ConfigStorePath` is required to be
  `X:\...` before it reaches `GetFullPath`, and the other `GetFullPath` calls compare paths that
  are already absolute.
- The installer is run from a FileSystem location; from another provider's drive (for example
  `IIS:\`) a relative value now resolves against that drive and is refused or reported by the
  checks that follow, as an unusable path was before.
- `deploy.ps1` is not changed: its `-PublishPath` defaults to an absolute path and is passed absolute
  by `tools/deploy-pipeline.ps1`.

Constraints: Invariants 1, 4 (a read, in plan and apply mode alike) and 6 (pure ASCII, no BOM, no
ternary, no `??`, no PS7-only parameters; parsed under 5.1). No version bump: ops-script-only.

Files: `tools/Install-ExchangeAdminWeb.ps1`, `tests/ps/InstallerConfigBackup.Tests.ps1`, this record.

Guards: a test on the lifted function from a pushed temporary location - `site` and `.\a\..\b`
resolve under that location, an absolute path is unchanged (trailing separator dropped), a drive
root is still refused; and a `-PlanOnly` run of the real installer under Windows PowerShell 5.1 from
a temporary location with relative `-PublishPath` and `-LogRoot`: the banner names the paths under
that location, and nothing is created there. Mutation: the old line back (`GetFullPath($Path)`, the
`0772c2a` shape).

## Approach

As planned. In `tools/Install-ExchangeAdminWeb.ps1` after the fix, inside `Assert-SafeDirectoryPath`:
a comment at 218-220 and, at 221, `$full =
[System.IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))`.
Nothing else changes.

The same 5.1 `-PlanOnly` run as in Evidence, against the fixed file: "Publish : <location>\site",
"Log root : <location>\logs", "Plan only. No changes were made.", and nothing created under
`<location>`.

## Guard proof

| Test (`tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-34: ...`) | Pins |
| --- | --- |
| `resolves a relative path against the current location, and leaves an absolute one as it was` | The lifted `Assert-SafeDirectoryPath`, called after `Push-Location` to a temporary folder while the process directory stays elsewhere (asserted): `site` returns `<location>\site`, `.\a\..\b` returns `<location>\b`, `D:\apps\App\` returns `D:\apps\App`, `C:\` is refused as a drive root |
| `a -PlanOnly run from another location plans the folders under it and creates nothing (Windows PowerShell 5.1)` | The real installer under `powershell.exe -Command "Set-Location <location>; & <installer> -PlanOnly -NonInteractive -PublishPath site -LogRoot logs ..."`: exit 0; the banner's Publish and Log root lines name `<location>\site` and `<location>\logs` (compared with whitespace removed); "Plan only. No changes were made."; `<location>` still empty |

Mutation on `tools/Install-ExchangeAdminWeb.ps1` (fixed md5 `566739ACCD558500523E4A42B3588C0C`,
pre-comment; md5s of the content as committed, LF), applied by a script that fails unless the edit
matches exactly once, run against `InstallerConfigBackup.Tests.ps1` and
`DeployInvariants.Tests.ps1`, then the original bytes restored, touched, md5 re-confirmed, and both
rerun green (162 passed). Post-comment md5 `535BC2C5A299C92B92333046A8283245` (162 passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| R1 `GetFullPath($Path)` back (byte-identical to `0772c2a`: the same md5 as that commit's file and cdt-33's post-comment md5) | `679A1FC9CD8A083BE95FBB9DBCFB697C` | both cdt-34 tests (the function: "Expected: '<location>\site' But was: 'D:\source\ExchangeAdminWeb\.claude\worktrees\agent-a80bd818fc511a34f\site'" - the process directory; the 5.1 plan: the banner named "Publish: D:\source\...\agent-a80bd818fc511a34f\site") |

Not run: an apply run (needs elevation and IIS).
