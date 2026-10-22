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

## Guard proof

Pending.
