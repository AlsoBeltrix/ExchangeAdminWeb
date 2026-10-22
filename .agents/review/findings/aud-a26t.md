# aud-a26t: Validator log wording and test hygiene - the shared remedy text ends "then reload this page" inside a service log line, and the validator tests share one audit log root and never delete their directories

**Severity**: LOW. (1) `SettingsStoreText.DatabaseRemedy` ends "then reload this page"; aud-a26q reused it
in `PermissionValidator`'s corrupt-branch log, where no page is involved. (2) `CreateValidator`'s
`Audit:LogRoot` is still one shared directory under the temp directory, and the per-validator test
directories aud-a26q introduced are never deleted (pre-existing shape; not a database, so the
aud-a26q tripwire does not cover it).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
