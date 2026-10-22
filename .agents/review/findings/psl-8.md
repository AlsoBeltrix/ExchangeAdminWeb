# psl-8: The remaining inline AD imports, the Conference Rooms private import copy, and the live-AD probe in the test suite

**Severity**: LOW - the psl-7 class on the sites psl-7 did not cover. (1) `ConferenceRoomService.ImportActiveDirectoryModule`
is still a private copy of the shared `ActiveDirectoryModuleImport` helper. (2) The other inline AD module
imports - `Comms10kService`, `EmergencyDisableService`, `LicensingUpdatesService`, `ProtectedPrincipalService`,
plus the DHCP and Exchange module imports - were not audited for a record left in the error stream being
taken for a later read's error. (3) The full-suite gate imports the real ActiveDirectory module through the
production probe in `ADDirectorySearchServiceTests` (the three known "AD unavailable" skips); psl-7's new
`CreateProbeRunspace` seam would let those tests use a stand-in, removing the suite's one live-module
dependency.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the psl-7/a28q/a26o agent, 2026-10-22, candidates 1, 2 and 5 of its hand-back)

## Evidence

At `5e2b80f3`: `git grep -n "Import-Module" Services/*.cs` for every site outside the five psl-7 covered;
`Services/ConferenceRoomService.cs` `ImportActiveDirectoryModule`; `ExchangeAdminWeb.Tests/ADDirectorySearchServiceTests.cs`
and the `_WhenAdUnavailable_ReturnsEmpty` skips.

## Approach (to be written by the implementing agent before code)

- Point the Conference Rooms copy at `ActiveDirectoryModuleImport.Import` (behaviour identical; the aud-a2r
  import test stays green).
- Audit each remaining import site: where a later read's `HadErrors` could take the import's record, route the
  import through the shared helper (AD) or an equivalent `finally` clear (DHCP, Exchange modules); where a dead
  `HadErrors` branch follows a `-ErrorAction Stop` import, remove it with the comment. One stand-in test per
  changed site, the psl-7 shape.
- `ADDirectorySearchServiceTests`: use the `CreateProbeRunspace` seam with a stand-in so the three skips become
  real passes and the suite imports no live module; record whether the "3 known AD skips" baseline becomes 0.
- Mutations at the call site: one `finally` clear dropped; one dead branch restored; the probe seam bypassed.

## Guard proof

Pending.
