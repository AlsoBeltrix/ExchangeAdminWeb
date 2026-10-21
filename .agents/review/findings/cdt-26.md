# cdt-26: both manifest writers record a git status that failed as a clean tree

**Severity**: LOW. `deploy.ps1` and the installer write `deployment-manifest.json` with
`gitDirty = try { [bool](git ... status --porcelain 2>$null) } catch { $false }`. When `git status`
fails, both record `false` - "built from a clean tree" - under either shell: Windows PowerShell 5.1
turns the redirected stderr into an error the `catch` maps to `$false`, and PowerShell 7 returns no
output, which `[bool]` reads as `$false`. The promotion pipeline refuses a dev build from a dirty tree
with `if ($manifest.gitDirty)`, so a manifest written while status failed passes that gate as clean.
`git rev-parse HEAD` can succeed while `git status` fails (an unreadable or corrupt index), so the
manifest then carries a valid commit and the provenance check passes too. The same fail-open class
aud-pipeline-provenance removed from the pipeline's own probes ("a failed status probe now says
drift was NOT checked instead of looking clean"), in the writers. Found while fixing cdt-14.
**Status**: Closed - codex group review (ops-batch-3, 2026-10-21) raised no defect in this change
**Branch**: `cdt-11-14-installer-deploy` (worktree `agent-a1b04b356d91eeed2`)
**Commit**: `23e57e5` (merged)
**Reviewer**: implementer (found by the cdt-11..19 agent; not yet confirmed by the supervisor)

## Evidence

At `325ffee`:

```
deploy.ps1:129:                         $gitDirty = try { [bool](git -C $RepoRoot status --porcelain 2>$null) } catch { $false }
tools/Install-ExchangeAdminWeb.ps1:340: $gitDirty = try { [bool](git -C $RepoRoot status --porcelain 2>$null) } catch { $false }
```

and the consumer, `tools/deploy-pipeline.ps1`:

```
187:     if ($manifest.gitDirty) {
188:         if (-not $AllowDirty) {
189:             throw "Dev was built from a dirty source tree (uncommitted changes). ...
```

**Mechanism: no error at all**, reproduced. Scratch probe: each writer lifted by AST and run under
`Stop` with a repository root git cannot read:

```
PS 5.1.20348.5756 deploy.ps1: gitCommit=[unknown] gitDirty=[False] -> the promotion's dirty gate passes (clean)
PS 5.1.20348.5756 installer:  gitCommit=[unknown] gitDirty=[False] -> the promotion's dirty gate passes (clean)
PS 7.6.6 deploy.ps1:          gitCommit=[]        gitDirty=[False] -> the promotion's dirty gate passes (clean)
PS 7.6.6 installer:           gitCommit=[]        gitDirty=[False] -> the promotion's dirty gate passes (clean)
```

and a temporary repository with an untracked file and a corrupt index, which is the case where the
commit IS known:

```
rev-parse exit=0 -> 3dd610c57d4d...
status exit=128 -> error: bad signature 0x04030201 fatal: index file corrupt
```

## Plan (approved by the owner's EVERYTHING go)

1. In both writers: `$gitDirty = 'unknown'`, then run `git status --porcelain` in a `try`, and only
   when it exits 0 record `[bool]` of its output; a throw (5.1's redirected stderr under `Stop`) or a
   non-zero exit leaves `'unknown'`. The manifest then says what is known.
2. Nothing else changes in the writers; the commit and branch probes keep their present behaviour
   (an unknown commit already fails the provenance check, which handles both `unknown` and empty).

Assumptions, recorded: `'unknown'` is a non-empty string, so the pipeline's `if ($manifest.gitDirty)`
reads it as dirty and refuses unless `-AllowDirty` - fail-closed, but its message says "built from a
dirty source tree" when the truth is "could not tell". Making that message distinguish the two is a
change to `tools/deploy-pipeline.ps1`, outside this batch's files; recorded here as a follow-up.

Constraints: `deploy.ps1` as before (5.1, ASCII, LF, no plan mode); the installer as cdt-11
(standalone, plan mode - the manifest step already prints its plan - 5.1, ASCII). No version bump.

Files: `deploy.ps1`, `tools/Install-ExchangeAdminWeb.ps1`, `tests/ps/DeployInvariants.Tests.ps1`, this
record.

Guards: a behavioural test that builds three temporary repositories - clean, dirty, and dirty with a
corrupt index - and runs each writer (lifted by AST) under PowerShell 7 and, through
`powershell.exe`, under Windows PowerShell 5.1: clean records `false`, dirty records `true`, and the
corrupt index records `unknown` while the commit is still the repository's HEAD. Mutations: each
writer's old line restored; the exit-code check dropped from one writer.

## Approach

As planned, the same block in both writers: `deploy.ps1` lines 129-138 and
`tools/Install-ExchangeAdminWeb.ps1` lines 340-349 - a comment, `$gitDirty = 'unknown'`, and a `try`
that records `[bool]` of `git status --porcelain` only when it exits 0. The installer's comment names
no other script.

The same probe as in Evidence, against the fixed files, now records `gitDirty=[unknown]` for both
writers under both shells, which the pipeline's `if ($manifest.gitDirty)` reads as dirty and refuses
unless `-AllowDirty`.

## Guard proof

| Test (`tests/ps/DeployInvariants.Tests.ps1`) | Pins |
| --- | --- |
| `records an unreadable git status as unknown, not as a clean tree, in both manifest writers and both shells (cdt-26)` | Builds three temporary repositories with a commit each - clean, dirty (an untracked file), and dirty with a corrupt `.git\index` (asserted to make `git status` fail) - and runs `Write-DeploymentManifest` from each script (lifted by AST) under PowerShell 7 in-process and under Windows PowerShell 5.1 through `powershell.exe`: clean records boolean `false`, dirty boolean `true`, corrupt the string `unknown` with `gitCommit` equal to the repository's HEAD |

Mutations (fixed md5s pre-comment `deploy.ps1` `FF9E66E9B7B9AF8FA7879ECD50DB3201`,
`tools/Install-ExchangeAdminWeb.ps1` `D6030177F3CC2060F576562E35D27DFB`; md5s of the content as
committed, LF), each applied to one file by a script that fails unless every edit matches exactly
once, run against `DeployInvariants.Tests.ps1`, then the original bytes restored, touched, md5
re-confirmed; the restored files re-run green (104 passed). Post-comment md5s `deploy.ps1`
`727A6E123E4E8B7E096FCE134BFF976B`, installer `FC9225F449E2C37C0E8904382A4E35F6` (both suites, 144
passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| J1 `deploy.ps1`'s writer back to the `325ffee` line (its pre-fix shape) | `0B210400C2024443E601997B6CA66F4B` | the cdt-26 test ("Expected 'unknown', because deploy.ps1 under pwsh, corrupt repository, but got $false") |
| J2 `deploy.ps1`'s exit-code check dropped | `C06A5716C4E4060CEA851760BAB6472B` | the cdt-26 test (the same, under pwsh) |
| J3 the installer's writer back to the `325ffee` line (its pre-fix shape) | `A6EC5B72F279C8EE2FB351E2DA49765E` | the cdt-26 test ("... tools/Install-ExchangeAdminWeb.ps1 under pwsh, corrupt repository, but got $false") |
| J4 the installer's exit-code check dropped | `C52A60747B45FEF0E2036BADCDB629C0` | the cdt-26 test (the same, for the installer) |
| J5 `deploy.ps1`'s `catch` recording `$false` - the path only Windows PowerShell 5.1 takes | `C33A1A4052D4CAC1E782E96ECA6B961B` | the cdt-26 test ("... deploy.ps1 under powershell.exe, corrupt repository, but got $false") - the 5.1 run is not decoration |

Follow-up, not in this batch's files: `tools/deploy-pipeline.ps1` reports a manifest whose `gitDirty`
is `unknown` as "Dev was built from a dirty source tree"; it refuses correctly, with the wrong reason.
