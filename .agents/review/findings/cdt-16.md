# cdt-16: a failed mirror restarts the app on a partial publish, and a failed restart hides the real error

**Severity**: HIGH. Both `deploy.ps1` mode branches mirror the new build over the publish folder
with `robocopy /MIR` inside a `try` whose `finally` restarts the app pool unconditionally. When
robocopy fails part-way (exit 8 or more, after copying or deleting some files), the `finally`
starts the pool anyway and IIS serves whatever mix of old and new binaries the folder now holds,
while the run reports the robocopy failure. Separately, when the restart itself throws inside that
`finally`, its exception REPLACES the one propagating (Known Failure Class 1), so a failed
reconciliation or `appsettings.json` generation surfaces only as a pool error, and the manifest
is never written. From the codex review of the cdt-8..10 batch (its finding 1).
**Status**: Closed - merged to master; codex extension cdt-31 closed. Row closed 2026-10-23 evening.
**Branch**: `cdt-11-14-installer-deploy` (worktree `agent-a1b04b356d91eeed2`)
**Commit**: `02aea82` (merged)
**Reviewer**: implementer (codex review of the cdt-8..10 batch, admitted by the supervisor)

## Evidence

`deploy.ps1` at `cafd4a5`, the upgrade branch (the fresh-install branch, which every `-Force` run
takes, has the same shape at 841-930):

```
571:         $poolState = Stop-AppPoolVerified -Name $AppPoolName
...
578:         $deploymentReadyForManifest = $false
579:         try {
...
588:             & robocopy @robocopyArgs
589:             if ($LASTEXITCODE -ge 8) { throw "robocopy failed with exit code $LASTEXITCODE" }
...
637:             $deploymentReadyForManifest = $true
638:         } finally {
639:             Write-Step "Starting app pool"
640:             Start-AppPoolWithRetry -Name $AppPoolName
641:             Write-Success "App pool started"
642:             if ($deploymentReadyForManifest) {
643:                 Write-DeploymentManifest -PublishFolder $PublishPath -RepoRoot $ProjectRoot
644:             }
```

`Start-AppPoolWithRetry` (line 72) rethrows after its last attempt. robocopy's exit code is a
native result; line 589 is the only thing that turns it into a throw, and it does so after the
copy has already changed files.

**Mechanism, reproduced** under Windows PowerShell 5.1.20348 (deploy's runtime). Scratch harness:
the real region of each branch - the flag line, the `try`/`finally` - lifted from the `cafd4a5`
file by the 5.1 parser and run under `$ErrorActionPreference = "Stop"`, with only `robocopy`,
`Start-AppPoolWithRetry` and `Write-DeploymentManifest` stubbed (the stub robocopy sets
`$LASTEXITCODE`; the reconciliation, the `appsettings.json` generation and the helpers are the
script's own):

| Case (both branches gave the same result) | Calls | What the caller saw |
| --- | --- | --- |
| robocopy exits 8 | `robocopy,start` | "App pool started", then "robocopy failed with exit code 8" |
| robocopy succeeds, the next step fails (malformed `appsettings.json` / unwritable path), the restart throws | `robocopy,start` | only "the pool would not start" - the real failure is gone |
| everything succeeds, the restart throws | `robocopy,start` | "the pool would not start"; no manifest written; the inner `finally` stopped before removing staging (the outer one, cdt-6, still removes it) |

The first row is the partial-publish restart: the pool was stopped and confirmed stopped for
exactly this window (aud-a35), and the `finally` undoes that on the failure path. The second row
is the masking: the restart's exception is the one the caller, and `tools/deploy-pipeline.ps1`,
receives.

## Plan (approved by the owner's EVERYTHING go)

In each mode branch, the same change:

1. `$mirrorSucceeded = $false` and `$poolStartError = $null` beside `$deploymentReadyForManifest`,
   before the `try`. `$mirrorSucceeded = $true` immediately after the `$LASTEXITCODE -ge 8` check -
   the only point at which the mirror is known to be complete.
2. A failed mirror leaves the pool stopped. The robocopy failure message says so and says what to
   do: the publish folder may hold a mix of builds, the pool was deliberately left stopped, the
   mirror does not touch the config database and names its verified pre-deploy backup (or that
   none was needed), and the staging publish is removed when the script exits, so the fix is to
   correct the cause and re-run the deploy from the source. In the `finally`, `if
   ($mirrorSucceeded)` gates the restart and the manifest; otherwise one warning says the pool is
   not being restarted.
3. The restart, when it runs, is wrapped: `try { Start-AppPoolWithRetry ...; Write-Success ... }
   catch { $poolStartError = ...; report it }`, so the `finally` completes and whatever was
   propagating propagates unchanged - the ORIGINAL exception. This is the shape
   `tools/promote-dev-to-prod.ps1` took after aud-a38 (`$poolStartError`).
4. Right after the `try`/`finally` (reached only when the `try` body succeeded): `if
   ($poolStartError) { throw ... }` - the files and config are deployed, the manifest is written,
   and the application is down; that is now its own failure, not a success and not a disguise for
   another.

Assumptions, recorded:

- Only a mirror failure leaves the pool stopped. A failure after a complete mirror (reconciliation,
  `appsettings.json` generation) still restarts the pool, as before: the binaries are whole, and
  the brief scopes the change to the mirror.
- No rollback of the binaries: `deploy.ps1` keeps no copy of the previous build (unlike the
  promote script), so the recovery for a partial mirror is to re-run the deploy. The staging
  folder is still removed (it holds a published `appsettings.json` with real values; cdt-6).
- `Start-AppPoolWithRetry` is kept. That it does not confirm the pool reached Started is a separate
  defect, recorded and fixed separately in this batch.
- The restart failure is reported with `Write-Host ... -ForegroundColor Red` in the `catch`, as the
  promote script does: `Write-Fail` throws, which is exactly what the `finally` must not do.

Constraints: no plan mode in `deploy.ps1`; pure ASCII, no BOM, LF in the index, no ternary, no
`??`, no PS7-only parameters, parsed under Windows PowerShell 5.1; the cdt-6 rule holds (every new
command sits in the outer `try` body). No version bump: ops-script-only.

Files: `deploy.ps1`, `tests/ps/DeployInvariants.Tests.ps1`, this record.

Guards: an AST test per branch - the flag initialised false before the `try`, set true only after
the exit-code check, the restart inside `if ($mirrorSucceeded)` and inside a `try` whose catch
records `$poolStartError` and does not throw, the mirror failure message, and `if
($poolStartError) { throw }` right after the `try`; and a behavioural test that runs each branch's
real region (lifted by AST) with robocopy and the pool stubbed: a failed mirror never starts the
pool and says so; a later failure with a failing restart surfaces the later failure; a failing
restart alone throws its own message after the manifest is written; success starts once and writes
the manifest. Mutations: the restart unconditional (each branch); the catch dropped (each branch);
the flag set before robocopy; the closing check removed; the `cafd4a5` shape.

## Approach

As planned, the same change in both branches of `deploy.ps1`:

- Upgrade: comment and flags at 578-585; robocopy at 595; the exit-code check, now carrying the
  explanation, at 596; `$mirrorSucceeded = $true` at 597; the `finally` at 646-665, the restart
  inside `if ($mirrorSucceeded)` (647) and its own `try`/`catch` (649-656), a warning in the
  `else`; `if ($poolStartError) { throw ... }` at 667-670, before the drift check.
- Fresh install / `-Force`: the same at 863-870, 881-883, 952-971 and 973-976, before the cdt-12
  live check.

The robocopy failure now reads, for example (from the 5.1 run below): "robocopy failed with exit
code 8, so <publish> may now hold a mix of the old and new builds. App pool ExamplePool was
deliberately LEFT STOPPED: starting it would serve that mix. The mirror does not touch the config
database (verified pre-deploy backup: <backup>\exchangeadmin.db). The staging publish <staging>
is removed when this script exits, so fix the cause and re-run this deploy from <source>; it
publishes afresh and starts the pool."

The same 5.1 harness as in Evidence, against the fixed file:

| Case (both branches gave the same result) | Calls | What the caller saw |
| --- | --- | --- |
| robocopy exits 8 | `robocopy` | the warning "App pool ExamplePool is NOT being restarted: the file mirror did not complete", then the message above; staging removed |
| robocopy succeeds, the next step fails, the restart throws | `robocopy,start` | "X App pool ExamplePool did not start: the pool would not start" printed, and the caller receives the ORIGINAL error ("Invalid object passed in, ':' or '}' expected..." / "Could not find a part of the path ...") |
| everything succeeds, the restart throws | `robocopy,start,manifest` | "The files and configuration are deployed, but app pool ExamplePool did not start (the pool would not start). The application is DOWN until the pool is started by hand." |
| everything succeeds | `robocopy,start,manifest` | no error |

## Guard proof

| Test (`tests/ps/DeployInvariants.Tests.ps1`) | Pins |
| --- | --- |
| `restarts the pool only after a complete mirror, and a failed restart never replaces the real error (cdt-16, AST)` | For the upgrade and the fresh-install branch alike: one `try` whose `finally` restarts the pool; `$mirrorSucceeded = $false` and `$poolStartError = $null` as statements of the branch before it; in the `try` body, `& robocopy`, then immediately the `$LASTEXITCODE -ge 8` check whose throw names `LEFT STOPPED`, `$PublishPath`, `$StagingPath`, `$ProjectRoot` and `$dbBackup`, then immediately `$mirrorSucceeded = $true` (the only such assignment in the branch); the restart in the then-branch of `if ($mirrorSucceeded)`, inside its own `try` whose single catch sets `$poolStartError = $_.Exception.Message` and contains no `throw` or `Write-Fail`; the statement right after the `try` is `if ($poolStartError)` opening with a `throw` |
| `runs each branch's real mirror-and-restart region: a failed mirror leaves the pool stopped, and the first error wins (cdt-16)` | Lifts each branch's region (the flag statements, the `try`/`finally`, the check after it) by AST and runs it under `Stop` with robocopy, `Start-AppPoolWithRetry` and `Write-DeploymentManifest` stubbed: robocopy exit 8 makes no other call and throws a message naming `LEFT STOPPED` and the staging path, staging removed; a later failure with a failing restart throws something other than the pool error; a failing restart alone writes the manifest and throws "did not start (the pool would not start)" ... "DOWN"; success starts once, writes the manifest, removes staging |

Mutations on `deploy.ps1` (fixed md5 `FB89865CF15CAE782160C524D9F87EE4`, pre-comment; md5s of the
content as committed, LF), each applied by a script that fails unless every edit matches exactly
once, run against the whole `DeployInvariants.Tests.ps1`, then the original bytes restored,
touched, md5 re-confirmed; the restored file re-run green (99 passed). Post-comment md5
`22446108FC80E0F943FD8300317D8618` (99 passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| U1 upgrade restart unconditional (`if ($true)`) | `B4C06EF51FB3091320E5CB9C7E5562B4` | both cdt-16 tests (AST: "Expected a value, because the upgrade restart must be conditional on a complete mirror"; behavioural: calls "robocopy,start" for a failed upgrade mirror) |
| U2 upgrade catch dropped (bare restart in the `finally`) | `0B6841D0C8F84123EE5BB484245F340C` | both (AST: "... the upgrade restart must have its own try inside the finally"; behavioural: the caller saw "the pool would not start" instead of the upgrade step's error) |
| U3 `-Force` restart unconditional | `7F5062E148D589A5C42CA53244AF2A28` | both (the same two messages, for `-Force`) |
| U4 `-Force` catch dropped | `230A016D42CA3D4565180006F0255712` | both (the same two messages, for `-Force`) |
| U5 upgrade `$mirrorSucceeded = $true` moved before robocopy | `D10D377D556120DBA1720804998DDAAB` | both (AST: "Expected 5, because the mirror counts as done only once its exit code has been checked, but got 2"; behavioural: calls "robocopy,start") |
| U6 `-Force` closing `if ($poolStartError) { throw }` removed | `7789EBF73821B4AF4222C5EFC55DBB02` | both (AST: the statement after the `try` is the live-DB resolve, not the check; behavioural: a failing restart alone threw nothing - swallowed) |
| U7 pre-fix shape (`cafd4a5`) | `4EC160E0796780E162B8AD4CD5677C89` | both (AST: "... the upgrade branch starts with the mirror not done"; behavioural: calls "robocopy,start") |

Not run: a real IIS deploy (needs elevation and a host). What stays outside the tests is the
behaviour of IIS and robocopy themselves, which the stubs stand in for.
