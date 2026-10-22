# aud-a28x: Exchange Online pool - a failed module-config read falls back to appsettings, so a borrow can connect under the appsettings values

**Severity**: MEDIUM - credential isolation and fail-closed reads. In `ExoConnectionPool.GetExoConfig`,
`BorrowAsync` and `IsConfigured`, a `GetValue` that FAILS reads as null and the appsettings
`ExchangeOnline:*` fallback applies. The Constitution allows the legacy fallback only when the
module-specific config is ABSENT; an unreadable store is not absent. If the store then recovers by the
presence check, a borrow connects under appsettings values (tenant, app ID, certificate) instead of the
module config's. aud-a28t added `TryGetIsConfigured` beside `IsConfigured` for the permission gate; the
pool's own three reads still use the permissive path.
**Status**: Plan written; in progress
**Branch**: `worktree-agent-a4c579a99f70ca768`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28t/a26q agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `18c62ce9`: `Services/ExoConnectionPool.cs` `GetExoConfig`, `BorrowAsync`, `IsConfigured` (the
`GetValue` reads and the `??` appsettings fallbacks); `TryGetIsConfigured` (the fail-closed shape);
`docs/ProjectConstitution.md` "Legacy appsettings fallback is allowed only ... when the module-specific
config is absent".

## Approach (to be written by the implementing agent before code)

- The three reads go through `TryGetModuleConfig`; a failed read makes `GetExoConfig` throw a typed
  "Exchange Online settings could not be read: <rows and remedy>" and `BorrowAsync` refuse (no connection
  attempted, one log line); `IsConfigured` keeps its contract for callers that cannot take a throw by
  delegating to `TryGetIsConfigured` and treating unreadable as "not configured AND unreadable" where the
  caller can show it. The fallback applies only to a value that is present-and-null or absent.
- Tests through the pool's connect seam (it throws) and a failing store double; mutations at the call
  site: `GetValue` again at one read; the fallback applied to a failed read again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21). Branch
`worktree-agent-a4c579a99f70ca768` from master `18530b58`: one fix commit `fix(exo-pool): ... (aud-a28x)`, second of three
(after aud-a28y, before aud-a26t), and the comments-only commit after all three. Behaviour: the shared
`ExoConnectionPool` every Exchange Online module borrows from, and the resolver's not-configured refusal (base app
version); no module version moves; the supervisor owns the bump.

Evidence re-read at `18530b58`:
- `ExoConnectionPool.GetExoConfig` 191-208: three `ModuleConfigService.GetValue` reads (193-195; `GetValue` 60-64 ->
  `ReadModuleConfig` 281-293 logs a read failure and returns an empty config, so null), then the presence read and the
  probe (197: `HasModuleConfigFile` -> `ModuleConfigRepository.HasModule` 52-61, which throws; `IsModuleCorrupt` ->
  `TryReadModule` 33-45, false on a failure), then appsettings `ExchangeOnline:AppId` / `Organization` /
  `CertificateSubject` for every null (203-206). A value read that fails while the presence read and the probe answer - a
  confined fault, or a store that recovers between them - is taken for "absent" and gets the appsettings value: possibly
  another tenant, app id or certificate than the module config's.
- `BorrowAsync` 218-326 reads it once (234), before the slot; those values decide the not-configured refusal (236-237),
  the stale-pool check (268) and what the connect is given (305), so the borrow connects under appsettings values.
  `DrainPool` 502-520 reads it (507) to record what the next borrow connects under. `IsConfigured` 153-162 reads it through
  the same chain - and throws when the presence read fails: a bool property that is not exception-safe.
- `TryGetIsConfigured` 175-189 (aud-a28t) is the fail-closed shape: one `TryGetModuleConfig` read, false when it fails,
  appsettings only for a key the rows do not hold.
- `ExchangeIdentityResolver.ResolveRecipientAsync` 47 refuses "Exchange Online is not configured" when `IsConfigured` is
  false. `DrainPool`'s one caller, the Exchange Online Config page's save (`ExchangeOnlineConfig.razor` 554-570, owned
  by another agent), reports any throw as "the connection pool could not be drained" and records it as a failed drain.
- Constitution, Configuration: "If a config store is corrupt or unreadable, fail closed. Do not silently fall back to
  stale defaults." and "Legacy appsettings fallback is allowed only for upgrade compatibility and only when the
  module-specific config is absent." Never Do: "Do not make corrupt config silently fall back to defaults."

1. **One read, and a read that failed is not absent.** `GetExoConfig` reads the ExchangeOnline rows once through
   `ModuleConfigService.TryGetModuleConfig` (aud-a26k). Unreadable -> one error line and a typed `ExoConfigUnreadableException`
   (an `InvalidOperationException`, declared in `ExoConnectionPool.cs`): "Exchange Online settings could not be read: "
   followed by the rows and their fix (`SettingsStoreText.ModuleConfig`, as the Exchange Online Config page names them;
   aud-a26t then gives the service callers a remedy without the page's reload clause). Readable -> every key from that one
   read, appsettings only for a key the rows do not hold, as today; the presence read and the probe go, the one read being
   both. `TryGetIsConfigured` reads AppId and Organization through the same helper, so the fallback rule is in one place.
2. **The borrow refuses.** `BorrowAsync` lets that exception out before it asks for a slot: no slot, nothing connected,
   no appsettings value used.
3. **The drain does not need the read.** `DrainPool` retires the generation and destroys the idle runspaces whether or
   not the rows read. When they cannot be read it records no connected-under config (null, the state before a first
   borrow), so the next borrow records what it reads without draining again, and it logs a warning; it does not throw,
   so a saved configuration is not reported as an undrained pool. Nothing connected before survives either way: the
   generation moved.
4. **`IsConfigured` keeps its contract.** It becomes `TryGetIsConfigured(out var configured) && configured`: a bool
   that never throws, false for rows or an enablement that cannot be read, so callers that cannot show more (the
   recipient autocomplete) keep working. `ExchangeIdentityResolver.ResolveRecipientAsync`, which can, asks
   `TryGetIsConfigured`: unreadable -> "Exchange Online's module enablement or its module_config rows could not be read -
   cannot resolve recipient."; readably not configured -> the existing message.
5. **Registries.** No page, handler or markup changes: ClickGateRegistry and ProgressRegistry untouched.

Tests, new `ExchangeAdminWeb.Tests/ExoConnectionPoolConfigReadTests.cs`: a real pool over a temp database, Exchange Online
enabled on its own store, the ExchangeOnline rows holding one tenant's AppId, Organization and CertificateSubject and
appsettings `ExchangeOnline:*` another's, the rows read through a store that fails a set number of reads after a set
number (aud-a28t's `FailsAfterReadsStore`), and a connect seam that records the config it is given and throws, so no
session can open. (a) The rows unreadable at the borrow's read, from then on or for that read only: the borrow throws
`ExoConfigUnreadableException` naming the rows, the seam never reached, one error logged; (b) the store failing every
read after the borrow's one: the seam is given the rows' values, not appsettings', and the borrow read the rows once;
(c) controls - no ExchangeOnline rows: the seam is given appsettings' values; one key absent: that key from appsettings,
the others from the rows; (d) `IsConfigured` over unreadable rows is false and does not throw, also with every read
failing, where the presence read used to throw; `TryGetIsConfigured` false; (e) `DrainPool` over unreadable rows does not
throw and moves the generation on, and a borrow once the store answers again is given the rows' values; (f) the real
`ExchangeIdentityResolver` over the pool: unreadable rows -> `ResolveRecipientAsync` throws naming the unreadable read,
not "not configured", nothing connected; AppId blank -> "not configured" (control).

Mutations at the call site: (1) `GetExoConfig` reads AppId through `GetValue` again, beside the one read, the appsettings
value standing in for a failed read; (2) `GetExoConfig` takes unreadable rows for absent again (the empty dictionary, so
every value from appsettings). Extras: (E1) `IsConfigured` back on the `GetValue` chain; (E2) `DrainPool` lets the
unreadable read throw; (E3) the resolver asks `IsConfigured` again. No separate pre-fix reproduction: the tests name the
new exception type, so they do not compile against the old pool; mutation 2 is the old fallback-on-failure at its call
site.

## Guard proof

Pending.

## Folded in: point the pool's fallback reads at ExoConfigFallback (aud-a26s follow-up, 2026-10-22) - LOW

aud-a26s moved the fallback rule into `Services/ExoConfigFallback.cs` (`Resolve`/`Caption`) and pinned a test
to the pool's current behaviour; the pool itself was owned by this finding's agent. `ExoConnectionPool.cs`
lines 176-179 (at `a583777d`) become three `ExoConfigFallback.Resolve(...).Value` calls so one function owns
the rule; the fail-closed read this finding adds sits in front of it (a failed read never reaches `Resolve`).
