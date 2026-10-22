# aud-a28v: A protected-principals save whose compare waits out the store's busy timeout fails whole with a generic "Failed to save"

**Severity**: LOW - operator guidance. Since aud-a28r the save compares the Secret ID under the
module-config store's write lock before any write. A database whose lock is held for more than the
5-second busy timeout now fails the whole save up front (correct: nothing written), but the operator
sees a generic "Failed to save" with no hint that the store was busy and a retry would succeed.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28r/a26p agent, 2026-10-22, candidate 5 of its hand-back)

## Evidence

At `25071281`: `Services/ModuleConfigService.cs` `CompareChangedValues` (the write lock it takes);
`Services/ProtectedPrincipalService.cs` `CompareDirectoryReadSecretId`; `Components/Pages/AdminSettings.razor`
(the save's catch and its message; NotYetConverted for `ClickGateRegistry`).

## Approach (to be written by the implementing agent before code)

- The compare's busy failure is distinguishable (the SQLite busy result or a typed outcome) and the page's
  message names it: "the settings store was busy; nothing was written; try again".
- One refusal record as today. Test through the settings harness with a held write lock; mutation at the
  call site: the generic message again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `d3f242a3`. One commit
`fix(settings): a protected-principals save whose Secret ID compare finds the settings store busy says so, and records
one refusal (aud-a28v)`, first on this branch (then aud-a26s, aud-a28w). Behaviour: Admin Settings (base app version);
no module version moves; the supervisor owns the bump.

Evidence re-read at `e9886df7`: `AdminSettings.razor` `SaveProtectedPrincipals` 1177-1196 runs the aud-a28r compare when
the secret changed - `ProtectedPrincipalService.CompareDirectoryReadSecretId` 1344-1348 -> `ModuleConfigService.CompareChangedValues`
165-167 -> `DecideChangedValues` 170-231 -> `ModuleConfigRepository.UpdateModuleIf` 122-152 -> `SqliteConfigStore.Write` 29-45,
whose `BeginTransaction(deferred: false)` (39) is `BEGIN IMMEDIATE`. Nothing on that path catches anything but
`UpdateModuleIf`'s own `NothingWritten` (148), so a write lock held elsewhere reaches the save's catch (1285-1290) as the
provider's exception: "Failed to save: SQLite Error 5: 'database is locked'." and one `LogSettingsSaveFailed` record.
Nothing was written: the compare runs before `ReplaceConfig` (1211).

Measured, not assumed (a throwaway probe in the test host, deleted before any commit): with another connection holding
`BEGIN IMMEDIATE` on the file, the store's write waited 35.2 s and then threw `Microsoft.Data.Sqlite.SqliteException`,
`SqliteErrorCode` 5 (SQLITE_BUSY), extended code 5, "SQLite Error 5: 'database is locked'.", no inner exception. The wait is
the factory's `busy_timeout=5000` (`SqliteConnectionFactory.cs` 77) inside each step of the provider's own busy retry, which
runs until the command's default 30-second timeout. This record's "5-second busy timeout" is the first of those steps.

1. **Busy is told apart from every other failure.** New `Services/Storage/ConfigStoreBusy.cs`:
   `ConfigStoreBusy.IsBusy(exception)` - a `SqliteException` whose primary result code (the low byte of the code) is
   SQLITE_BUSY. Not SQLITE_LOCKED (6): with the private cache this factory opens (`SqliteConnectionFactory.cs` 41-50) that is
   a conflict inside one connection, not another writer. Not any other `SqliteException`: a corrupt or unreadable database
   must not be told to try again.
2. **The compare's busy failure is a refusal.** In `SaveProtectedPrincipals`, the compare (1179) in its own `try` with
   `catch (Exception busy) when (ConfigStoreBusy.IsBusy(busy))`: a warning logged with the exception; the message "Not saved:
   the settings store was busy - another save, on this instance or the other, held it for longer than this one could wait -
   so the directory-read credential (Delinea Secret ID) could not be checked. Nothing was written, the protection rules
   included. Try again in a moment; your changes are still on this page."; one refusal record through
   `LogSettingsSaveRefused` under `ProtectedPrincipals`, `refusedAt: StoreBusy`, `mutationAttempted: false`; return. Any
   other exception still reaches the save's catch, as today. The filter's variable is not `ex`, so
   `SettingsSaveAuditTests.EveryRefusalAndCatchInASettingsSave_WritesItsRecord` still finds the save's own catch, and the new
   return has its refusal record directly above it.
3. **Not changed** (scope). The rules' write (`ReplaceConfig`) meeting a busy store when the secret did not change keeps the
   generic message (candidate for the hand-back); the secret's own write after the rules (`SaveDirectoryReadSecret` 1342-1348)
   already says the rules were saved and the secret was not. The 35-second wait is the shared connection factory's and is
   not changed.
4. **Registries.** ClickGateRegistry: AdminSettings.razor NotYetConverted, no line keys; nothing re-anchors. No handler
   added; ProgressRegistry's AdminSettings entry stands.

Tests, new `ExchangeAdminWeb.Tests/SettingsStoreBusyTests.cs`. Through `SettingsPageHarness`: a raw connection on the
harness's database file holds the write lock (`BEGIN IMMEDIATE`) from before the save until after it; the page, loaded before
the lock, has a rule added and a changed Secret ID; the save -> nothing written (rules and secret as seeded), exactly one
record (`Failed`, `section` ProtectedPrincipals, `refusedAt: StoreBusy`, `mutationAttempted: false`, its error the message),
the message naming the busy store, saying nothing was written and to try again, and the page still holding its edits
(dirty); then the lock released and the same page saved again -> rules and secret written, each with its success record (the
retry the message promises). The case waits out the real 35 s. At the helper: a busy code, alone or with an extended busy
code, is busy; SQLITE_LOCKED, SQLITE_CORRUPT, SQLITE_ERROR and a non-SQLite exception saying "database is locked" are not.
Mutations at the call site: (1) the generic message again - the compare's busy catch disabled (`when (false && ...)`); extra
(E1) every `SqliteException` taken as busy.

## Guard proof

Pending.
