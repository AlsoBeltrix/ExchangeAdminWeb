# Defender for Endpoint Devices Module

Module ID: `DefenderEndpointDevices` | Route: `/defender-endpoint-devices` |
Category: Infrastructure | Version: 1.1.0

Design record, including every source URL and the review history:
`docs/DefenderEndpointDevices-Plan.md`.

## Purpose

List and export Microsoft Defender for Endpoint devices - including the devices Defender has
discovered on the network that **can be onboarded and are not**, which is the report this module
was built for. The page defaults to onboarding status "can be onboarded" and any platform; every
filter is operator-changeable.

Intended operators: the IT operations and endpoint team closing onboarding gaps, plus whoever is
accountable for sensor coverage. This is an onboarding-gap report, not a security-investigation
surface: it carries no alert, incident or investigation content, and it cannot act on a device.

## Mutates data

No. The module issues `GET` requests against the device inventory and one `POST` carrying a
constant, read-only hunting query. There is no write path anywhere in it, and the app registration
behind it is deliberately built so that it holds no permission that could change anything in
Defender.

## Backend

Two Microsoft APIs, reached from **one** app registration and **one** Delinea secret - the same
client id and secret acquiring two tokens for two different audiences.

| Purpose | Endpoint | Token scope | Permission |
|---|---|---|---|
| The device list | `GET https://api.security.microsoft.com/api/machines` | `https://api.securitycenter.microsoft.com/.default` | `Machine.Read.All` (WindowsDefenderATP) |
| Discovery sources, device type, category, vendor, model | `POST https://graph.microsoft.com/v1.0/security/runHuntingQuery` | `https://graph.microsoft.com/.default` | `ThreatHunting.Read.All` (Microsoft Graph) |

Why two APIs, since it looks like duplication:

- **Microsoft Graph has no device-inventory resource for Defender for Endpoint at all.** The
  Defender for Endpoint API is the only place the device list exists.
- **`DiscoverySources` is not a property of the Defender `machine` resource.** It is a column of
  the advanced hunting `DeviceInfo` table, so it needs a hunting query. The **Graph** form of
  advanced hunting is used rather than the Defender for Endpoint one precisely because the
  Defender one is being retired: it stops returning data on 2027-02-01.

The global API host is used and nothing else. Regional hosts (`us.`, `eu.`, `uk.`) exist as a
performance option, but which one applies is a fact about where a tenant sits, and this repo does
not bake that into source.

### The token audience trap - read this before debugging a 403

**The request host and the token audience are different hostnames on purpose.**

- Request host: `https://api.security.microsoft.com`
- Token audience / scope: `https://api.securitycenter.microsoft.com/.default` - the **legacy**
  hostname.

Microsoft documents this explicitly: if the token audience does not match the resource the API
expects, requests fail with **`403 Forbidden`**, even though the endpoint host is
`api.security.microsoft.com`. It is a 403 and not a 404, so a wrong audience looks exactly like a
missing permission. That is why this module's 403 message names **both** causes rather than saying
"forbidden": the app registration is missing `Machine.Read.All` and its consent, **or** the token
was issued for the wrong audience.

## Required permissions / groups

One section-access policy, fail-closed - no access until a group is assigned:

| Policy alias | Gates |
|---|---|
| `DefenderEndpointDevices` | Opening the module, viewing the device inventory, and exporting it |

There is no granular tier. The module has exactly one capability: read and export are the same act
against the same data, and splitting them would gate the clipboard rather than the data
(`BlockedSenders` and `BitLockerRecovery` export under their main permission too).

The module ships **disabled by default**. Three independent acts are needed before anyone sees a
device, and none of them happens on deploy:

1. enable the module in Admin Settings,
2. assign a group to the `DefenderEndpointDevices` section-access key on the module config page,
3. enter the Secret ID on that same page.

Direct navigation to `/defender-endpoint-devices` is denied when the module is disabled or the
operator is in no assigned group - the page carries
`[Authorize(Policy = "DefenderEndpointDevices")]` and re-checks authorization in
`OnInitializedAsync`, so a hidden nav link is not the control.

## Required module config fields

Configured at `/module-config/DefenderEndpointDevices`.

| Field | Label | Required | Default | What it does |
|---|---|---|---|---|
| `GraphDelineaSecretId` | Graph App Delinea Secret ID | Yes | none | The numeric Secret Server ID of this module's own secret. Until it is set the module reports itself unconfigured in words and makes no call at all. |
| `MaxDevices` | Maximum Devices | No | `100000` | Safety ceiling on one run - how many devices this server will hold in memory for one operator. If more devices match the server-side filters than this, the module **refuses** the report rather than returning part of it. An absent, unparseable or non-positive value falls back to 100000. |
| `IncludeDiscoverySources` | Include Discovery Sources | No | `true` | Whether the advanced hunting query runs. Turn it **off** on a deployment whose registration does not hold `ThreatHunting.Read.All`. Blank means on (the declared default); a present-but-unparseable value reads as **off**, which is the fail-closed direction - off is the value that does not call a permission the registration may not hold. |

The field is called `GraphDelineaSecretId` even though the primary API is not Graph:
`DelineaSecretId` is reserved for on-prem AD and Exchange credentials, and `GraphDelineaSecretId`
is this repo's name for an Entra app-registration secret. This registration also holds a real Graph
permission, so the name is accurate as well as conventional.

## Required Delinea secret template fields

A Secret Server record **dedicated to this module**, with three fields named exactly:

| Field name | Contents |
|---|---|
| `Tenant ID` | The Entra tenant (directory) ID of the app registration |
| `Application ID` | The registration's application (client) ID |
| `Client Secret` | The client secret **value** |

- The record must be **directly readable by the Delinea API bootstrap credential, with no checkout
  and no approval workflow**. The module reads it from a background service call with no signed-in
  user, and a noninteractive call cannot complete a checkout.
- Only the record's numeric **Secret ID** is entered in this repo. The tenant ID, client ID and
  secret are read from Secret Server at call time. None of them is in `appsettings.json`, defaulted
  in code, or written into any doc.
- **Do not point this module at another module's secret.** Per-module credential isolation is a
  Constitution invariant, and nothing in this permission set overlaps an existing module's.
- The secret is read once per API client construction and never cached in a field, so a rotated
  client secret takes effect on the next load rather than on the next app-pool recycle. The cost is
  one extra Secret Server round trip per run, because the hunting call builds its own client.

## The app registration - what has to be created, and by whom

This is the part that cannot be done from inside this repo. It is written so it can be handed to
whoever holds the tenant admin role without a follow-up round trip.

### 1. Register the application

Microsoft Entra admin center: **App registrations** > **New registration**. **Single tenant.**
Creating it needs a role with app-registration rights, such as **Application Administrator**.

Name it for the module so it is identifiable later, for example
`ExchangeAdminWeb - Defender Endpoint Devices`. The name is cosmetic; nothing in the code reads it.

### 2. Grant these application permissions

**Application permissions only. No delegated permissions.** The app runs as a background service
with no signed-in user (OAuth 2.0 client credentials).

| # | Permission | API to pick in the portal | Needed for |
|---|---|---|---|
| 1 | `Machine.Read.All` ("Read all machine profiles") | **WindowsDefenderATP** - Add permission > **APIs my organization uses** > search `WindowsDefenderATP` | The device list and every column except the last five |
| 2 | `ThreatHunting.Read.All` ("Run hunting queries") | **Microsoft Graph** | Discovery sources, device type, device category, vendor, model |

That is the whole list. Two permissions.

### 3. Admin consent - required for both, and the roles differ

Application permissions are never user-consentable. They take effect only after a tenant admin
grants consent in **API permissions** > **Grant admin consent for <tenant>**.

- `Machine.Read.All` (WindowsDefenderATP): **Privileged Role Administrator**, or **Cloud
  Application Administrator** / **Application Administrator** / **AI Administrator** - those three
  may consent to any API's permissions *except* Microsoft Graph application permissions.
- `ThreatHunting.Read.All` (**Microsoft Graph**): **Privileged Role Administrator** only, or a
  **Global Administrator** (which contains it). Application Administrator and Cloud Application
  Administrator are explicitly excluded from consenting Microsoft Graph app roles.

**So the consent for the discovery-sources half must be granted by a Privileged Role Administrator
or a Global Administrator.** Discovery sources are wanted here - a direct request from senior
leadership - so plan the consent around the higher role from the start.

### 4. Credential: a client secret, not a certificate

Create a **client secret** (Certificates & secrets > New client secret) and record its **Value**
immediately; it cannot be retrieved after that blade is left. Set an expiry someone is willing to
diary: there is no secret-rotation automation, and a lapsed secret fails every call with a named
401.

A certificate is supported by the identity platform and by this API, but **this repo cannot use one
today**. The token path authenticates with `client_secret` in a form post, and the standard Delinea
secret shape here (`Tenant ID`, `Application ID`, `Client Secret`) has no certificate field.
Certificate auth would be separate shared work.

### 5. Redirect URI: none

No redirect URI and no platform configuration at all. This is a single-tenant daemon: there is no
browser, no sign-in and no reply URL. (Microsoft's walkthrough adds one only in its *multitenant
partner* section, for ISVs whose app runs in customers' tenants.)

### 6. Do NOT grant these

| Permission | Why it looks needed | Why it is not |
|---|---|---|
| `Machine.ReadWrite.All` | Microsoft's **Get machine by ID** page lists *only* `Machine.ReadWrite.All` under application permissions, so following that page leads straight to a write grant | This module never calls get-machine-by-id. The detail panel renders from the row **List machines** already returned, and that endpoint documents `Machine.Read.All`. The design is shaped this way deliberately, to keep a write scope off the registration |
| `Machine.Isolate`, `Machine.Scan`, `Machine.Offboard`, `Machine.StopAndQuarantine`, `Machine.RestrictExecution` | "Device management" sounds like it includes actions | Actions are out of scope. A registration that never holds them cannot isolate or offboard a machine even if the code is wrong |
| `AdvancedQuery.Read.All` (WindowsDefenderATP) or `AdvancedHunting.Read.All` (Microsoft Threat Protection) | The older advanced hunting APIs use these | Those endpoints are retired and stop returning data on 2027-02-01. This module uses Graph `ThreatHunting.Read.All`. Granting all three is the belt-and-braces mistake |
| `Alert.Read.All`, `Vulnerability.Read.All`, `SecurityRecommendation.Read.All`, `Score.Read.All` | They sit on the same consent screen and look harmless | Nothing in scope reads them |
| Any Graph `Device.*`, `Directory.*`, `User.*` | The list shows Entra device IDs, so directory access looks adjacent | The module renders `aadDeviceId` as an opaque string. It never resolves it against the directory, and must not start to without a plan amendment |

### 7. One privilege cost, stated plainly

`ThreatHunting.Read.All` is **not** device-scoped. It grants the application read access to every
advanced hunting table in the tenant - device events, email events, identity events, cloud app
activity - not merely `DeviceInfo`. There is no narrower documented scope and no way to restrict the
permission to one table.

The only thing keeping this module inside `DeviceInfo` is its query text, which is a **constant in
source** with no operator input interpolated into it, ever:

```kusto
DeviceInfo | summarize arg_max(Timestamp, *) by DeviceId | where isempty(MergedToDeviceId)
 | project DeviceId, DiscoverySources, DeviceType, DeviceCategory, Vendor, Model
```

A test asserts that text names `DeviceInfo` and no other table, because the permission cannot
enforce the narrowing. The *grant* is still broad, and a compromised client secret would not be
constrained by this module's code. That is the honest trade for the discovery-sources columns.

### 8. Tenant prerequisites - none of them code

- **Microsoft Defender for Endpoint Plan 2**, with at least one device onboarded. Onboarded devices
  are the sensors that discover the non-onboarded ones; with no sensor there are no "can be
  onboarded" devices to list.
- **Advanced hunting is not included in Microsoft Defender for Business.** On a Defender for
  Business tenant the discovery-sources half cannot work whatever is consented.
- **Device discovery must be on.** It is on by default in Standard mode. Set to Basic, or with the
  relevant networks outside the monitored list, the inventory simply will not contain the devices
  the report is meant to find, and the module will correctly report an empty list. That is tenant
  configuration, not a module defect.
- **Retention.** Devices last seen before the tenant's configured retention window are not returned.

## Filters - which ones the API applies, and which ones this app applies

The portal's device filters are all here. Which side each one runs on is a fact about the API, not
a preference: Learn's List machines page names the properties `$filter` accepts on this collection
(`computerDnsName`, `id`, `version`, `deviceValue`, `aadDeviceId`, `machineTags`, `lastSeen`,
`exposureLevel`, `onboardingStatus`, `lastIpAddress`, `healthStatus`, `osPlatform`, `riskScore`,
`rbacGroupId`), and anything not on that list cannot be pushed to the service.

| Filter on the page | Side | Clause sent, or why none is |
|---|---|---|
| Device name starts with | server | `startswith(computerDnsName,'...')` - a prefix, not a substring |
| Onboarding status | server | `onboardingStatus eq '...'`, using the API literal (`CanBeOnboarded`), not the portal label |
| Platform, one value | server | `osPlatform eq '...'` |
| Platform, **Any Windows** | client | No `osPlatform` value means "any Windows", and `startswith` is documented for `computerDnsName` only. A guessed clause the service accepts while matching nothing looks exactly like an empty inventory |
| Health status | server | `healthStatus eq '...'` |
| Risk score | server | `riskScore eq '...'` |
| Exposure level | server | `exposureLevel eq '...'` |
| Last seen from / before | server | `lastSeen ge ...` / `lastSeen lt ...`. Also the bounds of the partition below, so it makes the run cheaper as well as narrower |
| Machine tag | client | A tag filter is a collection query (`machineTags/any(...)`) with no worked example for this endpoint |
| Machine group | client | The filterable property is `rbacGroupId`, a numeric id. The name the portal shows, `rbacGroupName`, is not filterable |
| First seen from / before | client | `firstSeen` is absent from the filterable list - the one timestamp on the machine resource that is not |

Client-side filters are labelled `(after fetch)` on the page. They narrow what is **shown** and
never what is **fetched**, so they cannot rescue a run that refuses on the ceiling. Date bounds are
local, and every "before" bound is exclusive. A device with no `firstSeen` value is excluded once
either First seen bound is set.

There is no "Windows devices only" checkbox. Platform is a dropdown like the others and defaults
to Any.

## The report is complete, or it refuses - there is no quiet partial

The owner asked to list and export *all* devices, so this module treats a shortened list as a defect
rather than a feature. A run renders a table only on a **positive proof of exhaustion**.

**There is no continuation cursor on this endpoint.** The live run of 2026-09-21 established it:
`GET /api/machines` returns at most 10,000 rows and carries no `@odata.nextLink`. A tenant larger
than that therefore cannot be listed by asking once, so the module divides the **question**:

- The inventory is partitioned on `lastSeen` into parts that are disjoint and together exhaustive,
  and each part is asked for on its own.
- A part that comes back under the cap has proved itself complete (**P1** - it asked for `$top = N`,
  fewer than N rows came back, and there was no continuation link). **P2**, a cursor chain whose
  final response was short and cursor-free, still applies if the service ever starts issuing one.
- A part that comes back **at** the cap has proved nothing, and is split in two and asked again.
  The split point is the median of the timestamps that response returned, restricted to values
  strictly inside the part, so both halves are strictly narrower and the division terminates.
- The answer is the union of the parts that proved themselves. Rows from a part that came back at
  the cap are not in it - a child part re-reads them.

**Devices with no Last seen value.** A null satisfies neither `ge` nor `lt`, so intervals alone
would miss them. While the inventory needs no dividing the single request carries no `lastSeen`
clause at all and they are already in it. As soon as it has to be divided, a separate request asks
`lastSeen eq null` - unless the operator set a Last seen window, in which case those devices are
outside what was asked for.

**Parts are read oldest first.** A device that reports in mid-run moves forward in `lastSeen`, so
reading low parts first means it can only move into a part not yet read.

The results header reports the shape of the run: `N device(s)`, then `M fetched in R request(s).
Ceiling C.` A request count above one means the inventory had to be divided. The range count was on
screen in an earlier draft and was cut: it is an implementation detail, and the review of
2026-09-21 ruled it out of the operator's way.

Everything else **refuses**: a red banner headed *"No device list."*, the reason, and no table and
no export button.

| Refusal | Why it is not rendered |
|---|---|
| More devices match the server-side filters than `MaxDevices` (default 100000). The message names the ceiling, the filters that would reduce the fetch, and the ones that would not | The report would be incomplete. The request asks for `min(MaxDevices + 1, 10000)` rows, so one extra row settles the ceiling in a single round trip |
| A part came back at the cap and cannot be divided - every device in it shares one Last seen timestamp, or none has one | "Exactly N devices exist" and "the first N of more" produce byte-identical responses, and there is no point inside the range to divide at |
| More devices have **no** Last seen timestamp than one request returns | That part has no time axis to divide on. The message says to set a Last seen window, which takes those devices out of the question |
| The API rejected `lastSeen eq null` with a 400 | That request is the only way to reach devices with no Last seen value, so without it the list could silently miss them. Same operator action: set a Last seen window |
| Any request failed (401, 403, 400, 429, 5xx, a timeout, or a 404 **after** the first request of a part) | A failed read must never fall through to an empty table |
| A 2xx whose body carried no device collection | An unreadable response is not an empty inventory |
| 250 requests issued in one run with parts still unresolved | The endpoint documents 100 calls a minute and 1,500 an hour. A partition that terminates but does not converge has to stop, and a run stopped early has not proved exhaustion |

Two things that are **not** refusals:

- **`404` on the first request of a part is the documented empty result** for this collection ("if
  there are no recent machines, you see 404 Not Found"). A part covering a slice of time with no
  devices in it is an ordinary outcome of dividing the inventory. This inverts the usual rule in
  this repo; a 404 partway through a cursor chain is a broken chain and refuses.
- The client-side filters are applied **after** the whole partition, never during it. Filtering
  while accumulating would make a full response of non-matching rows look short and turn the
  ambiguous case into a false proof of exhaustion.

`$skip` paging is deliberately not used, and this is worth knowing before anyone adds it:
de-duplication removes overlaps but **cannot detect a gap**, a short page is exactly what a moved
window produces, and this collection documents no `$orderby`. The partition needs none of that - it
depends on no ordering at all.

**Counts will differ from some portal views, and that is expected.** Microsoft's own caveat: the
onboarding *recommendation* and the "devices to onboard" dashboard widget exclude ephemeral and
guest devices, and the API, UI, export and advanced hunting interfaces are powered by separate
backends with different update frequencies. This is documented here rather than on the page.

## `(unavailable)` versus blank in the discovery-sources columns

Five columns come from the hunting query: **Discovery sources, Device type, Device category, Vendor,
Model**. That call is independent of the device list: a 403, a 429, a timeout or the config switch
being off leaves every device row fully rendered.

The rule, on screen and in the file:

| Run state | Those five columns read |
|---|---|
| Enrichment **succeeded**, device has a value | the value |
| Enrichment **succeeded**, device genuinely has no value (Microsoft populates Vendor and Model only when discovery found enough) | `-` on screen, **blank** in the CSV |
| Enrichment did **not** succeed - switched off, failed, or not attempted | `(unavailable)`, in all five columns, for every device, plus a warning **above** the table naming the reason |

An empty cell must never be how an operator learns that half the report did not execute. Once
`(unavailable)` owns the failed-run meaning, a blank can only mean the run worked and that device
had no value.

**The screen and the CSV deliberately differ on exactly one case.** On a succeeded run an empty
value is a dash on screen and **blank** in the file. The shared CSV writer neutralises a leading `-`
into `'-` to defeat spreadsheet-formula injection, so writing the screen's placeholder would land a
literal apostrophe-dash in the spreadsheet, and it would be the only column in the file carrying a
placeholder at all. The direction that matters - a blank standing in for a run that never executed -
cannot happen, because that state writes `(unavailable)` instead.

Distinct reasons are named rather than collapsed into "unavailable", because the operator needs to
know whether to chase a consent grant, wait out a quota, or retry:

| Cause | What the warning says, in short |
|---|---|
| `IncludeDiscoverySources` is off | The switch is off; turn it on once the registration holds `ThreatHunting.Read.All` |
| 403 | Graph refused the query - the registration lacks `ThreatHunting.Read.All`, or nobody granted admin consent (which needs a Privileged Role Administrator or Global Administrator) |
| 429 | Graph throttled it - the tenant's advanced hunting CPU quota is exhausted. Nothing is misconfigured; wait and load again |
| 401 | Graph rejected the credentials - check whether the client secret has expired |
| Timeout | The query did not finish before the request timed out |
| A 2xx with an unreadable body | Graph answered with something the module could not read - no results collection |
| 5xx, or any other status | Graph was unavailable, or rejected the query, with the status named |
| No status came back at all | The query could not be sent - the sign-in for the app registration was refused, or this server cannot reach Graph. There is no status to name, which is what separates this from the 401 and 5xx rows |
| Credentials could not be built | The module's app credentials were unavailable, so the query never ran |
| No devices matched | Nothing to enrich; the query was not run |
| The listing refused | Enrichment was never reached |

Every one of these ends by saying the device list itself is unaffected, and none of them names
`Machine.Read.All` or `api.securitycenter` - those belong to the device list's own 403, and
borrowing that text would send an operator to check a permission that is demonstrably working.

The hunting call uses its own named `HttpClient` with a **4-minute** timeout, while the inventory
client uses 30 seconds. Graph allows a single hunting request up to three minutes of its own; a
30-second client would cancel legitimate work and report it as an intermittent, load-dependent
failure that looks like a service fault.

## CSV export

- Written through the shared `CsvExport.Write`, which handles quoting and neutralises
  spreadsheet-formula injection.
- Filename `DefenderEndpointDevices_yyyyMMdd_HHmmss.csv`, served through the existing `downloadFile`
  JS interop.
- **27 columns, in this order:** `DeviceId`, `ComputerDnsName`, `DnsDomain`, `OnboardingStatus`,
  `OsPlatform`, `OsVersion`, `OsBuild`, `OsArchitecture`, `HealthStatus`, `LastIpAddress`,
  `LastExternalIpAddress`, `IpAddresses`, `MacAddresses`, `FirstSeenUtc`, `LastSeenUtc`, `RiskScore`,
  `ExposureLevel`, `DeviceValue`, `MachineTags`, `MachineGroup`, `IsAadJoined`, `AadDeviceId`,
  `DiscoverySources`, `DeviceType`, `DeviceCategory`, `Vendor`, `Model`. The header is asserted
  verbatim by a test rather than derived from the code, so moving a column takes a human who can
  check it against the plan.
- Multi-valued cells (`IpAddresses`, `MacAddresses`, `MachineTags`, and `DiscoverySources` when it
  arrives as a list) are joined with `"; "`.
- `DnsDomain` is **derived** from `computerDnsName` - everything after the first dot, empty when
  there is no dot. It is a DNS suffix, not Active Directory domain membership; see "Not in this
  module".
- `FirstSeenUtc` and `LastSeenUtc` are written in UTC with the sortable invariant `u` format, so an
  exported file does not change shape with the host's locale. The **table on screen deliberately
  shows local time**, because that is what the operator reading the screen wants.
- `IsAadJoined` renders lowercase `true` / `false`.
- **The export button exists only in the complete branch.** A refusal carries zero devices by
  construction, so an export offered from that state would write a header-only file -
  indistinguishable from "no devices matched" to whoever opens it, and the refusal banner does not
  travel with the file. There is no truncation marker in the CSV, because there is no truncated
  state to mark.

## Protected-principal behavior

**Not applicable, and stated rather than silently omitted.** The protected-principal guard binds to
the *target of a write*. This module performs no write, so there is no target to check. If a later
plan ever adds a device action (isolate, offboard, scan), **that** plan must add the guard - the
principal would be the device's primary user.

## Audit actions emitted

Category `DefenderEndpointDevices`:

- `DefenderEndpointDevices_List` - lookup audit on **every** listing, success or failure. The target
  string is the filters as captured when the click was accepted (`onboardingStatus=<value>` plus
  every filter that was set, each prefixed with the API property it maps to), and the record
  carries `Outcome`, `Devices`, `DistinctDevices`, `Requests`, `Ranges` and `Ceiling`. **A refusal
  is audited as a FAILED lookup carrying its refusal reason** - not as a successful read of zero
  devices, which would be the blanket-success shape.
- `ExportCsv` - module audit on each download, with the row count.

Audit-write failures are caught and logged separately; they never change the listing or the export
result. On the export path that is deliberate: the bytes are already on their way to the browser,
and throwing there would report a completed disclosure as a failed one.

## Notifications

None. The module is classified **non-alerting**: audit is the record, and every read and every
export writes one.

This one is closer to the line than most read modules - the data is produced by a security product
and is in effect a map of the gaps in the tenant's sensor coverage - so the classification is a
deployment decision rather than an automatic property of the data. It remains **open question 4 in
the plan, and the owner has not answered it**. Overturning it adds one `SendAdminNotificationAsync`
call on the read path and nothing else.

## Fail-closed behavior

| Condition | Behavior |
|---|---|
| `GraphDelineaSecretId` unset, or not a positive integer | The page says the module is not configured, in words, **above** the filter card - and returns, so no control that could start a call is rendered. The service throws rather than returning an empty result |
| The Secret ID does not resolve in Secret Server, or the record is missing or blank in any of the three fields | Error banner naming the Secret ID and what to check. No table |
| Device list 401 | Named credential failure - check whether the client secret has expired. No table |
| Device list 403 | Named as **either** a missing `Machine.Read.All` and its consent **or** a wrong token audience, with both hostnames spelled out. Never an empty list |
| Device list 400 | "This is a FAILED report, not an inventory with no matching devices" |
| Device list 429, 5xx, timeout, or an unreadable body | Named failure. No table, no export |
| Device list 404 on the first request | The documented **empty** result: "no devices matched". A 404 later in a chain refuses |
| The result cannot be proved complete (ceiling, full page with no cursor, the 100-request stop) | Refusal in words. No table, no export button |
| Hunting query 403, 429, 401, timeout, malformed, the switch off, or a send that never got an answer | The device list stands; the five enrichment columns read `(unavailable)` with the reason named above the table. Never blank, never a page-wide failure. This holds for an **exception** on the hunting side too - a Secret Server read that fails or a refused sign-in greys the columns rather than clearing the page |
| `IncludeDiscoverySources` present but unparseable | Treated as **off** - the value that does not call a permission the registration may not hold. The module config page renders an unparseable Boolean as an unchecked box, so this keeps the switch and the screen in agreement |
| `MaxDevices` absent, unparseable or non-positive | Falls back to the declared default of 100000 |
| A continuation link pointing at a different host, or at plain HTTP | Refused **before a token is acquired**. The absolute-URL path exists to follow a link out of a response body, and following an arbitrary host would hand this registration's bearer token to whatever that body named |
| Operator in no assigned group, or the module disabled | Direct URL denied by policy, re-checked in `OnInitializedAsync` |

## Not confirmed against the live service - R1 is only partly answered

**The module has made one live run, on 2026-09-21. It answered R1(f) and R1(g) and nothing else:**
the run refused before it could show a device, so no field, no casing and no filter behaviour was
observed. Every unit test in the suite runs against a stub HTTP handler.

The items below still have a conservative fallback in the code, so the module is not *waiting* on
the answers - but these are the behaviours nobody has yet seen work:

- **(a) Whether `GET /api/machines` returns `CanBeOnboarded` devices at all.** This is the assumption
  the whole module rests on, inferred from three Microsoft documents rather than stated by one. If it
  turns out false, the list would have to come from the advanced hunting `DeviceInfo` query instead,
  `ThreatHunting.Read.All` stops being optional, and that goes back to the owner as a changed
  permission ask rather than being absorbed.
- **(b)** The JSON casing of `onboardingStatus` as returned, and whether `$filter` accepts either
  casing. Microsoft's own pages are inconsistent about it. Deserialization here is case-insensitive
  either way.
- **(c)** The distinct `osPlatform` values actually present, which is what the client-side "Any
  Windows" prefix rule matches against, and which of them the Platform dropdown should list.
- **(d)** Whether `startswith(osPlatform,'Windows')` is accepted server-side. Undocumented, so it is
  not used; the client-side rule is correct either way.
- **(e) Whether `ipAddresses` is populated on list responses - and the `IpAddresses` and
  `MacAddresses` columns, and the detail panel's IP and MAC rows, shipped ahead of that answer.**
  That was the implementing slice's decision, not the owner's, taken on asymmetric cost: if the
  collection comes back empty, two columns are blank until someone deletes them; if the columns had
  been left out and the collection is populated, the module would have shipped and been used without
  the MAC addresses that are often the only way to identify a discovered device with no name yet.
  **The undo, if it comes back empty:** remove `IpAddresses` and `MacAddresses` from `BuildCsv`'s
  header and projection, and from `ExpectedHeader` and the indices in
  `DefenderEndpointDevicesCsvTests`, and record it as Revision 8 in the plan.
- **(f) ANSWERED, 2026-09-21: the tenant exceeds the old ceiling.** The first live load matched more
  than 40,000 devices against a default of 20,000. The default is now 100,000.
- **(g) ANSWERED, 2026-09-21: this endpoint emits no `@odata.nextLink` at all.** The first live load
  returned exactly the 10,000 rows it asked for and carried no continuation link. There is no cursor
  to follow, which is why the module divides the inventory into Last seen ranges instead of paging;
  see "The report is complete, or it refuses". The cursor-following code is kept because it costs
  one branch and is the only correct thing to do if the service ever starts issuing one.
- **(h) Whether `DiscoverySources` arrives as a JSON string or a JSON array.** No Microsoft page
  settles it. **Both shapes are parsed** and joined with the house separator, so the column is
  correct either way. It is called out because reading only the string shape would have silently
  blanked the one column senior leadership asked for.

Four of the plan's owner questions are also still unanswered, and in each case the shipped behaviour
is the plan's proposal rather than a ruling: **Q2**, one app registration for both permissions
(shipped as one); **Q3**, whether the CSV export should require a ticket number (shipped without
one); **Q4**, the alerting classification above; and **Q5**, refuse at the ceiling rather than
return a clearly-marked partial - which is what shipped, and is the design the rest of the module is
built around, so overturning that one is not a small change.

## Manual validation steps - NOT YET PERFORMED

**Nothing in this repo renders a Blazor page.** There is no bUnit harness, so the unit suite proves
nothing about the page authorization re-check, the refusal wording, the absence of the export button
in the refusal state, the `(unavailable)` banner or the Windows-only toggle. This checklist is the
only evidence an operator will ever have that those work, **and it has not been run** - it cannot be
until the app registration exists and a dev deploy carries the Secret ID.

Run it after the first dev deploy that follows the app registration being created:

1. With `GraphDelineaSecretId` unset, the module reports itself unavailable in words - not as an
   empty table.
2. Set the Secret ID. The device list loads and the module version renders beside the heading.
3. The default view is every platform with onboarding status "can be onboarded". The count is
   plausible against the Defender portal's own **Onboarding status: Can be onboarded** filter - a
   difference against the *onboarding recommendation widget* is expected, not a defect. On a tenant
   larger than 10,000 matching devices the header reports more than one Last seen range, which is
   the partition doing its work.
4. Set Platform to **Any Windows**: non-Windows discovered devices disappear. Set it to
   **Windows11**: the request itself narrows, and the range count usually drops with it.
5. Switch the onboarding-status filter to onboarded: onboarded machines appear, with healthy sensor
   states.
6. Spot-check one device against its portal page: FQDN, OS, last IP, MAC, first and last seen.
7. Discovery sources are populated for at least one device, and the values look like the portal's
   ("MDE", and whatever else this tenant has). **This also settles item (h) above**: whichever JSON
   shape arrives, the cell must read as a source name and not as raw JSON.
8. Export. The CSV opens in Excel with the header intact, one row per on-screen device, and a device
   name containing a comma stays in its own cell.
9. The Event Log shows the lookup and the export, and the export entry carries the row count.
10. Point the Secret ID at a registration **without** `Machine.Read.All`: the page reports a
    permission failure naming the missing grant, not an empty device list.
11. Turn `IncludeDiscoverySources` off: the five columns read `(unavailable)` with the reason, and
    the rest of the report is unaffected. Then point at a registration without
    `ThreatHunting.Read.All` with the switch on: same behaviour, different stated reason.
12. Sign in as a user in no section-access group for `DefenderEndpointDevices`:
    `/defender-endpoint-devices` denies by **direct URL**, not merely by a hidden nav link.
13. Disable the module in Admin Settings: it disappears from the nav and the direct URL denies.
14. **The ceiling, which is the check that proves the completeness rule.** Set `MaxDevices` below the
    number of devices matching the default filters - a handful will do. The page must refuse in
    words, name the ceiling, and show **no table and no export button**. Restore the value and
    confirm the full report returns. A short table, or an export button offered in the refusal state,
    is the failure this check exists to catch.
15. Compare the row count against the same filters in the portal's device inventory. They should
    agree, modulo the update-frequency caveat above. A count that stops suspiciously near a round
    number is the signature of paging that stopped early.

## Rollback / remediation

Nothing here has to be undone in Defender - the module has written nothing to it. Everything below
is runtime configuration and needs no deploy.

- **Disable the module** in Admin Settings: it leaves the nav and the direct URL denies, for
  everyone, at once.
- **Remove the group** from the `DefenderEndpointDevices` section-access key: the same effect for
  those operators, leaving the module live for others.
- **Turn `IncludeDiscoverySources` off**: the device list keeps working and the five enrichment
  columns read `(unavailable)` with the reason. This is the switch to use while
  `ThreatHunting.Read.All` is un-consented, or if the broad hunting grant has to be withdrawn.
- **Clear the Secret ID**: the module reports itself unconfigured and makes no call at all.
- **Revoke at the tenant**: remove admin consent, or delete the client secret. No credential is
  cached across runs, so the next load fails with a named 401 rather than continuing on a stale
  token. Deleting the app registration removes both permissions at once.

## Not in this module

The boundary, named so a later reader does not have to re-derive it. None of these is a defect.

- **Every device action.** Isolate, release from isolation, run an antivirus scan, collect an
  investigation package, restrict app execution, offboard, stop and quarantine. This module reads and
  exports; it mutates nothing. Each of those would be a separate plan, a separate granular permission
  and a wider app-registration grant that this registration deliberately does not hold.
- **Device tagging** (`Machine.ReadWrite.All` territory).
- **Onboarding a device.** The module reports which devices *can be* onboarded. It does not onboard
  them, and nothing here installs or configures a sensor.
- **Alerts, incidents, vulnerabilities, software inventory, security recommendations.**
- **Network devices and IoT/OT inventory as a first-class view.** They live in the same inventory and
  will appear in the list when they match the filters, but the IoT-specific columns and tabs the
  portal has are not reproduced here.
- **Scheduled or emailed reports.** Export is operator-initiated and in-session.
- **Active Directory domain or OU membership of a discovered device.** Not in the Defender machine
  resource and not in `DeviceInfo`: a non-onboarded device has no sensor to report it. `DnsDomain` is
  a DNS suffix derived from the fully qualified name, and Entra join state is separate
  (`IsAadJoined` / `AadDeviceId`, rendered as opaque values and never resolved against the
  directory).
- **The logged-on user of a non-onboarded device**, for the same reason.
- **Why a device is unsupported.** `Unsupported` and `Insufficient info` are states, not
  explanations.
- **A "should this be onboarded?" verdict.** The portal's onboarding recommendation excludes
  ephemeral and guest devices by its own logic. This list does not reproduce that logic and must not
  be presented as if it did.
