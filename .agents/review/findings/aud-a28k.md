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
**Status**: Fixed on the branch; closed on guard proof; awaiting merge and codex review
**Branch**: `worktree-agent-af0cdb58244ff4bed`
**Commit**: `b2afe0e2` (plan `6869fd81`; comments `6aaa1133`)
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

## Executed reproduction - 2026-10-22

Branch `worktree-agent-af0cdb58244ff4bed` on aud-a28j (`1c2bb9ff`), the fix's services and tests in place
and the three pages and `ExtendedLogService.cs` as they were at `1c2bb9ff` swapped in (AdminSettings
`6C3C208CCC8503C14C8B0B27981B2212`, ModuleConfig `1A5A38150093E0CE63DE5CA50F655482`, ExchangeOnlineConfig
`3F5014B1374DCE1530C053E296D262B7`, ExtendedLogService `71AACCD15C659EAA86EC53F168B860A6`), build 0
errors: `SettingsConcurrentSaveTests` 10 failed / 1 passed. The toggle the other instance switched off
was switched back on by all three enablement saves (`Assert.False() Failure`), the two that ran it inside
the save included; a grant changed elsewhere on the edited alias was overwritten, both between load and
save and inside the save (`[A, B, C]` expected, `[A]` stored); a grant made elsewhere on the alias the page
did not edit was wiped (`MailboxPermissionsOnPrem` gone); a save with nothing edited rewrote the table
(change token 1 -> 2); Save Status with no toggle changed wrote its loaded state back; the connection
save's record held no removal (`[]`); the refused log level was reported as set (`"Warning"` in effect).
The one that passed is the never-configured case, which calls the new service directly. The fixed bytes
were restored to their MD5s afterwards.

## Fix

`Services/Storage/ModuleEnablementRepository.cs`: `SetModules(changes, defaults)` - one write; per module
the row read on the write's transaction (or the default where there is none) is the before-state; a module
already in the target state is skipped; each other one is upserted alone; nothing written throws the
private `NothingWritten`, rolling back, change token included. Record `EnablementChange(ModuleId, Before,
After)`.

`Services/ModuleEnablementService.cs`: `SetModulesEnabled(changes)` returns
`EnablementSaveOutcome(StoreCorrupt, Written)`: nothing written while the legacy file is not imported;
system modules and unknown ids dropped; otherwise the repository write under the service's lock.

`Services/Storage/SectionAccessRepository.cs`: `ReplaceAliases(decide)` - one write; `decide` sees the table
and marker as read on the write's transaction (`SectionAccessSnapshot`; unreadable as `Unreadable`);
the aliases it returns are each deleted and re-inserted, display names carried across, the marker set;
null rolls back. `ClearAndInsert` shares the new display-name and insert helpers; the readers gain
transaction-aware overloads. Records `SectionAccessSnapshot`, `SectionAccessReplaceOutcome`.

`Services/SectionAccessService.cs`: `SaveEditedAliases(loaded, edited)` returns
`SectionAccessSaveOutcome(Status, ChangedAliases, Before)`: `StoreCorrupt` (legacy file not imported, or
the table unreadable under the lock); `ConcurrentChange` naming every edited alias whose rows under the lock
- or, never configured, the fallback the gates read (`FallbackAccess`: the legacy appsettings block, else
each configurable alias as `GetGroupsForSection` answers it) - differ, as sets ignoring case, from what the
page loaded; `NothingToSave` (configured, nothing edited); else `Saved`, replacing only the edited aliases,
plus the whole fallback and the marker when never configured.

`Services/ExtendedLogService.cs`: `SetLevel` stores first and applies the level only once stored; a store
failure is logged and rethrown.

`Components/Pages/AdminSettings.razor`: `SaveEnablement` passes `ChangedToggles()` (the toggles that differ
from the loaded baseline) to `SetModulesEnabled` and records one change per module written, its before from
the lock; `SaveLogLevel`'s catch says the level was not saved and which level is still in effect.
`Components/Pages/ModuleConfig.razor`: `SaveModuleEnablementAsync` passes its module only when toggled;
`SaveSectionAccessAsync` passes the aliases whose confirmed snapshot differs from the loaded baseline and
their loaded lists to `SaveEditedAliases`, refuses `StoreCorrupt` and `ConcurrentChange` (the message names
each alias as the Access tab heads it, with the alias), records one change per edited alias with the rows
under the lock as before, and says when there was nothing to save. `Components/Pages/ExchangeOnlineConfig.razor`:
`SaveModuleStatus` gains the loaded baseline, writes only a changed toggle through `SetModulesEnabled`, and
says when nothing changed; `SaveExoConfig` writes through `UpdateModuleValues` and records `AppId=`, `Org=`,
`Cert=` as read under the lock (before) and as left (after), empty values omitted.

Tests: new `ExchangeAdminWeb.Tests/SettingsConcurrentSaveTests.cs` (11 cases); `SettingsPageHarness`:
`LogLevelStore` (the extended-log store behind a switch), `CreateOtherEnablement()`,
`CreateOtherSectionAccess()`. `ProgressRegistry` `SaveExoConfig`'s opening pin follows the write
(`ModuleConfigSvc.UpdateModuleValues(`); `GroupGrantValidationTests`' access-save tripwire follows the new
persist call, still requiring the confirmed snapshot and never the live list.

Caller-visible change, by design: an enablement save writes only the modules the admin switched, so a toggle
made elsewhere since the page loaded stays; a section-access save writes only the aliases the admin edited
and is refused, naming the alias, when one of them was changed elsewhere since the page loaded - the admin
reloads and re-applies; a save with nothing changed writes nothing. The connection save's record lists what
it replaced. A log level the store refuses is reported as not saved and is not applied.

## Guard proof

Same driver as aud-a28j, filter
`FullyQualifiedName~SettingsConcurrentSaveTests|FullyQualifiedName~SettingsSaveStoreCheckTests|FullyQualifiedName~SettingsSaveAuditTests`
(76 tests). Fixed-file MD5s: `Components/Pages/AdminSettings.razor` `303A84E590A0F32B2C759BEBC0770FBB`,
`Components/Pages/ExchangeOnlineConfig.razor` `5DCBF849D4CE0D272E3D529B541D3B5A`,
`Services/ExtendedLogService.cs` `47A60E337C302CDB73390EE516280BA0`, `Services/SectionAccessService.cs`
`AF907383DD0520919F8777A01DF1B2BB`; every mutant built with 0 errors and every file was restored to its MD5.

| # | Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|---|
| 4 | `AdminSettings.SaveEnablement`: the page's whole enablement map written again (`Enablement.SaveEnablement(moduleEnablement)` above the per-module call) | `27CA4CECD54476903DEA01D35AF59F14` | `AdminSettings_AToggleMadeElsewhereSinceThePageLoaded_IsNotReverted` (the other instance's toggle switched back on), `SettingsSaveAuditTests.AdminSettings_AnEnablementSave_LeavesExactlyOneSuccessRecord_AndReloads` |
| 5 | `ExchangeOnlineConfig.SaveExoConfig`: the empty before-state passed again | `7E55A0C8E08515152D20B18F14970645` | `ExchangeOnlineConfig_AConnectionSave_RecordsTheValueItReplaced` (no removal recorded) |
| 6 | `ExtendedLogService.SetLevel`: the store failure swallowed again (applied first, caught and logged) | `DBDAFB1FA867BEA620210E4C46380FF1` | `AdminSettings_ALogLevelTheStoreRefuses_IsReportedAndRecordedAsAFailure` (`"Warning"` in effect, a success record) |
| KA | extra, `SectionAccessService.SaveEditedAliases`: the comparison's refusal deleted | `E7D7ABD3E9ACEDDF98EF78E9AB0F6C6D` | `ModuleConfig_AGrantChangedElsewhereOnAnEditedAlias_RefusesTheSave_NamingTheAlias`, `ModuleConfig_AGrantChangedElsewhereInsideTheSave_RefusesTheSave` |
| KB | extra, the same: the comparison made against a read taken outside the write | `3A7962352715FD4CFD528C6C1BBCE4B1` | `ModuleConfig_AGrantChangedElsewhereInsideTheSave_RefusesTheSave` only - the between-load-and-save case still refuses, as it must: the outside read sees a change made before it, and only one landing between the read and the write gets through |
| KD | extra, `ExchangeOnlineConfig.SaveModuleStatus`: written with no toggle changed | `6CA4A5AF44CD9580639874DBE3D8C975` | `ExchangeOnlineConfig_AStatusSaveWithNoToggleChanged_WritesNothing` (the change made elsewhere reverted) |

The code comments are written after the round, in their own commit, so the MD5s above are this commit's
bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; warnings all pre-existing, none in a touched
  file.
- `dotnet test ExchangeAdminWeb.slnx -c Release --no-build --blame-hang --blame-hang-timeout 10m`: exit 0,
  5,302 passed / 3 skipped (the three known AD skips) / 0 failed, 5,305 total, 5m16s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check`: exit 0.
  `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.

No PowerShell source, AD module, directory, Exchange Online, Delinea, SMTP, push or deployment; no case adds
a usable group grant. No coder self-review verdict is claimed.

## Known gaps

- A section-access save is refused when an edited alias changed at all since the page loaded it, even when
  the other change touched a different group of that alias. Deliberate: nothing is merged or guessed; the
  admin reloads and re-applies.
- Enablement compares booleans, so nothing there refuses: a toggle switched the same way elsewhere is
  skipped and recorded only by its own writer.
- The protected-principals save still replaces the whole rule set from the page's lists, and
  `SaveConfigAsync` / `SaveModuleAdminsAsync` a module's whole config or admin list (aud-a28j's known gaps):
  outside this finding's listed saves.
- `ModuleEnablementService.SaveEnablement` and `SectionAccessService.SaveSectionAccess` (the whole-map and
  whole-table writes) stay public; no page calls them any more, and tests use them as fixtures.
- Two in-process stores on one temp file stand in for the two instances; nothing ran against the real
  shared database.

## Branch-tip gates - 2026-10-22 (`6aaa1133`: all three fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, warnings all pre-existing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is
  written): exit 0, 5,321 passed / 3 skipped (the three known AD skips) / 0 failed, 5,324 total, 5m15s. No
  hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`:
  exit 0. `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
