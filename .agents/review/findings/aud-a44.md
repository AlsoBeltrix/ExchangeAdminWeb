# aud-a44: The pool's borrow reads enablement through the fail-open IsModuleEnabled, and the pages' self-grant audit-failure banner says "self-grant" for an incomplete check and shows a raw exception

**Severity**: LOW. (1) `ExoConnectionPool.BorrowAsync` reads enablement through `IsModuleEnabled`, so an
unreadable enablement store refuses with "Exchange Online module is not enabled..." - fail-closed but
misleading (aud-a28t added `TryGetModuleEnabled` for exactly this). (2) In `MailboxPermissions.razor` and
`CalendarPermissions.razor`, when the self-grant audit write throws, the banner reads "Self-grant blocked,
but audit logging failed: {ex.Message}": it says self-grant for a check that could not be completed
(aud-a28y) and shows a raw exception message; since aud-a28q the writers do not throw, so the branch is
nearly dead, but the text is wrong where it can run.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
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

Pending.
