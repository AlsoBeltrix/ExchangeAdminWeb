# Defect review request: the room list named by its Guid, and every on-prem write outcome settled by the read-back (aud-a2m, aud-a2n)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `be0946cd` or later.
- The change is the branch merged at `5c116ce4`: `bd961140` (plans), `8441f609` (aud-a2m),
  `e223e131` (aud-a2n), forked from `e560de5e`. Review `git diff e560de5e e223e131`, and read the
  COMBINED `Services/ConferenceRoomService.cs` on master (`FindRoomList`, `ReadListGuid`,
  `RoomListTarget`, `PreflightRoomListAsync`, `IsOnRoomListAsync`, `AddToRoomListAsync`,
  `AddToRoomListViaAdAsync`, the error-code classification, the read-back), `Models/ConferenceRoomModels.cs`
  (`DirectoryMembershipConfirmed`), `Components/Pages/ConferenceRooms.razor` and
  `Services/Jobs/ConferenceRoomBulkProcessor.cs` (the audit `extra`).
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a2m.md`, `aud-a2n.md` (with two folded-in items and an
  explicit assumption about the live AD error code); parents `aud-a2l.md`, `aud-a2k.md`, `aud-a2j.md`,
  `aud-a2i.md`, `aud-a2g.md`, `aud-a2d.md`. Already admitted and out of scope: `aud-a2o` (bulk-row
  message lacks the confirmed-membership detail; the on-prem no-op reuses the add's step label; an
  unreachable `HadErrors` branch). Do not re-raise it. Repo rules: `AGENTS.md`;
  `.agents/repo-guidance.md` Architectural Invariant 7 and "Known Failure Classes" (1, 2, 3);
  `docs/ProjectConstitution.md` audit rules.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 5c116ce4`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a2m: `ReadListGuid` on the `Get-DistributionGroup` object (which
property; `ExternalDirectoryObjectId` versus `Guid`; the "D" form; a wrapped PSObject); the
no-Guid refusal ordering; a list the row creates - `New-DistributionGroup` output handling, the
partial-after-create path, the re-run; whether any Exchange call on the list still passes the name
(the preview's `ResolveRoomListAsync` is said to be read-only - confirm); the recorded assumption
that Exchange Online accepts a group Guid as `-Identity` for `Get-/Add-DistributionGroupMember`.
a2n: the error-code set (20, 68, 1378, 8205, 8305, 8323 - any that would mis-classify a genuine
failure as a duplicate; the "code wins over text" rule; where the code is read from in the exception
chain); every write error reconciled by the read-back - a read-back that succeeds after a write that
failed for an unrelated reason (permission denied) and finds the room present because ANOTHER
actor added it (audited as confirmed-present, not written - is the detail enough); a read-back
that times out; the `DirectoryMembershipConfirmed` flag reaching the audit `extra` on page and bulk
and never `writtenDirectoryObject` on that path; the stale log text fix. Tests passing for the
wrong reason: the harness `Set-ADGroup` applier and its error model; the `AdManagementExceptionDoubles`
`ErrorCode` double versus the real `ADException` shape.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
