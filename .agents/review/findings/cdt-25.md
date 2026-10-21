# cdt-25: deploy.ps1 reports the app pool started without checking that it is

**Severity**: MEDIUM. `deploy.ps1`'s restart helper `Start-AppPoolWithRetry` returns as soon as
`Start-WebAppPool` returns - IIS accepting the request - and the script then prints "App pool
started" and goes on to "Deploy complete". IIS can accept the start and still leave the pool stopped
(a wrong identity password, a missing runtime, rapid-fail protection), so a deploy can end with new
binaries in place, the application down and a success message. aud-a38 fixed exactly this in the
promotion script with `Start-AppPoolVerified` (`tools/AppPoolControl.psm1`); cdt-18 brought it to the
installer; `deploy.ps1`, which already imports the module, still assumed. cdt-16 made a THROWING
restart its own failure; this is the restart that does not throw. Found while fixing cdt-16.
**Status**: Closed - codex group review (ops-batch-3, 2026-10-21) raised no defect in this change
**Branch**: `cdt-11-14-installer-deploy` (worktree `agent-a1b04b356d91eeed2`)
**Commit**: `325ffee` (merged)
**Reviewer**: implementer (found by the cdt-11..19 agent; not yet confirmed by the supervisor)

## Evidence

`deploy.ps1` at `5b7d3ac`:

```
 77: function Start-AppPoolWithRetry {
 78:     param([string]$Name, [int]$MaxAttempts = 5, [int]$DelaySeconds = 3)
 79:     for ($i = 1; $i -le $MaxAttempts; $i++) {
 80:         try {
 81:             $state = (Get-ItemProperty "IIS:\AppPools\$Name").State
 82:             if ($state -eq 'Started') { return }
 83:             Start-WebAppPool -Name $Name -ErrorAction Stop
 84:             return
```

and in both restarts (cdt-16's guarded `finally`): `Start-AppPoolWithRetry -Name $AppPoolName`
followed by `Write-Success "App pool started"`. The retry covers a start request that THROWS; nothing
looks at the pool after a request that does not.

**Mechanism: no error at all**, reproduced under Windows PowerShell 5.1.20348: the real
`Start-AppPoolWithRetry`, lifted from the `5b7d3ac` file by the 5.1 parser, with `Get-ItemProperty`
reporting the pool `Stopped` throughout and `Start-WebAppPool` returning normally (IIS accepting the
request, the pool never running):

```
Start-AppPoolWithRetry RETURNED after 1 start request(s); the pool still reports 'Stopped' - the caller prints 'App pool started'
```

## Plan (approved by the owner's EVERYTHING go)

1. Keep the retry loop - it covers a start request that throws transiently right after the stop and
   the mirror - but leave the loop with `break` instead of `return`, and after it confirm with
   `Start-AppPoolVerified -Name $Name` (A38): it polls IIS until the pool reports Started and throws,
   naming the state and any start error, when it does not. Its own start request on a pool that is
   already started is harmless (it throws and is swallowed inside it; the state decides).
2. Nothing else changes: the callers (cdt-16's guarded restart) already report a throw from the
   restart as "did not start ... The application is DOWN", and only then.

Assumption, recorded: `Start-AppPoolVerified`'s default 60-second wait is kept, as the promotion
script and the installer use it.

Constraints: no plan mode in `deploy.ps1`; pure ASCII, no BOM, LF in the index, no ternary, no `??`,
no PS7-only parameters; parsed under Windows PowerShell 5.1. `tools/AppPoolControl.psm1` is not edited
(the supervisor: master has since changed it); `Start-AppPoolVerified` is in this branch's copy. No
version bump: ops-script-only.

Files: `deploy.ps1`, `tests/ps/DeployInvariants.Tests.ps1`, this record.

Guards: a behavioural test of the lifted `Start-AppPoolWithRetry` with the IIS calls and
`Start-AppPoolVerified` stubbed: a start IIS accepts but that never runs now throws (the
verification's message); a normal start returns and is verified once; a transient start failure is
retried and then verified; an already-started pool is still verified. Mutations: the verification
removed; the early `return` for an already-started pool restored; the `5b7d3ac` shape.

## Approach

As planned. In `deploy.ps1` after the fix, `Start-AppPoolWithRetry` (77-96): the loop leaves with
`break` (lines 82 and 84), and lines 91-95 are a comment and `$null = Start-AppPoolVerified -Name
$Name`. `deploy.ps1` imports `tools/AppPoolControl.psm1` unconditionally near its top, before either
branch can call the helper. The scratch probe from Evidence does not import that module, so against
the fixed file it stops on "Start-AppPoolVerified is not recognized"; the Pester test below is the
behavioural evidence, with the verifier stubbed the way the module's own tests drive it.

## Guard proof

| Test (`tests/ps/DeployInvariants.Tests.ps1`) | Pins |
| --- | --- |
| `confirms the pool is running after starting it, not only that IIS accepted the request (cdt-25)` | Lifts `Start-AppPoolWithRetry` by AST and runs it with `Get-ItemProperty`, `Start-WebAppPool`, `Start-AppPoolVerified` and `Start-Sleep` stubbed: a start IIS accepts but that never runs throws the verifier's "is 'Stopped' after 60 seconds" (calls `start,verify`); a normal start returns (`start,verify`); a start request that throws once is retried and verified (`start,start,verify`); a pool read as already started is still verified (`verify`) |
| cdt-16's two tests (unchanged) | The restart's throw is caught in the `finally` and becomes "did not start ... DOWN" after the deploy |

Mutations on `deploy.ps1` (fixed md5 `479FBD9326D83DC2BAAFE0958CBBFB80`, pre-comment; md5s of the
content as committed, LF), each applied by a script that fails unless every edit matches exactly
once, run against the whole `DeployInvariants.Tests.ps1`, then the original bytes restored,
touched, md5 re-confirmed; the restored file re-run green (103 passed). Post-comment md5
`0B210400C2024443E601997B6CA66F4B` (103 passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| H1 the verification removed | `B4B6A48811F4E5D3A6A4A8B6DF1782F1` | the cdt-25 test ("Expected like wildcard '*is 'Stopped' after 60 seconds*' to match $null, because IIS accepting the start is not the pool running (A38)") |
| H2 the early `return` for an already-started pool restored | `FF950928E43E84BC3737ED8F4024390D` | the cdt-25 test ("Expected: 'verify' But was: ''", because a pool read as started is still confirmed) |
| H3 pre-fix shape (`5b7d3ac`) | `7D404F85CF2973D029D79FA7C6652ECF` | the cdt-25 test (as H1) |

Not run on a host: no real pool was started.
