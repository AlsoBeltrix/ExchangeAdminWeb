# cons-2: a busy-wait test asserts an elapsed time under five seconds after a one-second hold

**Severity**: LOW - flaky test, the aud-a67 (7) class but outside the sub-item's named scope.
`ExchangeAdminWeb.Tests/SqliteBusyWaitTests.cs` `ALockReleasedWithinTheWait_LetsTheWriteThrough` asserts
`elapsed < BusyTimeout` (5 s) after holding the lock 1 s; on a loaded host the write can legitimately take longer.
**Status**: Merged to master `4afa745d`; codex defect review settings-batch-9 in flight
**Branch**: `fix/a64-a67` (from `eb017102`)
**Commit**: see below
**Reviewer**: implementer (aud-a64/a67 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `d0fc4c70`. Trigger: CPU or disk saturation. Predicted failure: "the write waited N s for a lock held 1 s" on a run
with no defect.

## Approach

- Keep the structural assertion (the write succeeded, no 30 s retry loop) and loosen or remove the wall-clock ceiling, as
  aud-a67 (7) did for its two; test-only; proof by the injected-delay method.


### Implementation notes (fix/a64-a67, 2026-10-26), at `b70b46e9`

The ceiling was `< SqliteConnectionFactory.BusyTimeout` (5 s) for a lock held 1 s, so it failed whenever the write took
four seconds longer than the hold to be scheduled - which a host running several builds and test runs at once can do
with nothing wrong. It is `NotTheRetryLoop` (25 s) now, the constant aud-a67 (7) added for its two, and the comment
says what the assertion is for: the provider's retry loop, measured at 30 to 35 s.

Kept, because they are what this case actually proves and neither drifts with load:
- the floor (`>= 0.8 s`), which shows the write WAITED for the lock rather than finding it free - load pushes a wait
  up, never below the hold;
- the row read back afterwards, which shows the write went through once the lock was released.

## Files changed

- `ExchangeAdminWeb.Tests/SqliteBusyWaitTests.cs`.

## Guard proof

Filter `FullyQualifiedName~SqliteBusyWaitTests|FullyQualifiedName~SettingsStoreBusyTests` (25 tests).
`SqliteConnectionFactory.cs` MD5 `42ed78d643da6fe413171bb262fe7dfb`, matched after the row; 25/25 green after the
restore.

| Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|
| Q1 `SqliteConnectionFactory.Open`'s `DefaultTimeout` removed - the aud-a41 defect this ceiling class exists for | `c2595784ca4c5bd2492540b81f093a3d` | 3 of 25: `AWriteThatMeetsAHeldLock_FailsBusy_AfterTheDocumentedWait` ("the write waited 31.62 s"), `AFactoryConnection_WaitsTheBusyTimeout_AndTheProviderRetriesNoLonger` (`DefaultTimeout` 30 where 5 was expected, in 14 ms), `SettingsStoreBusyTests.AdminSettings_ASecretIdCompareThatFindsTheStoreBusy_...` ("the save waited 30.74 s") |

**This case does not itself fail under Q1, and that is the point of the change.** Its lock is released after one
second, so the write goes through in about a second whether the provider would retry for 5 s or for 30 - the retry
loop only costs anything when the lock is NOT released. The defect that the old ceiling appeared to guard is caught,
in the same run, by the two assertions above that are built for it. What this case proves is its own property: a lock
released inside the wait lets the write through, and the row is there afterwards. The loosened ceiling is a bound on
pathology, not the case's evidence, and no mutant makes it bite without changing the case's own premise - which is
why it is loose rather than tight.

## Known gaps

- No mutant drives this case past 25 s; doing so would mean holding the lock longer, which turns it into a different
  case (the one its sibling already covers).
- `ANewDatabaseFilesSwitchToWal_WaitsForALockAsLongAsAnyOtherStatement` keeps a floor of 1.5 s against a 2-second
  hold. A floor is safe under load, so it is left as it is.

## Branch-tip gates - 2026-10-26 (`fix/a64-a67`, the cons-2/3/4/5 round)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 19 warnings - the same count as the fork, none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4`: 6,745 passed / 0 failed / 0 skipped, 4m40s. The
  previous tip of this branch was 6,742; these four records add 3 cases net (cons-4 adds three and retargets one,
  cons-5 extends an existing theory rather than adding cases, cons-2 and cons-3 add none).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `pwsh -NoProfile -File tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
- Not run: `Invoke-ScriptAnalyzer` and `Invoke-Pester tests/ps` - no PowerShell file changed. No directory, Exchange
  Online, Graph, DHCP, IIS, Delinea or SMTP call; nothing deployed.
