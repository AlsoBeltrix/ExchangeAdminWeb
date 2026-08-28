# pgwt-5: Forest-wide picker results cannot be validated outside the local domain

**Severity**: MEDIUM - a WINROOT group picked from the forest-wide suggestions is refused
as nonexistent by the local-domain revalidation, so it cannot be protected via the UI.
**Status**: In progress
**Branch**: `-` (default-branch mode)
**Commit**: (filled in after commit)

## Evidence

`ExecuteValidateExists` runs `Get-ADGroup` with no `-Server`, which binds the local domain
(the same file documents this for the picker and routes ITS search through the global
catalog). The admin page's pickers are forest-wide; validation of the selected foreign DN
then returns NotFound. Pre-existing for the Protected Groups list; inherited by the new
target list, where it blocks the capability outright.

## Predicted observable failure

Admin picks `WINROOT\...` from suggestions; Add refuses "was not found in Active
Directory"; the group cannot be protected through the supported UI (the pgwt-1/idm-3
unreachable-capability shape).

## Approach

DN-shaped identities route the validation lookup to the DN's owning domain:
`ExecuteValidateExists` derives `-Server` via the existing `DnsDomainFromDn` when the
(normalized) identity contains `=`. Non-DN input keeps today's local binding. This heals
the older Groups/OU lists through the same single call site.

## Files changed

- `Services/ADDirectorySearchService.cs` - DN-routed validation
- `ExchangeAdminWeb.Tests/ProtectedGroupWriteTargetTests.cs` - tripwire

## Guard proof

- `ProtectedGroupWriteTargetTests::Validation_RoutesDnShapedLookups_ToTheOwningDomain` -
  source tripwire (live cross-domain lookup needs a directory); revert fails, restore
  passes. Manual check rides the deploy: pick a WINROOT group as a target.

## Coder dispute (if any)

None.

## Known gaps

Live cross-domain validation unproven until the deploy-time manual check.

## Reviewer comments

`Reviewer: codex-commercial / gpt-5.6-sol / xhigh / standard` (owner standing dispatch),
generation pass over `8700531..5336072`, verdict `findings` (7), capability_ok true.
Verification round: NOT DISPATCHED - blocked by the workspace-write transport fault
recorded on lst-1.
