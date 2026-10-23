# Defect review request: test-only - ExtendedLogService test waits share one bound; the drain poll and writer holds run on it (kd-22, kd-25)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `4c2cfccd` or later.
- The change is two commits on `fix/a29g`: `518f1f18` (kd-22, merged `e4a15a5b`) and `e3b30f71` (kd-25, merged `4c2cfccd`), on top
  of `9762fef0` (kd-16, already reviewed clean in auth-batch-3). Review `git diff 9762fef0 e3b30f71 -- . ':!.agents'`: only
  `ExchangeAdminWeb.Tests/ExtendedLogServiceTests.cs` (a class-level `ConsumerWait` of 2 minutes used by eight consumer waits,
  kd-16's consumer-start wait, the drain poll in `AFullQueue_CountsTheEntriesItDiscards` (now a deadline from release, 100 ms
  polling) and the three writer `release.Wait` holds). No production code changed.
- You may read any file and run read-only git commands. One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/kd-22.md`, `kd-25.md`; parent `kd-16.md`. Out of scope: `Services/ExtendedLogService.cs`
  itself (aud-a64 (5), another branch), `aud-a67` (7).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat e3b30f71`. Report whether both succeeded.

## Mandate

Hunt for observable defects in the tests as guards: for each changed wait, name the regression the test guards and whether a
2-minute bound can still catch it (a bound that only makes failure slower is fine; a bound that lets a wrong count or a
dropped entry pass is not); the drain poll's deadline-from-release - can the read-back inside the poll now run past the
deadline and assert on a partially drained queue; the writer holds - with a 2-minute hold, can a test that fails before
releasing the consumer leak a parked thread into the next test in the same class (xUnit runs tests in a class serially,
but the collection fixture?) and change its result; any remaining wall-clock assumption not driven by `ConsumerWait` or a
deliberately measured setting; and whether the proof method (a 75 s injected stall) exercised the same code path as the
real failure the record describes.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.