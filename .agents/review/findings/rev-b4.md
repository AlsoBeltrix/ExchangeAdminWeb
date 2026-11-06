# rev-b4: when Graph does not answer, an alias-addressed protected recipient is re-resolved by address and can clear as a different account

**Severity**: HIGH - fail-open under a dependency failure. `ResolveSyncedTarget` reads the on-premises SID from Graph; on
`NotAnswered` (Graph unreachable, credential blank) it falls through to address resolution of the recipient's canonical
address. If Exchange resolved an alias to protected synced account A and another local account B uniquely carries the
canonical address, the method returns B, whose SID passes A's rule. Blocked Senders' protection gate then allows a
non-servicer to unblock A's alias. The 2026-11-03 ruling records that the code fails closed for a synced recipient with no
SID; the fall-through on an unanswered read is the gap.
**Status**: Admitted; awaiting fix on branch
**Lines** (master `145f513f`): `Services/ProtectedPrincipalService.cs:763-777`; `Services/BlockedSenderProtectionGate.cs:160`, `:237`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review B, range `5c4c2970..82a67e2f`, dispatched 2026-11-06 from master `145f513f`; report `.agents/review/rev-b.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

`NotAnswered` (and a missing or blank source) returns Unavailable, which the callers already treat as refuse; the address
fallback stays only for `NoneReported` (Graph answered: no on-premises SID). Consequence to state in the record and the
help: until the Graph Directory Read Credential field is filled on the deployed build, synced-recipient writes refuse with
that reason (fail closed, loud), which is the deploy step already on the owner's list. The test
`Fallback_AnAliasOfASyncedMailbox_WithGraphUnanswered_ReResolvesTheAddressAsBefore` pins the defect and must change.
