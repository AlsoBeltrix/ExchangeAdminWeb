# aud-a44: The pool's borrow reads enablement through the fail-open IsModuleEnabled, and the pages' self-grant audit-failure banner says "self-grant" for an incomplete check and shows a raw exception

**Severity**: LOW. (1) `ExoConnectionPool.BorrowAsync` reads enablement through `IsModuleEnabled`, so an
unreadable enablement store refuses with "Exchange Online module is not enabled..." - fail-closed but
misleading (aud-a28t added `TryGetModuleEnabled` for exactly this). (2) In `MailboxPermissions.razor` and
`CalendarPermissions.razor`, when the self-grant audit write throws, the banner reads "Self-grant blocked,
but audit logging failed: {ex.Message}": it says self-grant for a check that could not be completed
(aud-a28y) and shows a raw exception message; since aud-a28q the writers do not throw, so the branch is
nearly dead, but the text is wrong where it can run.
**Status**: Merged `fce22146`; closed on guard proof; codex review pending
**Branch**: `worktree-agent-ac928f0732402c4c5`
**Commit**: `8a90e8fa` (fix, tests, record); plan `1272a8cf`; comments `bce8d047`; after aud-a43 `4cc5756b`
**Reviewer**: implementer (surfaced by the aud-a28y/a28x/a26t agent, 2026-10-22, candidates 5 and 6 of its hand-back)

## Evidence

At `448b02c8`: `Services/ExoConnectionPool.cs` around 249; `Components/Pages/MailboxPermissions.razor` 450
and `Components/Pages/CalendarPermissions.razor` 420 (both line-keyed by `ClickGateRegistry`, 808 and 762
lines, edit in place only).

## Approach (to be written by the implementing agent before code)

- `BorrowAsync` reads enablement through `TryGetModuleEnabled`; unreadable refuses with "the Exchange
  Online enablement could not be read: <rows and remedy>" (the aud-a28x message shape), disabled keeps its
  text.
- The banner uses the validator's own refusal text and "the audit row could not be written" without the
  raw message (the aud-a28u outcome tells it whether the row was written).
- Tests through the pool's config-read tests and the pages' reflection driver; mutations at the call site:
  `IsModuleEnabled` again; the raw message again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21). Branch
`worktree-agent-ac928f0732402c4c5` from master `1410013e`: one fix commit `fix(exo-pool,permission-pages): ...
(aud-a44)`, second of two, after aud-a43 (both touch the two pages), then the comments-only commit. Behaviour: the shared
`ExoConnectionPool` (base app version) and the two pages' banner (both module versions); the supervisor owns the bumps.

Evidence re-read at `1410013e`:
- `ExoConnectionPool.BorrowAsync` 249-250: `_enablement.IsModuleEnabled("ExchangeOnline")`, which reads an unreadable
  store as every module disabled (`ModuleEnablementService.IsModuleEnabled` 91-102 -> `IsModuleRawEnabled` 155-168), so
  the borrow says "Exchange Online module is not enabled" while the store cannot be read. `TryGetModuleEnabled` 115-132
  (aud-a28t) tells the two apart; `TryGetIsConfigured` 180-192 already asks it. `LegacyImportFailed` 207 says which fix
  applies.
- `SettingsStoreText.Enablement(bool)` names the table (or the legacy file) and its fix, with the page's reload clause
  for the table; a service's message carries `SettingsStoreText.ServiceDatabaseRemedy` instead (aud-a26t). The file is
  another agent's, so no service variant is added there (follow-up below).
- `MailboxPermissions.razor` 448-452 / `CalendarPermissions.razor` 418-422: when the self-grant record throws, the banner
  is "Self-grant blocked, but audit logging failed: {ex.Message}" - "self-grant" for what may be an incomplete check
  (aud-a28y) and the raw exception text. The writers do not throw since aud-a28q; the branch runs only for a throw before
  the write (aud-a28q's known gap) or a missing writer.
- `AuditService.LogMailboxPermission` 47-78 / `LogCalendarPermission` 80-109 return nothing; the aud-a28u outcome
  (`WriteAuditEvent` 488) stays inside the service, so the pages cannot learn whether the row was written - AuditService is
  another agent's file (follow-up below).

1. **The borrow.** `BorrowAsync` asks `TryGetModuleEnabled(ConfigModuleKey, ...)`: unreadable -> one error line and
   `ExoConfigUnreadableException("The Exchange Online enablement could not be read: " + <the store and its fix>)`, the
   aud-a28x shape, before a slot is asked for; the fix is `SettingsStoreText.Enablement(legacy)` with the page's reload
   clause replaced by `ServiceDatabaseRemedy` (the legacy-file text has none). Readably disabled keeps its text.
2. **The banner.** The catch logs the audit failure with its exception (`Logger.LogWarning`, as the protected-target
   denial's catch does) and the banner is the validator's own refusal followed by "The audit row could not be written." -
   no exception text. Same-line edits: 808 and 762 lines, no registry pin moves.

Tests. `ExoConnectionPoolConfigReadTests` gains a pool over an enablement store that cannot be read: the borrow throws
`ExoConfigUnreadableException` naming the enablement and the service fix (no reload clause), nothing connected, one error
logged; the control, Exchange Online readably disabled, keeps "Exchange Online module is not enabled". In
`PermissionPagesSelfGrantTests` (aud-a43's driver), each page's `SubmitSingle` with an audit service whose writer is
missing (the record throws): the banner is the validator's refusal plus "The audit row could not be written.", carries
no exception text and does not say "Self-grant blocked"; one warning logged with the exception.

Mutations at the call site: M1 `BorrowAsync` reads `IsModuleEnabled` again; M2 / M3 the raw exception text back in the
Mailbox Permissions / Calendar Permissions banner.

Files: `Services/ExoConnectionPool.cs`, `Components/Pages/MailboxPermissions.razor` (450-451),
`Components/Pages/CalendarPermissions.razor` (420-421), `ExchangeAdminWeb.Tests/ExoConnectionPoolConfigReadTests.cs`,
`ExchangeAdminWeb.Tests/PermissionPagesSelfGrantTests.cs`, this record.

## Fix

`Services/ExoConnectionPool.cs` `BorrowAsync`: `_enablement.TryGetModuleEnabled(ConfigModuleKey, out var enabled)`; false ->
one error line and `ExoConfigUnreadableException("The Exchange Online enablement could not be read: " + UnreadableEnablement)`,
before the rows are read and before a slot is asked for; readably disabled keeps "Exchange Online module is not
enabled...". New `UnreadableEnablement`: `SettingsStoreText.Enablement(legacyImportFailed: true)` when the legacy file kept the
store closed (`LegacyImportFailed`), else `SettingsStoreText.Enablement(false)` with `DatabaseRemedy` replaced by
`ServiceDatabaseRemedy` - the table and its fix in the service's words.

`Components/Pages/MailboxPermissions.razor` 450-451 and `Components/Pages/CalendarPermissions.razor` 420-421, the catch of the
self-grant refusal's record: `Logger.LogWarning(ex, "Failed to write audit for the self-grant refusal on {Target}", target);`
and `selfGrantError += " The audit row could not be written.";` - the handler falls through to its existing
`result = PermissionResult.Fail(selfGrantError); return;`. Same-line edits: 808 and 762 lines; the registries untouched.

Tests: `ExoConnectionPoolConfigReadTests` +3 - `AnEnablementThatCannotBeRead_RefusesTheBorrow_SayingSo_NotThatTheModuleIsDisabled`
(the table unreadable; the legacy file unparseable) and `ExchangeOnlineReadablyDisabled_StillSaysTheModuleIsNotEnabled` (the
control, the exact type and text); `PoolOver` takes an optional enablement store. `PermissionPagesSelfGrantTests` +4 - each page's
banner for a self-grant and for a check that could not be completed, with an audit service whose writer is missing (the record
throws): the validator's refusal plus "The audit row could not be written.", no exception text, no "Self-grant blocked", one
warning with the exception, nothing written.

Caller-visible change, by design: while Exchange Online's enablement cannot be read, every borrow - so every Exchange Online
read and write - is refused with "The Exchange Online enablement could not be read: " and the store and its fix, where it said
the module was not enabled; logged once per borrow. When the self-grant refusal's record throws, the Mailbox Permissions and
Calendar Permissions banner is the refusal the validator gave followed by "The audit row could not be written.", and the
exception goes to the page's log; it said "Self-grant blocked, but audit logging failed: <exception text>".

## Left for a follow-up (file owned by another agent)

- `Services/AuditService.cs` 47-78 and 80-109 (`LogMailboxPermission`, `LogCalendarPermission`) and 463-517
  (`WriteAuditEvent`) return `void`. For the pages to say whether the self-grant refusal's row was written - the aud-a28u
  outcome the Approach names - they would return the `JsonlWriteOutcome` `WriteAuditEvent` already computes at 488, and the
  pages' self-grant branches (`MailboxPermissions.razor` 443-446, `CalendarPermissions.razor` 413-416) would add "The audit
  row could not be written." when it is not `Written`, not only when the call throws.
- `Services/SettingsStoreText.cs` 30-32: `Enablement(false)` ends in the page's reload clause (`DatabaseRemedy`); a
  `ServiceEnablement(bool)` beside `ServiceModuleConfig` (43) would let the pool name the store without replacing that
  clause itself.

## Guard proof

No separate pre-fix reproduction: M1 is the borrow's enablement read as it stood, and M2 / M3 each page's catch as it stood,
each put back at its call site.

The aud-a43 driver (`mutate.ps1`, session scratchpad `a43`, log `a44-mutations.log`): the fix staged; per mutant: MD5, one
exact-text replacement that must match exactly once, MD5, a Debug build of the test project with 0 errors required,
`dotnet test --no-build` filtered to
`FullyQualifiedName~ExoConnection|FullyQualifiedName~ExoConfigFallback|FullyQualifiedName~ExchangeIdentityResolver|FullyQualifiedName~PermissionValidator|FullyQualifiedName~ExchangeServiceBase|FullyQualifiedName~RecipientLookup|FullyQualifiedName~DelegationReport|FullyQualifiedName~Migration|FullyQualifiedName~OutOfOffice|FullyQualifiedName~WriteIdentityTests|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission|FullyQualifiedName~Settings|FullyQualifiedName~ModuleEnablement|FullyQualifiedName~ProtectedPrincipalExclusionsReadTests|FullyQualifiedName~SharedTempDatabaseTripwireTests|FullyQualifiedName~Comms10k|FullyQualifiedName~ConferenceRoom|FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistry`
(2,413 tests: every class that builds a pool, the two pages and their registries), the file restored from the git index, its
write time advanced, its MD5 re-checked. Fixed-file MD5s: `Services/ExoConnectionPool.cs` `B3497579FE1B24376CA8A1BD8B68BBEA`,
`Components/Pages/MailboxPermissions.razor` `7B2C771C3B9ACD5E398F18F7B20E4060`, `Components/Pages/CalendarPermissions.razor`
`1F953D7A79E5775A508B8B4C506140B9`. Every mutant built with 0 errors and its file was restored to that MD5 each time; each page
mutant kept its page's line count, so ClickGateRegistry passed under it and only the named tests failed. After the round, the
restored tree rebuilt and the same filter: 2,413/2,413 green.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 | `BorrowAsync`: the enablement read through `IsModuleEnabled("ExchangeOnline")` again (the old two lines) | `F633D38CE2F27909414D65C3132E72B7` | both `AnEnablementThatCannotBeRead_RefusesTheBorrow_SayingSo_NotThatTheModuleIsDisabled` rows - "not enabled", the wrong type |
| M2 | `MailboxPermissions.razor` 450-451: the old catch back ("Self-grant blocked, but audit logging failed: {ex.Message}"; return) | `AE9DDD598A4EE7CAC3A209789237035F` | both `TheMailboxPermissionsBanner_WhenTheRefusalsRecordCannotBeWritten_IsTheValidatorsRefusal_WithoutTheException` rows |
| M3 | `CalendarPermissions.razor` 420-421: the same | `3CA1998D2D047AF1AD3DB0040038016E` | both `TheCalendarPermissionsBanner_WhenTheRefusalsRecordCannotBeWritten_IsTheValidatorsRefusal_WithoutTheException` rows |
| E1 | extra, `MailboxPermissions.razor`: only the raw exception text appended to the new banner | `E7BE112841953E8322827209F6FE1A4A` | both Mailbox Permissions banner rows |
| E2 | extra, `UnreadableEnablement`: the legacy-file text never chosen (`=> false ? ...`) | `5A327C3C03182D7D74AF4E682D55C72D` | `AnEnablementThatCannotBeRead_RefusesTheBorrow_SayingSo_NotThatTheModuleIsDisabled(legacyFile: True)` |
| E3 | extra, `UnreadableEnablement`: the page's reload clause kept (the `Replace` removed) | `4BCEB7E0F30E46951A041C7808885E2C` | `AnEnablementThatCannotBeRead_RefusesTheBorrow_SayingSo_NotThatTheModuleIsDisabled(legacyFile: False)` |

The code comments are written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors; 59 warnings, the baseline, none in a touched
  file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug): exit 0, 6,078 passed / 3 skipped (the
  three known AD skips) / 0 failed, 6,081 total, 5m15s; nothing hung, no Sequence file. The seven cases over aud-a43's tree
  are this commit's.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0. The pages: 808 and 762 lines.

No AD, Exchange Online, Graph, DHCP or IIS call: the pool's connect seam throws, the pages run over the harness and its
stand-ins. No coder self-review verdict is claimed.

## Comments after the round

In the branch's comments commit `bce8d047`, checked comment-only as aud-a43 records: `Services/ExoConnectionPool.cs` gains 8
comment lines (the exception's summary, `UnreadableEnablement`, the borrow's enablement read); its code-line MD5
`B0C1AA9E0BAAB10078381FAB387ACB21` is equal before and after. The pages are untouched by it: their two lines carry no comment.

## Branch-tip gates - 2026-10-22 (`bce8d047`: both fixes and the comments)

As aud-a43 records them: Release build 0 errors, 59 warnings (baseline, none in a touched file); `dotnet test` 6,078 passed /
3 skipped / 0 failed, 6,081 total, 5m11s; `ClickGate*` 316/316 and `ProgressRegistry*` 35/35 alone; format, `git diff
--check HEAD` and the ASCII lint exit 0; the pages 808 and 762 lines.

## Closeout

Fixed on branch `worktree-agent-ac928f0732402c4c5`, not merged, not pushed. Due at merge: the base app version (the shared
`ExoConnectionPool`), and the Mailbox Permissions and Calendar Permissions module versions (their banner). The bumps are the
supervisor's.

## Known gaps

- Whether the self-grant refusal's row was written is still known to the pages only through a throw: the writer's outcome
  (aud-a28u) does not reach them - the follow-up below.
- A store-fault double and a corrupt legacy file stand in for an unreadable enablement; nothing ran against a damaged
  database.
