# blr-2: a capped live search that found nothing reports itself as complete

**Severity**: MEDIUM — it produces a confidently wrong answer on a live recovery
call ("this machine has no key on file") when the truth is "I stopped looking".
Not HIGH: it needs a broad search term, it discloses nothing, and the operator can
recover by narrowing the search — if they think to.
**Status**: Verified
**Branch**: — (default-branch mode)
**Commit**: `<pending>`

## Evidence

- `Services/BitLockerLiveDirectorySearch.cs:63` — the computer query is capped at
  `limit + 1` candidate computers.
- `Services/BitLockerLiveDirectorySearch.cs:120` — `truncated` is set when more
  computers matched than the cap, and it is set correctly.
- `Components/Pages/BitLockerRecovery.razor:97-113` — the zero-results branch never
  reads `truncated`, and states the search "was searched successfully".
- `Components/Pages/BitLockerRecovery.razor:116` — the truncation notice is rendered
  only inside the `else` (non-empty) branch, so it cannot appear when the result set
  is empty.

Triggering condition: a live search whose term matches more computers than the
result limit, where none of the first `limit + 1` computers examined has a readable
recovery key, but a later one does. With the default limit of 50, a term like `LAP`
in a laptop-heavy estate reaches this easily.

The same hole exists on the archive side: `BitLockerRecoveryService` sets `Truncated`
correctly, but the page ignores it whenever the rendered set is empty. The archive
case is harder to hit (`LIMIT` applies to matching key rows, not to candidate
machines) but the display defect is identical, so the fix belongs at the page and
covers both.

## Predicted observable failure

Run a broad live-AD search that caps out with no keys among the examined computers.
The page displays "No recovery keys found. Live Active Directory and the archive
were searched successfully." — an explicit claim of completeness — while matching
machines with keys were never examined. The operator tells the caller no key exists
and stops looking.

This is the exact failure mode the module's fail-closed rule exists to prevent:
"no key exists" and "I stopped looking" must not render identically.

## What

Truncation is computed correctly in both services and then dropped by the page in
the one case where it changes the meaning of the result: when there is nothing to
show. A truncated empty result is not an answer, and it is currently presented as
the most definitive answer the page can give.

## Approach

Fixed in the page's rendering, because the services already report the fact
correctly — this is purely a display defect and no service change is warranted.

The zero-results branch now tests `truncated` first and, when set, states that the
search stopped at its limit and must be narrowed, instead of claiming success. The
existing warning banner and the non-empty truncation notice are untouched.

Extracted as `internal static NoResultsMessage(bool truncated, bool warned, bool searchedLiveAd)`
so the four-way message choice is unit-testable without a component host, matching
`AuditSearchTarget` (blr-1) and the reasoning `MessageTraceExportListing` records.
The markup calls the helper, so page and tests cannot drift.

Precedence: truncated wins over warned, which wins over the two complete-search
messages. A truncated search is the strongest reason not to trust an empty result.

## Files changed

- `Components/Pages/BitLockerRecovery.razor:97-113` — zero-results branch renders
  `NoResultsMessage(...)`.
- `Components/Pages/BitLockerRecovery.razor` — new `NoResultsMessage` helper.
- `ExchangeAdminWeb.Tests/BitLockerRecoveryTests.cs` — 6 tests.

## Guard proof

`ExchangeAdminWeb.Tests/BitLockerRecoveryTests.cs::NoResults_TruncatedSearchDoesNotClaimCompleteness`
and the five beside it. Removing the `truncated` branch from `NoResultsMessage` makes
them FAIL; restoring makes them PASS.

`NoResults_TruncatedTakesPrecedenceOverEveryCompleteMessage` is the one that matters:
it pins that no ordering of the other conditions can resurrect a completeness claim
for a capped search.

## Coder dispute (if any)

None on the defect. One scope note: the reviewer's `better_approach` also suggested
"continuing or paging the live search until the key result limit is reached". That
is a behaviour change to the search strategy, not a defect fix — it would make a
broad live search arbitrarily more expensive against a production DC, and the cap
exists deliberately to bound that. Not implemented; the misreporting is fixed, which
is the defect. Raising the cap is a config change an operator can already make.

## Known gaps

The message tells the operator the search was capped and to narrow it, but cannot say
how many machines went unexamined — the services report truncation as a boolean. That
is sufficient for the decision the operator has to make.

## Reviewer comments

To be filled by the verification dispatch.
