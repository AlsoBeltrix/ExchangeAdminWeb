# aud-a26q: Validator tests share the real %TEMP% database, a log line still names a config file, and two documents name a retired method

**Severity**: LOW - test hygiene and stale text. (1) `PermissionValidatorTests.CreateValidator` puts
its protected-principal, enablement and section-access stores in the shared `%TEMP%\exchangeadmin.db`;
if any test ever writes central protected-principal config there, every validator test silently
changes meaning. (2) The validator's corrupt-branch log text still says "Module config file is
corrupt ... until file is fixed", though there has been no file since the SQLite move (the aud-a26f/a26h
class). (3) `.agents/decisions.md` (2026-07-28) and `docs/RetireExcludedUsersAppsettingsFallback-Plan.md`
still name `GetConfiguredExclusions`, renamed by aud-a28s; historical text, but a pointer to the
rename keeps a reader from searching for a method that no longer exists.
**Status**: Merged `18c62ce9`; closed on guard proof; codex review pending
**Branch**: `worktree-agent-acba5cd6b0762c5bf`
**Commit**: `a7767f0b` (fix, tests, pointers, record); plan `b18df623`; comments `9b4bf0e6`; merged `18c62ce9`
**Reviewer**: implementer (surfaced by the aud-a28s agent, 2026-10-22, candidates 3, 5 and 6 of its hand-back)

## Evidence

At `0bcb92c0`: `ExchangeAdminWeb.Tests/PermissionValidatorTests.cs` `CreateValidator`;
`Services/PermissionValidator.cs` the corrupt-branch log text; the two documents.

## Approach (to be written by the implementing agent before code)

- Tests use a per-test temp database file (the pattern the aud-a33b/a28i tests use), never the
  shared `%TEMP%` path; a tripwire asserts no test references `exchangeadmin.db` under `%TEMP%`.
- Log text names the config database table (`SettingsStoreText`, aud-a26h).
- The two documents get a one-line pointer: "renamed `TryGetConfiguredExclusions` by aud-a28s
  (2026-10-22)"; historical content otherwise untouched.
- Mutations at the call site: the shared path again; the file wording again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21); admitted at `0262d744`. One
fix commit `fix(permission-validator-tests): ... (aud-a26q)` on `worktree-agent-acba5cd6b0762c5bf`, after aud-a28t's;
the comments-only commit after it covers both. Behaviour: one log line of the shared `PermissionValidator` (base app
version, the bump aud-a28t already needs); the rest is tests and two documents; the supervisor owns the bump.

Evidence re-read at `5d93680b`:
- `ExchangeAdminWeb.Tests/PermissionValidatorTests.cs` `CreateValidator` 43-126 gives each validator its own `testDir`
  (51) for module config only; its app-settings (104), protected-principal (110), enablement (113) and section-access
  (122) stores are `TestConfigStore.Create*(Path.GetTempPath())` - `%TEMP%\exchangeadmin.db`, shared by every validator,
  every class and every run. `CreateValidatorWithScriptedResolution` 600-683 does the same with its section-access store
  whenever no servicer is seeded (667). A protected-principal, enablement or section-access row left in that file changes
  what every later validator test means. No other test file passes the bare temp root.
- `Services/PermissionValidator.cs` 657, the corrupt branch: "Module config file is corrupt - blocking all
  protected-target operations until file is fixed" - there is no file since the SQLite move (the aud-a26f/a26h class).
- `.agents/decisions.md` 2026-07-28 (1783-1805, the names at 1789-1790) and
  `docs/RetireExcludedUsersAppsettingsFallback-Plan.md` (19-20, 95, 107) name `GetConfiguredExclusions`, renamed
  `TryGetConfiguredExclusions` by aud-a28s, beside `GetLegacyExclusions`, renamed `TryGetLegacyExclusions` by aud-a28p.

1. **One database per validator.** Both helpers put every store on their own `testDir` file - the aud-a33b/a28i shape, a
   unique directory per test with every store on its one file. Nothing else in the class changes.
2. **Tripwire.** `ExchangeAdminWeb.Tests/TestConfigStore.cs` `Create` (13-19), the one place every test store is built,
   refuses the temp root itself (throws, naming the rule), so no expression - a ternary, a variable - reaches the shared
   file through the helper. New `ExchangeAdminWeb.Tests/SharedTempDatabaseTripwireTests.cs`: (a) the helper refuses the
   temp root, with and without its trailing separator; (b) source, comments blanked: in the test project every
   `GetTempPath()` is the first segment of a `Path.Combine` - a directory under the temp root, never the root - and none
   puts an `exchangeadmin*.db` directly under it; `TestConfigStore.cs` and the tripwire, which name the root on purpose,
   are the only files exempt.
3. **Log text.** The corrupt branch logs `SettingsStoreText.ModuleConfig("MailboxPermissions")` (aud-a26h: the table and
   its documented fix), that every protected-target operation is refused, and that the load is retried after
   `FailedLoadRetry` - no "file". Test in `PermissionValidatorExclusionsReadTests` (the corrupt branch's class): the store
   unreadable from its first read logs one error carrying that text and not "file".
4. **Documents.** One pointer line in each, next to the names: both renames, with the findings and the date. Historical
   content otherwise untouched; the two renames share the line because both documents name both methods side by side.

Mutations at the call site: (3) one `CreateValidator` store back on the shared temp root; (4) the corrupt branch's file
wording back.

## Fix

`ExchangeAdminWeb.Tests/PermissionValidatorTests.cs`: `CreateValidator` puts its app-settings, protected-principal,
enablement and section-access stores on its own `testDir` file beside its module config (104, 110, 113, 122), and
`CreateValidatorWithScriptedResolution` its section-access store on its `testDir` whether or not a servicer is seeded
(667). Nothing else in the class changed.
`ExchangeAdminWeb.Tests/TestConfigStore.cs`: `Create`, which every other helper builds through, refuses the temp directory
itself (`InvalidOperationException` naming the directory and the rule), with or without its trailing separator.
New `ExchangeAdminWeb.Tests/SharedTempDatabaseTripwireTests.cs` (2 cases): the helper refuses the temp directory itself;
in the test project's sources, comments blanked, every `GetTempPath()` is the first segment of a `Path.Combine`, and none
puts an `exchangeadmin*.db` directly under it (`TestConfigStore.cs` and the class itself exempt).
`Services/PermissionValidator.cs`: the corrupt branch logs "The protected-user list cannot be loaded - every
protected-target operation is refused; the load is retried after {Retry}. {Store}", the store being
`SettingsStoreText.ModuleConfig("MailboxPermissions")`.
`ExchangeAdminWeb.Tests/PermissionValidatorExclusionsReadTests.cs`: `ValidatorOver` takes an optional logger; new
`AStoreThatCannotBeRead_LogsTheRowsAndTheirFix_NotAFile` - one error, carrying that store text, and no "file".
`.agents/decisions.md` 2026-07-28 and `docs/RetireExcludedUsersAppsettingsFallback-Plan.md`: one line each after the names,
"Renamed since, 2026-10-22: `TryGetConfiguredExclusions` (aud-a28s) and `TryGetLegacyExclusions` (aud-a28p)."

No departure from the plan. Caller-visible change, by design: one log line - an operator reading the log after the
validator could not read the Mailbox Permissions rows is told which rows, how they are fixed and that the load is retried,
not to fix a file. The tests change no behaviour: the validator tests no longer share a database with each other, with
other classes or with earlier runs.

## Guard proof

Same driver and filter as aud-a28t (348 tests with this commit's three new cases). Fixed-file MD5s:
`ExchangeAdminWeb.Tests/PermissionValidatorTests.cs` `7891BC16FBD2452B8A73B6B5C34AE123`, `Services/PermissionValidator.cs`
`4851B2D665ABE08A13C136EDAFE0FDA7`, `ExchangeAdminWeb.Tests/TestConfigStore.cs` `3A7F4D6A4ABEF30AF5BEA5A930C6C0F3`; every
mutant built with 0 errors and its file was restored to that MD5 each time. After the round, the restored tree rebuilt and
the same filter: 348/348 green.

| # | Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|---|
| 3 | `CreateValidator`: the protected-principal store back on the shared temp root (`CreateProtectedPrincipal(Path.GetTempPath())`, 110) | `655ABB90B2DA55185B19D0A0A3B5D096` | `SharedTempDatabaseTripwireTests.NoTestSource_UsesTheTempDirectoryItself_OrPutsTheConfigDatabaseInIt` ("PermissionValidatorTests.cs:110 names the temp directory itself"), and the 27 `PermissionValidatorTests` cases built through `CreateValidator` (the helper: "... is the temp directory itself") |
| 4 | the corrupt branch's file wording back ("Module config file is corrupt - ... until file is fixed") | `CD5E1DBCC34698BD1D444658550B3F85` | `PermissionValidatorExclusionsReadTests.AStoreThatCannotBeRead_LogsTheRowsAndTheirFix_NotAFile` |
| E4 | extra, `TestConfigStore.Create`: the guard never true (`dir.Length < 0 && ...`) | `DF705C7CE219D78399A6032C561EADDC` | `SharedTempDatabaseTripwireTests.TheTestStoreHelper_RefusesTheTempDirectoryItself` |
| E5 | extra, `CreateValidatorWithScriptedResolution`: the ternary back (`servicerSid is null ? Path.GetTempPath() : testDir`, 667) | `CF54E3A0FAD0F9073D35EE53EE705B5C` | `SharedTempDatabaseTripwireTests.NoTestSource_UsesTheTempDirectoryItself_OrPutsTheConfigDatabaseInIt` ("PermissionValidatorTests.cs:667 names the temp directory itself"), and the 19 cases built with no servicer (the helper) |

Mutant 4 is byte for byte aud-a28t's committed `Services/PermissionValidator.cs`: the log line is this commit's only change
to that file. E5 is the shape a pattern for a direct call would miss; the helper and the scan each catch it. The code
comments are written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; 61 warnings, all pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,648 passed / 3 skipped (the three known AD skips) / 0 failed, 5,651 total, 5m11s. No hang. (aud-a28t's tree: 5,645 /
  3 / 0, 5,648; the three added are this commit's. The helper's refusal of the temp directory broke no other test.)
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No PowerShell source, AD module, directory, Exchange Online, Delinea, SMTP, push or deployment. No coder self-review
verdict is claimed.

## Known gaps

- The per-validator directories under the temp directory are not deleted after the run, as before; nothing reads one
  after its test.
- `CreateValidator`'s `Audit:LogRoot` is still one directory under the temp directory shared by every validator
  (`eaw-test-logs`): a log directory, not a database, and not this finding.
- The scan looks for `GetTempPath()`. A test reaching the temp directory another way (the TEMP variable, a temp file's
  directory) and handing it to something other than `TestConfigStore` is not caught.
- `docs/history/state-archive.md` also names the two retired methods; it is the state archive, left as written.
