# Conference Rooms — Room List Grouping by Building Plan

**Status:** Implemented (module 2.0.2; build 0 errors, 350/350 tests pass; guard verified non-vacuous). Manual EXO check not run — no live tenant.
**Date:** 2026-06-11
**Author:** Michael Coelho (via agent investigation)
**Module:** ConferenceRooms (`Modules/ModuleCatalog.cs` — current module `Version = "2.0.1"`)
**Reporter:** Shaun Hogan
**Decisions confirmed with stakeholder (Shaun):** Naming **Option A** + Existing-data **Option ii**.

## Context

Reported by Shaun Hogan against the Conference Rooms module Room Finder
(`Components/Pages/ConferenceRooms.razor`), with supporting files in
`D:\BugReports\calendar issue` (`RTPRF.csv`, `RTPRF2.csv`, screenshots).

In Microsoft Room Finder the **room list is the building**, and the **city shown above
it is derived from each room's Place metadata** (the `City` field on the room). Existing
tenant room lists are **building-named** (e.g. `RTP Conference Rooms`), and Room Finder
nests them under their city (e.g. *Durham*) — see `DurhamRF.jpeg`.

The web module names and matches the room list **from the City field only**; the Building
field is written as room metadata but never used for grouping. So Shaun's correct file
(`RTPRF2.csv`: `City=Durham`, `Building=RTP`) makes the app compute
`"Durham Conference Rooms"` and offer to create it **new** (the `NEW` badges in
`DurhamRF2.jpeg`) instead of targeting the existing **RTP** list.

This is a pre-existing design issue, **not** a regression from commit `7e7c9ac` (that fix
addressed the `@`-gate that silently dropped CSV rows). The same City-vs-Building grouping
problem was previously documented for the standalone script in
`D:\source\ConfRoomScript\Analysis_Report_Room_Finder_Issues.md` (Nov 2025); it carried
into the web port.

## Root cause (verified in code)

| Location | Current behavior |
|---|---|
| `ConferenceRoomService.cs:119` `ResolveRoomListAsync(string city)` | Canonical name `"{city} Conference Rooms"`, legacy `"RoomList-{city}"` — both keyed on **City**. |
| `ConferenceRoomService.cs:253` (inside `SetRoomMetadataAndListAsync`) | Calls `AddToRoomListAsync(roomEmail, city)` — passes **city**, never building. |
| `ConferenceRoomService.cs:193-205` | `Set-Place` writes `Building` as metadata on the room only — correct, unchanged by this fix. |
| `ConferenceRooms.razor:683` | Preview resolves the list via `ResolveRoomListAsync(row.City)`. |

**The `RTPRF_Failed.jpeg` batch** is Shaun's *workaround* (setting `City=RTP` to force the
right list name); it failed with a generic server-side error. The proper fix removes the
need for that workaround. Diagnosing that specific server error is **out of scope** (it was
a side effect of mislabeling City); if it recurs after this fix we treat it as a new report.

## Confirmed decisions

1. **Naming — Option A:** room list = `"{Building} Conference Rooms"`. Matches existing
   tenant lists so uploads slot into what already exists. City continues to be written as
   Place/User metadata so Room Finder keeps nesting the building under the city.
2. **Existing data — Option ii:** fix forward **and** surface a preview warning when the
   computed building list does not match existing lists (specifically: when a stray
   **city-named** list also exists). **No automatic creation-avoidance or deletion** — the
   operator reviews and decides. Cleanup of stray city-named lists is manual in EXO.

## Scope of changes

### 1. Service: group room lists by Building (`Services/ConferenceRoomService.cs`)

- **Extract a pure, testable naming helper** (mirrors how the CSV parsers were extracted
  for testability):
  - `public static string BuildRoomListName(string building)` → `"{building} Conference Rooms"`.
  - `public static string BuildLegacyRoomListName(string key)` → `"RoomList-{key}"`
    (preserves the existing legacy lookup shape).
- **Re-key `ResolveRoomListAsync`** to resolve by **building**:
  - Signature becomes `ResolveRoomListAsync(string building)`.
  - Canonical lookup uses `BuildRoomListName(building)`.
  - **Mismatch detection (Option ii):** also probe for a stray **city-named** list. To
    avoid changing the public tuple in a lossy way, extend the return to include whether a
    different city-named list exists and its name. Proposed return shape:
    `(string? roomListName, bool exists, bool isLegacy, string? strayCityListName)`.
    The city value needed for the stray probe is passed in alongside building (see below).
  - Concretely: `ResolveRoomListAsync(string building, string city)` — building drives the
    target; city is used only to detect a stray `"{city} Conference Rooms"` list that
    differs from the target, for the warning.
- **`AddToRoomListAsync`** takes `building` (was `city`) and uses `BuildRoomListName`.
- **`SetRoomMetadataAndListAsync`**: the room-list step calls `AddToRoomListAsync(roomEmail, building)`
  (line ~253). `Set-Place`/`Set-User` continue to receive `city` as metadata — unchanged.
  The guard at line 251 changes from `if (!string.IsNullOrWhiteSpace(city))` to
  `if (!string.IsNullOrWhiteSpace(building))` (a room with no building is not added to a
  list; see blank-building handling below).

### 2. UI: preview + warnings (`Components/Pages/ConferenceRooms.razor`)

- **Finder preview** (`HandleFinderCsvUpload`, ~line 679-694): resolve the list by
  `row.Building` (with `row.City` for stray detection). Set `RoomListName` from the building
  name. Replace the `Missing City` list warning with **`Missing Building — will not be added
  to a room list.`** (City is still validated/warned for timezone/metadata as today).
- **Mismatch warning (Option ii):** when `ResolveRoomListAsync` reports a `strayCityListName`,
  add a row warning, e.g.:
  `A city-named list '{strayCityListName}' also exists. Rooms will be added to '{buildingListName}'. Review the stray list manually.`
- **Single-room path** (`SetupSingleRoom`, ~line 575): no signature change at the call site
  (`SetRoomMetadataAndListAsync` already receives `building`); behavior now lists by building
  automatically. Add a UI note/validation that **Building** is required to create/join a room
  list (consistent with the CSV path).
- The existing `new`/`legacy` badges (razor `:188-190`) are retained; the new stray-list
  warning renders in the existing Status/Warnings cell (`:193-200`).

### 3. Models (`Models/ConferenceRoomModels.cs`)

- Add `public string? StrayCityRoomListName { get; set; }` to `RoomFinderPreviewRow` (drives
  the mismatch warning; keeps the warning data structured rather than only a string).

### 4. Sample CSV / help text

- The Finder sample CSV (`ConferenceRooms.razor:1082-1084`) already includes a `Building`
  column — no change needed, but confirm the inline help communicates that **Building** now
  determines the room list. Update the one-line helper text near the Finder upload if present.

## Files to modify

- `Services/ConferenceRoomService.cs` — naming helpers; re-key resolve/add to building; stray-list probe.
- `Components/Pages/ConferenceRooms.razor` — preview resolves by building; mismatch warning; building-required note; helper text.
- `Models/ConferenceRoomModels.cs` — add `StrayCityRoomListName` to `RoomFinderPreviewRow`.
- `ExchangeAdminWeb.Tests/ConferenceRoomCsvParseTests.cs` (or a new `ConferenceRoomNamingTests.cs`) — tests for the pure naming helpers.
- `Modules/ModuleCatalog.cs` — bump ConferenceRooms module `Version` `2.0.1` → `2.0.2`.

## Versioning

Per `docs/ProjectConstitution.md` §Deployment And Versioning (two independent rules):
this is a **module-scoped behavior change**, so the **ConferenceRooms module `Version`
bumps** (`2.0.1` → `2.0.2`). No shared/app-wide infrastructure changes here, so the base
app `<VersionPrefix>` is **not** required to bump by the rule. **Open point for owner:** the
prior Conference Rooms CSV fix also bumped the app version (2.3.3 → 2.3.4); if you want to
keep that convention for user-visible module fixes, bump `<VersionPrefix>` + assembly/file
versions too. **Owner decision: module bump only** (the prior app bump was an error, not a
convention to repeat).

## Tests (required before "done")

Pure naming-helper unit tests (no EXO dependency — the resolve/add methods hit live EXO via
`RunPooledQueryAsync` and are not unit-testable without a harness, so the **naming logic** is
extracted specifically to be covered):

1. `BuildRoomListName("RTP")` → `"RTP Conference Rooms"`.
2. `BuildRoomListName` trims/handles a building with surrounding whitespace consistently with parse output.
3. `BuildLegacyRoomListName("Durham")` → `"RoomList-Durham"`.
4. Building-vs-city naming guard: a row with `City=Durham, Building=RTP` produces target
   name `"RTP Conference Rooms"`, **not** `"Durham Conference Rooms"` (reproduces the bug at
   the naming layer; fails on current inline code, passes after extraction+fix).

Guard-the-fix discipline (per AGENTS.md): temporarily revert the building re-key, confirm
test #4 fails, restore, confirm green.

Existing `ConferenceRoomCsvParseTests` must remain green (parsing is untouched).

## Verification

- `dotnet build -c Release` → 0 warnings, 0 errors.
- `dotnet test` → all new + existing tests pass.
- `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore` and `git diff --check HEAD`.
- **Manual (not automatable — EXO live):** upload `RTPRF2.csv` (`City=Durham, Building=RTP`)
  to the Room Finder tab → preview shows target list **`RTP Conference Rooms`**, marked
  *exists* (not `new`), and a warning if a stray `Durham Conference Rooms` list is present.
  State explicitly in the final report that this manual EXO check was or was not run.

## Out of scope

- The specific server-side error in `RTPRF_Failed.jpeg` (a side effect of the City=RTP
  workaround; revisit only if it recurs after this fix).
- Room Type tab logic, calendar permissions, booking policies — untouched.
- Any bulk migration/rename or deletion of existing room lists (Option ii is warn-only).
- Hierarchical OU restructuring for building-level lists (raised in the Nov 2025 report;
  not requested here).

## Owner decisions (resolved 2026-06-11)

1. **App version bump? — No.** Module `Version` bump only (`2.0.1` → `2.0.2`). The prior
   CSV-fix's app-version bump was an error we are living with, not a convention to repeat.
2. **Blank Building? — Approved as planned.** Blank Building = not added to a room list,
   **with a clear preview warning** so the user understands why that row is not joining a
   list. (Not Option C / city fallback.)
