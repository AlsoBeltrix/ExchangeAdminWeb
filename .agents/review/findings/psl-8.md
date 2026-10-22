# psl-8: The remaining inline AD imports, the Conference Rooms private import copy, and the live-AD probe in the test suite

**Severity**: LOW - the psl-7 class on the sites psl-7 did not cover. (1) `ConferenceRoomService.ImportActiveDirectoryModule`
is still a private copy of the shared `ActiveDirectoryModuleImport` helper. (2) The other inline AD module
imports - `Comms10kService`, `EmergencyDisableService`, `LicensingUpdatesService`, `ProtectedPrincipalService`,
plus the DHCP and Exchange module imports - were not audited for a record left in the error stream being
taken for a later read's error. (3) The full-suite gate imports the real ActiveDirectory module through the
production probe in `ADDirectorySearchServiceTests` (the three known "AD unavailable" skips); psl-7's new
`CreateProbeRunspace` seam would let those tests use a stand-in, removing the suite's one live-module
dependency.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
**Branch**: `worktree-agent-a56481d2c0a45a8cb`
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs includes ALL the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21); the
admitted approach above and the supervising session's brief. One fix commit, first of three on branch
`worktree-agent-a56481d2c0a45a8cb` (then aud-a2w, then aud-a2x, which edit the same Conference Rooms file). Base
`e9886df7`; the line numbers below are that tree's.

**Rule** (psl-7's). An import's error is the one it throws, named by whatever names its caller's steps; nothing it
leaves in the stream is any later read's. Where a later check on the same PowerShell could read a record the import
left, the import goes through `ActiveDirectoryModuleImport.Import` (an AD import) or clears the stream after itself.

**The audit.** `git grep -n "Import-Module" -- '*.cs'` outside the tests, and `ImportPSModule`, every site outside
psl-7's five. "First check" is the first read of `HadErrors` or `Streams.Error` on that PowerShell after the import.

| Site | The import clears | First check after it | Verdict |
| --- | --- | --- | --- |
| `ConferenceRoomService.ImportActiveDirectoryModule` (2201; called 1620, 1722, 1920, 2031) | commands and the stream (aud-a2r's private copy of the helper's shape) | - | part 1: the four calls go to the helper, the copy goes |
| `EmergencyDisableService.ReadAdEnabledState` 482 | commands only | the pre-state read's `ps.HadErrors \|\| result.Count == 0` (496), which throws the first record as the read's error | takes it: fix |
| `EmergencyDisableService.ExecuteDisableAD` 658 | commands only | `VerifyBoundObject` checks none; the write's `if (ps.HadErrors)` (679), after `Set-ADUser -Enabled $false` ran: FAILED with the import's record though the account was disabled | takes it: fix |
| `EmergencyDisableService.ExecuteMoveToLockdownOu` 730 | commands only | the lockdown OU read's `ps.HadErrors \|\| ouLookup.Count == 0` (750): "Configured lockdown OU could not be read: <the import's record> The account was NOT moved." | takes it: fix |
| `EmergencyDisableService.ExecuteResetPassword` 897 | commands only | the reset's `if (ps.HadErrors)` (923), after `Set-ADAccountPassword -Reset` ran: FAILED though the password was reset | takes it: fix |
| `SelfServiceGroupService.PrepareAdRunspace` 1259 (callers 100, 177, 254, 561, 857, 917) | commands only | the owned-groups list and the single-group search: `CallerCanManageMembers`' `HadErrors` (1391), which excludes the first group; the member listing and the membership change: `ResolveGroupByGuid`'s (1097) - "That group no longer exists, or could not be read"; the two bulk reads clear before each query (863, 923) | takes it (four callers): fix the helper |
| `SelfServiceGroupService.GroupWithIdentityExists` 1232 | commands only | the probe's `if (ps.HadErrors) return false` (1242): no group named - the refusal's wording only | takes it: fix |
| `SelfServiceGroupService.ResolveListedMemberByGuid` 979, `ResolveUserMember` 1133 | commands only | none (counts only) | unchanged |
| `Comms10kService` 100, 192, 326, 468, 807 | commands, and the stream on a throw (`InvokeAndClear`) | every read clears the stream first (114, 144, 200, 342, 579, 599, 647, 666, 686, 725) | unchanged |
| `LicensingUpdatesService.ResolveUsers` 229 | commands only | none; each row's `finally` clears the stream (285) | unchanged |
| `LicensingUpdatesService.ApplyChanges` 321 | commands and the stream (psl-2, 326) | - | unchanged |
| `DhcpAuthorizationService.GetAuthorizedServersAsync` 36 (DhcpServer) | commands only | none | unchanged |
| `DhcpAuthorizationService.AuthorizeServer` 90, `DeauthorizeServer` 154 (ActiveDirectory) | commands only | discovery checks none, and its `finally` clears the stream (psl-3, 218, 245) before the change's check (111, 175) | unchanged |
| the DHCP remote script blocks 101, 165 (`Import-Module DhcpServer -ErrorAction Stop; <the change>`) | one `Invoke-Command`: the import's records and the change's come back in one stream | the change's `HadErrors` (111, 175) - the same invocation, so no clear can come between them | a known gap (below), unchanged |
| `ProtectedPrincipalService` 832, 991, 1509 (owned by another agent) | 832 the stream too; 991, 1509 commands only | 991 none; 1509's directory reads clear first (`PowerShellProtectedGroupDirectory` 180, 202) | nothing to leave for a follow-up |
| `ExchangeServiceBase.CheckAdGroupMembership` 678 (owned by another agent) | commands only | none | nothing to leave for a follow-up |
| `ExoConnectionPool.CreateConnected` 679 (ExchangeOnlineManagement; owned by another agent) | commands only | `ReadConnectionIds`' `ps.Streams.Error.Count > 0` (644): the "before" list returns null, so the new connection's id is not identified | takes it: left for a follow-up (below) |
| `ImportPSModule`: `ADDirectorySearchService` 618, `BitLockerLiveDirectorySearch` 253, `PowerShellDirectoryCommands` 171 | the import runs at the runspace's Open, in the session's own pipeline | measured (`scratchpad/psl8/probe-iss.ps1`, pwsh 7.6.6, a module that does not exist, no directory): Open does not throw, a later PowerShell's clean read reads `HadErrors` false with no record; the failure is only in the session's `$Error` | not this class |

No dead `HadErrors` branch follows a Stop import anywhere now: psl-7 removed the last two. The approach's "one dead
branch restored" mutation has no site.

**Changes.**

1. `Services/ConferenceRoomService.cs`: the four calls of `ImportActiveDirectoryModule(ps)` become
   `ActiveDirectoryModuleImport.Import(ps)`; the private copy and its comment go (the helper's remarks say the same).
   The same statements in the same order: behaviour identical.
2. `Services/EmergencyDisableService.cs`: the four imports are the helper. The four sessions come from a new
   `internal virtual Runspace CreateDirectoryRunspace()` - psl-7's `GroupManagementService` seam: the session each built
   inline (`CreateDefault`, `Bypass`), opened where it was. `ReadAdEnabledState`, `ExecuteDisableAD`,
   `ExecuteMoveToLockdownOu` and `ExecuteResetPassword` become `internal` for the tests, their signatures otherwise
   unchanged. No step, message, order or write changes otherwise.
3. `Services/SelfServiceGroups/SelfServiceGroupService.cs`: `PrepareAdRunspace` is the helper (and `internal`);
   `GroupWithIdentityExists(creds, memberIdentity)` keeps its runspace and hands it to a new `internal static bool
   GroupWithIdentityExists(PowerShell ps, PSCredential credential, string memberIdentity)` - the sequence on a
   caller-supplied PowerShell, psl-2's seam - whose import is the helper; `ResolveGroupByGuid` becomes `internal` for
   the tests. The bound text the tripwires pin (`GroupWithIdentityExists(creds.Value, memberIdentity)`, the bounded
   bodies) is unchanged.
4. `ExchangeAdminWeb.Tests/ADDirectorySearchServiceTests.cs`: `CreateService()` sets `CreateProbeRunspace` to a stand-in
   session whose `Import-Module` is an advanced function writing a bare `Write-Error` - the real cmdlet's shape for a
   module it cannot find, thrown by the probe's Stop (aud-a2r) - with autoloading off, and checks before handing the
   service over that the directory is unavailable and that the stand-in took the probe's one import. So nothing in the
   class can reach a search: `Search` returns before any session while unavailable. The three
   `*_WhenAdUnavailable_ReturnsEmpty` lose their `Assert.SkipWhen` and run everywhere; `IsAvailable_WhenAdUnavailable_
   ReturnsFalse` asserts false, and the warning carrying the stand-in's error. The class leaves
   `LiveDirectoryCollection`, whose rule is that pure tests must not join it.
5. Comments after the mutation round, checked code-identical; `ActiveDirectoryModuleImport`'s remark that Conference
   Rooms keeps its own copy corrected there.

**Tests** (stand-in sessions only: every `Import-Module` and read an advanced function, autoloading off; no directory,
module or network), in `ExchangeAdminWeb.Tests/DirectoryModuleImportTests.cs`, psl-7's file and its `StandIn`:
- Emergency Disable, for each of the four steps (the pre-state read, the disable, the lockdown move, the password
  reset): an import that reports an error without throwing is not taken for the step's error - the read answers, the
  step is OK, the move is Moved and stamped, the write was made; and a failing import is the step's failure, carrying
  its error, with no read sent.
- Self-Service Groups: after `PrepareAdRunspace` on a reporting import the stream is empty and `ResolveGroupByGuid`
  returns the group; the group probe on a reporting import says a group answers; a failing import throws its own error
  from `PrepareAdRunspace` with no read sent.
- Conference Rooms, part 1: aud-a2r's tests through the four paths, unchanged
  (`AnErrorTheImportReportsWithoutThrowing_IsNotTakenForAnyStepsError`,
  `AFailureAtEachStepOfTheCityStateAndCountryPreflight_NamesThatStep`, `AFailedImportOnTheOnPremRoomListPaths_NamesThatStep`,
  aud-a2q's write-import case).
- `ADDirectorySearchServiceTests`, part 3, as in change 4.

**Mutations, at the call sites** (md5 before and after, the named failing tests, restored, touched, rebuilt, md5
matched, green; filter `DirectoryModuleImportTests|ADDirectorySearchServiceTests|ConferenceRoom|EmergencyDisable|
SelfService` - stand-ins and source scans only, the live directory classes excluded):
- M1: the City/State/Country preflight's import inline with a commands-only clear (the shape the Emergency Disable sites
  had) - aud-a2r's reporting-import test fails.
- M2a-d: each Emergency Disable site's import as at `e9886df7` in turn. M2e: `PrepareAdRunspace` as at `e9886df7`. M2f:
  the group probe's import as at `e9886df7`.
- Part 3: P1 `Search`'s unavailable return answering one result instead of none (the three un-skipped tests fail); P2
  the probe's catch marking the directory available (the guard in `CreateService` fails before any search); P3 the
  probe opening an empty session of its own instead of `CreateProbeRunspace`'s - a bypass that cannot load anything,
  `Import-Module` not existing there (measured, `scratchpad/psl8/probe-empty-session.ps1`) - so the guard's pin on the
  stand-in's import is what fails. Not run, by design: the probe opening production's own session in place of the seam,
  which imports the real module, and the module's import connects to the directory for its drive (*documented module
  behaviour, not probed*) - the probe the owner forbids (psl-7 declined seam reverts for the same reason).

**Left for a follow-up (file owned by another agent)** - recorded here, not fixed:
- `Services/ExoConnectionPool.cs` `CreateConnected`, 679-682:
  `ps.AddCommand("Import-Module").AddParameter("Name", "ExchangeOnlineManagement").AddParameter("ErrorAction", "Stop");
  ps.Invoke(); ps.Commands.Clear();`. A record the import writes past Stop stays in the stream; the next check is
  `ReadConnectionIds`' `if (ps.Streams.Error.Count > 0) return null;` (644), so the list of connections before the connect
  reads as unknown, `NewConnectionId` returns null, and the new connection is logged as unidentifiable and left to time
  out on its server instead of being disconnected by its id (A6B). Needed: `try { ps.Invoke(); } finally {
  ps.Commands.Clear(); ps.Streams.Error.Clear(); }` around the import (the shape of `ActiveDirectoryModuleImport`, for this
  module), with a stand-in test that a reporting import leaves the id identified. *Assumption, not probed:* the real
  ExchangeOnlineManagement import writes no error past Stop.

**Files.** `Services/ConferenceRoomService.cs`, `Services/EmergencyDisableService.cs`,
`Services/SelfServiceGroups/SelfServiceGroupService.cs`, `ExchangeAdminWeb.Tests/DirectoryModuleImportTests.cs`,
`ExchangeAdminWeb.Tests/ADDirectorySearchServiceTests.cs`, this record; in the comments commit
`Services/ActiveDirectoryModuleImport.cs`.

## Guard proof

Pending.
