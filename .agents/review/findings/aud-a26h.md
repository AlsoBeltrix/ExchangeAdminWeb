# aud-a26h: Other corrupt-store texts still name retired JSON files, and a pool-drain failure after a successful save is shown as a failed save

**Severity**: LOW - wording and reporting, no wrong write. (1) The corrupt-store banners and errors
for stores other than the allowlist (aud-a26f fixed only that one) still tell an administrator to
fix a `config/*.json` file; those stores live in the SQLite config database since the legacy import.
(2) `ExchangeOnlineConfig.razor` `SaveExoConfig`: when the write succeeds and the following
`DrainPool` fails, the page shows and records "Failed to save", though the configuration was saved;
the admin re-saves a value that is already there and the audit says a save failed that did not.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28g/h/a26f/a28i agent, 2026-10-22, candidates 7 and 8 of its hand-back)

## Evidence

At `f1b4692d`: `git grep -n "\.json" Components/Pages/ModuleConfig.razor Components/Pages/AdminSettings.razor
Components/Pages/ExchangeOnlineConfig.razor Services/` for the corrupt-store and load-error texts
(enablement, section access, module admins, protected principals, EXO config); `ExchangeOnlineConfig.razor`
`SaveExoConfig`, the `DrainPool` call after the write inside the same try.

## Approach (to be written by the implementing agent before code)

- Texts: name the real store (the page or tab where the setting is edited, or the table in
  `docs/ConfigDbOperations.md` with both pools stopped), and the legacy JSON file only where an
  import of it failed. README likewise. Test: the messages contain the table/page name and not the
  retired file name (this finding is about the text).
- Save then drain: the save's success record and message are written when the write succeeds; a
  drain failure is reported as its own warning ("saved; the Exchange Online connection pool could not
  be drained: <reason>; recycle the pool") and recorded as a separate failed step, not as a failed
  save. Test: a failing drain after a successful write leaves one success record for the save and a
  distinct failure for the drain; the message says saved. Mutation at the call site: fold the drain
  back into the save's try.

## Guard proof

Pending.
