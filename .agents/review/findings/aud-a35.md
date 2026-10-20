# aud-a35: an empty catch let a deploy mirror over a running application

**Severity**: HIGH. Partial replacement of a live application's binaries, with no error and
no sign anything went wrong. It only bites when the pool fails to stop, which is also
exactly when someone is already having a bad day.
**Status**: Verified and fixed; review pending
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 35 of the 2026-10-16 audit. The audit named two sites; there are **three** - the
audit missed one.

Every one read:

```powershell
try { Stop-WebAppPool -Name $AppPoolName -ErrorAction Stop } catch {}
Start-Sleep -Seconds 3
```

followed, within a few lines, by `robocopy /MIR` into the publish folder.

| File | Line (pre-fix) | Path |
| --- | --- | --- |
| `deploy.ps1` | 496 | update |
| `deploy.ps1` | 686 | fresh install - **not in the audit** |
| `tools/Install-ExchangeAdminWeb.ps1` | 710 | generic installer |

The empty catch was put there for a real reason: `Stop-WebAppPool` throws when the pool is
**already stopped**, which is normal and harmless. But it swallows everything else with it -
the pool does not exist under that name, the caller is not elevated, IIS refuses, or the
worker process will not exit. In each case the script continued into `/MIR` and replaced the
binaries of a running application file by file while it served requests.

The `Start-Sleep -Seconds 3` is not a safety net either. It is a guess about how long a
worker takes to exit; a pool draining a long request takes longer, and the deploy proceeds
regardless because nothing ever checks.

## Predicted observable failure

A deploy runs while the app pool is wedged - a hung worker, or the operator is not elevated.
The stop fails silently, the script sleeps three seconds, and robocopy mirrors a new build
over the DLLs of the running process. Some files are locked and skipped, some are replaced.
The application is now a mixture of two builds. robocopy's exit code does not report this as
failure, so the deploy is announced as successful.

## Approach

`tools/AppPoolControl.psm1`, a new module with one function: `Stop-AppPoolVerified`.

It keeps the tolerant stop - the already-stopped exception must stay harmless, or every
redeploy of a stopped instance breaks - and then **asks IIS what state the pool is in**,
polling until it reports `Stopped` or a 60-second timeout expires. A pool that never stops
throws, and the caller aborts before mirroring anything. An unreadable state also throws:
not being able to confirm a pool is stopped is not the same as it being stopped.

**A pool that does not exist returns `Absent` rather than throwing.** Nothing is serving, so
nothing can be corrupted; the installer's first run is exactly this case.

**The stop exception is kept rather than discarded.** If the pool then fails to stop, the
operator is told what the stop attempt actually said - "Access denied" is a different problem
from a worker that will not exit, and the old empty catch threw that distinction away.

The three IIS operations are injectable script blocks defaulting to the real cmdlets, so the
behaviour is testable without IIS. No caller passes anything but `-Name`.

## Files changed

- `tools/AppPoolControl.psm1` (new)
- `deploy.ps1` - both sites, plus the module import
- `tools/Install-ExchangeAdminWeb.ps1` - the site, plus the module import
- `tests/ps/AppPoolControl.Tests.ps1`

No version bump. Ops-script-only changes do not bump the app version here - precedent
`7ba6b25`, `a1103cc`, `8657b57`, `f0658c0`, none of which touched the csproj. The app binary
is unchanged.

### Two constraints this had to respect

- **Pure ASCII.** `deploy.ps1` is read by Windows PowerShell 5.1, which reads a BOM-less file
  as ANSI and turns non-ASCII into parse errors (Architectural Invariant 6). It imports this
  module, so the module is ASCII too, and a test asserts it.
- **The installer stays standalone** (Architectural Invariant 1). The module ships beside it
  and names no host, domain or configuration. The first draft of the comment explaining this
  used the words "deploy.ps1", and `DeployInvariants.Tests.ps1` failed it - the repo's own
  guard catching the mention. Reworded; the guard was not touched.

## Guard proof

Behavioural against the real function, using the injection the module provides.

| Test | What it pins |
| --- | --- |
| `returns Stopped once IIS reports it stopped` | the ordinary path |
| `waits for a pool that is still stopping rather than trusting a fixed sleep` | polls through Starting/Stopping/Stopping/Stopped; asserts it slept 3 times |
| `tolerates the already-stopped exception, which is why the catch existed` | the reason the empty catch was there still holds |
| `throws rather than letting the caller deploy over a running application` | **the finding** |
| `surfaces what the stop attempt said, because the cause changes the fix` | "Access denied" reaches the operator |
| `names the pool and refuses in terms the operator can act on` | the message is actionable |
| `fails closed when the state cannot be read at all` | unreadable is not stopped |
| `reports Absent instead of throwing, because nothing is serving` | the installer's first run, and that no stop is attempted |
| `leaves no bare Stop-WebAppPool before a mirroring deploy` | the old pattern cannot be pasted back |
| `calls the verified stop in both deploy paths and in the installer` | 2 + 1 call sites - this is what found the third site |
| `keeps the module pure ASCII` | Invariant 6 |

| Mutation | Result |
| --- | --- |
| verification removed (return Stopped straight after the stop) | FAIL - 5 tests |
| a pool that will not stop returns `Absent` instead of throwing | FAIL - 3 tests |
| the old `try/catch {}` + sleep pasted back into `deploy.ps1` | FAIL - 2 tests |

Both sources restored byte-identical (md5 verified).

**One mutation round was vacuous and is reported as such:** the first attempt's script had a
Python quoting error, applied nothing, and the suite passed - which I nearly read as "the
guard does not bite". The md5 check is what caught it. The numbers above are from the re-run.

Gate: ASCII lint passed; `Invoke-Pester tests/ps` 168 passed / 0 failed; PSScriptAnalyzer 0
errors (CI gates on Error severity); `git diff --check` clean. The .NET build and suite were
not run - no C# or Razor changed.

## Known gaps

- **Nothing was run against real IIS.** Every test drives injected script blocks. That
  `Get-WebAppPoolState` returns the string `Stopped`, and that `Stop-WebAppPool` throws the
  already-stopped error rather than something else, are taken from the cmdlets' documented
  behaviour, not observed here.
- **No deploy was performed.** The call sites are read by a source guard, not executed.
- **The 60-second timeout is a judgement, not a measurement.** It is longer than the 3-second
  sleep it replaces and errs toward waiting, but no one has measured how long a real wedged
  pool on this host takes to give up.
- `PSUseShouldProcessForStateChangingFunctions` is raised against `Stop-AppPoolVerified` and
  left unaddressed. 49 other functions in this repo raise it; adding `SupportsShouldProcess`
  to a function called from a non-interactive deploy is a behaviour change, not a lint fix.
- **Other empty catches were not swept.** PSScriptAnalyzer reports 6 `PSAvoidUsingEmptyCatchBlock`
  repo-wide; this finding addressed the ones before a mirroring deploy. The rest are unexamined.

## Closeout

Fixed directly on `master`. Independent review pending - and currently impossible: both
review harnesses are returning 401 (`.agents/state.md`). Worth a reviewer's attention
specifically on the third site, which the audit did not list and which I found only because
a guard counted call sites.
