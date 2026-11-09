# sweep-g-38: the stop waited 60 s while IIS allows 90 s, and the promotion's stop sat outside the try that restarts prod (sweep item 38)

**Severity**: MEDIUM, not run on prod - sweep item 38: `Stop-AppPoolVerified` (`tools/AppPoolControl.psm1`) refused after 60 s
while IIS gives a stopping worker `processModel.shutdownTimeLimit` (90 s by default); the promotion stopped prod outside its try
(`tools/promote-dev-to-prod.ps1`), so a stop that took 60-90 s ended the run with prod stopping and not restarted. The two scripts
also treat a failed copy differently: `deploy.ps1` leaves the pool stopped, the promotion rolls back and restarts.
**Status**: Fixed on branch (timing and promotion); the `deploy.ps1` half is a reported conflict, not changed; awaiting review
**Branch**: `fix/sweep-g` (from master `145f513f`)
**Commit**: `b01bee2c` (`fix(sweep-g, item 38): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built)

- **Timing.** With no `-TimeoutSeconds` (every script), `Stop-AppPoolVerified` waits 60 s; past that, and only then, it reads the
  pool's `processModel.shutdownTimeLimit` through a new injectable `-ShutdownTimeLimitProbe` (`Get-ItemProperty
  IIS:\AppPools\<pool> -Name processModel.shutdownTimeLimit -ErrorAction Stop`), 90 s when unreadable, and waits until that plus
  `-MarginSeconds` (30) has passed since the stop - never less than the 60 s it waited before. The refusal names the wait and the
  limit's source. An explicit `-TimeoutSeconds` is used as given (the tests' immediate-timeout rows).
- **Promotion.** The stop is the first statement of the promotion's try. aud-a38b's rule is kept by two flags: the catch rolls
  back only once `$mirrorStarted` (set after the stop, before the mirror); the finally starts prod only once
  `$prodStopRequested` (set in the stop step just before `Stop-AppPoolVerified`, cleared for 'Absent'). A refusal before the stop
  starts and rolls back nothing; a stop that times out is restarted, and the verdict says nothing was mirrored and prod's files
  are unchanged.
- README deploy section.

## Conflict - not changed: `deploy.ps1` after a failed mirror

The sweep asks `deploy.ps1` to restart its pool on a failed copy as the promotion does, or roll back if it has a previous build.
Restarting conflicts with cdt-16 (closed, merged): a failed `robocopy /MIR` leaves a mix of old and new files, and starting the
pool serves that mix - cdt-16 removed exactly that restart. Rolling back is not possible: `deploy.ps1` keeps no copy of the
previous build (only the config backups). So it still leaves the pool stopped, with its message saying why and how to recover.
Adding a binaries backup to `deploy.ps1` would be new functionality - an owner decision.

Related, not changed: the promotion's finally restarts prod even when its rollback robocopy failed (exit >= 8), which serves a
mix of builds - the cdt-16 class in the promotion. Reported for the owner; outside this item's fix shape.

## Assumptions not verified live

- The shape `Get-ItemProperty` returns for `processModel.shutdownTimeLimit` under Windows PowerShell 5.1 (a TimeSpan, or an
  attribute whose `Value` is one); a string or number of seconds is accepted too, and anything else reads as 90 s. The owner's
  check: `(Get-ItemProperty IIS:\AppPools\ExchangeAdminWeb -Name processModel.shutdownTimeLimit).GetType()` on the host.
- That `Start-WebAppPool` on a pool that is still stopping when the wait ends leaves it Stopped (then the restart is reported
  failed: "prod is DOWN until it is started by hand") rather than starting it. Either is reported, never hidden.

## Guard proof (`tests/ps/AppPoolControl.Tests.ps1`, "sweep item 38")

Both tools files restored to the pre-fix version: 13 of 13 new or changed tests failed; restored, all pass. Two timing rows
("reads no limit ...", "explicit -TimeoutSeconds") fail there only because the probe parameter does not exist - structural, not
behavioural.

| Mutation | Failing tests |
|---|---|
| Stop wait back to a fixed 60 s | the 6 limit rows: past 60 s for a 90 s limit, refusal at limit + margin, a longer limit (two shapes), an attribute's Value, the 90 s default |
| Promotion stop back outside the try | "a stop that times out restarts prod ...", the AST row on the try's first statement and the catch's `-not $mirrorStarted` gate, and the region rows |

## Deploy-time checks

- On the host: `(Get-ItemProperty IIS:\AppPools\ExchangeAdminWeb -Name processModel.shutdownTimeLimit)` - the type, and the value
  (expect 00:01:30).
- A promotion: normal output unchanged; a stop that takes long prints nothing new until 60 s have passed.
