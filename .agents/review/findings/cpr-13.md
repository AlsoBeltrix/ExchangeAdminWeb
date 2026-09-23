# cpr-13: A guarantee about capitalisation was implemented as a tendency

**Severity**: MEDIUM - cosmetic in effect. AC14 states that no two adjacent words share a
capitalisation style; the code tried twenty times and then shipped whatever it had.
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: the commit that completes this record

## Evidence

`Services/PasswordGenerator.cs` as of `c219a27`: `ShuffleAvoidingAdjacentDuplicates` shuffled,
re-shuffled while `attempt < 20 && HasAdjacentDuplicate(styles)`, and returned regardless.

`docs/CloudPasswordReset-Plan.md` AC14 states the property absolutely, and the method description
presents it as part of the algorithm rather than as best-effort.

**And my own test encoded the weaker claim.** It counted violations across 500 passwords and
asserted fewer than 100 - so it would have passed with the rule half-working, and it passed with
it not guaranteed at all.

## Predicted observable failure

A 4-, 5- or 6-word password is returned with two neighbouring words in the same style - both
ALLCAPS, say. Cosmetic rather than a strength problem, but the plan claims otherwise and the
entropy score counts style arrangements as though the constraint holds.

## Approach

Keep the shuffle attempts, which is what makes the arrangement random in the ordinary case, and
add a deterministic repair behind them so the property holds absolutely.

**The first repair was wrong and the process is the point.** It was a local swap pass; on
`[0,0,1,1,2,2]` it fixed the head and stranded a collision in the tail, because by the time it
reached the last pair there was nothing left to swap with. It is now a greedy rebuild by remaining
count - the standard arrangement for this problem, correct whenever no style takes more than half
the positions, which these multisets never approach.

## Files changed

- `Services/PasswordGenerator.cs` - `RepairAdjacentDuplicates`.
- `ExchangeAdminWeb.Tests/PasswordGeneratorTests.cs` - the end-to-end assertion tightened from
  "fewer than 100" to zero.
- `ExchangeAdminWeb.Tests/PasswordGeneratorWordListTests.cs` - `PasswordGeneratorStyleRepairTests`.

## Guard proof

Mutation: disable the repair. Five of the direct repair tests fail, naming the multisets.

**The end-to-end test cannot carry this guard and that is recorded rather than glossed.** Removing
the repair was mutation-probed against the 500-draw test FIRST and it stayed green: twenty random
shuffles almost always succeed, so the repair rarely runs and sampling cannot see it. A guarantee
asserted only by sampling a rare event is not asserted. The direct unit test is the one that
bites - and it is also what caught the first repair being wrong, which the draw test would never
have shown.

## Coder dispute

None.

## Known gaps

The greedy fallback is deterministic, so in the rare case it runs the style arrangement is not
random. The entropy score counts style arrangements as a fixed quantity regardless, so this does
not make the score dishonest - but the arrangement in those cases carries less real entropy than
the number suggests. It is a fraction of a bit on a rare path, against a floor of 60.

## Reviewer comments

Round 1, Change review over `c219a27^..c219a27`. Same dispatch as `cpr-12`.

## Closeout

Fix and this record land with cpr-12 in one commit.
