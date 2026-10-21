# cdr-followup-1 - resolve-stage prose mixes address and member counts

Status: Merged to master; codex group review pending. (Was: open follow-up; outside the approved twenty
findings and shared replay repair.)
Branch: `worktree-agent-a6bb7956d82362f14`
Commit: `f2cfc6c` (merged)
Severity: LOW / P3. Native `/root/review_c11`, `gpt-6-astra` at max, 2026-10-15.
Module: Comms-10k.
Evidence head: `b0c535ee8d09f06405236200fda2e5dd177183d8`. The formatter is unchanged
from baseline `958cce71a973f45afa9cb3c892ce286e287aa930`.

## Evidence and consequence

Comms10kReplaceProcessor.ResolveAsync (lines 140-147, specifically 146 at the evidence
head) formats the distinct resolved member count as N of [input address count] matched
in AD. Two valid uploaded aliases for one person resolve to one GUID and zero rejections,
but produce 1 of 2 matched, falsely suggesting an unmatched address.

Comms10kAddressResolver (253-254) intentionally deduplicates by GUID. The existing
Comms10kAddressIdentityTests cross-batch test (line 13) supplies 501 addresses and asserts
500 members with zero rejections; the current formatter would report 500 of 501 matched.
BulkJobService.RecordRow (464) persists that stage message, and AdminBulkJobs (397) displays
row.Message. C11's requested, acknowledged and observed member counts remain correct.

This is a source-confirmed pre-existing wording defect. The dedicated alias-to-stage-message
integration test below has not been run. It does not block C11 or authorize expanding C5.

## Proposed follow-up

Scope separately after current authorized work. Label both quantities explicitly, such as
2 addresses resolved to 1 distinct member. Add a two-address/one-GUID processor case asserting
the persisted stage message and prove it fails with the old formatter. No code change is
approved by this record.

## Plan (approved by the owner's EVERYTHING go)

Owner, 2026-10-21: "fix all the bugs that you know about and any more you find while doing
so" and "... EVERYTHING." (`.agents/decisions.md`, 2026-10-21 "Every open review follow-up
is approved for implementation"). That approves this follow-up. The plan is the proposal
above:

1. **One formatter, beside the deduplication it describes.**
   `Comms10kAddressResolver.DescribeResolved(int resolvedAddresses, int distinctMembers,
   int? rejected = null)` returns `"2 addresses resolved to 1 distinct member; 0 rejected"`:
   both quantities named, singular at one, counts `N0` as the old message had them, and the
   rejected clause only when a count is passed (a caller that lists its rejections elsewhere
   omits it). It lives on the resolver because the resolver is where one person reached by
   two addresses becomes one member (`Resolve`, the `resolvedSet` GUID check).
2. **The resolve stage uses it**: `DescribeResolved(payload.Emails.Count -
   resolve.Rejections.Count, _resolvedMembers.Count, resolve.Rejections.Count)`. The stage has
   already refused any rejection by then, so the clause reads "0 rejected" and every uploaded
   address is one that resolved; writing the subtraction keeps the first count right by
   construction should that refusal ever move. `Resolve` walks every input address and gives
   each exactly one of a rejection or a resolution, which is what makes the subtraction exact.
3. **Counts consistent with the existing cross-batch case**
   (`Comms10kAddressIdentityTests.OnePersonReturnedByTwoRealBatchQueriesResolvesOnceWithoutAmbiguity`:
   501 addresses, 500 members, no rejection): that reads "501 addresses resolved to 500
   distinct members; 0 rejected", where the old formatter said "500 of 501 matched in AD".
4. **Guards**: the record's two-address/one-GUID case driven through the real runner
   (`BulkJobService.DrainQueueAsync` over a real jobs database), the real processor and the
   real service resolution - only the directory query is the fixture's - asserting the stage
   message as PERSISTED (`GetRows`), the thing `AdminBulkJobs` and the owned-jobs panel
   display; and the formatter's wording, including the 501/500 case, unit-tested.
5. **Module**: Comms-10k; the version bump is the supervisor's. No shared change.
6. **Not in this finding**: the same two counts are conflated where the validation step
   summarises the same resolve - the page banner ("1 of 2 email(s) matched in AD") and the
   service's own resolve message ("1 resolved, 0 rejected."). Found while doing this one;
   recorded and fixed separately as `cdr-followup-2`, so this commit stays the stage message.

Mutations planned at the call site: the old formatter restored; the member count passed as
the address count.

## Approach

As planned. `Services/Comms10kAddressResolver.cs` gains `DescribeResolved` after `Resolve`.
`Services/Jobs/Comms10kReplaceProcessor.cs`, `ResolveAsync`: the success outcome's message is
`Comms10kAddressResolver.DescribeResolved(payload.Emails.Count - resolve.Rejections.Count,
_resolvedMembers.Count, resolve.Rejections.Count)`, with a comment above the return. Nothing
else in the stage moved; its refusals keep their wording.

## Files changed

- `Services/Comms10kAddressResolver.cs` - `DescribeResolved`.
- `Services/Jobs/Comms10kReplaceProcessor.cs` - the resolve stage's success message.
- `ExchangeAdminWeb.Tests/Comms10kResolveStageMessageTests.cs` (new, a part of the
  `Comms10kRefusalTests` partial class, for its fixture).
- `ExchangeAdminWeb.Tests/Comms10kAddressResolverTests.cs` - section 8, the wording.
- This record.

## Guard proof

| Test | Pins |
| --- | --- |
| `Comms10kRefusalTests.TheResolveStagePersistsAddressesAndDistinctMembersSeparately` | The record's case. The fixture's directory returns ONE candidate whose UPN is one uploaded address and whose mail is the other; the job (the C2 helper, a real authorization snapshot) carries both addresses and the one-GUID fingerprint. `BulkJobService.DrainQueueAsync` runs it with the real processor and the real `Comms10kService.ResolveEmailsAsync` over a real jobs database. Asserts the PERSISTED resolve row (`GetRows`, row `ResolveRow`): `Success`, message exactly "2 addresses resolved to 1 distinct member; 0 rejected"; and that both addresses went to the directory in one query |
| `Comms10kAddressResolverTests.TheSummaryNamesAddressesAndDistinctMembersSeparately` (5 cases) | The wording: 2/1/0, the singular 1/1/0, the cross-batch 501/500/0 of `Comms10kAddressIdentityTests`, 2/2/1 and 0/0/3. No count reaches 1,000, so no case depends on the culture's group separator |
| `Comms10kAddressResolverTests.TheSummaryOmitsTheRejectedClauseWhenNoCountIsGiven` | The clause-less form, for a caller that reports its rejections elsewhere |

Mutations on `Services/Jobs/Comms10kReplaceProcessor.cs` at the call site, fixed md5
`9BCEA35678901E257419D4408ADB8EB7` (pre-comment; LF in the working tree and the index). Each
applied by exact-text replacement asserted to match once, confirmed by md5, built in Release
and run against every Comms-10k test (`FullyQualifiedName~Comms10k`, 401 tests), then the fixed
bytes restored, touched and md5-confirmed.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1: the old formatter, `$"{_resolvedMembers.Count:N0} of {payload.Emails.Count:N0} matched in AD"` | `C8EAE0021E2AA4A6A35CF719F014CD48` | 1 of 401: `TheResolveStagePersistsAddressesAndDistinctMembersSeparately` (expected "2 addresses resolved to 1 distinct member; 0 rejected", actual "1 of 2 matched in AD") |
| M2: the member count passed as the address count, `DescribeResolved(_resolvedMembers.Count, _resolvedMembers.Count, ...)` | `4A3E2F47C6A3B8517B4BB9A6A39D6794` | 1 of 401: the same test (actual "1 address resolved to 1 distinct member; 0 rejected") |

Post-comment md5s: `Comms10kReplaceProcessor.cs` `3A20E5CA9370D0506771FCA0F342490C`,
`Comms10kAddressResolver.cs` `84EC2C0A27C509F7AA751FF220798C72`.

## Gates

On the final code, on top of `aud-a23b` (`edafb75`):

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 4499 passed, 3
  skipped (the known `ADDirectorySearchServiceTests` AD skips), 0 failed, 5 m 9 s; no hang. The
  difference from the 4492 before it is the seven tests above.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD`: clean. `tools/Test-AsciiOnly.ps1` after `git add`: passed.

## Known gaps

- **Rows already in the jobs database keep the old wording.** The message is persisted per job;
  a job run before this change still shows "N of M matched in AD". Nothing rewrites history,
  and the test fixtures that seed rows with that text (`Comms10kInterruptionTests`,
  `Comms10kOwnedJobsPageTests`) are standing in for exactly such stored rows, so they were left.
- **No directory was reached.** The candidate set is the fixture's; that one person reached
  through UPN and mail resolves once is `Resolve`'s tested behaviour
  (`Comms10kAddressIdentityTests`), not re-proved here.
- **The same conflation elsewhere is `cdr-followup-2`**: the validation banner and the service's
  resolve message, both found while doing this one.
