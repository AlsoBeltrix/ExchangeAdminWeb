# aud-a29k: two PageRequestRevalidation tests hold a directory read open under a 10 s real-time harness timeout and can fail under load

**Severity**: LOW - flaky test. `ExchangeAdminWeb.Tests/PageRequestRevalidationTests.cs` about 404 (`AnAnswerFromAReadThatStartedEarlier_DoesNotReplaceALaterOne`) and 437 (its sibling) hold a read open while other steps run; the harness lookup timeout (about 697) is 10 s of wall time, so a stall under load turns the early check into `LookupFailed`. Seen once in about 76 runs on a saturated host; no race found in the code.
**Status**: Verified on branch; awaiting merge - admitted 2026-10-23 evening from the aud-a29g agent's second hand-back; inside the released scope
**Branch**: `fix/a29g` (worktree `ExchangeAdminWeb-wt/a29g`), after `0526dc83` (aud-a29j)
**Commit**: the `fix(aud-a29k)` commit on `fix/a29g` that carries this record
**Reviewer**: implementer (aud-a29g agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `d58b5d7c`. Trigger: CPU saturation during the test. Predicted failure: a false red on CI or a loaded dev box.

## Approach

- Give both tests a lookup timeout far above any stall (or drive the clock deterministically); no behaviour change; the test's own re-run under an injected stall proves it.

### Implementer's approach (written before code, at `0526dc83`)

The two real-time bounds the tests run under, quoted from
`ExchangeAdminWeb.Tests/PageRequestRevalidationTests.cs`:

```csharp
697            Options = new CircuitRevalidationOptions(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(10), enabled);
...
574    private static async Task WaitForReadsAsync(Harness h, int reads)
575    {
576        var deadline = DateTime.UtcNow.AddSeconds(10);
```

`AnAnswerFromAReadThatStartedEarlier_DoesNotReplaceALaterOne` (404) and
`AFailureOfAReadThatStartedEarlier_DoesNotForgetALaterAnswer` (437) are the only users of
`WaitForReadsAsync` and the only tests here that hold a directory read open while other steps run.
Exposure, traced: in 404 the early check's 10 s budget runs from line 416 until line 424 answers it,
across a whole circuit check; a stall that long makes it `LookupFailed` and line 425 fails. In 437
the early check's timeout does not matter (it is expected to fail anyway), but the circuit check at
451 has its own 10 s budget, and a stall in it makes line 451 fail. Both also fail if the first read
takes more than 10 s to be asked (576).

Chosen: **a lookup timeout far above any stall, for those two tests, with the read-wait tied to it.**
- `Harness` takes an optional `lookupTimeout` (default the 10 s every other test keeps).
- The two tests use `SlowReadLookupTimeout` = 2 minutes - twelve times the longest stall seen (the
  failing run took about 10 s longer than usual).
- `WaitForReadsAsync` waits as long as the harness's lookup timeout instead of a fixed 10 s, so the
  two bounds are one knob.

Not taken: driving the clock deterministically. The lookup timeout is enforced by real timers
inside `CircuitMembershipRevalidator.CheckAsync` (`CancelAfter`, `WaitAsync(timeout)`); moving them
onto the test clock changes T1 authorization code to fix a test, and the timeout itself is proved
with real timers in `CircuitRevalidationTests`. A genuine hang in either test now fails within 2
minutes rather than 10 s; the run's own hang guard is 10 minutes.

Files (declared): `ExchangeAdminWeb.Tests/PageRequestRevalidationTests.cs` and this record. No
production code changes.

Proof (not committed): a 12 s stall injected where each test is exposed - in 404 while the early read
is open, after `WaitForReadsAsync`; in 437 inside the circuit check's directory read - run with the
fix (both pass, the stall absorbed), then with the fix reverted to the 10 s harness (both fail),
then the final bytes restored (md5-equal, both pass).

## Files changed

- `ExchangeAdminWeb.Tests/PageRequestRevalidationTests.cs` - `Harness(lookupTimeout:)`,
  `SlowReadLookupTimeout`, the two tests on it, `WaitForReadsAsync` bounded by the harness timeout.
- this record.

## Guard proof

As the approach, no change of scope; no production code touched. Final test file md5
`706B1308FB14365AC84243389E0124B4`.

**The injected stall** (`a29k-stall.ps1`, session scratchpad; the injections are never committed). A
12 s stall - above the old 10 s budget, far below the new 2 minutes - put where each test is exposed:
in 404, `await Task.Delay(12 s)` after `WaitForReadsAsync`, while the early read is open; in 437, the
circuit check's directory read answers through `Task.Delay(12 s)`. Each variant rebuilt
(`ExchangeAdminWeb.Tests.csproj -c Release`) and the two tests run:

| Variant | md5 | Result |
| --- | --- | --- |
| V1 the stall, with the fix | `79E3F41E02223CADE11067B10CC27EC1` | **both passed** (24 s): the stall is absorbed |
| V2 the stall, the fix reverted (both tests back on `new Harness()`, so 10 s for the checks and the read-wait) | `117A901ADE9C157A760F9BB3365C8B39` | **both failed**, `Assert.True() Failure` each - 404 at `(await early).IsValid`, 437 at `provider.ValidateAsync`, the failures the record predicts |
| V3 the final bytes restored, fresh mtime | `706B1308FB14365AC84243389E0124B4` (md5-equal) | both passed (627 ms) |

Every other test in the class keeps the 10 s default and passes unchanged; the class with
`CircuitRevalidationTests` passed 125/125 on the final file.

Gates, on the final tree:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; no warning in the touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m -- xUnit.MaxParallelThreads=4`:
  **6717 passed, 0 failed, 0 skipped, 6717 total** (8 m 8 s); nothing hung.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check` (staged and against `HEAD`): clean. `tools/Test-AsciiOnly.ps1`: passed.

## Coder dispute

None.

## Known gaps

- **Only the two tests that hold a read open moved.** The rest of the class keeps the 10 s harness
  default: none of them holds a read across other steps, so a check of theirs would have to stall
  10 s by itself to fail. A class-wide default was not taken, to keep the change to what was seen.
- **A genuine hang in either test now takes up to 2 minutes to fail**, inside the runs' 10-minute
  hang guard.

## Closeout

Pending: review, merge to `master` by the supervisor. Test-only: no version bump.