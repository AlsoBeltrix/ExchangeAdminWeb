# blr-3: a second search rendered a false "no keys found" while the live query was still running

**Severity**: HIGH — it states, in the operator's own words and with an explicit claim of
completeness, that a machine has no recovery key on file, at the moment an operator on a
live recovery call is looking for one. Live AD takes seconds, so the false answer is on
screen long enough to be read and acted on.
**Status**: Verified
**Branch**: — (default-branch mode)
**Commit**: `c615955`

Found by the owner on dev, not by review or tests. Reported first as "the live AD search box
returns an immediate no results even after running an archive only search returns results".

## Evidence

- `Components/Pages/BitLockerRecovery.razor:85` (pre-fix) — `@if (searched && errorMessage == null)`
  gates the whole results region on `searched` alone.
- `Components/Pages/BitLockerRecovery.razor:246-250` (pre-fix) — `SearchAsync` sets
  `isSearching = true` and empties `results` and `warnings`, but **never clears `searched`**.

So from the second search onward, `searched` is still `true` from the previous one while the new
query is in flight, and the emptied `results` renders through the zero-results branch.

The owner's reproduction isolates it exactly:

| Sequence | Result |
| --- | --- |
| Load page, tick box, search | Works. `searched` is still `false`, so nothing renders until results arrive |
| Load page, search unticked, tick box, search again | Flashes "No recovery keys found... searched successfully", then the real rows appear |

The first case is why this survived: the obvious manual test passes.

## Predicted observable failure

With the live-AD box ticked, run any second search. For the duration of the AD query the page
displays "No recovery keys found. Live Active Directory and the archive were searched
successfully" — then replaces it with the keys it just claimed did not exist.

## What

An in-flight search rendered as a completed one. Not a search defect: the service was correct
throughout and returned the right rows, which is why the audit log recorded `Success` and the
application log had nothing to say.

## Approach

Two changes, either of which alone would fix the symptom; both are kept because they fail
differently.

1. `:90` — the render gate is now `searched && !isSearching && errorMessage == null`. Nothing about
   a result set is asserted while a query is running.
2. `SearchAsync` — clears `searched` (and `truncated`) before awaiting. Retracts the previous
   answer rather than leaving it standing over emptied data, so even a later relaxation of the
   render gate cannot resurrect a stale completed answer.

`truncated` was reset for the same class of reason: it also survived a search, so a narrow second
search could inherit the first one's cap warning and its `alert-warning` styling.

## Files changed

- `Components/Pages/BitLockerRecovery.razor` — render gate, plus the state reset in `SearchAsync`.
- `ExchangeAdminWeb.Tests/BitLockerRecoveryTests.cs` — 3 tests.

## Guard proof

`SearchResults_AreNotRenderedWhileASearchIsStillRunning`,
`SearchAsync_RetractsThePreviousAnswerBeforeRunningTheNextOne`,
`SearchAsync_ResetsTruncationBetweenSearches`.

Reverting all three changes fails all three tests; restoring passes 45.

Recorded because it nearly produced a false proof: the first probe attempt used `\r\n`-suffixed
replacements that silently matched nothing, so only 1 of 3 guards fired. That looked like two weak
tests. Verifying the file contents after the edit — rather than trusting the reverting script —
showed the revert itself had not applied.

These are source assertions, not behavioural coverage: the condition lives in Razor markup and this
repo has no bUnit harness. Same instrument and same justification as
`MessageTracePageRoutingTests`.

## Coder dispute (if any)

None. Reproduced from the owner's description and confirmed against the source.

## Known gaps

The render gate is the only thing preventing a stale render, and a rapid double-submit is not
otherwise serialised — the Search button is disabled while `isSearching`, which is what makes that
adequate here. A page that gained a second trigger for `SearchAsync` would need a request token,
as `MessageTrace.razor` uses for its detail fetches.

## Reviewer comments

Not dispatched. The owner found this defect after the codereview pass on
`81fd069..e39e18f` returned two other findings; this fix is landing directly.

## Follow-on defect this fix caused (blr-4)

Suppressing the stale result left **nothing** in its place, so the results area went blank for the
seconds a live AD query takes. Owner, from prod: "just appears hung until results appear". Fixed by
`blr-4`, which adds the in-flight indicator this fix should have shipped with.

The lesson is narrow and worth keeping: **removing a wrong answer is only half a fix.** A blank
region is not a neutral state - an operator who believes the app has hung reloads and starts over,
which on a recovery call is worse than the flicker it replaced. When a guard suppresses output,
something true has to take its place.

## How three passes missed it

Worth recording, because the pattern is now repeated:

- The codereview pass reviewed a diff. This defect is not visible in the added lines — it is an
  interaction between a pre-existing render gate and a state field that the diff never touched.
- Both my earlier review rounds read the service and the fail-closed paths, where the risk looked
  concentrated, and treated the page as a thin shell over them.
- Every automated check passed, and would still pass: no test can render this page.

The common thread with blr-2 and the MessageTrace historical branch: **the service was right and
the page was wrong, and the page is the only part no test can see.**
