# tsr-1: Coverage ratchet is set below the measured baseline

**Severity**: MEDIUM - the gate permits the exact regression it was added to prevent; a PR can
delete tests covering security-critical code and CI still passes.
**Status**: Verified
**Branch**: -- (default-branch mode)
**Commit**: `9f34d6c`

## Evidence

`tools/Test-CoverageFloor.ps1:28-30` records the measured baseline as 64.7% and then sets the
default floor to **64.0**. `docs/TestSuiteRemediation-Plan.md` claims the floor was "set at the
measured 64.7%, not at an aspiration". Both statements cannot be true.

Measured after the calendar work: **65.1%**. So the live slack is 1.1 points of
security-critical line coverage.

Trigger: any change that drops scoped coverage to anything at or above 64.0 - deleting a test
file, or adding uncovered code to a scoped path - exits 0.

## Predicted observable failure

CI reports a passing coverage gate while the coverage it exists to protect has fallen. The gate
is decoration in exactly the band where regressions are most likely: small ones.

## What

I rounded the floor down when writing it, out of a vague instinct to avoid a brittle gate, and
then wrote the plan as though I had not. The instinct was wrong on its own terms: measurement
here is deterministic - the same commit yields the same number - so there is no flake to absorb.
The only thing the slack buys is permission to regress.

## Approach

Pin the floor to the measured value and stop hand-maintaining two numbers that can disagree.

The floor moves into `.agents/review/coverage-floor.txt`, a single committed number the script
reads, so raising it is a visible one-line diff rather than an edit buried in a parameter
default. The script fails when the file is missing rather than falling back to a built-in
default, because a silently-defaulting gate is the same failure class as the empty-scope one it
already guards against.

Rounding is now explicit: the comparison uses the unrounded percentage, and the DISPLAY rounds
to one decimal. Comparing a rounded number against the floor would reintroduce sub-0.05 slack.

## Files changed

- `tools/Test-CoverageFloor.ps1` - read the floor from the committed file; fail closed when it is
  absent or unparseable; compare unrounded.
- `.agents/review/coverage-floor.txt` - the floor, `65.1`, with a comment on how to change it.
- `docs/TestSuiteRemediation-Plan.md` - corrected; it claimed the floor matched the measurement.

## Guard proof

`tests/ps/CoverageFloor.Tests.ps1`:
- a report just below the floor FAILS (exit 1) - this is the case the finding names, and it
  passed before the fix;
- a report at exactly the floor passes;
- a missing floor file FAILS rather than defaulting.

Reverting the floor to 64.0 makes the just-below-floor test pass, which is the vacuity check.

## Coder dispute (if any)

None. The finding is correct and the contradiction was mine: the script and the plan I wrote in
the same session state different floors.

## Known gaps

The floor still has to be raised by hand after coverage improves. Auto-raising it on every green
run would make the ratchet self-satisfying - it would record whatever happened rather than what
was intended - so this stays manual on purpose.

## Reviewer comments

`Reviewer: codex / default configured model (gpt-5.5-dzs @ xhigh) / standard`
Harness: codex-cli 0.146.0 (`codex exec`, generation pass, no `--model` flag - owner asked for the
default configured model).
Range: `802ea74b293760cd22821cb5220dc46daadfbbff..2543fb90479302979a928ca6058881be03174e91`
(both SHAs echoed correctly). `capability_ok: true`. Verdict: **findings** (1).
Timestamp: 2026-08-03T19:05Z.

Notable: the reviewer was asked to scrutinise two specific claims - that the extractions were
behavior-neutral, and that the coverage gate could not pass vacuously. It cleared the first and
found the defect in the second. The gate I added to catch regressions was itself the regression
risk.
