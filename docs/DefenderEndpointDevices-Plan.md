# Defender for Endpoint Devices Module - Plan

Status: In progress (2026-09-24). S1-S5 landed and the module ships at `1.1.0`; the owner then ran it against the live service, which answered R1(g) (no continuation cursor) and R1(f) (the tenant exceeds the old 20000 ceiling) and forced the rebuild recorded in Revision 3 of the second series. **The owner has now REVISED queue item 8 - the quote below is the new text, and Revision 5 folds it in.** The app-registration blocker earlier revisions carried is gone: the registration exists, Revision 3 records both permissions as granted and consented, and the Delinea Secret ID is **657**. Owner rulings of 2026-09-24/25 closed Q7 and Q10, and R1(o) is measured (the AD subnet-to-site map is real: 531 subnets, 515 mapped, 167 sites, `location` empty everywhere). **All owner questions are now ruled or closed** (Q1-Q8 and Q10; Q8 approved the column set on 2026-09-25). Still open and none of it needs the owner: Q9 (how to batch the 1,000-device `SeenBy()` cap - a design call), R1 (a)-(e) and (h)-(o), the manual acceptance checklist, and the fact that NO SLICE IS WRITTEN for the revised requirement. Queue 8's park and the two defects open against the shipped module are recorded in `.agents/state.md`, which owns them - not here. Deliberately ONE line: wrapping it shifts every line below and invalidates the line citations in the revisions.

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

### T3 - there is no quiet first-N

**Heading corrected 2026-09-25.** It used to read "the report is complete or it refuses", and the
refusal half was overturned by the owner the same day - see "The ceiling is a runaway guard" below,
which is the current rule. **What survives is the half this trap was always about:** a result that
has been cut short must never be indistinguishable from a complete one. Refusing was one way to
guarantee that; stamping the partial set is another, and it keeps the rows.

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

### The ceiling is a runaway guard, not a limit the report is expected to meet - owner ruling 2026-09-25

**This overturns T3 above, whose heading is now wrong on purpose until a slice rewrites it.** T3
said the report is complete or it refuses. It refuses no longer.

**The ruling came out of the owner asking a question neither the plan nor its reviewer had asked:
when does the ceiling trip, before the query or after?** Owner, verbatim: *"if it's before, then
refuse to waste the time. if it's after, DO NOT waste what's already been collected."*

**It is always after, and that settles it.** This API exposes no count: `$count` is not supported,
so there is no way to learn how many devices match without fetching them. A ceiling can therefore
only ever trip on rows already retrieved and already paid for. Discarding them buys nothing - the
requests have been spent either way - and hands the operator nothing in exchange for the wait.

Three consequences, and the first is the one that makes the other two small.

#### 1. In practice this should never fire

The fetch divides the question rather than paging the answer: partition on `lastSeen`, and any
partition returning at the cap is split and re-asked (see T3's completion rule). The measured cost
is driven by the ratio of matching devices to the per-request cap, not by tenant size - a 4.3x
ratio measured at **12 requests against a 250-request budget**. This module's target set is
Windows devices in "can be onboarded", which `.agents/state.md` records as roughly 12,900 of this
tenant's 113,102. That is about a dozen requests.

So the ceiling is a guard against a pathological case - a filter that matches nearly everything, an
API that starts returning tiny pages - **not a limit the report is expected to meet.** Design it as
the rare branch it is, and do not let it shape the common path.

#### 2. If it does fire: keep what was collected, and offer to continue

- **Render everything fetched.** No refusal, no discarding.
- **Say how far it got** and that more match: what happened, in the 2026-09-21 user-facing-strings
  shape.
- **Offer a "Keep going" control that resumes the partition loop from where it stopped.** This is
  the part worth stating plainly, because it sounds harder than it is: *there is no second query to
  compose.* "The remaining results" is not a different question - it is the same recursion,
  continued. The stopping state is a set of unsplit partitions; resuming means popping them and
  carrying on. If the design cannot resume cheaply, that is a reason to revisit the design, not to
  hide the button.
- **A partial set is a subset, and not an interesting one.** Which devices are in it falls out of
  the partition order, not out of risk, recency or anything an operator would choose. The notice
  must not imply otherwise, and nothing may sort a partial set in a way that suggests a ranking.

#### 3. The export says it was partial. The audit record says so too.

The export carries the same fact the screen does, via the filename - exports already land as
`<module>_yyyyMMdd_HHmmss.csv`, so a partial run marks itself there. No new format, no preamble
row (the shared `CsvExport.Write` puts column names on line 1 and a test pins it), and **no
per-row column**: an identical value repeated on every row is metadata about the file wearing the
costume of data about a device.

The export's audit record already logs a row count. It must also log whether the run was complete,
because a count cannot be interpreted later without it.

#### Why a ceiling exists at all

Reasons about the API, not about any deployment: the endpoint allows 100 calls per minute, and an
unbounded loop against a paged API is how a read module becomes an outage. The request count
follows from the page size rather than from a number written here: at
`$top = min(MaxDevices + 1, 10000)` the default of 20000 costs **one** request when the endpoint
emits no cursor, and at most `ceil(MaxDevices / pageSize)` when it does, where `pageSize` is
whatever the service actually returns - which R1(g) observes rather than this file asserting.

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

1. With `GraphDelineaSecretId` unset, the module reports unavailable in words - not an empty
   table.
2. Set the Secret ID. The device list loads and the module version renders beside the heading.
3. The default view is Windows devices with onboarding status "can be onboarded". The count is
   plausible against the Defender portal's own **Onboarding status: Can be onboarded** filter -
   and a difference against the *onboarding recommendation widget* is expected, not a defect.
4. Set the Platform dropdown to Any: non-Windows discovered devices appear. (Corrected in the
   2026-09-25 audit - this step used to name a "Windows only" toggle that Revision 3 of the second
   series removed, replacing it with the dropdown.)
5. Switch the onboarding-status filter to onboarded: onboarded machines appear, with healthy
   sensor states.
6. Spot-check one device against its portal page: FQDN, OS, last IP, MAC, first and last seen.
7. **Discovery sources, and what a blank actually means. Revised in the 2026-09-25 audit.**
   Discovery sources are populated for at least one device and the values look like the portal's
   ("MDE", and whatever else this tenant has). **Expect blanks - but do not accept them as normal
   without checking which set you are looking at.** The 30-day hunting window explains blanks
   across the whole 113,102-device inventory, most of which was last seen months ago. It does
   **not** explain blanks on Windows devices currently in "can be onboarded", which are the ones
   Defender is actively discovering and therefore the ones most likely to be inside the window.
   Widespread blanks on THAT set mean the hunting call is not doing what it should. R1(i) is the
   query that measures it; this step is the eyeball version.
   **A column of blanks and a column of `(unavailable)` mean opposite things and the operator must
   be able to tell them apart on screen.** That distinction is the check. And do not read populated
   discovery sources as evidence that the report can locate anything - see "The purpose, and what
   it reframes".
7a. **Recently Seen By (requirement 3).** Pick a can-be-onboarded device that the Defender portal
   shows a "Recently seen by" value for, and confirm this module shows the same observing machine.
   The portal renders an FQDN; `SeenBy()` returns a device ID, so a column of GUIDs means the join
   back to `DeviceInfo` for the name is missing, not that the data is absent.
7b. **AD site (L17).** For a device with an IP, confirm the site column carries a site name the
   organisation recognises. Then find a device whose IP falls outside every AD subnet and confirm
   the cell is **blank, not a nearest guess**. Both halves matter: the second is the one that sends
   somebody to the wrong building if it is wrong.
7c. **AD unreachable fails the report (Q10).** Contrive a directory read failure and confirm the
   whole report refuses in words rather than rendering with an empty site column. This is the
   deliberate opposite of step 11's behaviour for hunting, and the asymmetry is the point.
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
14. **The ceiling. Rewritten 2026-09-25 - it used to check for a refusal, which the owner
    overturned.** Set `MaxDevices` to something smaller than the number of devices matching the
    default filters; a handful will do. Then confirm all four:
    - the table renders the devices that WERE collected - nothing is discarded;
    - a notice says how far it got and that more match;
    - **Keep going** resumes and the report completes;
    - export while partial: the downloaded filename marks it, and the audit record says the run
      was not complete. Restore the value and confirm a complete run marks neither.
15. Compare the row count against the same filters in the Defender portal's device inventory. They
    should agree, modulo the update-frequency caveat Learn states. A count that stops suspiciously
    near a round number is the signature of paging that stopped early.

## Versioning

**The bullets immediately below are the ORIGINAL first-ship reasoning and are history: the module
shipped and is at `1.1.0` (`Modules/ModuleCatalog.cs:816`), not `1.0.0`. The current position is
the dated subsection at the end of this section.** They are kept because the argument in them - why
this module does not bump the base app version, and the single condition that would flip that - is
still binding on every future slice.

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
2. **RULED 2026-09-25: ONE app registration.** Owner: *"one"*. One registration and one Delinea
   secret (**657**) carry both `Machine.Read.All` and `ThreatHunting.Read.All`. The alternative was
   a second registration isolating the broad hunting grant, at the cost of a second secret and a
   second config field; it is not taken. The least-privilege reasoning in section 7 stands
   unchanged - the hunting grant is tenant-wide and that is the accepted price of requirement 3.
3. **RULED 2026-09-25: NO ticket required for CSV export.** Owner: *"no"*. The export carries IP
   and MAC addresses for unmanaged machines, and `BitLockerRecovery` requires a ticket to search -
   but the owner's call puts this module with the other export modules, which do not. The export
   is still audited with its row count and, after Q5, with whether the run was complete.
4. **RULED 2026-09-25: NOT a security-response surface. Audit only, no administrator alert per
   read.** Owner: *"no"*, confirming the plan's proposal. Reads are audited and never alert-emailed
   - the same shape the Risky Users module ships (`.agents/decisions.md` 2026-08-31). Write actions
   do not arise here; the module is read-only by design (see Out of scope).
5. **RULED 2026-09-25, then refined by the owner's follow-up question: a PARTIAL report, kept and
   stamped - and the ceiling should almost never fire.** Owner: *"partial marked as partial"*, then
   *"if it's before, then refuse to waste the time. if it's after, DO NOT waste what's already been
   collected."* It is always after - this API has no count endpoint - so nothing is discarded. The
   full design, including the **Keep going** control and why the CSV carries the stamp rather than
   the screen, is under "The ceiling is a runaway guard" in Design constraints and traps. T3's
   heading was corrected to match.
6. **CLOSED, 2026-09-21 (Revision 5, first series).** *Module display name: "Defender for Endpoint
   Devices"?* Shipped verbatim, with route `defender-endpoint-devices` and section-access alias
   `DefenderEndpointDevices`.

### Added 2026-09-24 by the revised requirement - these are the ones waiting

**Q1 through Q7 and Q10 are now all ruled or closed.** The two below are what remains, and Q8 is
the approval the owner explicitly asked to be given.

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
8. **APPROVED BY THE OWNER, 2026-09-25.** *Which of L1-L17 goes in the report?* The owner approved
   the recommended set below as written. **This is the column contract for the slice; a field not
   listed here is not built, and adding one goes back through this question.** Two entries remain
   conditional on a recon query, and that conditionality is part of what was approved - neither is
   built if its query says the data is not there.
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


## Revision history

**Rotated out of this file on 2026-09-25 to `docs/history/defender-plan-revisions.md`**,
which holds all of it verbatim: the four codex review rounds, the live run that falsified
the first design, the rebuild it forced, and the 2026-09-24 revision that folded in the
owner's revised queue item 8.

It was 1,036 lines of a 2,911-line plan. Before moving it, every measured fact it
established was confirmed to be stated independently in the body above - the 10,000-row
cap, the absent continuation cursor, the 113,102-device tenant size and the partitioned
fetch. **Nothing in the archive is needed to build this module**; it is there to explain
why the plan says what it says.

| Revision | What it settled |
| --- | --- |
| 1-2 (first series) | Two codex rounds on the draft. Finding 1 (HIGH): a hunting-side exception took a COMPLETED device list down, which is why T7 exists. |
| 3-8 (first series) | Slices S1, S2, S4, S3, then a review of the finished module. |
| 3 (second series) | **The live run.** The first design could not work here: `GET /api/machines` returned exactly 10,000 rows with no `@odata.nextLink`, and the tenant holds more. Forced the partitioned fetch. |
| 4 (second series) | Codex review of the rebuild. Cleared the completeness argument, termination and filter ordering. |
| 5 (second series) | The owner's revised queue item 8: the stated purpose, "Recently Seen By", the created app registration. |

Later rulings are recorded where they apply rather than as revisions: Q7 and Q10 in
`## Open questions for the owner`, and R1(o)'s measurement in `## Slices` under R1.
