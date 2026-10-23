# aud-a40: Every other settings save still reports a busy store as "Failed to save: SQLite Error 5"

**Severity**: LOW - the aud-a28v class at the saves aud-a28v did not cover; nothing is written in any case.
A protected-principals save whose Secret ID did not change meets a busy store at `ReplaceConfig` and
reaches the generic catch; the other settings saves' catches do the same: Admin Settings (two saves),
Exchange Online Config (two), Module Config (three). The operator sees a provider error code instead of
"the store was busy; nothing was written; try again".
**Status**: Complete on the branch (not merged); gates below; review pending; approved under the owner's 2026-10-21 "EVERYTHING" go
**Branch**: `worktree-agent-a885c578973c1cad3`
**Commit**: `00ab79a9` (plan `6db3a417`; comments `f219f2e8`)
**Reviewer**: implementer (surfaced by the aud-a28v/a26s/a28w agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `a583777d`: `Components/Pages/AdminSettings.razor` around lines 709 and 1230/1306; `Components/Pages/ExchangeOnlineConfig.razor`
around lines 352 and 581; `Components/Pages/ModuleConfig.razor` around lines 1254, 1491 and 1609 (all
NotYetConverted for `ClickGateRegistry`); `Services/Storage/ConfigStoreBusy.IsBusy` (aud-a28v).

## Approach (to be written by the implementing agent before code)

- One shared helper turns a save's exception into its operator message and refusal record
  (`StoreBusy` -> the aud-a28v text and `refusedAt: StoreBusy`, `mutationAttempted: false`; anything else
  -> the existing failure path), used by every settings save catch, so the pages stop hand-writing it.
- A busy failure on a write that may have partially applied (the second half of a two-store save) says
  so: "the store was busy during <step>; <what was written>; check and retry".
- Tests through the settings harness with a held lock per page (keep the wait short: see aud-a41);
  mutation at the call site: one page's catch back to the generic message.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `6972a5c5`. One commit `fix(settings): every settings save
that meets a busy store says so and records one refusal, through one helper (aud-a40)`, after aud-a41 on this branch (the two
share Admin Settings and the busy test). Behaviour: the three settings pages (base app version) and Exchange Online Config (the
Exchange Online module's version); the supervisor owns both bumps.

Survey at `4830230a` - every settings-save catch on the three pages, and what reaches it from a busy store (line numbers before
aud-a41 moves them). Every store call is one transaction that a throw rolls back, and the reads before a write never wait for a
held lock (measured, aud-a41), so a busy store at any of these catches means the save wrote nothing - except the protected-principals
secret, written after the rules and caught on its own.

| Page | Handler | Catch today | A busy store reaches it from |
|---|---|---|---|
| Admin Settings | SaveEnablement | "Failed to save: {message}", a failure record (709) | `SetModulesEnabled` |
| | SaveLogLevel | "Failed to save log level: ... still {level}." (771) | `ExtendedLog.SetLevel` |
| | SaveProtectedPrincipals | "Failed to save: ..." (1306); aud-a28v's busy-only catch on the compare (1190) | `ReplaceConfig` when the Secret ID did not change; the compare (aud-a28v's catch) |
| | SaveDirectoryReadSecret | "The protection rules were saved ..., but the ... Secret ID was not saved: {message} Save again ..." (1364) | the secret's write, after the rules saved |
| Exchange Online Config | SaveModuleStatus | "Failed to save: ..." (352) | `SetModulesEnabled` |
| | SaveExoConfig | "Failed to save: ..." (581) | `SaveChangedValues` |
| Module Configuration | SaveConfigAsync | "Failed to save: ..." (1254) | `SaveChangedValues` |
| | SaveModuleEnablementAsync | "Failed to save module status: ..." (1345) | `SetModulesEnabled` |
| | SaveSectionAccessAsync | "Failed to save: ..." (1491) | `SaveEditedAliases` (`SaveGroupDisplayNames` never throws) |
| | SaveModuleAdminsAsync | "Failed to save: ..." (1609) | `ApplyAdminChanges` |
| | SaveAllowlistAsync | (2008), reached only from its authorization re-check | nothing: the write is caught and recorded inside `ADAttributeEditorService.SaveAllowlistAndAudit` (owned by another agent; below) |

The record counted two, two and three saves; the survey finds three and the secret's catch on Admin Settings, two on Exchange
Online Config, and four on Module Configuration, with the allowlist's in another agent's file.

1. **One helper.** `Services/SettingsSaveAudit.cs` gains `LogSettingsSaveException(audit, logger, user, ip, section, exception,
   failure, sections = null, busy = null)`, which returns what the admin is told. A busy store (`ConfigStoreBusy.IsBusy`) is a
   refusal: a warning logged with the exception, one record through `LogSettingsSaveRefused` with `refusedAt: StoreBusy` and
   `mutationAttempted: false`, and the busy message. Anything else is the page's failure, recorded through `LogSettingsSaveFailed`
   with the page's own text, as today. Never throws (both record paths never throw). `StoreBusyMessage(written)` is aud-a28v's text
   made general - "Not saved: the settings store was busy - another save, on this instance or the other, held it for longer than
   this one could wait. Nothing was written. Try again in a moment; your changes are still on this page." - and `StoreBusyCause` is
   its cause clause, for the partial case.
2. **Every catch in the survey uses it**, its failure text unchanged: SaveLogLevel's busy text keeps "the extended log level is
   still {level}"; SaveProtectedPrincipals' says nothing was written, the protection rules and the directory-read credential (Delinea
   Secret ID) included, and aud-a28v's busy-only catch around the compare goes: its busy now reaches the same catch and the same
   refusal (StoreBusy, nothing written), so one path decides it; SaveExoConfig keeps completing its activity from the message.
3. **The partial case** - the second half of a two-store save. SaveDirectoryReadSecret's catch: a busy store at the secret's write,
   after the rules saved, is a refusal under the secret's own section (`ProtectedPrincipals:DirectoryReadSecretId`, StoreBusy,
   mutationAttempted false: its write was not made) and the message says what landed and to retry: "The protection rules were saved
   and are in effect, but the directory-read credential (Delinea Secret ID) was not saved: the settings store was busy - ... Save
   again to retry it." The rules' success record is already written, as today.
4. **The tripwire.** `SettingsSaveAuditTests.EveryRefusalAndCatchInASettingsSave_WritesItsRecord` requires each listed save's catch
   to go through `Audit.LogSettingsSaveException(` (it required `LogSettingsSaveFailed(`), so a catch that hand-writes its failure
   again fails it.
5. **Not changed.** The stores and services. SaveAllowlistAsync's catch (its busy store is decided in the owned service; see the
   follow-up section). Registries: ClickGateRegistry NotYetConverted on all three pages; no handler added.

Tests, in `ExchangeAdminWeb.Tests/SettingsStoreBusyTests.cs` (the busy class). A real held write lock - a raw connection's
`BEGIN IMMEDIATE` on the harness's file, the documented wait (about 5 s since aud-a41) - for one save per page: Admin Settings'
protected-principals save with the Secret ID unchanged (`ReplaceConfig`), Exchange Online Config's connection save, Module
Configuration's config save. Each: refused, nothing written, exactly one record (`refusedAt: StoreBusy`, `mutationAttempted: false`,
its error the message), the message saying the store was busy, nothing was written and to try again; then the lock released and the
same page's save succeeds. Every other save in the survey meets the store's own busy exception - aud-a28v's measured shape,
`SqliteException` code 5 and extended 5, "SQLite Error 5: 'database is locked'." - thrown as its write opens (`BeforeWrite`), so the
catch is pinned without a 5-second wait: the same record and message, nothing written. The partial case: the rules saved, the
secret's write busy - two records (the rules' success, the secret's StoreBusy refusal), the rules stored, the secret not, the message
saying both. At the helper: busy and not busy, with sections. The existing failure cases (`SettingsSaveAuditTests`) keep the failure
path. Mutations at the call site: (1) one page's catch back to the generic message (SaveConfigAsync's catch hand-written
`"Failed to save: {message}"` with a failure record) - its busy case fails, and the tripwire; extra (E1) the helper's busy branch
off (every exception a failure) - every busy case fails; (E2) the partial case recorded as a failure again.

## Fix

`Services/SettingsSaveAudit.cs`: `LogSettingsSaveException(audit, logger, user, ip, section, exception, failure, sections, busy)`,
which returns what the admin is told. A busy store (`ConfigStoreBusy.IsBusy`) is logged as a warning with the exception, recorded
through `LogSettingsSaveRefused` with `refusedAt: StoreBusy` and `mutationAttempted: false`, and answered with `busy`, or
`StoreBusyMessage()`. Anything else is recorded through `LogSettingsSaveFailed` with the page's own `failure` text, which is returned.
`StoreBusy`, `StoreBusyCause` and `StoreBusyMessage(written)` are aud-a28v's text made general.

`Components/Pages/AdminSettings.razor`: SaveEnablement, SaveLogLevel (its busy text keeps the level in effect) and
SaveProtectedPrincipals catch through the helper. aud-a28v's busy-only catch around the compare is gone: its busy reaches the save's
catch, which says nothing was written, the protection rules and the directory-read credential included. SaveDirectoryReadSecretAsync's
catch: a busy store at the secret's write, after the rules saved, is a refusal under `ProtectedPrincipals:DirectoryReadSecretId` (its
write was not made), the message saying the rules were saved and the secret was not, and to save again; it logs an error only for a
failure that is not busy.

`Components/Pages/ExchangeOnlineConfig.razor`: SaveModuleStatus and SaveExoConfig catch through the helper.

`Components/Pages/ModuleConfig.razor`: SaveConfigAsync, SaveModuleEnablementAsync, SaveSectionAccessAsync (its pending sections kept)
and SaveModuleAdminsAsync catch through the helper.

Tests: `SettingsStoreBusyTests` gains 11 cases. Three hold a real write lock - the protected-principals rules' write, the Exchange
Online connection, a module's configuration: refused, nothing written, one StoreBusy record, the message; then the lock released and
the same save succeeds. Six meet the store's own busy exception thrown as the write opens: the enablement save on both pages, the log
level, the Exchange Online status, section access, module admins. Then the partial case, and the helper itself.
`SettingsSaveAuditTests.EveryRefusalAndCatchInASettingsSave_WritesItsRecord` requires each listed catch to call
`Audit.LogSettingsSaveException(` and not `Audit.LogSettingsSaveFailed(`.

Caller-visible change, by design: on all three settings pages a save that meets a store another writer holds past the 5-second wait
says "Not saved: the settings store was busy - another save, on this instance or the other, held it for longer than this one could
wait. Nothing was written. Try again in a moment; your changes are still on this page." and leaves one refusal record (StoreBusy,
mutationAttempted false), where it said "Failed to save: SQLite Error 5: 'database is locked'." and left a failure record. A
protected-principals secret that meets a busy store after the rules saved says the rules were saved and the secret was not, recorded
as the secret's refusal. Every other failure keeps its text and its failure record.

## Left for a follow-up (file owned by another agent)

`Services/ADAttributeEditorService.cs` `SaveAllowlistAndAudit`, lines 464-472 at `4830230a`: the catch around
`_repository.ReplaceAllowlist(...)` turns any exception, a busy store included, into `Failed to save: {ex.Message}` and a failure record
(`LogAllowlistSaveFailed`). Needed: when `ConfigStoreBusy.IsBusy(ex)`, return `SettingsSaveAudit.StoreBusyMessage()` and record
`LogAllowlistSaveRefused(performedBy, ip, SettingsSaveAudit.StoreBusy, message)` - a refusal, nothing written - and anything else as
today. The page (`ModuleConfig.SaveAllowlistAsync`) needs nothing: it shows `result.Error`. Test: `SettingsPageHarness.AllowlistStore`
(aud-a41) throwing the busy exception as its write opens, then one StoreBusy refusal and nothing written.

## Guard proof

The aud-a41 driver (`a885-mutate.ps1`) and filter (`a885-filter-a41.txt`, 41 classes; 1328 tests with this commit's cases): the fix
staged; MD5; mutate by exact text (one match required); touch; build; filtered run; `git checkout --` from the index; touch; MD5
re-confirmed; rebuild; the same filter. Every mutant built with 0 errors, every file came back to its MD5, and the restored tree ran
1328/1328 after each row.

| # | Mutation (call site) | MD5 before | MD5 mutant | MD5 after | Named test failing |
|---|---|---|---|---|---|
| 1 | `ModuleConfig.SaveConfigAsync`: its catch hand-written again (`"Failed to save: {message}"` with a failure record) | `B3A459DB358D5F6BF3EFA73F2C26336B` | `5FE754EB93106E11157FF5C5084D5A8B` | `B3A459DB358D5F6BF3EFA73F2C26336B` | `SettingsStoreBusyTests.ModuleConfig_AConfigSaveThatFindsTheStoreBusy_IsRefused_SaysSo_WritesNothing_AndARetrySaves` (the record a failure, not the StoreBusy refusal), `SettingsSaveAuditTests.EveryRefusalAndCatchInASettingsSave_WritesItsRecord(ModuleConfig.razor, SaveConfigAsync)` (no `Audit.LogSettingsSaveException(` in the catch) |
| E1 | extra, `SettingsSaveAudit.LogSettingsSaveException`: the busy branch off (`false && ...`) | `2E324B3E37DA928001463282EF05DA86` | `7DAC8BB32970CC6AA7593AC306B0B22A` | `2E324B3E37DA928001463282EF05DA86` | every busy case in `SettingsStoreBusyTests`: the 11 new and aud-a28v's, 12 |
| E2 | extra, `AdminSettings.SaveDirectoryReadSecretAsync`: the secret's busy write after the rules recorded as a failure again | `1996AE96D530ED59712954614A6C0323` | `B45BABA0A996F05D8D2976B56F41CF7E` | `1996AE96D530ED59712954614A6C0323` | `AdminSettings_ASecretWriteThatFindsTheStoreBusyAfterTheRulesSaved_IsTheSecretsRefusal_AndSaysTheRulesWereSaved` |
| E3 | extra, `AdminSettings.SaveProtectedPrincipals`: its catch hand-written again | `1996AE96D530ED59712954614A6C0323` | `2F4CA7CBAF8777512027F996FCDCE125` | `1996AE96D530ED59712954614A6C0323` | the tripwire for `SaveProtectedPrincipals`, `AdminSettings_ARulesWriteThatFindsTheStoreBusy_...`, and aud-a28v's `AdminSettings_ASecretIdCompareThatFindsTheStoreBusy_...` - the compare's busy now decided by this catch |

The MD5s are this commit's bytes; the code comments are written after the round, in their own commit.

## Gates - 2026-10-23 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors; 61 warnings, all pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 5,933 passed /
  3 skipped (the three known AD skips) / 0 failed, 5,936 total, 5m55s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No directory, Exchange Online, Graph, DHCP, IIS, Delinea or SMTP: no case adds a group grant or a rule that is looked up, and the
connection save drains a pool that holds no session. The pages were not rendered in a browser. No coder self-review verdict is claimed.

## Known gaps

- The six cases without a held lock throw the store's busy exception from the harness's switchable store as the write opens, ahead
  of the real write; the three held-lock cases and aud-a28v's take the provider's own path. The exception's shape is aud-a28v's
  measurement.
- The Module Configuration allowlist save still reports a busy store as a failure (the follow-up above).
- A busy read ahead of a write - the corrupt re-checks - would be refused the same way, "nothing written" still true; a WAL read does
  not wait for a writer (aud-a41's measurement), so it is not exercised.
- Nothing ran against the shared database or a second instance; the held lock is a raw connection on the harness's temp file.

## Branch-tip gates - 2026-10-23 (`f219f2e8`: the three fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings, all pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 5,933 passed /
  3 skipped (the three known AD skips) / 0 failed, 5,936 total, 5m46s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
- The comments commit `f219f2e8` changes comment lines only: the code lines of all 19 source files the fixes touch, whole-line comments
  and blank lines stripped, hash the same at `086501ad` and `f219f2e8`.