# aud-a40: Every other settings save still reports a busy store as "Failed to save: SQLite Error 5"

**Severity**: LOW - the aud-a28v class at the saves aud-a28v did not cover; nothing is written in any case.
A protected-principals save whose Secret ID did not change meets a busy store at `ReplaceConfig` and
reaches the generic catch; the other settings saves' catches do the same: Admin Settings (two saves),
Exchange Online Config (two), Module Config (three). The operator sees a provider error code instead of
"the store was busy; nothing was written; try again".
**Status**: In progress (plan below); approved under the owner's 2026-10-21 "EVERYTHING" go
**Branch**: `worktree-agent-a885c578973c1cad3`
**Commit**: -
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

## Guard proof

Pending.
