# aud-a26t: Validator log wording and test hygiene - the shared remedy text ends "then reload this page" inside a service log line, and the validator tests share one audit log root and never delete their directories

**Severity**: LOW. (1) `SettingsStoreText.DatabaseRemedy` ends "then reload this page"; aud-a26q reused it
in `PermissionValidator`'s corrupt-branch log, where no page is involved. (2) `CreateValidator`'s
`Audit:LogRoot` is still one shared directory under the temp directory, and the per-validator test
directories aud-a26q introduced are never deleted (pre-existing shape; not a database, so the
aud-a26q tripwire does not cover it).
**Status**: Plan written; in progress
**Branch**: `worktree-agent-a4c579a99f70ca768`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28t/a26q agent, 2026-10-22, candidates 4 and 5 of its hand-back)

## Evidence

At `18c62ce9`: `Services/SettingsStoreText.cs` `DatabaseRemedy`; `Services/PermissionValidator.cs` (the
corrupt-branch log); `ExchangeAdminWeb.Tests/PermissionValidatorTests.cs` `CreateValidator`
(`Audit:LogRoot`, the `testDir` creation and no disposal).

## Approach (to be written by the implementing agent before code)

- `DatabaseRemedy` takes a surface (page or service) or the service callers use a remedy text without the
  reload clause; the existing page callers' text is unchanged.
- The validator helpers put the audit log root under the test's own directory and the test class disposes
  it (an `IDisposable` fixture or a per-test `try/finally`); the aud-a26q source scan gains "no
  `Audit:LogRoot` directly under the temp directory".
- Mutations at the call site: the reload clause back in the service log; the shared root again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21). Branch
`worktree-agent-a4c579a99f70ca768` from master `18530b58`: one fix commit `fix(settings-text): ... (aud-a26t)`, third of
three, after aud-a28y (the same `PermissionValidator.cs`) and aud-a28x (whose unreadable message it also moves), and the
comments-only commit after it. Behaviour: two log lines and one refusal message of shared services (base app version,
the bump aud-a28y and aud-a28x already need); the rest is tests.

Evidence re-read at `18530b58`:
- `SettingsStoreText.DatabaseRemedy` 16-17 ends "then reload this page"; every text in the class is built on it
  (`Enablement`, `SectionAccess`, `ModuleConfig`, `ModuleAdmins`, `ProtectedPrincipals`), and the admin pages render and
  refuse with them (AdminSettings, ExchangeOnlineConfig, ModuleConfig). The only callers that are not a page are in
  `PermissionValidator`: the corrupt-branch log (673-674, aud-a26q) and the unreadable self-grant setting's log (88-89,
  aud-a28t), both `SettingsStoreText.ModuleConfig("MailboxPermissions")` in a service's log line, where no page is
  involved. aud-a28x adds a third: the pool's unreadable message, a service's exception that is logged and shown by
  whichever module borrowed, a background job included.
- `PermissionValidatorTests.CreateValidator` 43-126: `Audit:LogRoot` is `%TEMP%\eaw-test-logs` (58), one directory every
  validator and every run shares - the `ExtendedLogService` each validator builds creates `ExchangeAdminWeb` under it
  (`ExtendedLogService` constructor). The per-validator `testDir` (51), `NewStore`'s directory (130-135) and
  `CreateValidatorWithScriptedResolution`'s `testDir` (606) are created and never deleted, and the `ExtendedLogService`
  and `ExoConnectionPool` each helper builds are never disposed. The class is not `IDisposable`.
- `SharedTempDatabaseTripwireTests` (aud-a26q) refuses a source naming the temp directory itself or a config database
  directly in it; `Path.Combine(Path.GetTempPath(), "eaw-test-logs")` passes it. No other test source sets
  `Audit:LogRoot` from the temp directory (every other one is the test's own directory variable).

1. **A service's remedy.** `SettingsStoreText` gains `ServiceDatabaseRemedy` - the same fix without "then reload this
   page" - and `ServiceModuleConfig(moduleId)` on it. `DatabaseRemedy` and every page text keep their exact wording; the
   two remedies are built from one shared clause so they cannot drift apart. The three service callers - the validator's
   two log lines and the pool's unreadable message - use the service text.
2. **Every directory a validator test makes is its own and is deleted.** `PermissionValidatorTests` becomes
   `IDisposable`. Its three helpers (instance methods now) make each directory through one helper that registers it, put
   `Audit:LogRoot` under the validator's own directory, and register the `ExtendedLogService` and `ExoConnectionPool`
   they build. `Dispose` disposes those, clears the SQLite connection pool for each directory's database (the
   `AttributeEditorHarness` shape) and deletes the directories.
3. **Tripwire.** `SharedTempDatabaseTripwireTests` gains a rule: no test source sets `Audit:LogRoot` from the temp
   directory (`GetTempPath()` in the value) - a log root is a directory the test made and deletes.

Tests: `PermissionValidatorExclusionsReadTests.AStoreThatCannotBeRead_LogsTheRowsAndTheirFix_NotAFile` asserts the service
text and no "reload this page"; `PermissionValidatorConfigReadTests` gains the unreadable self-grant setting logging one
error with the service text and no reload clause; `ExoConnectionPoolConfigReadTests` (aud-a28x) asserts the same of the
pool's message; new `ExchangeAdminWeb.Tests/StoreTextSurfaceTests.cs` pins that the page text is unchanged and that the
service text is that text without its reload clause; `PermissionValidatorTests` gains a case that builds a validator
through each helper, finds its logs under its own directory, disposes the class and finds every directory gone; the
tripwire's new rule. Mutations at the call site: (1) the reload clause back in the corrupt-branch log
(`SettingsStoreText.ModuleConfig` there again); (2) the shared root again (`Path.Combine(Path.GetTempPath(),
"eaw-test-logs")` in `CreateValidator`). Extras: (E1) `Dispose` deletes nothing; (E2) the self-grant setting's log back
on the page text; (E3) the pool's message back on the page text.

## Fix

`Services/SettingsStoreText.cs`: the fix's clause is one private constant; `DatabaseRemedy` is that clause and ", then reload
this page.", byte for byte the text it was, and new `ServiceDatabaseRemedy` is the clause and "."; `ModuleConfig(moduleId)`
is unchanged in its text, and new `ServiceModuleConfig(moduleId)` names the same rows with the service remedy. Every page
caller is untouched.
`Services/PermissionValidator.cs`: the corrupt-branch log (aud-a26q) and the unreadable self-grant setting's log (aud-a28t)
use `ServiceModuleConfig`. `Services/ExoConnectionPool.cs`: the unreadable-rows message and its log line (aud-a28x) too.
`ExchangeAdminWeb.Tests/PermissionValidatorTests.cs`: the class is `IDisposable` (and sealed; nothing derives from it). Its
three helpers, instance methods now, make every directory through `NewTestDir`, which registers it; `CreateValidator`'s
`Audit:LogRoot` is `logs` under its own directory (`CreateValidatorWithScriptedResolution`'s already was its directory);
both register the `ExtendedLogService` and `ExoConnectionPool` they build. `Dispose` disposes those, clears the SQLite
connection pool for each directory's database and deletes the directories.
`ExchangeAdminWeb.Tests/SharedTempDatabaseTripwireTests.cs`: a third case - no test source sets `Audit:LogRoot` from the
temp directory (an indexer assignment or a collection-initializer pair whose value names `GetTempPath()`); the source walk
the two scans share is one helper.

Tests: `PermissionValidatorTests.TheHelpers_KeepEveryDirectoryAndTheLogsTheirOwn_AndDisposeDeletesThem`;
`SharedTempDatabaseTripwireTests.NoTestSource_SetsTheAuditLogRootFromTheTempDirectory`; new
`ExchangeAdminWeb.Tests/StoreTextSurfaceTests.cs` (2: the page text as it was, the service text without its reload clause);
`PermissionValidatorConfigReadTests.AModuleConfigThatCannotBeRead_LogsTheRowsAndTheirFix_WithNoPageToReload`; and the
service-text assertions in `PermissionValidatorExclusionsReadTests.AStoreThatCannotBeRead_LogsTheRowsAndTheirFix_NotAFile`
and the two `ExoConnectionPoolConfigReadTests` message cases. 5 cases added.

Caller-visible change, by design: three service texts - the validator's two error lines and the pool's unreadable-rows
refusal and its log line - end at the fix ("... with both application pools stopped.") instead of "then reload this page".
Every admin page's text is as it was. The tests change no behaviour: the validator tests' logs no longer share a directory
under the temp directory, and nothing they make is left behind.

## Guard proof

Pending.
