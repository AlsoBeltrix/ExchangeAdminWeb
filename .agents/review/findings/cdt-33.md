# cdt-33: the installer mirrors past a pool reported absent, although the run has just created it

**Severity**: MEDIUM. cdt-19's second defect, in `tools/Install-ExchangeAdminWeb.ps1`. After
`Stop-AppPoolVerified`, the installer treats `Absent` as "nothing to stop", warns, and mirrors.
In an apply run that state cannot be a fresh install: the "Create or update IIS app pool" step
creates the pool when it is missing, well before the stop, and the application is bound to it. So
`Absent` at the stop means IIS did not answer what was asked - an unreadable configuration, a
provider failure, a name the module's non-literal `Test-Path` reads as a wildcard pattern - and the
run replaces the files of an application nobody has shown to be stopped. cdt-19 fixed this in
`deploy.ps1` and recorded "The installer, which has its own stop, is not changed here." Found while
fixing cdt-32; not in the codex verdict.
**Status**: Closed - codex group review (ops-batch-4, 2026-10-21) raised no defect in this change (its nested-IIS candidate is aud-a38f, already in progress)
**Branch**: `worktree-agent-a80bd818fc511a34f` (worktree `agent-a80bd818fc511a34f`)
**Commit**: `0772c2a` (merged)
**Reviewer**: implementer (found while fixing cdt-28/cdt-32 from codex / gpt-5.5-dzs / xhigh, ops-batch-3, 2026-10-21)

## Evidence

`tools/Install-ExchangeAdminWeb.ps1` at `94eb43a`:

```
866: Invoke-PlanOrAction "Create or update IIS app pool $AppPoolName" {
867:     if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
868:         New-WebAppPool -Name $AppPoolName | Out-Null
869:     }
...
1081:     Invoke-PlanOrAction "Stop app pool $AppPoolName and confirm it stopped" {
...
1084:         $poolState = Stop-AppPoolVerified -Name $AppPoolName
1085:         if ($poolState -eq 'Absent') {
1086:             Write-Warn "App pool $AppPoolName does not exist yet - nothing to stop."
1087:         }
```

and `tools/AppPoolControl.psm1` (not edited here), whose `Absent` is the result of its existence
probe alone:

```
55:         [scriptblock]$ExistsProbe = { param($PoolName) Test-Path "IIS:\AppPools\$PoolName" },
...
64:     if (-not (& $ExistsProbe $Name)) {
65:         # Nothing is serving from this pool, so nothing can be corrupted by replacing files.
66:         return 'Absent'
```

Both steps run only in apply mode (`Invoke-PlanOrAction`), and the pool step runs first on every
apply run, so no apply run reaches the stop with the pool legitimately missing. (The module's own
description names "the installer's first run" as the `Absent` case; in the installer as it stands,
the pool step creates the pool first, so that is no longer the case.)

**Mechanism: no error at all**, reproduced under Windows PowerShell 5.1.20348 with the cdt-32
harness (the installer's real statements from after the backup decision through the mirror's
`try`, lifted from the `d32a848` file and run under `Stop` with the installer's own helpers): with
`Stop-AppPoolVerified` reporting `Absent`, the calls were `stop(ExamplePool)`, `mirror`,
`start(ExamplePool)` - the files replaced and the pool started after a stop that confirmed nothing.

## Plan (approved by the owner's EVERYTHING go)

In the stop step: only `Stopped` continues. `if ($poolState -ne 'Stopped') { Write-Fail ... }`
replaces the `Absent` warning, naming the pool and the reported state and saying the run created or
updated the pool above, so it refuses to replace the files of an application that cannot be shown
to be stopped. It throws inside the `try` whose `finally` removes staging, before the mirror.

Assumptions, recorded:

- `-PlanOnly` is unaffected: the stop step is a plan line and runs nothing.
- The refusal comes after the changes made earlier in the run (pool settings, ACLs,
  `appsettings.json`, seeds, the IIS application), which stay, as for every refusal after them; the
  publish folder itself is unchanged.
- The module's `Absent` result and its comment stay as they are (not edited here); this caller no
  longer accepts it, as `deploy.ps1` (cdt-19) and the promotion script (aud-a38b) already do not.

Constraints: Invariants 1, 4 and 6. The text `Stop-AppPoolVerified` still occurs once in the
installer. No version bump: ops-script-only.

Files: `tools/Install-ExchangeAdminWeb.ps1`, `tests/ps/InstallerConfigBackup.Tests.ps1`, this record.

Guards: an AST test - in the stop step, the statement right after `$poolState =
Stop-AppPoolVerified -Name $AppPoolName` is `if ($poolState -ne 'Stopped')` with no else, opening
with `Write-Fail`; nothing in the installer compares `$poolState` with `'Absent'`. And a behavioural
test running the real region with the IIS calls stubbed: `Absent` is refused before the mirror and
leaves no staging; `Stopped` reaches the mirror. Mutations: the `Absent` warning back (the `94eb43a`
shape); the check removed; only `Started` refused.

## Approach

As planned. In `tools/Install-ExchangeAdminWeb.ps1` after the fix, inside the stop step: after
`$poolState = Stop-AppPoolVerified -Name $AppPoolName` (1084), a comment (1085-1087) and `if
($poolState -ne 'Stopped') { Write-Fail "App pool $AppPoolName was reported '$poolState', not
confirmed stopped, although this run created or updated it above. Refusing to replace the files of
an application that cannot be shown to be stopped; the publish folder is unchanged." }` (1088-1090),
replacing the `Absent` warning.

The cdt-32 harness under 5.1, against the fixed file (the module's real serving check fed a picture
in which only the application serves the folder): `Absent` is refused with that message after the
check and the stop, before the mirror; `Stopped` reaches the mirror and the start.

## Guard proof

| Test (`tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-33: ...`) | Pins |
| --- | --- |
| `refuses anything but Stopped right after the verified stop, and nothing compares the state with Absent (AST)` | In the stop step's scriptblock, the statement right after `$poolState = Stop-AppPoolVerified -Name $AppPoolName` is `if ($poolState -ne 'Stopped')` with no else, opening with `Write-Fail`; the installer's text has no `poolState -eq 'Absent'` |
| `runs the real region: a pool reported absent is refused before the mirror, a stopped one reaches it (behavioural)` | Lifts the `try` that removes staging and runs it under `Stop` with the installer's own helpers, the external calls stubbed: `Absent` calls only the stop, throws "App pool ExamplePool was reported 'Absent', not confirmed stopped ... the publish folder is unchanged." and leaves no staging; `Stopped` gives `stop,mirror,start,manifest` |
| cdt-23's behavioural test (unchanged) | A stop that throws is still that error, with no staging left |

Mutations on `tools/Install-ExchangeAdminWeb.ps1` (fixed md5 `4A50ED568C73AE43515C9032D50F9BF8`,
pre-comment; md5s of the content as committed, LF), each applied by a script that fails unless
every edit matches exactly once, run against `InstallerConfigBackup.Tests.ps1`,
`DeployInvariants.Tests.ps1` and `AppPoolControl.Tests.ps1`, then the original bytes restored,
touched, md5 re-confirmed, and the three rerun green (220 passed) each time. Post-comment md5
`679A1FC9CD8A083BE95FBB9DBCFB697C` (220 passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| A1 the `Absent` warning back (byte-identical to `94eb43a`: the same md5 as that commit's file and cdt-32's post-comment md5) | `DAEB5336FF44EFC80098EAD7C7C7AEBE` | both cdt-33 tests (AST: "Expected: '$poolState -ne 'Stopped'' But was: '$poolState -eq 'Absent''"; behavioural: "Expected: 'stop' But was: 'stop,mirror,start,manifest'" - mirrored past an absent pool) |
| A2 the check removed | `5A6675C29A0E0BF932C40465FE9DD1E4` | both (AST: "Expected the value to have type [System.Management.Automation.Language.IfStatementAst] ..., but got $null"; behavioural as A1) |
| A3 only a pool still `Started` refused | `EB6D1F986959FE811AA0F52F8E87F80F` | both (AST: "But was: '$poolState -eq 'Started''"; behavioural as A1) |

Not run: a real install (needs elevation and IIS).
