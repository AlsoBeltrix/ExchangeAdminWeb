# aud-a28j: Settings saves with no corrupt-store re-check - a protected-principals save after a failed load wipes every protection rule

**Severity**: HIGH - `AdminSettings.razor` `SaveProtectedPrincipals` has no corrupt/unreadable
re-check and the Save button stays enabled when the load failed, so a save then writes the page's
EMPTY lists over the protected-principal store and removes every protection rule in one click. The
protected-principal store is the gate every mutating module consults (Known Failure Class 3). Same
class, lower impact: `ExchangeOnlineConfig.razor` `SaveModuleStatus` has no `IsStoreCorrupt`
refusal, and `SaveExoConfig` guards a corrupt config only with a disabled button, which a stale
render or a second tab can defeat.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28g/h/a26f/a28i agent, 2026-10-22, candidates 1 and 2 of its hand-back)

## Evidence

At `f1b4692d`: `Components/Pages/AdminSettings.razor` `SaveProtectedPrincipals` (refusals:
Authorization, ValidationInProgress; no store-state check; the page's lists are empty when the load
threw or the store read as corrupt); `Components/Pages/ExchangeOnlineConfig.razor` `SaveModuleStatus`
and `SaveExoConfig`. Compare `ModuleConfig.razor`'s saves, which refuse `StoreCorrupt` before writing,
and aud-a28f/a28i's allowlist save, which re-reads under the lock.

## Approach (to be written by the implementing agent before code)

- Every settings save that replaces a store's content re-checks the store immediately before the
  write (fresh read, or inside the write transaction as aud-a28i does) and refuses - audited
  `refusedAt: StoreCorrupt` / `StoreUnreadable`, `mutationAttempted: false` (aud-a28h helpers) -
  when the store is corrupt, unreadable, or when the page's loaded state did not come from a
  successful load. A page whose load failed must not offer Save at all, and the handler must refuse
  even if invoked (the button is not the guard).
- Protected principals: additionally refuse a save that would empty a store that currently holds
  rules unless the page explicitly loaded them and the admin removed them one by one (an
  empty-over-non-empty save from a page whose load produced nothing is refused with a message
  saying the rules could not be read and nothing was changed). No guess.
- Tests through `SettingsPageHarness` (aud-a28h): a failed load followed by Save writes nothing and
  leaves one refusal record; a corrupt store refuses; a healthy load and save still succeeds;
  ExchangeOnlineConfig's two saves refuse a corrupt store. Mutations at the call site: drop the
  re-check; drop the empty-over-non-empty refusal.

## Guard proof

Pending.
