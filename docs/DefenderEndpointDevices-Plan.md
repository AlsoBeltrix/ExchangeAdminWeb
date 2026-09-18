# Defender for Endpoint Devices Module - Plan

Status: Draft (2026-09-18). Nothing is approved and no code exists. **The app registration
described under "What the owner must create" blocks every slice below**: creating it is the
owner's job, not this repo's, and the first live call cannot happen without it.

Owner request, verbatim from the queue:

> 8. New module for Microsoft Defender for Endpoints:
> 	List & Export all devices
> 	Specific request was for all windows devices in the "Can be onboarded" state
> 	including
> 	1. discovery sources
> 	2. IP, domain, OS, etc.
> 	3. other important info
> 	Report should be exportable. Will require a new app reg, so tell me what the permissions/requirements are.

New module `DefenderEndpointDevices`. Read-only. Closest precedent in this repo is
`docs/IntuneDeviceManagement-Plan.md` (a device-listing module over a per-module Entra app
registration) and the CSV half of `docs/ModuleCsvExport-Plan.md`. This module is independent of
both: no shared code, no ordering constraint either way.

All API facts below were verified against Microsoft Learn on **2026-09-18**, not from memory.
Every source URL is listed under Sources. Anything that could NOT be verified is collected under
"Assumptions" and is labelled as an assumption in the body as well.

## Scope

1. List **every** Defender for Endpoint device matching the active filters, paging the API to
   completion - defaulting to the two filters the owner asked for: onboarding status "can be
   onboarded", operating system Windows. Both filters are operator-changeable on the page.
   "Every" is load-bearing and is what T3 exists to guarantee: the module either returns the
   complete matching set or refuses in words. It never returns a partial set that looks whole.
2. Show the per-device fields listed under "Field mapping" in a table.
3. Export that complete result set to CSV.

## Out of scope

Named so a later reader does not have to re-derive the boundary. None of these are defects.

- **Every device action.** Isolate, release from isolation, run antivirus scan, collect
  investigation package, restrict app execution, offboard, stop-and-quarantine. This module
  reads and exports; it mutates nothing. Adding any of them is a separate plan, a separate
  granular permission and a wider app-registration grant (`Machine.Isolate`,
  `Machine.Scan`, `Machine.Offboard`, and so on - none of which this plan asks for).
- Device tagging (`Machine.ReadWrite.All` territory).
- Onboarding a device. The module reports which devices *can be* onboarded; it does not onboard
  them, and nothing here installs or configures a sensor.
- Alerts, incidents, vulnerabilities, software inventory, recommendations.
- Network devices and IoT/OT inventory as a first-class view. They appear in the same inventory
  and will show up in the list when they match the filters; this module does not add the
  IoT-specific columns or tabs the portal has.
- Scheduled or emailed reports. Export is operator-initiated, in-session.

## D1 - which API

**Recommendation: the Defender for Endpoint API at `https://api.security.microsoft.com/api/machines`,
NOT Microsoft Graph, because Graph has no device-inventory resource for Defender for Endpoint at
all.** This is a constraint, not a preference, and the owner needs to know it before the
registration is created, because it changes which API the permission is granted on.

Evidence, not recollection:

| Question | Answer | Source |
| --- | --- | --- |
| Does Microsoft Graph expose the Defender for Endpoint device inventory? | No. The Graph security API surface is alerts, incidents, advanced hunting, attack simulation, eDiscovery, audit log query, identities (Defender for Identity sensors and health issues), information protection, records management, Secure Score and threat intelligence. There is no machine/device resource in that list. | Graph security API overview |
| Where does the device list live? | `GET https://api.security.microsoft.com/api/machines`, the Defender for Endpoint API. | List machines API |
| Does that API expose the onboarding state? | Yes. `onboardingStatus` is a property of the `machine` resource and is one of the properties `$filter` supports. | Machine resource; List machines API |
| Is the Defender for Endpoint API being retired in favour of Graph? | Only the **advanced hunting** endpoint is. Learn's warning: the MDE advanced hunting API "is transitioning to the Microsoft Graph security API ... Retirement began in January 2026. After retirement completes, the Microsoft Defender for Endpoint advanced hunting API no longer functions." The Graph overview puts a date on it: the older endpoints "are now retired and will stop returning data on February 1, 2027." No equivalent notice exists on the machines API pages. | Advanced Hunting API (MDE); Graph security API overview |

**Consequence for this module: it talks to two different APIs, for two different reasons.**

- **Defender for Endpoint API** (`api.security.microsoft.com`) for the device list. This is the
  only place the data exists.
- **Microsoft Graph** (`graph.microsoft.com/v1.0/security/runHuntingQuery`) for **discovery
  sources only**, because `DiscoverySources` is a column of the advanced hunting `DeviceInfo`
  table and is **not** a property of the `machine` REST resource. The Graph form is used rather
  than the MDE `POST /api/advancedqueries/run` form precisely because the MDE one is the piece
  that is retiring.

Both are reachable from **one** app registration and **one** Delinea secret: the same client ID
and secret acquire two tokens with two different scopes. That is why this plan asks the owner for
one registration, not two.

Naming note, because the hostnames are confusing and a wrong one is a silent 403: Learn's current
pages use `https://api.security.microsoft.com` for the request URL, while the **token audience is
still the legacy value** `https://api.securitycenter.microsoft.com`. Learn says so explicitly:
"Some Microsoft Defender for Endpoint APIs continue to require access tokens issued for the legacy
resource `https://api.securitycenter.microsoft.com`. If the token audience doesn't match the
resource expected by the API, requests fail with `403 Forbidden`, even if the API endpoint uses
`https://api.security.microsoft.com`. Use `https://api.securitycenter.microsoft.com` as the
resource or scope when acquiring tokens." See T2.

## What the owner must create - the app registration

This is the deliverable. It is written so it can be handed to whoever holds the tenant admin role
without a follow-up round trip.

### 1. Register the application

In the Microsoft Entra admin center: **App registrations** > **New registration**. Single tenant.
Creating it needs a role with app-registration rights, such as **Application Administrator**
(Learn names that role in the prerequisites of the client-credentials walkthrough).

Name it for the module so it is identifiable later, for example `ExchangeAdminWeb - Defender
Endpoint Devices`. The name is cosmetic; nothing in the code reads it.

### 2. Grant these application permissions

**Application permissions only. No delegated permissions.** The app runs as a background service
with no signed-in user (OAuth 2.0 client credentials).

| # | Permission | Display name in the portal | API / resource to pick | Needed for | Required? |
| --- | --- | --- | --- | --- | --- |
| 1 | `Machine.Read.All` | 'Read all machine profiles' | **WindowsDefenderATP** (API permissions > Add permission > **APIs my organization uses** > search `WindowsDefenderATP`) | The device list, all fields except discovery sources | **Yes** - without it the module does nothing |
| 2 | `ThreatHunting.Read.All` | 'Run hunting queries' (Microsoft Graph) | **Microsoft Graph** | Discovery sources, device type, vendor and model columns | **Only if the owner wants discovery sources** - see Q1 |

That is the whole list. Two permissions, and the second one is optional.

### 3. Admin consent

**Yes, admin consent is required, and it is required for both.** Application permissions (app
roles) are never user-consentable; they take effect only after a tenant admin grants consent, in
**API permissions** > **Grant admin consent for <tenant>**.

Which role can grant it depends on which of the two permissions is being consented, and this
matters:

- `Machine.Read.All` (WindowsDefenderATP): **Privileged Role Administrator**, or
  **Cloud Application Administrator** / **Application Administrator** / **AI Administrator** -
  those three can consent to any permission for any API *except* Microsoft Graph application
  permissions.
- `ThreatHunting.Read.All` (**Microsoft Graph**): **Privileged Role Administrator** only (or
  Global Administrator, which contains it). Application Administrator and Cloud Application
  Administrator are explicitly excluded from consenting Microsoft Graph app roles.

**So: if the owner wants the discovery-sources column, the consent has to be done by a Privileged
Role Administrator or a Global Administrator.** If discovery sources are dropped, an Application
Administrator can complete the whole thing.

### 4. Credential: client secret

Create a **client secret** (Certificates & secrets > New client secret) and record its **Value**
immediately - it is not retrievable after the blade is left. Set an expiry the owner is willing
to diary; the module has no secret-rotation automation and a lapsed secret fails every call.

A certificate is also supported by the identity platform and by this API, but **this repo cannot
use one today**: `Services/GraphTokenClient.cs` and everything modelled on it authenticate with
`client_secret` in a form post, and the Delinea secret shape this repo standardises on
(`Tenant ID`, `Application ID`, `Client Secret`) has no certificate field. Certificate auth would
be a separate piece of shared work. **Create a secret.**

### 5. Where the credential lands in this repo

Exactly the `ServiceHealth` / `IntuneDevices` shape, unchanged:

1. A **Delinea Secret Server record dedicated to this module**, containing three fields named
   exactly `Tenant ID`, `Application ID` and `Client Secret`
   (`docs/AdminModuleSpec.md`, "For Graph API modules"). The secret must be directly readable by
   the Delinea API bootstrap credential, with no checkout or approval workflow - a noninteractive
   call cannot complete one.
2. That record's numeric **Secret ID** is typed into the module's own config page, into the
   `GraphDelineaSecretId` field declared by the descriptor below. Same field id and same label as
   `ServiceHealth` and `IntuneDevices` use.
3. Nothing else is configured anywhere. **The tenant ID and client ID are read from the Delinea
   secret at call time.** They are not in `appsettings.json`, not defaulted in code, and not
   written into this plan.

The field is named `GraphDelineaSecretId` and not `DelineaSecretId` even though the primary API
is not Graph: `DelineaSecretId` is reserved for on-prem AD / Exchange credentials, and
`GraphDelineaSecretId` is the repo's name for an Entra app-registration secret
(`docs/ProjectConstitution.md`, Credential Isolation). This registration also holds a real Graph
permission, so the name is accurate as well as conventional.

**Do not point this module at an existing module's secret.** Per-module credential isolation is a
Constitution invariant. Reuse is allowed only if the owner deliberately types the same Secret ID
into both modules' config, and there is no reason to here - the permissions do not overlap with
any existing module's.

### 6. Redirect URI: none

**Confirmed: no redirect URI, and no platform configuration at all.** Learn's client-credentials
walkthrough configures none. The only place it adds one (`https://portal.azure.com`) is the
**multitenant partner** section, for ISVs whose app runs in customers' tenants. This is a
single-tenant daemon; there is no browser, no sign-in and no reply URL.

### 7. Least privilege - what to grant and what NOT to grant

The repo has a live blocker about a registration carrying unnecessary `*.ReadWrite.All` grants
(`.agents/state.md`, CloudPasswordReset residuals: `Directory.ReadWrite.All`,
`User.ReadWrite.All`, `Group.ReadWrite.All`, `UserAuthenticationMethod.ReadWrite.All` on survey
registration `16866221-...`). This section exists so that is not repeated.

**Narrowest set that satisfies the request:** `Machine.Read.All`, plus `ThreatHunting.Read.All`
if and only if discovery sources are wanted. Nothing else.

**Tempting and NOT needed - do not grant:**

| Permission | Why it looks needed | Why it is not |
| --- | --- | --- |
| `Machine.ReadWrite.All` ('Read and write all machine information') | Learn's **Get machine by ID** page lists *only* `Machine.ReadWrite.All` for the Application permission type, so following that page leads straight to a write grant. | This module never calls Get-machine-by-id. The detail panel is rendered from the row already returned by **List machines**, which documents `Machine.Read.All`. The design is shaped this way deliberately, to keep a write scope off the registration. See T4. |
| `Machine.Isolate`, `Machine.Scan`, `Machine.Offboard`, `Machine.StopAndQuarantine`, `Machine.RestrictExecution` | "Device management" sounds like it includes actions. | Actions are out of scope. A registration that never holds them cannot isolate or offboard a machine even if the code is wrong. |
| `AdvancedQuery.Read.All` (WindowsDefenderATP) or `AdvancedHunting.Read.All` (Microsoft Threat Protection) | The older advanced hunting APIs use these. | Those endpoints are retired and stop returning data on 2027-02-01. Use Graph `ThreatHunting.Read.All` instead. Granting all three is the classic belt-and-braces mistake. |
| `Alert.Read.All`, `Vulnerability.Read.All`, `SecurityRecommendation.Read.All`, `Score.Read.All` | They are on the same consent screen and look harmless. | Nothing in scope reads them. |
| Any Microsoft Graph `Device.*`, `Directory.*`, `User.*` | The list shows Entra device IDs, so directory access looks adjacent. | The module renders `aadDeviceId` as an opaque string. It never resolves it against the directory, and must not start to without a plan amendment. |

**One privilege cost that must be stated plainly rather than buried:** `ThreatHunting.Read.All`
is not a device-scoped permission. It grants the application read access to **every advanced
hunting table in the tenant** - device events, email events, identity events, cloud app activity -
not merely `DeviceInfo`. There is no narrower documented scope, and no way to restrict the
permission to one table. The module's own query only ever touches `DeviceInfo` and that is
asserted by a test (T7), but the *grant* is broad, and a compromised secret would not be
constrained by the module's code. That is the honest trade for the discovery-sources column, and
it is Q1 below rather than a decision this plan makes for the owner.

### 8. Other prerequisites, none of them code

- **Microsoft Defender for Endpoint Plan 2.** Learn's prerequisites for reviewing discovered
  devices name it, and add: "You have at least one device onboarded to Defender for Endpoint.
  Onboarded devices act as network sensors and data sources for discovering non-onboarded
  devices." Without an onboarded sensor there are no "can be onboarded" devices to list, because
  nothing is discovering them.
- **Advanced hunting is not included in Microsoft Defender for Business.** If the tenant is on
  Defender for Business rather than Defender for Endpoint P2, the discovery-sources half cannot
  work at all, whatever is consented.
- **Device discovery must be on.** It is on by default in Standard mode. If it has been set to
  Basic, or the relevant networks are not in the monitored list, the inventory simply will not
  contain the devices the report is meant to find, and the module will correctly report an empty
  list. That is a tenant configuration question, not a module defect.
- **Retention.** "You can get devices last seen according to your configured retention period."
  Devices last seen before that window are not returned.

## The "Can be onboarded" state

**The owner's phrase matches the portal label exactly, and does NOT match the API literal.** Both
forms are real; they belong to different interfaces, and using the portal's spelling in an API
filter returns nothing.

| Interface | Literal | Verified from |
| --- | --- | --- |
| Defender portal filter, **Onboarding status** | `Can be onboarded` | Device inventory doc: "Filter by **Onboarding status** > **Can be onboarded** to find discovered devices ready for agent deployment." |
| Defender for Endpoint REST API, `machine.onboardingStatus` | **`CanBeOnboarded`** | Machine resource: "Status of machine onboarding. Possible values are: `onboarded`, `CanBeOnboarded`, `Unsupported`, and `InsufficientInfo`." |
| Advanced hunting, `DeviceInfo.OnboardingStatus` | not verified - see Assumptions | Learn's published queries only ever compare against `"Onboarded"` (`where OnboardingStatus != "Onboarded"`), never against the can-be-onboarded value. |

**The filter this module sends is therefore:**

```http
GET https://api.security.microsoft.com/api/machines?$filter=onboardingStatus eq 'CanBeOnboarded'&$top=...
```

`onboardingStatus` is on the documented `$filter` list for machines, so this is a supported
server-side filter and not a client-side scan.

What the state means, quoted so nobody has to guess: "Defender for Endpoint discovers the device
in the network and supports its operating system, but the device isn't onboarded." The full
enumeration is `Onboarded`, `Can be onboarded`, `Unsupported` ("Defender for Endpoint discovers
the endpoint, but doesn't support the device") and `Insufficient info` ("The system couldn't
determine the supportability of the device").

**Two warnings that belong on the page, not just in this file:**

1. Learn's own caveat: "You may notice differences between the number of listed devices under
   **can be onboarded** in the device inventory, the **onboard to Microsoft Defender for
   Endpoint** security recommendation, and the **devices to onboard** dashboard widget. The
   security recommendation and the dashboard widget are for devices that are stable in the
   network, excluding ephemeral devices, guest devices, and others." An operator comparing this
   module's count with a portal widget will see a difference, and it is not a bug.
2. Learn on the four interfaces: "The API, UI, export, and Advanced Hunting (AH) interfaces all
   draw from a single authoritative data source. However, because each is powered by separate
   backend systems with different update frequencies, slight variations may appear across views."

**Is the endpoint the right one for these devices?** Learn does not state outright that
`GET /api/machines` returns non-onboarded devices, but three things say it does: `CanBeOnboarded`
is a documented value of the `machine` resource's own `onboardingStatus`; `onboardingStatus` is a
documented `$filter` property on that collection; and the discovered-devices article says "You can
also use the onboarding status column on API queries to filter out unmanaged devices", which only
makes sense if unmanaged devices are in the API's results. **That is strong, but it is inference
from three documents rather than one sentence, so R1(a) proves it against the live service and
records the answer in this file before S3 or S4 proceed.** If it turns out the machines
collection returns only onboarded devices, the fallback is to source the whole list from the same
advanced hunting
`DeviceInfo` query that already supplies discovery sources - which changes the permission ask to
`ThreatHunting.Read.All` being mandatory rather than optional, and would need to go back to the
owner. This is the single riskiest unknown in the plan.

## Field mapping

What the owner asked for, against real field names. Blank is not an option: where something is
not obtainable it says so here so it is not discovered during a demo.

| Owner asked for | Concrete field | Where from | Notes |
| --- | --- | --- | --- |
| Discovery sources | `DiscoverySources` | advanced hunting `DeviceInfo`, via Graph `runHuntingQuery` | **Not available from the machines API.** Learn: "Products or services that have seen or reported the device, including when they last reported it." The portal column "tells you how each device was found: **MDE** (found by the Defender for Endpoint sensor), **Microsoft Defender for IoT** (discovered by Defender for IoT), and other sources." |
| IP | `lastIpAddress`, `lastExternalIpAddress`, and the `ipAddresses` collection | machines API | `ipAddresses` entries carry `ipAddress`, `macAddress` and `operationalStatus` (Learn's OData samples show all three on a list response), so MAC addresses come free with the IPs. |
| Domain | **partially available - read this before promising it** | machines API | There is **no `domain` property** on the `machine` resource. The portal's device inventory does show a "domain" column, but the REST resource does not expose it. What exists is `computerDnsName`, the "machine fully qualified name" - so the **DNS suffix** can be derived from it (everything after the first dot) and that is what the CSV column `DnsDomain` will contain. **Active Directory domain membership as such is not obtainable here.** Entra join state is available separately as `isAadJoined` / `aadDeviceId`. |
| OS | `osPlatform`, `version`, `osBuild`, `osArchitecture` | machines API | `osPlatform` carries values like `Windows10`, `Windows11`; `osProcessor` is deprecated in favour of `osArchitecture`. |
| "other important info" | `id`, `computerDnsName`, `firstSeen`, `lastSeen`, `healthStatus`, `riskScore`, `exposureLevel`, `deviceValue`, `machineTags`, `rbacGroupName`, `onboardingStatus`, `isAadJoined`, `aadDeviceId` | machines API | `healthStatus` is `Active` / `Inactive` / `ImpairedCommunication` / `NoSensorData` / `NoSensorDataImpairedCommunication` / `Unknown`. `riskScore` is `None` / `Informational` / `Low` / `Medium` / `High`. `exposureLevel` is `None` / `Low` / `Medium` / `High`. |
| (useful for "what is this thing?") | `DeviceType`, `DeviceCategory`, `Vendor`, `Model` | advanced hunting `DeviceInfo` | Same query as discovery sources, so they cost nothing extra once it runs. Learn flags `Vendor` and `Model` as "only available if device discovery finds enough information about this attribute" - expect blanks. |

**Not obtainable, stated so the owner is not surprised:**

- **AD domain / OU membership of a discovered device.** Not in the machine resource, not in
  `DeviceInfo`. A non-onboarded device has no sensor to report it.
- **Logged-on user for a non-onboarded device.** `DeviceInfo.LoggedOnUsers` exists, but there is
  no agent on a can-be-onboarded device to report it (assumption - see Assumptions).
- **Why a device is unsupported, per device.** `Unsupported` and `Insufficient info` are states,
  not explanations.
- **A "should this be onboarded?" verdict.** The portal's onboarding *recommendation* excludes
  ephemeral and guest devices by its own logic; this list does not reproduce that logic and must
  not be presented as if it had.

## CSV export

`Services/CsvExport.Write(header, rows)` - the shared writer, which already handles quoting and
neutralises spreadsheet-formula injection. Downloaded through the existing
`JS.InvokeVoidAsync("downloadFile", ...)` path, filename
`DefenderEndpointDevices_yyyyMMdd_HHmmss.csv`, exactly as `BlockedSenders.razor:236-256` does,
and audited the same way (`ExportCsv`, with the row count).

Proposed columns, in order. Multi-valued cells are joined with `"; "`, matching
`NamedLocations.razor`.

| Column | Source |
| --- | --- |
| `DeviceId` | `id` |
| `ComputerDnsName` | `computerDnsName` |
| `DnsDomain` | derived: `computerDnsName` after the first `.`, empty when there is no dot |
| `OnboardingStatus` | `onboardingStatus` |
| `OsPlatform` | `osPlatform` |
| `OsVersion` | `version` |
| `OsBuild` | `osBuild` |
| `OsArchitecture` | `osArchitecture` |
| `HealthStatus` | `healthStatus` |
| `LastIpAddress` | `lastIpAddress` |
| `LastExternalIpAddress` | `lastExternalIpAddress` |
| `IpAddresses` | `ipAddresses[].ipAddress`, joined |
| `MacAddresses` | distinct `ipAddresses[].macAddress`, joined |
| `FirstSeenUtc` | `firstSeen` |
| `LastSeenUtc` | `lastSeen` |
| `RiskScore` | `riskScore` |
| `ExposureLevel` | `exposureLevel` |
| `DeviceValue` | `deviceValue` |
| `MachineTags` | `machineTags`, joined |
| `MachineGroup` | `rbacGroupName` |
| `IsAadJoined` | `isAadJoined` |
| `AadDeviceId` | `aadDeviceId` |
| `DiscoverySources` | `DeviceInfo.DiscoverySources` |
| `DeviceType` | `DeviceInfo.DeviceType` |
| `DeviceCategory` | `DeviceInfo.DeviceCategory` |
| `Vendor` | `DeviceInfo.Vendor` |
| `Model` | `DeviceInfo.Model` |

The last five columns are present in the header whatever happens, so the column set does not
change shape between runs. When the hunting query did not run (permission not granted, or the
config switch off) or failed, every cell in them reads `(unavailable)` and the page says why
above the table. An empty cell must never be the way the operator learns that half the report did
not execute - that is Known Failure Class 2 in `.agents/repo-guidance.md`.

The export writes the rows currently on screen, with the active filters, and those rows are by
construction the **complete** matching set: a run that could not be completed within `MaxDevices`
renders no table and offers no export button at all (T3). So the CSV needs no truncation marker,
because a truncated CSV is never produced. That is the design, and it is the point - a truncated
export that looks complete is the failure mode that matters, and the way to prevent it is to not
have that state rather than to annotate it.

## Module descriptor

Added to `Modules/ModuleCatalog.cs` in `RegisterAll()`.

```csharp
new()
{
    Id = "DefenderEndpointDevices",
    DisplayName = "Defender for Endpoint Devices",
    Description = "List and export Microsoft Defender for Endpoint devices, including devices discovered on the network that can be onboarded but are not.",
    Route = "defender-endpoint-devices",
    // Reused deliberately, same reason as IntuneDevices and ServiceHealth: no device or shield
    // icon class exists in wwwroot/app.css today and tools/validate-module-package.ps1 rejects an
    // icon class it cannot find. Adding one is a separate change.
    IconCss = "bi bi-gear-fill-nav-menu",
    Category = ModuleCategories.Infrastructure,
    EnabledByDefault = false,
    IsSystemModule = false,
    Version = "1.0.0",
    // One permission, no granular tier: the module reads and exports and mutates nothing.
    MainPermission = new(
        "Access",
        "DefenderEndpointDevices",
        "Open the module and view or export the Defender for Endpoint device inventory, including network-discovered devices that are not onboarded, with their IP and MAC addresses, operating system and risk level. Read-only - grants no ability to change anything in Defender.",
        FailClosed: true),
    GranularPermissions = [],
    ConfigFields = [
        new("GraphDelineaSecretId", "Graph App Delinea Secret ID",
            "Secret Server secret containing Tenant ID, Application ID, and Client Secret fields (the app registration needs Machine.Read.All on WindowsDefenderATP, plus ThreatHunting.Read.All on Microsoft Graph if discovery sources are wanted)"),
        new("MaxDevices", "Maximum Devices",
            "Safety ceiling on one run. The module pages the API until the matching set is complete; if more devices match than this, it REFUSES the report rather than returning a partial one. Defaults to 20000. Raise it, or narrow the filters.",
            Required: false, DefaultValue: "20000"),
        new("IncludeDiscoverySources", "Include Discovery Sources",
            "Run the advanced hunting query that supplies the discovery sources, device type, vendor and model columns. Turn off when the app registration does not hold ThreatHunting.Read.All.",
            Required: false, DefaultValue: "true", FieldType: ConfigFieldType.Boolean)
    ]
}
```

Notes on the choices:

- **Fail-closed.** Device inventory with IP and MAC addresses, exposure levels and a map of which
  machines have no sensor is not address-book data. The 2026-06-30 classification's reasoning
  ("only data already visible in AD or the address book") does not transfer.
- **`EnabledByDefault = false`**, per the Constitution's optional-module rule.
- **No granular permission.** The module has exactly one capability. Read and export are the same
  act against the same data; splitting them would gate the clipboard, not the data. `BlockedSenders`
  and `BitLockerRecovery` export under their main permission too.
- **Category and display name** put it next to the other Infrastructure modules. Ordering is
  alphabetical by `DisplayName` and there is no sort field (`docs/AdminModuleSpec.md`).

### Read-alerting classification

The Constitution requires an administrator **alert** on reads by a module classified as a
security-response surface, and says that classification is a deployment decision rather than an
automatic property of the data.

This one is genuinely closer to the line than `IntuneDevices` was. The data is produced by a
security product and it is, in effect, a map of the gaps in the tenant's sensor coverage. Against
that: the module is an onboarding-gap report for IT operations, it exposes no alert, incident or
investigation content, and it cannot act on anything.

**Proposed classification: non-alerting. Audit is sufficient, and every read and every export
writes an audit record.** This is Q4 - one line from the owner overturns it, and doing so adds one
`SendAdminNotificationAsync` call on the read path and nothing else.

### Protected principals

Not applicable, and stated rather than silently omitted. The protected-principal guard binds to
**the target of a write** (`docs/ProjectConstitution.md`, Protected Principals). This module
performs no write, so there is no target to check. If a later plan adds isolate or offboard, that
plan must add the guard - a device action's principal would be the device's primary user, the
two-branch shape `docs/IntuneDeviceManagement-Plan.md` T4 works out.

## Design constraints and traps

### T1 - the shared `GraphTokenClient` cannot reach this API, and must not be taught to

`Services/GraphTokenClient.cs` hardcodes **both** the base URL (`GraphBaseUrl`,
`https://graph.microsoft.com/v1.0`, line 16) **and** the token scope
(`https://graph.microsoft.com/.default`, line 203). The Defender API needs a different host and a
different audience, so the shared client cannot call it as written.

**Resolution: a module-local `Services/DefenderApiClient.cs`, modelled on `GraphTokenClient` but
taking its base URL and token scope as constructor arguments instead of hardcoding them.** Not a
generalisation of the shared client, for two reasons: three other modules use that file, and
changing it is a shared infrastructure change that would bump the base app version - which the
"adding a module does not bump the base version" rule is specifically there to avoid.
`docs/IntuneDeviceManagement-Plan.md` S0 is the precedent for the other direction, and it bumped
the base version exactly because it touched this file.

**The discovery-sources call goes through a SECOND INSTANCE of that same module-local client, not
through `GraphTokenClient.PostAsync`.** The obvious-looking reuse does not work, and this is the
whole reason the client is parameterised rather than hardcoded to the Defender host:

- `GraphTokenClient.PostAsync` (`GraphTokenClient.cs:109-126`) returns a bare `JsonDocument?` and
  line 122 is `if (!response.IsSuccessStatusCode) return null;`. It discards the status code and
  the response body. Against it, a 403 for un-consented `ThreatHunting.Read.All`, a 429 for CPU
  quota and a 500 are all the same value - `null` - so T7's requirement that each render as a
  distinct named reason would be unimplementable and the S4 tests for 403 and 429 could not be
  written. This is exactly the defect `idm-1` recorded against `DeleteAsync` and
  `PostNoContentAsync`, on the one sibling method that never got the status-returning treatment.
- **The rejected alternative is to add `PostWithStatusAsync` to `GraphTokenClient`**, beside the
  existing `PatchWithStatusAsync`. It would be a two-line, entirely additive change and it is the
  tidier long-term fix - but `GraphTokenClient.cs` is shared infrastructure used by three other
  modules, so **it would bump the base app version** and contradict this plan's versioning
  section. Rejected on that ground alone, not on merit. If a future work stream needs a
  status-returning Graph POST for another module, that is the right time to add it there, with
  the base bump it implies.

So the module constructs the same client class twice from one Delinea secret:

| Instance | Base URL | Token scope |
| --- | --- | --- |
| Defender inventory | `https://api.security.microsoft.com` | `https://api.securitycenter.microsoft.com/.default` |
| Graph hunting | `https://graph.microsoft.com/v1.0` | `https://graph.microsoft.com/.default` |

Both expose `GetWithStatusAsync` and `PostWithStatusAsync` returning
`(JsonDocument? Document, HttpStatusCode StatusCode, string? SafeError)` - the shape
`PatchWithStatusAsync` already established. `SafeError` is produced by calling
`GraphTokenClient.ExtractGraphError` (`GraphTokenClient.cs:162`), which is `internal static` and
therefore reachable from another class in the same assembly **with a zero-line diff to that
file**: the sanitizer is reused, the shared file is not edited, and only the `error.code` /
`error.message` can escape - never a token or a raw body. Say all of this out loud in the
client's doc comment; two instances of one client class looks like an error otherwise.

**Timeouts are an exception, not a status code.** An `HttpClient` timeout surfaces as
`TaskCanceledException`, so `PostWithStatusAsync` catches it and returns a distinct timed-out
result rather than letting it escape as an unhandled exception. T7 requires timeout to be one of
the named reasons; without this it would be a stack trace instead.

### T2 - the token audience is the legacy hostname, and getting it wrong is a 403, not a 404

Scope: `https://api.securitycenter.microsoft.com/.default`. Endpoint host:
`https://api.security.microsoft.com`. They differ on purpose and Learn warns about exactly this
("requests fail with `403 Forbidden`, even if the API endpoint uses
`https://api.security.microsoft.com`").

A test pins the literal scope string sent in the token request, and a second pins the literal
request URL. A 403 in this module must therefore report "the app registration is missing
`Machine.Read.All`, or the token audience is wrong" rather than a bare failure, because those are
the two live causes and an operator cannot tell them apart from "failed".

Regional hosts (`us.`, `eu.`, `uk.`, ... `api.security.microsoft.com`) are documented as a
performance option. **This module uses the global host and nothing else.** A regional host is a
fact about where the tenant is, which is exactly what `.agents/repo-guidance.md` invariant 7
forbids baking into source. If latency ever justifies it, it becomes a config field then.

### T3 - the report is complete or it refuses; there is no quiet first-N

The owner asked to "List & Export **all** devices". A single bounded page silently redefines that
as "the first N devices", and a CSV that has been handed on to someone else carries no hint that
it was cut short. **Paging is therefore in this plan, in S1, not deferred.**

The API facts that are documented: `$top` has a maximum of 10,000, maximum page size is 10,000,
`$skip` is supported, and rate limits are 100 calls per minute and 1,500 per hour.

**The fact that is NOT documented, and that the whole design turns on: whether this endpoint
emits `@odata.nextLink` at all.** The only mention anywhere on the Defender for Endpoint API pages
is a Tip on `apis-intro` reading "When more than one query request is required to retrieve all the
results, **Microsoft Graph** returns an `@odata.nextLink` property in the response" - it names
Graph, on a Defender page, and links to Graph's paging article. That is not evidence about this
collection. R1(g) settles it before any multi-page behaviour ships.

### The completion rule

A run is complete only on a **positive proof of exhaustion**. Absence of evidence is never
treated as proof, and every ambiguous state takes the same exit as the ceiling: refusal.

- **P1 - single request.** The request asked for `$top = N`, the response returned **fewer than
  N** rows, and carried no `@odata.nextLink`. Complete.
- **P2 - cursor chain.** Every continuation was an `@odata.nextLink` followed as an absolute URL,
  and the final response carried no `@odata.nextLink` and was short in the P1 sense. Complete.
- **Refuse** when any of these holds: a response returns **exactly** the `$top` it was asked for
  and carries **no** `@odata.nextLink`; accumulating the next page would exceed `MaxDevices`; or
  any request in the chain fails. No table, no export button, and a message saying which.

The full-page-without-a-cursor case is the one worth spelling out, because it is the case that
looks like success: a response holding exactly as many rows as we asked for is indistinguishable
from a server that capped us. "Exactly N devices exist" and "the first N of more" produce byte-
identical responses. There is no way to tell them apart, so the module does not guess.

The mechanics:

1. Ask for `$top = min(MaxDevices + 1, 10000)` in one request, with the server-side
   `onboardingStatus` filter from D1. Asking for one more than the ceiling is what makes the
   ceiling test decisive in a single round trip: if `MaxDevices + 1` rows come back, more than
   `MaxDevices` match, and that is a refusal without a second call.
2. **If the response carries `@odata.nextLink`, follow it as an absolute URL.** The module-local
   client can do this and the shared `GraphTokenClient` cannot: `GraphTokenClient` prepends its
   base URL to whatever path it is handed (`GraphTokenClient.cs:35`), so concatenating an absolute
   `nextLink` onto it produces a broken request - the trap
   `docs/IntuneDeviceManagement-Plan.md` T1 and `docs/RiskyUsersModule-Plan.md` both pin. Because
   this client is module-local, the fix is available here without touching shared code:
   `GetWithStatusAsync` accepts either a relative path or an absolute URL, and a test asserts an
   absolute URL is sent unmodified.
3. Accumulate, **de-duplicating on device `id`**. This is cheap insurance against a duplicate row
   inside a cursor chain. **It is explicitly NOT a completeness argument** - see below.
4. Stop on P1, P2, or a refusal condition. Never on "the page looked short enough".

### Why `$skip` is not used - rejected alternative, with the argument

Revision 1 had a `$skip` fallback for the case where no `@odata.nextLink` comes back, and argued
that de-duplicating on `id` made it trustworthy. **That argument is wrong, and it is written out
here rather than deleted so the next person to reach for `$skip` hits the reasoning instead of a
silence:**

- **De-duplication removes overlaps. It cannot detect a gap.** The two failures are not
  symmetrical. If the window moves between requests so that pages overlap, the duplicate `id`s are
  visible and dropping them is correct. If the window moves the other way, the skipped devices
  leave no trace anywhere in the accumulated set - there is nothing to compare against, no
  sequence number, no gap in a key. The loop's own data cannot distinguish "these are all of
  them" from "these are the ones I happened to see".
- **A short page is exactly what a moved window produces.** So "page until a short page arrives"
  terminates identically on a complete collection and on a truncated one. The old step 3 used
  that as its stopping condition, which means the fallback path could accumulate a subset, declare
  the run complete, and hand S2 and S3 a list to render and export - the precise silent truncation
  the rest of this section promises cannot happen.
- **`$skip` is order-dependent and the machines collection documents no `$orderby` support.** With
  no stable order there is no stable window, and nothing in the documentation says otherwise.

`$skip` therefore stays out of the design. Reintroducing it needs **a cited source that `$skip`
paging is stable for this endpoint** - not an argument from de-duplication, which is the argument
that was already tried and does not hold.

### When the ceiling is hit, the listing FAILS - it does not truncate

The page renders no table and no export button, and says in words: more devices match than the
configured maximum of N, so the report would be incomplete; raise Maximum Devices in Module
Config, or narrow the filters. That is the loud outcome. A partial CSV that looks complete is the
one result this module must never produce, and `.agents/repo-guidance.md` Known Failure Class 2 -
never report blanket success over an incomplete run - is the rule it would break.

The ceiling exists at all for two reasons that are about the API and not about any particular
deployment: the endpoint allows 100 calls per minute, and an unbounded loop against a paged API is
how a read module becomes an outage. The request count follows from the page size rather than
from a number written here: at `$top = min(MaxDevices + 1, 10000)` the default ceiling of 20000
costs **one** request when the endpoint emits no cursor, and at most `ceil(MaxDevices / pageSize)`
when it does, where `pageSize` is whatever the service actually returns per page - which R1(g)
observes rather than this file asserting. Whether refusal or a clearly-marked partial is the right
behaviour at the ceiling is Q5.

Separately, and unchanged: a **`404`** from the first page is the documented empty result, not a
failure (T5).

### T4 - never call Get-machine-by-id

Learn's **Get machine by ID** page lists only `Machine.ReadWrite.All` under Application
permissions. Calling that endpoint would force a write scope onto the registration for a read-only
module. The detail view renders from the row the list already returned. **A source-level test
asserts no request path matching `/api/machines/{id}` is ever composed**, so this cannot be
reintroduced quietly by a later change that "just needed one more field".

### T5 - `404 Not Found` from the machines list means "no devices", not "broken"

Learn: "If successful, and the machines exist, you see `200 OK` with list of machine entities in
the body. If there are no recent machines, you see `404 Not Found`."

This inverts the usual rule. Everywhere else in this repo a non-success status must not render as
a benign empty result (`docs/IntuneDeviceManagement-Plan.md` T7); here, **404 specifically** is the
documented empty result and must render as "no devices matched", while 403, 429 and 5xx must
render as named failures. Both directions are asserted by tests, because getting either one wrong
produces a confident lie.

### T6 - the OData literal, the filter value, and case

Three separate hazards in one line of query string:

1. The filter value goes into an OData string literal. Any operator-supplied value (the device
   name box) must have embedded single quotes doubled and must then be escaped as a query
   parameter, value-only - **not** the whole expression, or the parentheses, commas and delimiting
   quotes reach the service percent-encoded and the filter silently stops meaning what it says.
   This is `docs/IntuneDeviceManagement-Plan.md` T4c and its 2026-09-03 revision, learned the hard
   way on a different endpoint. Tests cover a value containing `'`, one containing `&` and `#`, an
   empty value and an overlong one.
2. **Property-name casing is inconsistent in Microsoft's own documentation.** The machine resource
   table spells the property `onboardingstatus`; the filterable-properties list spells it
   `onboardingStatus`; the OData samples page spells other properties `ComputerDnsName`,
   `OsPlatform`, `HealthStatus` with leading capitals. **R1(b)** verifies the JSON property name
   the service actually returns and the casing the filter actually accepts, and records both here.
   The deserializer is configured case-insensitively regardless, so S1 can be written and tested
   against stubs before that answer exists.
3. **"All Windows devices" has no single filter value.** `osPlatform` carries `Windows10`,
   `Windows11` and the server variants as separate values, so `osPlatform eq 'Windows'` matches
   nothing. The module filters `onboardingStatus` server-side and applies the Windows test
   client-side as an ordinal case-insensitive `StartsWith("Windows")`, with the rule stated on the
   page and a "Windows only" toggle the operator can clear. `startswith(osPlatform,'Windows')`
   might work server-side - `startswith` is documented for `computerDnsName` - but it is not
   documented for `osPlatform` and this plan does not assume it. **R1(d)** tests it against the
   live service and, if it works, this section gets revised rather than the code getting clever.
   S1 ships the client-side rule, which is correct either way.

### T7 - the hunting query is a second, failable call and must fail visibly and narrowly

- It runs **once per refresh**, not once per device: one `DeviceInfo` query returning the whole
  latest-state set, merged into the device rows by `DeviceId` in memory.
- The query is a **constant in source**. No operator input is interpolated into KQL, ever. A test
  asserts the query text contains `DeviceInfo` and no other table name - the scope of
  `ThreatHunting.Read.All` is the whole hunting schema, and the only thing keeping this module
  inside `DeviceInfo` is the query text itself.
- Shape, following Learn's own published pattern for discovered devices:
  `DeviceInfo | summarize arg_max(Timestamp, *) by DeviceId | where isempty(MergedToDeviceId) | project DeviceId, DiscoverySources, DeviceType, DeviceCategory, Vendor, Model`.
  `MergedToDeviceId` is not decoration: without it, merged and invalidated duplicate records come
  back and one physical device appears twice.
- Its failures are **independent of the device list**. A 403 (permission not consented), a 429
  (CPU quota) or a timeout leaves the device list fully rendered with the five enrichment columns
  reading `(unavailable)` and a warning naming the reason. It must never take the whole page down,
  and it must never leave those columns looking merely blank.
- **Naming those three reasons apart requires the status code and the sanitized error, which the
  shared `GraphTokenClient.PostAsync` throws away** (`:122` returns `null` on every non-success).
  The call therefore goes through the module-local client's `PostWithStatusAsync` - see T1, which
  records why, and records that fixing the shared client instead would bump the base app version.
- **Timeout.** The `"MicrosoftGraph"` named `HttpClient` in `Program.cs:128-132` has a 30-second
  timeout. A hunting query may legitimately run longer - Graph times a single request out at three
  minutes. So the hunting call uses its own named client with a longer timeout registered for this
  module. Reusing the 30-second one would produce an intermittent, load-dependent failure that
  looks like a service fault. The timeout arrives as `TaskCanceledException`, not as a status
  code, and is mapped to its own reason (T1).
- Graph hunting quotas: 30 days of data, up to 100,000 rows, at least 45 calls per minute per
  tenant, 50 MB result cap, `429` when CPU quota is exhausted. None of these bite at one query per
  page refresh, but a future auto-refresh would need to respect them.

### T8 - ASCII only, and the count that another file owns

Source files must stay pure ASCII - CI fails on any non-ASCII character in a tracked `.cs` or
`.ps1`, comments included. And `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs:17` asserts the total
module count, with a second assertion over the configurable policy aliases; adding this module
moves both. **Read the current numbers from those files at the time of the change** - this plan
deliberately does not copy them, per the `.agents/playbooks/drift.md` rule that a count another
file owns is pointed to, not duplicated.

## Slices

Each slice is one commit, one session, and must build and test green on its own. Boundaries are
drawn on compilation order, not on conceptual grouping.

### S1 - API client, models, read-only service

- `Services/DefenderApiClient.cs`: base URL and token scope taken as constructor arguments, not
  hardcoded (T1), so one class serves both the Defender host and Graph. Client-credentials token
  cached with the same lock-and-expiry shape as `GraphTokenClient`. Exposes `GetWithStatusAsync`
  (accepting a relative path **or** an absolute `@odata.nextLink`) and `PostWithStatusAsync`, both
  returning `(JsonDocument? Document, HttpStatusCode StatusCode, string? SafeError)`, with
  `TaskCanceledException` mapped to a distinct timed-out result. `SafeError` comes from
  `GraphTokenClient.ExtractGraphError`, called as an `internal static` member with no edit to that
  file. Never logs the token, the secret or a raw auth response body.
- `Models/DefenderDeviceModels.cs`: `DefenderDevice`, `DefenderDeviceIpAddress`,
  `DefenderDeviceListResult` (`Devices`, `DistinctCount`, `PagesFetched`, `CeilingExceeded` +
  the ceiling that was exceeded, `DiscoveryEnrichment` state + reason). **There is deliberately
  no `Truncated` flag**: the only two outcomes are a complete set and a refusal (T3), and a flag
  that says "some of the data" is how a partial result gets rendered as a table by the next
  person to touch the page.
- `Services/DefenderEndpointDeviceService.cs`: `GetApiClientAsync()` in the exact shape of
  `ServiceHealthService.GetGraphClientAsync()` (`ServiceHealthService.cs:50-71`) but reading
  `DefenderEndpointDevices` / `GraphDelineaSecretId` and never another module's config;
  `IsAvailable`; `ListDevicesAsync(filters)`. 404 on the first page handled per T5, other
  non-success per T7's reporting rule, and the **P1/P2 completion rule with the `MaxDevices`
  refusal per T3**.
- **Every test in S1 runs against a stub `HttpMessageHandler`. S1 makes no live call** - the
  client, the response parser and the query builder are all exercised against canned responses,
  and the live questions belong to R1.
- Paging tests are the load-bearing ones in this slice:
  - a cursor chain - two pages joined by `@odata.nextLink`, the second short and cursor-free -
    completes, and the absolute URL is asserted to be sent unmodified;
  - a single short cursor-free page completes (P1);
  - a duplicate row spanning two pages is de-duplicated on `id`;
  - a chain that would exceed `MaxDevices` **refuses**;
  - **the guard test: a full page - exactly the `$top` that was asked for - carrying no
    `@odata.nextLink` must produce a refusal, not a list.**
- **Why that guard is not vacuous, which is the part worth keeping.** Against the committed
  design (`b12a7b1`), that same stubbed response falls into the old step 3's `$skip` fallback,
  pages until something short comes back, and renders a list - so the test **fails on the design
  this revision replaces** and passes only after it. It is the executable form of the gapped-
  `$skip` scenario the reviewer asked for: once `$skip` is gone that scenario is unreachable, and
  a full page with no cursor is the state that would otherwise let an unprovable result through.
- `Program.cs`: `AddSingleton<DefenderEndpointDeviceService>()` beside the other module services,
  plus the named `HttpClient` the module needs. Additive registration for one module; it is part
  of adding a module and does not make this a shared-infrastructure change.
- Tests with a **slice-local** stub `HttpMessageHandler`. Do not reference
  `GraphTokenClientTests.StubHandler` - it is `private sealed` and unreachable from another test
  class.

Nothing user-reachable ships in S1: no catalog entry, so no route and no page.

**S1 performs no live call, and cannot.** This was wrong in Revision 0, which put the live
reconnaissance here. Module config is descriptor-driven: `Components/Pages/ModuleConfig.razor:73`
and `:332` render the Configuration tab only `@if (module.ConfigFields.Count > 0)`, and the
config page lives at `/module-config/{ModuleId}` for catalogued modules
(`docs/AdminModuleSpec.md`, Configuration). The descriptor arrives in S2, so until S2 lands there
is **no supported way for anyone to enter the Secret ID** - and therefore no way for the S1
service to obtain a credential. Writing the row into the SQLite store by hand is not a supported
path and this plan does not propose it. The reconnaissance moves to R1, below.

Nothing in S1's code depends on a reconnaissance answer: every open question has a documented
default and a conservative fallback already chosen (client-side Windows matching rather than an
undocumented `startswith`; case-insensitive deserialization; `ipAddresses` treated as optional).
R1 confirms or triggers a revision; it does not unblock the writing.

### S2 - catalog entry, page, read-only table

- The descriptor above.
- `Components/Pages/DefenderEndpointDevices.razor`:
  `@attribute [Authorize(Policy = "DefenderEndpointDevices")]`, the `OnInitializedAsync`
  authorization re-check, `<ModuleVersion />` inside the `<h1>` (required, and enforced by
  `tools/validate-module-package.ps1`), the two filter controls (onboarding status, Windows only),
  a results table and a detail panel rendered from the already-fetched row (T4).
- The ceiling refusal, unavailability and every failure state rendered in words, never as an
  empty table and never as a short one.
- Read auditing via `Audit.LogLookupAction`.
- Updates the catalog count and alias assertions in `ModuleCatalogTests.cs` (T8).
- Ships `EnabledByDefault = false` and fail-closed, so landing S2 makes the module *configurable*
  on dev without making it reachable by anyone who has not been granted it. That is what makes
  "land S2, then do the live reconnaissance" safe.

### R1 - live reconnaissance (a gate between S2 and S3, not a code slice)

No commit of its own beyond the revision it writes into this file. It runs on dev, after S2 is
deployed, the app registration exists and the Secret ID has been entered on the module's own
config page. Its answers are recorded **in this file** before S3 starts.

- (a) Does `GET /api/machines` return `CanBeOnboarded` devices at all? **This is the assumption
  the whole module rests on** (see Assumptions 2). If it is false, the device list has to come
  from advanced hunting instead, `ThreatHunting.Read.All` stops being optional, and that goes back
  to the owner as a changed permission ask - it is not absorbed.
- (b) The JSON casing of `onboardingStatus` as returned, and whether `$filter` accepts either
  casing (T6.2).
- (c) The distinct `osPlatform` values present, confirming the `StartsWith("Windows")` rule
  (T6.3).
- (d) Whether `startswith(osPlatform,'Windows')` is accepted server-side. If yes, this file is
  revised and the filter moves server-side; the code does not get clever ahead of the answer.
- (e) Whether `ipAddresses` is populated on list responses (Assumptions 3). If not,
  `IpAddresses` and `MacAddresses` leave the CSV before S3 writes it.
- (f) Whether the result set on this deployment approaches `MaxDevices`, and therefore whether
  the ceiling default needs raising before anyone relies on the report. Asked because T3 refuses
  rather than truncates, so the failure mode is visible rather than silent - this is verifying an
  environment fact, which `.agents/repo-guidance.md` invariant 7 requires, not resting a design
  on an assumed one.
- **(g) The paging contract. This is the decisive one and it has a designed experiment, because
  no Learn page states the answer (T3).** Issue `GET /api/machines?$top=1` with a filter known to
  match more than one device, and record two things: (i) does the response carry
  `@odata.nextLink`, and (ii) does following that link return a *different* device?

  Both branches are decided in advance, so R1(g) records a fact and does not reopen a design
  argument:

  - **Cursor present and it advances** - the endpoint supports cursor paging. P2 applies,
    multi-page runs ship, and the observed per-page row count is recorded here so the request
    budget in T3 can be computed rather than guessed.
  - **No cursor** - the endpoint has no continuation token. The module stays **single-request**,
    asking for `$top = min(MaxDevices + 1, 10000)`, completing on P1 and refusing on a full page
    exactly as T3 says. `MaxDevices` above 10,000 then cannot be satisfied and the refusal message
    says so. This file is revised to state that the endpoint emits no cursor, so the next reader
    does not re-litigate it.

  **No multi-page behaviour ships that R1(g) has not observed.** Following a cursor that has never
  been seen to exist is speculative code on a read path that must not silently under-report, and
  the single-request branch is correct-but-limited rather than wrong. If R1(g) cannot be run - no
  tenant has more than one matching device, say - that is recorded as "not established" and the
  single-request branch ships, because the ambiguous case refuses anyway.

### S3 - CSV export (starts after R1's answers are recorded)

- `BuildCsv` as a `static internal` method taking the row list, so tests call it without a page
  instance (`DhcpAuthorization.razor:174` is the pattern), over `CsvExport.Write`.
- `DownloadCsvAsync` through the existing `downloadFile` JS interop, timestamped filename, audited
  as `ExportCsv` with the row count, audit failure caught and logged without failing the export.
- Tests: header order; a device name containing a comma, a quote and a newline round-trips; a
  value starting with `=` is neutralised; the multi-value join; a row count mismatch throws.

### S4 - discovery sources enrichment (conditional on Q1)

- `GetDiscoveryEnrichmentAsync()` over **the module-local client's `PostWithStatusAsync`** against
  a Graph-configured instance (T1), to `/security/runHuntingQuery`, with the constant KQL and its
  own longer-timeout named client (T7). **Not `GraphTokenClient.PostAsync`** - that method returns
  `null` for every non-success status (`GraphTokenClient.cs:122`) and the two tests below could
  not be written against it.
- Merge by `DeviceId`; the five enrichment columns; `(unavailable)` plus an on-screen reason on
  403, 429, timeout, or the config switch being off.
- Tests: successful merge; a device present in the list and absent from the hunting result; a
  device present in the hunting result and absent from the list (dropped, not injected); 403 named
  as un-consented `ThreatHunting.Read.All`; 429 named as quota; a `TaskCanceledException` named as
  a timeout; malformed response; switch off. Each must produce a **distinct** reason string -
  collapsing any two of them must fail a test. Plus the query-text assertion from T7.
- **This slice is droppable.** If the owner declines `ThreatHunting.Read.All` (Q1), S4 is not
  built, `IncludeDiscoverySources` leaves the descriptor, and the five columns leave the CSV. That
  is why it is last before documentation and not folded into S1.

### S5 - documentation and version

- `docs/DefenderEndpointDevices.md` in the shape of `docs/BlockedSenders.md`: purpose, operators,
  permissions table, config table, credentials, the app-registration requirements from this plan,
  audit actions, fail-closed table, manual validation, rollback, and an explicit "not in this
  module" list matching Out of scope.
- README section.
- `.agents/state.md` entry (owner/orchestrator territory, not written by the implementing slice
  without saying so).
- Module version `1.0.0` confirmed in the descriptor. **No base app version bump** - see
  Versioning.

## Verification

Per `.agents/repo-guidance.md`:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx` (always the `.slnx`; bare `dotnet test` from the repo root
  runs zero tests)
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`

No PowerShell changes are planned, so PSScriptAnalyzer and Pester are not expected to be relevant;
if a slice does touch a `.ps1`, both run.

**Non-vacuity, per the repo standard.** For each of: the 404-is-empty mapping (T5), the
`MaxDevices` refusal and the full-page-without-a-cursor refusal (T3), the token scope literal
(T2), and the three distinct enrichment-failure reasons (T7) - revert the
behaviour, confirm the specific test fails by name, restore, confirm green. Confirm the revert
actually landed on disk before trusting the verdict, and **touch the file after restoring**: a
`Copy-Item` restore carries the backup's timestamp and MSBuild will happily keep testing the
mutant (`.agents/state.md`, mutation-probe timestamp trap).

**What tests cannot cover here, stated so nobody reads green as proof.** There is no bUnit harness,
so nothing in this repo renders the page. The page authorization re-check, the ceiling-refusal
wording, the `(unavailable)` banner, the Windows-only toggle and the export button's gating - in
particular that the button is **absent** in the refusal state - are all unproven by the suite. The manual checklist below is the only evidence for them.

## Manual acceptance checklist

Run after the first dev deploy that follows the app registration being created. Nothing here is
automatable today.

1. With `GraphDelineaSecretId` unset, the module reports unavailable in words - not an empty
   table.
2. Set the Secret ID. The device list loads and the module version renders beside the heading.
3. The default view is Windows devices with onboarding status "can be onboarded". The count is
   plausible against the Defender portal's own **Onboarding status: Can be onboarded** filter -
   and a difference against the *onboarding recommendation widget* is expected, not a defect.
4. Clear the "Windows only" toggle: non-Windows discovered devices appear.
5. Switch the onboarding-status filter to onboarded: onboarded machines appear, with healthy
   sensor states.
6. Spot-check one device against its portal page: FQDN, OS, last IP, MAC, first and last seen.
7. Discovery sources are populated for at least one device, and the values look like the portal's
   ("MDE", and whatever else this tenant has).
8. Export. The CSV opens in Excel with the header intact, one row per on-screen device, and a
   device name containing a comma is intact in its own cell.
9. Audit log shows the lookup and the export, with the export's row count.
10. Point the Secret ID at a registration **without** `Machine.Read.All`: the page reports a
    permission failure naming the missing grant, not an empty device list.
11. Turn `IncludeDiscoverySources` off: the five columns read `(unavailable)` with the reason, and
    the rest of the report is unaffected. Then point at a registration without
    `ThreatHunting.Read.All` with the switch on, and confirm the same behaviour with a different
    stated reason.
12. Sign in as a user in no section-access group for `DefenderEndpointDevices`: `/defender-endpoint-devices`
    denies by **direct URL**, not merely by a hidden nav link.
13. Disable the module in Admin Settings: it disappears from nav and the direct URL denies.
14. **The ceiling, which is the check that proves T3.** Set `MaxDevices` to something smaller than
    the number of devices that match the default filters - a handful will do. The page must refuse
    in words, name the ceiling, and show **no table and no export button**. Restore the value and
    confirm the full report returns. A short table, or an export button offered in the refusal
    state, is the failure this check exists to catch.
15. Compare the row count against the same filters in the Defender portal's device inventory. They
    should agree, modulo the update-frequency caveat Learn states. A count that stops suspiciously
    near a round number is the signature of paging that stopped early.

## Versioning when this lands

- New module `DefenderEndpointDevices` at `1.0.0`.
- **No base app version bump.** Adding a new module is not a shared-infrastructure change
  (`docs/ProjectConstitution.md`, Deployment And Versioning; `.agents/decisions.md` 2026-07-21).
  This plan is deliberately built to keep that true: the new API client is module-local precisely
  so that `Services/GraphTokenClient.cs` is not touched (T1). **If a slice ends up modifying any
  shared file, the base version bumps and that slice says so in its commit** - the two rules fire
  independently and each has been assumed-from-the-other before in this repo.
- **The call still holds after Revision 1.** Closing finding 1 could have been done by adding
  `PostWithStatusAsync` to the shared `GraphTokenClient`, which would have bumped the base
  version; the plan instead parameterises the module-local client and reuses the shared
  `internal static ExtractGraphError` **without editing that file**. Net diff to shared
  infrastructure: zero. Module `1.0.0`, no base bump, unchanged.
- One thing to watch at implementation time, since it is the only way this call flips: reading an
  `internal` member is not a change to the file that declares it, but *moving* or *widening* that
  member would be. If `ExtractGraphError` has to be touched at all, stop and re-read this section.
- All slices before the first deploy means the module ships once at `1.0.0`. A behaviour change
  after a deploy bumps the module version only.

## Environment neutrality

`.agents/repo-guidance.md` invariant 7, checked deliberately rather than assumed:

- **No tenant identifier anywhere.** The tenant ID, application ID and client secret are read from
  the Delinea secret at call time. There is no default tenant, no tenant in `appsettings.json`, no
  tenant in a test fixture that the code could fall back to, and none in this plan.
- **No domain, host, OU, group or address is named as behaviour.** `DnsDomain` in the CSV is
  *derived* from whatever `computerDnsName` the API returns; the module does not know, default or
  validate any particular suffix.
- **No regional API host.** The global `api.security.microsoft.com` is used. A regional host would
  be a fact about where this tenant sits (T2).
- **No safety argument rests on this environment's shape.** The module is safe because it holds
  one read-only scope and composes no mutating request - not because of anything about this
  forest, this tenant or this network. If a reviewer's answer to "why is this safe?" ever contains
  a local fact, the fix is not done.

## Assumptions - not verified against live documentation

Listed separately so none of them is mistaken for a citation.

1. **`DeviceInfo.OnboardingStatus`'s literal for the can-be-onboarded state.** Learn's published
   queries only ever compare against `"Onboarded"`. The portal label is "Can be onboarded" and the
   REST value is `CanBeOnboarded`; the KQL column is *assumed* to carry the display-string form.
   The module does not depend on it - the hunting query filters nothing on onboarding status and
   merges by `DeviceId` - which is a deliberate way of not betting on an unverified literal.
2. **`GET /api/machines` returns `CanBeOnboarded` devices.** Inferred from three documents (see
   "The 'Can be onboarded' state"); no single Learn sentence says it outright. **R1(a)** proves
   it. The fallback if it is false changes the permission ask, so it goes back to the owner rather
   than being absorbed.
3. **`ipAddresses` is present on list responses.** The OData samples page shows it on machine list
   responses; the List machines and Get machine by ID reference pages both omit it from their own
   examples. Assumed present; verified by **R1(e)**, before S3 writes the CSV. If it is not,
   `MacAddresses` and `IpAddresses` leave the CSV and only `lastIpAddress` survives.
4. **`LoggedOnUsers` is empty for non-onboarded devices.** Reasoning, not documentation: there is
   no sensor on such a device. The column is not in the CSV, so nothing breaks if the reasoning is
   wrong.
5. **`ThreatHunting.Read.All`'s portal display name is 'Run hunting queries'.** The permission name
   itself is verified from the Graph reference and the migration table; the exact string the
   consent screen shows was not read from a Learn page. **Grant by permission name, not by display
   string.**
6. **`startswith` on `osPlatform`** is not documented. Treated as unknown; **R1(d)** tests it
   against the live service, and S1 ships the client-side rule that is correct either way (T6).
7. **Rate limits are per tenant, not per application.** Assumed. At `PageSize` 1000 a default-
   ceiling run costs 20 requests against a 100-per-minute limit, so there is headroom either way;
   it would matter if this ever polls or if two operators refresh together.
8. **The paging contract.** Whether `GET /api/machines` emits `@odata.nextLink` is **not
   documented** - the only mention on the Defender API pages names Microsoft Graph, not this
   collection (T3). Nothing is assumed: R1(g) probes it with `$top=1` and both branches are
   pre-decided. The one inference the design does rest on is that **the service honours `$top`
   and returns fewer rows than asked for only when the collection is exhausted** (rule P1). That
   cannot be proven from the documentation either, which is precisely why a **full** page with no
   cursor refuses instead of completing: the ambiguous case is routed away from the inference
   rather than through it. `$orderby` is likewise undocumented on this collection, which is one of
   the three reasons `$skip` is not used at all (T3).

## Open questions for the owner

1. **Discovery sources cost a tenant-wide hunting permission. Grant it?** The only way to get them
   is `ThreatHunting.Read.All` on Microsoft Graph, which lets the app read every advanced hunting
   table in the tenant (email, identity and cloud-app events, not just devices), and its consent
   needs a Privileged Role Administrator or Global Administrator. Yes (build S4) or no (drop the
   five columns, and an Application Administrator can do the whole consent)?
2. **One app registration for both permissions, or two?** This plan assumes one registration and
   one Delinea secret carrying both grants. Two would isolate the broad hunting permission from the
   device read, at the cost of a second secret and a second config field.
3. **Should the CSV export require a ticket number?** The export leaves the app carrying IP and MAC
   addresses for unmanaged machines. `BitLockerRecovery` requires a ticket to search; the other
   export modules do not. Require one, or not?
4. **Is this module a security-response surface?** If yes, every read sends an administrator alert
   on top of the audit record (Constitution, Notifications). Proposed answer is no - audit only.
   Confirm or overturn.
5. **At the `MaxDevices` ceiling: refuse the whole report, or return a clearly-marked partial
   one?** The plan proposes refuse - no table, no export, and a message naming the ceiling - on
   the grounds that a partial CSV handed to someone else looks complete. The alternative is a
   partial report stamped as partial on screen and in a CSV column. Which?
6. **Module display name: "Defender for Endpoint Devices"?** It sets the nav label, the route
   `defender-endpoint-devices` and the section-access alias `DefenderEndpointDevices`. Shorter
   alternatives ("Defender Devices") are cheaper to type and easier to confuse with Defender
   Antivirus.

## Sources

All fetched 2026-09-18.

- Machine resource type (properties, `onboardingStatus` enumeration):
  https://learn.microsoft.com/en-us/defender-endpoint/api/machine
- List machines API (URL, permissions, `$filter` properties, `$top`/`$skip`, limits, 404-on-empty):
  https://learn.microsoft.com/en-us/defender-endpoint/api/get-machines
- Get machine by ID (documents only `Machine.ReadWrite.All` - the reason T4 exists):
  https://learn.microsoft.com/en-us/defender-endpoint/api/get-machine-by-id
- OData queries with Defender for Endpoint (filterable properties, `startswith`, `ipAddresses`
  with `macAddress` in list responses):
  https://learn.microsoft.com/en-us/defender-endpoint/api/exposed-apis-odata-samples
- Create an app to access Defender for Endpoint without a user (WindowsDefenderATP, client secret,
  token endpoint and scope, the legacy-audience 403 warning, redirect URI only for multitenant):
  https://learn.microsoft.com/en-us/defender-endpoint/api/exposed-apis-create-app-webapp
- Access the Defender for Endpoint APIs (application context, no machines-API deprecation notice):
  https://learn.microsoft.com/en-us/defender-endpoint/api/apis-intro
- Advanced Hunting API, Defender for Endpoint (the retirement notice):
  https://learn.microsoft.com/en-us/defender-endpoint/api/run-advanced-query-api
- Use the Microsoft Graph security API (what Graph exposes and does not; advanced hunting
  migration table; quotas; the 2027-02-01 date):
  https://learn.microsoft.com/en-us/graph/api/resources/security-api-overview
- security: runHuntingQuery (v1.0, `ThreatHunting.Read.All`, request and response shape):
  https://learn.microsoft.com/en-us/graph/api/security-security-runhuntingquery
- DeviceInfo table (`DiscoverySources`, `OnboardingStatus`, `DeviceType`, `Vendor`, `Model`,
  `MergedToDeviceId`): https://learn.microsoft.com/en-us/defender-xdr/advanced-hunting-deviceinfo-table
- Explore devices in the device inventory ("Can be onboarded" filter; the single-authoritative-
  source caveat): https://learn.microsoft.com/en-us/defender-endpoint/machines-view-overview
- Devices in Microsoft Defender for Endpoint (discovery sources column and its values):
  https://learn.microsoft.com/en-us/defender-endpoint/devices-overview
- Review and assess devices (onboarding-status table and definitions, the count caveat, the
  API-filter tip, the published discovered-device queries and `SeenBy()`):
  https://learn.microsoft.com/en-us/defender-endpoint/assess-devices
- Device discovery overview (modes, monitored networks, what is discovered):
  https://learn.microsoft.com/en-us/defender-endpoint/device-discovery
- Grant tenant-wide admin consent (which roles may consent; the Microsoft Graph app-role
  exception): https://learn.microsoft.com/en-us/entra/identity/enterprise-apps/grant-admin-consent

## Revision 1 - codex review, 2026-09-18

Reviewer: codex, `@azure-openai-eus2-global/gpt-5.5-dzs` at xhigh, standard depth, capability
proof passed. Verdict **sound_with_changes**, three findings. Every finding was re-verified
against the named files before being acted on, per the repo rule that disputes are settled by
reading the file. All three were confirmed and all three are closed. Nothing was rejected; one
was accepted with a recorded correction to part of its reasoning, and that correction is written
here rather than left in a chat log, because only repo files are durable memory.

**Finding 1 (HIGH) - S4 could not report Graph hunting failures with the planned call shape.
ACCEPTED, confirmed.** `GraphTokenClient.PostAsync` is `GraphTokenClient.cs:109-126`, and line 122
is `if (!response.IsSuccessStatusCode) return null;` - status code and body both discarded. The
plan's T7 demanded distinct reasons for 403, 429 and timeout, and S4 planned the call over that
method; those two statements could not both be satisfied. **Changed:** T1 rewritten - the
module-local client now takes base URL and token scope as constructor arguments and is
instantiated twice (Defender host, Graph host), exposing `PostWithStatusAsync` alongside
`GetWithStatusAsync`, both returning document plus status plus sanitized error, with
`TaskCanceledException` mapped to a distinct timed-out result because a timeout is an exception
and not a status. S4 rewritten to use it, with one test per distinct reason. **Route chosen, as
the reviewer asked to be told explicitly:** module-local, **not** a new `PostWithStatusAsync` on
the shared `GraphTokenClient`. The shared route is tidier and is recorded in T1 as the rejected
alternative with its reason - it is shared infrastructure used by three other modules and would
bump the base app version. The sanitizer is still reused: `ExtractGraphError` is
`internal static` (`GraphTokenClient.cs:162`) and is callable from the same assembly with a
zero-line diff to that file. **The versioning call is unchanged and now says so in its own
section: module `1.0.0`, no base app bump.**

**Finding 2 (HIGH) - the one-page fetch quietly narrowed "all devices" to "first N". ACCEPTED on
the substance; one part of the reasoning corrected.** The correctness gap was real and was the
most serious of the three: Scope promised the tenant's devices and an export of the result set,
while T3 fetched one bounded page and deferred paging, so a large result set would have produced
a CSV that looked complete and was not. **Changed:** paging is now in S1, not deferred -
`@odata.nextLink` followed as an absolute URL (which the module-local client can do and the
shared one cannot), `$skip` as the fallback, de-duplication on device `id` because the collection
documents no `$orderby` and `$skip` pages can therefore overlap, and a `MaxDevices` ceiling whose
behaviour is **refusal, not truncation**: no table, no export button, and a message naming the
ceiling. The config field `DeviceFetchLimit` (5000) became `MaxDevices` (20000) with that
semantics; the model has no `Truncated` flag at all, deliberately, so that a partial state does
not exist to be rendered later. Scope, the CSV section, the verification non-vacuity list and
manual checks 14 and 15 were updated to match. **The correction:** the reviewer read old open
question 5 as breaching `.agents/repo-guidance.md` invariant 7. Invariant 7 forbids naming this
environment in **source** as behaviour, and forbids a **safety argument** resting on this
environment's shape; it explicitly requires the opposite of silence about environment facts -
"Where an environment fact is unavoidable, verify it and fail closed rather than assume it." A
plan asking the owner how many devices they have is verification, not assumption, and was not a
breach. It was reworded anyway, because the better answer made the question redundant: Q5 now
asks about ceiling *policy* (refuse or mark partial) and names no deployment, and R1(f) verifies
the actual count on dev. The distinction is recorded so a future reader does not "fix" R1(f) back
out on invariant-7 grounds.

**Finding 3 (MEDIUM) - S1's live proof depended on config S1 does not register. ACCEPTED,
confirmed.** Module config is descriptor-driven: `Components/Pages/ModuleConfig.razor:73` and
`:332` gate the Configuration tab on `module.ConfigFields.Count > 0`, and the page is
`/module-config/{ModuleId}` for catalogued modules. With no descriptor until S2, there was no
supported way to enter the Secret ID, so S1's "live tasks, blocking S2" were unreachable.
**Changed:** the reviewer's second option, with the reason recorded. S1 is explicitly
call-free and says why; the reconnaissance moved into a new gate **R1**, sited between S2 and S3,
run on dev after S2 ships `EnabledByDefault = false` and fail-closed. The first option - a
config-only descriptor in S1 - was not taken because it would register a route with no page and
`tools/validate-module-package.ps1` requires a razor page carrying `<ModuleVersion />`
(`validate-module-package.ps1:296-297`). S1 was also given the sentence that makes the ordering
safe: nothing in S1's code depends on a reconnaissance answer, because each open question already
has a documented default and a conservative fallback.

**Unchanged by this revision:** `Status: Draft`, the API recommendation and its evidence, the
permission ask, the `CanBeOnboarded` literal, the field mapping and what is not obtainable, and
all six open questions except Q5's wording. No source file was touched; this plan file is still
the only artifact.

## Revision 2 - codex round 2, 2026-09-18

Same reviewer and configuration. Verdict on Revision 1: **unsound**, two findings. Both were
re-verified against this file before being acted on; both were confirmed exactly as described,
and both are closed here. Revision 1's record above is left intact - it is the history of what
round 1 changed, and round 2 upheld two of its conclusions.

**Carried forward from round 1, independently confirmed by round 2:** finding 1 is closed
(`PostAsync` collapses non-success to null; `ExtractGraphError` is `internal static` and reachable
without editing that file; the module-only version call is sound **provided
`Services/GraphTokenClient.cs` is not touched**). Finding 3's validator reasoning is confirmed -
`validate-module-package.ps1` accumulates `CAT002`, `PAGE001` and `PAGE009` through `Add-Issue`
and exits non-zero, so the config-only-descriptor alternative really was closed. **And the
invariant-7 rebuttal recorded in Revision 1 was upheld:** asking about or verifying an unavoidable
environment fact is permitted; resting a safety argument on this deployment's shape is not. That
reasoning stays in this file deliberately.

### Finding A (HIGH) - the `$skip` fallback could still export a silently partial list. ACCEPTED

Finding 2 was closed on the main path only. Revision 1's T3 step 3 read "If no `@odata.nextLink`
is returned, fall back to `$skip` paging until a short page arrives", conceded that pages "can in
principle overlap or gap", and then concluded that de-duplicating on `id` was "the only thing
making the `$skip` fallback trustworthy". **That conclusion does not follow, and the sentence was
wrong.** The corrected reasoning is now written into T3 under "Why `$skip` is not used" rather
than quietly deleted, because the next person to reach for `$skip` needs to meet the argument:

- de-duplication removes overlaps and **cannot detect a gap** - skipped devices leave no trace in
  the accumulated set, so the loop's own data cannot tell "all of them" from "the ones I saw";
- **a short page is exactly what a moved window produces**, so "page until a short page arrives"
  terminates identically on a complete collection and a truncated one;
- `$skip` is order-dependent and this collection documents no `$orderby`.

**Changed:** `$skip` is removed from the design entirely and recorded as a named rejected
alternative; reintroducing it requires a cited source that `$skip` paging is stable for this
endpoint, not an argument from de-duplication. De-duplication is demoted to cheap insurance
against a duplicate row inside a cursor chain and is explicitly labelled **not a completeness
argument**. The request-count sentence now derives from the page size instead of restating a
stale "20 requests". A guard test was added to S1 - a full page carrying no `@odata.nextLink` must
refuse - together with the note proving it bites: against the committed design (`b12a7b1`) that
same stubbed response falls into the `$skip` fallback and renders a list, so the test fails before
this change and passes after. It is the executable equivalent of the gapped-`$skip` scenario the
reviewer asked for, which becomes unreachable once `$skip` is gone.

**The completion rule, in full, so it does not have to be reconstructed:** a run is complete only
on a positive proof of exhaustion, and absence of evidence is never proof.

- **P1 - single request.** Asked for `$top = N`, fewer than N rows returned, no
  `@odata.nextLink`. Complete.
- **P2 - cursor chain.** Every continuation followed an `@odata.nextLink` as an absolute URL, and
  the final response carried none and was short in the P1 sense. Complete.
- **Refuse** when a response returns exactly the `$top` it asked for and carries no
  `@odata.nextLink`; when the next page would exceed `MaxDevices`; or when any request in the
  chain fails. No table, no export button.

A full page with no cursor is byte-identical whether exactly N devices exist or the server capped
us, so the module refuses rather than guessing. That is why it cannot silently under-report: the
only route to a rendered table is a proof, and every ambiguous state exits through the same
refusal the ceiling already uses.

One dependency this exposed and did not paper over: **whether this endpoint emits
`@odata.nextLink` at all is undocumented** - the only mention on the Defender API pages names
Microsoft Graph. So **R1(g)** was added: probe `$top=1` against a filter matching more than one
device, with both branches pre-decided (cursor present, P2 applies and multi-page ships; cursor
absent, single-request at `$top = min(MaxDevices + 1, 10000)` with the full-page refusal, and this
file revised to say so). No multi-page behaviour ships that R1(g) has not observed. Assumption 8
was rewritten from the old `$orderby` note to state the paging contract and the one inference P1
rests on.

### Finding B (MEDIUM) - stale references to the pre-R1 slice order. ACCEPTED

Revision 1 created the R1 gate but left five sentences still assigning live proof to S1 - Known
Failure Class 4, and two rounds of edits is how it got in. All five are repointed: the
endpoint-coverage proof to R1(a), the JSON/filter casing to R1(b), server-side `startswith` to
R1(d), and `ipAddresses` to R1(e), in both the body and the Assumptions list. A sixth was found
on the sweep the reviewer asked for and fixed too - Assumption 6 said `startswith` was "tested
live" without naming the gate. S1 now states positively that **every one of its tests runs against
a stub and it makes no live call**, and a missing blank line that ran a paragraph into a bullet
list was repaired.

**Unchanged by this revision:** `Status: Draft`, the API recommendation, the permission ask and
the least-privilege analysis, the `CanBeOnboarded` literal, the field mapping, the descriptor, and
all six open questions. The versioning call is unaffected - nothing here touches a shared file -
so module `1.0.0` with no base app version bump still stands. No source file was touched; this
plan file is still the only artifact.
