# psl-10: Remaining error-stream leftovers after psl-8 - Emergency Disable's bound-object re-read, Self-Service Groups' caller and group reads, the DHCP remote script, and the live-directory test classes

**Severity**: LOW - recorded under the 2026-10-23 stopping rule, not dispatched. (1) `EmergencyDisableService.VerifyBoundObject`
neither checks nor clears the stream; a record its re-read leaves past Stop is picked up by the disable's
and the reset's `HadErrors` checks (FAILED after the write) and by the lockdown OU read (refuses the move).
(2) `SelfServiceGroupService.ResolveCallerDn` and the owned-groups / search `Get-ADGroup` reads leave
records for `CallerCanManageMembers`' `HadErrors`, excluding the first group from the list.
(3) `DhcpAuthorizationService`'s remote script imports DhcpServer and makes the change in one invocation;
a record left by the import fails the change's `HadErrors` (unprobed; no clear between the two is possible
from C#, so the script itself must clear `$Error` or the change must run separately). (4)
`ADDirectoryValidateExistsTests.Search_AllObjectKindsIncludingOu_DoNotThrow` (4 cases) runs live searches
through the production probe wherever AD is reachable; it could use the psl-8 stand-in. (5) The
deliberately live classes `ADDirectoryLiveTests` (5) and `AdDirectoryForestSearchLiveTests` (3) query the
live directory on every full-suite run on this host - the "no live directory calls" rule for agents is not
met by the mandated gate itself; whether those classes stay in the default run is the owner's call.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; held by the 2026-10-23 stopping rule (LOW) for after the first dev deploy
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the psl-8/a2w/a2x agent, 2026-10-23, candidates 2-6 of its hand-back)

## Evidence

At `9293b77a`: `Services/EmergencyDisableService.cs` 1256 (`VerifyBoundObject`), 690, 926, 757;
`Services/SelfServiceGroups/SelfServiceGroupService.cs` 1286, 108-114, 182, 1408;
`Services/DhcpAuthorizationService.cs` 101, 165, 111, 175; `ExchangeAdminWeb.Tests/ADDirectoryValidateExistsTests.cs`
238; `ExchangeAdminWeb.Tests/ADDirectoryLiveTests.cs`, `AdDirectoryForestSearchLiveTests.cs`.

## Approach (to be written by the implementing agent before code)

- Each read that a later `HadErrors` check follows clears the stream in a `finally` (or the check reads only
  its own invocation's records); the DHCP script clears `$Error` between the import and the change, or the
  import runs as its own invocation.
- The validate-exists test uses the psl-8 stand-in; the two live classes move behind an explicit opt-in
  trait so the default gate makes no live call (owner decision recorded when taken).
- Mutations at the call site: one `finally` clear dropped; the stand-in bypassed.

## Guard proof

Pending.
