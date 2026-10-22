# Defect review request: throw-free audit and trace writers (aud-a28q), one AD import helper across five services (psl-7), attribute editor preview, change detection and null handling (aud-a26o)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `900071b8` or later.
- The change is the branch merged at `5e2b80f3`: `9301c34b` (plans), `1edeb40d` (aud-a28q), `339da5c9` (psl-7),
  `e4fa78ad` (aud-a26o), `d4e69a27` (comments only), `7c2bc4e5` (record SHAs), forked from `89096ac9`. Review
  `git diff 89096ac9 7c2bc4e5`, and read the COMBINED `Services/JsonlLogService.cs` (`WriteToFile`, `TextField`),
  `Services/OperationTraceService.cs` and `Services/AuditService.cs` (`SamName`), `Services/ActiveDirectoryModuleImport.cs`
  (new), `Services/GroupManagementService.cs` (`CreateDirectoryRunspace`, the eight import sites),
  `Services/ADAttributeEditorService.cs` (`PerformLookup`, `PerformSave`, `ReReadCurrentValues`, `ComputeChanges`,
  `ReadDirectoryValue`, the several-values refusal), `Services/ADAttributeEditorUndoService.cs` (`GetString`),
  `Services/ADOrganizationalUnitService.cs`, `Services/ADDirectorySearchService.cs` (`ProbeAvailability`,
  `CreateProbeRunspace`), `Services/AccountLockoutRemediationService.cs` (`CreateAdPowerShell`),
  `Components/Pages/AdminEventLog.razor` (`ShowUndoPreview` guards) on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a28q.md`, `psl-7.md`, `aud-a26o.md`; parents `aud-a28f.md`, `aud-a2r.md`,
  `aud-a2o.md`, `aud-a26g.md`, `aud-a26l.md`, `aud-a28o.md`, `psl-2.md`, `psl-3.md`. ALREADY admitted and out of
  scope: `psl-8` (the Conference Rooms private import copy; the remaining inline imports not audited; the live-AD
  probe in the test suite), `aud-a28u` (the "AuditWritten" trace step reads Success when the writer dropped the
  record), `aud-a26r` (the lookup still reads with ToString; a binary attribute's misleading refusal; a closed
  loading preview blocks the next). Do not re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md`
  "Known Failure Classes" (1, 2, 3); `docs/ProjectConstitution.md` ("Audit failure must not make an
  already-completed operation appear to have failed").

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 5e2b80f3`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a28q: the writer's catch (one error, action and category only - can `TextField`
itself throw; can the logger call throw); `SamName` null to "unknown" (any caller that relied on the throw);
the tripwire's argument builder (a parameter type it does not know fails the theory - good - but does the
theory actually reach every writer path, including `OperationScope.Complete`/`Dispose`). psl-7: the helper's
`finally` clearing commands AND the error stream after an import (any caller that queued a command before
the import and expected it to survive; any caller that read an import's non-terminating record on purpose);
the removed `CreateAdPowerShell` dead branch and the dispose-on-throw it now does; the init-only runspace
seams on sealed services (production path unchanged?); the comment at the routed re-read reading
`HadErrors` after a caught throw. a26o: the preview applied only while `ReferenceEquals(undoTargetEvent,
evt)`; `ComputeChanges` with the aud-a26l syntax rules (a form-only edit is no change - does the audit then
record nothing where the operator expected a change; does the no-change path announce); `GetString` null
for JSON null/undefined (any consumer that treated "null" text as a value); the preview re-read routed and
through the credential seam; the several-values refusal (a one-element collection; `byte[]` - out of scope
as a26r). Tests passing for the wrong reason: the harness `ImportReportsErrorWithoutThrowing` knob; the
stand-in sessions; the held stand-in handler in `EventLogUndoRefusalTests`.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
