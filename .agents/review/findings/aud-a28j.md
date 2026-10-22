# aud-a28j: Settings saves with no corrupt-store re-check - a protected-principals save after a failed load wipes every protection rule

**Severity**: HIGH - `AdminSettings.razor` `SaveProtectedPrincipals` has no corrupt/unreadable
re-check and the Save button stays enabled when the load failed, so a save then writes the page's
EMPTY lists over the protected-principal store and removes every protection rule in one click. The
protected-principal store is the gate every mutating module consults (Known Failure Class 3). Same
class, lower impact: `ExchangeOnlineConfig.razor` `SaveModuleStatus` has no `IsStoreCorrupt`
refusal, and `SaveExoConfig` guards a corrupt config only with a disabled button, which a stale
render or a second tab can defeat.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; in progress (plan below)
**Branch**: `worktree-agent-af0cdb58244ff4bed`
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `374718b3`, with the partial
protected-principals save below folded in by codex attr-batch-4 (`83cb9992`). One commit
`fix(settings): a settings save re-checks its store and refuses a page that did not load it, and a
protected-principals save decides under the write lock and reports a partial save (aud-a28j)`, first
of three on this branch (then aud-a28k, aud-a26h). Behaviour: Admin Settings and Module Configuration
and the shared stores (base app version) and Exchange Online Config (the Exchange Online module's
version); the supervisor owns both bumps.

Survey at `374718b3` - every settings save that replaces a store's content:

| Page | Handler | Fresh re-check before the write | Page's load state consulted | Save offered after a failed load |
|---|---|---|---|---|
| `AdminSettings.razor` | `SaveEnablement` 560 | `IsStoreCorrupt` 582 | no (`enablementStoreCorrupt` 539) | yes (save bar 325) |
| | `SaveProtectedPrincipals` 961 | none | no: a load error (695) leaves the five lists empty | yes |
| `ModuleConfig.razor` | `SaveConfigAsync` 1105 | `IsModuleCorrupt` 1131 | no (`moduleConfigCorrupt` 961) | yes (save bar 670) |
| | `SaveModuleEnablementAsync` 1173 | `IsStoreCorrupt` 1194 | no (960) | yes |
| | `SaveSectionAccessAsync` 1246 | `IsFragmentCorrupt` 1267 | no (962); and `IsSectionAccessConfigured` (963) throws on an unreadable store, so that load fails outright | yes |
| | `SaveModuleAdminsAsync` 1352 | none | none: `GetModuleAdmins` (967) reads an unreadable store as no admins | yes |
| `ExchangeOnlineConfig.razor` | `SaveModuleStatus` 199 | none; `GetAllEnablement` (221) on a corrupt store is the all-disabled fallback, written back for every module (224) | none | yes (59) |
| | `SaveExoConfig` 251 | none | `configCorrupt` only through the button (81); a load that threw (193) leaves blank fields, `configCorrupt` false | yes |

Not content replaces: `SaveLogLevel` (one key; aud-a28k) and `SaveAllowlistAsync` (decides under the
lock since aud-a28i; its Save is withheld on a corrupt allowlist like the rest).

1. **Load state.** Each page records, per store, whether its load read the store: the existing
   corrupt flags, plus `moduleAdminsUnreadable` (`ModuleAdmin.TryGetAllGrants`), `ppLoaded`
   (`LoadEffectiveConfig` returned no error), and on Exchange Online Config `enablementStoreCorrupt`
   (`IsStoreCorrupt` at load) and `exoConfigLoaded` (`LoadExoConfig` neither found the module corrupt
   nor threw). `IsSectionAccessConfigured` is not asked when the section store is already corrupt, so
   that load completes and shows its banner instead of failing the whole page.
2. **Save not offered.** The save bar's Save on both tabbed pages is disabled while the current tab's
   store did not load (`CanSaveCurrentTab()`); Exchange Online Config's two buttons each follow their
   own store. New banners for the module-admins and Exchange Online enablement load failures, naming
   the table.
3. **The handler refuses even if invoked.** After the authorization re-check, through
   `LogSettingsSaveRefused` directly above the return, `mutationAttempted: false`:
   `StoreCorrupt` when the fresh re-check finds the store corrupt or unreadable now (kept where it
   exists; new on `SaveModuleAdminsAsync`, `SaveModuleStatus`, `SaveExoConfig`); `StoreUnreadable`
   when the page's own load did not read the store, so what it would write is not the store's state.
4. **Protected principals decide inside the write** (the aud-a28i shape). New
   `ProtectedPrincipalRepository.Replace(decide)`: one `_store.Write` (BEGIN IMMEDIATE) reads the
   rows and the presence marker on its own transaction and hands them to `decide`; null throws a
   private `NothingWritten`, so a refusal rolls back, change token included; returns the rows as read
   and as read back. New `ProtectedPrincipalService.ReplaceConfig(config, pageLoadedRules)` returns
   `Saved | StoreCorrupt | EmptyOverNonEmpty` with the before and after rows: `StoreCorrupt` when the
   legacy file was not imported or the rows cannot be read under the lock; `EmptyOverNonEmpty` when
   the new config holds no rule of any of the five kinds, the rows under the lock hold at least one,
   and the page loaded none - refused saying the rules could not be read (or there were none when
   the page loaded) and that nothing was changed. A page that loaded rules and had the admin remove
   them one by one still saves. No merge and no guess: the admin reloads.
5. **The folded-in partial save.** The directory-read secret id is written only when the admin changed
   it (`SecretIdDelta`), after the rows, in its own try, through new
   `ModuleConfigService.UpdateModuleValues` (upserts the named keys inside one write and returns the
   module's values as read under the lock); `SaveDirectoryReadSecretId` uses it. Its store is
   re-checked (`IsModuleCorrupt`) before anything is written. Records: the rows' success record with
   the real before and after (`Users:<value>`, `Groups:`, `OUs:`, `Patterns:`, `Targets:`, the rows read
   under the lock and read back - aud-a28k's before-state for this save lands here, because the
   partial-save record needs it); a changed secret's own success record (section
   `ProtectedPrincipals:DirectoryReadSecretId`); a secret write that throws after the rows landed
   leaves the rows' success record and a failure record for the secret, and the message says the
   rules were saved and the secret was not. The rows re-baseline; the secret stays pending.
6. **Registries.** `ClickGateRegistry`: all three pages NotYetConverted ("tier 2, not approved"), no
   line keys, no `ExpectedLineCount` - nothing re-anchors. No handler is added; `ProgressRegistry`'s
   pins hold.

Tests, new `ExchangeAdminWeb.Tests/SettingsSaveStoreCheckTests.cs` through `SettingsPageHarness` (a
second service over a second store on the same temp file stands in for the other instance): on each
of the three pages a load that could not read the store, then Save -> nothing written, exactly one
`StoreUnreadable` refusal, Save not offered; a store corrupt at save -> one `StoreCorrupt` refusal; a
healthy load and save still succeed. Protected principals: a failed load, an entry added, Save ->
refused, every stored rule kept; a page that loaded no rules saving over rules the other instance
added since -> `EmptyOverNonEmpty`, the rules kept; the rows unreadable under the lock -> refused, no
write; a throwing secret save after the rows saved -> the rows' success record (real removals and
additions) and the secret's failure record, the message says the rules were saved. Mutations at the
call site: (1) the protected-principals re-check dropped; (2) the empty-over-non-empty refusal
dropped; (3) an Exchange Online Config corrupt refusal dropped; (folded) one record for both again.

## Guard proof

Pending.

## Folded in: a partial protected-principals save audited only as failed (codex, attr-batch-4, 2026-10-22)

`AdminSettings.razor` `SaveProtectedPrincipals` (around line 1004 at `f1b4692d`): `SaveConfig(newConfig)`
succeeds, then `SaveDirectoryReadSecretId(...)` throws. The rules are live, the page says "Failed to
save", and the only record is a generic failed `ProtectedPrincipals` settings save with no
before/after detail. Save the rows and the directory-read secret in one atomic store write (one
`Write` callback, aud-a28i shape), or - if the secret id lives in another store - audit the partial
outcome explicitly: a success record for the rows that landed (real before/after) and a failure record
for the secret, and a message that says which part landed. Test: a throwing secret save after a
successful rows save leaves the rows' success record and the secret's failure record, and the message
says the rules were saved. Mutation at the call site: one record for both again.
