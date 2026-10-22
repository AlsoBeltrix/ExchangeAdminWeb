# psl-7: Three services import the AD module without clearing the error stream, and two keep a dead HadErrors branch after a Stop import

**Severity**: LOW - latent. `GroupManagementService`, `ADAttributeEditorService` and
`ADOrganizationalUnitService` import the ActiveDirectory module without clearing the error stream
afterwards, so a record an import wrote past `-ErrorAction Stop` could be taken for a later read's
error (the psl-2/psl-3 class: a flag read for the wrong operation). `AccountLockoutRemediationService`
(around line 1047) and `ADDirectorySearchService` (around 885) keep an `if (ps.HadErrors)` branch
straight after a `-ErrorAction Stop` import, the dead pattern aud-a2o/a26g/a2r removed elsewhere -
measured: a failing import under Stop throws and leaves zero records. Also measured and worth a
comment where it matters: `PowerShell.HadErrors` reads true after an `Invoke` that threw, even with
zero records.
**Status**: Fixed on branch; not merged, not pushed; awaiting review
**Branch**: `worktree-agent-aa41f72251493711c`
**Commit**: `339da5c9` (fix, tests, record; comments `d4e69a27`; plan `9301c34b`)
**Reviewer**: implementer (surfaced by the aud-a2r agent, 2026-10-22, candidates 2 to 4 of its hand-back)

## Evidence

At `027c3309`: `git grep -n "Import-Module" Services/*.cs` and the lines after each; the two
`HadErrors` branches named above. aud-a2r's `ConferenceRoomService.ImportActiveDirectoryModule` is
the shape to copy (import under Stop; `finally` clears commands and the error stream).

## Approach (to be written by the implementing agent before code)

- One shared import helper (a static in a shared PowerShell helper class, or the per-service copy
  of aud-a2r's) used by all five services: import under `-ErrorAction Stop`, the thrown error is the
  step's error, `finally` clears commands and the error stream. Remove the two dead branches with the
  comment saying why. Where code reads `HadErrors` after a caught throw, note the measured behaviour
  or restructure so it is not read then.
- Tests: for each service, a stand-in import that writes a non-terminating record is not taken for a
  later read's error; a failing import is reported as the import's failure. Mutation at the call site:
  drop one `finally` clear; restore one dead branch. No live AD.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the aud-a2r hand-back. One commit
`fix(ad-directory): ... (psl-7)`, second of three on branch `worktree-agent-aa41f72251493711c` (after aud-a28q,
before aud-a26o). Behaviour: Group Management, AD Attribute Editor and Account Lockout Remediation (their module
versions), and two app-shared services - the OU browser (`ADOrganizationalUnitService`, Module Configuration) and
the directory autocomplete's availability probe (`ADDirectorySearchService`) - with a new shared helper: a base app
version change as well. The supervisor owns the bumps. Evidence re-read at `89096ac9`:

- `GroupManagementService`, eight imports, each `Import-Module -ErrorAction Stop`, `Invoke`, `Commands.Clear()`, the
  error stream kept, each on a runspace built inline: SearchGroupsAsync 112-121, GetGroupManagerAsync 288-297,
  GetMembersAsync 396-405, AddMemberAsync 677-686, RemoveMemberAsync 852-861, QueryBatchCandidates 959-968,
  ResolveMemberForWrite 1179-1188, ResolveGroupForWrite 1351-1360. Three take a record the import left for a later
  read's error: AddMemberAsync and RemoveMemberAsync through IsDirectMemberOf's HadErrors (1312, their first read
  after the import - "Could not read the group's membership.", thrown), and ResolveGroupForWrite's DN read (1384 -
  "AD group not found by its distinguished name"). The rest clear the stream before their first checked read (422,
  977, 1428) or check none.
- `ADAttributeEditorService`: PerformLookup 1182-1184 - with no search base the read's HadErrors (1206) returns the
  import's record as the lookup's error; with bases, the first base's (1234) reports that base failed with it.
  PerformSave 1367-1369 (the write's finally clears it before the read-back's check) and ReReadCurrentValues
  1760-1762 (no check) are harmless today.
- `ADOrganizationalUnitService.EnumerateOUs` 96-100: the read's HadErrors (111) throws the import's record as the
  enumeration's failure, and GetOrganizationalUnits then hides the OU browser.
- `AccountLockoutRemediationService.CreateAdPowerShell` 1039-1057: `if (ps.HadErrors)` straight after the import
  (1047-1052). Dead for the import's own error, which under Stop is thrown from Invoke with no record (aud-a2r's
  measurement); live only for a record written past the preference, which it throws as the import's failure
  though the module loaded. The `ps.Dispose()` it holds (1050) is the only one on a failure path, so a failing
  import - the throw - leaks the PowerShell.
- `ADDirectorySearchService.ProbeAvailability` 867-901: the same branch (885-890) marks the directory unavailable on
  such a record.
- HadErrors read after a caught throw, in the five: one - ResolveGroupForWrite's routed re-read (1466-1470,
  `catch (Exception) { routed = null; } finally { ps.Commands.Clear(); }` then `if (routed is null ||
  ps.HadErrors)`). Measured below, HadErrors reads true after an Invoke that threw with no record; `routed` is null
  then anyway, so either operand refuses. Noted in a comment, not restructured.

Measured (`scratchpad/psl7-a28q-a26o-work/probe-import.ps1`, pwsh 7.6.6, an in-process runspace, stand-in functions
only, autoloading off, no module loaded, no directory): an advanced-function stand-in Import-Module that writes
`Write-Error -ErrorAction Continue` under the caller's `-ErrorAction Stop` does not throw and leaves 1 record,
HadErrors true; after a commands-only clear a clean read reads HadErrors true with the record still there - the
defect; with the stream cleared HadErrors still reads true until the next Invoke, and the next clean read reads
false with no record. A stand-in whose `Write-Error` is the caller's Stop throws `ActionPreferenceStopException`
carrying its message and leaves 0 records; without Stop, no throw and 1 record (the real cmdlet's shape, aud-a2r).
A read that throws under Stop, caught: 0 records, HadErrors true.

**Rule.** An import's error is the one it throws, named by whatever names its caller's steps; nothing it leaves in
the stream is any later read's.

1. New `Services/ActiveDirectoryModuleImport.cs`: `internal static class ActiveDirectoryModuleImport`,
   `Import(PowerShell ps)` - aud-a2r's shape, `Import-Module -Name ActiveDirectory -ErrorAction Stop` in
   `try { ps.Invoke(); } finally { ps.Commands.Clear(); ps.Streams.Error.Clear(); }`. One helper rather than
   fourteen copies. `ConferenceRoomService.ImportActiveDirectoryModule` keeps its private copy: that file is outside
   this brief; pointing it here is a follow-up.
2. The helper replaces the import's lines at all fourteen sites above (eight, three, and one in each of the other
   three). The two dead branches go: CreateAdPowerShell becomes the helper inside a try whose catch disposes the
   PowerShell and rethrows - the dispose the dead branch meant to make - and ProbeAvailability sets the directory
   available when the helper returns, its catch unchanged.
3. The routed re-read: a comment saying what was measured; the code unchanged.
4. Test seams; production builds exactly the session it builds today. GroupManagementService gains
   `internal virtual Runspace CreateDirectoryRunspace()` - ADAttributeEditorService's shape - for its eight
   runspaces. `ADOrganizationalUnitService` and `ADDirectorySearchService` are sealed, so each gains an
   `internal Func<Runspace> ... { get; init; }` (the shape of `AdSiteDirectory.WorkerScheduler`): the OU
   enumeration's runspace, the probe's runspace. CreateAdPowerShell takes an optional runspace and becomes internal
   with its `Invoke` helper.
5. Comments after the mutation round, checked code-identical: why each import has no HadErrors check, and the
   measured note at the routed re-read.

Tests. New `ExchangeAdminWeb.Tests/DirectoryModuleImportTests.cs`, over a stand-in session (Import-Module and each
read an advanced function, autoloading off, every call noted); `AttributeEditorHarness`'s LocalAd gains
`ImportReportsErrorWithoutThrowing` (off by default). No directory, module or network.
- the helper: an import that reports a record without throwing leaves none and nothing queued, and the next read's
  HadErrors is false; a failing import throws its own error under Stop and leaves nothing queued;
- per service, a stand-in import writing a non-terminating record is not taken for a later read's error, and a
  failing import is the import's failure: GroupManagementService.ResolveGroupForWrite by DN (resolved; the
  import's error thrown, no read sent); ADAttributeEditorService.LookupAsync with no search base and with one
  (found, no base reported failed; the import's error thrown, no read sent); GetOrganizationalUnits (the OUs, and
  available; empty, unavailable, the warning carrying the import's error); CreateAdPowerShell then its Invoke (the
  read's answer; the import's error thrown); ADDirectorySearchService.IsAvailable (true; false, the warning
  carrying the import's error).

Mutations at the call site (md5 before and after, the named failing test, restored, md5 matched, touched, rebuilt,
green): M3 the helper's finally without its error-stream clear; M4 CreateAdPowerShell's dead branch restored (its
import as at `89096ac9`); extras E2 ProbeAvailability's dead branch restored, E3 `-ErrorAction Stop` dropped from
the helper. No mutation reverts a test seam: a reverted seam opens a real session and imports the real module, a
directory probe the owner forbids.

Files: `Services/ActiveDirectoryModuleImport.cs` (new), `Services/GroupManagementService.cs`,
`Services/ADAttributeEditorService.cs`, `Services/ADOrganizationalUnitService.cs`,
`Services/AccountLockoutRemediationService.cs`, `Services/ADDirectorySearchService.cs`,
`ExchangeAdminWeb.Tests/AttributeEditorHarness.cs`, the new test file, this record.

## Fix

- New `Services/ActiveDirectoryModuleImport.cs`: `internal static class ActiveDirectoryModuleImport`, `Import(PowerShell
  ps)` - `Import-Module -Name ActiveDirectory -ErrorAction Stop` in `try { ps.Invoke(); } finally {
  ps.Commands.Clear(); ps.Streams.Error.Clear(); }`.
- `Services/GroupManagementService.cs`: its eight imports are `ActiveDirectoryModuleImport.Import(ps)`, and its eight
  runspaces `CreateDirectoryRunspace()` - new, `internal virtual`, the session each built inline (`CreateDefault`,
  `Bypass`), beside the `GetCredentialsAsync` seam.
- `Services/ADAttributeEditorService.cs`: PerformLookup's, PerformSave's and ReReadCurrentValues' imports are the
  helper.
- `Services/ADOrganizationalUnitService.cs`: EnumerateOUs' import is the helper, on a runspace from the new
  `internal Func<Runspace> CreateDirectoryRunspace { get; init; }` (default: the session it built inline).
- `Services/AccountLockoutRemediationService.cs`: `CreateAdPowerShell(Runspace? runspace = null)`, now internal, is
  the helper in a try whose catch disposes the PowerShell and rethrows; its dead `HadErrors` branch and the clears
  after it are gone (the helper's finally clears). `Invoke(PowerShell)` is internal. The three callers are unchanged.
- `Services/ADDirectorySearchService.cs`: ProbeAvailability's import is the helper, on a runspace from the new
  `internal Func<Runspace> CreateProbeRunspace { get; init; }`; its dead `HadErrors` branch (and its warning "could
  not be loaded") is gone, so the directory is available when the import returns and the catch reports any throw,
  as before.
- `ExchangeAdminWeb.Tests/AttributeEditorHarness.cs` LocalAd: `ImportReportsErrorWithoutThrowing`, off by default -
  the stand-in Import-Module writes it with its own `-ErrorAction Continue` after loading.
- The routed re-read's `HadErrors` after a caught throw (plan item 3) is a comment, in the comments commit.

Tests: new `ExchangeAdminWeb.Tests/DirectoryModuleImportTests.cs`, 15 cases - the stand-ins' own behaviour
(`TheStandIns_ReportOrFailAsTheTestsBelowRelyOn`) and the harness knob's
(`TheHarness_ImportReportsAnErrorWithoutThrowing_UnderStop`); the helper
(`TheImport_ThatReportsAnErrorWithoutThrowing_LeavesNoRecordAndNothingQueued`,
`TheImport_ThatFails_ThrowsItsOwnError_AndLeavesNothingQueued`); and per service a reporting import that is not
taken for a later read's error and a failing import that is the import's failure:
`GroupManagement_AnImportThatReportsAnError_IsNotTakenForTheGroupReadsError`,
`GroupManagement_AFailingImport_IsTheImportsFailure_AndNoReadIsSent`,
`AttributeEditor_AnImportThatReportsAnError_IsNotTakenForTheLookupsError`,
`AttributeEditor_AnImportThatReportsAnError_IsNotTakenForTheFirstSearchBasesError`,
`AttributeEditor_AFailingImport_IsTheLookupsFailure_AndNoReadIsSent`,
`OrganizationalUnits_AnImportThatReportsAnError_IsNotTakenForTheEnumerationsError`,
`OrganizationalUnits_AFailingImport_IsTheImportsFailure`,
`AccountLockout_AnImportThatReportsAnError_IsNeitherTheImportsFailureNorTheReadsError`,
`AccountLockout_AFailingImport_IsTheImportsFailure`, `DirectorySearch_AnImportThatReportsAnError_LeavesTheDirectoryAvailable`,
`DirectorySearch_AFailingImport_IsTheImportsFailure`.

Caller-visible change, by design: an error the ActiveDirectory module's import reports without throwing no longer
fails anything - not a Group Management add, remove or write-target resolution by DN, not an attribute lookup (or
one of its search bases), not the OU browser, not Account Lockout Remediation's directory steps, not the
autocomplete's availability. An import that throws fails as before, with its own error. No outcome the real cmdlet
is known to produce changes (*assumption, not probed*, aud-a2r's: the real ActiveDirectory module's import writes no
error past `-ErrorAction Stop`). A failing import no longer leaks Account Lockout Remediation's PowerShell.

## Guard proof

No separate pre-fix reproduction: M3 puts back the defect of the three services that kept the import's record (their
imports, as at `89096ac9`, cleared the commands only), and M4 and E2 put back the two dead branches as at `89096ac9`,
each at its call site; each fails the tests named.

Same driver and conditions as aud-a28q (`round-psl7.ps1`, log `psl7-mutations.log`), the filter
`FullyQualifiedName~DirectoryModuleImportTests|FullyQualifiedName~ADAttributeEditorSaveTests|FullyQualifiedName~ADAttributeEditorLookupTests|FullyQualifiedName~GroupMemberListingTests|FullyQualifiedName~GroupSearchForestScopeTests`
(108 tests): stand-in sessions and source scans only, so no mutant could reach a real session. Every mutant built
with 0 errors; each restore matched its original MD5. After the round, a rebuild on the restored bytes and the
filter: 108/108 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M3 the helper's finally without its error-stream clear (the commands still cleared) | `ActiveDirectoryModuleImport.cs`, `5CD2122FBB24112D59A05985DA15DBB3` | `8F27B08464DA073677C149673DFE0686` | 6 of 108: `TheImport_ThatReportsAnErrorWithoutThrowing_...` (the record left), and the reporting import taken for a later read's error in four services - `GroupManagement_AnImportThatReportsAnError_...` (refused "not found by its distinguished name"), both `AttributeEditor_AnImportThatReportsAnError_...` (the lookup failed; the first base reported failed), `OrganizationalUnits_AnImportThatReportsAnError_...` (unavailable), `AccountLockout_AnImportThatReportsAnError_...` (the read threw). The directory search passes: nothing reads after its probe's import |
| M4 CreateAdPowerShell's dead branch restored (its import as at `89096ac9`, inside the new signature) | `AccountLockoutRemediationService.cs`, `D0A6EABB4AD86BD9A6FA88AA41F01AA3` | `880E704CA7DD48C5778442B5967BB5D7` | 1 of 108: `AccountLockout_AnImportThatReportsAnError_IsNeitherTheImportsFailureNorTheReadsError` - the record thrown as the import's failure though the module loaded |
| extra E2 ProbeAvailability's dead branch restored (as at `89096ac9`) | `ADDirectorySearchService.cs`, `F6839DF8D0F0484C23389B37A7E7124B` | `AA38C9E2EB787BA02580B9D60E5CA9F5` | 1 of 108: `DirectorySearch_AnImportThatReportsAnError_LeavesTheDirectoryAvailable` - unavailable |
| extra E3 `-ErrorAction Stop` dropped from the helper | `ActiveDirectoryModuleImport.cs`, `5CD2122FBB24112D59A05985DA15DBB3` | `CE47432645A4EE8E344937610496B55E` | 5 of 108: the failing import, a record cleared unread instead of a throw, in `TheImport_ThatFails_...` and the GroupManagement, OrganizationalUnits, AccountLockout and DirectorySearch failing-import tests. `AttributeEditor_AFailingImport_...` passes: the harness's `ImportFailure` is a ThrowTerminatingError, which throws whatever the preference (as aud-a2q's knob did for aud-a2r) |

Not run, by design: no mutation reverted a test seam - a reverted seam opens a real session and imports the real
module, a directory probe the owner forbids.

Unchanged through the round: `DirectoryModuleImportTests.cs` `3F1D33FB30A4C6867FBA74D6D93A09C6`,
`AttributeEditorHarness.cs` `332B435BB3CF83161BCD08E45A6836E8`, `GroupManagementService.cs`
`22302309CB1AFD6F044F9741F61ED30C`, `ADAttributeEditorService.cs` `139EBCF5DDB8671337BADEAFD8B7A4C3`,
`ADOrganizationalUnitService.cs` `92A27FE3DB289CACB0FFBD04C7F50C8D`. The code comments are written after the round, in
their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count. One is
  in a touched file and predates this change: CS8604 at `AccountLockoutRemediationService.cs`'s `AuditLogoff` call
  (line 525 at `89096ac9`, 526 after the added `using`), not a changed line.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug): exit 0, 5,593 passed / 3 skipped
  (the three known AD skips) / 0 failed, 5,596 total, 5m08s; nothing hung, no Sequence file. aud-a28q's tree: 5,578 /
  3 / 0, 5,581 total; the fifteen new cases are this record's. Before the round, the related suites
  (`Group`, `ADAttributeEditor`, `AccountLockout`, `OrganizationalUnit`, `ModuleConfig`, `Allowlist`, `Undo`,
  `EventLog`, `PageAuthorizationRecheck`, the directory search excluded) in Release: 1,085/1,085.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No PowerShell source changed (no analyzer or Pester run). No real module, directory, Delinea, SMTP, push or
deployment: every new session is a stand-in. The existing `ADDirectorySearchServiceTests` in the full suite load the
real module through the production probe, as they did before (the three known skips); no new test does. No coder
self-review verdict is claimed.

## Comments after the round

Written after all three rounds, in their own commit (`d4e69a27`), checked comment-only as aud-a28q's record says
(113 lines over eleven files). This record's files: `Services/ActiveDirectoryModuleImport.cs`
`5CD2122FBB24112D59A05985DA15DBB3` -> `7F7BA25B6F7E724E4A559B9EA71D1826` (16 lines: why the throw is the import's
error, why Stop is load-bearing, why the finally clears the error stream, the measurement and the assumption);
`Services/GroupManagementService.cs` `22302309CB1AFD6F044F9741F61ED30C` -> `5DAFE973E098247946453B5FA63CE33E` (10: the
seam, and the measured note at the routed re-read's HadErrors after a caught throw);
`Services/AccountLockoutRemediationService.cs` `D0A6EABB4AD86BD9A6FA88AA41F01AA3` ->
`F10DC84D13D7E68CE4735EC6DF7ECCC5` (5: why the dead branch went, why the catch disposes, the seam);
`Services/ADDirectorySearchService.cs` `F6839DF8D0F0484C23389B37A7E7124B` -> `BFC67EE19836F92B3E29749C6258ED5F` (6: the
seam, and why no HadErrors check follows the import); `Services/ADOrganizationalUnitService.cs`
`92A27FE3DB289CACB0FFBD04C7F50C8D` -> `5C1FA4C885A8D1EF5C01CA863F8B9058` (10: the seam, and why the read's own check
now sees only the read's record); and two lines in `Services/ADAttributeEditorService.cs` at the lookup's read
check, the same note (that file's MD5s are in aud-a26o's record). The tests and the harness carry their comments
from their own commit.

## Branch-tip gates - 2026-10-22 (`d4e69a27`: the three fixes and the comments)

As aud-a28q's record: Release build 0 errors / 61 warnings (the one in this record's files the pre-existing CS8604
above); the full suite 5,604 / 3 / 0, 5,607 total, 5m23s, the fifteen cases here among the branch's fifty-two;
`ClickGate*` 316/316 and `ProgressRegistryTests` 35/35 alone; format, `git diff --check HEAD` and
`tools/Test-AsciiOnly.ps1` exit 0.

## Closeout

Fixed on branch `worktree-agent-aa41f72251493711c`, not merged, not pushed. Module-scoped behaviour changes to Group
Management, AD Attribute Editor and Account Lockout Remediation (an import's reported, unthrown error no longer
fails them; a failing lockout import no longer leaks), so their module versions are due; and shared code - the new
`ActiveDirectoryModuleImport`, the Module Configuration page's OU browser (`ADOrganizationalUnitService`) and the
directory autocomplete's availability probe (`ADDirectorySearchService`) - so the base app version as well. The bumps
are the supervisor's, at merge: `ExchangeAdminWeb.csproj` and `Modules/ModuleCatalog.cs` were outside this brief.

## Known gaps

- `ConferenceRoomService.ImportActiveDirectoryModule` is aud-a2r's private copy of the helper's shape, left where it
  is: that file was outside this brief. Pointing it at `ActiveDirectoryModuleImport` is a follow-up.
- Other services import the module inline and are outside the five this record names - `Comms10kService`,
  `EmergencyDisableService`, `LicensingUpdatesService` (psl-2 cleared its imports), `ProtectedPrincipalService`
  (outside this brief) and the DHCP and Exchange imports. Whether each leaves a record a later check reads was not
  audited here; a candidate.
- *Assumption, not probed* (aud-a2r's): the real ActiveDirectory module's import writes no error past
  `-ErrorAction Stop`. No test imports the real module; the existing `ADDirectorySearchServiceTests` do, through the
  production probe, and could use the new `CreateProbeRunspace` seam instead - a candidate.
