# Conference Rooms — Room Finder Metadata Apply Failure Plan

**Status:** Implemented in dev (2026-06-17) — pending live verification against the tenant.
**Date:** 2026-06-17
**Module:** ConferenceRooms (`Modules/ModuleCatalog.cs` module `Version = "2.0.7"`)
**Reporter:** Shaun Hogan
**Evidence:** `D:\BugReports\roomfinder`; live reproduction + room-data confirmation 2026-06-17

## Implementation note (2026-06-17)

Implemented per the corrected root cause below. Key decisions made during implementation,
confirmed against live tenant data (`rooms_with_c_co_countrycode.csv`):
- Resolve the AD object by **userPrincipalName** (owner: email always == UPN here,
  forest-unique), assert exactly one match, write by **objectGUID**.
- Country written as the full **c / co / countryCode** triple to match existing rooms.
  `c` = uppercased alpha-2; `co` = `RegionInfo.EnglishName` (the Windows SHORT name —
  existing US rooms carry "United States", not the ISO long name); `countryCode` = ISO
  3166-1 numeric from a new hardcoded `Services/IsoCountryCodes.cs` table (249 entries),
  because .NET cannot supply it (`RegionInfo.GeoId` is a Microsoft GeoId, not ISO numeric).
  An unmappable country fails the row closed; an integrity test pins the table.
- New files: `Services/IsoCountryCodes.cs`, `ExchangeAdminWeb.Tests/IsoCountryCodesTests.cs`,
  `ExchangeAdminWeb.Tests/ConferenceRoomSyncedAttributeTests.cs`.
- ConferenceRooms module `2.0.6 → 2.0.7`; new `DelineaSecretId` ConfigField (AD cred from PAM).
- Build + 467 tests green; new guard tests proven non-vacuous via revert (the table guard
  was caught vacuous on first draft — RegionInfo rejected the test inputs before the table
  did — and fixed by using `XK`, which RegionInfo accepts but the ISO table rejects).
- **Still required:** live verification against the tenant (see Verification §), and the AD
  `DelineaSecretId` must be configured in the deployed instance before apply will work.

## Context

Shaun reported that the Room Finder upload preview looks correct, but Apply fails. The
bug report contains `report.png`, `ADLK_Buildings1.jpeg`, `ADLK_Buildings2.jpeg`, and
`ADLKRF_Buildings.csv`.

The preview screenshot shows the building-list fix is already active: rooms target
building-named lists such as `Catalyst Conference Rooms`, `ERDC Conference Rooms`, and
`B1 Conference Rooms`, with a warning when a city-named list also exists. This is not a
repeat of the previous City-vs-Building grouping bug documented in
`docs/ConferenceRooms-BuildingRoomList-Plan.md`.

The apply screenshot shows `0 / 36 succeeded`; every visible row fails at the
`Metadata` step after `Set-Place`, before any room-list membership step can run. The CSV
has 86 rows. Its relevant metadata shape is:

- `CountryOrRegion=IE`, `State=Limerick`, `City=Limerick`.
- `Building` values include `Catalyst`, `ERDC`, `B1`, and related building names.
- `Floor` is always numeric-looking (`0`, `1`, or `2`).
- `DisplayDeviceName` is blank for all rows.
- `VideoDeviceName` is blank for 39 rows, and otherwise includes values such as `MTR`,
  `BYOD`, `Jabra`, and `Pexip`.
- The file has no `FloorLabel` column; the parser treats that as blank.

## Root Cause — CONFIRMED FROM LOGS + LIVE REPRODUCTION

**The draft's original root cause (a rejected `Set-Place` parameter, most likely the
`VideoDeviceName` device labels) was WRONG and is superseded by this section.** The
operation trace and a live tenant reproduction prove the failure is in `Set-User`
(Step 2), not `Set-Place` (Step 1).

### Evidence

`Components/Pages/ConferenceRooms.razor:750-753` passes each parsed Room Finder row to
`ConferenceRoomService.SetRoomMetadataAndListAsync(...)`, which runs four EXO commands in
sequence on one pipeline:

1. `Set-Place` (City, Building, Capacity, Floor, optional device/floor-label) — line 285
2. `Set-User` (City, CountryOrRegion → StateOrProvince) — line 303
3. `Set-MailboxRegionalConfiguration` / `Set-MailboxCalendarConfiguration` (timezone) — 318/324
4. room-list membership — line 346

If any step throws, `ConferenceRoomService.cs:334-339` collapses the failure to a single
generic `Metadata` step and returns before room-list membership.

Shaun's run (operation trace, `E:\WWWOutput\ExchangeAdminWeb\exchangeadmin_20260617_trace.jsonl`)
shows the SAME pattern on every one of the 86 rows:

- `SetPlace` step logged → `SetUser` step logged → `Complete: Failed`.
- **No `SetTimezone` step ever appears.**

`OperationTraceService.Step()` emits its line *before* the command runs (default
`result:"Success"` is "about to run", not "succeeded"). So the last step logged marks how
far execution got: reaching `Step("SetUser")` means **Set-Place succeeded**; never reaching
`Step("SetTimezone")` means **Set-User threw**. The failure is unambiguously at Step 2,
`Set-User` — uniform across all rows, independent of `VideoDeviceName` (blank-device rows
fail identically). This refutes the device-parameter hypothesis.

### Why Set-User fails (live reproduction, 2026-06-17)

`City`, `StateOrProvince`, and `CountryOrRegion` are **on-prem-AD-mastered** attributes on
these room objects. The cloud copy is dir-synced and read-only in EXO:

```
Get-CUser CatalystIP1.Conf@analog.com | fl ...,IsDirSynced   →  IsDirSynced : True
Set-CUser CatalystIP1.Conf@analog.com -City Limerick -EA Stop →  FAILS:
  "The action 'Set-User', 'City', can't be performed on the object ... because the object
   is being synchronized from your on-premises organization. This action should be
   performed on the object in your on-premises organization."
```

(The app talks to EXO, so its `Set-User` is the prefixed `Set-CUser` shown above. The
generic "A server side error has occurred" the user sees is EXO's surfaced form of this
out-of-write-scope error; the app's `Metadata` collapse hides the specific text.)

### Why the legacy `SetupRoomFinder.ps1` worked for years

The reference script (`D:\source\ConfRoomScript\SetupRoomFinder.ps1:388-389`) is asymmetric:
it sets the synced user attributes with **unprefixed `Set-User` against ON-PREM Exchange**,
but Place metadata with `Set-CPlace` against EXO. On-prem accepts the synced-attribute
write; EXO accepts the Place write. The app routes *everything* through its single EXO
connection, so its Step-2 `Set-User` hits the synced object in the cloud and is rejected.
It is NOT that the app combined parameters — the reference script also sends device fields
in one combined `Set-CPlace` call and works fine.

### Forward constraint (owner direction 2026-06-17)

This is true only while hybrid. After Exchange Online... (correction: after the on-prem
**Exchange** decommission next year, per AC14), the room objects remain dir-synced from
on-prem **AD**, but there is no on-prem Exchange to run `Set-User` against. So the synced
attributes must be written directly on the AD object with `Set-ADUser`. Writing via
`Set-ADUser` is correct **both now and after** the Exchange decommission — it targets the
AD master in both worlds — which is why it is the chosen fix rather than routing to on-prem
Exchange `Set-User`.

### Not an AC14 reversal

AC14 retired the on-prem **Exchange** path (`Set-RemoteMailbox` over on-prem Exchange
remote PowerShell; see `ConferenceRoomService.cs:1065-1083`). `Set-ADUser` targets on-prem
**Active Directory**, a separate capability the app already uses live in Comms10k,
ADAttributeEditor, and GroupManagement via the `ModuleCredentialService` Delinea pattern.
Using it here is consistent with AC14, not a reversal of it.

## Scope

Fix the synced-attribute write in the Room Finder metadata apply path. Specifically:
move the `City` / `StateOrProvince` / `CountryOrRegion` write off EXO `Set-User` and onto
on-prem `Set-ADUser`. Leave `Set-Place` (Building/Capacity/Floor/devices — not synced, EXO
accepts them), the timezone steps, and room-list membership on EXO as they are. Do not
change building-list naming, room-list creation, type-template behavior, ServiceNow
ticketing, or audit policy beyond surfacing the new step's result.

## Proposed Fix

1. **Add an AD credential to the ConferenceRooms module config (PAM).**
   - The `Set-ADUser` call needs an on-prem AD credential. ConferenceRooms has no
     `DelineaSecretId` ConfigField today, so add one to its `ModuleCatalog` descriptor and
     resolve it with `ModuleCredentialService.GetCredentialsAsync("ConferenceRooms", ...)`,
     exactly as Comms10k / ADAttributeEditor do. The credential comes from the deployment's
     PAM (Delinea today), per the 2026-06-17 PAM decision — never plaintext config.
   - Fail closed: if the secret is unconfigured or the module config is corrupt, the synced
     attribute step fails with a clear message rather than silently skipping the write.

2. **Replace EXO `Set-User` (Step 2) with on-prem `Set-ADUser`.**
   - Resolve the AD object by **`userPrincipalName -eq <room email>`** — in this
     environment email always equals UPN (owner direction 2026-06-17), and UPN is
     forest-unique. Assert the lookup returns **exactly one** object; 0 (not found) or >1
     (ambiguous) fails the row closed, so a wrong object can never be written.
   - **Write by the returned `objectGUID`, not by UPN**, so the mutation targets an
     immutable identity even though resolution used UPN.
   - Set the synced attributes directly:
     - `City` → AD `l`
     - `StateOrProvince` → AD `st`
     - `CountryOrRegion` → the **three-attribute country set** EXO's `Set-User
       -CountryOrRegion` writes as a unit: `c` (ISO-3166 alpha-2, e.g. `IE`), `co`
       (English name, e.g. `Ireland`), `countryCode` (ISO-3166 numeric, e.g. `372`).
       Derive all three from the CSV's country value with `System.Globalization.RegionInfo`
       (already imported); an unmappable country fails the row with a precise message rather
       than writing a partial/inconsistent country.
   - Skip cleanly: if all three of City/State/Country are blank for a row, skip the
     `Set-ADUser` step (nothing to write) rather than calling it with no attributes.
   - Run `Set-ADUser` in its own runspace with the AD credential, mirroring
     `Comms10kService` (Import ActiveDirectory module, pass `-Credential`,
     `-ErrorAction Stop`). It does NOT go through the EXO connection pool.

3. **Sequence and trace as a named step.**
   - Order: `Set-Place` (EXO) → `Set-ADUser` (on-prem, synced attrs) → timezone (EXO) →
     room-list membership (EXO). Emit a distinct `SetADUser` operation-trace step so a
     future failure is attributable, not collapsed into the generic `Metadata` bucket.
   - Keep room-list membership gated behind successful required metadata (today's
     all-or-nothing behavior is unchanged unless the owner approves partial apply).

4. **Set expectations on propagation (UX).**
   - A successful `Set-ADUser` writes on-prem; the EXO-visible City/State/Country update
     only after the next Entra Connect sync cycle (~30 min). The success message must say
     the change is written and will appear in Room Finder after directory sync, not that it
     is live immediately.

5. **Forward compatibility (no code now).**
   - This same `Set-ADUser`-by-objectGUID path remains correct after the on-prem Exchange
     decommission (objects stay AD-synced). No second implementation is needed later;
     recorded so the next maintainer does not "re-cloud" this write.

## Files To Modify

- `Modules/ModuleCatalog.cs`
  - Add a `DelineaSecretId` ConfigField to the ConferenceRooms descriptor (AD credential
    from PAM).
  - Bump ConferenceRooms module version `2.0.6` → next patch.
- `Services/ConferenceRoomService.cs`
  - Replace the Step-2 EXO `Set-User` block in `SetRoomMetadataAndListAsync(...)` with an
    on-prem `Set-ADUser` step: resolve by UPN (assert exactly one), write by objectGUID,
    set `l`/`st` and the `c`/`co`/`countryCode` country triple.
  - Extract the country mapping (`CountryOrRegion` → `c`/`co`/`countryCode`) and the
    AD-attribute build into pure, testable helpers.
  - Resolve the AD credential via `ModuleCredentialService`; fail closed when missing.
  - Add a `SetADUser` operation-trace step.
- `Components/Pages/ConferenceRooms.razor`
  - Update the success/result messaging to reflect directory-sync propagation latency and
    render the precise per-row failure message from the new step.
- `Models/ConferenceRoomModels.cs`
  - Add structured fields only if needed for the new step's result/diagnostics.
- `ExchangeAdminWeb.Tests/`
  - Unit tests for the country mapping and AD-attribute build helpers (pure, no live AD).
- `appsettings.json.sample` / module-config docs
  - Note the new ConferenceRooms `DelineaSecretId` (AD credential from PAM).

## Tests

Automated (pure helpers — no live AD/EXO):

- Country mapping: `IE` → `c=IE`, `co=Ireland`, `countryCode=372`; a representative set of
  the CSV's countries; an unmappable value yields a deterministic failure, not a partial
  write.
- AD-attribute build: City/State present → `l`/`st` set; all-blank location → step skipped
  (no `Set-ADUser` with empty attributes).
- Resolution guard (unit-level around the helper that enforces it): a lookup returning 0 or
  2 objects fails closed; exactly 1 proceeds and writes by that object's GUID.

The live `Set-ADUser`/runspace call itself has no injection seam (same constraint as
Comms10k), so it is covered by manual verification, not an automated test. Prove each new
helper test non-vacuous by reverting the guarded logic, confirming failure, restoring.

## Verification

Before claiming implementation complete:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore`
- `git diff --check HEAD`

Manual/live verification (depends on AD + dir-sync; cannot be automated here):

- With the ConferenceRooms AD `DelineaSecretId` configured, re-run Room Finder Apply
  against the ADLK data (or an approved subset).
- Confirm rows no longer fail as blanket `Metadata -- A server side error...`.
- Confirm `Set-ADUser` wrote `l`/`st`/`c`/`co`/`countryCode` on the on-prem object
  (`Get-ADUser <room> -Properties l,st,c,co,countryCode`).
- Confirm the EXO-visible values update after the next Entra Connect sync, and that rooms
  still join the building-named room list.

## Open Questions

- Country source: the CSV `CountryOrRegion` values are a mix of ISO-2 (`IE`) and full names.
  `RegionInfo` accepts ISO-2 cleanly; confirm whether any rows use a name/format that needs
  normalization before mapping.
- Is `DisplayDeviceName` / `VideoDeviceName` on `Set-Place` confirmed working in this tenant
  (the trace shows `Set-Place` succeeded with them present)? Assumed yes from the logs; flag
  if a later row proves otherwise.
- Module version: confirm `2.0.6 → 2.0.7` is the intended bump (module-scoped behavior
  change).
