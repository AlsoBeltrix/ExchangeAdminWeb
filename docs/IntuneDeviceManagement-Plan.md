# Intune Device Management Module - Plan

Status: Draft - awaiting owner go to implement. D1 ruled 2026-08-14. D2 and D3 open.

Owner request 2026-08-14: *"we need to plan a module for managing intune devices, pulling
device details, and deleting"*.

New module `IntuneDevices`. Microsoft Graph v1.0 Intune device management. Independent of
`docs/GroupMemberNesting-Plan.md`, `docs/ProtectedGroupWriteTarget-Plan.md` and
`docs/RiskyUsersModule-Plan.md`: no shared code, no ordering constraint in either direction.

## Scope

1. Search Intune managed devices and list matches.
2. Show full detail for one device.
3. Three destructive actions, at two permission tiers (D1):
   - Tier 1: **Delete** the Intune management record.
   - Tier 2: **Retire** (remove company data) and **Wipe** (factory reset).

## Out of scope

Named here so a later reader does not have to re-derive the boundary. None of these are
defects; they are unbuilt.

- The Microsoft Entra ID device object. Deleting, retiring or wiping in Intune does **not**
  remove it - Microsoft's own guidance is to remove it as a separate step
  (`https://learn.microsoft.com/en-us/intune/intune-service/remote-actions/devices-wipe`,
  section "Remove a device from Microsoft Entra ID"). See D3.
- Every other Intune remote action (sync, restart, remote lock, fresh start, Autopilot
  reset, locate device, recovery-password rotation, defender scan).
- Operator-chosen wipe options. `wipe` accepts `keepEnrollmentData`, `keepUserData`,
  `macOsUnlockCode`, `obliterationBehavior` and `persistEsimDataPlan`. This module sends a fixed
  body pinning the first two to `false` (see S4) and does not offer the operator any choice.
  Surfacing the flags is a later change.

  **An earlier revision said the module would send no body "which is a full factory reset". That
  was an inference stated as a verified fact and is withdrawn** - the Learn page for `wipe` says
  "in the request body, supply JSON representation of the parameters", while the page for
  `retire` says "do not supply a request body". What Graph does with an absent `wipe` body is
  unknown, and a default of `keepUserData: true` on any platform would leave data on a machine
  the operator was told had been wiped. The fixed explicit body removes the dependency on that
  unknown instead of resolving it.
- Windows Autopilot deregistration.
- Device configuration, compliance policy or app assignment.
- Bulk / multi-select action across many devices. One device per action.

## Owner decisions

### D1 - RULED 2026-08-14: all three actions, two permission tiers

Owner: *"options for all of the above with different permission levels for 1 and 2+3. Two
permission levels."*

- Delete sits behind its own granular permission.
- Retire and Wipe share a second, higher granular permission.
- Read (search + detail) sits behind the module's main permission.

This is the `AccountLockoutRemediation` shape (main `Access` plus granular `Logoff`,
`ModuleCatalog.cs:387-390`), with two granular permissions instead of one.

### D2 - OPEN: does the device's primary user get notified?

The Constitution requires an **administrator** notification on every mutating action
(`docs/ProjectConstitution.md`, Notifications). That is not in question and the plan
implements it.

The separate affected-user rule fires on "any change to a user's permissions or access". A
device wipe is not literally a permissions or access change, so no existing predicate
settles this - unlike `docs/GroupMemberNesting-Plan.md` D6, where one did. Options:

1. **No affected-user email.** Admin notification and audit only. No shared-file change; the
   base app version stays put.
2. **Email the primary user on Retire and Wipe only.** They lose a working device; Delete
   changes nothing they can see.
3. **Email the primary user on all three.**

Cost of 2 or 3: `EmailService` has no device-shaped notification. The nearest existing
methods are mailbox/calendar/group specific (`SendUserNotificationAsync:169`,
`SendGroupMembershipUserNotificationAsync`), so a new one is needed.

**An earlier revision of this plan priced that as a base app version bump and used it as an
argument for option 1. That argument is withdrawn** - S0 already bumps the base version for
`GraphTokenClient`, so options 2 and 3 add a second shared-file change under the same single
bump and cost no version consequence at all. D2 is a question about what the affected person
should be told, and nothing else.

Second-order point for whoever rules it: on a Wipe the email may reach a mailbox the user
can now only open on another device, and on a lost/stolen device the mail may be readable by
whoever has it. That argues for a terse body naming the device and the ticket, and no
detail.

**D2 is a pre-ship gate, not a start gate.** S1 through S4 proceed with admin notification
only; nothing is marked `Implemented` until D2 is ruled and, if 2 or 3 wins, S6 exists.

### D3 - OPEN, default is out of scope: the Entra ID device object

Deleting an Intune record leaves the Entra ID device object behind. An operator who deletes
a device here and expects it gone from the tenant will be wrong.

1. **Leave it (default, and what this plan builds).** Page text says plainly that the Entra
   ID record survives and where to remove it.
2. **Offer a separate "also remove from Entra ID" action.** Needs `Device.ReadWrite.All`
   added to the same app registration, a third gate, and its own audit action.

The owner asked for "intune devices"; adding Entra device deletion unasked is the scope
error recorded in `.agents/state.md` for `docs/ProtectedGroupWriteTarget-Plan.md`. Default
stands unless the owner rules otherwise.

## Read-alerting classification

The Constitution requires an administrator **alert** on reads by a module classified as a
security-response surface. `.agents/decisions.md` 2026-06-30 classifies every existing
module's reads as non-alerting because they expose only data already visible in AD or the
address book.

Intune device inventory is **not** address-book data - serial number, IMEI, encryption
state, compliance state and jailbreak status are not - so that specific reasoning does not
transfer. But this module is not purpose-built for security response either; it is an
inventory and decommissioning surface for service desk staff. **Classification: non-alerting.
Audit is sufficient.** Recorded here so the reasoning is visible; the owner can overturn it,
and doing so adds one call to the read path and nothing else.

Every read still audits (`LogLookupAction`).

## External prerequisites

Neither is code. Both block the first live call, not the build.

1. **A dedicated Entra app registration** with its own Delinea Secret Server record
   containing `Tenant ID`, `Application ID` and `Client Secret` fields, per
   `docs/AdminModuleSpec.md` "For Graph API modules". Application permissions, admin
   consented:
   - `DeviceManagementManagedDevices.Read.All` - search and detail.
   - `DeviceManagementManagedDevices.ReadWrite.All` - delete.
   - `DeviceManagementManagedDevices.PrivilegedOperations.All` - retire and wipe.

   The three are distinct and the split is the point: an app registration that never gets
   `PrivilegedOperations.All` cannot wipe a machine even if the code is wrong. Do not
   collapse them.

2. **An active Intune licence on the tenant.** The Graph Intune API errors without one. As
   with `docs/RiskyUsersModule-Plan.md` D-prerequisite-1, the request for the module is the
   evidence; this is recorded, not asked.

## Verified API surface

All endpoints verified against Microsoft Learn on **2026-08-14**, not from memory. All are
**v1.0**, so `Services/GraphTokenClient.cs:16` (hardcoded `https://graph.microsoft.com/v1.0`)
needs no change and no NuGet package is added.

| Operation | Request | Success | Application permission |
| --- | --- | --- | --- |
| List | `GET /deviceManagement/managedDevices` | 200 + collection | `...ManagedDevices.Read.All` |
| Get one | `GET /deviceManagement/managedDevices/{managedDeviceId}` | 200 + object | `...ManagedDevices.Read.All` |
| Delete | `DELETE /deviceManagement/managedDevices/{managedDeviceId}` | 204 | `...ManagedDevices.ReadWrite.All` |
| Retire | `POST /deviceManagement/managedDevices/{managedDeviceId}/retire` | 204 | `...ManagedDevices.PrivilegedOperations.All` |
| Wipe | `POST /deviceManagement/managedDevices/{managedDeviceId}/wipe` | 204 | `...ManagedDevices.PrivilegedOperations.All` |

**Delete and retire take no request body; wipe does, and the two Learn pages differ on this
deliberately.** Retire: "Do not supply a request body for this method." Wipe: "In the request
body, supply JSON representation of the parameters." Do not treat the three actions as
interchangeable in shape - S4 specifies wipe's body exactly.

Fields the detail view uses, all present on `managedDevice` in v1.0: `id`, `deviceName`,
`managedDeviceName`, `userPrincipalName`, `userDisplayName`, `userId`, `operatingSystem`,
`osVersion`, `manufacturer`, `model`, `serialNumber`, `imei`, `meid`, `wiFiMacAddress`,
`ethernetMacAddress`, `enrolledDateTime`, `lastSyncDateTime`, `complianceState`,
`managementAgent`, `managedDeviceOwnerType`, `deviceEnrollmentType`, `deviceRegistrationState`,
`isEncrypted`, `isSupervised`, `jailBroken`, `azureADDeviceId`, `azureADRegistered`,
`totalStorageSpaceInBytes`, `freeStorageSpaceInBytes`, `deviceActionResults`, `notes`.

**Do not render `activationLockBypassCode`.** It is returned by the API and it is a secret
that unlocks a device. It must not reach the page, an audit record, a notification or a log
line - the `BitLockerRecovery` recovery-key rule (`docs/BitLockerRecovery.md`) applied to a
different secret.

## Design constraints and traps

### T1 - paging: `@odata.nextLink` is absolute and the shared client cannot follow it

`GraphTokenClient` prepends `GraphBaseUrl` to whatever path it is given
(`GraphTokenClient.cs:35`); `@odata.nextLink` is a fully qualified URL. Concatenating them
produces a broken request. This is the identical trap `docs/RiskyUsersModule-Plan.md` pins.

Same resolution, for the same reason: **do not teach the shared client to page.** Two other
modules use that file and the change would bump the base app version. Instead the service
requests a bounded page with `$top`, and when `@odata.nextLink` is present it returns
`Truncated = true` and the page says so in words. A silently short list is the failure mode
that matters here - an operator who searches a serial number, sees nothing, and concludes
the device is not enrolled.

### T2 - `$filter` support on `managedDevices` is narrow and is NOT yet verified

The Learn list page documents no filterable-property list. Server-side filtering on
`managedDevices` is known to be restricted, and `$search` is not available.

**S1's first live task is to establish, against the real tenant, which of `deviceName`,
`serialNumber` and `userPrincipalName` accept `$filter eq`, and record the answer in this
file.** Until then the plan does not assume any of them work.

Fallback if a property will not filter server-side: fetch one bounded page with `$top` and
match client-side, with `Truncated` surfaced exactly as in T1. A client-side match over a
truncated page is a **partial** search and must never render as "no such device" - it renders
as "no match in the first N devices" with the truncation stated.

Do not reach for `/users/{id}/managedDevices` without verifying it first; it was not
confirmed during planning (the Learn URL for it 404'd) and nothing here depends on it.

### T3 - the destructive verbs are asynchronous, and 204 is acceptance not completion

`retire`, `wipe` and `delete` return `204 No Content`. For retire and wipe that means Intune
accepted the request; the device acts on it at its next check-in, which for a powered-off or
offline machine may be never. `managementState` carries values such as `retirePending`
precisely because of this.

This is the `D8` lesson from `docs/MigrationBatchSelection-Plan.md` verbatim - "the cmdlets
return when Exchange ACCEPTS the request, not when the work is done", and the page reported
"Removed" over a row still on screen. Wording here is **"Queued wipe of ..."** and
**"Queued retire of ..."**, with a line saying the device carries it out at its next check-in
and an offline device may not act for a long time. Delete is immediate on the Intune record
and is worded "Deleted the Intune record for ..." - and that message must also state that
company data stays on the device and the Entra ID object survives (D3).

### T4 - protected principals bind to the device's primary user

A device is not a principal, but wiping the CEO's laptop is exactly the harm the rule exists
for, and the Constitution's guard "binds to the target of the write" with no routine-change
carve-out. The principal is the device's `userPrincipalName`.

Intune devices belong to **cloud** identities, so this is the `MfaReset` situation: an
AD-only resolve reports a cloud-only user as "no AD object" and skips the check, leaving
protection close to inert. Use the two-branch shape at `Components/Pages/MfaReset.razor:262-364`:

- `ProtectedPrincipalService.ResolveWithExchangeFallbackAsync(upn)`.
- `Unavailable` or `Ambiguous` -> refuse, audit the refusal.
- Resolved -> `CheckAsync(resolved)`; `CheckFailed` refuses; `IsProtected` refuses unless a
  servicer note is returned.
- Not resolved -> build a raw `ResolvedDirectoryPrincipal` from the UPN and `CheckAsync` that,
  so protected **user** rows still match a cloud-only identity. Group, OU and
  SamAccountName-pattern rules are structurally inapplicable to it, not skipped
  (`docs/ProjectConstitution.md`, Protected Principals, final bullet).

One improvement this module can make over `MfaReset`: the device carries `userId`, the Entra
object id of the primary user, so the raw principal is built with `EntraObjectId` populated
rather than null.

**A device with no primary user** (shared, kiosk, or Autopilot pre-provisioned) has an empty
`userPrincipalName` on a successfully read device. That is a determinate answer - there is no
principal to protect - so the action proceeds. It is **not** the same as a failed read or an
absent field on a failed request, both of which are unavailable data and fail closed. Get
this distinction wrong in either direction and you either strand every shared device or open
a hole.

### T5 - the page gate is part of the authorization decision

`pps-2` in `.agents/state.md`: a page that hides the write UI at lookup time, without
consulting the servicer service, makes the serviced write path unreachable. Both gates -
the page and the service - honour servicing, and a serviced operator sees a banner naming
the override. An override the operator cannot see is one they cannot decline.

### T6 - authorization is re-checked immediately before each write

Not at page load. The granular policy for the specific action
(`IntuneDevicesDelete` or `IntuneDevicesPrivileged`) is re-evaluated inside the handler after
the confirmation step, exactly as `MfaReset.razor:250-258` does. `pps-1` found a module that
re-checked authorization after its confirmation dialog but not protection; both are re-checked
here.

### T7 - a failed Graph call must not read as a benign outcome, on reads or on writes

**Reads.** `GetAsync` collapses failure and empty into null (`GraphTokenClient.cs:45-51`). Use
`GetWithStatusAsync` on every read and throw or report on non-success, the fix already made
in `MfaResetService.cs:54-57`. A 403 from a missing consent must not render as an empty
device list.

**Writes, and this is why S0 exists.** The shared client's `DeleteAsync`
(`GraphTokenClient.cs:53-61`) and `PostNoContentAsync` (`:63-77`) both return a bare `bool` and
discard the status code. Against them, a 403 for missing `PrivilegedOperations.All`, a 404 for a
device someone already deleted, and a 503 are the same value - so the module could report only
"it failed". That is not enough for this module specifically: its whole three-permission split
exists to make a misconfigured app registration visible, and manual check 12 tests exactly that
case. S0 adds the status-returning pair before any slice needs them.

## Module descriptor

Added to `Modules/ModuleCatalog.cs` `RegisterAll()`.

```csharp
new()
{
    Id = "IntuneDevices",
    DisplayName = "Intune Devices",
    Description = "Search Intune managed devices, view device detail, and delete, retire or wipe a device.",
    Route = "intune-devices",
    IconCss = "bi bi-gear-fill-nav-menu",
    Category = "Infrastructure",
    SortOrder = 820,
    EnabledByDefault = false,
    IsSystemModule = false,
    Version = "1.0.0",
    MainPermission = new("Access", "IntuneDevices", FailClosed: true),
    GranularPermissions = [
        new("Delete", "IntuneDevicesDelete", FailClosed: true),
        new("Privileged", "IntuneDevicesPrivileged", FailClosed: true)
    ],
    ConfigFields = [
        new("GraphDelineaSecretId", "Graph App Delinea Secret ID",
            "Secret Server secret containing Tenant ID, Application ID, and Client Secret fields"),
        new("SearchResultLimit", "Search Result Limit",
            "Devices returned per search. Defaults to 50, capped at 500.",
            Required: false, DefaultValue: "50")
    ]
}
```

Notes on the choices:

- **Icon.** Only twelve `*-nav-menu` classes exist in `wwwroot/app.css:952-1019` and none is a
  device or laptop. `tools/validate-module-package.ps1` rejects an icon class it cannot find,
  which is why `BitLockerRecovery` reuses `bi bi-gear-fill-nav-menu` with a comment saying so
  (`ModuleCatalog.cs:488-491`). Same reuse, same reason. Adding an icon is a separate change.
- **Category and sort.** `Infrastructure` at 820, after `DhcpAuthorization` (800) and
  `BitLockerRecovery` (810). Verified unused.
- **All three permissions fail closed.** Read included: device inventory is not address-book
  data.
- **`EnabledByDefault = false`**, per the Constitution's optional-module rule.

## Slices

Each slice is one commit and must build and test green on its own. Slice boundaries are drawn
on **compilation order**, not conceptual grouping - `ru-3` in `.agents/state.md` records a plan
whose first commit would have failed `CS0246` because DI registration preceded the type.

### S0 - status-returning Graph mutation helpers (shared infrastructure)

Two purely additive methods on `Services/GraphTokenClient.cs`, both modelled on the existing
`PatchWithStatusAsync` (`:104-119`) and returning its shape
`(bool Ok, HttpStatusCode StatusCode, string? SafeError)`:

- `DeleteWithStatusAsync(string endpoint)`
- `PostNoContentWithStatusAsync(string endpoint, object? body = null)`

Both reuse the existing `ExtractGraphError` (`:132-154`), so only the Graph `error.code` /
`error.message` can escape - never a token or a raw body.

`DeleteAsync` and `PostNoContentAsync` stay exactly as they are, with their current callers
untouched, the same back-compat pairing `PatchAsync` already has over `PatchWithStatusAsync`
(`:121-125`). Additive is deliberate rather than tidy: `ppsvc-1` in `.agents/review/index.md` is
this repo's record of a change near this file reaching further than its author expected.

Tests extend `ExchangeAdminWeb.Tests/GraphTokenClientTests.cs` - the one file this module may
legitimately touch, because the code under test lives there.

**This slice bumps the base app version.** `GraphTokenClient` is shared infrastructure and two
other modules use it. See Versioning.

### S1 - models and read-only service

- `Models/IntuneDeviceModels.cs`: `IntuneDevice` (the fields listed above, minus
  `activationLockBypassCode`), `IntuneDeviceSearchResult` (`Devices`, `Truncated`,
  `SearchedCount`), `IntuneDeviceActionResult` (`Success`, `Message`, `SafeError`).
- `Services/IntuneDeviceService.cs`: `GetGraphClientAsync()` copied in shape from
  `MfaResetService.cs:20-37` but reading `IntuneDevices` / `GraphDelineaSecretId`, never
  another module's config; `IsAvailable`; `SearchDevicesAsync`; `GetDeviceAsync`. Reads use
  `GetWithStatusAsync` (T7). `$top` from `SearchResultLimit`, capped at 500. `@odata.nextLink`
  present sets `Truncated` (T1).
- Register in `Program.cs` beside `builder.Services.AddSingleton<MfaResetService>();` (line
  113), using the `"MicrosoftGraph"` named client (line 104).
- Tests with a slice-local stub `HttpMessageHandler`. **Do not reference
  `GraphTokenClientTests.StubHandler`** - it is `private sealed` and not reachable from another
  test class (`ru-3`).
- **Live task, blocking S2's search UI shape:** determine which properties accept `$filter eq`
  (T2) and record the result in this file.

No catalog entry yet, so no page and no route: nothing user-reachable ships in S1.

### S2 - catalog entry, page, read-only UI

- The descriptor above.
- `Components/Pages/IntuneDevices.razor`: `@attribute [Authorize(Policy = "IntuneDevices")]`,
  the `OnInitializedAsync` re-check, `<ModuleVersion />` inside the `<h1>` (required, and
  enforced by `tools/validate-module-package.ps1`), a search box, a results table, and a detail
  panel.
- Truncation and unavailability are rendered in words, never as an empty table (T1, T7).
- A standing note on the page: deleting an Intune record does not remove company data from the
  device and does not remove the Entra ID device object (D3).
- Read auditing via `Audit.LogLookupAction`.
- **Test guards this slice breaks, and they are the point of naming it:**
  `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs:16` asserts 25 modules (becomes 26) and `:109`
  asserts 34 configurable aliases (becomes 37, since all three aliases arrive together here).
  Also check `Catalog_ConfigureAuthorizationPolicies_GeneratesExpectedPolicies`
  (`ModuleCatalogTests.cs:112` onward) - if its expected-policy array is exhaustive it needs the
  three new aliases too. Verify before assuming either way.

### S3 - Delete, behind `IntuneDevicesDelete`

- `DeleteDeviceAsync` on the service, over S0's `DeleteWithStatusAsync`. It reports 403, 404 and
  5xx as distinct outcomes carrying the sanitized Graph error - never a bare "failed".
- Page action: ticket number field adjacent to the button (`MigrationBatchSelection-Plan.md` D1
  and `mbs-1` - a confirm bar far from the acting control reads to the operator as a dead
  button), confirmation step, then in the handler: granular authorization re-check (T6),
  protected-principal check with the two-branch shape and servicer support (T4, T5),
  `Audit.LogModuleAction` on success and on every refusal, `Email.SendAdminNotificationAsync`.
- **Add `"IntuneDevices"` to `ModulesWithProtectedPrincipalServicing`
  (`Components/Pages/ModuleConfig.razor:650-657`) in this same commit.** That set is hardcoded,
  and `:849-853` renders the `ProtectedServicer:` editor only for members of it - so without
  this line no operator can ever grant `ProtectedServicer:IntuneDevices`, while every test that
  seeds `section_access` directly still passes. The list's own comment at `:648` states the
  rule: add the module here in the same commit that adds its `Evaluate` call, never before.
  This repo has shipped the unreachable-capability defect twice (`ppsvc-1`, `pgwt-1`); the rule
  it earned is that a capability is not implemented until the person meant to use it can reach it.
- A source-level tripwire asserts the pairing holds: if a gate in `IntuneDevices.razor` calls the
  servicing helper, the module id must be in that set. Removing the id must fail a test by name,
  not merely make a manual step awkward.
- Result wording per T3.
- Audit action name `IntuneDevices_Delete`, category `IntuneDevices`, target the device name
  plus its `id`.

### S4 - Retire and Wipe, behind `IntuneDevicesPrivileged`

- `RetireDeviceAsync` and `WipeDeviceAsync`, both over S0's `PostNoContentWithStatusAsync`, with
  the same distinct-outcome reporting as S3.
- **Retire sends no body. Wipe sends exactly this, always:**

  ```json
  { "keepEnrollmentData": false, "keepUserData": false }
  ```

  Those two flags are what decide whether the reset is full, so they are stated rather than
  defaulted. The other three parameters are omitted deliberately and for named reasons:
  `macOsUnlockCode` is a per-device recovery PIN the operator would have to supply and record,
  which is its own feature; `obliterationBehavior` and `persistEsimDataPlan` are
  platform-specific and belong with the wipe-options work listed as out of scope. Omitting them
  accepts Graph's defaults for behaviour that does not change whether data survives.
- A service test asserts the serialized body byte for byte. The intent is pinned by an assertion,
  not by this paragraph - which is the whole difference from the revision this replaces.
- Same gate chain as S3 against the second alias.
- Wipe requires typing the device name to confirm. Retire uses the ordinary confirm. The
  asymmetry is deliberate: retire is recoverable by re-enrolling, wipe destroys the machine's
  contents.
- Buttons are `Delete record` / `Retire` / `Wipe`. No two share a leading verb, and none is
  captioned with a bare "Remove" - `D6` in `docs/MigrationBatchSelection-Plan.md`.
- Audit actions `IntuneDevices_Retire` and `IntuneDevices_Wipe`.

### S5 - documentation and version

- `docs/IntuneDeviceManagement.md` in the shape of `docs/BitLockerRecovery.md`: purpose,
  operators, permissions table, config table, credentials, audit actions, fail-closed table,
  manual validation, rollback, and an explicit "not in this MVP" list matching Out of scope
  above.
- README section.
- `.agents/state.md` entry.
- Module version `1.0.0` in the descriptor. **Base app version unchanged** - adding a module is
  not a shared-infrastructure change (`docs/ProjectConstitution.md`, Deployment And Versioning;
  `.agents/decisions.md` 2026-07-21).
- Mark this plan `Implemented` only after D2 is ruled.

### S6 - affected-user notification (exists only if D2 rules 2 or 3)

New `EmailService` method and its call sites. No additional version consequence - S0 has already
bumped the base app version. Kept as its own slice because it is the only part of the work whose
existence depends on an unruled decision, so a D2 of option 1 simply deletes a slice rather than
unpicking edits from the others.

## Acceptance criteria

- AC1 A user in no section-access group for `IntuneDevices` is denied at `/intune-devices` by
  direct URL, not merely by a hidden nav link.
- AC2 A user with `IntuneDevices` but not `IntuneDevicesDelete` can search and view detail and
  cannot delete - proven at the handler, not by the button being hidden.
- AC3 A user with `IntuneDevicesDelete` but not `IntuneDevicesPrivileged` can delete and cannot
  retire or wipe.
- AC4 With no section access configured for any of the three aliases, all three deny. Fail
  closed.
- AC5 A Graph 403 on search renders as an error naming that the request failed, never as an
  empty device list.
- AC6 A search whose response carries `@odata.nextLink` renders the truncation in words, and a
  client-side no-match over a truncated page does not claim the device does not exist.
- AC7 A device whose primary user is a protected principal refuses delete, retire and wipe, and
  each refusal is audited with the matched rules.
- AC8 The same device, acted on by a member of a group holding
  `ProtectedServicer:IntuneDevices`, succeeds, shows the override banner, and writes an audit
  record naming the authorising group and the rules overridden. **The grant must be made through
  the module's own Module Config page, not seeded into `section_access` by a test.** A seeded row
  proves the gate reads the key; only the page proves an operator can create it, and that is the
  half this repo has twice shipped broken.
- AC9 A protection check that is `Unavailable`, `Ambiguous`, `CheckFailed`, or that throws,
  refuses. Fail closed outranks servicing - there is no known refusal to override.
- AC10 A device read successfully with an empty `userPrincipalName` is actionable; a device
  whose read failed is not.
- AC11 Retire and wipe results say "queued" and state that the device acts at its next
  check-in. Delete says the Intune record is gone and that company data and the Entra ID object
  remain.
- AC12 `activationLockBypassCode` appears nowhere in the page, audit record, notification or log.
- AC13 Every action writes an audit event on success and on failure, and an administrator
  notification on every one. An audit or notification failure is logged and does not make a
  completed action report as failed.
- AC14 The module's own Delinea secret is the only credential used. With
  `GraphDelineaSecretId` unset the module reports unavailable and falls back to nothing.
- AC15 Delete, retire and wipe report 403, 404 and 5xx as three distinct outcomes carrying the
  sanitized Graph error. Reverting either S0 helper to its bool-returning equivalent must fail
  the service tests that distinguish them - if it does not, the distinction was never tested.
- AC16 Wipe sends `{"keepEnrollmentData":false,"keepUserData":false}` and a test asserts that
  exact body; retire sends none. The full-reset intent is carried by an assertion, not by prose.

## Test plan

S0 extends `ExchangeAdminWeb.Tests/GraphTokenClientTests.cs`: `DeleteWithStatusAsync` and
`PostNoContentWithStatusAsync` each over 204, 403, 404 and 500, asserting the status and the
extracted `SafeError`; a non-JSON error body yields a null `SafeError` rather than echoing the
body; and the existing `DeleteAsync` / `PostNoContentAsync` tests still pass unchanged, which is
what proves the addition was additive.

New file `ExchangeAdminWeb.Tests/IntuneDeviceServiceTests.cs` with a slice-local stub handler.

- Search: success; 403; 429; 500; empty collection; response with `@odata.nextLink` sets
  `Truncated`; `$top` clamped at 500 and defaulted at 50.
- Detail: success; 404; malformed JSON.
- Delete / retire / wipe: 204 success; 403; 404; 5xx - each mapped to a distinct reported
  outcome, never a blanket success.
- Wipe body: the serialized request body equals `{"keepEnrollmentData":false,"keepUserData":false}`.
  Flipping either flag to `true` must fail this test. Retire sends no body at all, asserted
  separately - the two must not converge on one helper that quietly gives retire a body.
- `GetGraphClientAsync`: unset secret id; non-numeric secret id; secret present but missing
  `Client Secret`; each returns null and the caller reports unavailable.

New file `ExchangeAdminWeb.Tests/IntuneDeviceProtectionTests.cs`, following the existing
protection suites: protected resolved principal refused; protected unresolved (cloud-only)
principal refused via the raw-identity branch; `Unavailable` refused; `Ambiguous` refused;
`CheckFailed` refused; exception refused; empty `userPrincipalName` on a good read allowed;
servicer grant allows and returns a note; no servicer grant refuses. If the gate is not
reachable from a public method, add an `internal` seam as
`SelfServiceGroupService.CheckMemberProtectedAsync` and two others already do - the test
assembly already sees internals.

`ModuleCatalogTests.cs` counts updated in S2 as described.

**Non-vacuity, per the repo standard.** For each of: the truncation flag, the protection gate,
the granular authorization re-check, and the `GetWithStatusAsync` error path - revert the
behaviour, confirm the specific tests fail, restore, confirm green. Confirm the revert actually
landed on disk before trusting the verdict, and confirm it is gone after. Note the
timestamp trap in `.agents/state.md`: a `Copy-Item` restore carries the backup's timestamp and
MSBuild will keep testing the mutant - touch the file after restoring.

**What tests cannot cover here, stated so nobody reads green as proof.** There is no bUnit
harness, so no test renders the page. Every gate that lives in `IntuneDevices.razor` - the page
authorization re-check, the servicer banner, the confirm-adjacency, the wording of the queued
result - is unproven by the suite. Three of the last four review findings in this repo lived in
a page. The manual checks below are the only evidence for those.

## Manual dev validation

1. Enable the module in Admin Settings; grant your group `IntuneDevices` only.
2. Search a known device by name; confirm rows, and confirm the version renders beside the
   heading.
3. Open detail; confirm no activation lock bypass code is shown anywhere.
4. Confirm Delete, Retire and Wipe are unavailable, then hit the handler directly and confirm
   it refuses (AC2).
5. Grant `IntuneDevicesDelete`; delete a scrap device; confirm the message states the Entra ID
   object and on-device data survive; confirm the audit record and admin email.
6. Confirm Retire and Wipe still refuse (AC3).
7. Grant `IntuneDevicesPrivileged`; retire a scrap device; confirm the wording says queued and
   the device shows `retirePending`.
8. Wipe a scrap device; confirm the type-the-name confirmation is required.
9. Add the primary user of a test device to the protected user rows; confirm all three refuse
   and the audit names the matched rules.
10. On the module's Module Config page, confirm the `ProtectedServicer:IntuneDevices` editor is
    offered at all, and grant it to a test group there - do not write the row by hand. Then
    repeat 9 as a member of that group; confirm success, the banner, and the audit note naming
    the group. Confirm afterwards that the module's ordinary grants survived the save, since
    that page writes the whole section-access store back.
11. Clear `GraphDelineaSecretId`; confirm the module reports unavailable rather than empty.
12. Point the secret at an app registration lacking `PrivilegedOperations.All`; confirm wipe
    reports a permission failure rather than a silent success.
13. Sign in as a user outside every group; confirm `/intune-devices` denies by direct URL.

## Rollback

Disable the module in Admin Settings. It holds no local state and writes nothing outside
Intune, so there is nothing to undo. Actions already queued at Intune are not recallable from
here - that is a property of the API, not of the rollback.

## Versioning when this lands

- New module `IntuneDevices` `1.0.0`.
- **Base app version bumps**, because S0 changes `Services/GraphTokenClient.cs`, which is shared
  infrastructure used by two other modules. Adding a module does not bump the base version; this
  work does one other thing as well, and that other thing does.
- The two rules fire independently. A correct base bump for S0 is not evidence about the module
  version, and vice versa - `.agents/state.md` records the `2.5.1` and Migration `1.6.0` failures,
  both of which were one rule firing and the other being assumed to have.
- If D2 rules option 2 or 3, S6 also touches `EmailService`. That is a second shared change under
  the same single base bump, not a second bump.
