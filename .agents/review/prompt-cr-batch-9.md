# Defect review request: room type by name, Conference Rooms job-control re-authorization, Admin Bulk Jobs cancel audit, refusal rows, directory search kinds proven (kd-28, kd-27, kd-26, kd-29, kd-32, psl-11)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `c3af5bd4` or later.
- The change is the range merged at `c3af5bd4`: commits `d4abb472` (kd-28), `c2d8f761` (kd-27), `d7c15d5f` (kd-26), `48ea1d4c` (kd-29),
  `654ff843` (kd-32), `4c7a0bbc` (psl-11), on top of `f7b794cb`; the supervisor resolved `Modules/ModuleCatalog.cs` (Conference Rooms
  2.12.0). Review `git diff f7b794cb 4c7a0bbc -- . ':!.agents'` and read the COMBINED `Services/ConferenceRoomService.cs` and
  `Services/Jobs/ConferenceRoomBulkProcessor.cs` (`RoomTypeNames.TryParse` at the three parse sites), `Components/Pages/ConferenceRooms.razor`
  (`CancelJob` now async and `RemoveJob` re-checking access at the click with an audited denial; helpers on blank lines 838 and 946;
  the invalid-type refusal row written directly at about 1401), `Components/Pages/AdminBulkJobs.razor` (cancel audited as cancelled /
  not cancelled / refused / failed; the `StillAuthorized` refusal row "Authorization denied." with refusal fields; the kd-32 no-change
  row), `Services/ADDirectorySearchService.cs` (new `CreateSearchRunspace` seam), `ExchangeAdminWeb.Tests/ADDirectoryValidateExistsTests.cs`
  and the other tests in the diff. Note master merged other Conference Rooms work in between (kd-7 `aed153c9`, kd-23/24 `2554e7b6`);
  review this range's diff but read the combined files.
- You may read any file and run read-only git commands. One command per call. No writes, no builds, no network, no directory.
- Records: `.agents/review/findings/kd-28.md`, `kd-27.md`, `kd-26.md`, `kd-29.md`, `kd-32.md`, `psl-11.md`; parents `kd-5.md`,
  `psl-10.md`, `aud-a2y.md`. ALREADY admitted and out of scope - do not re-raise: `kd-52` (ReauthorizeAsync denial on the four write
  handlers writes no audit row), `kd-53` (Admin Bulk Jobs' fixed ConferenceRooms category), `kd-54` (a no-op removal recorded with no
  reason), `kd-55` (ValidateExists untested on the stand-in), psl-10 (5). Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` Known
  Failure Classes (1, 2, 3); `docs/ProjectConstitution.md` (audit, authorization).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 4c7a0bbc`. Report whether both succeeded.

## Mandate

Hunt for observable defects. kd-28: `RoomTypeNames.TryParse` - exact names only (case? trailing whitespace? the CSV's quoting), every
defined `RoomType` reachable by its name, no site still calling `Enum.TryParse`/`Enum.Parse` on the type, and the CSV preview's
source-check pin actually pins the preview path; kd-27: the re-check at the click - the same authorization source the write handlers
use, before the cancel or remove, refusal audited once with `mutationAttempted=false`, and `CancelJob` becoming async - any caller
or registry entry that assumed synchronous completion (double-click, busy flag, the registry's line-keyed entry text); kd-26/kd-32:
the four outcomes on Admin Bulk Jobs cancel and the "nothing changed" row - consistent fields with Conference Rooms' rows, no double
row, and `StillAuthorized`'s changed refusal text not breaking any other consumer; kd-29: the row written directly at 1401 - same
fields as kd-7's `AuditTypeAction(refusedAt:)` on master, and does the single-room path now write two rows (kd-7's and kd-29's) for one
refusal; psl-11: the stand-in proves each kind reaches its own command - does the production path build the same session shape, and
does the seam open any way for production to receive a test runspace; the catalog resolution - exactly one `Version` per entry and the
Conference Rooms notes 2.10.0 / 2.11.0 / 2.12.0 each matching their content; tests passing for the wrong reason.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.