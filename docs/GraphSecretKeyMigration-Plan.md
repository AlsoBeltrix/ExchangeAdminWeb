# Graph Delinea Secret Key Migration — Plan

Status: Implemented
Owner: Michael Coelho
Created: 2026-06-26
Implemented: 2026-06-26 (app 2.3.25 -> 2.3.26; commits `2eb9c98`, `063964e`)
Tracking: "MFA Reset stranded config key" (`.agents/state.md` Known issues / Next-up #1)

## Problem

The Graph credential config key was renamed `DelineaSecretId` → `GraphDelineaSecretId`
in the module catalog. The catalog (`Modules/ModuleCatalog.cs`) now declares **only**
`GraphDelineaSecretId` for the four Graph modules, so the module config page
(`Components/Pages/ModuleConfig.razor`) renders only that field. But the services still
read the old key as a fallback:

```
GetValue("MfaReset", "GraphDelineaSecretId") ?? GetValue("MfaReset", "DelineaSecretId")
```

Any environment configured **before** the rename holds its value under the OLD key
`DelineaSecretId`. Result: the config page shows the Graph secret field **blank** (it only
binds the new key), while the service keeps working through the fallback. The value is
"stranded" — present in storage, invisible in the UI, and at risk of being wiped if an
admin saves the page (a save is a whole-module delete-then-insert; the stranded old-key
row would be deleted and not re-created).

Confirmed blank in prod (pre-SQLite) and dev. The SQLite legacy import copied every key
verbatim, so it neither fixed nor worsened this.

### Affected modules (catalog declares `GraphDelineaSecretId`)

| Module | Service | Current read |
| --- | --- | --- |
| `MfaReset` | `Services/MfaResetService.cs:22,43` | `Graph… ?? DelineaSecretId` |
| `NamedLocations` | `Services/NamedLocationsService.cs:22,44` | `Graph… ?? DelineaSecretId` |
| `M365GroupManagement` | `Services/M365GroupManagementService.cs:32-33,55-56` | `Graph… ?? DelineaSecretId` |
| `EmergencyDisable` | `Services/EmergencyDisableService.cs:271` | `GraphDelineaSecretId` only (no fallback) |

`EmergencyDisable` already reads only the new key. If it ever held a pre-rename config it
is silently broken **today**; the same migration un-strands it.

### NOT affected — do not touch

The on-prem modules legitimately use `DelineaSecretId` as their **current** key
(`MailboxPermissions`, `CalendarPermissions`, `ConferenceRooms`, `Comms10k`,
`DhcpAuthorization`, message-tracking, etc.). The migration MUST be scoped to Graph
modules only, identified by the catalog declaring a `GraphDelineaSecretId` ConfigField —
never by blanket-renaming `DelineaSecretId` everywhere.

## Decision (owner, 2026-06-26)

1. **Scope: all Graph modules**, via a catalog-driven one-time migration, not MFA Reset
   alone. Same effort, and no other environment silently hits this later.
2. **One-time data migration** rewrites the stranded row: for each module whose catalog
   descriptor declares a `GraphDelineaSecretId` ConfigField, if the stored config has a
   `DelineaSecretId` value but no `GraphDelineaSecretId` value, copy the value to the new
   key and remove the old key. Idempotent; runs at startup alongside the existing
   migrator/seed step.
3. **Retire the service-side `?? DelineaSecretId` fallback** in the three services once the
   data migration guarantees the value lives under the new key. After this, all four
   services read `GraphDelineaSecretId` only — one key, one source of truth.

## Design

### A. One-time config migration (new code)

Add a small startup step that performs the key remap. It runs **after** the schema
migrator and legacy JSON import (so any imported stranded rows are already present) and
in the same `Program.cs` startup block as `SeedMissingModules()`.

Mechanism (catalog-driven, fail-safe):

- Enumerate catalog modules whose `ConfigFields` contain a key `GraphDelineaSecretId`.
- For each, read current config via the existing repository.
- If `DelineaSecretId` has a non-empty value **and** `GraphDelineaSecretId` is
  absent/empty: write the value under `GraphDelineaSecretId` and delete the
  `DelineaSecretId` row, in one transaction.
- If `GraphDelineaSecretId` already has a value: leave both alone except delete a now-dead
  `DelineaSecretId` row if present (new key wins; no data loss). *(Decide in implementation
  whether to delete the dead old-key row or leave it; default: delete it so a later page
  save can't resurrect confusion. Either way the new key is authoritative.)*
- No row / nothing stranded: no-op (no write, no change-token bump).

Implementation choice (to confirm during build, not now):
- **Option 1 — repository method.** Add a scoped `MigrateGraphSecretKeys()` to a
  repository/service that does the read-modify-write through the existing
  `ModuleConfigRepository` transaction primitives (`SaveModule` is whole-module
  delete-then-insert; a narrower targeted remap may warrant a new repo method to avoid
  rewriting unrelated rows). Preferred: keeps SQL in the repository layer per the storage
  design.
- This is **non-destructive seeding-class** work, consistent with the 2026-06-12 SQLite
  decision relaxation (INSERT/repair-if-missing at startup is allowed; destructive
  overwrite of existing distinct values is not). It never overwrites an existing
  `GraphDelineaSecretId` value.

### B. Retire the fallback (edit existing code)

In `MfaResetService`, `NamedLocationsService`, `M365GroupManagementService`, change both
read sites each from:

```
GetValue(<module>, "GraphDelineaSecretId") ?? GetValue(<module>, "DelineaSecretId")
```

to:

```
GetValue(<module>, "GraphDelineaSecretId")
```

`EmergencyDisableService` already reads only the new key — no edit, but it is covered by
the migration in A.

### Ordering / safety

- Migration (A) must land and run **before or with** the fallback removal (B) in the same
  build, so no environment is left reading the new key before its value has been moved
  there. Since both ship together, startup order guarantees A runs at boot before any
  request hits B.
- The migration is idempotent and bumps no change token when there's nothing to do, so
  repeated startups are clean.

## Files

- `Modules/ModuleCatalog.cs` — no field changes (already correct); used as the source of
  truth for "which modules are Graph modules."
- `Services/Storage/ModuleConfigRepository.cs` — possibly one new targeted remap method.
- New or existing startup helper for `MigrateGraphSecretKeys()` (mirroring
  `SeedMissingModules()` placement in `Program.cs:140-151`).
- `Program.cs` — invoke the migration in the startup block.
- `Services/MfaResetService.cs` — drop fallback (2 sites).
- `Services/NamedLocationsService.cs` — drop fallback (2 sites).
- `Services/M365GroupManagementService.cs` — drop fallback (2 sites).

## Tests (`ExchangeAdminWeb.Tests/`)

New tests, each proven non-vacuous (revert the fix, see it fail, restore):

1. **Migration moves a stranded value.** Seed a module's config with `DelineaSecretId="123"`
   and no `GraphDelineaSecretId`; run the migration; assert `GraphDelineaSecretId=="123"`
   and `DelineaSecretId` is gone.
2. **Migration does not overwrite an existing new-key value.** Seed both keys with
   different values; assert `GraphDelineaSecretId` is unchanged.
3. **Migration is scoped to Graph modules.** Seed an on-prem module (e.g. `ConferenceRooms`)
   with `DelineaSecretId`; assert it is untouched.
4. **Migration is idempotent / no-op.** Run twice; second run makes no changes (and bumps
   no change token).
5. **Service reads the new key only.** A service-level test (at least for `MfaResetService`)
   that `IsAvailable` / client construction works with only `GraphDelineaSecretId` set and
   that a value under only the old key is NOT picked up (proving the fallback is gone).

## Versioning (per Constitution §Deployment And Versioning)

- **Module versions** bump for the four Graph modules whose behavior/config handling
  changed: `MfaReset`, `NamedLocations`, `M365GroupManagement`, `EmergencyDisable` (the
  last only if its migration coverage counts as a behavior change — confirm at build; the
  read path is unchanged, so it may not need a bump).
- **App version** (`<VersionPrefix>` + `AssemblyVersion` + `FileVersion`) bumps because a
  shared startup migration step is added (app-wide change).
- Apply bumps per the two-independent-rules invariant; each fires on its own.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx` (full suite green; new tests proven non-vacuous)
- `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore`
- `git diff --check HEAD`
- **Manual (post dev deploy, not automatable):** open each affected module's config page
  and confirm the Graph secret field now shows the previously-stranded value, and that the
  module still functions. State clearly if this manual check is deferred until the next dev
  deploy.

## Out of scope

- No change to on-prem `DelineaSecretId` usage.
- No change to the Delinea/PAM resolution seam or credential backend.
- No new PAM backend.

## Commit slicing

Per Git Safety (one item per commit):
1. Migration code + its tests (A).
2. Fallback removal + service test (B).
3. Version bumps + this plan → `Implemented`.

(Or fold version bumps into slice 2 if A and B must ship together to be safe — they do; if
combined, ship A+B as one coherent commit since the migration must precede the fallback
removal at runtime. Final slicing confirmed at implementation time.)
