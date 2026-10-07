# prog-1: The progress guard does not prove the slow derive is inside the window

**Severity**: MEDIUM - the runtime code is correct today, but the new guard is vacuous for
one of the two things the fix promised
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: the commit carrying this record

## Evidence

`ExchangeAdminWeb.Tests/ProgressRegistry.cs:395` registers
`CloudPasswordReset.ExecuteResetAsync` as `Reports` with
`CoveredCall = "ResetService.ResetPasswordAsync("`.

The handler does TWO slow things: the documented ~30-second destination derive at
`Components/Pages/CloudPasswordReset.razor:427` and the PATCH at `:464`. The registry
assertions (`ProgressRegistryTests.cs:540`, `:587`) compare `Progress.Begin`/`Complete`
against the registered covered call ONLY. `CloudPasswordResetWritePathTests.cs:82` pins the
`Task.Run` text but asserts nothing about progress ordering.

Triggering condition: a later edit moves
`destination = await Task.Run(() => ResetService.DeriveDestination(fresh));` above the
`Progress.Begin` while leaving it before `ResetPasswordAsync`.

Predicted observable failure: every test still passes. The operator waits through the long
directory search outside the global activity - **the exact behaviour the S2 fix was written
to remove.**

## Coder dispute

None. The implementing agent chose the PATCH deliberately, reasoning that naming the LAST
heavy call also pins `Complete` into the `finally`. That reasoning is sound as far as it
goes and is why the entry looks defensible. It is also incomplete: naming the last call
proves nothing about anything before it, and the derive is the slower of the two.

**This is the fifth vacuous-guard finding in five days and the second on this exact feature.**
The pattern each time: an assertion that is true of the code as written, standing in for an
invariant it does not enforce.

## Approach

A `Reported` entry may now name SEVERAL covered calls, and every one of them is checked
independently by all three conditions - so `Begin` must dominate all of them and the activity
must still be open at all of them. One covered call cannot carry both ends of the window; two
can, and the shape generalises rather than special-casing this page.

`Reported` keeps its single-call constructor, so the other fifty-odd entries are untouched, and
gains a `(method, IReadOnlyList<string> coveredCalls, note)` form.
`CloudPasswordReset.ExecuteResetAsync` now names `ResetService.DeriveDestination(` as well as
`ResetService.ResetPasswordAsync(`. `TrueLastLogon.SearchAsync` is the second user: its note
already admitted two halves and it held only `LookupOnPremAsync`, so `LookupCloudAsync(` is
named too.

Three hardenings came with it, because they are the same failure class:

- An entry naming NOTHING would satisfy conditions 1 to 3 vacuously - each iterates the covered
  calls and an empty list has nothing to fail. `EveryReportedOperationNamesAtLeastOneCallAndNamesEachOnce`
  refuses the empty, blank and duplicated forms.
- Conditions 2 and 3 now check EVERY occurrence of a named call, not the first. A call made once
  inside the window and again after `Complete` is the same defect as one made outside it, and a
  first-occurrence check cannot see the second.
- A covered call is now looked for in CODE, not in text. `StripComments` already blanked comments
  so that prose naming a service could not satisfy condition 1; string literals were not blanked
  and did exactly the same thing. `StripStringLiterals` blanks their contents, same length so the
  indices still line up. It is used ONLY for locating covered calls: markup attributes are quoted,
  so blanking them would delete handler discovery, and `ActivitiesIn` keeps the raw body so a
  blanking mistake can only hide an activity from itself - the direction that fails a test rather
  than passing one.

## Files changed

- `ExchangeAdminWeb.Tests/ProgressRegistry.cs`
- `ExchangeAdminWeb.Tests/ProgressRegistryTests.cs`

## Guard proof

The mutation named in the finding, applied to `Components/Pages/CloudPasswordReset.razor`: the
derive moved above `Progress.Begin` (as `ResetService.DeriveDestination(target!)` above the
`using var activity`, deleted from its place at :427).

| Registry entry | Mutated page | Result |
| --- | --- | --- |
| the new two-call entry | derive hoisted above `Begin` | FAIL - `AReportedOperationOpensItsActivityBeforeTheCallItCovers`: "ExecuteResetAsync opens no activity before 'ResetService.DeriveDestination(' at offset 1475" |
| the old PATCH-only entry | the same hoisted derive | PASS, 12/12 - the defect, reproduced |

The second row is the point: the mutation is identical and only the registry entry differs, so
what catches it is the fix and not the mutation being clumsy. Page restored, `touch`ed, 12/12.

A second mutation, for the literal hardening: the real derive replaced by
`var literalProbe = "ResetService.DeriveDestination(";`. Before the blanking it passed 12/12 -
observed, not predicted. After it, `AReportedOperationContainsTheCallItClaimsToCover` FAILS with
"ExecuteResetAsync does not call 'ResetService.DeriveDestination('". Page restored, `touch`ed,
12/12.

## Known gaps

Recorded rather than closed, all of them the same root cause - **regex over source cannot see
control flow.** Bypasses attempted against the new assertions, and what survived:

- **String-literal laundering: found, demonstrated, and CLOSED in this commit.** Left here
  because the next person needs to know the blanker exists and why. `SkipLiteral` is imperfect on
  an interpolated string holding a nested quoted string (`$"{d["k"]}"`), so the blanking can run
  to the wrong closing quote. That mis-blanks code, which fails condition 1 or 2 - it cannot
  manufacture a pass, because the covered call can only be erased, never conjured, and activities
  are still found in the raw body.
- **A covered call named loosely proves little.** `M365GroupManagement.RunMemberOpAsync` names
  `op(`, which `Stop(` and `Pop(` contain; `AccountLockoutRemediation.RunAsync` names `work(`.
  Nothing stops a future entry naming `Task.Run(`. The registry cannot police the quality of its
  own names.
- **An activity that is never completed launders everything after it.** A
  `using var a = Progress.Begin(...)` with no `Complete` has `Complete == -1` and a scope reaching
  the end of the method, so it satisfies condition 3 for every call below it. The predecessor
  per-file guard in `GlobalProgressWiringTests` requires every begun activity to be completed
  somewhere in the file, which covers the adopted pages and not the rest.
- **The scanner only knows one spelling of the open.** `ActivityOpened` matches
  `using var X = Progress.Begin(` exactly: a block `using (var X = ...)`, an `await using`, or the
  same declaration wrapped across two lines is invisible, and the entry then fails condition 2.
  That is a false alarm rather than a false pass, so it fails safe.

Two directions were attempted and did NOT survive - the guard catches them:

- **Crossing a method boundary.** Extracting the derive into a page helper and calling the helper
  removes `ResetService.DeriveDestination(` from `ExecuteResetAsync`'s own body, so condition 1
  fails whether the helper is called inside the window or above it. Credit for a helper's work
  still requires an explicit `DelegatesTo`, which is the rule that caught `ConfirmUnblock`.
- **Crossing a branch.** An activity opened inside an `if` block with the covered call after that
  block fails condition 3: `EnclosingBlockEnd` walks forward from the declaration, so `ScopeEnd`
  lands on the `if`'s closing brace, before the call. An early `activity.Complete()` on a refusal
  path above the covered call also fails condition 3, because `ActivitiesIn` takes the FIRST
  `Complete` after the open - conservative in the right direction.

## Reviewer comments

Change review of `56e7f2c..ed7754f`, finding 1 of 2.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard

It also reported a limitation honestly: **it did not reproduce the full suite** - an earlier
full run stalled with no output and was stopped. It verified the focused
`ProgressRegistryTests` (11/11) and `git diff --check` on the range. The coder-side full-suite
result (3651 passed) therefore stands unconfirmed by the reviewer.

It tried and FAILED to construct a `DelegatesTo` chain laundering
`BlockedSenders.ConfirmUnblock` past the guard, and confirmed it is correctly a `KnownGap`.
It also confirmed the `Task.Run` hop is safe: `DeriveDestination` reaches the singleton
`ADDirectorySearchService`, which serialises on a `SemaphoreSlim`, and component state is
mutated only after the await resumes.

## Closeout

Fixed directly on `master` in the commit that carries this record. Verification on that commit:
build Release 0 errors, `dotnet test ExchangeAdminWeb.slnx` **3661 passed / 0 failed / 3 skipped,
3664 total** (the registry suite goes 11 -> 12 with the new anti-vacuity test),
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` clean, `git diff --check`
clean, ASCII only. Test-project only, so no version bump: the registry is not shipped
(`docs/ProgressCoverage-Plan.md`, Versioning). Row `[v]` in `.agents/review/index.md`: the fix is on main and guard-proved,
and the push is outstanding (`.agents/push-policy.md` is ask).

No verification round dispatched: MEDIUM, and `.agents/decisions.md` 2026-08-31 closes everything
below CRITICAL on the coder-side guard proof.
