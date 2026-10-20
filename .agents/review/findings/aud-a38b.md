# aud-a38b: the promotion script still stops its pool the way aud-a35 stopped doing

**Severity**: MEDIUM. The same defect class as `aud-a35`, in the third script that finding
did not cover.
**Status**: Recorded follow-up; not approved
**Branch**: --
**Commit**: --

## Evidence

`tools/promote-dev-to-prod.ps1`, `Stop-AppPoolChecked`:

```powershell
Stop-WebAppPool -Name $Name -ErrorAction Stop
Start-Sleep -Seconds 3
```

Better than the sites `aud-a35` fixed - the exception is not swallowed, so a refused stop
does throw - but the three-second sleep is still a guess, and nothing confirms the worker
exited. `aud-a35` replaced exactly this pattern in `deploy.ps1` (two sites) and
`Install-ExchangeAdminWeb.ps1`, and `aud-a38` has just replaced the START half of this same
script with a verified call.

So the script now confirms its pool started and assumes its pool stopped.

## Predicted observable failure

A promotion stops the prod pool, waits three seconds while a worker drains a long request,
and robocopies over a process that is still running. Some files are locked and skipped. The
promotion reports success and prod holds a mixture of two builds.

Narrower than `aud-a35` because the stop itself cannot fail silently here - only the wait
can be too short.

## Approach (proposed, not approved)

Replace the body with `Stop-AppPoolVerified -Name $Name` from `tools/AppPoolControl.psm1`,
which the script already imports as of `aud-a38`. Keep the `Assert-NoActiveBulkJobsBeforeRecycle`
warning and the `-Apply` / plan branch around it; only the stop-and-wait changes.

The existing `tests/ps/AppPoolControl.Tests.ps1` guard that asserts no bare
`try { Stop-WebAppPool ... } catch {}` survives in `deploy.ps1` and the installer should be
widened to this file, with the different shape accounted for - this one has no empty catch.

## Known gaps

- Whether any other script stops a pool before mirroring was not surveyed beyond the three
  files `aud-a35` and this entry name.
