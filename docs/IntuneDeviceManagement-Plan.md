# Intune Device Management Module - Plan

Status: Implemented 2026-09-01 (owner go the same day). D1, D2 and D3 all ruled
2026-08-14; no owner decision was outstanding at implementation. All seven slices
landed: S0 `aa31d49` (status-returning Graph mutation helpers; base app version
2.14.0), S1 `d26906e` (models and read-only service), S2 `5d0d308` (catalog entry, page,
read-only UI; module `1.0.0`), S3 `c339e83` (Delete), S4 `723d7c0` (Retire and Wipe), S5
`931294c` (Entra ID device object removal), S6 `080273a` (affected-user notification).
Module `IntuneDevices` stays `1.0.0` -- all seven slices landed before any deploy, so it
ships once (the versioning rule below). Full suite after S6: 2162 passed / 0 failed / 3
skipped. NOT DEPLOYED -- the external prerequisite (a dedicated Entra app registration
and its own Delinea secret) is still outstanding and blocks the first live Graph call;
the manual checks under Manual dev validation ride the next dev deploy once it lands.
Reviewed: openreview `codex` (`@azure-openai-eus2-global/gpt-5.5-dzs` @ xhigh, grade fallback)
over `b868e5c..6aef9e3`: `acceptable_with_changes`, three findings, all admitted and folded in
(`.agents/review/findings/idm-{1,2,3}.md`), plus one material change adopted outside intake.
Re-reviewed after the D2 and D3 rulings: openreview `codex` (same pair) over `6aef9e3..236b91b`,
covering S4, S5, S6, the fourth policy alias and the `Device.ReadWrite.All` prerequisite:
`acceptable_with_changes`, two findings, **both durable-record hygiene, neither touching the
plan's substance** (`.agents/review/findings/idm-{4,5}.md`). No part of this plan is now
unreviewed.

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
4. Wipe behaviour is operator-selectable, with full-reset defaults (D2 principle; see S4).
5. Whether the device's primary user is emailed is operator-selectable per action, with an
   admin-set default per action (D2).
6. Removing the device's **Entra ID** object, behind its own permission, offered beside each
   action and standalone (D3).

**A standing design rule for this module, from the owner, 2026-08-14: *"anything that can be an
option should be an option. do not build in restraints."*** Where Graph exposes a choice, this
module surfaces it rather than hardcoding one branch. The constraint that survives is on
**defaults**, not on availability: a default must be the safe, least-surprising reading of the
button's own label, and every non-default choice must be visible on screen at the moment of
acting and recorded in the audit event. An option nobody can see they selected is worse than no
option.

## Out of scope

Named here so a later reader does not have to re-derive the boundary. None of these are
defects; they are unbuilt.

- Every other Intune remote action (sync, restart, remote lock, fresh start, Autopilot
  reset, locate device, recovery-password rotation, defender scan).
- Bulk / multi-select action across many devices. One device per action.

**Wipe options were on this list and are not any more** (D2 principle, owner 2026-08-14). Two
earlier revisions got this wrong in opposite directions and both are recorded because the pair
is the lesson: the first said the module would send no body "which is a full factory reset" - an
inference stated as verified fact, withdrawn under `idm-2`; the second pinned the flags to a
fixed body and put operator choice out of scope, which is exactly the built-in restraint the
owner then removed. What survives from `idm-2` is the part that was right: **the body is always
explicit and always asserted by a test.** What changes is that its values are chosen by the
operator, with full-reset defaults. See S4.
- Windows Autopilot deregistration.
- Device configuration, compliance policy or app assignment.

## Owner decisions

### D1 - RULED 2026-08-14: all three actions, two permission tiers

Owner: *"options for all of the above with different permission levels for 1 and 2+3. Two
permission levels."*

- Delete sits behind its own granular permission.
- Retire and Wipe share a second, higher granular permission.
- Read (search + detail) sits behind the module's main permission.

This is the `AccountLockoutRemediation` shape (main `Access` plus granular `Logoff`,
`ModuleCatalog.cs:387-390`), with two granular permissions instead of one.

### D2 - RULED 2026-08-14: notifying the primary user is an option, not a fixed rule

Owner: *"anything that can be an option should be an option. do not build in restraints. make
email the user an option."*

The three fixed alternatives that were offered here (never / retire and wipe only / always) are
all **rejected as written**, because each hardcodes one answer. The ruling is that the module
chooses none of them and lets the deployment choose:

- **Three config fields, one per action** - `NotifyUserOnDelete`, `NotifyUserOnRetire`,
  `NotifyUserOnWipe`. These set the deployment's default.
- **A per-action checkbox on the page**, initialised from the matching config field, which the
  operator may change before confirming. The lost-or-stolen case is the reason it must be
  changeable at the moment of acting and not only in config.
- **The audit event records what actually happened** - notified, or not notified and why (config
  default, operator unticked, no address on the device, or suppressed app-wide). "Was the user
  told?" is an audit question, and a silent no is indistinguishable from a failure.

Defaults, per the standing rule in Scope - safe and least-surprising, not restrictive:
`NotifyUserOnDelete` **false** (nothing changes on the user's device, so a mail would confuse),
`NotifyUserOnRetire` **true**, `NotifyUserOnWipe` **true**.

**One interaction that must not be missed, or the option is a lie.** `EmailService` gates every
affected-user send on an app-wide `_notifyUsers` switch (`EmailService.cs:176-180`). It wins over
anything this module sets. An operator who ticks the box on a deployment where user
notifications are off must be told nothing was sent - both on screen and in the audit event -
rather than being shown a ticked box and left to assume.

Constitution position, unchanged either way: the **administrator** notification on every mutating
action is mandatory and is not configurable (`docs/ProjectConstitution.md`, Notifications). Only
the affected-user mail is an option. The Constitution's affected-user rule fires on a permissions
or access change and a device action is neither, so making it configurable does not weaken a rule
that was never binding here.

Body content, since the mail may be read by the wrong person: name the device, the action and the
ticket, and nothing else. On a wipe it may reach a mailbox the user can now only open elsewhere,
and on a lost or stolen device whoever holds it may read it.

**This ruling is why S6 is a required slice rather than a conditional one.**

#### Revision 2026-09-02 - the defaults leave Module Config; the checkbox stays

Owner: *"the email options should not live in global module settings. they should be in the tool so
the user doing the wipe can make the determination."*

This supersedes the **config-default half** of D2 above, and by the same reasoning D3's
`RemoveEntraObjectByDefault` (also a global default for an act-time checkbox). Recorded in
`.agents/decisions.md`, 2026-09-02.

- The four Boolean config fields - `NotifyUserOnDelete`, `NotifyUserOnRetire`, `NotifyUserOnWipe`
  and `RemoveEntraObjectByDefault` - are **removed from the descriptor**. Only
  `GraphDelineaSecretId` and `SearchResultLimit` remain.
- The **act-time checkboxes stay exactly as ruled** - this half of D2 is untouched. Their starting
  states are now fixed in code (`IntuneDeviceService.NotifyUserStartsTicked`,
  `IntuneDeviceService.EntraRemovalStartsTicked`): notification **off** for delete, **on** for
  retire and wipe - D2's own defaults, now hardcoded - and Entra removal **off**.
- Everything else in D2 is unchanged: the app-wide `_notifyUsers` interaction stated above still has
  to be surfaced on screen and in the audit event, and the audit still records which not-sent reason
  applied.

### D3 - RULED 2026-08-14: the Entra ID device object can be removed too, as an option

Owner: *"yes, add it as an option."*

No Intune action removes the Entra ID device object - Microsoft's own guidance is to remove it
as a separate step. An operator who deletes here and expects the device gone from the tenant
would otherwise be wrong, and would have to finish the job in another portal.

Built as S5: `Device.ReadWrite.All` on the same app registration, its own granular permission
`IntuneDevicesEntraDelete`, a checkbox offered beside each of the three Intune actions and a
standalone action on the detail panel, starting unticked. (The `RemoveEntraObjectByDefault` config
field this originally read is gone - see D2's *Revision 2026-09-02*; the checkbox itself is
unchanged and still starts off.)

**Two things about this decision that are not like the other three actions, and both are why it
gets its own permission rather than riding `IntuneDevicesPrivileged`:**

1. `Device.ReadWrite.All` is a **directory** scope, not an Intune one. It confers write access
   over every device object in the tenant, including devices this module would never show
   because they are not Intune-managed. It is the widest grant in the module by some way.
2. The blast radius differs in kind. Wiping a device destroys data on one machine; removing its
   directory object affects how that machine authenticates, and it is the step conditional-access
   and compliance reporting notice.

Neither is an argument against the ruling - the owner asked for it knowing the module manages
devices. They are the reasons the permission is separable and the default is off.

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
   - `DeviceManagementManagedDevices.ReadWrite.All` - delete the Intune record.
   - `DeviceManagementManagedDevices.PrivilegedOperations.All` - retire and wipe.
   - `Device.ReadWrite.All` - remove the Entra ID device object (D3). **This one is not an
     Intune scope**: it is directory write access covering every device object in the tenant,
     which is a wider grant than the three above and worth weighing separately.

   The four are distinct and the split is the point: an app registration that never gets
   `PrivilegedOperations.All` cannot wipe a machine even if the code is wrong, and one that
   never gets `Device.ReadWrite.All` cannot touch the directory. Do not collapse them.

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
| Remove Entra object | `DELETE /devices(deviceId='{azureADDeviceId}')` | 204 | `Device.ReadWrite.All` |

**The Entra row uses the alternate-key form on purpose.** `DELETE /devices/{id}` also exists and
takes the directory **object id**; `managedDevice.azureADDeviceId` is the **`deviceId`**, a
different GUID on the same device. Learn's `device: get` example shows both on one object
(`id: 000005c3-b7a6-...`, `deviceId: 6fa60d52-01e7-...`). Using the path form with an
`azureADDeviceId` returns 404 for a device that is present and fine.

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

### T4b - the secret is excluded at the HTTP boundary, not just at the renderer

`activationLockBypassCode` is returned by `GET /deviceManagement/managedDevices` unless the
request narrows the fields. "Do not render it" leaves it in the response body, in memory, and in
anything that later logs a response for diagnosis.

Both reads therefore send an explicit `$select` naming exactly the fields the page uses (the list
under Verified API surface, which does not include it), and `IntuneDevice` has no property to
hold it. Two independent barriers, because the renderer is the one this repo has watched fail:
`blr-1` is a module that carefully kept recovery keys out of the audit log on one path and wrote
one there from another.

`$select` also bounds the response size on a tenant with thousands of devices, which is the
second reason to do it and not the first.

### T4c - search input goes into an OData literal and must be escaped

A `$filter` clause is built as `deviceName eq '<value>'`. A device name or serial containing a
single quote - `O'Brien-Laptop` is an ordinary asset-tag shape - terminates the literal early and
the request fails or, worse, changes meaning. OData escapes an embedded single quote by doubling
it; the whole value is then `Uri.EscapeDataString`d as a query parameter.

This is the structured-parser rule from `docs/ProjectConstitution.md` (Code Change Discipline)
applied to a query language rather than a shell. Tests cover a value containing `'`, one
containing `&` and `#`, an empty value, and an overlong value.

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

> Superseded in part - see D2's *Revision 2026-09-02*: the four Boolean fields below
> (`NotifyUserOnDelete`, `NotifyUserOnRetire`, `NotifyUserOnWipe`, `RemoveEntraObjectByDefault`) are
> NOT in the shipped descriptor. `Modules/ModuleCatalog.cs` is the live shape.

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
        new("Privileged", "IntuneDevicesPrivileged", FailClosed: true),
        new("EntraDelete", "IntuneDevicesEntraDelete", FailClosed: true)
    ],
    ConfigFields = [
        new("GraphDelineaSecretId", "Graph App Delinea Secret ID",
            "Secret Server secret containing Tenant ID, Application ID, and Client Secret fields"),
        new("SearchResultLimit", "Search Result Limit",
            "Devices returned per search. Defaults to 50, capped at 500.",
            Required: false, DefaultValue: "50"),
        new("NotifyUserOnDelete", "Email User On Delete",
            "Default for the per-action checkbox: email the device's primary user when its Intune record is deleted. Nothing changes on the device, so this defaults off.",
            Required: false, DefaultValue: "false"),
        new("NotifyUserOnRetire", "Email User On Retire",
            "Default for the per-action checkbox: email the device's primary user when company data is removed from the device.",
            Required: false, DefaultValue: "true"),
        new("NotifyUserOnWipe", "Email User On Wipe",
            "Default for the per-action checkbox: email the device's primary user when the device is factory reset. Suppressed app-wide if user notifications are disabled.",
            Required: false, DefaultValue: "true"),
        new("RemoveEntraObjectByDefault", "Also Remove Entra ID Object By Default",
            "Default for the 'also remove the Entra ID device record' checkbox offered beside each action. Off by default: it is a second, separately permissioned deletion against a different object.",
            Required: false, DefaultValue: "false")
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
  present sets `Truncated` (T1). Both reads send an explicit `$select` naming only the fields the
  page uses, so `activationLockBypassCode` never enters a response (T4b), and search values are
  OData-escaped before they reach a `$filter` clause (T4c).
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
  asserts 34 configurable aliases (becomes 38, since all four aliases arrive together here -
  the descriptor declares them all in S2 even though `IntuneDevicesEntraDelete` is not consumed
  until S5, because a policy alias is data in a list and creates no compile dependency; that is
  the `ru-2` rule, which forbids registering a *type* early, not a string).
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
- **Retire sends no body, ever.** Learn is explicit that it takes none, and a test asserts the
  request body is absent so a shared helper cannot quietly give it one.
- **Wipe always sends an explicit body, and every parameter Graph accepts is an operator
  option** (D2 principle). The wipe panel offers:

  | Control | Parameter | Default | Note |
  | --- | --- | --- | --- |
  | Keep user data | `keepUserData` | `false` | Ticking it contradicts the button's own label, so the panel says so inline |
  | Keep enrollment state | `keepEnrollmentData` | `false` | Device stays enrolled after the reset |
  | macOS recovery PIN | `macOsUnlockCode` | empty | Six digits. Shown for macOS devices; **displayed back once after a successful queue, because the operator must give it to the device owner and it is not retrievable afterwards** |
  | macOS obliteration behaviour | `obliterationBehavior` | unset | `default` / `doNotObliterate` / `obliterateWithWarning` / `alwaysObliterate` |
  | Keep eSIM data plan | `persistEsimDataPlan` | `false` | iOS/iPadOS eSIM |

  `keepUserData` and `keepEnrollmentData` are **always** serialized, at their chosen values, so
  the reset semantics are never left to a Graph default - that is the surviving half of `idm-2`.
  The other three are included only when the operator sets them, since sending
  `macOsUnlockCode: ""` or an obliteration behaviour to a Windows device is meaningless.
- **The audit event records the exact flag set used**, not merely "wipe". A wipe with
  `keepUserData: true` and a full reset are different acts and an audit trail that renders them
  identically cannot answer the only question anyone will ask afterwards.
  `macOsUnlockCode` is a device-unlock secret and is recorded as `(set)` or `(not set)`, never
  its value - the `activationLockBypassCode` rule (T4b) applied to an operator-supplied secret
  rather than a returned one.
- A service test asserts the serialized body for the defaults and for each non-default
  combination. The intent is pinned by assertions, not by this table.
- Same gate chain as S3 against the second alias.
- Wipe requires typing the device name to confirm. Retire uses the ordinary confirm. The
  asymmetry is deliberate: retire is recoverable by re-enrolling, wipe destroys the machine's
  contents.
- Buttons are `Delete record` / `Retire` / `Wipe`. No two share a leading verb, and none is
  captioned with a bare "Remove" - `D6` in `docs/MigrationBatchSelection-Plan.md`.
- Audit actions `IntuneDevices_Retire` and `IntuneDevices_Wipe`.

### S5 - Entra ID device object removal, behind `IntuneDevicesEntraDelete` (D3)

- `RemoveEntraDeviceAsync` on the service, over S0's `DeleteWithStatusAsync`.
- **Address the object by the alternate key, never by path id:**
  `DELETE /devices(deviceId='{azureADDeviceId}')`. This is the trap in this slice and it is
  silent if you get it wrong - `managedDevice.azureADDeviceId` holds the Entra **`deviceId`**,
  while `DELETE /devices/{id}` expects the directory **object id**, and the two are different
  GUIDs on the same device (Learn's own `device: get` example returns
  `id: 000005c3-...` alongside `deviceId: 6fa60d52-...`). Passing the former to the latter's
  form yields a 404 against a real, still-present device. Both forms are documented in v1.0; only
  the alternate-key one takes what this module has.
- Offered as a **checkbox alongside each of the three Intune actions**, starting unticked (D2's
  *Revision 2026-09-02* removed the config field), and also runnable on its own from the detail panel -
  the Entra record often outlives the Intune one, so an operator cleaning up needs it without a
  second Intune action.
- **Order and reporting: the Intune action runs first, the Entra removal second, and the two
  results are reported independently.** Never a blanket success - this is Known Failure Class 2
  in `.agents/repo-guidance.md`, and the half-finished case ("Intune record deleted; the Entra ID
  object could not be removed: <reason>") is the one an operator must be able to see and retry.
  `azureADDeviceId` is captured from the device detail **before** the Intune action, because
  after a successful delete there is nothing left to read it from.
- **A device that is not Entra-joined has no usable `azureADDeviceId`** (absent, empty, or the
  all-zero GUID). The checkbox is then not offered, with the reason stated - not offered-and-
  silently-skipped.
- Same gate chain as S3 and S4: granular authorization re-checked in the handler (T6), the
  protected-principal two-branch check on the device's primary user (T4), servicer support (T5),
  ticket, audit on success and on every refusal, admin notification.
- Its **own** granular permission rather than riding `IntuneDevicesPrivileged`. It acts on a
  different object, in a different Graph scope, and an operator entitled to wipe a phone is not
  automatically entitled to remove directory records. More separable permissions is the D2
  principle applied to authorization.
- Audit action `IntuneDevices_EntraDelete`, target the device name plus the `deviceId` used.
- T3 and AC11 change conditionally here: when the Entra object was removed too, the result must
  no longer claim it survives.

### S6 - affected-user notification (required; D2)

- A device-shaped method on `EmailService`, alongside the existing
  `SendUserNotificationAsync:169` and `SendGroupMembershipUserNotificationAsync` rather than
  overloading either: their subjects and bodies are mailbox and group specific. It honours the
  same app-wide `_notifyUsers` gate (`EmailService.cs:176-180`) and **returns whether it
  actually sent**, so the caller can record a suppressed send instead of assuming one happened.
- The three config fields, the per-action checkbox seeded from them, and the operator override.
- The audit event's notification outcome, with its reason when nothing was sent.
- A visible on-screen statement when the app-wide switch suppressed a send the operator asked
  for. Without it the checkbox is a control that silently does nothing, which is the
  unreachable-capability shape from the other direction.
- Notification failure is caught and logged and never changes the reported result of the device
  action (`docs/ProjectConstitution.md`, Notifications). The device is already wiped by then.

### S7 - documentation and version

- `docs/IntuneDeviceManagement.md` in the shape of `docs/BitLockerRecovery.md`: purpose,
  operators, permissions table, config table, credentials, audit actions, fail-closed table,
  manual validation, rollback, and an explicit "not in this MVP" list matching Out of scope
  above.
- README section.
- `.agents/state.md` entry.
- Module version `1.0.0` in the descriptor, **and the base app version bumped** for the shared
  changes in S0 and S6. Both versioning rules fire; see Versioning.
- Last slice deliberately: the module doc has to describe the notification and Entra options,
  which do not exist until S5 and S6 land.

## Implementation record 2026-09-01

All seven slices landed, one commit each, in order:

| Slice | Content | SHA |
|---|---|---|
| S0 | Status-returning Graph mutation helpers (shared infrastructure); base app version bumped to `2.14.0` | `aa31d49` |
| S1 | Models and read-only service | `d26906e` |
| S2 | Catalog entry, page, read-only UI; module version `1.0.0` | `5d0d308` |
| S3 | Delete, behind `IntuneDevicesDelete` | `c339e83` |
| S4 | Retire and Wipe, behind `IntuneDevicesPrivileged` | `723d7c0` |
| S5 | Entra ID device object removal, behind `IntuneDevicesEntraDelete` (D3) | `931294c` |
| S6 | Affected-user notification (D2) | `080273a` |

Full suite after S6: 2162 passed / 0 failed / 3 skipped. Module `IntuneDevices` reads
`1.0.0` and the base app version reads `2.14.0` in `ExchangeAdminWeb.csproj` -- both
verified against the live catalog and csproj at close, neither changed by this record.
NOT DEPLOYED. The external prerequisite (dedicated Entra app registration with the four
scopes named above, admin-consented, plus its own Delinea secret) remains outstanding
and blocks the first live Graph call; the manual checks in this file ride the next dev
deploy once it lands.

## Acceptance criteria

- AC1 A user in no section-access group for `IntuneDevices` is denied at `/intune-devices` by
  direct URL, not merely by a hidden nav link.
- AC2 A user with `IntuneDevices` but not `IntuneDevicesDelete` can search and view detail and
  cannot delete - proven at the handler, not by the button being hidden.
- AC3 A user with `IntuneDevicesDelete` but not `IntuneDevicesPrivileged` can delete and cannot
  retire or wipe.
- AC3b A user with `IntuneDevicesPrivileged` but not `IntuneDevicesEntraDelete` can wipe and
  cannot remove the Entra ID object, and is not offered the checkbox.
- AC4 With no section access configured for any of the four aliases, all four deny. Fail
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
  check-in. Delete says the Intune record is gone and that company data remains on the device.
  The claim that the Entra ID object survives is made only when it actually does - when the
  Entra removal ran and succeeded, the result says so instead.
- AC12 `activationLockBypassCode` is excluded at the request boundary by `$select` and has no
  property on `IntuneDevice` to land in, so it appears nowhere in the page, audit record,
  notification or log. Both barriers are asserted; either alone is one edit away from failing.
- AC13 Every action writes an audit event on success and on failure, and an administrator
  notification on every one. An audit or notification failure is logged and does not make a
  completed action report as failed.
- AC14 The module's own Delinea secret is the only credential used. With
  `GraphDelineaSecretId` unset the module reports unavailable and falls back to nothing.
- AC15 Delete, retire and wipe report 403, 404 and 5xx as three distinct outcomes carrying the
  sanitized Graph error. Reverting either S0 helper to its bool-returning equivalent must fail
  the service tests that distinguish them - if it does not, the distinction was never tested.
- AC16 Wipe always serializes `keepUserData` and `keepEnrollmentData` explicitly, at whatever
  values the operator chose, defaulting both to `false`; the optional three appear only when set;
  retire sends no body at all. Asserted per combination, not described.
- AC17 A device name or serial containing a single quote is searchable and produces a well-formed
  request.
- AC18 The per-action "email the user" checkbox defaults from the module's config field for that
  action, and the operator's change at the moment of acting is what takes effect.
- AC19 With user notifications disabled app-wide, ticking the box sends nothing, says so on
  screen, and records the suppression and its reason in the audit event. A suppressed send is
  never reported as a send.
- AC20 A device with no primary user address offers no notification and records why - it does not
  fail the action, and it does not silently look like a successful send.
- AC21 The audit event for a wipe names the exact flag set used, so a `keepUserData: true` wipe
  and a full reset are distinguishable afterwards. `macOsUnlockCode` appears as `(set)` /
  `(not set)`, never its value.
- AC22 The Entra removal addresses `/devices(deviceId='...')`. A test asserts the request URL;
  building the path form from `azureADDeviceId` must fail it.
- AC23 An Intune action that succeeds followed by an Entra removal that fails reports both
  outcomes separately and is not recorded as a plain success. Each writes its own audit event.
- AC24 A device with no usable `azureADDeviceId` is not offered the Entra checkbox, and the
  reason is on screen - the option is never offered-and-silently-skipped.

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
- Query construction: the request URL carries `$select`, and that `$select` does not name
  `activationLockBypassCode`; a search value containing `'` is doubled and escaped; values
  containing `&` and `#`, an empty value, and an overlong value each produce a well-formed URL.
- A response that nonetheless contains `activationLockBypassCode` (a `$select` regression, or a
  tenant returning it anyway) does not surface it: `IntuneDevice` has nowhere to put it.
- Delete / retire / wipe: 204 success; 403; 404; 5xx - each mapped to a distinct reported
  outcome, never a blanket success.
- Wipe body, one case per combination: both flags default (`false`/`false`); `keepUserData` on;
  `keepEnrollmentData` on; a macOS PIN set; an obliteration behaviour set; `persistEsimDataPlan`
  on. Each asserts the serialized body, and the unset optional three must be **absent** rather
  than present-and-null. Retire sends no body at all, asserted separately - the two must not
  converge on one helper that quietly gives retire a body.
- Notification decision, as a pure function so it is testable without a mail server: config
  default on/off per action, operator override in both directions, no primary-user address, and
  the app-wide `_notifyUsers` switch off. Each returns a distinct outcome with a reason, and the
  suppressed cases are distinguishable from the sent case.
- Audit payload for a wipe carries the flag set, and `macOsUnlockCode` is rendered `(set)` /
  `(not set)`. A test asserts the PIN's literal value appears in no audit field.
- Entra removal: the request URL is `/devices(deviceId='<guid>')` and not `/devices/<guid>`;
  204, 403, 404 and 5xx are distinct outcomes; an absent, empty or all-zero `azureADDeviceId`
  is refused before any request is made.
- Combined action outcomes: Intune success + Entra success; Intune success + Entra failure;
  Intune failure (Entra step must not run, because the operator's intent was conditional on the
  first succeeding). Each case reports both steps and is never collapsed to one verdict.
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
8. Wipe a scrap device; confirm the type-the-name confirmation is required, that the wipe options
   are all present and default to a full reset, and that the audit event names the flag set used.
   Repeat with "keep user data" ticked and confirm the two runs are distinguishable in the audit
   log. On a macOS device, confirm the recovery PIN is shown back once after queuing and appears
   nowhere in the audit record.
8b. Confirm the notification checkbox starts ticked on a wipe and unticked on a delete - fixed
   states, with no config field to change them (D2's *Revision 2026-09-02*). Clear it on a wipe and
   confirm no mail arrives; tick it on a delete and confirm the user is emailed - the operator's
   choice at the moment of acting is the point of the control.
8c. Disable user notifications app-wide; repeat 8b and confirm the page says nothing was sent and
   the audit event records the suppression. This is the check that proves the checkbox is not
   decorative.
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
14. Without `IntuneDevicesEntraDelete`, confirm the Entra checkbox is not offered and the
    handler refuses if invoked directly. Grant it, then delete a scrap device with the box
    ticked and confirm the Entra ID object is actually gone from the tenant - check the portal,
    not just the module's message.
15. Repeat 14 against an app registration without `Device.ReadWrite.All`: the Intune record must
    be deleted, the Entra step must report its own failure with a reason, and the result must
    not read as a plain success. This is the half-finished case and it is the one worth seeing.
16. Find a device that is Intune-managed but not Entra-joined; confirm the checkbox is absent
    with the reason stated rather than silently doing nothing.

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
- S6 also touches `EmailService`, which is shared as well. That is a second shared change under
  the same single base bump, not a second bump.
