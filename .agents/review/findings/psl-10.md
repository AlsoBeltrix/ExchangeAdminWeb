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

### As written before each item's code (branch `fix/psl10-a2y`, forked from master `3e43b35b`)

Line numbers are the base's. One item per commit, after aud-a2y and kd-5 on the same branch (item (2) builds on aud-a2y
(2)'s credential seam).

**The SDK facts the items rest on**, measured in this session (pwsh 7.6.6, `[powershell]::Create()` in-process, local
functions only - no module, directory, network or remote session; scratchpad `psl10/probe-stream.ps1`): a record left in the
stream makes the next clean invocation's `HadErrors` true (psl-2); `Streams.Error.Clear()` does not reset the flag already
set, the next clean invocation does (psl-6); `$Error.Clear()` in the same script leaves the record in the stream and
`HadErrors` true; `2>$null` on the command that writes it drops the record (stream empty, `HadErrors` false) while a bare
`Write-Error` under `-ErrorAction Stop` still throws through it; and in a second runspace standing for the remote session,
with its error records re-emitted by the caller, the same three hold (`$Error.Clear()` leaves the change "failed",
`2>$null` leaves it clean).

**(1) Emergency Disable's bound-object re-read.** `Services/EmergencyDisableService.cs` `VerifyBoundObject` (1256): its
`Get-ADUser` (1258-1263) is followed by `ps.Commands.Clear()` alone (1264), so a record the re-read leaves past Stop stays,
and the next check on that PowerShell takes it: the disable's `if (ps.HadErrors)` after `Set-ADUser -Enabled $false` (690) -
FAILED after the write was made; the lockdown move's OU read `ps.HadErrors || ouLookup.Count == 0` (757) - "Configured
lockdown OU could not be read: <the re-read's record> The account was NOT moved."; the reset's `if (ps.HadErrors)` after
`Set-ADAccountPassword -Reset` (926) - FAILED after the reset was made. The fix: the re-read's `Invoke` in a try whose finally
clears the commands and the error stream (`ActiveDirectoryModuleImport`'s shape). The re-read is still judged by what it
returned - no object, or another GUID, refuses as before; a throw is the step's failure as before. Tests in
`ExchangeAdminWeb.Tests/DirectoryModuleImportTests.cs` (its Emergency Disable stand-ins): a re-read that answers the bound
object and leaves a record without throwing - the disable OK with its `Set-ADUser` sent, the move Moved and Stamped with its
`Move-ADObject` sent, the reset OK with its `Set-ADAccountPassword` sent. Mutation: the fix reverted. Emergency Disable
1.5.1 -> 1.6.0 (`EmergencyDisableServiceTests` pins the version).

**(2) Self-Service Groups' caller and group reads.** `Services/SelfServiceGroups/SelfServiceGroupService.cs`:
`ResolveCallerDn` (1286-1299) and the group reads of the owned-groups list (108-114) and the single-group search (182-188)
each clear only the commands, so a record any of them leaves past Stop is taken by the first group's
`CallerCanManageMembers` check, `if (ps.HadErrors)` (1408) - that group excluded from the list, or the searched group
answered "not found, or you do not have permission" (psl-5 cleared the check's own records, not the reads' before it). The
fix: `ResolveCallerDn`'s read, and the two group reads through one new helper `ReadGroups(ps, credential, ldapFilter)`, clear
the commands and the error stream in a finally; each is still judged by what it returned (no caller DN throws; the list is
the groups returned). Seam for the tests: `internal virtual Runspace CreateDirectoryRunspace()` - GroupManagementService's
and EmergencyDisableService's shape - for those two paths, building the session they built inline; with aud-a2y (2)'s
`LookupCredentialsAsync` seam, the two paths run for real on a stand-in session. Tests in `DirectoryModuleImportTests`: the
owned-groups list and the search, with the caller read, then the group read, leaving a record without throwing - both groups
listed, the searched group found manageable. Mutations: each clear dropped in turn. Self-Service Groups already takes its one
minor step in aud-a2y (2) (1.12.0) on this branch.

**(3) The DHCP remote script.** `Services/DhcpAuthorizationService.cs` 101 and 165: `"param($DnsName, $IPAddress)
Import-Module DhcpServer -ErrorAction Stop; Add-DhcpServerInDC ..."` (and `Remove-...`) in one `Invoke-Command`, whose
records come back in the one stream the change's `HadErrors` (111, 175) reads. The record's first option - `$Error.Clear()`
between the import and the change - is measured above not to work: the record has already been written to the stream, and
`$Error` is a session variable apart from it. The second - the import as its own invocation - would take a persistent remote
session (`New-PSSession`, two `Invoke-Command -Session` calls, cleanup on every path) against a module this host does not
have. The fix: the import's own error stream redirected, `Import-Module DhcpServer -ErrorAction Stop 2>$null;` - the remote
twin of `ActiveDirectoryModuleImport`: the import's error is the one it throws (Stop still throws, and the script stops
before the change), and a record it writes past Stop never reaches the stream. ASCII. Tests in
`ExchangeAdminWeb.Tests/DhcpAuthorizationPowerShellLifecycleTests.cs`: a stand-in `Invoke-Command` that runs the script
block in a second runspace standing for the remote session - its `Import-Module` and `Add-/Remove-DhcpServerInDC` local
functions - and writes that session's error records into its own stream, a terminating error as a record (*assumption,
documented remoting behaviour, not probed*: `Invoke-Command -ComputerName` without `-ErrorAction Stop` reports a remote
terminating error as an error record). A remote import that leaves a record, then a clean change: success. A remote import
that fails: failed with its error, the change not run. The change's own error: failed, as psl-3's control. Mutation: the fix
reverted. *Assumption, not probed*: the DhcpServer module's import writes no error past Stop - the fix changes nothing where
it writes none. DHCP Authorization 1.4.3 -> 1.5.0.

**(4) The validate-exists search theory.** `ExchangeAdminWeb.Tests/ADDirectoryValidateExistsTests.cs` 238-244,
`Search_AllObjectKindsIncludingOu_DoNotThrow` (User, Group, OU, Any), searches "test search term" through `CreateService()`,
the production probe: on a host with RSAT it imports the real module (which connects for its AD: drive) and searches the live
directory. It takes psl-8's stand-in - `ADDirectorySearchServiceTests.CreateService`, made internal - whose probe session
fails the import as the real cmdlet does for a module it cannot find, and which checks the directory unavailable and the
stand-in's one import before handing the service over; each kind's search then answers nothing, asserted. No test left in the
class reaches a live directory (the blank-input and short-term tests return before the probe), so it leaves
`LiveDirectoryCollection`, whose rule is that such a class must not join; its remarks and `ADDirectorySearchServiceTests`'
say so. Mutation: the stand-in's session replaced by an empty one that cannot load anything (psl-8's P3 shape) - the pin on
the stand-in's import fails. Not run, by design: the theory back on the production probe (the live call itself).

**(5) The two deliberately live classes - an owner call, not changed.** See "Owner call" below.

## Owner call (item 5) - not implemented; what the opt-in would be

`ExchangeAdminWeb.Tests/ADDirectoryLiveTests.cs` (5 cases) and `ExchangeAdminWeb.Tests/AdDirectoryForestSearchLiveTests.cs`
(3 cases) exist to exercise the real directory: each builds the production `ADDirectorySearchService`, whose availability
probe imports the real ActiveDirectory module, and where that succeeds it searches the live forest; where it does not they
skip loudly (`Assert.SkipWhen` / `Assert.SkipUnless`). So the mandated gate, `dotnet test ExchangeAdminWeb.slnx`, probes the
directory on every run on a host with RSAT - this one included - while agents are told never to probe it without a go. On CI
(`windows-latest`, no RSAT) all eight skip. This branch leaves both classes exactly as they are. The opt-in, were the owner
to take it:

- **Trait.** `[Trait("Category", "LiveDirectory")]` on both classes, beside their `[Collection(LiveDirectoryCollection.Name)]`
  (which stays: it serialises them when they do run). The trait name and value are new; no test in the repo uses `[Trait]`
  today.
- **How `dotnet test` filters it.** The suite runs on VSTest (`Microsoft.NET.Test.Sdk` + `xunit.runner.visualstudio`),
  whose `--filter` reads an xUnit trait as a property of its name. The default gate becomes
  `dotnet test ExchangeAdminWeb.slnx --filter "Category!=LiveDirectory"` - in `.agents/repo-guidance.md` (Commands,
  Verification) and in CI's `build-test` step - and runs none of the eight. The opt-in is
  `dotnet test ExchangeAdminWeb.slnx --filter "Category=LiveDirectory"`, run by hand on a domain-joined host with RSAT and the
  owner's go. (A `.runsettings` `TestCaseFilter` referenced from the test project would make the bare command exclude them
  too; how it combines with a command-line `--filter` was not checked here, so the explicit filter is the recommendation.)
- **What CI would run.** `dotnet test ExchangeAdminWeb.slnx -c Release --no-build --filter "Category!=LiveDirectory" ...`:
  the eight cases leave its count (they skip there today, so nothing CI proves changes); CI never runs the opt-in, having no
  directory.
- **What it costs.** The real-cmdlet property-name checks these classes exist for (`ADDirectoryLiveTests`' remarks: a typo
  in a mapped property name compiles, passes every stand-in test and yields empty strings live) would run only when someone
  runs the opt-in, so the owner would own when that is.

Until then, this branch's own suite runs exclude the two classes by `--filter "FullyQualifiedName!~ADDirectoryLiveTests&
FullyQualifiedName!~AdDirectoryForestSearchLiveTests"`, under the standing no-probe instruction, and say so.

## Guard proof

Each row: the file's md5 with the fix, the mutation (the fix reverted to the commit before it, or one exact replacement at
the call site), rebuilt, the filter run, the failures read from the TRX; the fixed bytes copied back with a fresh mtime, md5
checked equal, rebuilt, run again. Stand-in sessions only - every Import-Module and directory or DHCP cmdlet a local
function, autoloading off where a session is built for the test; no module, directory, DHCP server or network.

**(1) Emergency Disable's bound-object re-read** - `DirectoryModuleImportTests.EmergencyDisable_ARecordTheBoundObjectReReadLeaves_IsNotTakenForTheStepsError`
(the disable, the lockdown move, the password reset), on psl-8's stand-ins with one difference - the re-read, the one
`Get-ADUser` that asks for ObjectGUID, leaves a record without throwing and answers the bound object: the disable OK, its
`Set-ADUser` sent; the move Moved and Stamped, both steps OK, `Get-ADOrganizationalUnit`, `Move-ADObject`, the stamp's
re-read and `Set-ADUser` sent in order; the reset OK, its `Set-ADAccountPassword` sent. Filter
`DirectoryModuleImportTests|EmergencyDisable` (111).

| # | Mutation | md5 fixed = restored | md5 mutated | Failing (of 111) | Restored |
| --- | --- | --- | --- | --- | --- |
| 1 | the fix reverted - `EmergencyDisableService.cs` as at `18e601f8` (the brief's "one finally clear dropped", here the only one) | `ec74fed1c01f7e0d10925b3c4dfec4c6` | `cdf3eb0201ff06f0c8f6d76a7a155fe5` | 3: the disable and the reset `FAILED` with "A record the bound-object re-read wrote past the callers ErrorAction." as their detail, their write sent; the move `Failed` | PASS 111 |

**(2) Self-Service Groups' caller and group reads** - in `DirectoryModuleImportTests`, on a stand-in session through the
two new seams' overrides (the session; the credential lookup answering a credential):
- `SelfService_ARecordTheCallerOrGroupReadLeaves_DoesNotHideTheFirstOwnedGroup` (the caller read, the group read, each
  leaving a record without throwing): `GetOwnedGroupsAsync` lists both of the caller's groups, in order, both manageable;
  the import, the caller read, the group read and the two DACL reads the calls.
- `SelfService_ARecordTheCallerOrGroupReadLeaves_DoesNotHideTheSearchedGroup` (the same two): `SearchManageableGroupAsync`
  answers the group, manageable, with no message.
- `SelfService_TheCallerRead_LeavesNoRecordForTheNextRead`: `ResolveCallerDn` (internal for this test) on a noisy caller
  read answers the caller's DN and leaves the stream and the commands empty; the group's re-read by objectGUID, which checks
  `HadErrors`, then answers.

The approach named the two clears; the first round showed the end-to-end cases cannot tell them apart - the group read
runs straight after the caller read on both paths and its clear removes the caller read's record too, so dropping the
caller read's clear alone (M1) left every case green. The third test was added to pin that clear on its own, and
`ResolveCallerDn` made internal for it (psl-8 did the same for `ResolveGroupByGuid`); M1 was re-run. No whole-file revert
row: the tests override `CreateDirectoryRunspace`, which the file as at `c4374938` lacks; M3 is the revert's substance.
Filter `DirectoryModuleImportTests`; md5 fixed = restored `acca43b7f2f79d3cc07aa3722e167654` (first round, before the third
test and the `internal`: `71d19cac3fd08cc5c6d14d3766411a76`).

| # | Mutation (call site) | md5 mutated | Failing | Restored |
| --- | --- | --- | --- | --- |
| M1 (first round) | `ResolveCallerDn`'s finally clearing the commands only | `69d97e10ca9af8282be59b2deb3b191d` | none of 33 - survived, as above | PASS 33 |
| M2 (first round) | `ReadGroups`' finally clearing the commands only | `549f6d16c1e72f8e0a093f0dab01f739` | 2 of 33: both group-read cases | PASS 33 |
| M1b | `ResolveCallerDn`'s finally clearing the commands only | `c117451e6ffbba80b4a688469b66dd19` | 1 of 34: `SelfService_TheCallerRead_LeavesNoRecordForTheNextRead`, "Assert.Empty() Failure ... [A record the read wrote past the callers ErrorAction.]" | PASS 34 |
| M2b | `ReadGroups`' finally clearing the commands only | `97f3228def795b9e4ae1ca63a1a3d4df` | 2 of 34: both group-read cases - the owned list starting at "CN=Travel Approvers,...", the search answering "That group was not found, or you do not have permission ..." | PASS 34 |
| M3 | both - the reads as they were | `02d5d173918b3618d383eaf3fc2d2996` | 5 of 34: all four end-to-end cases and the pin | PASS 34 |

On the fix, before the round: `DirectoryModuleImportTests`, `SelfService*`, `GroupMember*`, `ResourceDomainDenies*`,
`GroupCredentialRefusal*`, `ProtectedGroupWriteTarget*`, `GroupBulk*` 450 of 450 (before the third test).

**(3) The DHCP remote script** - in `DhcpAuthorizationPowerShellLifecycleTests`, a `RemoteSession` whose stand-in
`Invoke-Command` runs the script block it is handed in a second runspace (the remote session: `Import-Module` and
`Add-/Remove-DhcpServerInDC` as advanced functions, autoloading off) and writes that session's error records into its own
stream, a terminating error as one record:
- `ARemoteImportThatLeavesARecord_DoesNotFailTheChangeAfterIt` (authorize, deauthorize): success, "Successfully ...d DHCP
  server ...", the local import, the discovery, the `Invoke-Command`, the remote import and the remote change the calls.
- `ARemoteImportThatFails_FailsTheChange_AndTheChangeIsNotMade` (both): failed, "Failed to <op>: " and the import's error;
  the remote import the last call - the change not made.
- `TheRemoteChangesOwnError_StillFailsIt` (both): "Failed to <op>: remote change refused", psl-3's control on the remote side.

Filter `DhcpAuthorizationPowerShellLifecycleTests` (18); md5 fixed = restored `d786609b16ffd798ed6d295b6137a9b0`.

| # | Mutation | md5 mutated | Failing (of 18) | Restored |
| --- | --- | --- | --- | --- |
| 3 | the fix reverted - `DhcpAuthorizationService.cs` as at `370e32f3` | `b3fb7c14f949565e31bfcd3ce7fadf6c` | 2: both noise cases, "Failed to authorize: A record the DhcpServer import wrote past the callers ErrorAction." (and deauthorize); the failing-import and control cases pass, as they should - behaviour the fix keeps | PASS 18 |
| 3a | the record's first option on the authorize script - `Import-Module DhcpServer -ErrorAction Stop; $Error.Clear();` in place of the redirection | `8abff3f442e2f1aed7203b9444fb5474` | 1: the authorize noise case, the same failure - `$Error.Clear()` leaves the record in the stream | PASS 18 |
| 3b | the same, the deauthorize script | `3d2eb52e0a17fa6c8ffbd26cf04297f4` | 1: the deauthorize noise case | PASS 18 |

On the fix, before the round: `DhcpAuthorization*` 21 of 21.
