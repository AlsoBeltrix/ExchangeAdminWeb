# aud-a17: a Graph collection that failed partway through was returned as if complete

**Severity**: HIGH - the caller receives a short list with no indication it is short. For
group membership and for named locations, "absent from the list" is an answer an operator
acts on.
**Status**: Verified and fixed; review pending
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 17 of the 2026-10-16 audit, verified against the code. Two sites, in different
states of wrong.

**`Services/M365GroupManagementService.GetPagedMembersAsync`** was the worse of the two:

```csharp
using var doc = await client.GetAsync(url);
if (doc == null) break;
```

`GetAsync` discards the status, and `break` returns whatever had accumulated - **including on
the very first page**, where the result is an empty list that reads as "this group has no
members".

**`Services/NamedLocationsService.GetAllAsync`** already handled the first page correctly
(it throws, with a comment saying why). A later page failing logged a warning and returned
the partial list. The log was the only trace, and nobody reads logs to decide whether a list
is whole.

Known Failure Class 2 in `.agents/repo-guidance.md`, and the same class as `aud-a18`.

## Predicted observable failure

An operator checks who is in an M365 group while Graph is throttling. Page two fails. The
page shows the first 999 members, the operator does not find the person they were looking
for, and concludes they are not a member. Nothing on screen says the list stopped early.

For named locations the inversion is worse: a missing entry reads as "that network is trusted
nowhere", which is an argument for widening conditional access.

## Approach

Both now refuse rather than truncate - the standing rule that inaccurate data is reported, not
worked around.

- `GetPagedMembersAsync` uses `GetWithStatusAsync` so the status reaches the message, and
  throws on any page failure. The message distinguishes a first-page failure from a partial
  one and says how many had been read, because "it failed" and "you are throttled after 999"
  need different responses from whoever reads it.
- `NamedLocationsService` throws on a later-page failure too, keeping its existing first-page
  behaviour and its warning log.

A partial list is not offered as an option anywhere. There is no way to hand one back that a
caller cannot mistake for the whole set, and both lists are used to decide who has access to
something.

## Files changed

- `Services/M365GroupManagementService.cs`
- `Services/NamedLocationsService.cs`
- `ExchangeAdminWeb.Tests/GraphPagingTruncationTests.cs`

## Guard proof

**Behaviour, not a source scan.** A routing stub handler stands in for the whole network -
Delinea's token and secret endpoints, the Graph token endpoint, then the paged collection -
so the real service, the real `GraphTokenClient` and the real paging loop all run. The second
page is the one that fails.

| Test | What it pins |
| --- | --- |
| `ASecondPageFailure_RefusesInsteadOfReturningAShortList` | the refusal, and that the message says how much had been read |
| `AllPagesSucceeding_StillReturnsEveryLocation` | both pages are still collected; a fix that always threw would satisfy the first test alone |

The failing test also asserts `SecondPageRequests > 0`. **That assertion earned its place
immediately**: the first run of this test failed on it rather than on the behaviour, because
`DelineaService` had no bootstrap credentials and returned null for every secret - the paging
loop was never reached and the test would otherwise have passed for the wrong reason.

| Mutation | Result |
| --- | --- |
| restore the silent `break` on a later page | FAIL - `ASecondPageFailure_...`, and only it |

Source restored byte-identical (md5 verified), touched, rebuilt.

Gate: Release 0 errors; suite 4342 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean. `NamedLocations` 1.2.3 -> 1.3.0, `M365GroupManagement` 1.4.2 ->
1.5.0 - minor rather than patch, because callers that previously received a short list now
receive an exception.

## Known gaps

- **Only the Named Locations path is covered by the behavioural test.** The M365 members path
  takes the same shape and the same fix, but constructing that service reaches considerably
  further, and it is not exercised here. That is the main thing a reviewer should attack.
- Callers of both methods now have to handle an exception where they previously got a list.
  The call sites were read and they surface errors, but no test drives a caller end to end.
- Nothing here ran against real Graph throttling.

## Closeout

Fixed directly on `master`. Independent review pending.
