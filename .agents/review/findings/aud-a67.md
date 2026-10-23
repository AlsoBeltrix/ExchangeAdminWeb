# aud-a67: Settings pages and stores - unsaved counts carry across modules, the db CLI and the BitLocker archive wait 35 s not 5, the pool drain holds the circuit, no re-entrancy guard, a stale test comment, and the allowlist save's generic busy message

**Severity**: LOW - recorded under the 2026-10-23 stopping rule, not dispatched. (1) Module Configuration's
`LoadState` never clears `dirty`; only `ReloadAsync` does, so the previous module's unsaved counts survive a
move to another module on the same page instance. (2) `ConfigDbMaintenance` sets `busy_timeout=5000` with no
command timeout, so the `db` CLI waits about 35 s, not the 5 s `docs/ConfigDbToolInApp-Plan.md` documents;
`BitLockerRecoveryService`'s read-only archive connection has the same shape (inferred from the mechanism,
not measured). (3) `ExoPool.DrainPool()` in `SaveExoConfig` still runs on the circuit: up to five synchronous
disconnects. (4) No settings page has a re-entrancy guard: a second save started before the disabled Save
button renders runs alongside the first, whose `finally` clears `saving` early (cosmetic; the stores decide
under their locks). (5) `SettingsSaveAuditTests` 444-445 says `SetLevel` "swallows" a store failure; it has
rethrown since aud-a28k. (6) `ADAttributeEditorService.SaveAllowlistAndAudit` (464-472) still reports a busy
store as "Failed to save: SQLite Error 5" (the aud-a40 class inside the service; the exact change is in
aud-a40.md's follow-up section).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; held by the 2026-10-23 stopping rule (LOW) for after the first dev deploy
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a41/a40/a42 agent, 2026-10-23, candidates 2-6 and its follow-up)

## Evidence

At `86df002a`: `Components/Pages/ModuleConfig.razor` `LoadState` / `ReloadAsync`; `Services/Storage/ConfigDbMaintenance.cs`
175-183; `Services/BitLockerRecoveryService.cs` 243-257; `Components/Pages/ExchangeOnlineConfig.razor`
`SaveExoConfig` (the drain); the three pages' `saving` flags; `ExchangeAdminWeb.Tests/SettingsSaveAuditTests.cs`
444-445; `Services/ADAttributeEditorService.cs` 464-472.

## Approach (to be written by the implementing agent before code)

- `LoadState` resets `dirty`; the two other connections take `SqliteConnectionFactory.BusyTimeout` as their
  command timeout (measure the CLI wait); the drain runs off the circuit; each save sets `saving` before
  its first await and refuses a second entry; the comment corrected; the allowlist save uses
  `SettingsSaveAudit.LogSettingsSaveException`.
- Mutations at the call site: `dirty` left; the CLI timeout removed; the drain back on the circuit; the
  allowlist's generic message again.

## Guard proof

Pending.

## Also held (codex, settings-batch-6, 2026-10-23) - LOW

(7) `ExchangeAdminWeb.Tests/SqliteBusyWaitTests.cs` 58 and `SettingsStoreBusyTests.cs` 128: the `< 9 s` wall-clock
ceiling can false-fail on a slow runner; keep the structural assertions (`busy_timeout`, `DefaultTimeout`) and
loosen the elapsed check to "not the 30 s retry loop". (8) `SettingsSaveAuditTests.cs` 644: the helper tripwire
enumerates catches by `InlineData`, so a new or nested settings-save catch escapes it; discover the catches from
the page source and require the helper for every non-allowlisted one.
