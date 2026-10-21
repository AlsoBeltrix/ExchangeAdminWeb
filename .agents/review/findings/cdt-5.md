# cdt-5: a fresh install passes an unset config directory to the active-job check

**Severity**: MEDIUM. Every `deploy.ps1` fresh install, and every `-Force` run (which takes the
same branch), stops on a parameter-binding error after the app pool, ACLs, IIS web
application and authentication have already been configured, and before the pool stops or
any file is mirrored. Nothing is lost or half-mirrored, but the install path cannot complete
and leaves an IIS application pointing at an empty or stale publish folder. Upgrades are
unaffected. Present since `5183603` (the bulk-job warning).
**Status**: Fixed on master; awaiting verdict
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`
**Reviewer**: implementer (surfaced by the S2 agent, confirmed by the supervisor)

## Evidence

`deploy.ps1` at `1633ad0`. The only assignment sits inside the upgrade branch:

```
433: if ($isUpgrade) {
...
458:     $runtimeConfigDir = Join-Path $PublishPath "config"
```

The fresh-install `else` branch reads it:

```
721:     Assert-NoActiveBulkJobsBeforeRecycle -ConfigDir $runtimeConfigDir -AppPath $StagingPath | Out-Null
```

`tools/JobStateWarning.psm1` declares `[Parameter(Mandatory)][string]$ConfigDir`. The script
sets no strict mode, so the unset variable reads as `$null`, which binds to `[string]` as an
empty string, and Mandatory refuses that.

Mechanism, reproduced (not reasoned) with the same call shape under
`$ErrorActionPreference = "Stop"`, in both PowerShell 7.6.6 and Windows PowerShell 5.1.20348:
a **parameter-binding error**, `System.Management.Automation.ParameterBindingValidationException`,
FullyQualifiedErrorId `ParameterArgumentValidationErrorEmptyStringNotAllowed,Assert-NoActiveBulkJobsBeforeRecycle`,
message "Cannot bind argument to parameter 'ConfigDir' because it is an empty string." It is
not a Write-Fail and not a throw inside the function: the function body never runs. Under
"Stop" it terminates the script; the statement after the call is never reached.

## Approach

Compute `$runtimeConfigDir` once, unconditionally, just before the `if ($isUpgrade)` mode
branch, and drop the in-branch assignment. The fresh-install call is kept for consistency:
with no jobs database yet, `Get-ActiveBulkJobs` returns `@()` when the file is missing, so
the check is a harmless no-op there; on a `-Force` run over an existing deployment it does
find a real jobs DB and the warning is meaningful.

## Guard proof

| Test (`tests/ps/DeployInvariants.Tests.ps1`) | Pins |
| --- | --- |
| `sets $runtimeConfigDir unconditionally before any use, so the fresh-install jobs check gets a path (cdt-5)` | Parses the AST: exactly one assignment to `$runtimeConfigDir`; no ancestor is an if, loop, switch, catch, trap, script block or function; every read starts after the assignment ends; exactly two `Assert-NoActiveBulkJobsBeforeRecycle` calls, both passing `-ConfigDir $runtimeConfigDir` |

Mutations on `deploy.ps1` (fixed md5 `022DAEF91B4D9F1A41911CB6BE5B389C`, pre-comment), each
run, then restored, touched, restored md5 re-confirmed, test re-run green. Post-comment md5
`0FD63FDAA0F3CB2DEBE459A4DA040380`.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| Assignment moved back inside the upgrade branch (the `1633ad0` shape) | `441A73956234DCF419603B3EC03B491F` | the cdt-5 test ("got 'IfStatementAst at line 433'") |
| Assignment unconditional but moved after the mode branch | `224CD4B95B85C8FB38A5803FE6DEC074` | the cdt-5 test ("Expected the actual value to be greater than 42266, because line 476 reads it, but got 22981") |
