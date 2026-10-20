# aud-a38: a failed restart erased the promotion's verdict

**Severity**: HIGH. The failure mode is prod down with the operator told the wrong thing
about why - or, on the success path, told nothing at all.
**Status**: Verified, fixed, independently reviewed, review findings remediated
**Branch**: -- (direct-to-main)
**Commit**: `3fc5e6e`

## Evidence

Finding 38 of the 2026-10-16 audit, verified against the code. The audit names the structural
problem; reading the file turned up a second defect in the same three lines.

`tools/promote-dev-to-prod.ps1`:

```powershell
try   { ...promotion... }
catch { $promotionFailed = $true; ...rollback...; $rolledBack = $true }
finally { Start-AppPoolChecked -Name $ProdAppPoolName }

if ($promotionFailed) {
    if ($rolledBack) { throw "Promotion failed and was rolled back..." }
    else             { throw "Promotion failed and automatic rollback did not complete..." }
}
```

### The restart is in `finally` and is allowed to throw

An exception raised inside a `finally` block **replaces** whatever was propagating. So when
`Start-WebAppPool` failed:

- the `if ($promotionFailed)` block below never executed;
- the operator saw a raw pool-start error and nothing about whether the promotion had
  succeeded, failed, or been rolled back;
- the rollback could have completed perfectly and the operator had no way to know.

Keeping the restart in `finally` is correct - it must run on both paths. Letting it throw
from there is not.

### The restart was never verified

```powershell
Start-WebAppPool -Name $Name
Write-Ok "Prod app pool started"
```

IIS accepting a start request is not the pool running. A bad identity password - which
`aud-a34` shows is reachable by an unrelated route - a missing runtime, or rapid-fail
protection all leave the pool stopped with no exception raised. The script then printed
"Prod app pool started" and exited 0.

**Together these produce the worst case: a successful promotion, new binaries in place, the
application down, and a success message.** The old shape had no branch that could report it.

## Predicted observable failure

A promotion runs. Robocopy succeeds. `Start-WebAppPool` is accepted but the pool fails to
come up because the service account's password has expired. The script prints "Prod app pool
started", then "Promotion complete", and exits 0. Prod is down. Monitoring finds it before
the operator does, and the promotion log says everything worked.

## Approach

**The restart may fail, but it may not be what throws.** It stays in `finally`, wrapped so
its error is captured into `$poolStartError` and reported rather than propagated.

**The verdict reports both facts.** A failed promotion now appends the pool state to its
message instead of being replaced by it, so the operator learns the rollback status *and*
that prod is down, in one sentence.

**A new branch for the case that could not be reported**: promotion succeeded, pool did not
start. It throws, naming the backup, rather than printing "Promotion complete".

**`Start-AppPoolVerified`** in `tools/AppPoolControl.psm1`, the symmetric twin of
`Stop-AppPoolVerified` from `aud-a35`: issue the start, then poll IIS until it reports
`Started` or the timeout expires, and surface what the start attempt said if it does not.

One deliberate asymmetry between the twins: a **missing** pool is `Absent` and fine on the
stop side - nothing is running, nothing can be corrupted - and an **error** on the start
side, because the application that was supposed to come back is not there.

### Scope note

The verification half is not literally what A38 names. I included it because a start that
fails silently *is* a restart failure that the finding's own fix would then have nothing to
report - the two halves produce one observable outcome, and shipping the reporting without
the detection would have been knowingly half-done. Recorded here rather than assumed.

## Files changed

- `tools/AppPoolControl.psm1` - `Start-AppPoolVerified`
- `tools/promote-dev-to-prod.ps1` - the import, the verified start, `$poolStartError`, the
  guarded `finally`, the combined verdict, the new success-but-down branch
- `tests/ps/AppPoolControl.Tests.ps1`

No version bump; ops-script-only, same precedent as `aud-a34`, `aud-a35`, `aud-a36`.

## Guard proof

`Start-AppPoolVerified` is driven for real through its injected probes. The promotion
script's control flow is a source guard - it needs IIS, two publish trees and a real
promotion to execute.

| Test | What it pins |
| --- | --- |
| `returns Started once IIS confirms it` | the ordinary path |
| `waits through Starting rather than reporting the request as the outcome` | polling, not announcing |
| `throws when the pool never reaches Started, and says the application is down` | **the silent-failure half** |
| `surfaces what the start attempt said` | the cause reaches the operator |
| `treats a missing pool as an error, unlike the stop side` | the deliberate asymmetry |
| `fails closed when the state cannot be read` | unreadable is not running |
| `does not let the restart throw out of the finally block` | **the finding** |
| `records the restart failure instead of discarding it` | `$poolStartError` is captured |
| `reports a failed promotion AND a dead pool together, not whichever threw last` | the combined verdict |
| `reports the case the old shape could not: promotion succeeded, application down` | the new branch |
| `confirms the pool is running rather than announcing it` | and that the bare `Start-WebAppPool` call is gone |

| Mutation | Result |
| --- | --- |
| `finally` reverted to a bare `Start-AppPoolChecked` | FAIL - 2 |
| the success-but-down branch disabled (`if ($false)`) | FAIL - 1 **after the guard was fixed; see below** |
| verified start replaced with bare `Start-WebAppPool` | FAIL - 1 |
| the `$state -eq 'Started'` check replaced with `$true` | FAIL - 2 |

**One guard was found weak by its own mutation and had to be rewritten.** The
success-but-down test originally asserted only the throw's message text. Disabling the
branch with `if ($false)` left that string in the file, unreachable, and the test stayed
green - 33 passed, 0 failed, proving nothing. It now matches the condition and the throw
together, and the mutation fails it. This is the repo's recurring "a source scan passes for
the wrong reason", caught this time by the mutation rather than by a reviewer.

Each mutation confirmed applied before its run. Both sources restored byte-identical (md5).

Gate: ASCII lint passed; `Invoke-Pester tests/ps` 199 passed / 0 failed; PSScriptAnalyzer 0
errors; both files parse clean; `git diff --check` clean. No .NET run - no C# or Razor
changed.

## Known gaps

- **No promotion was run and no IIS was touched.** The control-flow assertions read source.
  That an exception from `finally` replaces the propagating one is PowerShell semantics,
  stated from the language's behaviour rather than demonstrated here.
- **The success-but-down case now throws**, which is a behaviour change for any caller
  treating a non-zero exit as "promotion did not happen". It did happen; the message says
  so. Nothing automated consumes this script's exit code today as far as I can see, but I
  did not audit for that.
- **No attempt is made to recover a pool that will not start** - no retry, no rollback of
  the binaries. Rolling back on a start failure is arguably right and is a bigger decision
  than this finding carries.
- `Stop-AppPoolChecked` in the same script still uses a bare `Stop-WebAppPool` plus
  `Start-Sleep 3` - the `aud-a35` pattern, in a third script that finding did not cover.
  Filed as `aud-a38b`.

## Closeout

Fixed on `master` as `3fc5e6e`.

## Independent review - `codex`, 2026-10-20

Reviewed as batch 2 of the backlog, with the other three deployment findings. Prompt kept at
`.agents/review/prompt-backlog-2.md`.

**This record previously said review was "currently impossible".** That was wrong: every
probe had passed `--model gpt-5.5`, stripping the Portkey routing prefix out of the
configured model slug. `.agents/state.md` carries the correction.

**No new defects.** The reviewer traced all three verdict flows and found them correct:
promotion succeeds + restart fails throws "promotion completed, application down";
promotion fails + rollback succeeds + restart fails reports both; promotion fails + rollback
fails + restart succeeds reports the rollback failure. Exit status is non-zero in every
failure case.

It confirmed that `aud-a38b` - the promotion script still stopping its pool with a bare
`Stop-WebAppPool` and a fixed sleep - is honestly filed rather than a false claim, and
restated it as HIGH. That follow-up remains unapproved work.

Remediation landed in the follow-up commit, each fix mutation-proven - including a
"present but dead" mutation, because a guard that only checks a condition exists would pass
with its refusal turned into a warning.
