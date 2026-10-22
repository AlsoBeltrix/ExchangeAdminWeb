# aud-a26q: Validator tests share the real %TEMP% database, a log line still names a config file, and two documents name a retired method

**Severity**: LOW - test hygiene and stale text. (1) `PermissionValidatorTests.CreateValidator` puts
its protected-principal, enablement and section-access stores in the shared `%TEMP%\exchangeadmin.db`;
if any test ever writes central protected-principal config there, every validator test silently
changes meaning. (2) The validator's corrupt-branch log text still says "Module config file is
corrupt ... until file is fixed", though there has been no file since the SQLite move (the aud-a26f/a26h
class). (3) `.agents/decisions.md` (2026-07-28) and `docs/RetireExcludedUsersAppsettingsFallback-Plan.md`
still name `GetConfiguredExclusions`, renamed by aud-a28s; historical text, but a pointer to the
rename keeps a reader from searching for a method that no longer exists.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
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

## Guard proof

Pending.
