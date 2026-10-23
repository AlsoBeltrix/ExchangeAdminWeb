# aud-a50: Permission pages - the on-prem write is not re-checked for self-grant right before it writes; the stored PreventSelfGrant warning floods; blank-User grant rows are misrecorded; the pages cannot learn whether the refusal's audit row was written; no service wording for an unreadable enablement store

**Severity**: LOW/MEDIUM - the aud-a43/a44 leftovers. (1) `ExecuteOnPrem` in both pages re-runs only
`ValidateTargetMailboxAsync` immediately before the on-prem write; the self-grant check ran in `SubmitSingle`
before the operator's confirmation pause and is not repeated right before the write (a write-time re-check
is the aud-a2l/a26l pattern). (2) `PermissionValidator` warns on every validation for a STORED
`PreventSelfGrant` value that is neither true nor false (the aud-a43 once-guard covers only appsettings).
(3) The bulk loops have no blank-User check before the self-grant check, so a grant row with an empty User is
refused as "Exchange Online could not resolve ''" and recorded under `*_SelfGrantAttempt` rather than as an
invalid row. (4) `AuditService.LogMailboxPermission` / `LogCalendarPermission` return void, so the pages
cannot learn the aud-a28u write outcome; the banner says "The audit row could not be written." only when the
call throws. (5) `SettingsStoreText` has no `ServiceEnablement(bool)`; the pool rebuilds the service wording
by replacing the page clause.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a43/a44 agent, 2026-10-22, candidates 1-3 and its two "Left for a follow-up" items)

## Evidence

At `fce22146`: `Components/Pages/MailboxPermissions.razor` 693-695 and 708-709, 443-446;
`Components/Pages/CalendarPermissions.razor` 647-649 and 661-663, 413-416 (both line-keyed by
`ClickGateRegistry`, 808 and 762 lines, edit in place only); `Services/PermissionValidator.cs` around 116;
`Services/MailboxPermissionService.cs` 274-277 and `Services/CalendarPermissionService.cs` 162-165;
`Services/AuditService.cs` 47-109 and 463-517 (`WriteAuditEvent` computes the outcome at 488);
`Services/SettingsStoreText.cs` 30-32 and 43.

## Approach (to be written by the implementing agent before code)

- `ExecuteOnPrem` re-runs the self-grant check immediately before the write (same principal, same grantee);
  a refusal there is the same refusal record and banner as in `SubmitSingle`; nothing written.
- The stored-value warning is once per process per observed value, like the appsettings one.
- The bulk loops refuse a blank User as an invalid row before any check, recorded as such.
- The two audit methods return the `JsonlWriteOutcome` `WriteAuditEvent` already computes; the pages add
  "The audit row could not be written." when the outcome is not `Written`, not only on a throw.
- `SettingsStoreText.ServiceEnablement(bool)` beside `ServiceModuleConfig`; the pool uses it.
- Tests through `PermissionPagesSelfGrantTests`, the bulk loops' harness, `AuditWriterThrowFreeTests` and
  `StoreTextSurfaceTests`; mutations at the call site: the re-check removed; the stored-value once-guard
  removed; the blank-User check removed; the outcome ignored on one page; the pool's replace again.

## Guard proof

Pending.
