# cdt-19: deploy.ps1's wrong-pool check passes when it cannot see the binding, and an absent pool reaches the mirror

**Severity**: MEDIUM. Before stopping the pool and mirroring, both `deploy.ps1` branches ask IIS
which pool serves the application and refuse when it is not `-AppPoolName` (review of aud-a35).
The check refuses only a positive wrong answer: when the lookup throws (it is wrapped in `try {
} catch { $null }`) or returns nothing, the binding is unknown and the check passes. Then
`Stop-AppPoolVerified` returning `Absent` is accepted with a warning, and robocopy `/MIR` replaces
the files. Neither state is expected at that point - both branches configure the pool and the
application earlier in the same run - so both mean the run cannot show that what serves the folder
has stopped, and it proceeds anyway. Found by the promote agent in this batch's files; admitted by
the supervisor.
**Status**: Closed - merged to master; codex extension cdt-28 closed. Row closed 2026-10-23 evening.
**Branch**: `cdt-11-14-installer-deploy` (worktree `agent-a1b04b356d91eeed2`)
**Commit**: `80bdd65` (merged)
**Reviewer**: implementer (found by the promote agent, admitted by the supervisor)

## Evidence

`deploy.ps1` at `4fcd9f1`, the upgrade branch (the fresh-install branch, which every `-Force` run
takes, has the same lines at 847-861):

```
562:         $boundPool = try { (Get-WebApplication -Name $AppAlias -Site $ParentSite -ErrorAction Stop).applicationPool } catch { $null }
563:         if ($boundPool -and $boundPool -ne $AppPoolName) {
564:             Write-Fail "IIS application $ParentSite/$AppAlias runs under app pool '$boundPool', not '$AppPoolName'. ...
565:         }
...
571:         $poolState = Stop-AppPoolVerified -Name $AppPoolName
572:         if ($poolState -eq 'Absent') {
573:             Write-Warn "App pool $AppPoolName does not exist - nothing to stop."
574:         } else {
575:             Write-Success "App pool $AppPoolName confirmed stopped"
576:         }
```

The `catch { $null }` and the `$boundPool -and` make "could not read the binding" and "nothing is
bound" both pass. `Stop-AppPoolVerified` (`tools/AppPoolControl.psm1`) returns `Absent` when its
`Test-Path "IIS:\AppPools\<name>"` is false, and the caller treats that as nothing to protect.

Neither state is a fresh-install state in this script. Before either branch is reached, the run
creates the pool when it is missing (line 308, `New-WebAppPool`) and the application when it is
missing (lines 387-394, `New-WebApplication ... -ApplicationPool $AppPoolName`, in the
"Verifying IIS web application and authentication" step that runs for upgrade and fresh install
alike); the fresh-install branch configures the application again (804-820). So at the check the
application exists and is bound, and the pool exists: a lookup that fails or returns nothing, or a
pool reported absent, means IIS did not answer what was asked (an unreadable configuration, a
provider failure, a name `Test-Path` reads as a wildcard pattern), not that there is nothing to
stop.

**Mechanism: no error at all**, reproduced under Windows PowerShell 5.1.20348. Scratch harness:
each branch's real region, from the `$boundPool` lookup through the check on the stop result,
lifted from the `4fcd9f1` file by the 5.1 parser and run under `Stop` with only `Get-WebApplication`
and `Stop-AppPoolVerified` stubbed:

| Lookup | Stop result | Both branches |
| --- | --- | --- |
| throws ("insufficient permissions") | Stopped | reached the mirror (calls `lookup,stop`) |
| returns nothing | Stopped | reached the mirror |
| the named pool | Absent | reached the mirror, after "does not exist - nothing to stop" |
| a different pool | - | refused (the existing check) |
| the named pool | Stopped | reached the mirror (correct) |

## Plan (approved by the owner's EVERYTHING go)

In both branches, the same change:

1. Right after the lookup: `if (-not $boundPool) { Write-Fail ... }` naming `$ParentSite/$AppAlias`
   and saying the binding could not be read although this run configured the application, so
   stopping `-AppPoolName` cannot be shown to stop what serves the folder. Nothing has been stopped
   or mirrored at that point.
2. The existing wrong-pool refusal becomes `if ($boundPool -ne $AppPoolName)`: the null case is now
   handled above, so `$boundPool -and` would only hide it again.
3. After `Stop-AppPoolVerified`: only `Stopped` continues. `if ($poolState -ne 'Stopped') {
   Write-Fail ... }` replaces the warning for `Absent`, naming the pool and the state; then the
   existing "confirmed stopped" line.

The distinction the brief asks to keep explicit, recorded: on a genuine fresh install the pool and
the application do not exist when the script STARTS, but this script creates both before it ever
reaches the check (Evidence, lines 308 and 387-394), so no fresh install reaches the check without
them, and neither branch keeps an "absent is fine" path. The installer, which has its own stop,
is not changed here.

Assumptions, recorded: the refusals happen before anything is stopped or mirrored, so "Nothing was
deployed" is true; IIS configuration changes made earlier in the run (pool settings, ACLs, the web
application) are not undone, as for every other refusal in this script. master's newer
`Assert-AppPoolServesPath` (aud-a38c, in `tools/AppPoolControl.psm1`, which this branch predates and
does not edit) checks by physical path and would be a stronger replacement for this per-alias
lookup; not adopted here.

Constraints: no plan mode in `deploy.ps1`; pure ASCII, no BOM, LF in the index, no ternary, no
`??`, no PS7-only parameters; parsed under Windows PowerShell 5.1. The cdt-6 rule holds. No version
bump: ops-script-only.

Files: `deploy.ps1`, `tests/ps/DeployInvariants.Tests.ps1` (the new tests, and the aud-a35 review
test, which pinned the old `$boundPool -and` condition), this record.

Guards: an AST test per branch - the lookup, immediately followed by `if (-not $boundPool)` opening
with `Write-Fail` naming the site and alias, then `if ($boundPool -ne $AppPoolName)` with its
`Write-Fail`, all before the stop; the statement after `$poolState = Stop-AppPoolVerified` is `if
($poolState -ne 'Stopped')` opening with `Write-Fail`; nothing in the script compares `$poolState`
to `Absent`. And a behavioural test running each branch's real region with the two IIS calls
stubbed: a throwing or empty lookup refuses before any stop; `Absent` refuses; a wrong pool refuses;
only the named pool and `Stopped` reach the mirror. Mutations: the null lookup passing (each
branch); `Absent` accepted (each branch); the `4fcd9f1` shape.

## Approach

As planned, the same change in both branches of `deploy.ps1`:

- Upgrade: the lookup at 562; a comment and `if (-not $boundPool) { Write-Fail "Cannot read which
  app pool serves IIS application $ParentSite/$AppAlias ..." }` at 563-568; `if ($boundPool -ne
  $AppPoolName)` at 569; after `Stop-AppPoolVerified` (577), a comment and `if ($poolState -ne
  'Stopped') { Write-Fail ... }` at 578-583, then "confirmed stopped".
- Fresh install / `-Force`: the same at 855-862 and 870-876.

The same 5.1 harness as in Evidence, against the fixed file, both branches: a throwing or empty
lookup is refused before any stop ("Cannot read which app pool serves IIS application Example
Site/ExampleApp (the lookup failed or returned nothing), although this run configured that
application above. ... Nothing was deployed."); `Absent` is refused ("App pool ExamplePool was
reported 'Absent', not confirmed stopped ..."); a different pool is refused as before; the named
pool, `Stopped`, reaches the mirror.

Found during the mutation round and fixed in the test before commit: with `$boundPool -and` gone,
the wrong-pool condition alone also refuses a null binding, so deleting only the new check left the
run fail-closed - but with a message saying the application "runs under app pool ''". The
behavioural test first asserted just the site and "Nothing was deployed", which both messages
contain, so it passed that mutation. It now asserts the unreadable-binding message itself (Y1m/Y2m
below), and the "null passes" mutations remove the check AND restore `$boundPool -and`, which is
the shape the brief describes.

## Guard proof

| Test (`tests/ps/DeployInvariants.Tests.ps1`) | Pins |
| --- | --- |
| `refuses to stop and mirror when the binding cannot be read, and continues only on a confirmed stop (cdt-19, AST)` | For the upgrade and the fresh-install branch alike: the statement right after `$boundPool = try { (Get-WebApplication ...` is `if (-not $boundPool)` with no else, opening with `Write-Fail` naming `$ParentSite/$AppAlias`; the next is `if ($boundPool -ne $AppPoolName)` opening with `Write-Fail`; the statement right after `$poolState = Stop-AppPoolVerified -Name $AppPoolName` is `if ($poolState -ne 'Stopped')` with no else, opening with `Write-Fail`; nothing in the script compares `$poolState` with `'Absent'` |
| `runs each branch's real binding-and-stop region: only the named pool, confirmed stopped, reaches the mirror (cdt-19)` | Lifts each branch's region (the lookup through the stop-result check) by AST and runs it under `Stop` with `Get-WebApplication` and `Stop-AppPoolVerified` stubbed: a throwing and an empty lookup each call nothing but the lookup and throw "Cannot read which app pool serves IIS application Example Site/ExampleApp ... Nothing was deployed"; `Absent` throws "reported 'Absent', not confirmed stopped"; a different pool throws the wrong-pool refusal before any stop; the named pool and `Stopped` pass, calling the lookup and the stop |
| `verifies which pool serves the app before stopping one (review of aud-a35)` (existing, two lines changed) | `if ($boundPool -ne $AppPoolName)` with its `Write-Fail`, twice (was `$boundPool -and $boundPool -ne $AppPoolName`) |

Mutations on `deploy.ps1` (fixed md5 `5080B47A0802E3FB7493EF47FCF89FE2`, pre-comment; md5s of the
content as committed, LF), each applied by a script that fails unless every edit matches exactly
once, run against the whole `DeployInvariants.Tests.ps1`, then the original bytes restored,
touched, md5 re-confirmed; the restored file re-run green (101 passed). Post-comment md5
`34F8413F9826EDEFA45A5DD502C293B8` (101 passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| Y1 upgrade: the null lookup passes (the check removed, `$boundPool -and` restored) | `CF4F391B92F6B21D541588229938291B` | the cdt-19 AST test ("Expected: '-not $boundPool' But was: '$boundPool -and $boundPool -ne $AppPoolName'"), the behavioural test ("Expected: 'lookup' But was: 'lookup,stop'" - the stop ran on an unreadable binding), and the aud-a35 review test ("Expected 2 ... but got 1") |
| Y2 `-Force`: the null lookup passes | `ABC1FEB1B0F90C063A3753A19A67F99C` | the same three, for `-Force` |
| Y1m upgrade: only the new check removed | `3C2775B9052EADD9EC6294CEE0C82B8D` | the AST test; the behavioural test (the refusal said "runs under app pool ''" instead of "Cannot read which app pool serves ...") |
| Y2m `-Force`: only the new check removed | `979F47984E9EAB16FEF364F8867E7D52` | the same two, for `-Force` |
| Y3 upgrade accepts `Absent` (the 4fcd9f1 if/else back) | `DC8EC9076446F4346E37BEEF6A3EC129` | the AST test ("Expected: '$poolState -ne 'Stopped'' But was: '$poolState -eq 'Absent''"); the behavioural test ("Expected like wildcard '*reported 'Absent', not confirmed stopped*' to match $null, because an absent pool must not reach the upgrade mirror") |
| Y4 `-Force` accepts `Absent` | `7A7E3D96E09DA85178E39DCF67B5700F` | the same two, for `-Force` |
| Y5 pre-fix shape (`4fcd9f1`) | `22446108FC80E0F943FD8300317D8618` | all three |
