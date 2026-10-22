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
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; in progress (plan below)
**Branch**: `worktree-agent-af0cdb58244ff4bed`
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `374718b3`. One commit
`fix(settings): enablement and section-access saves decide under the write lock from the rows as they
are, the connection save records its real before-state, and a log level that did not persist is
reported (aud-a28k)`, after aud-a28j on the same branch. Behaviour: Admin Settings, Module
Configuration and the shared enablement, section-access and extended-log services (base app version)
and Exchange Online Config (the Exchange Online module's version); the supervisor owns both bumps.

Evidence re-read at `374718b3`: `AdminSettings.SaveEnablement` 591-592 reads the store, then writes the
page's whole loaded map (`ModuleEnablementService.SaveEnablement` 159-175 -> `SaveAll` delete-then-insert);
`ModuleConfig.SaveModuleEnablementAsync` 1203-1206 and `ExchangeOnlineConfig.SaveModuleStatus` 221-224
read the whole map outside the write and replace it to change one module - the same read-then-replace
in two transactions, a narrower window; `ModuleConfig.SaveSectionAccessAsync` 1293-1310 re-reads the
table, overwrites every alias of the module with the page's loaded snapshot and replaces the whole
table (`SectionAccessRepository.SaveAll` 98-105), so a grant made elsewhere on any of those aliases since
the page loaded is lost, edited or not; `SaveExoConfig` 363 and `SaveProtectedPrincipals` 1010 pass an
empty before; `ExtendedLogService.SetLevel` 84-99 applies the level, then logs and swallows a store
failure, and `SaveLogLevel` 674-678 records and reports success.

1. **Enablement: only what the admin changed, decided under the lock.** New
   `ModuleEnablementService.SetModulesEnabled(changes)` over new
   `ModuleEnablementRepository.SetModules(changes, defaults)`: one write (BEGIN IMMEDIATE); per module
   the row as it is under the lock - the descriptor default where there is none - is the before-state;
   a module already in the target state is not written; nothing to write rolls back, change token
   included; a store whose legacy file was not imported writes nothing. Callers pass only the modules
   whose toggle the admin changed against what the page loaded: Admin Settings its differing toggles;
   Module Configuration and Exchange Online Config their one module when toggled (Exchange Online
   Config gains the baseline it lacked). A toggle made elsewhere is never reverted. A boolean toggle
   cannot conflict - a row already holding the target was changed the same way elsewhere, is skipped
   and is recorded by its own writer - so no `ConcurrentChange` exists here. One success record per
   module written, its before from the lock.
2. **Section access: per alias, compared under the lock.** New
   `SectionAccessService.SaveEditedAliases(loaded, edited)` over new
   `SectionAccessRepository.ReplaceAliases(decide)`: one write; each edited alias's rows as they are
   under the lock - or, when section access was never configured, the fallback the gates read today -
   are compared, as sets ignoring case, with what the page loaded; any that differs refuses the whole
   save, rolled back, as `ConcurrentChange` naming each changed alias (the admin reloads and re-applies;
   nothing merged); otherwise only the edited aliases are replaced, display names carried across, and
   aliases the admin did not edit are not touched. The never-configured case writes the fallback for
   every alias plus the marker, as the whole-table save did, so the first save still starts enforcement
   without changing anyone's effective access. A save with no edits on a configured store writes nothing.
3. **Before-state.** `SaveExoConfig` writes through `UpdateModuleValues` (aud-a28j) and records the three
   values as read under the lock as its before (empty values omitted on both sides). The protected
   principals' half landed with aud-a28j's folded-in partial save, which needed it.
4. **Log level.** `SetLevel` persists first and applies the level only once it is stored; a store
   failure is logged and rethrown, so the level in effect is unchanged. The page's catch records the
   failure (no refusal fields: the write was tried) and says the level was not saved and which level
   is still in effect.
5. **Registries and tripwires.** `ProgressRegistry` `SaveExoConfig`'s opening pin follows its write
   (`ModuleConfigSvc.UpdateModuleValues(`); `GroupGrantValidationTests`' access-save tripwire follows
   the new persist call - same property, confirm then persist the snapshot. ClickGateRegistry: nothing
   line-keyed on these pages.

Tests, new `ExchangeAdminWeb.Tests/SettingsConcurrentSaveTests.cs`, two stores on one temp file (the
aud-a33b style; a second service over a second connection factory is the other instance): a toggle made
elsewhere between Admin Settings' load and save is not reverted, and the save's record holds only its own
module; the same through Module Configuration's and Exchange Online Config's status saves; a grant
changed elsewhere on an edited alias refuses the save naming the alias, nothing written, one refusal;
the same landing inside the save as its write opens (the store's `BeforeWrite` hook); a grant made
elsewhere on an alias the page did not edit survives the save; the connection save's record lists the
value it replaced; a store that refuses the log level leaves the stored level, the level in effect, one
failure record and a message saying it was not saved. Mutations at the call site: (4) the whole
enablement map written again; (5) the empty before-state passed again; (6) the `SetLevel` failure
swallowed again; extras - the section-access comparison deleted; the section-access read taken outside
the write.

## Guard proof

Pending.
