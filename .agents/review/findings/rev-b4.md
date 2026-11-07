# rev-b4: when Graph does not answer, an alias-addressed protected recipient is re-resolved by address and can clear as a different account

**Severity**: HIGH - fail-open under a dependency failure. `ResolveSyncedTarget` reads the on-premises SID from Graph; on
`NotAnswered` (Graph unreachable, credential blank) it falls through to address resolution of the recipient's canonical
address. If Exchange resolved an alias to protected synced account A and another local account B uniquely carries the
canonical address, the method returns B, whose SID passes A's rule. Blocked Senders' protection gate then allows a
non-servicer to unblock A's alias. The 2026-11-03 ruling records that the code fails closed for a synced recipient with no
SID; the fall-through on an unanswered read is the gap.
**Status**: Fixed on branch; awaiting review
**Branch**: fix/rev-abc
**Commit**: 44ad5162
**Lines** (master `145f513f`): `Services/ProtectedPrincipalService.cs:763-777`; `Services/BlockedSenderProtectionGate.cs:160`, `:237`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review B, range `5c4c2970..82a67e2f`, dispatched 2026-11-06 from master `145f513f`; report `.agents/review/rev-b.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

`NotAnswered` (and a missing or blank source) returns Unavailable, which the callers already treat as refuse; the address
fallback stays only for `NoneReported` (Graph answered: no on-premises SID). Consequence to state in the record and the
help: until the Graph Directory Read Credential field is filled on the deployed build, synced-recipient writes refuse with
that reason (fail closed, loud), which is the deploy step already on the owner's list. The test
`Fallback_AnAliasOfASyncedMailbox_WithGraphUnanswered_ReResolvesTheAddressAsBefore` pins the defect and must change.

## As built (fix/rev-abc `44ad5162`)

Verified real, in `ResolveWithExchangeFallbackAsync`'s alias branch (the cited `ResolveSyncedTarget` is that branch at
master `145f513f`): a `NotAnswered` SID read, or no SID source, fell through to `ResolveWithStatusAsync(canonical address)`.
`BindSyncedAsync` (the Exchange-target path) already refused it.
- `NotAnswered` and a missing source return `Unavailable` (logged "failing closed"); `Reported` binds by SID as before; only
  `NoneReported` (Graph answered: no SID) re-resolves the canonical address, logged "bound by address".
- Consequence (stated in README under the Graph Directory Read Credential and in the pane note): until that field is filled
  on the deployed build, or while Graph cannot be read, a protection check of a synced recipient addressed by an alias Active
  Directory does not hold refuses as unavailable - Blocked Senders included. That is the deploy step already on the owner's
  list.

## Guard proof

| Test | Fails on the old code / mutation (the NotAnswered return removed) |
| --- | --- |
| `ProtectedPrincipalExchangeFallbackTests.Fallback_AnAliasOfASyncedMailbox_WithGraphUnanswered_IsUnavailable_NotReResolvedByAddress` (replaces `..._ReResolvesTheAddressAsBefore`; the canonical address held by another account) | yes |
| `ProtectedPrincipalExchangeFallbackTests.Fallback_AnAliasOfASyncedMailbox_WithNoSidSource_IsUnavailable` | yes |
| `ProtectedPrincipalExchangeFallbackTests.Fallback_AnAliasOfASyncedMailbox_GraphAnsweredWithNoSid_StillReResolvesTheAddress` | passes on both (step 5 kept) |
| Four alias re-resolve tests now script Graph's `NoneReported` to stay on that path | - |
