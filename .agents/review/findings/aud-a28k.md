# aud-a28k: Settings saves overwrite from a page snapshot, record an empty before-state, or report success the store did not keep

**Severity**: MEDIUM - three shapes already fixed elsewhere, left on other saves. (1) Whole-content
overwrite from the page's snapshot (the aud-a28i / aud-a33b class): `AdminSettings.SaveEnablement`
writes the page's whole loaded enablement map, so a toggle made elsewhere since the page loaded is
reverted; `ModuleConfig.SaveSectionAccessAsync` still re-reads the table and then overwrites the
aliases it edits with what it loaded (aud-a33b's recorded gap: a newer grant or revocation on those
aliases is lost - the authorization store). (2) Empty before-state (the aud-a28f class): the
ProtectedPrincipals and ExchangeOnlineConfig success records pass an empty "before", so every save
reads as everything added. (3) False success: `AdminSettings.SaveLogLevel` reports and records
success even when the level failed to persist, because `SetLevel` swallows the store failure.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28g/h/a26f/a28i agent, 2026-10-22, candidates 3, 4, 5 and 9 of its hand-back)

## Evidence

At `f1b4692d`: `Components/Pages/AdminSettings.razor` `SaveEnablement`, `SaveLogLevel`,
`SaveProtectedPrincipals` (the `LogSettingsChange` before-argument); `Components/Pages/ModuleConfig.razor`
`SaveSectionAccessAsync`; `Components/Pages/ExchangeOnlineConfig.razor` `SaveExoConfig`; the
`SetLevel` path for the extended log level.

## Approach (to be written by the implementing agent before code)

- Snapshot overwrites: decide under the write lock from current rows (aud-a33b / aud-a28i shape) or
  compare-and-swap on a content version captured at load and refuse `ConcurrentChange` (audited,
  reload and re-apply). For enablement: write only the modules whose toggle the admin changed, each
  compared to the row as it is under the lock. For section access: per-alias compare against the
  loaded snapshot under the lock; a changed alias refuses the whole save with the alias named.
- Before-state: pass the rows read under the lock as "before" for ProtectedPrincipals and
  ExchangeOnlineConfig success records (aud-a28f shape).
- Log level: `SetLevel` reports failure (or the page reads the level back) and the handler records a
  failure and tells the admin when it did not persist.
- Tests through `SettingsPageHarness` and two stores on one temp file (aud-a33b style) for each
  overwrite; before-state records list real additions and removals; a store that refuses the log
  level yields a failed record and message. Mutations at the call site: write the whole map again;
  pass the empty before again; swallow the SetLevel failure again.

## Guard proof

Pending.
