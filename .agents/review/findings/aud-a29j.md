# aud-a29j: the recorded-answer cache keeps expired entries for operators who never return

**Severity**: LOW - unbounded retention by distinct SID. `Authorization/CircuitMembershipRevalidator.cs` about 93: `_answers` is a singleton dictionary keyed by SID; expired entries are removed only when `RecordedAnswerFor` runs for that SID or a failed read calls `Forget`. Over a long app-pool lifetime, stale membership sets for operators who never return stay in memory past the interval. Bounded by the real operator population, but not interval-bounded as the record describes the cache.
**Status**: Open - admitted 2026-10-23 evening from codex auth-batch-1 on aud-a29g; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier=standard pair (grade fallback, owner 2026-08-03) [escalated: T1 Authorization/* paths]; codex-cli 0.163.0; base eb017102 head 2678735e (merged a5d12cc8); 2026-10-23T19:33-19:5xZ; capability proof: AGENTS.md read and git show --stat 2678735e both succeeded

## Evidence

Codex auth-batch-1 finding 2 at `a5d12cc8`.

## Approach (to be written by the implementing agent before code)

- Sweep expired entries opportunistically on record/read (cheap, no new dependency), or move to `IMemoryCache` with absolute expiration and a size limit. Test: an expired entry for SID A is gone after a record for SID B; mutation: the sweep removed.

## Guard proof

Pending.