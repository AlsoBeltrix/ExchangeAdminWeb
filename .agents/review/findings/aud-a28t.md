# aud-a28t: Two more fail-open reads on the permission gate - group expansion's IsConfigured and the self-grant setting

**Severity**: MEDIUM - Known Failure Class 3 on the shared permission gate. (1)
`PermissionValidator.TryExpandGroupAsync` checks `ExoConnectionPool.IsConfigured`, which reads the
Exchange Online config through a read that treats failure as "not configured"; if only that read
fails while the resolver's later read succeeds, a listed protected GROUP stays name-only and its
members are unprotected for the 30-minute cache. (2) `GetPreventSelfGrant` reads its setting with the
same fail-open read and, on failure, falls back to appsettings `Security:PreventSelfGrant`; the
Constitution allows that fallback only when the module config is ABSENT, so a host whose appsettings
says false allows self-grants while the store is unreadable.
**Status**: Fixed on its branch, not merged; guard proof below; approved under the owner's 2026-10-21 "EVERYTHING" go
**Branch**: `worktree-agent-acba5cd6b0762c5bf`
**Commit**: plan `b18df623`; the fix and comments commits follow it on the branch
**Reviewer**: implementer (surfaced by the aud-a28s agent, 2026-10-22, candidates 1 and 2 of its hand-back)

## Evidence

At `0bcb92c0`: `Services/PermissionValidator.cs` `TryExpandGroupAsync` (around 728, `_exoPool.IsConfigured`)
and `GetPreventSelfGrant` (around 74-81); `Services/ExoConnectionPool.cs` `IsConfigured` (the
`GetValue`-based read); `docs/ProjectConstitution.md` on the appsettings fallback rule;
`docs/RetireExcludedUsersAppsettingsFallback-Plan.md` for the precedent.

## Approach (to be written by the implementing agent before code)

- `IsConfigured` distinguishes unreadable from unconfigured (`TryGetModuleConfig`); the group
  expansion treats unreadable as a failed load (`_initFailed`, every validation refuses), never as
  "names only".
- `GetPreventSelfGrant`: unreadable store means the self-grant gate refuses (fail closed); the
  appsettings fallback applies only when the module config is readable and holds no value, per the
  Constitution; label the Constitution clause in the record.
- Tests through the aud-a28s failing-store fixture: a store failing only on the Exchange Online
  read refuses the validation; a store failing on the self-grant read refuses a self-grant even with
  appsettings false; a readable store with no value still falls back. Mutations at the call site:
  `IsConfigured` fail-open again; the fallback on failure again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21); admitted at `0262d744`. One
fix commit `fix(permission-validator): ... (aud-a28t)` on `worktree-agent-acba5cd6b0762c5bf` (forked from master
`5d93680b`), before aud-a26q's on the same branch; one comments-only commit after both. Behaviour: the shared
`PermissionValidator` that Mailbox Permissions, Calendar Permissions and Out of Office reach, `ExoConnectionPool` and
`ModuleEnablementService` (base app version); no module version moves; the supervisor owns the bump.

Evidence re-read at `5d93680b`:
- `PermissionValidator.TryExpandGroupAsync` 736-745: `if (!_exoPool.IsConfigured)` keeps the entry a literal ("Exchange
  Online not configured") and `EnsureInitializedAsync` 627-734 completes the load without the group's members.
- `ExoConnectionPool.IsConfigured` 153-162 reads `_enablement.IsModuleEnabled("ExchangeOnline")`
  (`ModuleEnablementService.IsModuleEnabled` 91-102 -> `ReadState` 257-270: an unreadable store is every module disabled),
  then `GetExoConfig` 164-180: three `ModuleConfigService.GetValue` reads (60-64 -> `ReadModuleConfig` 260-272: a failed
  read is an empty config, so null), the appsettings fallback for each null, and the `HasModuleConfigFile && IsModuleCorrupt`
  probe at 170 (a probe whose second read fails returns blanks). A failure of the enablement read, of a value read (no
  `ExchangeOnline:*` in appsettings) or of the probe's second read is "not configured"; only the presence read
  (`ModuleConfigRepository.HasModule`, which throws) fails the load. The real resolver's own `IsConfigured`
  (`ExchangeIdentityResolver.ResolveRecipientAsync` 47) refuses an Exchange Online that is not configured, so a name-only
  group survives a load exactly when the two reads disagree - the confined failure - and its members are unprotected for
  the cache lifetime.
- `GetPreventSelfGrant` 80-86 reads `GetValue("MailboxPermissions", "PreventSelfGrant")` (fail-open: a failed read is
  null) and for null, blank or an unparseable value returns appsettings `Security:PreventSelfGrant` (default true);
  `ValidateSelfGrant` 554 and `ValidateSelfGrantAsync` 568 return null without comparing when it is false. A host whose
  appsettings says false allows self-grants while the store is unreadable.
- `docs/ProjectConstitution.md`, Configuration: "If a config store is corrupt or unreadable, fail closed. Do not silently
  fall back to stale defaults." and "Legacy appsettings fallback is allowed only for upgrade compatibility and only when
  the module-specific config is absent." Never Do: "Do not make corrupt config silently fall back to defaults."

1. **Configured, or unreadable.** New `ExoConnectionPool.TryGetIsConfigured(out bool configured)` beside `IsConfigured`,
   which stays as it is for its other callers (the resolver, the recipient autocomplete; `GetExoConfig` and `BorrowAsync`
   untouched): false when Exchange Online's enablement or its module config cannot be read; otherwise `configured` is
   `IsConfigured`'s rule - enabled, AppId and Organization non-blank, a key absent from the module config falling back to
   `ExchangeOnline:*` in appsettings as `GetExoConfig` does. One read of each: the module config through
   `ModuleConfigService.TryGetModuleConfig` 75-76 (aud-a26k); the enablement through new
   `ModuleEnablementService.TryGetModuleEnabled(moduleId, out bool enabled)` - `IsModuleEnabled`'s rule (unknown off,
   system on, a disabled parent disables the child, else the stored toggle or the default) over one
   `ModuleEnablementRepository.TryGetAll` read, false when it cannot be read or a legacy file keeps the store fail-closed.
   The enablement half is in scope because `ReadState` answers an unreadable store as "disabled", which at this call site
   is the same "names only" (a file beyond the finding's two; reported in the hand-back).
2. **The expansion.** `TryExpandGroupAsync` asks `TryGetIsConfigured`: false throws, so the load takes the existing catch
   (720-729: `_initFailed`, logged, retried after `FailedLoadRetry`) and every validation refuses ("Protected-user list is
   unavailable"); configured false keeps the entry a literal as today; true expands as today.
3. **The self-grant setting.** `GetPreventSelfGrant` reads the MailboxPermissions rows once through `TryGetModuleConfig`:
   unreadable -> true, so self-grants are refused (logged with `SettingsStoreText.ModuleConfig`); a value that parses ->
   that value; no value (absent or blank) -> appsettings `Security:PreventSelfGrant`, default true, as today; a value that
   does not parse -> true (logged): the module config is present, so the fallback is not allowed, and refusing is the
   fail-closed reading. A grant to someone else is not this gate's to refuse and is unchanged.
4. **Registries.** No page, handler or markup changes: ClickGateRegistry and ProgressRegistry untouched.

Tests, new `ExchangeAdminWeb.Tests/PermissionValidatorConfigReadTests.cs`: a real validator whose module config and
Exchange pool share one `ModuleConfigService`, as in production, over `ExchangeWriteHarness`'s database file (Exchange
Online enabled and configured) through `FailsAfterReadsStore` (aud-a28p/a28s's fixture, copied per class, plus an optional
number of failing reads after which it answers again); the enablement on its own store over the same file; the
protected-principal and section-access stores empty; a stand-in resolver; a pool connect seam that counts and throws, so
no session can open. Cases: (a) ExcludedUsers names a group, the store answers the exclusions read and fails the Exchange
Online read - from then on, and that one read only - and a member of the group is refused as the list unavailable,
`IsUserExcludedAsync` true, nothing connected; (b) the enablement store unreadable: the same refusal; (c) control: readable
stores with Exchange Online not configured (AppId blank, or the module disabled) keep the entry a literal - the member
allowed, bound to its mailbox, the listed name refused as protected; (d) the module config unreadable refuses a self-grant,
sync and async, with appsettings false; (e) over a readable store: no value or blank -> appsettings (false allows, absent
refuses), a stored value wins over appsettings either way, an unparseable value refuses with appsettings false. Mutations
at the call site: (1) `TryExpandGroupAsync` asks `IsConfigured` again; (2) `GetPreventSelfGrant` reads through `GetValue`
and falls back on a failed read again; extras (E1) the call site takes unreadable for not configured, (E2)
`TryGetIsConfigured` reads the enablement through `IsModuleEnabled`, (E3) an unparseable value falls back again. Plus an
executed reproduction with the three production files as at `5d93680b`.

## Executed reproduction - 2026-10-22

Branch `worktree-agent-acba5cd6b0762c5bf`, the fix's tests in place and `Services/PermissionValidator.cs`,
`Services/ExoConnectionPool.cs` and `Services/ModuleEnablementService.cs` as they are at `5d93680b` (`git hash-object` equal
to the blobs `cf2761b3`, `f8d76648`, `ce7c4a97`; MD5 `56C5F0A7A6D7003078AF1031CD8EBDFD`, `961683F4BCC31E44D593BAAA02C8CFD2`,
`73F054CA4FCD54FCDD51E692F53E5C96`), build 0 errors: `PermissionValidatorConfigReadTests` 4 failed / 8 passed. A member of a
listed group was allowed (`Error` null) when the store failed only the Exchange Online read and answered again - the first
value read failed open to null, the probe after it answered, and `IsConfigured` said "not configured", so the group stayed
a name - and allowed again with the enablement store unreadable (read as disabled). A self-grant was allowed with the module
config unreadable and appsettings false (the failed read fell back), and with the value `yes` and appsettings false. Passed,
as they must against the old code too: the store failing from the Exchange Online read on (the old chain's presence read
throws there and fails the load), both not-configured controls, and the five readable self-grant rows. The fixed files were
then restored to their MD5s and touched.

## Fix

`Services/ModuleEnablementService.cs`: `TryGetModuleEnabled(moduleId, out bool enabled)` - `IsModuleEnabled`'s rule over one
`ModuleEnablementRepository.TryGetAll` read (`IsModuleEnabledIn`); a module the catalog decides alone (unknown, system) is
answered without a read; false when the store cannot be read or a legacy file keeps it fail-closed.
`Services/ExoConnectionPool.cs`: `TryGetIsConfigured(out bool configured)` beside `IsConfigured` - the enablement through
`TryGetModuleEnabled`, the module config through `ModuleConfigService.TryGetModuleConfig`, false when either cannot be read;
otherwise `IsConfigured`'s rule, the appsettings fallback only for a key absent from the module config. `IsConfigured`,
`GetExoConfig` and `BorrowAsync` are unchanged.
`Services/PermissionValidator.cs`: `TryExpandGroupAsync` throws when `TryGetIsConfigured` is false, so the load fails
closed through the existing catch; not configured keeps the entry a literal as before. `GetPreventSelfGrant` reads the
MailboxPermissions rows once through `TryGetModuleConfig`: unreadable refuses self-grants (logged with
`SettingsStoreText.ModuleConfig`); a value that parses is used; absent or blank falls back to appsettings
`Security:PreventSelfGrant`, default true; a value that does not parse refuses (logged).

Tests: new `ExchangeAdminWeb.Tests/PermissionValidatorConfigReadTests.cs` (12 cases), as planned: the store failing the
Exchange Online read from then on, and that read only (refused as the list unavailable, `IsUserExcludedAsync` true, nothing
connected); the enablement store unreadable (the same); the readable not-configured controls, AppId blank and module
disabled (the member allowed and bound to its mailbox, the listed name refused as protected); the module config unreadable
refusing a self-grant, sync and async, with appsettings false; the six-row self-grant table over a readable store. No
departure from the plan.

Caller-visible change, by design: none while the stores read. When Exchange Online's enablement or module config cannot be
read during a load that lists an entry, the load fails closed and every Mailbox Permissions, Calendar Permissions and Out of
Office target is refused as the protected-user list unavailable, retried after `FailedLoadRetry` - where a listed group used
to stay a name with its members unprotected for the cache lifetime. A self-grant is refused while the Mailbox Permissions
module config cannot be read, or while its `PreventSelfGrant` holds a value that is neither true nor false, whatever
appsettings says; a grant to someone else is unaffected.

## Guard proof

Session-local driver (`mutate.ps1` in the scratchpad's `a28t-a26q-work`): back up, mutate by exact text (one match
required), MD5, `dotnet build` of the test project, `dotnet test` filtered to
`FullyQualifiedName~PermissionValidator|FullyQualifiedName~WriteIdentityTests|FullyQualifiedName~OutOfOfficeRequestCaptureTests|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission|FullyQualifiedName~ProtectedPrincipalExclusionsReadTests|FullyQualifiedName~ExoConnectionPool|FullyQualifiedName~ModuleEnablement|FullyQualifiedName~ExchangeIdentityResolver|FullyQualifiedName~SharedTempDatabaseTripwireTests`
(345 tests), restore from the backup, set the write time, MD5 re-confirmed. Fixed-file MD5s: `Services/PermissionValidator.cs`
`CD5E1DBCC34698BD1D444658550B3F85`, `Services/ExoConnectionPool.cs` `BF64A1F4FC5565B82AB6F058AC8F4E6C`; every mutant built
with 0 errors and its file was restored to that MD5 each time. After the round, the restored tree rebuilt and the same
filter: 345/345 green.

| # | Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|---|
| 1 | `TryExpandGroupAsync`: asks `IsConfigured` again - the read that is "not configured" on a failure | `6AB2003495A4CB2707B6EFFDF0391DBB` | `AStoreThatFailsTheExchangeOnlineRead_RefusesEveryValidation_NotNamesOnly(failing: 1)` (the member allowed: `Error` null), `AnEnablementStoreThatCannotBeRead_RefusesEveryValidation_NotNamesOnly` (`Error` null) |
| 2 | `GetPreventSelfGrant`: the value read through `GetValue` again, so a failed read falls back to appsettings | `D94E0DA1E856F7CB0865EFB17445E255` | `AModuleConfigThatCannotBeRead_RefusesASelfGrant_EvenWithAppsettingsFalse` (the self-grant allowed: null) |
| E1 | extra, `TryExpandGroupAsync`: unreadable taken for not configured (`_ = TryGetIsConfigured(...)`, names only) | `A6AB7351EBF02AF342EBE1CDFC8F19B0` | both `AStoreThatFailsTheExchangeOnlineRead_RefusesEveryValidation_NotNamesOnly` rows, `AnEnablementStoreThatCannotBeRead_RefusesEveryValidation_NotNamesOnly` |
| E2 | extra, `ExoConnectionPool.TryGetIsConfigured`: the enablement read through `IsModuleEnabled` again | `F8F5EE8831F2414913ECBF7D82D9B367` | `AnEnablementStoreThatCannotBeRead_RefusesEveryValidation_NotNamesOnly` (`Error` null) |
| E3 | extra, `GetPreventSelfGrant`: a value that does not parse falls back to appsettings again | `93085BA2D62946F1C9E492DF0D0DD09C` | `TheSelfGrantSetting_FallsBackToAppsettings_OnlyWhenTheModuleConfigHoldsNoValue(stored: "yes", appsettings: "false", refused: True)` (null) |

Mutation 1 is the finding's own code, and only the confined failure catches it: from the Exchange Online read on, the old
chain's presence read throws and fails the load, which is why the `failing: int.MaxValue` row passes under it and the
`failing: 1` row is in the class. The code comments are written after the round, in their own commit, so the MD5s above are
this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; 61 warnings, all pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,645 passed / 3 skipped (the three known AD skips) / 0 failed, 5,648 total, 5m14s. No hang. (Baseline at
  `5d93680b`: 5,633 / 3 / 0, 5,636, 5m44s; the twelve added are this class's.)
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No PowerShell source, AD module, directory, Exchange Online, Delinea, SMTP, push or deployment: Exchange is a stand-in
resolver and a connect seam that throws, and no case here opens a session. No coder self-review verdict is claimed.

## Known gaps

- `IsConfigured` and `GetExoConfig` keep their fail-open reads for their other callers. `BorrowAsync`: a failed value read
  is null and falls back to appsettings `ExchangeOnline:*`, so with the store answering again a borrow can connect under the
  appsettings values rather than the module config's. `ExchangeIdentityResolver.ResolveRecipientAsync` refuses on "not
  configured", so it fails closed; the recipient autocomplete shows nothing. Not this gate; handed back.
- The expansion asks once per entry, as before, so two entries of one load can see different answers (one configured, one
  not), as before.
- No case drives a real expansion - a listed group's members read from Exchange and protected - because that needs a
  session and no case here opens one. The refusal is what is tested.
- The failing stores stand in for a store fault; nothing ran against a damaged database.
