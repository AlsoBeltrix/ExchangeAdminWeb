# Retire the `Security:ExcludedUsers` appsettings fallback

Status: Approved (owner, 2026-07-28). Owner ruled: remove the appsettings
`Security:ExcludedUsers` source entirely and retire the two code fallbacks that read
it. `CLD_LIC_MS_BOD` (present only in appsettings, with no database counterpart on
either install) correctly loses protection: the owner removed that group from the UI
as the wrong one, and its lingering presence in appsettings is itself the bug this
change fixes.

## Problem

Protected-principal configuration is meant to live in the SQLite config store
(`protected_principal` table) and be visible/editable on the Admin Settings ->
Protected Principals page. But two services silently read a SECOND, older source that
the admin UI never displays:

- `Services/PermissionValidator.cs:50` (`GetConfiguredExclusions`)
- `Services/ProtectedPrincipalService.cs:421` (`GetLegacyExclusions`)

Both first read the `MailboxPermissions/ExcludedUsers` module-config value; when that
is empty they fall back to the `Security:ExcludedUsers` string array in
`appsettings.json`. On both the dev (`D:\inetpub\ExchangeAdminWebDev`) and prod
(`D:\inetpub\ExchangeAdminWeb`) hosts the module-config value is empty, so the
appsettings array is the LIVE source. It contains:

```
VRStaff@analog.com
CLD_LIC_MS_BOD
vincent.roche@analog.com
mcoelho-2@analog.com
```

Because this source is invisible in the admin UI, an admin cannot see why a principal
is blocked, nor unprotect it from the UI. This was hit in practice: `mcoelho-2` (a
test account) was blocked by the self-service groups gate with no visible cause.

## Reconciliation against the DB store (verified read-only on both installs, 2026-07-28)

Both installs' `protected_principal` tables are identical:

```
group|ANALOG\VR Staff
group|CN=ADIBoard,OU=SYNC,OU=Distribution Lists,OU=Recipients,OU=Analog,OU=Exchange,DC=ad,DC=analog,DC=com
group|CN=CEO Staff,CN=Users,DC=ad,DC=analog,DC=com
user|Janene.Asgeirsson@analog.com
user|Nitin.Mittal@analog.com
user|Russell.Koste@analog.com
user|vincent.roche@analog.com
```

Mapping each appsettings entry to the DB store:

| appsettings entry     | DB counterpart                          | Effect of removal |
|-----------------------|-----------------------------------------|-------------------|
| `vincent.roche@analog.com` | `user\|vincent.roche@analog.com` (exact) | None - true duplicate. |
| `VRStaff@analog.com`  | `group\|ANALOG\VR Staff` (broader, transitive) | None - DB group form already covers it. |
| `mcoelho-2@analog.com`| none                                    | Correctly loses protection - test account, never should have been protected. |
| `CLD_LIC_MS_BOD`      | **none**                                | Correctly loses protection - owner removed it from the UI as the wrong group; its lingering presence in appsettings is the bug (2026-07-28). |

No migration step is required: the three entries that must stay protected are already
in the DB store on both installs.

## Non-goals

- No change to the DB `protected_principal` store contents (no rows added or removed).
- No change to the `MailboxPermissions/ExcludedUsers` module-config read path (the
  primary source stays; only the appsettings fallback is retired).
- No change to any other `Security:*` appsettings fallback
  (`SectionAccess`, `ProtectedPrincipalDirectoryReadSecretId`, `PreventSelfGrant`)
  or to `AllowedGroups`.

## Scope split

Two independent halves; the code half is what this plan + source control cover, the
host half is runtime data outside source control.

- **Code (source-controlled, this plan):** retire the two appsettings fallback reads
  so nothing can ever silently re-read a file-based exclusion source again.
- **Host data (runtime, per box):** remove the `Security:ExcludedUsers` block from
  `appsettings.json` on the dev and prod hosts. This file is excluded from source
  control and never overwritten by deploys, so it must be edited on each host. It is
  NOT done from this repo and NOT part of the build.

Safe order: land + deploy the code change first (after which the appsettings block is
already inert because the reader is gone), then remove the now-dead block from each
host's `appsettings.json` as cleanup. Removing the code first means there is never a
window where the block is silently read.

## Slices

### Slice 1 - Retire the `ProtectedPrincipalService` fallback

`Services/ProtectedPrincipalService.cs` `GetLegacyExclusions()` (line ~415): drop the
`_config.GetSection("Security:ExcludedUsers")` fallback branch. The method keeps
reading `MailboxPermissions/ExcludedUsers` module config and returns `[]` when that is
empty. Update the method's comment to state the appsettings fallback was retired
(2026-07-28) and module config is the only legacy source.

Fail-closed behavior is unchanged: an empty legacy source contributes no matches, and
the DB store remains the primary protection source with its existing fail-closed load
path.

### Slice 2 - Retire the `PermissionValidator` fallback

`Services/PermissionValidator.cs` `GetConfiguredExclusions()` (line ~42): same change -
drop the `Security:ExcludedUsers` fallback branch, keep the module-config read, return
empty array when module config is empty. Update the comment.

### Slice 3 - Tests

- `ExchangeAdminWeb.Tests/PermissionValidatorTests.cs`: the helper seeds exclusions via
  `Security:ExcludedUsers:{i}` (line ~27). After Slice 2 that key is no longer read, so
  those tests would silently exercise nothing. Re-point the helper to seed the
  `MailboxPermissions/ExcludedUsers` module-config value instead (the path that is
  actually read), so the existing exclusion assertions keep guarding real behavior.
- Add a test asserting that a value present ONLY under `Security:ExcludedUsers` is NOT
  treated as excluded (guards the retirement - proves the fallback is gone).
- `ExchangeAdminWeb.Tests/ProtectedPrincipalServiceTests.cs`: add/adjust a test proving
  `Security:ExcludedUsers` no longer contributes a `LegacyExclusion:` match, while
  `MailboxPermissions/ExcludedUsers` module config still does.
- Non-vacuity: for each new guard test, confirm it fails if the fallback line is
  restored, then passes with it removed.

### Slice 4 - Docs

- `README.md:453`: remove/replace the "falls back to `Security:ExcludedUsers`" line.
- `docs/SqliteConfigStore-Plan.md:102`: mark the `Security:ExcludedUsers` fallback row
  retired (2026-07-28) rather than "transitional".
- `docs/ProdReadinessReview-2026-06-12.md` context is historical; do not rewrite that
  review, but the catalog finding at line 306 is resolved by removing the read rather
  than adding a field.

### Slice 5 - Host cleanup (runtime, not source; recorded here for the operator)

On each host, remove the `Security.ExcludedUsers` array from
`<PublishPath>\appsettings.json`:

- Dev: `D:\inetpub\ExchangeAdminWebDev\appsettings.json`
- Prod: `D:\inetpub\ExchangeAdminWeb\appsettings.json`

Leave `Security.PreventSelfGrant` and `Security.AllowedGroups` intact. This is a
manual host edit performed after the code change is deployed; it is verification-only
from this repo's perspective (nothing to build or test).

## Versioning

Shared authorization-layer behavior change (affects every mutating module's protected-
target gate), so bump the base app version (`<VersionPrefix>` + `AssemblyVersion` +
`FileVersion` in `ExchangeAdminWeb.csproj`) per Constitution "Deployment And
Versioning". No single module owns this; no module `Version` bump.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- Non-vacuity proof on each new guard test (revert the fallback removal -> test fails).
- Manual (not automatable here): confirm on each host after deploy + appsettings edit
  that a formerly-appsettings-only principal is no longer reported protected, and that
  the three DB-backed principals still are.
