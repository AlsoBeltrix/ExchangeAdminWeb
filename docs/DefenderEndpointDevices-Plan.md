# Defender for Endpoint Devices Module - Plan

Status: In progress (2026-09-24). S1-S5 landed and the module ships at `1.1.0`; the owner then ran it against the live service, which answered R1(g) (no continuation cursor) and R1(f) (the tenant exceeds the old 20000 ceiling) and forced the rebuild recorded in Revision 3 of the second series. **The owner has now REVISED queue item 8 - the quote below is the new text, and Revision 5 folds it in.** The app-registration blocker earlier revisions carried is gone: the registration exists, Revision 3 records both permissions as granted and consented, and the Delinea Secret ID is **657**. Owner rulings of 2026-09-24/25 closed Q7 and Q10, and R1(o) is measured (the AD subnet-to-site map is real: 531 subnets, 515 mapped, 167 sites, `location` empty everywhere). Still open: R1 (a), (b), (c), (d), (e) and (h) through (n); the manual acceptance checklist; Q2, Q3, Q4, Q5, Q8 and Q9. Queue 8's park and the two defects open against the shipped module are recorded in `.agents/state.md`, which owns them - not here. Deliberately ONE line: wrapping it shifts every line below and invalidates the line citations in the revisions.

Owner request, verbatim from the queue as revised on 2026-09-24. **This supersedes the original
queue text, which asked for "3. other important info" and said the app registration still had to be
created.** What changed: the module's PURPOSE is now stated, item 3 is a named field rather than a
blank cheque, the registration exists with its Delinea Secret ID, and the owner asks for a
location-narrowing column set to be proposed for approval.

> 8. New module for Microsoft Defender for Endpoints:
> 	List & Export all devices
> 	Specific request was for all windows devices in the "Can be onboarded" state
> 	including
> 	1. discovery sources
> 	2. IP, domain, OS, etc.
> 	3. "Recently Seen By" (see screenshot "C:\Users\mcoelho\Desktop\Screenshot 2026-09-24 163154b.png")
> 	Purpose of this is to help locate machines physically in a global company, so using recently seen by helps narrow a location. anything else that could help narrow a physical loc should be included in the plan and presented to me for approval.
> 	Report should be exportable. New appreg created. SS Secret ID 657.

New module `DefenderEndpointDevices`. Read-only. Closest precedent in this repo is
`docs/IntuneDeviceManagement-Plan.md` (a device-listing module over a per-module Entra app
registration) and the CSV half of `docs/ModuleCsvExport-Plan.md`. This module is independent of
both: no shared code, no ordering constraint either way.

All API facts below were verified against Microsoft Learn on **2026-09-18**, and everything the
2026-09-24 revision added or re-leaned on was verified again on **2026-09-24**, not from memory.
Every source URL is listed under Sources. Anything that could NOT be verified is collected under
"Assumptions" and is labelled as an assumption in the body as well.

## The purpose, and what it reframes

**The module is still list-and-export. What changed is the criterion for choosing its columns:
every column now has to earn its place by narrowing a physical location.** The owner's sentence is
the whole of it - "Purpose of this is to help locate machines physically in a global company."

Three consequences, each of which contradicts something written below it in earlier revisions:

1. **"Discovery sources" was never the requirement.** It was a proxy for "where did this thing come
   from", and the research settles that it is the wrong data: `DeviceInfo.DiscoverySources` is
   documented as "Products or services that have seen or reported the device, including when they
   last reported it" - it names **MDE** or **Microsoft Defender for IoT**, a *product*, not a
   machine and not a place. It is also mostly blank in this tenant, for the reason
   `.agents/state.md` records under queue 8. `DiscoverySources` is **not deleted** - it is a cheap
   column already shipped and it costs nothing to keep - but it is **demoted**: it stops being
   presented as answering the owner's need, and nothing in the report, this plan or
   `docs/DefenderEndpointDevices.md` may imply that it locates anything.
2. **"Recently Seen By" is the field that does the work,** it is obtainable, and it is obtainable
   only through advanced hunting. See "Recently Seen By" below.
3. **A column set has to be proposed and approved.** The owner asked for exactly that - "anything
   else that could help narrow a physical loc should be included in the plan and presented to me
   for approval" - so it is a proposal, not a decision this plan makes. See "Location-narrowing
   candidate fields".

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

## The app registration - CREATED; this section is now a verification checklist

**Status changed on 2026-09-24. The registration exists, the Delinea secret exists, and its Secret
ID is 657.** Everything below was written as instructions for creating it; it is kept, converted,
because one of the two permissions changed status - `ThreatHunting.Read.All` went from optional to
load-bearing when "Recently Seen By" became a named requirement - and a registration consented
under the old, optional reading may not hold it. **Deleting this section would delete the only
place that says what to check.**

Each numbered item below now reads as: what was asked for, and what has to be observed against the
registration that exists. Nothing here is a new ask unless it says so.

### 1. Register the application - DONE

In the Microsoft Entra admin center: **App registrations** > **New registration**. Single tenant.
Creating it needs a role with app-registration rights, such as **Application Administrator**
(Learn names that role in the prerequisites of the client-credentials walkthrough).

Name it for the module so it is identifiable later, for example `ExchangeAdminWeb - Defender
Endpoint Devices`. The name is cosmetic; nothing in the code reads it.

**Verify:** nothing. Revision 3 of the second series records the owner creating it, entering the
Secret ID, deploying to dev and reaching the live service, which is proof the registration exists
and authenticates.

### 2. Grant these application permissions

**Application permissions only. No delegated permissions.** The app runs as a background service
with no signed-in user (OAuth 2.0 client credentials).

| # | Permission | Display name in the portal | API / resource to pick | Needed for | Required? |
| --- | --- | --- | --- | --- | --- |
| 1 | `Machine.Read.All` | 'Read all machine profiles' | **WindowsDefenderATP** (API permissions > Add permission > **APIs my organization uses** > search `WindowsDefenderATP`) | The device list, all fields except the hunting-only ones | **Yes** - without it the module does nothing |
| 2 | `ThreatHunting.Read.All` | 'Run hunting queries' (Microsoft Graph) | **Microsoft Graph** | **"Recently Seen By"** and every location-narrowing field that comes from `DeviceNetworkInfo` or `DeviceInfo`; also discovery sources, device type, vendor and model | **Yes, now mandatory** - see below |

That is still the whole list. Two permissions - but **the second is no longer optional**, and that
is the substantive change in this revision.

**Why permission 2 changed status.** It was optional while its only consumer was the
discovery-sources enrichment, which shipped behind the `IncludeDiscoverySources` config flag
(`Modules/ModuleCatalog.cs:830`) and which Q1 made droppable. "Recently Seen By" is now a named
owner requirement, the only documented route to it is the `SeenBy()` advanced hunting function, and
the only non-retiring API for advanced hunting is Graph `runHuntingQuery` with
`ThreatHunting.Read.All`. So hunting is load-bearing: without that consent the module cannot answer
requirement 3 at all. This does not make the permission any narrower - the tenant-wide cost stated
under "Least privilege" is unchanged - it makes it unavoidable. Q1 closed "yes" on 2026-09-21 for
discovery sources (Revision 5 of the first series); it would now close "yes" a second time on a
different and stronger ground.

**Verify against the registration that exists:**

1. In the registration's **API permissions** blade, `ThreatHunting.Read.All` is present under
   **Microsoft Graph**, type **Application**, and its Status column reads **Granted for
   \<tenant\>**. A permission that is added but not consented looks almost identical and fails at
   runtime with a 403.
2. `Machine.Read.All` is present under **WindowsDefenderATP**, type **Application**, granted. It
   is proved working already - the live run in Revision 3 of the second series returned 10,000
   device rows - so this one is a formality.
3. No `*.ReadWrite.*` grant is present on this registration. See "Least privilege"; the repo has a
   live blocker about exactly that on a different registration.

If item 1 fails, the fix is a consent by a Privileged Role Administrator or Global Administrator,
not a code change. See "Admin consent" below, which is the same fact and is why it is still here.

### 3. Admin consent - the fact that decides who has to be in the room

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

**So: the Graph consent has to be done by a Privileged Role Administrator or a Global
Administrator. An Application Administrator cannot do it, however senior they are.** That was
already true; what changed is that there is no longer a version of this module that avoids it. The
old escape hatch - drop discovery sources and an Application Administrator can complete the whole
thing - is closed, because "Recently Seen By" is a named requirement and hunting is the only route
to it.

**Verify:** the Status column in the registration's API permissions blade, per item 1 of the
verification list above. `.agents/state.md` and this file have both carried the
Privileged-Role-Administrator fact since the first draft; it is not new and it is not negotiable
by asking a different administrator.

### 4. Credential: client secret

Create a **client secret** (Certificates & secrets > New client secret) and record its **Value**
immediately - it is not retrievable after the blade is left. Set an expiry the owner is willing
to diary; the module has no secret-rotation automation and a lapsed secret fails every call.

A certificate is also supported by the identity platform and by this API, but **this repo cannot
use one today**: `Services/GraphTokenClient.cs` and everything modelled on it authenticate with
`client_secret` in a form post, and the Delinea secret shape this repo standardises on
(`Tenant ID`, `Application ID`, `Client Secret`) has no certificate field. Certificate auth would
be a separate piece of shared work. **Create a secret.**

### 5. Where the credential lands in this repo - Secret ID 657

**The Delinea Secret ID is 657.** It goes in this module's own `GraphDelineaSecretId` config field,
declared on the descriptor at `Modules/ModuleCatalog.cs:825` (the descriptor starts at `:799`), and
it is entered on the module's config page - never in `appsettings.json`, never in source, and never
in this file as a credential. The number itself is not a secret; the record it names is.

Exactly the `ServiceHealth` / `IntuneDevices` shape, unchanged:

1. A **Delinea Secret Server record dedicated to this module**, containing three fields named
   exactly `Tenant ID`, `Application ID` and `Client Secret`
   (`docs/AdminModuleSpec.md`, "For Graph API modules"). The secret must be directly readable by
   the Delinea API bootstrap credential, with no checkout or approval workflow - a noninteractive
   call cannot complete one.
2. That record's numeric **Secret ID - 657** is typed into the module's own config page, into the
   `GraphDelineaSecretId` field declared by the descriptor below. Same field id and same label as
   `ServiceHealth` and `IntuneDevices` use. **Verify:** the value on the module's config page reads
   657, and the module reports available rather than "credentials unavailable".
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
constrained by the module's code.

**This was Q1, and Q1 is closed "yes" (Revision 5 of the first series).** It is recorded here
unchanged rather than deleted, because the trade did not stop being real when it stopped being
optional - it is now the price of requirement 3 rather than the price of a nice-to-have column, and
the request-body guard that keeps the module inside the hunting tables it actually needs is
therefore the only boundary there is. **The query text is about to widen** - "Recently Seen By"
adds `invoke SeenBy()` and a self-join on `DeviceInfo`, and the fallback would add
`DeviceNetworkInfo` - so the T7 guard that pins the posted `Query` must be widened deliberately and
exactly, never loosened. See T7.

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

## "Recently Seen By" - requirement 3, and how it is obtained

### The owner's screenshot answers a question this repo recorded as open

`.agents/state.md` records, under queue 8, one open question asked and unanswered: *does the
Defender portal itself show a discovering or onboarded machine on a "Can be onboarded" device's
page?* **It is answered, and the evidence is the owner's own screenshot of 2026-09-24**, cited in
the revised queue text above.

What the screenshot shows, on the device page of a device whose onboarding status is **Can be
onboarded** and whose Last seen is 21 Sep 2026: a field labelled **"Recently seen by"**, carrying
the name of a *different* device - an **onboarded** one - together with a **"View all seen by"**
link. The observed device is a VMware-vendor machine; the observer is an onboarded workstation
named by FQDN. So the answer is **yes: the portal does show an onboarded machine on a
can-be-onboarded device's page, the data exists, and the route needed finding rather than
inventing.**

The device names, the vendor string and the dates in that screenshot are **evidence about this
tenant, not behaviour**. Nothing in this plan, and nothing in any source file, may name them, key
off them or rest a safety argument on them (`.agents/repo-guidance.md` invariant 7). They appear
here only because the finding has to be attributable.

### `SeenBy()` is a FUNCTION, not a column - which is why the column hunt failed

This is the whole reason the earlier search came up empty. `.agents/state.md` records that
`DeviceInfo`'s columns were enumerated in the portal and **none of them is a "discovered by"**.
That measurement is correct and is not overturned. The relationship is not a column at all; it is
an enrichment **function**:

> "The `SeenBy()` function is invoked to see a list of onboarded devices that have seen a certain
> device using the device discovery feature."
> -- https://learn.microsoft.com/en-us/defender-xdr/advanced-hunting-seenby-function

From that page, verified rather than recalled:

| Fact | Value |
| --- | --- |
| Syntax | `invoke SeenBy(x)`, where x is the device ID of interest. Piped from `DeviceInfo` it needs no argument, because the pipe supplies it. |
| Returned column | `DeviceId` (`string`), "Unique identifier for the device in the service" |
| Hard cap | "You can enter up to **1,000 devices** in this function." |
| Caveat Learn states itself | "Enrichment functions show supplemental information only when they're available. Availability of information is varied and depends on many factors." |

**The 1,000-device cap is the design constraint that matters**, and it does not fit this module's
shape as built. The listing partitions an inventory of 12,902 can-be-onboarded devices (the count
is `.agents/state.md`'s, measured by the owner; it is pointed at, not copied) and the enrichment
runs **once per refresh** (T7). One `invoke SeenBy()` cannot cover that set. Whatever S6 does about
it - batch the enrichment in chunks of at most 1,000, or enrich only the rows the operator is
looking at - is a design question this revision raises and does not answer. It is **Q9**.

### Microsoft publishes the query for exactly this scenario, and says what it is for

This is not an inference from a function reference. Learn's "Review and assess devices" page has a
section on discovered devices that states the purpose in the same words the owner used:

> "By invoking the **SeenBy** function, in your advanced hunting query, you can get detail on which
> onboarded device a discovered device was seen by. **This information can help determine the
> network location of each discovered device** and subsequently, help to identify it in the
> network."
> -- https://learn.microsoft.com/en-us/defender-endpoint/assess-devices

And the query, quoted from that page verbatim:

```kusto
DeviceInfo
| where OnboardingStatus != "Onboarded"
| summarize arg_max(Timestamp, *) by DeviceId
| where isempty(MergedToDeviceId)
| limit 100
| invoke SeenBy()
| project DeviceId, DeviceName, DeviceType, SeenBy
```

Three things to read off it rather than past it:

1. `summarize arg_max(Timestamp, *) by DeviceId` and `where isempty(MergedToDeviceId)` are the same
   two clauses the module's shipped hunting query already carries (T7). The shape is compatible.
2. The `| limit 100` is Microsoft's, and it is in their example because of the cap above. It is not
   decoration and it must not be dropped without deciding Q9.
3. **The published query and the function reference disagree about the returned column name.** The
   reference table says the function returns `DeviceId`; Microsoft's own example projects a column
   called `SeenBy`. Both pages are current. **This plan does not resolve that on paper** - it is
   **R1(h)**, observed on the first live run, and the parser must be written so the answer is a
   one-line change rather than a rewrite.

### The observer comes back as an ID, not a name - and an implementer who forgets will ship blanks

The function's documented return is `DeviceId`: an opaque service identifier. The FQDN the portal
shows on the "Recently seen by" field is the portal joining that ID back to the observer's own
`DeviceInfo` row for `DeviceName`. **The module must do the same join.** Rendering the raw
`DeviceId` would satisfy the letter of "Recently Seen By" and be useless for locating a machine,
which is the entire purpose. Say it in the code comment too; it is the kind of thing that reads as
working right up until someone opens the CSV.

### Correction to a claim this plan and `.agents/state.md` both rest on: `HostDeviceId`

`.agents/state.md` records `HostDeviceId` as "a dead end", populated on 1 device out of 113,102 and
pointing at itself, listed alongside the genuine dead ends as though it were a data gap.

**It is not a data gap and it was never a candidate.** Learn documents the column as:
`HostDeviceId` (`string`) - "**Device ID of the device running Windows Subsystem for Linux**"
(https://learn.microsoft.com/en-us/defender-xdr/advanced-hunting-deviceinfo-table). It is a WSL
host pointer. One row in a hundred thousand is exactly what that column should look like in a
tenant with almost no WSL, and a row pointing at itself is a WSL guest and host being the same
machine. **The measurement was right; the conclusion drawn from it - that a promising lead had
failed - was wrong, and the surprise was unearned.** Recorded here because the same reasoning would
otherwise be repeated the next time someone reads the schema looking for a relationship column.

### No REST endpoint returns this relationship. The hunting API is the only route.

Checked in both directions rather than assumed:

- **WindowsDefenderATP.** The `machine` resource has no seen-by, discovered-by or observer property
  of any kind (https://learn.microsoft.com/en-us/defender-endpoint/api/machine). The device list
  this module already fetches cannot carry it.
- **Microsoft Graph security.** Has no device-inventory resource at all, which is D1's original
  finding and is unchanged
  (https://learn.microsoft.com/en-us/graph/api/resources/security-api-overview).

So the call is **`POST https://graph.microsoft.com/v1.0/security/runHuntingQuery`**, permission
`ThreatHunting.Read.All`, application context, which is the call S4 already makes
(https://learn.microsoft.com/en-us/graph/api/security-security-runhuntingquery). The documented
per-query limits that bound it - 30-day window, 100,000-row result set, at least 45 calls a minute
per tenant, a three-minute per-request timeout and a 50 MB result cap - are the Graph API's own and
are already recorded in T7 from the Graph security API overview. The portal-side figures differ
slightly (10-minute timeout, 64 MB) and are **not** the ones that apply here
(https://learn.microsoft.com/en-us/defender-xdr/advanced-hunting-overview); do not mix them.

**Do not build any part of this on the legacy endpoint.**
`POST https://api.security.microsoft.com/api/advancedqueries/run` carries the retirement notice -
"Retirement began in January 2026" - and the older endpoints stop returning data on 2027-02-01
(https://learn.microsoft.com/en-us/defender-endpoint/api/run-advanced-query-api). This was already
D1's ruling and it binds the new work too.

### The 30-day retention argument does NOT transfer to this module's target set

`.agents/state.md` and this plan both explain the blankness of the discovery data with the same
fact: advanced hunting holds 30 days, the inventory holds devices last seen months ago, so there is
no hunting row left to join to. The fact is right and it is a **product limit, not a tenant
setting** - "Advanced hunting is a query-based threat hunting tool that you use to explore up to 30
days of raw Defender XDR data", with the quota table giving "Date range - 30 days for native
Defender XDR data" (https://learn.microsoft.com/en-us/defender-xdr/advanced-hunting-overview). No
configuration widens it; onboarding a Sentinel workspace is a different product decision and is out
of scope.

**But the inference does not carry.** That measurement was taken across the whole 113,102-device
inventory, which includes tens of thousands of stale and unsupported records. This module's target
set is **Windows devices currently in "Can be onboarded"** - by definition devices that Defender is
*actively discovering right now*, because a discovered device is one an onboarded sensor has
recently seen. Those are the devices most likely to be **inside** the 30-day window, not outside
it. Learn's own note points the same way: a non-onboarded device stays in the portal for more than
180 days when "the device is discovered by an onboarded endpoint on the same network"
(https://learn.microsoft.com/en-us/defender-endpoint/assess-devices) - which is a statement about
inventory retention, not hunting retention, and is precisely why the two sets differ.

**This plan therefore asserts nothing about coverage and asks instead.** It is **R1(i)**: measure
what fraction of the Windows / can-be-onboarded set returns a `SeenBy()` result. If the answer is
high, the requirement is met. If it is low, the fallback below becomes relevant and the owner needs
to hear the number before the module is presented as answering the request.

### Fallback, if and only if R1(i) says `SeenBy()` is sparse

Documented mechanism, presented as a fallback and **not** as the primary design. Device discovery
attributes network activity to non-onboarded devices through the onboarded sensor that observed
them, and Learn names the two action types:

> "when a non-onboarded device attempts to communicate with an onboarded Defender for Endpoint
> device, the attempt generates a DeviceNetworkEvent ... `ConnectionAttempt` - An attempt to
> establish a TCP connection (syn); `ConnectionAcknowledged` - An acknowledgment that a TCP
> connection was accepted (syn\ack)"
> -- https://learn.microsoft.com/en-us/defender-endpoint/assess-devices

In those rows the **observer** is `DeviceNetworkEvents.DeviceId` / `DeviceName` (the onboarded
sensor) and the **discovered device** is the other end, `RemoteIP` or `LocalIP`. Two documented
functions bridge between a device and an IP:

- `AssignedIPAddresses(x, y)` - x is a `DeviceId` or `DeviceName`, y an optional timestamp; returns
  `Timestamp`, `IPAddress`, `IPType` (public or private), `NetworkAdapterType` and
  `ConnectedNetworks`
  (https://learn.microsoft.com/en-us/defender-xdr/advanced-hunting-assignedipaddresses-function).
- `DeviceFromIP()` - maps an IP to the devices assigned it, returning `IP` and `DeviceId`. Learn is
  explicit about its limit: the IP "should be a local IP address. **External IP addresses aren't
  supported.**"
  (https://learn.microsoft.com/en-us/defender-xdr/advanced-hunting-devicefromip-function)

**Why this is a fallback and not the design.** It is an inference chain - IP to device to observer -
where `SeenBy()` is a first-class answer the service computes itself; it costs at least one more
hunting call per run against the same 30-day window and the same quotas; and it is subject to the
`DeviceFromIP()` external-IP limit, which silently excludes exactly the devices seen across a NAT
boundary. Building it before R1(i) has been run would be speculative code on a read path.

### Consequence for the module, and it is the important one

1. **Hunting is load-bearing.** See "The app registration", permission 2. The report cannot answer
   requirement 3 without `ThreatHunting.Read.All`.
2. **The registration must be verified to hold that consent**, because it was consented under the
   old reading where the permission was optional.
3. **The hunting query text widens**, which moves the T7 request-body guard. Widen it exactly; see
   T7.
4. **What the module does when hunting fails and the device list succeeds is already decided and
   must not be re-decided loosely.** T7's rule stands: the list renders, the hunting-fed columns
   read `(unavailable)` with a named reason above the table, and a failed enrichment never blanks a
   column silently. T7 is extended below rather than duplicated here, because two copies of that
   rule is how one of them drifts.

## Field mapping

What the owner asked for, against real field names. Blank is not an option: where something is
not obtainable it says so here so it is not discovered during a demo.

| Owner asked for | Concrete field | Where from | Notes |
| --- | --- | --- | --- |
| **"Recently Seen By" (requirement 3 - the one that carries the purpose)** | the `SeenBy()` function's returned observer `DeviceId`, joined back to `DeviceInfo.DeviceName` for the FQDN | advanced hunting, via Graph `runHuntingQuery` | **Not a column anywhere - a function.** `invoke SeenBy()` piped from `DeviceInfo`; returns the **onboarded** device that saw the target; capped at 1,000 devices per invocation (Q9). Returns an ID, not a name - the join is mandatory or the column ships blank. Returned column name is `DeviceId` per the function reference and `SeenBy` per Microsoft's own example: **R1(h)**. See "Recently Seen By". |
| Discovery sources (requirement 1) | `DiscoverySources` | advanced hunting `DeviceInfo`, via Graph `runHuntingQuery` | **Demoted, and kept only because it is already shipped and costs nothing.** Learn: "Products or services that have seen or reported the device, including when they last reported it" - it names a **product** (MDE, Microsoft Defender for IoT), never a machine and never a place. It does **not** answer the owner's purpose and must not be presented as if it did. It is also mostly blank in this tenant for the reason `.agents/state.md` records under queue 8. |
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

## Location-narrowing candidate fields - PROPOSAL, for the owner to approve or strike

The owner asked for this explicitly: *"anything else that could help narrow a physical loc should
be included in the plan and presented to me for approval."* So this is a proposal. **Nothing in
this section is decided, nothing here is implemented, and a field only enters the report when the
owner says so.** Strike a row and it does not get built.

Three things to hold while reading the table:

- **The target set is NON-onboarded devices.** Several of the strongest location signals in
  Defender exist only for onboarded ones. That limitation is stated in its own column, on every
  row, rather than in a footnote, because a footnote is how half a report ends up empty.
- **Every hunting-sourced row costs the 30-day window and the quotas** already described, and the
  `SeenBy()` rows additionally cost the 1,000-device cap (Q9).
- **Location strength is a judgement about this KIND of data, not about this tenant.** Where the
  strength depends on a local convention - a subnet-to-site map, a naming standard, a tagging
  habit - the row says so and says the strength is conditional. `.agents/repo-guidance.md`
  invariant 7 forbids resting the design on a fact about this environment; asking whether such a
  convention exists is permitted and is what Q7 does.

### Available for a NON-onboarded device (advanced hunting)

| # | Field | Exact source | Location strength | Non-onboarded? | Notes |
| --- | --- | --- | --- | --- | --- |
| L1 | Observing device, then that observer's own network facts | `SeenBy()` -> observer `DeviceId` -> join `DeviceInfo` for `DeviceName` and `DeviceNetworkInfo` for L2-L6 below | **Strongest available.** Microsoft says so: the seen-by relationship "can help determine the network location of each discovered device" - the observer shares an L2/L3 segment with the target | **Yes** - this is the one designed for it | Requirement 3. 1,000-device cap per invocation (Q9); coverage is R1(i); column name is R1(h) |
| L2 | Subnet and address space | `DeviceNetworkInfo.IPAddresses` - "JSON array containing all the IP addresses assigned to the adapter, along with their respective subnet prefix and IP address space, such as public, private, or link-local" | **Strong, conditional** - a subnet is a site only if an IPAM or subnet-to-site map exists to read it against (Q7) | Yes, for the **observer**; for the target only if discovery populated a row for it | Carries the prefix, not just the address, which is what makes it a site key rather than a number |
| L3 | Default gateway | `DeviceNetworkInfo.DefaultGateways` - "Default gateway addresses in JSON array format" | **Strong.** A gateway is typically per-VLAN or per-wiring-closet, so it is effectively a site key even without an IPAM | Same as L2 | The single highest-value field after L1 if no IPAM exists |
| L4 | DHCP server | `DeviceNetworkInfo.IPv4Dhcp`, `DeviceNetworkInfo.IPv6Dhcp` - "IPv4 address of DHCP server" / "IPv6 address of DHCP server" | **Strong.** Microsoft's own corporate-network heuristic keys on network name plus default gateway plus DHCP server, which is a statement that these three together identify a network | Same as L2 | Pairs with L3; two independent site keys agreeing is worth more than either alone |
| ~~L5~~ | **STRUCK (owner, Q7): no site-scoped suffix scheme.** Adapter DNS suffix | `DeviceNetworkInfo.NetworkAdapterDnsSuffix` - "Domain suffix assigned to the device's network adapter, indicating the network environment the network adapter is connected to" | **Strong where suffixes are site-scoped, weak where one flat suffix is used everywhere** (Q7) | Same as L2 | Learn's own description says "indicating the network environment", which is the claim being relied on |
| L6 | DNS servers | `DeviceNetworkInfo.DnsAddresses` - "DNS server addresses in JSON array format" | **Medium-strong.** DNS resolvers are usually regional rather than per-site | Same as L2 | Narrows to a region, rarely to a building |
| L7 | Connected networks | `DeviceNetworkInfo.ConnectedNetworks` - each JSON element carries "the network name, category (public, private or domain), a description, and a flag indicating if it's connected publicly to the internet" | **Medium.** The name is operator-chosen, so its usefulness is exactly the discipline behind it | Same as L2 | Learn's discovered-devices article has a worked query filtering on network name |
| L8 | MAC and adapter vendor | `DeviceNetworkInfo.MacAddress`, `DeviceNetworkInfo.NetworkAdapterVendor`; the machines API also returns MACs on `ipAddresses[].macAddress` | **Weak for location, strong for identity - and useful mainly as an EXCLUSION** | Yes | **This is not a footnote.** The owner's own screenshot shows the example device with vendor `VMware`: a virtual machine, which has **no physical location to find**. Vendor is therefore the field that keeps VMs out of a report about walking to a machine. Without it the report is a list padded with things nobody can go and look at |
| L9 | Site | `DeviceInfo.Site` - "Represents the physical location where the device is located" | **Strong on its face** - it is literally the field being asked for | Yes, when populated | **Licence-gated and must not be assumed.** The DeviceInfo reference gives no licence note, and the claim that it is populated by Defender for IoT Site security (public preview) is an **assumption** (see Assumptions 9). Whether it is populated in this tenant is **R1(k)** - one query answers it, and if the answer is yes it outranks most of this table |
| ~~L10~~ | **STRUCK (owner, Q7): no coherent naming convention.** Device FQDN | `DeviceInfo.DeviceName` - "Fully qualified domain name (FQDN) of the device"; `machine.computerDnsName` on the REST side, already in the report | **Medium, entirely conditional on a naming convention** | Yes | The owner's examples look like a convention exists. **This plan does not assert what any part of a name means**, and no code may parse one - that would be invariant 7 in source. Q7 asks the owner whether a documented convention exists and whether decoding it is wanted |
| L11 | Machine group and tags | `DeviceInfo.MachineGroup` - "Machine group of the device. This group is used by role-based access control"; `DeviceInfo.DeviceManualTags`; `DeviceInfo.DeviceDynamicTags` | **Unknown - demoted to recon item R1(n), not an owner question.** Medium IF groups or tags encode geography, nil otherwise | Yes | Worth knowing before relying on dynamic tags: a dynamic rule "can be based on device name, domain, OS platform, internet facing status, onboarding status and manual device tags" - **there is no IP-range or subnet condition**, so a dynamic tag can never be derived from where a device sits on the network |
| L12 | Hardware identity | `DeviceInfo.Model`, `DeviceInfo.Vendor`, `DeviceInfo.DeviceType`, `DeviceInfo.DeviceSubtype` | **Weak for location; useful for recognising the thing once you are in the room** | Yes, partially | Learn flags `Model`, `Vendor` and `DeviceSubtype` as "only available if device discovery finds enough information about this attribute" - expect blanks, and never let a blank read as a finding |

### L17 - the AD site, derived locally. Not a Defender field at all.

**Added 2026-09-24 on the owner's answer to Q7**, and it is the strongest location signal in this
document after L1, because it is the only one that yields a **name a human recognises** rather
than a number someone has to look up.

Defender has no AD site field and never will - that is still true and is still recorded below
under "not documented anywhere". But this application is an on-prem Active Directory admin tool,
and **Active Directory already holds an authoritative subnet-to-site map**: the configuration
partition's `CN=Subnets,CN=Sites,<configurationNamingContext>`, where each subnet object carries
`siteObject`, the site it belongs to. This is maintained by the network and directory teams as a
matter of course, because AD itself depends on it for replication and client affinity - so it is
kept current for a reason that has nothing to do with this report, which is what makes it
trustworthy.

**This was measured in this forest on 2026-09-25, not assumed - R1(o) records it: 531 subnet
objects, 515 carrying a site, 167 sites.** The subnet `location` attribute, which an earlier draft
of this section expected to be the prize, is **empty on every one of the 531** and is not used.
The site NAME carries the location instead, and carries it better: the names are structured
region / country / state-or-province / city.

So the derivation is local, and needs nothing new from Microsoft:

```
device IP (L2, or the observer's IP from L1)
  -> longest-prefix match against AD's subnet objects
  -> siteObject -> the site's own cn, rendered as-is
```

**Rendered raw. Nothing parses it.** The name is already readable, so splitting it into region and
city would buy nothing and would be precisely the environment-specific format-parsing invariant 7
keeps out of source. Displaying a string the directory returned is not parsing it. **No part of
this plan records what a segment of a site name means, and no test may assert a shape for one.**

**The repo can already reach it.** `Services/SectionAccessGroupDirectory.cs:86-93` reads
`configurationNamingContext` from RootDSE via `Get-ADRootDSE` - the same partition, the same
discovery route, an existing pattern in this codebase rather than a new capability.

**This is invariant-7 compliant, and the distinction matters.** Invariant 7 forbids naming a
domain, host, OU, group or subnet as behaviour, and forbids a safety argument resting on this
environment's shape. It does **not** forbid reading the directory the host is already joined to.
The subnet table is *discovered at runtime from the host's own forest membership*, which is the
exact phrasing invariant 7 uses to describe the permitted shape. No subnet, site or location
string appears in source.

**Three ways this column can be empty, and they must not be collapsed into one.** R1(o) found
that two of them are real in this forest today, so this is not defensive hypothesising:

1. **The device has no IP to match.** Nothing to look up.
2. **The IP matches no subnet object.** 531 subnets do not cover every address in a global
   network, and an unmatched IP is a real answer - it says the device is somewhere the directory
   does not describe.
3. **The IP matches a subnet that carries no `siteObject`.** Measured: **16 of the 531**. The
   subnet is known; its site is not.

None may render as a guess, none may render as another, and **a nearest-match or
shortest-prefix fallback is forbidden** - a confidently wrong city sends someone to the wrong
building, which is worse than a blank in a report whose entire purpose is finding a machine.

| # | Field | Exact source | Location strength | Non-onboarded? | Notes |
| --- | --- | --- | --- | --- | --- |
| L17 | AD site name | AD configuration partition, `CN=Subnets,CN=Sites,<configurationNamingContext>`: subnet object `cn` (the CIDR) and `siteObject`, resolved to the site's `cn`. Matched against the device IP from L2, or the observer IP from L1 | **Strongest human-readable signal.** Turns an IP into a site name the organisation already uses. **Measured in this forest (R1(o), 2026-09-25): 531 subnets, 515 with a site, 167 sites, names structured region / country / state / city** | **Yes** - it depends only on having an IP, not on a sensor | Needs no Defender licence, no new Graph permission and no network-team deliverable. **The subnet `location` attribute is empty on all 531 and is NOT used** - the site name carries the location instead. Rendered raw, never parsed. **Q10 ruled 2026-09-25: an AD read failure FAILS the report** - but an IP that matches no subnet is a blank cell, not a failure |

**Q10 - RULED by the owner, 2026-09-25. The module reads AD itself, and an AD read failure FAILS
THE REPORT.** Verbatim: *"if AD read fails, the report fails. we have bigger problems if none of
our 40+ DCs are reachable."*

This overrules the plan's own recommendation, which was to degrade to an empty column on the
reasoning that a site name is an enrichment. The owner's argument is better and is about this
forest rather than about API etiquette: **with 40-plus domain controllers, a failed directory read
is not a flaky dependency, it is an outage.** Rendering a report with a silently missing column in
that situation would hide a far larger problem behind a cosmetic gap - and would hand an operator
a location report with no locations in it, at exactly the moment they most need to trust it.

**The asymmetry with T7 is deliberate and must not be "harmonised" later.** T7 requires a failed
hunting enrichment NOT to take the page down. AD is the opposite. They differ because the
dependencies differ: hunting is one cloud service with documented per-tenant quotas and throttling
that can fail transiently on a healthy day, while the directory is dozens of servers the entire
environment already depends on. A future reader who notices the two rules disagree should read
this paragraph, not reconcile them.

**What this does NOT mean, and it is the easy mistake:** "the AD read failed" is not "the IP
matched no subnet". The three empty cases listed above stay empty cases - they are answers, and
the report renders normally with those cells blank. Only an actual failure to read the directory
fails the report. A slice that collapses "no match" into "failure" turns 16 unmapped subnets into
a broken module.

### ONBOARDED-only - these describe the OBSERVER, not the target

Stated as its own block because the distinction is the single easiest thing to get wrong here: on a
"Can be onboarded" report, every field below is empty on the row it appears to belong to unless it
is deliberately carried across from the observer that L1 found.

| # | Field | Exact source | Location strength | Notes |
| --- | --- | --- | --- | --- |
| L13 | Egress IP | `DeviceInfo.PublicIP` - "Public IP address used by the **onboarded** device to connect to the Microsoft Defender for Endpoint service. This could be the IP address of the device itself, a NAT device, or a proxy" | **Strong as a site proxy**, because a shared egress usually means a shared site | Learn's own wording names the onboarded device. Carrying it from the observer is meaningful; showing it on a non-onboarded row is not |
| L14 | REST device facts | `machine.lastIpAddress`, `machine.lastExternalIpAddress`, `machine.ipAddresses[]`, `machine.computerDnsName`, `machine.rbacGroupName`, `machine.machineTags` | Medium; same reasoning as L2, L10, L11 | Already in the shipped report for every row the list returns, so these cost nothing new |
| L15 | Find devices by internal IP | `GET /api/machines/findbyip(ip='{IP}',timestamp={TimeStamp})` - machines "seen with the requested internal IP in the time range of 15 minutes prior and after a given timestamp"; the timestamp "must be in the past 30 days"; 100 calls a minute and 1,500 an hour | Medium - turns an IP into the devices that held it | **Recommended AGAINST, on a rule this plan already has.** Its only documented Application permission is **`Machine.ReadWrite.All`** - a write scope, on a read-only module, which is precisely what T4 exists to keep off this registration. Using it would mean re-opening the least-privilege decision with the owner. Advanced hunting's `DeviceFromIP()` answers the same question with no write scope |
| L16 | The logged-on user's own directory location | `DeviceInfo.LoggedOnUsers`, joined to `IdentityInfo` for `City`, `Country`, `Address`, `Department`, `DistinguishedName` | **Medium-strong BY INFERENCE, and the inference is the caveat** | It locates the **user**, not the machine, and the two part company for anything shared, virtual or left behind. `DistinguishedName`'s OU path often encodes a site, which is the strongest part of this row. Note `LoggedOnUsers` needs a sensor, so this is an observer-side field; and `DistinguishedName` is marked as available only with Defender for Identity, Defender for Cloud Apps or Defender for Endpoint P2 licensing - P2 is already a stated prerequisite |

### Not documented anywhere - do not go looking

Recorded so the next reader does not spend a day on it:

- **There is no Active Directory site field** in the MDE device schema or in the `machine` REST
  resource. Not under another name, not in `AdditionalFields` as anything documented.
- **There is no building, floor, room, rack or geo-coordinate field.** `DeviceInfo.Site` (L9) is
  the closest thing that exists, and it is one opaque string.

### "View in map" - a second recon surface, explicitly NOT a design

The portal's device page offers **View in map** among its response actions, and Learn states where
it comes from: "View in map and set criticality are features from Microsoft Exposure Management,
which is currently in public preview"
(https://learn.microsoft.com/en-us/defender-endpoint/investigate-machines).

Its data is the enterprise exposure graph, which **is** queryable from advanced hunting as
`ExposureGraphNodes` and `ExposureGraphEdges`
(https://learn.microsoft.com/en-us/security-exposure-management/query-enterprise-exposure-graph).
That page is also explicit that the labels are tenant-dependent rather than a fixed schema - its
own recipe for finding out is `ExposureGraphEdges | summarize by EdgeLabel`. The labels it happens
to show in examples are `Can Authenticate As` and `CanRemoteInteractiveLogonTo`; **no seen-by,
discovered-by or located-at edge label is documented anywhere.**

So: **enumerate it as recon (R1(l)), do not design on it.** One query lists every edge label the
tenant actually has, which either turns up a relationship worth pursuing or closes the question in
a minute. Committing to it before that would be building on a public-preview feature whose schema
this plan cannot name.

## CSV export

`Services/CsvExport.Write(header, rows)` - the shared writer, which already handles quoting and
neutralises spreadsheet-formula injection. Downloaded through the existing
`JS.InvokeVoidAsync("downloadFile", ...)` path, filename
`DefenderEndpointDevices_yyyyMMdd_HHmmss.csv`, exactly as `BlockedSenders.razor:236-256` does,
and audited the same way (`ExportCsv`, with the row count).

Proposed columns, in order. Multi-valued cells are joined with `"; "`, matching
`NamedLocations.razor`.

**2026-09-24: this is the SHIPPED column set, and it does not yet carry requirement 3.** The header
is asserted verbatim by `DefenderEndpointDevicesCsvTests.ExpectedHeader` rather than derived from
the page (Revision 7), so the list below and that constant have to move together and a human has to
check them against each other. "Recently Seen By" and whichever of L1-L16 the owner approves are
**additive** to this list; nothing here is removed by the revision, and the demotion of
`DiscoverySources` is a demotion in how it is described and relied on, not a deletion of the
column. The one column removal still pending is the R1(e) contingency Revision 7 recorded: if
`ipAddresses` comes back unpopulated, `IpAddresses` and `MacAddresses` leave.

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

**A documented fact that a reader will meet here and must not misread as a reversal (2026-09-24).**
Learn's List machines page does list `$skip` among the OData operators this collection supports,
beside "`$top` with max value of 10,000" and the limitation "Maximum page size is 10,000"
(https://learn.microsoft.com/en-us/defender-endpoint/api/get-machines, and the worked examples at
https://learn.microsoft.com/en-us/defender-endpoint/api/exposed-apis-odata-samples). Revision 3 of
the second series records the opposite-looking live measurement: 10,000 rows back and **no**
`@odata.nextLink`.

**Both are true and they do not contradict each other.** "The service accepts `$skip`" and "the
service emits no continuation cursor" are statements about two different mechanisms; an endpoint
can support a client-computed offset and still have no server-side cursor. What the documentation
does **not** say - and the only thing that would matter - is that `$skip` paging is **stable**,
which needs a documented ordering, and this collection documents no `$orderby`. That is the third
bullet above, and it is untouched by the `$skip` line in the docs.

**The rejection therefore STANDS.** It is a recorded decision with an argument, and
`AGENTS.md` forbids stepping past a guard without first proving it is not load-bearing - which
nobody has. What this revision adds is a way to find out rather than argue: **R1(j)** probes
`?$top=10000&$skip=10000` against the live tenant and records what comes back. Even a successful
probe reopens only the narrow question of whether an offset is stable under a moving window; it is
not by itself the cited source this section asks for. The partition design (Revision 3, second
series) is unaffected either way - it does not need `$skip` and is not waiting on this answer.

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

#### T7 extended, 2026-09-24 - the hunting call stops being optional

**Extended here rather than restated in the "Recently Seen By" section, deliberately: two copies of
a failure rule is how one of them drifts.** Everything above stays exactly as written. What follows
is what changes when the hunting call carries a named owner requirement instead of a nice-to-have
column.

- **Unchanged, and this is the part that matters most:** a hunting failure must never take the
  device list down, and must never leave a column merely blank. Revision 8 of the first series
  fixed a live defect of exactly that shape - two throw paths reaching the page's own catch and
  replacing a device list that had already proved itself complete - and that fix and its guard
  proof stand. Nothing in this revision may weaken them.
- **Changed: what the banner has to SAY.** While enrichment was optional, "these five columns are
  unavailable because X" was a complete statement. It is no longer: with "Recently Seen By" in the
  report, a failed hunting call means **the report does not answer the request it was run for**,
  and the operator has to be told that rather than left to infer it from a greyed column. The
  distinctness requirement on the reason strings
  (`EveryEnrichmentReasonIsADifferentSentence`) is unchanged; the owner's on-screen wording ruling
  of 2026-09-21 - state what happened and what to do, nothing else - is unchanged and binds the new
  sentence too.
- **Changed: the query text widens, so the guard moves.** The shipped guard parses the posted body
  and requires the root `Query` property to EQUAL
  `DefenderEndpointDeviceService.HuntingQuery` exactly, with no second property at all (Revision 8
  of the first series, finding 2, probes M3-M5). Adding `invoke SeenBy()` and the observer-name
  join changes that constant. **Widen the constant; do not widen the assertion.** The guard is the
  only boundary there is - `ThreatHunting.Read.All` cannot narrow this module to any table - and an
  assertion relaxed to `Contains` to make a new query fit would silently re-open the hole M3
  proves is closed. If the fallback is ever built, `DeviceNetworkEvents` and the two IP functions
  enter the constant the same way: named, exact, and one commit at a time.
- **Changed: "once per refresh" may no longer be achievable.** `SeenBy()` accepts at most 1,000
  devices per invocation. One query cannot enrich a can-be-onboarded set of the size
  `.agents/state.md` records. Either the enrichment batches - several calls per run, inside the
  45-per-minute Graph budget and the run's own request accounting - or the report enriches only a
  subset, which would be a different product. **This plan does not choose. It is Q9**, and it is
  the one open question that could change the module's shape rather than its columns.
- **Changed: the `IncludeDiscoverySources` config field is now misnamed.** It is declared at
  `Modules/ModuleCatalog.cs:830` and its description reads "Run the advanced hunting query that
  supplies the discovery sources, device type, vendor and model columns. Turn off when the app
  registration does not hold `ThreatHunting.Read.All`." Once the same call also supplies
  "Recently Seen By", turning that switch off turns off requirement 3 while calling itself a
  discovery-sources switch. The field must be renamed and re-described, or split, when the work
  lands - and renaming a config field is a config-store question, not only a string change, so it
  is called out here rather than left to the implementing slice to discover.

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

**(f) and (g) are ANSWERED** - by the live run recorded in Revision 3 of the second series. (a),
(b), (c), (d) and (e) are still open. The items below were added on 2026-09-24 by Revision 5.

#### R1 items added by the revised requirement, 2026-09-24

**Most of these do not need the module, the deploy or the park lifted.** (h), (i), (k), (l) and (m)
are advanced hunting queries the owner can run in the Defender portal exactly as the 2026-09-22
queries in `.agents/research/defender-discovery-source.kql` were run. Only (j) needs an API call.
Answers are recorded **in this file** before any code is written against them.

- **(h) What column name does `SeenBy()` actually return?** The function reference documents
  `DeviceId`; Microsoft's own published example projects `SeenBy`. Run the published query as
  printed and read the result's column headers. This decides one line of the parser and nothing
  else, but getting it wrong ships a blank column that looks like "no data" rather than a bug.
- **(i) What fraction of the target set returns a `SeenBy()` result?** The decisive coverage
  question, and the one this plan refuses to assert (see "The 30-day retention argument"). Restrict
  to Windows devices with `OnboardingStatus` matching the can-be-onboarded state, invoke
  `SeenBy()`, and record: how many rows went in, how many came back with a non-empty observer, and
  the ratio. **Mind the 1,000-device cap when designing the sample - a truncated input is not a low
  coverage rate, and confusing the two would answer Q9 wrongly as well.** If coverage is low, the
  fallback in "Recently Seen By" becomes live and the owner hears the number before the module is
  presented as answering the request.
- **(j) Does `?$top=10000&$skip=10000` return a second 10,000 rows?** One API call against a filter
  known to match more than 10,000 devices. Record the row count and whether the first row differs
  from the first row of the unskipped request. **This records a fact; it does not reverse the
  `$skip` rejection** - see the 2026-09-24 note in "Why `$skip` is not used", which says why a
  successful probe is still not the cited source that section asks for.
- **(k) Is `DeviceInfo.Site` populated in this tenant, and for non-onboarded devices?**
  `summarize by` it, or count non-empty values split by onboarding status. If it is populated it
  outranks most of the L2-L12 proposal, so it is worth an early minute. If it is empty, L9 comes
  off the proposal and the licence question (Assumptions 9) never needs answering.
- **(l) What edge labels does this tenant's exposure graph actually carry?**
  `ExposureGraphEdges | summarize by EdgeLabel`. Enumeration only. No seen-by or discovered-by edge
  label is documented, so this either turns up something worth pursuing or closes "View in map" as
  a route in one query. **Nothing is designed on it either way.**
- **(m) Does `DiscoverySources` serialise as a JSON string or a JSON array?** Carried forward from
  Revision 6 of the first series, which flagged it as unverified, handled both shapes defensively,
  and said to "add it to R1's list if that gate is still open". It is still open, so here it is,
  numbered. The module is correct either way today; the answer lets the defensive branch be
  simplified and confirmed rather than left as a guess.
- **(n) Do `MachineGroup`, `DeviceManualTags` or `DeviceDynamicTags` encode geography?** Added
  2026-09-24. The owner's answer to Q7 on tags was "no idea", which makes this a query rather than
  an owner question. `DeviceInfo | summarize count() by MachineGroup` and the same for each tag
  column. L11 is built only if the answer is yes, and a handful of geographic values among mostly
  non-geographic ones is a no, not a yes - a column that is right for 5 percent of rows is worse
  than no column.
- **(o) ANSWERED 2026-09-25 by a live read of this forest's configuration partition. L17 holds.**
  Measured, not inferred, by an LDAP read of
  `CN=Subnets,CN=Sites,<configurationNamingContext>` - the same partition and the same discovery
  route `Services/SectionAccessGroupDirectory.cs:86-93` uses:
  - **531 subnet objects.**
  - **515 of them carry `siteObject` - 97 percent.** The map exists and is maintained.
  - **`location` is empty on all 531.** Zero. **Do not build a column for it** and do not treat a
    blank as a data-quality problem to chase; the attribute is simply unused here.
  - **167 sites, and the site NAME is the location.** The names are structured
    region / country / state-or-province / city. That is more than the empty `location` attribute
    would have given, so L17 is stronger than it was written, not weaker.
  **Two consequences for the design:**
  1. **Render the site name raw. Parse nothing.** The name is already human-readable, so there is
     no reason to split it into parts - and splitting it would be exactly the environment-specific
     format-parsing that `.agents/repo-guidance.md` invariant 7 forbids in source. Showing a
     string the directory returned is not parsing it. **This plan does not record what any segment
     of a site name means**, and no test may assert a shape for one.
  2. **Two blank cases exist and they are different.** 16 subnets carry no `siteObject`, and a
     device whose IP matches no subnet at all matches nothing. Neither may render as a guess and
     neither may render as the other. Fail closed and visibly, per L17's own rule.

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

### S6 - "Recently Seen By" - NOT YET SLICED, and deliberately so

S1 to S5 are landed and the module shipped; the descriptor now reads `Version = "1.1.0"`
(`Modules/ModuleCatalog.cs:816`), which supersedes S5's `1.0.0` line above. Requirement 3 is new
work and has **no slice here yet**, because a slice written now would be written over three
unanswered questions:

- **Q9** decides the shape, not the detail: whether the enrichment batches within the 1,000-device
  `SeenBy()` cap or the report narrows to what an operator is looking at. Those are different
  modules, not different implementations.
- **Q7 and Q8** decide which of the L1-L16 candidate fields exist at all, and the owner has been
  asked to approve or strike them.
- **R1(h) and R1(i)** decide the parser and whether the fallback is live.

**The pending step is the owner's, and it is one thing: approve or strike the candidate table and
answer Q9.** The proposed next action after that is to draft S6 against the answers - not before.
Writing a slice against guesses is how a plan acquires a design nobody chose, and this file has a
recorded history of the opposite discipline.

Two further things S6 will have to carry, recorded now so they are not rediscovered: the
`IncludeDiscoverySources` config field is misnamed once hunting feeds requirement 3 (T7 extended),
and `.agents/state.md` holds an **unresolved owner fork about the page being unusable at tenant
scale** which touches the same page S6 would edit. Neither is in this revision's scope; both are in
S6's path.

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

**Two steps below are STALE and are flagged rather than silently rewritten, because this file is
what the next slice reads.** Steps 3 and 4 describe a "Windows only" toggle that **no longer
exists**: Revision 3 of the second series removed it at the owner's instruction and replaced it
with a Platform dropdown defaulting to Any. Re-point both when the checklist is next actually run;
they are not edited here because this revision is scoped to the requirement change and an
un-run checklist step is better left visibly wrong than quietly plausible.

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
7. **Revised 2026-09-24.** Discovery sources are populated for at least one device, and the values
   look like the portal's ("MDE", and whatever else this tenant has). **Expect most cells to be
   empty** - the 30-day hunting window against an inventory of much older devices is the reason
   (`.agents/state.md`, queue 8), and it is not a defect of this module. What this step actually
   proves is that the hunting call ran at all; **a column of blanks and a column of
   `(unavailable)` mean opposite things and the operator must be able to tell them apart on
   screen.** That distinction is the check. Do not read populated discovery sources as evidence
   that the report can locate anything - see "The purpose, and what it reframes".
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

### 2026-09-24 - where the version stands, and what S6 will cost

- **The module is at `1.1.0` today**, set by Revision 3 of the second series and readable at
  `Modules/ModuleCatalog.cs:816`. The `1.0.0` in the bullets above is history, not the current
  value; **read the field, never this file** (T8).
- **This revision is plan text only and bumps nothing.** No `.cs`, no `.razor`, no `.csproj`, no
  descriptor. A docs-only change is not a version event under either rule.
- **Proposed for S6 when it lands: module `1.1.0` -> `1.2.0`, and NO base app version bump.**
  Minor, not patch: adding "Recently Seen By" and any approved location column is new behaviour on
  a shipped module, not a fix to existing behaviour - the same reading the `MessageTrace` split
  took when it went `1.4.2` -> `1.5.0` for a descriptor-level capability change. No base bump
  because nothing S6 needs is shared: the API client is module-local by design precisely so that
  `Services/GraphTokenClient.cs` is never touched (T1), and the widened KQL, the new columns and
  the renamed config field all live inside this module's own files. **This is a proposal computed
  from the field and the rule, and the implementing slice must recompute it from
  `Modules/ModuleCatalog.cs` at the time rather than trusting this line** - the two rules fire
  independently and each has been assumed from the other before in this repo.
- **The one thing that would flip the base-bump call**, and it is worth naming because S6 is the
  first slice with a real chance of tripping it: if batching the hunting call (Q9) turns out to
  need a change to a shared file - `Services/GraphTokenClient.cs`, `Program.cs` beyond additive
  module registration, or `Services/CsvExport.cs` - then the base app version bumps too and that
  slice says so in its commit. Stop and re-read this section if any of those three appear in the
  diff.

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
- **Added 2026-09-24, because the location work is where this invariant is easiest to break.** The
  device names, vendor string and dates read off the owner's screenshot, and the device counts
  `.agents/state.md` records, are **evidence that a route exists** - they are not behaviour and no
  code may key off them. Specifically: **no source file may parse a device-name convention**,
  hardcode a subnet, gateway, DHCP server, DNS suffix or site string, or carry a
  subnet-to-site map. Every location field in the L1-L16 proposal is **rendered as whatever the
  service returned**, exactly as `DnsDomain` already is. If decoding a naming convention or holding
  a site map is ever wanted, it is operator-supplied configuration with a plan of its own, and Q7
  is where that conversation starts - it is not something a column quietly starts doing.

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
7. **Rate limits are per tenant, not per application.** Assumed. The request count is **not a
   fixed number** and must not be written as one: T3 asks for `$top = min(MaxDevices + 1, 10000)`
   in a single request, and how many requests a full run actually costs depends on the page size
   the service chooses to honour, which **R1(g)** observes. Single-request branch: one request.
   Cursor branch: `ceil(matching devices / observed page size)`. Either way the ceiling bounds it,
   and against a 100-per-minute limit there is headroom; it would matter only if this ever polls
   or if several operators refresh together. **An earlier revision of this line assumed a fixed
   `PageSize` of 1000 and a 20-request run. That is obsolete** - the revision 2 paging design
   removed the fixed page size, and carrying the old number into code or into rate-limit
   reasoning is the stale-reference failure class this repo names.
8. **The paging contract.** Whether `GET /api/machines` emits `@odata.nextLink` is **not
   documented** - the only mention on the Defender API pages names Microsoft Graph, not this
   collection (T3). Nothing is assumed: R1(g) probes it with `$top=1` and both branches are
   pre-decided. The one inference the design does rest on is that **the service honours `$top`
   and returns fewer rows than asked for only when the collection is exhausted** (rule P1). That
   cannot be proven from the documentation either, which is precisely why a **full** page with no
   cursor refuses instead of completing: the ambiguous case is routed away from the inference
   rather than through it. `$orderby` is likewise undocumented on this collection, which is one of
   the three reasons `$skip` is not used at all (T3).

### Added 2026-09-24 with the revised requirement

9. **`DeviceInfo.Site` is licence-gated.** What IS verified is the column and its description -
    "Represents the physical location where the device is located"
    (https://learn.microsoft.com/en-us/defender-xdr/advanced-hunting-deviceinfo-table). The
    DeviceInfo reference carries **no** licence note on it. The claim that it is populated by
    Defender for IoT Site security, which is in public preview, is an **assumption** and no Learn
    page was read that says so. It does not matter much either way: **R1(k)** asks the only
    operationally useful question - is it populated in this tenant, for these devices - and the
    answer does not depend on knowing why.
10. **The portal's "Recently seen by" field and the hunting `SeenBy()` function are the same
    relationship.** Strongly indicated - the function reference says it lists "onboarded devices
    that have seen a certain device **using the device discovery feature**", and the portal field
    sits on a discovered device's page - but no Learn page states the equivalence outright. If it
    turned out to be false, **R1(h) and R1(i) would expose it immediately**: the queried result
    would not match what the owner's screenshot shows. This is why those two run before code, not
    after.
11. **`SeenBy()` behaves identically through Graph `runHuntingQuery` as it does in the portal.**
    Enrichment functions are documented on the advanced hunting schema pages without an API caveat,
    and the module's existing `DeviceInfo` query already runs through that API. But Learn's own
    warning on the function - "Enrichment functions show supplemental information only when they're
    available" - is exactly the kind of sentence that hides an interface difference. Assumed;
    observed by **R1(i)**, which cannot produce a coverage number without proving this in passing.
12. **The observing device is physically near the device it saw.** This is the inference the whole
    requirement rests on, so it is written down rather than left implicit. Microsoft supports the
    *purpose* explicitly - the seen-by data "can help determine the network location of each
    discovered device" - but "network location" is not "physical location", and the two part
    company across a routed VPN, a site-to-site link or a virtualised segment. **"Help narrow",
    which is the owner's own phrasing, is the honest claim; "locates" is not.** Any on-screen or
    documentation wording must keep that distinction, per the owner's 2026-09-21 wording ruling.

## Open questions for the owner

1. **CLOSED yes, 2026-09-21 (Revision 5, first series), and now moot.** *Discovery sources cost a
   tenant-wide hunting permission. Grant it?* The only way to get them is `ThreatHunting.Read.All`
   on Microsoft Graph, which lets the app read every advanced hunting table in the tenant (email,
   identity and cloud-app events, not just devices), and its consent needs a Privileged Role
   Administrator or Global Administrator. The owner answered yes. **It would now be unaskable
   anyway**: "Recently Seen By" is obtainable only through hunting, so declining the permission
   would decline requirement 3.
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
6. **CLOSED, 2026-09-21 (Revision 5, first series).** *Module display name: "Defender for Endpoint
   Devices"?* Shipped verbatim, with route `defender-endpoint-devices` and section-access alias
   `DefenderEndpointDevices`.

### Added 2026-09-24 by the revised requirement - these are the ones waiting

**Q2, Q3, Q4 and Q5 above are still unanswered.** The three below are new, and Q8 is the one the
owner explicitly asked to be given.

7. **ANSWERED by the owner, 2026-09-24 - and one part of the answer opened a better route than
   any field in the L-table.**
   - **Subnet-to-site / IPAM map (L2): the owner has none, and the network team may.** But the
     owner named the thing that settles it: **Active Directory Sites and Services**, which is an
     authoritative subnet-to-site map the organisation already maintains. See L17 below; it
     changes the recommendation.
   - **Site-scoped DNS suffix scheme (L5): no.** L5 is **STRUCK**. Do not build it.
   - **Device naming convention (L10): no coherent one.** Owner, verbatim: *"not a coherent one
     that would help here, else we wouldn't need you."* L10 is **STRUCK**. This also retires the
     earlier note that the owner's examples "look like a convention exists" - they do not, and no
     code may parse a device name.
   - **Geographic machine groups or tags (L11): the owner does not know.** That is not an owner
     question, it is a query. Demoted to recon item **R1(n)**: enumerate the distinct
     `MachineGroup`, `DeviceManualTags` and `DeviceDynamicTags` values and see whether any encode
     geography. Build L11 only if they do.
   - The **show-the-raw-value** default stands for anything that survives, for the invariant-7
     reason: a map or a rule is a fact about this environment and does not belong in source unless
     it arrives as operator configuration or from the directory at runtime. **L17 is the second
     of those, not the first**, which is why it is permitted.
8. **Which of L1-L17 goes in the report?** This is the approval the owner asked for. Strike any
   row and it is not built. **Revised after Q7 was answered on 2026-09-24**, which struck two
   candidates and added one better than any that remained:
   - **L1** - the requirement. Recently Seen By.
   - **L17** - AD site and the subnet's `location` string, derived locally from the directory this
     host is already joined to. **The strongest human-readable signal here**, and the answer to
     the subnet-to-site map the owner does not have. **Q10 is ruled** - an AD read failure fails the report.
   - **L2** - subnet. Was "conditional on a map existing"; L17 **is** that map, so L2 becomes
     L17's input as well as a column. Show both - the subnet is the evidence for the site name.
   - **L3**, **L4** - default gateway and DHCP server. Two independent site keys needing no map,
     and the fallback when an IP matches no AD subnet.
   - **L8** - MAC vendor, to keep virtual machines out of a report about walking to a machine.
   - **L9** - only if R1(k) says `DeviceInfo.Site` is populated here.
   - **L11** - only if R1(n) finds geography in the machine groups or tags.
   - **STRUCK by the owner:** L5 (no site-scoped DNS suffixes), L10 (no coherent naming
     convention).
   - **L13-L16** stay out until L1 works; all four describe the observer and are meaningless
     without it.
9. **`SeenBy()` takes at most 1,000 devices per call. Batch, or narrow the report?** The shipped
   module enriches **once per refresh** over the whole matching set, which for this tenant's
   can-be-onboarded population cannot be one call. Two shapes, and they are different products:
   **(a) batch** - the run makes several hunting calls inside the Graph budget and the report keeps
   its present meaning, at more requests, more time and more ways to half-fail; **(b) narrow** -
   enrich only the rows the operator is actually looking at, which is fast and cheap but means the
   CSV export either loses the column or re-enriches on export. **This interacts with the
   unresolved page-scale fork recorded in `.agents/state.md`, which is the owner's and is not
   re-asked here** - if that fork lands on filter-first-and-paged browsing, (b) becomes the natural
   answer and this question may resolve itself. **Recommendation: answer the state.md fork first,
   then this one.**

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

### Added 2026-09-24, all fetched that day for the revised requirement

- `SeenBy()` function (the definition, the syntax, the returned `DeviceId` column, the 1,000-device
  cap, the enrichment-availability caveat):
  https://learn.microsoft.com/en-us/defender-xdr/advanced-hunting-seenby-function
- `DeviceNetworkInfo` table (`IPAddresses` with subnet prefix and address space, `DefaultGateways`,
  `IPv4Dhcp`, `IPv6Dhcp`, `DnsAddresses`, `NetworkAdapterDnsSuffix`, `ConnectedNetworks`,
  `MacAddress`, `NetworkAdapterVendor`):
  https://learn.microsoft.com/en-us/defender-xdr/advanced-hunting-devicenetworkinfo-table
- `AssignedIPAddresses()` function (arguments and returned columns, for the fallback):
  https://learn.microsoft.com/en-us/defender-xdr/advanced-hunting-assignedipaddresses-function
- `DeviceFromIP()` function (returned columns; "It should be a local IP address. External IP
  addresses aren't supported."):
  https://learn.microsoft.com/en-us/defender-xdr/advanced-hunting-devicefromip-function
- `IdentityInfo` table (`City`, `Country`, `Address`, `Department`, `DistinguishedName`, and which
  columns need which licence):
  https://learn.microsoft.com/en-us/defender-xdr/advanced-hunting-identityinfo-table
- Advanced hunting overview (the 30-day date range as a product limit; the portal-side quota table,
  which is NOT the Graph API's): https://learn.microsoft.com/en-us/defender-xdr/advanced-hunting-overview
- Create dynamic rules for devices (the exact condition list - device name, domain, OS platform,
  internet facing status, onboarding status, manual device tags - and therefore the absence of any
  IP-range condition): https://learn.microsoft.com/en-us/defender-xdr/configure-asset-rules
- Investigate devices ("View in map and set criticality are features from Microsoft Exposure
  Management, which is currently in public preview"):
  https://learn.microsoft.com/en-us/defender-endpoint/investigate-machines
- Query the enterprise exposure graph (`ExposureGraphNodes`, `ExposureGraphEdges`, and the
  `summarize by EdgeLabel` recipe that makes R1(l) a one-query question):
  https://learn.microsoft.com/en-us/security-exposure-management/query-enterprise-exposure-graph
- Find devices by internal IP API (the 15-minutes-either-side window, the 30-day timestamp limit,
  the rate limits - and that its only Application permission is `Machine.ReadWrite.All`, which is
  why L15 is recommended against):
  https://learn.microsoft.com/en-us/defender-endpoint/api/find-machines-by-ip

**Re-read on 2026-09-24 and confirmed unchanged** where this revision leans on them:
`advanced-hunting-deviceinfo-table` (for `HostDeviceId`, `Site`, `PublicIP`, `LoggedOnUsers`,
`MachineGroup`, the tag columns and the "only available if device discovery finds enough
information" flags), `assess-devices` (for the published `SeenBy()` query and the network-location
sentence), `get-machines` (for `$skip`, `$top` max 10,000, maximum page size 10,000, the rate
limits and 404-on-empty) and `security-security-runhuntingquery` (for the method, URL,
`ThreatHunting.Read.All` and the 30-day `Timespan` default).

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
# Revision 3 - codex round 3, CONSENSUS REACHED

Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard, 2026-09-18.
Capability proof passed. Verdict **sound_with_changes**, one LOW finding, now fixed.

**Both round 2 findings confirmed closed.** `$skip` is no longer a live path; T3 and S1 refuse on
full-page-without-cursor, on `MaxDevices` exceedance and on any request failure; and the reviewer
independently confirmed the S1 guard test is **not vacuous** by comparing against the design at
`b12a7b1`. The stale S1 live-proof references are gone in substance - body text assigns live proof
to R1, and S1 says stub-only.

**R1(g) was explicitly upheld as an adequate way to handle the undocumented paging contract**,
and the reason is worth keeping because it is the answer to "shouldn't this be resolved before the
owner creates the app registration?": it cannot be. Observing the contract *requires* the
registration. What makes pre-deciding both branches sufficient is that no multi-page behaviour
ships that R1(g) has not observed, and every ambiguous no-cursor state refuses rather than
rendering or exporting a partial list. The uncertainty is bounded by refusal, not by hope.

**The one finding (LOW):** Assumption 7 still carried `PageSize 1000` and a fixed 20-request run
from before the revision 2 paging design removed the fixed page size - the stale-reference failure
class again, and the second time it has appeared in this plan across three rounds. Fixed: the
request count is now expressed in terms of the chosen `$top`, the service-observed page size and
the branch R1(g) records, with the obsolete number named as obsolete so it cannot be carried into
code.

**This plan has reached codex consensus.** It stays `Status: Draft` because consensus is not the
same as approval here: the owner still has to answer the six open questions - question 1 above all,
since it decides whether S4 exists and who must grant consent - and create the app registration,
which is theirs to do and which no amount of planning can substitute for.
# Revision 4 - S1 implemented, and a tension S1 surfaced, 2026-09-19

S1 landed call-free as designed. Four corrections and one open tension, all found by implementing
it rather than by reading it.

**The tension, and S2's author must read this before starting.** R1(g) states that **no multi-page
behaviour ships that R1(g) has not observed**, and R1 is gated between S2 and S3. But S1's own
bullet list mandates the cursor-chain code and its tests, and **S2 ships a reachable page** - so
cursor-following becomes reachable one slice before the gate meant to authorise it. S1's section is
the more specific instruction and the ambiguous-state refusal bounds the risk either way, so the
code shipped; but the ordering is genuinely inconsistent as written. The likely resolution is to
state in S2 that the module ships `EnabledByDefault = false`, so "reachable" means "reachable by
the owner on dev" - which is where R1 runs anyway. **That is a proposal, not a ruling.**

**T6.1 asks for escaping tests over "the device name box", which does not exist** anywhere in the
design - S2's control list has only two filters, onboarding status and Windows-only. The escaping
tests were written against the onboarding-status value, the only operator-supplied string that
reaches a filter.

**Two additions beyond the plan text, made by the implementer and flagged as theirs.** First,
`GetWithStatusAsync` refuses an absolute continuation URL on a different host or over plain HTTP,
and refuses it **before acquiring a token** - the absolute-URL path exists to follow a link out of
a response body, and following an arbitrary host would hand this registration's bearer token to
whatever that body named. Second, a run stops after 100 requests, derived from the documented
per-minute limit, and **refuses** rather than returning what it has. Both are tested.

**One pinned shape was deviated from, additively.** The plan pins `(JsonDocument?, HttpStatusCode,
string?)`. The implementation returns that plus a `TimedOut` flag, because a 3-tuple's only free
slot is the status, and mapping a client-side timeout onto 408 makes it indistinguishable from a
service-issued 408 - the collapse T7 forbids. `ServiceIssued408_IsNotReportedAsAClientSideTimeout`
pins the distinction.

**Confirmed against the plan:** `Program.cs` is in S1 and the plan pre-clears it as additive module
registration, so no base app bump - `ExchangeAdminWeb.csproj`, `Modules/ModuleCatalog.cs` and
`Services/GraphTokenClient.cs` are all unchanged. `DiscoveryEnrichment` state ships in S1 as
`NotAttempted`; S4's five per-device enrichment properties deliberately do **not**, because S4 is
droppable on the owner's question 1 and they would be dead code.

Everything downstream still needs the owner: the app registration, question 1, and R1(g) - which
cannot run before the registration exists, as Revision 3 upheld.

# Revision 5 - S2 implemented, and the R1(g) tension resolved, 2026-09-21

S2 landed: the descriptor, `Components/Pages/DefenderEndpointDevices.razor`, the catalog-count and
alias assertions, four house-style descriptor facts, and a click-gating registry entry written with
the page rather than bolted on afterwards. Two owner answers arrived with the slice and both change
what was written above.

**Owner answer to Q6: the display name is "Defender for Endpoint Devices".** Shipped verbatim, with
route `defender-endpoint-devices` and section-access alias `DefenderEndpointDevices`. Q6 is closed.

**Owner answer to Q1: discovery sources ARE wanted - a direct request from senior leadership.** So
`ThreatHunting.Read.All` will be granted, consent must be done by a Privileged Role Administrator or
a Global Administrator (see "Admin consent"), and **S4 is no longer droppable**. The consequence for
this slice: `IncludeDiscoverySources` ships in S2 as the descriptor's third config field, and the
`GraphDelineaSecretId` description names both `Machine.Read.All` on WindowsDefenderATP and
`ThreatHunting.Read.All` on Microsoft Graph. Deferring the switch was correct only while S4 might
never exist; that reasoning is superseded and the field is live now, so the operator can turn the
enrichment off on a deployment whose registration does not yet hold the grant. Q1 is closed.

**The Revision 4 tension is moot - but NOT for the reason Revision 4 proposed.** Revision 4 offered
`EnabledByDefault = false` as the likely resolution: cursor-following becomes reachable in S2, one
slice before the R1(g) gate meant to authorise it, and shipping the module disabled would mean
"reachable" only means "reachable by the owner on dev". **That cannot be what resolves it, because
`EnabledByDefault = false` is already required independently** - the Constitution's optional-module
rule mandates it and `tools/validate-module-package.ps1:202` raises CAT004 when a descriptor omits
it. A property the module was going to carry anyway resolves nothing; it would have been the same
with or without the tension.

**The real answer is that the shipped paging is reactive, not speculative.**
`DefenderEndpointDeviceService` reads `@odata.nextLink` out of the response body
(`TryReadPage`, `DefenderEndpointDeviceService.cs:384-385`), stops when it is absent
(`:233`) and follows it only when the service emitted one (`:267`). There is no code that
*assumes* a cursor exists and no request that would be issued on a guess. R1(g)'s two pre-decided
branches are therefore not two implementations of which one is unproven: they are one piece of code
dispatching on what actually came back. The no-cursor branch is the `:233` exit, the cursor branch
is the `:267` exit, and the ambiguous state between them - a full page with no cursor - exits
through the same refusal the ceiling uses. So "no multi-page behaviour ships that R1(g) has not
observed" is satisfied by construction: the multi-page path cannot execute unless the service has
already demonstrated the contract in the response that triggers it. R1(g) remains worth running -
it records the observed page size, which is what turns the request budget in T3 from an unknown
into a number - but it is no longer a gate on correctness.

Reachability is separately gated by three independent owner acts, none of which this slice
performs: enabling the module in Admin Settings, granting a group the `DefenderEndpointDevices`
section-access alias, and entering the Secret ID on the module's own config page. That is defence
in depth, not the argument; the argument is the paragraph above.

**Click gating: registered, not deferred.** The page is in `ClickGateRegistry.Pages` rather than
`NotYetConverted`, which was cheap because the page was designed for it: one operation, one
in-flight flag (`isLoading`), one page-wide predicate (`IsBusy`), no staged-confirmation state, no
banner to dismiss, every click target a `<button>`, and no keyboard handler at all - so
`ExcludedFields`, `ExemptControls`, `NonButtonTargets`, `KeyboardPaths` and `HarmlessKeyboardPaths`
are all legitimately empty and the two hardest assertion families are vacuous rather than waived.
Two DOM-synced controls (the onboarding-status `<select>` and the Windows-only checkbox) are
registered with their verbatim `disabled="@IsBusy"` attributes, and both filters are registered as
`CapturedAtEntry` snapshot obligations.

**One deviation from the S2 sketch, and the reason for it.** The sketch had `OnInitializedAsync`
end with `isLoading = DeviceService.IsAvailable;` so the first paint shows a spinner - the shape
`ServiceHealth.razor:340-341` uses. That shape cannot be registered: `isLoading` is the page's
in-flight flag, and `ClickGateTests.EveryRegisteredFlagIsLoweredInAFinally` and
`SingleFlightHandlersRaiseAFlagBeforeTheirFirstAwait` both require every raise of a registered flag
to sit inside a try whose finally lowers it, before that handler's first await.
`OnInitializedAsync` has neither. ServiceHealth gets away with it only because it is listed in
`NotYetConverted` and nothing checks it; here it would be a stuck-flag raise, and under a page-wide
predicate a stuck flag does not grey one button, it deadens the page. The first-render spinner is
therefore keyed on `result == null` instead, which costs nothing: the deferred load starts from
`OnAfterRenderAsync` on that same render, and the branch strictly contains the sketch's
`isLoading && result == null` condition.

**Confirmed against the plan and unchanged by this slice:** module `1.0.0`, and **no base app
version bump** - `ExchangeAdminWeb.csproj` stays at 2.21.1 / 2.21.1.0. `Program.cs` was already
complete from S1 (the named `HttpClient` and the singleton registration) and was not touched.
`Services/GraphTokenClient.cs` was not touched. `tools/validate-module-package.ps1` was NOT run
against this change and must not be reported as passing: it requires `-PackagePath` and validates a
contributed-package layout, which an in-repo module slice does not produce.

**Still outstanding, all of it the owner's:** the app registration itself, the two consents, the
Delinea secret and its Secret ID, then R1 on dev - and Q2, Q3, Q4 and Q5 remain unanswered.
# Revision 6 - S4 implemented, 2026-09-21

Discovery sources shipped. Three corrections and one flagged unknown, all found by implementing it.

**The enrichment warning belongs ABOVE the table and S2 shipped it below.** The Field mapping
section says "the page says why above the table"; S4 moved it. Recorded so it is not helpfully
moved back.

**A 200 with an unreadable body is a second door into the malformed state.** The plan anticipated
malformed only as "no results collection". Without the extra branch the operator reads "Microsoft
Graph rejected the advanced hunting query (200 OK)", which is a contradiction in one sentence.
Tested both ways.

**Eleven distinct reason strings, not the eight the plan enumerated** - the code has three more
doors: credentials unavailable, no devices to enrich, and a listing that refused. All eleven live
in one type so the model's refusal factory and the service cannot drift apart, and the distinctness
test runs seven of them through the real service rather than asserting over constants. It also
asserts **no reason names `Machine.Read.All` or `api.securitycenter`** - those belong to the device
list's own 403, and borrowing that text would send an operator to check a permission that is
demonstrably working.

**Unverified and R1-class: whether `DiscoverySources` serialises as a JSON string or a JSON array.**
No Learn page settles it and WebSearch is blocked in this environment. Both shapes are handled and
joined with the house separator, so the module is correct either way - but **reading only the string
shape would silently blank the one column senior leadership asked for**, which is why it is called
out rather than left to the merge code. Add it to R1's list if that gate is still open.

**One judgement call, stated plainly:** the hunting call reads the Delinea secret a second time
within a run rather than sharing the listing's read. The service is a singleton, so sharing would
mean holding a credential in a field - which is how a rotated client secret keeps failing until the
app pool recycles. The cost is one extra Secret Server round trip per run.

# Revision 7 - S3 implemented, and the R1(e) decision taken without R1, 2026-09-21

The CSV export shipped. **S3 ran BEFORE R1**, which the plan gates it behind, and the rest of this
section is the record of why that was safe and of the one decision it forced.

**Only R1(e) touches S3, and its answer was pre-empted deliberately.** R1's other questions are
about the listing - whether `CanBeOnboarded` devices come back at all, the casing of
`onboardingStatus`, the `osPlatform` values present, server-side `startswith`, how close this
tenant runs to the ceiling, and the paging contract. Not one of them changes a column. R1(e) does:
it asks whether `ipAddresses` is populated on list responses, and the plan says that if it is not,
`IpAddresses` and `MacAddresses` leave the CSV before S3 writes it.

**Decision taken with the slice, recorded here so it does not read as an oversight: both columns
are IN.** It was not the owner's call and is not presented as one - it is the implementing slice's,
and R1(e) can still overturn it. The reasoning is asymmetric cost. If R1(e) comes back empty,
removing two columns from the header list and two cells from the projection is a five-line revision
with a test to update. If they had been
left out and R1(e) comes back populated, the module would have shipped, been documented in S5, and
been used - without the MAC addresses that are the only way to identify a discovered device that
has no name yet, and which the machines API hands over for free alongside the IPs it was asked for.
The columns are written from `ipAddresses[].ipAddress` and distinct `ipAddresses[].macAddress`, and
an absent collection parses to empty rather than throwing (S1), so the empty answer costs two blank
columns until someone deletes them. **If R1(e) comes back empty, delete `IpAddresses` and
`MacAddresses` from `BuildCsv`'s header and projection and from `ExpectedHeader` and the indices in
`DefenderEndpointDevicesCsvTests`, and record it as Revision 8.**

**Twenty-seven columns, in the plan's order, unchanged from the "CSV export" table.** The header is
asserted verbatim in `DefenderEndpointDevicesCsvTests.ExpectedHeader` rather than derived from the
code, so a column moved in the page has to be moved by a human who can check it against this file.

**`BuildCsv` takes a second argument the plan's bullet did not name, and it is load-bearing.** The
bullet says "a `static internal` method taking the row list". It takes the row list AND the run's
`DefenderDiscoveryEnrichmentState`, because the five enrichment columns cannot be rendered from a
device: an empty `DiscoverySources` means "this device has no value" on a succeeded run and "NO
device has one, the query never ran or failed" on any other, and only the RUN knows which. That is
the same reason Revision 6 put the state on the result rather than on the device. Passing it in is
also what lets the two readings be proven apart by a test with no page instance.

**How a non-succeeded enrichment reads in the file, and the one way it deviates from the screen.**
Any state but `Succeeded` writes `(unavailable)` into all five columns, from
`DefenderEndpointDeviceService.EnrichmentUnavailable` - the same constant the page uses, so the file
and the screen cannot describe the same run differently, and a test asserts the CSV cell equals
`DescribeEnrichmentCell`'s output for the same state. The deviation: on a **succeeded** run a
genuinely empty value is written blank, where the screen shows "-". `CsvExport` neutralises a
leading `-` into `'-` (`Services/CsvExport.cs:50`), so the screen's placeholder would reach a
spreadsheet as a literal apostrophe-dash, and it would be the only column in the file carrying a
placeholder at all. Nothing is lost: once `(unavailable)` owns the failed-run meaning, a blank can
only mean the run worked and that device had no value. The direction that matters - a blank
standing in for a run that never executed - cannot happen.

**Two formatting decisions the plan's table did not specify.** The timestamp columns are NAMED
`FirstSeenUtc` and `LastSeenUtc`, so they are written in UTC with the `u` format - sortable, and
always invariant-culture, so an exported file does not change shape with the host's locale. The
table above them deliberately still shows local time, because that is what the operator reading the
screen wants. `IsAadJoined` renders lowercase `true`/`false`, matching `NamedLocations.BuildCsv`.

**The export is offered from the complete branch and nowhere else, and that is now asserted.** A
refusal carries zero devices by construction, so an export offered from that state would write a
header-only file - indistinguishable from "no devices matched" to whoever opens it, and the refusal
banner that explained it does not travel with the file.
`DefenderEndpointDevices_OffersNoExportFromTheRefusalBranch` brackets the single button between the
first element of the complete branch and the device table's closing tag, which is a span entirely
inside that branch; an anchor below the branch alone would also be satisfied by a button placed
after the whole if/else chain. Honest limitation, stated as everywhere else in this file: that is
textual position in the markup, not a render.

**Click gating, updated with the slice rather than after it.** `isDownloadingCsv` is the second
member of `IsBusy`. The button carries two registry obligations a CSV control on a page-wide
predicate attracts: an `AnnotatedControl` at line 172, because `ExportableDevices.Count == 0` is a
precondition a mechanical rewrite to the bare predicate would delete, and a `RaiseAfterEarlyReturn`,
because the raise must stay below the empty-set return that no `finally` covers - on a page-wide
predicate that raise would not grey one button, it would deaden the page. `ExpectedLineCount` moved
430 -> 612 and the two `DomSyncedControls` moved 65/75 -> 66/76, because the page gained
`@inject IJSRuntime JS` above them.

**Two stale line references in this file, found by following them.** S3's bullet cites
`DhcpAuthorization.razor:174` as the `BuildCsv` pattern; it is at **187**. The "CSV export" section
cites `BlockedSenders.razor:236-256` for the download-and-audit shape; that method now runs
**230-256**. Both patterns are still correct - only the coordinates drifted. Recorded rather than
silently corrected, because this file is the thing being read by the next slice.

**Confirmed against the plan and unchanged by this slice:** module `1.0.0` in
`Modules/ModuleCatalog.cs:760`, and **no base app version bump** - `ExchangeAdminWeb.csproj` is
byte-identical (`git diff --stat` empty) and stays at 2.21.1 / 2.21.1.0. `Program.cs`,
`Services/GraphTokenClient.cs`, `Services/DefenderEndpointDeviceService.cs`,
`Services/DefenderApiClient.cs` and `Models/DefenderDeviceModels.cs` were all untouched: S3 is the
page, its tests, and the click-gate registry entry.

**Still outstanding, all of it the owner's:** the app registration, the two consents, the Delinea
secret and its Secret ID, then R1 on dev - including R1(e) above and the `DiscoverySources`
serialisation question Revision 6 added. S5 (documentation and the README section) is the only slice
left, and it is not this one's: S3 was the last code slice.

# Revision 8 - codex review of the finished module, 2026-09-21

Reviewer: codex, `@azure-openai-eus2-global/gpt-5.5-dzs`, effort xhigh, against `bb37bc9`, with the
suite at 2923 passed / 0 failed / 3 skipped. Capability proof passed. Verdict **unsound**, two
findings, both fixed here. Full result in `.agents/review/q8-module.result.json`.

**What it cleared, and that is the half that mattered.** No path renders or exports a short device
list as complete, and no page or CSV blank can mean a failed enrichment run - T3 and T7's central
promises hold, and neither was re-litigated here. The defect was narrower and lay entirely on the
enrichment side of a run whose first half had already succeeded.

## Finding 1 (HIGH), verified and fixed - a hunting-side exception took a COMPLETED list down

`GetDiscoveryEnrichmentAsync` handled a null client and a status-bearing `DefenderApiResult` and
nothing else, and two throw paths reached it. `BuildClientAsync` throws `InvalidOperationException`
three ways for a missing, unreadable or incomplete Delinea secret. `DefenderApiClient` throws when
the token request comes back non-2xx, and its send catches only `TaskCanceledException`, so a
transport failure or an unreadable token body escapes it too. Either one reached the page's own
catch, which sets `result = null` - so a device list that had already PROVED itself complete was
replaced by a page-wide failure and no table.

**The call site's own comment convicted the code.** It reads "its failure is independent of the
listing's success ... It must never take the page down, and it must never leave those columns
merely blank." That was the contract; the code did not honour it. The reviewer is right and the
finding is accepted without qualification.

**The fix: two narrow try blocks, one per call that leaves this process.** The factory call and the
POST are wrapped separately, so each converts into its own reason and neither can borrow the
other's sentence. A factory throw is `NotAttempted` with the existing `CredentialsUnavailable` -
nothing was sent, which is exactly what that constant already says. A POST throw is `Failed` with a
new constant. Both reasons are FIXED strings, never `ex.Message`: the credential messages name a
Secret ID and a Secret Server condition and the token message names a sign-in status, and
`DefenderApiClient` already refuses to echo an auth body for precisely that reason.

**What is caught, and what deliberately is not.** The catches are
`catch (Exception ex) when (IsHuntingSideFailure(ex))`, and that predicate names five types:
`InvalidOperationException`, `HttpRequestException`, `JsonException`, `KeyNotFoundException` and
`TaskCanceledException`. Each is a path that exists today, not a defensive guess - the doc comment
on the predicate records which code throws which. **A blanket `catch (Exception)` was rejected**: it
would also swallow a defect in this module's own parsing and merging and report it to the operator
as a Graph failure, and a bug that greys five columns and blames Microsoft is a bug nobody ever
finds. For the same reason the read of the response body - `TryReadHuntingResults` and the
`MalformedResponse` branch - sits OUTSIDE both try blocks. Anything not on the list still escapes
and still takes the page down, which is what an unexpected exception should do.

`TaskCanceledException` is on the list only because no `CancellationToken` is plumbed through this
call chain, so it cannot be a caller's cancellation being swallowed; the predicate's remarks say so,
and say to revisit that entry if one is ever added. On the POST side the client already converts a
cancellation into the `TimedOut` reason, so that entry bites only on the Secret Server read.

**One new reason constant, `DefenderDiscoveryReasons.SendFailed`, because none of the nine fitted.**
The existing reasons are either not-attempted states or answers off the wire; this is the case where
the module reached for Graph and got no status at all, so there is nothing for
`DescribeEnrichmentFailure` to read. Reusing `Unauthorized` would name a 401 that did not happen and
send the operator to check a client secret that may be fine; reusing `TimedOut` would name a timeout
that did not happen. It is called `SendFailed` and NOT `RequestFailed` on purpose:
`DefenderDeviceListOutcome.RequestFailed` already owns that word in this module and means the DEVICE
LIST failed, which is the one thing this reason promises did not happen.
`EveryEnrichmentReasonIsADifferentSentence` now collects twelve reasons rather than eleven and stays
green; collapsing the new one onto `TimedOut` fails it (probe M6).

## Finding 2 (MEDIUM), fixed - the query guard proved the safe text appeared, not that it ran

`ThreatHunting.Read.All` is scoped to the whole hunting schema, so the permission cannot narrow this
module to `DeviceInfo` and the request body is the only boundary there is. The guard asserted only
that the body CONTAINED `"Query"` and CONTAINED the constant, which a broken implementation could
satisfy while posting broader KQL as the real `Query`, or by appending to it.

The body is now parsed as JSON and the root `Query` property must EQUAL
`DefenderEndpointDeviceService.HuntingQuery` exactly. Two further assertions close the escape hatch:
no second property whose name contains "query" in any casing, and - strictly stronger, and not
redundant, because `runHuntingQuery` also accepts `Timespan` - no second property at all. Three
mutations prove the three halves independently (M3, M4, M5), and the first of them, appending
`| union DeviceNetworkEvents` to the posted query, PASSES the assertions this revision replaced.

## What was deliberately not changed

- **The page.** `Components/Pages/DefenderEndpointDevices.razor` is untouched. Its catch is correct
  for what it is - a listing failure should clear the result - and the defect was that enrichment
  exceptions were reaching it at all. Fixing it there would have been fixing the symptom one layer
  too late, and would have required the page to distinguish two failure sources it cannot see.
- **The version.** Module stays `1.0.0`, no base app bump; `ExchangeAdminWeb.csproj` is
  byte-identical (`git diff` empty) at 2.21.1 / 2.21.1.0.
- **`Services/DefenderApiClient.cs`.** Widening its catch was considered and rejected: it is the
  layer that must NOT decide what a failure means to an operator, and turning its throws into
  results would have changed behaviour for the inventory call as well - the one call that SHOULD
  take the page down when it fails.

## Guard proof - six probes, each attributable to one test

| Probe | Mutation | Test that failed |
| --- | --- | --- |
| M1 | remove the factory catch | `AHuntingClientFactoryThatThrows_LeavesTheCompletedListStandingAndSaysWhyInFixedWords` |
| M2 | remove the POST catch | `AHuntingTokenRequestThatFails_LeavesTheCompletedListStandingAndSaysWhyInFixedWords` |
| M3 | append `\| union DeviceNetworkEvents` to the posted query | `TheHuntingCallIsOnePostToRunHuntingQueryOnTheGraphHostCarryingTheConstantKql` |
| M4 | add a second `QueryOverride` property carrying the wider KQL | same |
| M5 | add a `Timespan` property beside a correct `Query` | same |
| M6 | collapse `SendFailed` onto `TimedOut` | `EveryEnrichmentReasonIsADifferentSentence` |

Every probe failed exactly one test with 91 of 92 still passing, so each is attributable. Every
restore was a file copy followed by `touch` and a SHA256 comparison against the pre-mutation hash -
no `git checkout --` at any point.

## Verification

`dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental` 0 errors / 23 warnings;
`dotnet test ExchangeAdminWeb.slnx` 2925 passed / 0 failed / 3 skipped, +2 on the 2923 baseline and
the two new tests account for it exactly; `dotnet format ExchangeAdminWeb.slnx --verify-no-changes
--no-restore` exit 0; `git diff --check HEAD` exit 0; `tools/Test-AsciiOnly.ps1` exit 0.

## Still outstanding, all of it the owner's, and unchanged by this revision

The app registration, the two consents, the Delinea secret and its Secret ID, then R1 on dev -
including R1(e) and the `DiscoverySources` serialisation question - and the manual acceptance
checklist. Q2, Q3, Q4 and Q5 remain unanswered.

## Revision 3 - the live run, and the rebuild it forced, 2026-09-21

The owner created the app registration, granted and consented both permissions, created the
Delinea secret, entered the Secret ID, deployed to dev and clicked Load. The module authenticated,
reached the real service, and refused with an empty page:

> The Defender for Endpoint API returned exactly the 10000 devices this request asked for and gave
> no continuation link... No devices are shown and no export is offered... Narrow the filters.

Owner, verbatim: *"we have well over 40000 devices. we need this to work in our environment. I
will not approve any code that limits what this can do."* And: *"anything that is obtainable via
Microsoft's portal needs to be obtainable here. No compromises."*

The refusal was correct - the run genuinely could not prove it had seen every device - and useless.
"Narrow the filters" named an action the operator could not take, because the only server-side
filter on the page was one dropdown.

### The two R1 questions this run answered

**R1(g) - the paging contract. ANSWERED: there is no cursor.** `GET /api/machines` returned exactly
the 10,000 rows the request asked for and carried no `@odata.nextLink`. The endpoint has no
continuation token. The branch R1(g) decided in advance was "the module stays single-request", and
that branch is now known to be **unusable on a real tenant**: a single request can return at most
10,000 rows against an inventory of more than 40,000, so the single-request design can never
produce a provable answer here. R1(g) recorded the fact; this revision replaces the design that
fact invalidates.

**R1(f) - does the result set approach `MaxDevices`? ANSWERED: it exceeds it.** The descriptor's
default of 20,000 was below the size of the tenant, so even with paging solved the first load would
have refused on the ceiling. The default is raised to 100,000, which is what the ceiling was always
for - a runaway stop on what one operator's circuit will hold in memory, not a statement about how
many devices may exist.

The remaining R1 questions - (a), (b), (c), (d), (e) - are **still unanswered**. Nothing in this
revision depends on any of them: (c) and (d) are why the "Any Windows" platform choice stays
client-side, and (b) is why the parser still reads properties case-insensitively.

### The design: partition the QUESTION, not the answer

Paging divides an answer the service has already computed; with no cursor there is nothing to
divide. So the module divides the question instead.

The inventory is partitioned on `lastSeen` into parts that are **disjoint** and together
**exhaustive**. Each part is asked for on its own. A part that comes back under the cap has proved
itself complete by the unchanged P1 rule. A part that comes back AT the cap has proved nothing, and
is split in two and asked again. The answer is the union of the parts that proved themselves.

Why this is a proof where `$skip` was not - the argument Revision 2's Finding A rejected `$skip`
on, and the reason this is not the same shape:

- Nothing depends on the service's ordering. Each part carries its own server-side predicate;
  two sibling parts are `lastSeen lt X` and `lastSeen ge X` for one literal X. No device satisfies
  both. No device with a `lastSeen` value satisfies neither.
- Nothing depends on a stable window. Each part is an independent question with a fixed answer set.
- Each part proves its own completeness positively, by P1: it asked for N and got fewer than N,
  with no continuation link. There is no "we probably saw everything".
- **The refusal machinery is unchanged.** A part that cannot be divided, a run that exhausts its
  request budget, a failed request, or a breached ceiling all refuse and carry zero devices. What
  changed is that the common case no longer reaches any of them.

**`lastSeen` is filterable with range operators, and that is documented rather than assumed.**
Learn's List machines page names `lastSeen` in the `$filter` list for this collection, and the
OData samples page carries a worked range example on this exact endpoint:
`GET /api/machines?$filter=lastSeen gt 2018-08-01Z`. The literal is emitted unquoted, in UTC, at
the tick precision the service's own samples use.

**The devices with no `lastSeen`.** A null satisfies neither `ge` nor `lt`, so a partition built
only from intervals silently omits them - the exact failure this design exists to prevent. Two
things stop it:

1. While the root part needs no dividing it carries **no `lastSeen` clause at all**, so those
   devices are in the one request like everyone else. A small tenant costs one request and the
   question never arises.
2. The moment the root has to be split, a separate part asking `lastSeen eq null` is queued -
   **unless the operator set a Last seen window**, in which case devices with no Last seen value
   are outside what they asked for and adding them would answer a different question.

`lastSeen eq null` is core OData v4 and this is an OData v4 surface, but unlike `lastSeen gt` it
has no worked example on this collection. If the service rejects it with a 400, the run **refuses**
and the refusal names the one action that makes the question answerable: set a Last seen window, at
which point the null case is no longer part of what was asked.

**Parts are visited in ascending time order.** A device that reports in mid-run moves FORWARD in
`lastSeen`, so visiting low parts first means it can only move into a part not yet read. The stack
is pushed high-then-low so the low child pops first; the no-`lastSeen` part is pushed last so it
pops first of all, and a device that acquires its first `lastSeen` mid-run lands in the final,
unbounded-above part. This rests on `lastSeen` being non-decreasing for a device, which is what
"last seen" means - not on anything about this environment.

**Termination, and the budget.** The split point is the median of the timestamps the ambiguous
response actually returned, restricted to values STRICTLY INSIDE the part. Strictly-inside is the
whole termination argument: both children are strictly narrower than the parent, widths are whole
numbers of ticks, so the recursion cannot descend forever. A part with no eligible value cannot be
divided and refuses. The split point comes from the data rather than from arithmetic on the clock
because bisecting wall-clock time between an epoch and now would spend a dozen requests walking
down to the few days most devices were last seen in; the data lands the split in the middle of the
population. Balance is not guaranteed - the returned rows are an arbitrary subset - but correctness
never depended on balance, only the request count does.

`MaxRequestsPerRun` rises from 100 to **250**. 100 was chosen for a design that issued one request.
A balanced division of a 100,000-device tenant at 10,000 rows a part is 16 leaves, so 31 requests
plus the no-`lastSeen` part; 250 leaves roughly eight times that headroom for an unbalanced
division while staying a sixth of the endpoint's documented 1,500 calls an hour. It is a STOP for a
partition that terminates but does not converge, not a promise: the endpoint's other documented
limit, 100 calls a minute, is enforced by the service as a 429, which already refuses with its own
operator action.

**Rows from an ambiguous part are not in the answer.** They are counted against the ceiling - they
are real devices that matched - but only keys from parts that proved themselves are promoted into
the union. Nothing is lost, because a child part re-reads them; what is gained is that the answer
is by construction the union of parts that each proved themselves, and a partition whose two
children do not meet loses the boundary device visibly instead of having it already in hand from
the parent.

### Filtering - portal parity

The two hardcoded filters are replaced by the machine record's real fields. Which side each one
runs on is a fact about the API, taken from Learn's List machines page, which names the properties
`$filter` accepts on this collection: `computerDnsName`, `id`, `version`, `deviceValue`,
`aadDeviceId`, `machineTags`, `lastSeen`, `exposureLevel`, `onboardingStatus`, `lastIpAddress`,
`healthStatus`, `osPlatform`, `riskScore`, `rbacGroupId`.

| Filter | Side | Clause, or why not |
| --- | --- | --- |
| Device name starts with | server | `startswith(computerDnsName,'...')` - the one function with a worked example on this collection |
| Onboarding status | server | `onboardingStatus eq '...'` |
| Platform (one value) | server | `osPlatform eq '...'` |
| Platform - Any Windows | **client** | No `osPlatform` value means "any Windows". `startswith` is documented for `computerDnsName` and no other property, and a guessed clause the service accepts while matching nothing is indistinguishable from an empty inventory. R1(d) would settle it; it has not run. |
| Health status | server | `healthStatus eq '...'` |
| Risk score | server | `riskScore eq '...'` |
| Exposure level | server | `exposureLevel eq '...'` |
| Last seen from / before | server | `lastSeen ge ...` / `lastSeen lt ...`, and the partition's root bounds |
| Machine tag | **client** | `machineTags` is in the filterable list, but a tag filter is a collection query (`machineTags/any(...)`) with no worked example here |
| Machine group | **client** | The filterable property is `rbacGroupId`, a numeric id. The name the operator knows, and the portal shows, is `rbacGroupName`, which is not filterable |
| First seen from / before | **client** | `firstSeen` is absent from the filterable list - the one timestamp on the machine resource that is not |

A client-side filter narrows what is SHOWN and never what is FETCHED. The ceiling refusal says so
in as many words, because sending an operator to narrow a filter that cannot reduce the fetch is a
loop with no exit.

The **Windows-devices-only checkbox is gone**, at the owner's instruction. Platform is now one
dropdown among the others and defaults to Any.

### UI

The filter card is a three-per-row Bootstrap grid of labelled controls with the two buttons (Clear,
Load devices) in the last cell, and one help line under the whole card rather than a grey paragraph
beside one control. Client-side filters are marked `(after fetch)` inline in their own labels, so
nothing shoves the layout. No new CSS.

The results header now reports the partition: `N device(s)`, then `M fetched in R request(s).
Ceiling C.` The request count is the visible evidence that the partition ran: above one means the
inventory had to be divided. The range count was in this header until the codex round of
2026-09-21, which read it as implementation detail on screen; it is still carried on the result
type and still asserted by the partition tests.

### On-screen wording

Owner ruling, 2026-09-21, applied to every user-facing string this module owns: **state what
happened and what to do, nothing else.** No design rationale, no self-reference ("this module must
never", "the run could not prove"), no restating the situation twice, no reassurance. The rationale
lives here and in the code comments, where the people who need it look. The distinctness
requirement on the failure reasons is unchanged and still enforced by
`EveryEnrichmentReasonIsADifferentSentence`; the reasons are simply shorter.

The page banner is one line. The refusal banner is a heading and the reason. The detail panel's
paragraph about Active Directory domains is gone - the row is labelled "DNS domain (derived)",
which was the whole of the point.

### Versioning

Module `1.0.0` -> `1.1.0` in `Modules/ModuleCatalog.cs`. **No base app bump**:
`ExchangeAdminWeb.csproj` is byte-identical, verified by `git diff` rather than assumed. Nothing
outside `Models/DefenderDeviceModels.cs`, `Services/DefenderEndpointDeviceService.cs`,
`Components/Pages/DefenderEndpointDevices.razor`, this module's catalog entry and its tests was
touched, apart from this plan and `docs/DefenderEndpointDevices.md`.

### What a real run costs now

A "Windows, can be onboarded" load on a tenant of 40,000+ devices, with the default 10,000-row cap.

The cost is driven by the RATIO of matching devices to the per-request cap, not by the device count,
because each part is divided until it fits under the cap. **Measured, not estimated**: the
43-device fixture against a 10-row cap - a ratio of 4.3, the same ratio 43,000 devices have against
the real 10,000-row cap - costs **12 requests**: one root, one for the no-`lastSeen` part, and ten
across the tree that divides it. A tenant at twice that ratio adds roughly one level, not twice the
requests.

Against the endpoint's documented 100 calls a minute, a run of that size is one sixth of one
minute's allowance, and the hard stop at 250 is eight times the worst division the fixture
produced.

The "Any Windows" choice does not reduce the fetch, so it costs the same as no platform filter;
choosing an exact platform (`Windows11`) does reduce it, and proportionally.

### Guard proof

| Probe | Mutation | Test that failed |
| --- | --- | --- |
| P1 | `lastSeen ge` -> `lastSeen gt` in the high child's clause | `MoreDevicesThanOneRequestCanReturn_AreAllListedByDividingOnLastSeen`, `TwoSiblingPartsMeetExactly_WithNoGapAndNoDeviceInBoth` |
| P2 | never queue the `lastSeen eq null` part | `TheDevicesWithNoLastSeen_AreFetchedByATheirOwnRequestOnceTheRootIsDivided` |
| P3 | queue the `lastSeen eq null` part even when the operator set a window | `AnOperatorSuppliedLastSeenWindow_IsTheRootRangeAndSuppressesTheNoLastSeenRequest` |
| P4 | drop the strictly-inside test in `ChooseSplit` | `ASplitPointIsAlwaysStrictlyInsideItsRange`, `ARangeWithNothingToDivideOn_CannotBeSplit` |
| P5 | push the high child last, reversing the visit order | `ThePartitionVisitsItsPartsInAscendingTimeOrder` |
| P6 | treat the no-`lastSeen` part's 400 as an empty part | `AServiceThatRejectsTheNoLastSeenFilter_RefusesAndNamesTheWindowToSet` |
| P7 | send `machineTags/any(t: t eq '...')` server-side | `TheClientSideFiltersNeverReachTheQuery` |
| P8 | a device with no `firstSeen` passes a `firstSeen` bound | `ADeviceWithNoFirstSeenFailsAFirstSeenBoundRatherThanPassingIt` |

Every restore was a file copy followed by `touch` and a SHA256 comparison against the pre-mutation
hash - no `git checkout --` at any point.

P3's mutation is "push the no-`lastSeen` part unconditionally beside the root", because simply
flipping `HasLastSeenBound` changes nothing: that flag is only read inside the split branch, and
the window fixture completes in one request without splitting.

### Verification

`dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental` 0 errors / 23 warnings;
`dotnet test ExchangeAdminWeb.slnx` 2946 passed / 0 failed / 3 skipped, +21 on the 2925 baseline
and the twenty-one new tests account for it exactly; `dotnet format ExchangeAdminWeb.slnx
--verify-no-changes --no-restore` exit 0; `git diff --check HEAD` exit 0;
`tools/Test-AsciiOnly.ps1` exit 0.

### Still outstanding

R1 (a), (b), (c), (d) and (e), and the manual acceptance checklist. Q2, Q3, Q4 and Q5 remain
unanswered. The design above has been proved against a stub that evaluates the filter it is given;
it has not been run against the live service since the change.

## Revision 4 - codex review of the rebuild, 2026-09-21

Harness: codex-cli 0.154.0, `codex exec --json -s read-only`, model
`@azure-openai-eus2-global/gpt-5.5-dzs` at `model_reasoning_effort=xhigh`. Prompt
`.agents/review/q8-scale.prompt.txt`, verdict `.agents/review/q8-scale.result.json`. Capability
proof passed. Reviewed the uncommitted working tree against HEAD `1ddafab`.

**Verdict: unsound - 1 MEDIUM, 4 LOW. No CRITICAL and no HIGH.**

What it cleared explicitly, which is worth as much as the findings and does not get re-litigated:
sibling parts emit `lastSeen lt` and `lastSeen ge`, so they are disjoint and meet exactly; a null
`lastSeen` is covered on the unsplit root and by the one explicit null part after a split, and an
operator-supplied window suppresses that part correctly; rows from an ambiguous parent are counted
against the ceiling but not promoted into the answer; only a `Complete` result renders or exports
rows. Termination is fail-closed rather than unbounded - `ChooseSplit` returns only timestamps
strictly inside the range, and a capped part whose rows share one timestamp or carry none refuses.
The server/client filter split is applied in the right order, and the ceiling refusal matches it.
It also confirmed that `MoreDevicesThanOneRequestCanReturn_AreAllListedByDividingOnLastSeen` does
fail a partition that misses a dated or an undated device, so the miss-a-device case is covered.

### F1 (MEDIUM) - user-facing text still editorialised in six places

The owner's ruling of 2026-09-21 is that every string states what happened and what to do, and
nothing else. The rebuild applied it to the refusals and the banner and missed these.

| Was | Now |
|---|---|
| `This module's app credentials could not be built...` | `App credentials could not be built...` |
| `...the sign-in for this module's app registration succeeds...` | `...the app registration can sign in...` |
| `Microsoft Graph rejected this module's credentials... the module's Secret Server record` | `Microsoft Graph rejected the credentials... the Secret Server record` |
| `The Defender for Endpoint API rejected this module's credentials... the module's Secret Server record` | `...rejected the credentials... the Secret Server record` |
| Two-line grey helper paragraph under the filter card | One line |
| `M fetched in R request(s) across P Last seen range(s). Ceiling C.` | `M fetched in R request(s). Ceiling C.` |

The range count is gone from the header: it is the partition's implementation detail, and an
operator does not act on it. A request count above one already says the inventory had to be
divided. `RangesCompleted` stays on the result type and stays asserted by the partition tests,
which is where it is load-bearing.

### F2 (LOW) - the enrichment warning fired for rows that do not exist

A complete run matching zero devices rendered "...read '(unavailable)' for every device here" above
an empty table. The warning is now guarded on `result.Devices.Count > 0`; a run that matched nothing
says so in the header and says nothing else.

**Honest limitation.** The guard that bites here is the CSV export-position test's textual anchor
(M4 below), not a behavioural one. There is no render harness for this page, so no test asserts
that the warning is absent at zero devices - only that the branch condition is the string the test
expects. Removing the Count clause fails the suite; rewriting it to something equally wrong that
kept the same text would not, and nothing on this page can currently close that.

### F3 (LOW) - the ceiling-refusal test asserted four of seven server-side names

`TheCeilingRefusalNamesTheFiltersThatCannotReduceTheFetch` checked onboarding status, platform,
health status and risk score, while the production string also names exposure level, device name
and the Last seen window. A regression dropping one of those three would send an operator away from
a filter that would in fact have reduced the fetch - the exact loop this refusal was rewritten to
break - and the test would not have noticed. All seven are now asserted.

### F4 (LOW) - a budget test whose floor and ceiling were both 250

`MaxRequestsPerRun >= 250` and `MaxRequestsPerRun <= 1500 / 6` are the same number, so the test
pinned the constant to itself while its name claimed headroom. Both bounds are now derived and
neither evaluates to 250: the floor is ten times the cost of a balanced division of a ceiling-sized
tenant (`2 * leaves - 1`, plus the no-`lastSeen` part - 200 at today's constants), and the ceiling
is a fifth of the endpoint's documented 1,500 calls an hour (300), so one run cannot eat the
tenant's hour and four more operators can still run in it.

### F5 (LOW) - the accumulation-order guard covered one client-side filter of four

`TheClientSideFiltersAreAppliedAfterThePartitionAndNeverDuringIt` exercised only the Any Windows
prefix. An implementation that got the prefix right and applied the machine tag, the machine group
or a First seen bound inside the page reader has exactly the same defect - a full response of
non-matching rows reads as short, which is a false proof of exhaustion - and the test cleared it.
It is now `EveryClientSideFilterIsAppliedAfterThePartitionAndNeverDuringIt`, a Theory over all four.
The `Page` test helper gained an `extra` parameter so a fixture row can carry `machineTags`,
`rbacGroupName` or `firstSeen`. Only the platform case sets a non-matching `osPlatform`; the other
three leave the rows as Windows, so the exclusion is provably the filter under test and not the
prefix.

### Guard proof - 4 probes, each attributable

| Probe | Mutation | Test that failed |
|---|---|---|
| M1 | filter the page through `MatchesClientSideFilters` before `rows.AddRange(page)` | `EveryClientSideFilterIsAppliedAfterThePartitionAndNeverDuringIt`, all 4 cases |
| M2 | drop exposure level, device name and the Last seen window from `CeilingRefusal` | `TheCeilingRefusalNamesTheFiltersThatCannotReduceTheFetch` |
| M3 | `MaxRequestsPerRun` 250 -> 100 | `TheRequestBudgetLeavesRoomForAPartitionedRunAndStaysUnderTheHourlyLimit` |
| M4 | drop `result.Devices.Count > 0 &&` from the enrichment warning | `DefenderEndpointDevices_OffersNoExportFromTheRefusalBranch` |

M1 is the finding-5 probe and it fails all four Theory cases, which is the point: each of the four
client-side filters is guarded on its own, not by proxy through the platform prefix. Every restore
was `Copy-Item` plus an explicit `LastWriteTime` touch - a preserved mtime makes MSBuild skip the
rebuild and the next run tests the mutated binary - followed by a SHA256 compare against the
pre-mutation hash, which the probe script throws on. Both files match.

One in-flight break to record rather than hide: the F2 guard changed the exact line
`DefenderEndpointDevicesCsvTests.DefenderEndpointDevices_OffersNoExportFromTheRefusalBranch` uses to
locate the start of the complete branch, so that test failed on the first run after the fix. The
anchor was repointed to the new condition, not weakened - it still brackets the export button
between the first element of the complete branch and the device table's closing tag.

### What was not re-dispatched

Per `.agents/decisions.md` 2026-08-31, reviewer verification rounds are CRITICAL-only and each needs
an explicit owner go. None of these five is CRITICAL, so all five close on the guard proof above
rather than on a second codex round.

### Verification after the five fixes

`dotnet build ExchangeAdminWeb.slnx -c Release` 0 errors / 23 warnings; `dotnet test
ExchangeAdminWeb.slnx` **2949 passed / 0 failed / 3 skipped**, +3 on the 2946 of Revision 3 and the
three are accounted for exactly by the one-Fact ordering test becoming a four-case Theory;
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0; `git diff --check
HEAD` exit 0; `tools/Test-AsciiOnly.ps1` exit 0.

### Still outstanding

Unchanged by this round. R1 (a), (b), (c), (d) and (e), and the manual acceptance checklist. Q2,
Q3, Q4 and Q5 remain unanswered. Nothing here has been run against the live service.

# Revision 5 - the owner revised queue item 8, 2026-09-24

Plan text only. No source file, test, script or descriptor was touched; `git diff --stat` over this
change names one path, `docs/DefenderEndpointDevices-Plan.md`.

**Numbering note, because this file has two revision series and a reader will trip on it.**
Revisions 1-8 record the first-series work (draft, three codex rounds, then S1, S2, S4, S3 and a
review of the finished module). A second series then restarted at "Revision 3 - the live run" and
"Revision 4 - codex review of the rebuild". This entry continues the second series as **5**. Where
the body above needs to disambiguate it says "first series" or "second series" explicitly.

## What triggered it

The owner revised queue item 8. `.agents/state.md` parked queue 8 on 2026-09-22 "waiting on an
updated requirements document from the stakeholder"; **that document has arrived**, and the revised
queue text is quoted verbatim at the top of this file, replacing the original. Whether the park
lifts is the owner's call and `.agents/state.md` owns it - this revision folds the new requirement
into the plan and does not resume the work.

Four things changed, and each one invalidated something written above it.

## 1. The purpose is now stated, and it re-decides the column set

"Purpose of this is to help locate machines physically in a global company." The module is still
list-and-export; what changed is that a column now has to earn its place by narrowing a location.
Recorded in a new section, "The purpose, and what it reframes".

**`DiscoverySources` is demoted, not deleted.** It names a product, never a machine and never a
place; it was a proxy for the real question and the research shows it is the wrong data. It stays
in the report because it is already shipped and costs nothing, and it stops being described as
answering the owner's need anywhere - field mapping, manual check 7 and the CSV section all say so
now.

## 2. The blocker is gone, so the registration section became a checklist

The registration exists, Revision 3 of the second series records both permissions granted and
consented, and the Delinea Secret ID is **657** - which goes in `GraphDelineaSecretId`
(`Modules/ModuleCatalog.cs:825`; descriptor at `:799`, `Version = "1.1.0"` at `:816`, `MaxDevices`
at `:827`, `IncludeDiscoverySources` at `:830`, all read from the file rather than remembered).

**The permission section was NOT deleted.** It was converted, because one permission changed status
and a registration consented under the old reading may not hold what the new one needs:
`ThreatHunting.Read.All` went from optional to load-bearing. The Privileged Role Administrator /
Global Administrator fact - an Application Administrator cannot consent a Microsoft Graph app role -
is unchanged, and is now the thing the checklist exists to make somebody check.

## 3. "Recently Seen By" is obtainable, and it makes hunting mandatory

The owner's screenshot **answers the question `.agents/state.md` records as open and unanswered**:
yes, the Defender portal shows an onboarded machine on a can-be-onboarded device's page, in a field
labelled "Recently seen by" with a "View all seen by" link.

The route is `SeenBy()` - **a documented advanced hunting function, not a column**, which is why
the earlier column enumeration was correct and still found nothing. Microsoft publishes the query
for exactly this scenario and states the purpose in the owner's own terms: the data "can help
determine the network location of each discovered device". A 1,000-device cap per invocation, a
documented disagreement about the returned column name, and the fact that the function returns an
**ID and not an FQDN** are all recorded in the new "Recently Seen By" section, with their URLs.

**One prior claim is falsified.** This file and `.agents/state.md` both treat
`DeviceInfo.HostDeviceId` as a dead end that disappointed - populated on 1 device of 113,102,
pointing at itself. Learn documents it as "Device ID of the device running Windows Subsystem for
Linux". It is a WSL host pointer; one row in a hundred thousand is exactly correct behaviour, not a
data gap, and it was never a candidate. The measurement was right and the conclusion drawn from it
was not.

**And one inference is scoped rather than repeated.** The 30-day hunting retention is real and is a
product limit, not a tenant setting - but it was measured across the whole 113,102-device inventory
and **does not transfer** to this module's target set, which is devices Defender is actively
discovering right now and which are therefore far more likely to be inside the window. The plan
now asks rather than asserts: **R1(i)** measures the coverage fraction.

**Consequence, and it is the important one:** hunting becomes load-bearing, the registration must
be verified to hold that consent, and the behaviour when hunting fails while the list succeeds is
**extended in T7 rather than duplicated** - the list still stands, the columns still read
`(unavailable)` with a named reason, and a failed enrichment still may not blank a column silently.
What T7 adds is that the banner must now say the report does not answer the request, that the
request-body guard must be widened exactly rather than relaxed, and that "once per refresh" may not
survive the 1,000-device cap (**Q9**).

## 4. A location-narrowing column set is proposed for approval

The owner asked for one. The new "Location-narrowing candidate fields" section presents L1-L16 as
a table with, per row, the exact source, the location strength, and **whether the field exists for
a non-onboarded device** - stated per row rather than in a footnote, because several of the
strongest signals are onboarded-only and that is exactly the trap. It names what is not documented
anywhere (no AD site field, no building, floor or geo-coordinate), recommends **against** the
`findbyip` REST route because its only Application permission is `Machine.ReadWrite.All` and T4
exists to keep a write scope off this registration, and treats "View in map" as a second recon
surface to enumerate (**R1(l)**) rather than as a design. The owner's own screenshot supplies the
sharpest point in the table: the example device is VMware-vendored, so adapter vendor is primarily
an **exclusion** signal - it is what keeps virtual machines out of a report about walking to a
machine.

## The `$skip` tension - flagged, not resolved

Learn documents `$skip` as supported on `GET /api/machines`, beside `$top` max 10,000 and "Maximum
page size is 10,000". Revision 3 of the second series recorded a live run returning 10,000 rows
with **no** `@odata.nextLink`. **Both are true**: supporting a client-computed offset and emitting
no server-side cursor are different mechanisms.

**The rejected-alternative section was NOT reversed.** It is a recorded decision with an argument,
and the argument it actually rests on - that `$skip` needs a stable order and this collection
documents no `$orderby` - is untouched by the docs listing `$skip`. A note now sits inside that
section where a reader meets the tension, and **R1(j)** probes `?$top=10000&$skip=10000` live. The
rejection stands until the probe and a cited stability source say otherwise, and the partition
design does not depend on the answer either way.

## Also corrected while in the file

- **Q1 and Q6 were still listed as open** in "Open questions for the owner" although Revision 5 of
  the first series closed both on 2026-09-21. Both are now marked closed with their answers. Stale
  reference, Known Failure Class 4.
- **Revision 6 of the first series left an instruction dangling** - the `DiscoverySources`
  string-or-array serialisation question, to be added "to R1's list if that gate is still open". It
  is still open, so it is now **R1(m)**.
- **Manual checklist steps 3 and 4 are stale** - they exercise a "Windows only" toggle Revision 3
  of the second series removed. Flagged in place rather than rewritten; see the note under that
  heading for why.
- **S5's `1.0.0` line is history.** The module is at `1.1.0`. A new "S6 - NOT YET SLICED" section
  says so and says why no slice is written yet.

## Versioning

**This revision bumps nothing** - it is plan text. The proposal for S6, computed from the field and
the Constitution rule rather than from memory, is module `1.1.0` -> **`1.2.0`** (minor: new
behaviour on a shipped module) with **no base app version bump** (nothing S6 needs is shared). The
condition that would flip the base call is named in the Versioning section. The implementing slice
recomputes from `Modules/ModuleCatalog.cs` rather than trusting this line.

## Verification

Docs-only, so the repo's docs rule applies: `git diff --check`. Build, test and format were not run
and are not claimed - no compiled file, test or script changed. No live query was run against the
tenant by this revision; every API fact added here came from Microsoft Learn on 2026-09-24 and the
URLs are in Sources. Every `file:line` cited was read from the file at the time of writing.

## Still outstanding

R1 (a), (b), (c), (d), (e) and the new (h), (i), (j), (k), (l), (m). The manual acceptance
checklist. Q2, Q3, Q4, Q5, Q8 and Q9. Q7 and Q10 are RULED (2026-09-24/25) and R1(o) is measured. **The pending step is the owner's: approve or
strike the L1-L16 candidate table (Q8) and answer Q9.** The proposed next action after that is to
draft S6 against the answers. Queue 8's park, and the unresolved fork about the page being unusable
at tenant scale, are `.agents/state.md`'s and are not re-asked here.
