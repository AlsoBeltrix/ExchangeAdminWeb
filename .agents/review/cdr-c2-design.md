# C2 implementation guidance - authorization at command dispatch

Read-only design advice from native agent `/root/design_confirmed_set`, dispatched as
`gpt-6-astra` at `max` under approved Revision 6, 2026-10-15. No edits, tests or directory calls
were made in the design pass. These are implementation details of the existing C2 scope.

Inside Comms10k.ExecuteReplace, obtain the current principal and pass live policy authorization.
Read current section groups, call JobAuthorizationSnapshot.Capture with section Comms10k,
confirm effective module enablement and persist AuthSnapshotJson immediately with enqueue.
Use that same principal for SubmittedBy. Do not reuse initialization-time authorization.

In the worker, require a readable snapshot for the exact Comms10k section, an originally
authorizing SID still present in the current usable section groups, and effective Comms10k
module enablement. Preserve AuthorizedGroups semantics: newly allowed groups merely found in
RoleClaims must not supply a replacement grant. Existing section-access and enablement stores
read their repositories on each call; no new cache or authorization framework is needed.

Check at stage entry, then again after credential retrieval, runspace setup, target reread and
primary-member preflight, immediately before clear. A check merely before ExecuteReplaceAsync
leaves substantial asynchronous preparation between permission and write. Apply the gate before
subsequent add attempts and retries too.

Track whether clear was actually dispatched. Denial before that point is RefusedBeforeAnyChange.
After a clear attempt, stop further mutation and read back to report the actual/unknown result.
The current writer assumes entering its clear delegate means a write was attempted; a guard
inside that delegate makes that assumption false, so adjust the boundary explicitly.

This detects allowed-group configuration removal and module disablement. It does not refresh
Windows token membership, detect revocation hidden by an existing token, or retract a cmdlet
already dispatched. Preserve those limits in wording and tests.

Regression cases include configuration removal during preparation, disablement between batches,
missing or wrong-section snapshots, and one failed audit with a specific completion reason.
Coordinate with C3 refusal auditing and C11 interrupted-write reporting; do not duplicate audits
when the runner advances from a failed Resolve row to Replace. Mutation-prove every new guard.
