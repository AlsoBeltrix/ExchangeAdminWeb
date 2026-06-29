# BlockedSenders Plan

Status: Implemented
Owner: Michael
Last verified against code: commit cb00774 (2026-06-29)

<!-- Sections marked [YOU] are written or approved by Michael, in plain language.
     Sections marked [MODEL] are drafted by the model and only skimmed by Michael.
     This is a change ticket for source code. Treat it like one. -->

## 1. Goal  [YOU]

"I need a new module to view and unblock Blocked Senders in Exchange Online.
in powershell I use Get-BlockedSenderAddress and Remove-BlockedSenderAddress cmdlets."

A blocked sender is a Microsoft 365 account that the service has blocked from
sending mail because it sent messages classified as spam (often a compromised or
misconfigured account). Operators need to see the current list and clear the block
for a specific address.

## 2. Non-goals  [YOU]

- No on-premises path. Both cmdlets are cloud-only; there is no on-prem equivalent
  and no on-prem session code in this module.
- No bulk CSV unblock. Single-address unblock only (the EXO unblock has a per-account
  rate limit, so bulk would routinely hit Microsoft's unblock cap).
- No ServiceNow validation. The ticket field is plain audit metadata.
- No remediation of *why* the account was blocked (no password reset, no sign-in
  review). This module only clears the send-block flag.
- No protected-principal directory checks (see §6 — unblocking writes no directory
  principal).

## 3. Acceptance criteria  [YOU approve each; model may propose]

- AC1: An authorized operator can open the module and see the list of currently
  blocked senders (address, reason, and when blocked) read live from Exchange Online.
- AC2: An authorized operator can unblock one sender by address, with a required
  ticket number and an explicit confirmation step before the write.
- AC3: Every unblock attempt (success and failure) writes an audit record with
  actor, IP, action, target address, result, and ticket.
- AC3b: Every unblock attempt (success and failure) also sends an admin email
  notification (to the global admin-email recipient), matching the house pattern for
  mutating modules. A notification failure must not change the unblock result.
- AC4: The module is denied by direct URL when disabled, or when the operator is not
  in the section-access group for its policy (fail-closed).
- AC5: Authorization is re-checked immediately before the unblock write, not only on
  page load. The unblock write additionally requires the granular
  `BlockedSendersUnblock` policy; viewing the list requires only `BlockedSenders`.
- AC6: The page shows the module version next to its heading.

## 4. Failure behavior  [YOU own]

| Step / dependency | If it fails | The user sees | System state afterward |
|---|---|---|---|
| EXO not connected / pool borrow fails | Read or unblock cannot run | Red error banner with a safe message; no raw exception | No change; nothing blocked/unblocked |
| `Get-BlockedSenderAddress` errors | List cannot load | Red error banner; empty/last-known list not shown as success | No change |
| `Remove-BlockedSenderAddress` errors (incl. Microsoft unblock-limit cap) | Unblock did not happen | Red error banner with the operator-safe message | Sender still blocked; failure audited |
| Authorization revoked mid-session | Re-check before write fails | "Authorization denied." | No write; nothing audited as success |
| Audit write fails after a successful unblock | n/a (already unblocked) | Banner notes unblock succeeded but audit logging failed | Sender unblocked; logged separately |
| Admin email send fails | n/a (already unblocked) | No change to the result banner | Sender unblocked; email exception caught + logged, result unchanged |
| Global admin email not configured | n/a | No change | Notification skipped silently (EmailService self-skips); unblock + audit still happen |

## 5. Rollback / blast radius  [YOU own]

- New, self-contained, optional module, disabled by default. Adding it changes no
  existing module's behavior.
- Reverse by disabling it in Admin Settings (runtime, no deploy) or removing the
  descriptor + files and republishing.
- The only mutating action is `Remove-BlockedSenderAddress`, which *re-enables* a
  sender. It does not delete data. The compensating action (re-block) is automatic:
  if the account keeps sending spam, the service re-blocks it.
- Open versioning question (state.md Blockers, 2026-06-26): the committed rule bumps
  the base app version for a new module, but you flagged that as wrong pending the
  dynamic-load decision. This plan bumps the module's own `Version` (1.0.0) and
  defers the app-version question to you (§6).

## 6. Design sketch  [MODEL — Michael skims]

Reference module: **CalendarPermissions** (Exchange-backed via the shared EXO pool).
This module is a strict subset of it — cloud-only, no on-prem branch, no Delinea
secret, no bulk upload.

Components touched:

- `Modules/ModuleCatalog.cs` — add descriptor in `RegisterAll()` (draft in §6.1).
- `Components/Pages/BlockedSenders.razor` — new page: a refreshable table of blocked
  senders (View) + an Unblock action with ticket + confirmation. Page auth block and
  pre-write re-check copied verbatim from `CalendarPermissions.razor:237-296`. After the
  unblock returns, calls `EmailService.SendAdminNotificationAsync` inside a
  try/catch-and-log so a send failure never changes the result (AC3b).
- `Services/BlockedSenderService.cs` — new service, inherits `ExchangeServiceBase`.
  - `GetBlockedSendersAsync()` → `RunPooledQueryAsync(... allowRetry: true)` running
    `Get-BlockedSenderAddress` (read; safe to retry on a dead pooled session).
  - `UnblockSenderAsync(address, reason)` → `RunAsync(... allowRetry: true)` running
    `Remove-BlockedSenderAddress -SenderAddress <addr> [-Reason <r>]` (single write;
    `allowRetry` is safe per the base-class contract for single-write ops).
- `Models/BlockedSenders/*` — a small `BlockedSenderInfo` record (Address, Reason,
  BlockedDateUtc, raw detail) and reuse of `PermissionResult` for the write.
- `Components/Pages/BlockedSenders.razor` injects `EmailService` (already DI-registered)
  for the admin notification.
- `Program.cs` — `builder.Services.AddScoped<BlockedSenderService>();`
- `ExchangeAdminWeb.Tests/BlockedSendersTests.cs` — catalog + security tests (§8).
- `docs/BlockedSenders.md` — module doc.

Conformance to invariants (each verified against current code):

- **EXO auth ownership**: uses the shared `ExoConnectionPool` via `ExchangeServiceBase`
  (`Services/ExchangeServiceBase.cs:47,110`); `DependsOn = "ExchangeOnline"` so the
  parent's connection config gates it. No module credential — matches `DelegationReport`
  / `OutOfOffice` (`ModuleCatalog.cs:198-262`).
- **Fail-closed**: `MainPermission` is `FailClosed: true` (mutating module; required by
  `Catalog_MutatingModulePermissions_AreFailClosed`, `ModuleCatalogTests.cs:342`).
- **Authorization**: page-load check + immediate pre-write re-check, both via
  `IAuthorizationService` against the main policy (pattern at
  `CalendarPermissions.razor:244,283-296`).
- **Ticket**: required `Ticket Number` input bound with `@bind:event="oninput"`,
  captured into a local before the write, included in audit. Plain metadata, no
  ServiceNow (Constitution §External Integrations).
- **Audit**: `AuditService.LogModuleAction(...)` for success, failure, and denied
  paths, category `BlockedSenders` (generic API at guide lines 730-740).
- **Admin notification**: `EmailService.SendAdminNotificationAsync` (the generic
  `details`-dictionary overload at `Services/EmailService.cs:105`) on success and
  failure — Sender Address + Reason carried in the details dict. Recipient is the
  global admin-email setting (`_adminEmail`, `EmailService.cs:48`); no per-module
  config field, and the service self-skips when unconfigured. Send wrapped in
  try/catch-and-log so it cannot mask the unblock result (guide Notifications,
  lines 837-847; house pattern — 12 of ~15 mutating modules notify).
- **Operation trace**: `OperationTraceService` scope around the unblock with steps
  for re-auth, backend write, complete (pattern at guide lines 805-827).
- **Protected principals — N/A**: `Remove-BlockedSenderAddress` clears a service-side
  spam-block flag keyed by SMTP address; it performs no write against a directory
  principal object (no AD/Graph object mutation). The protected-principal pattern in
  the guide (lines 630-643) governs identity writes bound to GUID/DN, which this is
  not. Recorded here as an explicit, deliberate non-application.
- **Module version display**: `<ModuleVersion />` in the page heading (required;
  enforced by `tools/validate-module-package.ps1`, Error PAGE009).
- **Icon**: reuse `bi bi-envelope-fill-nav-menu` (already in host CSS; used by
  MessageTrace) — to be confirmed present during build.

### 6.1 Draft descriptor

```csharp
new()
{
    Id = "BlockedSenders",
    DisplayName = "Blocked Senders",
    Description = "View and unblock Exchange Online blocked senders (accounts blocked from sending mail for outbound spam).",
    Route = "blocked-senders",
    IconCss = "bi bi-envelope-fill-nav-menu",
    Category = "Exchange",
    SortOrder = 650,
    EnabledByDefault = false,
    IsSystemModule = false,
    Version = "1.0.0",
    DependsOn = "ExchangeOnline",
    MainPermission = new("Access", "BlockedSenders", FailClosed: true),
    GranularPermissions = [new("Unblock", "BlockedSendersUnblock", FailClosed: true)]
}
```

**Decided (Michael, 2026-06-29): split view vs unblock.** The main `BlockedSenders`
policy gates *viewing* the list; the granular `BlockedSendersUnblock` policy gates the
*unblock write*. A wider group can view; a narrower group can unblock. This means two
section-access groups to configure for this module, and the unblock action carries its
own pre-write re-check against `BlockedSendersUnblock` (in addition to the page-load
check against `BlockedSenders`), per the granular-policy pattern at
`CalendarPermissions.razor:380-387`.

## 7. Task breakdown  [MODEL — Michael skims]

1. (AC1–AC6) Add tests first: catalog count/route/policy + security tests (§8).
2. (AC1,AC2) `BlockedSenderService` with read + single unblock over the EXO pool.
3. (AC1–AC6) `BlockedSenders.razor` page: list/refresh, unblock with ticket +
   confirmation, page-load auth, pre-write re-auth, `<ModuleVersion />`.
4. (AC3, AC3b) Audit success/failure/denied; admin email notification on
   success/failure (try/catch-and-log); operation-trace scope on unblock.
5. (AC4) Descriptor in `ModuleCatalog.RegisterAll()`; update catalog count tests
   (21→22 modules, 29→31 configurable aliases — main + granular).
6. (AC1,AC2) `AddScoped<BlockedSenderService>()` in `Program.cs`.
7. Module doc `docs/BlockedSenders.md`.

## 8. Test plan  [MODEL writes; YOU check the mapping only]

xUnit (`ExchangeAdminWeb.Tests`):

- AC4: module count is 22; route `blocked-senders` unique; page `@page` exists and its
  `[Authorize]` policy equals `BlockedSenders`; configurable aliases include both
  `BlockedSenders` and `BlockedSendersUnblock`; count updated.
- AC4: `Catalog_MutatingModulePermissions_AreFailClosed` already covers the new module
  (it is not on the read-only allowlist) — confirm it passes.
- AC2/AC5: service unblock fails closed when EXO write reports failure → returns a
  failed `PermissionResult`, no success claimed (NSubstitute over the pool seam).
- AC1: service read maps `Get-BlockedSenderAddress` PSObjects to `BlockedSenderInfo`
  including a row with a missing optional property (no throw).
- AC3b: admin-notification send failure does not change the unblock result
  (EmailService seam throws → result still reflects the successful unblock). Covered at
  the service/seam level if the notification call lives in the service; if it stays in
  the page (as in CalendarPermissions), this is asserted via the page's try/catch
  shape and noted as page-level-not-unit-tested, matching the reference module.
- Each new test proven non-vacuous (revert under test, confirm red, restore).

## 9. Traceability check  [MODEL fills when iteration ends; YOU read]

Every §6–§7 element traces to an AC. Notable mappings:
- Service read/unblock → AC1/AC2. List+refresh UI → AC1. Ticket+confirm → AC2.
- Page-load auth + pre-write re-check against `BlockedSendersUnblock` → AC4/AC5.
- Audit (`ListBlockedSenders`/`UnblockSender`/`UnblockSender_Denied`) → AC3.
- Admin notification (try/catch-and-log) → AC3b. `<ModuleVersion />` → AC6.
- Descriptor + count/alias test updates → AC4.

Empty list of untraceable elements — clean. No scope creep: no on-prem path, no
bulk, no ServiceNow, no config field, no protected-principal call (all per §2).

## 10. Review log  [MODEL appends each round]

- Round 0 (draft): plan created from template; descriptor drafted; one open fork
  (view/unblock split) surfaced for Michael.
- Round 1 (2026-06-29): Michael chose the split — main `BlockedSenders` (view) +
  granular `BlockedSendersUnblock` (unblock write). Descriptor, AC5, task 5, and §8
  updated to lock it in.
- Round 3 (2026-06-29): Implemented. New files: `Models/BlockedSenders/BlockedSenderInfo.cs`,
  `Services/BlockedSenderService.cs`, `Components/Pages/BlockedSenders.razor`,
  `ExchangeAdminWeb.Tests/BlockedSendersTests.cs`, `docs/BlockedSenders.md`. Edited:
  `Modules/ModuleCatalog.cs` (descriptor, SortOrder 650), `Program.cs` (DI),
  `ModuleCatalogTests.cs` (count 21→22, aliases 29→31). One compile error fixed during
  build: Razor `@inject` field named `BlockedSenders` collided with the page class
  (CS0542) → renamed field to `BlockedSenderSvc`. Gates: build Release clean (pre-existing
  NU1903 warning only); `dotnet test` 553/553 green (was 539 + 14 new); format
  `--verify-no-changes` clean; `git diff --check HEAD` clean. Non-vacuous proof: reverting
  the granular permission turned 3 tests red (granular/aliases/count), restored → green.
  App version left at 2.3.27 per Michael (new-module bump deferred); module Version 1.0.0.
- Round 2 (2026-06-29): Michael confirmed mutating modules send an admin notification
  (house pattern: 12 of ~15 do; guide §Notifications permits, does not mandate). Added
  AC3b + failure rows + §6/§7/§8 wiring: `EmailService.SendAdminNotificationAsync`
  (generic details overload) on success/failure, global admin-email recipient, no new
  config field, try/catch-and-log so it can't mask the result. Separately flagged:
  whether the *guide* should be amended to mandate this for all mutating modules is a
  `decision`-level change, NOT part of this module — not yet actioned. No code yet —
  awaiting plan approval (Status still Draft).
