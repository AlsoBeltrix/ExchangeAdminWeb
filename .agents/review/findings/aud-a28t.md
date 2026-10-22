# aud-a28t: Two more fail-open reads on the permission gate - group expansion's IsConfigured and the self-grant setting

**Severity**: MEDIUM - Known Failure Class 3 on the shared permission gate. (1)
`PermissionValidator.TryExpandGroupAsync` checks `ExoConnectionPool.IsConfigured`, which reads the
Exchange Online config through a read that treats failure as "not configured"; if only that read
fails while the resolver's later read succeeds, a listed protected GROUP stays name-only and its
members are unprotected for the 30-minute cache. (2) `GetPreventSelfGrant` reads its setting with the
same fail-open read and, on failure, falls back to appsettings `Security:PreventSelfGrant`; the
Constitution allows that fallback only when the module config is ABSENT, so a host whose appsettings
says false allows self-grants while the store is unreadable.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
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

## Guard proof

Pending.
