# aud-a28h: The other settings saves audit only successes

**Severity**: MEDIUM - the Module Configuration page's other tabs (enablement, section access,
module admins, protected principals, scalar settings), the Admin Settings page and the Exchange
Online Config page each write a settings-change record on success and nothing on a refusal or an
exception. Section access and protected principals are the authorization stores; a refused or
failed attempt to change them leaves no trace. aud-a28f fixed the allowlist save and introduced
`AuditService.LogSettingsChangeFailed` for exactly this.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28f/a26e agent, 2026-10-22, candidate 2 of its hand-back)

## Evidence

At `488e535e`: `Components/Pages/ModuleConfig.razor`, every `Save*Async` other than the allowlist
one; `Components/Pages/AdminSettings.razor` and `Components/Pages/ExchangeOnlineConfig.razor`
save handlers. Each calls `LogSettingsChange` after a successful write; refusal returns and catches
write no record. `LogSettingsChangeFailed` exists since aud-a28f (`07c366ef`).

## Approach (to be written by the implementing agent before code)

- Survey every settings save handler on the three pages (list them in the record with line
  numbers). For each: every refusal return records through `LogSettingsChangeFailed` with
  `refusedAt` and `mutationAttempted: false`; every catch records a failure without the refusal
  fields. Same action, category and section as the matching success record so the Admin Event Log
  filters them together. Exactly one record per attempt; notifications unchanged.
- Where a page handler can move into its service the way aud-a28f moved the allowlist save, prefer
  that; otherwise record from the page through non-throwing helpers. Do not change what any save
  writes.
- Tests: for each handler, a refused attempt leaves exactly one failed record and the store is
  unchanged; an exception leaves one failure record; a success still leaves exactly one success
  record (no double logging). Page tripwires in the a28b/a28f style where a handler cannot be driven
  without bUnit. Mutations at the call site: drop one refusal record per page; drop one catch record
  per page.
- `ClickGateRegistry`: all three pages `NotYetConverted`? Confirm each; re-anchor per the playbook if
  any has line-keyed entries. `ProgressRegistry` pins unchanged.

## Guard proof

Pending.
