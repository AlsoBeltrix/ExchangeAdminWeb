# scdi-1: Prod EXO runspaces stay on the old module config after a dev-side config save

**Severity**: HIGH - with the shared database, an Exchange Online connection setting
saved on dev is live on prod at once for reads, but prod's pooled EXO runspaces keep
running privileged operations under the PREVIOUS app registration / organization /
certificate until the pool drains, the app recycles, or a runspace idles out; under
steady use `LastUsed` keeps refreshing and the stale connection persists.
**Status**: Verified
**Branch**: -
**Commit**: the commit that completes this record; read it from `git log -1 -- .agents/review/findings/scdi-1.md`

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

The pool now records the connection config its runspaces connected under
(`ExoConnectionConfig`, a record struct of exactly the three values the config page saves
and `Connect-ExchangeOnline` is given) and compares it on every borrow. `BorrowAsync`
already reads AppId / Organization / CertificateSubject fresh from the store on every call
for its not-configured refusal (`ModuleConfigService` has no cache), so the same read now
feeds `DrainIfConnectionConfigChanged(current)`: when any field differs from the recorded
config it drains through the same core `DrainPool` uses (generation increment + destroy),
records the new config, and only then is the generation read for the pooled-runspace
test - so every runspace connected under the old values fails it, in the pool or still
borrowed (`Return` destroys it). Equal values - an unrelated module's save, or the same
connection written again - drain nothing; the comparison is by value, never by "the
database changed". `DrainPool` (the in-process save path) is unchanged in effect and
additionally records the just-saved config so the next borrow does not drain twice.
`CreateConnected` no longer reads config itself: the borrow passes the config it compared
and the generation it read BEFORE the connect, so a drain landing while
`Connect-ExchangeOnline` runs leaves that runspace stale instead of stamping it with the
post-drain generation (previously it was stamped after the connect). That connect step is
the internal seam the tests replace; the pool is otherwise hosted for real over a temp
SQLite store, with "the other instance" writing through a second store on the same file.

**Deviation from the recorded direction (token watcher):** the direction proposed
`ConfigChangeWatcher.HasChangedSince` as the trigger for a re-read. The borrow path already
performs that re-read unconditionally, so a token pre-check would add one store read per
borrow and a 2 s throttle window in which a stale runspace could still be handed out,
while removing nothing. The outcome the direction asked for - foreign ExchangeOnline write
drains, unrelated write does not, values compared not the token - is exactly what is
implemented and tested.

Shared infrastructure changed, so the base app version moves `2.19.0 -> 2.19.1`; this bump
also covers scdi-2's `ConfigStorePath` change (missed in that commit).

## Files changed

- `Services/ExoConnectionPool.cs:47-53` - `ExoConnectionConfig` record struct (the three connection fields)
- `Services/ExoConnectionPool.cs:69-109` - `_connect` seam + internal constructor, `_connectedUnder` with its lock, `ConfigGeneration` / `AvailableCount` test seams
- `Services/ExoConnectionPool.cs:118-138` - `GetExoConfig` returns the record
- `Services/ExoConnectionPool.cs:154-206` - `BorrowAsync`: one fresh read feeds the refusal, `DrainIfConnectionConfigChanged(current)` before the generation read, connect through `_connect(current, currentGen)`
- `Services/ExoConnectionPool.cs:350-416` - `DrainPool` (records the saved config), `DrainIfConnectionConfigChanged`, `DrainPoolCore`
- `Services/ExoConnectionPool.cs:418-459` - `CreateConnected(config, generation)` uses the passed values and generation
- `ExchangeAdminWeb.Tests/ExoConnectionPoolSharedConfigTests.cs` - new, 8 tests (below)
- `ExchangeAdminWeb.csproj:11,14-15` - `2.19.0 -> 2.19.1`

## Guard proof

All in `ExchangeAdminWeb.Tests/ExoConnectionPoolSharedConfigTests.cs`; each mutation was
applied to `Services/ExoConnectionPool.cs`, rebuilt, run, then restored from a scratch copy
and rebuilt (19/19 with the retry tests after every restore).

- `::ForeignWriteToConnectionField_PooledRunspaceIsNotReused` (theory: AppId, Organization,
  CertificateSubject) - a second store on the same file rewrites one field; the previously
  pooled runspace is not returned, the generation is 1, the new connect got the new value.
  Mutation: delete the `DrainIfConnectionConfigChanged(current);` call in `BorrowAsync` (the
  shipped shape) -> 3 FAIL, 5 pass.
- `::ForeignWriteToAnotherModule_PooledRunspaceIsStillReused`,
  `::ForeignRewriteOfIdenticalConnection_PooledRunspaceIsStillReused`,
  `::DrainIfConnectionConfigChanged_FirstCallRecordsWithoutDraining` - unrelated-module writes and
  an identical rewrite keep the runspace pooled at generation 0. Mutation: drop the value
  compare (`if (_connectedUnder is null)` only, i.e. drain whenever the database was touched)
  -> 4 FAIL (these three + the in-process test's "did not drain twice" assert).
- `::InProcessDrainPool_StillRetiresPooledRunspace` - the config page's save + `DrainPool` still
  retires the pooled runspace (generation 1, pool empty) and the next borrow connects under the
  saved AppId without a second drain. Mutation: `DrainPool` stops recording the saved config
  -> 1 FAIL (generation 2).
- `::DrainDuringConnect_LeavesTheNewRunspaceStale` - a drain fired from inside the connect seam
  leaves the created runspace at generation 0 and `Return` destroys it. Mutation: stamp the
  runspace with `Interlocked.Read(ref _configGeneration)` after the connect (the previous
  shape) -> 1 FAIL.
- Full suite after the final build: 2330 passed / 0 failed / 3 skipped; format clean.

## Coder dispute (if any)

None.

## Known gaps

- The comparison happens on the borrow path; a runspace already borrowed when the foreign
  save lands finishes its current operation on the old connection (same as the in-process
  drain today) and is destroyed on `Return`.
- Not exercised against a live tenant (no dev tenant); the live `Connect-ExchangeOnline`
  step is the one line the seam replaces, and it now takes its inputs from the compared
  config rather than re-reading them, which the seam does exercise.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
  (owner dispatch: "codereview codex default the first agent's work")
Harness: codex-cli 0.152.1, `codex exec -s read-only`, generation half over
`8d9ccb0..b9e944f`, verdict `findings` (3), capability_ok true, both SHAs echoed.
Dispatched 2026-09-04; envelope at `.agents/review/scd-impl.result.json`.
