# Defect review request: a job's audit row files under its own module, a removal that did not happen says why, ValidateExists proven on the stand-in (kd-53, kd-54, kd-55)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `595a74ba` or later.
- The change is the range merged at `595a74ba`: commits `622fe2cc` (kd-53), `e69f4c18` (kd-54), `43cd59df` (kd-55) and
  `eba9a5c3` (records), on top of `7315733c`. Review `git diff 7315733c eba9a5c3 -- . ':!.agents'` and read the COMBINED
  `Components/Pages/AdminBulkJobs.razor` (the category taken from the job's owning module; the six job fields now passing
  through `extra` under `old_` names because `LogModuleAction` has no `oldValues` parameter; a job with no module filing
  under AdminBulkJobs; `RemoveJob` reading the job back on the failure path and filing the reason with `refusedAt`
  "JobState" and `mutationAttempted` false), `Components/Pages/AdminEventLog.razor` (MessageTrace and Migration added to
  the category filter, with a reflection guard requiring an option for every `IBulkJobProcessor`'s `ModuleName`),
  `Services/Jobs/*` as called, `Services/ADDirectorySearchService.cs` and its stand-in seam, and the tests - the new
  ValidateExists cases (each kind, not-found, DN routing, ambiguity), `AuditCategoryFilingTests` and the bulk-job tests.
- You may read any file and run read-only git commands. One command per call. No writes, no builds, no network, no directory.
- Records: `.agents/review/findings/kd-53.md`, `kd-54.md`, `kd-55.md`; parents `kd-26.md`, `kd-32.md`, `psl-11.md`; the
  cr-batch-9 and cr-batch-8 results. ALREADY admitted and out of scope - do not re-raise: `kd-60` (a countdown range in
  test stand-ins), psl-10 (5). Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` Known Failure Classes (1, 2) and the
  ClickGateRegistry line-keying rule; `docs/ProjectConstitution.md` (audit).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat eba9a5c3`. Report whether both succeeded.

## Mandate

Hunt for observable defects. kd-53 is the one to press: the category now varies by job, so name anything that keys off a
job audit row's category and would change behaviour - the event log's filter and its section scoping, undo eligibility,
usage telemetry, any report or export, and any test or guard that asserted ConferenceRooms; the six fields moved from
`oldValues` into `extra` under `old_` names - does any reader (the event log's detail pane, an export, a test) look for
them under the old shape, and can an `old_` name now collide with a real `extra` key; the reflection guard requiring a
filter option per `IBulkJobProcessor.ModuleName` - what happens when a processor's name is not a valid filter value, and
does the guard fail closed; a job whose module is unknown filing under AdminBulkJobs rather than blank - is that
reachable in production and is it the right bucket. kd-54: the job is re-read on the failure path only - can the re-read
itself throw or return a third state the two reasons do not cover, is `DeleteJob`'s bool still honoured on the success
path, and does the banner now promise a reason it cannot always have. kd-55: the stand-in takes a count - confirm the
production path builds the same session and that nothing in it reads the count; the ambiguity and DN-routing cases
assert the command actually reached, not just the result. And across all three, tests that pass for the wrong reason.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.