# blr-4: a running search showed nothing at all, so the page read as hung

**Severity**: MEDIUM — no wrong answer is stated, but an operator on a live recovery call who
believes the app has frozen will reload and start over, losing the search. Not HIGH because
nothing incorrect is asserted and the results do arrive.
**Status**: Verified
**Commit**: `b31ff66`
**Branch**: — (default-branch mode)

Found by the owner on prod, immediately after `blr-3` was deployed. **Caused by the `blr-3` fix.**

## Evidence

- `Components/Pages/BitLockerRecovery.razor:90` (pre-fix) —
  `@if (searched && !isSearching && errorMessage == null)` gates the entire results region.
  `blr-3` added `!isSearching` to stop a stale result rendering during a search, and nothing else
  renders in that state, so the region is empty for the whole query.
- `Components/Pages/BitLockerRecovery.razor:56` — the only in-flight feedback is a spinner inside
  the Search button, which is small, above the fold, and easy to miss on a wide page.

Trigger: any search with **Search live Active Directory too** ticked. Live AD takes seconds; the
archive alone is fast enough that the gap was invisible in testing.

## Predicted observable failure

Tick the box and search. The results area is blank for several seconds with no indication that
anything is happening, then results appear. Reported as "just appears hung until results appear".

## What

`blr-3` correctly stopped the page asserting a completed result while a search was running, but put
nothing in place of what it removed. Silence is not a neutral state here: it is a different wrong
answer, and the one that invites a reload mid-search.

## Approach

An in-flight indicator in the results area — where the answer will appear, not only in the button.
The message names live Active Directory when it was requested and explicitly asks the operator not
to retry, because a generic spinner sets no expectation about a multi-second wait.

`SearchAsync` now calls `StateHasChanged()` followed by `await Task.Yield()` before doing any work.
This is load-bearing rather than defensive: `Microsoft.Data.Sqlite`'s `*Async` methods complete
**synchronously**, so the entire archive query can run without the handler ever yielding to the
renderer. Without the explicit yield, the indicator would not paint until some later await happened
to be genuinely asynchronous — on an archive-only search, potentially not until the method returned,
which is exactly the reported symptom.

## Files changed

- `Components/Pages/BitLockerRecovery.razor` — in-flight indicator block; forced render + yield in
  `SearchAsync`.
- `ExchangeAdminWeb.Tests/BitLockerRecoveryTests.cs` — 3 tests.

## Guard proof

`Page_ShowsAnInFlightIndicatorWhileSearching`, `Page_WarnsThatALiveDirectorySearchIsSlow`,
`SearchAsync_PaintsTheSearchingStateBeforeDoingTheWork`.

Each proven against the specific mutation it targets: disabling the indicator block fails the first
and third; genericising the wait wording fails the second. Restoring passes 48.

**The first cut of two of these guards was false coverage, and the probe caught it.** They checked
for `@if (isSearching)` and the message text as separate substrings — both still matched with the
block disabled to `@if (false)`, because the condition also appears on the Search button's spinner
and the text survived inside the dead block. A test that a broken page satisfies is worse than no
test, since it reads as coverage. Both now anchor the condition to the alert markup it gates, and
the live-AD assertion is anchored to the enclosing `isSearching` block rather than to its own inner
branch.

## Coder dispute (if any)

None. Self-inflicted by `blr-3`.

## Known gaps

The indicator cannot show progress through the nine-plus AD queries a broad search issues — the
service reports no intermediate state. It sets the expectation of a wait, which is what stops the
reload; a progress count would need a service-level change not warranted here.

## Reviewer comments

Not dispatched. Owner-reported from prod; fix landing directly.
