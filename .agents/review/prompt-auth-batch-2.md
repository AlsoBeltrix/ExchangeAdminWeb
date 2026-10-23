# Defect review request: off-switch wording, page path in the refusal audit line, recorded-answer sweep (aud-a29h, aud-a29i, aud-a29j)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `d58b5d7c` or later.
- The change is the branch range merged at `d58b5d7c`: commits `2699dd29` (aud-a29h), `14079ee5` (aud-a29i), `0526dc83`
  (aud-a29j), on top of the already-reviewed `2678735e` (aud-a29g). Review `git diff 2678735e 0526dc83 -- . ':!.agents'` and
  read the COMBINED `Authorization/CircuitMembershipRevalidator.cs` (the sweep on record; `RecordedAccountSids`),
  `Authorization/MembershipRevalidatingAuthenticationStateProvider.cs` (the shared warning constant; the audit line's new
  `path` field), `Middleware/PageRequestRevalidationMiddleware.cs` (path capture: PathBase + Path, no query, percent-encoded,
  256-char cap), `README.md`, and the tests `CircuitRevalidationTests.cs`, `PageRequestRevalidationTests.cs`.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command per
  call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a29h.md`, `aud-a29i.md`, `aud-a29j.md`; parent `aud-a29g.md`; the auth-batch-1 result
  that raised i and j is `.agents/review/auth-batch-1.result.txt`. ALREADY admitted and out of scope - do not re-raise:
  `aud-a29k` (two tests hold a read open under a 10 s real-time harness timeout), `kd-16` (an ExtendedLogService Dispose
  test timing), `aud-a67` (7) (the 9 s wall-clock bounds). Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` Known Failure
  Classes (1, 3) and Architectural Invariant 7; `docs/ProjectConstitution.md` (audit).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 0526dc83`. Report whether both succeeded.

## Mandate

Hunt for observable defects. aud-a29i: can the recorded path leak anything sensitive (a route carrying an identity, an
address or a ticket in the path rather than the query), is the 256 cap applied before or after percent-encoding (a
multi-byte path that encodes past the cap), does the encoding double-encode an already-encoded path, and is the field
present on every refusal shape (LookupFailed, revoked, off-switch-off never audits) - name any refusal without it;
aud-a29j: the sweep runs on record - is the dictionary access thread-safe under concurrent records and lookups (what
type, what lock), can the "guarded removal so a newer answer stays" compare-and-remove race with a concurrent record and
drop a live answer, does the sweep's cost grow with distinct SIDs on the response path (O(n) per page request after a
cache miss - acceptable? bound it), and does `RecordedAccountSids` expose anything a non-test caller can misuse; aud-a29h:
is the new wording true in every mode (switch on, switch off, Kerberos, NTLM, a UPN-less Negotiate principal), does the
startup warning fire exactly once, and is the README sentence the same claim; tests: do the new pins read the constant
(tautological) or the rendered text, and could aud-a29k's stall shape affect any new test.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.