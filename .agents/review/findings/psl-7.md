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
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; in progress (plan below)
**Branch**: `worktree-agent-aa41f72251493711c`
**Commit**: -
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

## Guard proof

Pending.
