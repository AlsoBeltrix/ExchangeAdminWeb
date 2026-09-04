# scdi-1: Prod EXO runspaces stay on the old module config after a dev-side config save

**Severity**: HIGH - with the shared database, an Exchange Online connection setting
saved on dev is live on prod at once for reads, but prod's pooled EXO runspaces keep
running privileged operations under the PREVIOUS app registration / organization /
certificate until the pool drains, the app recycles, or a runspace idles out; under
steady use `LastUsed` keeps refreshing and the stale connection persists.
**Status**: Open
**Branch**: -
**Commit**: (filled by the fix)

## Evidence

`.agents/repo-guidance.md:120-122` (new invariant): a setting saved on dev is live on
prod at once. `Components/Pages/ExchangeOnlineConfig.razor:291-297`: the save writes
the shared module config and drains only the LOCAL injected pool. `Program.cs:214`:
`ExoConnectionPool` is a singleton. `Services/ExoConnectionPool.cs:135-144`: acquire
reuses any available runspace whose `ConfigGeneration` matches the local field;
`:305-307`: `ConfigGeneration` is incremented only by `DrainPool`, which only the local
save calls. Trigger: prod holds an idle pooled EXO runspace; an admin changes the
ExchangeOnline AppId, Organization or CertificateSubject on dev.

## Predicted observable failure

Prod's next EXO operation reads the new config for its pre-check and log context, then
receives the old pooled session and executes against the old connection. The audit
names the new organization; the write happened on the old one.

## What

Cross-process invalidation was added to the four settings caches (S2) but the EXO
connection pool is a fifth holder of config-derived state that the plan did not list.

## Approach

(To be filled by the fix.) Direction: the pool records the change token (via
`ModuleConfigService.CreateChangeWatcher`) with the ExchangeOnline config snapshot it
connected under; on acquire, when `HasChangedSince(loadedToken)` reports movement, it
re-reads the ExchangeOnline module config and, if AppId/Organization/CertificateSubject
differ from the snapshot, drains (increments `ConfigGeneration`) before handing out a
runspace. A token move caused by an unrelated module's save must NOT drain (compare
the values, not just the token). Guard: a test that simulates a foreign write to the
ExchangeOnline config and proves the previously pooled runspace is discarded, plus one
proving an unrelated module's write leaves it pooled.

## Files changed

(filled by the fix)

## Guard proof

(filled by the fix)

## Coder dispute (if any)

None.

## Known gaps

None.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
  (owner dispatch: "codereview codex default the first agent's work")
Harness: codex-cli 0.152.1, `codex exec -s read-only`, generation half over
`8d9ccb0..b9e944f`, verdict `findings` (3), capability_ok true, both SHAs echoed.
Dispatched 2026-09-04; envelope at `.agents/review/scd-impl.result.json`.
