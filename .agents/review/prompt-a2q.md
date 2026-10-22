# Defect review request: the City/State/Country preflight proves its server, and every write outcome is settled by the read-back (aud-a2q)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `f64357ee` or later.
- The change is the branch merged at `f4de7909`: `a37754c4` (plan), `df084c68` (fix), forked from
  `657a11be`. Review `git diff 657a11be df084c68`, and read the COMBINED `Services/ConferenceRoomService.cs`
  (`PreflightSyncedAttributesViaAdAsync`, `ReadObjectThrough`, `SetSyncedAttributesViaAdAsync`,
  `AddToRoomListAsync`, `BuildRoomListAlreadyInMessage`), `Models/ConferenceRoomModels.cs`
  (`DirectoryAttributesConfirmed`), `Services/Jobs/ConferenceRoomBulkProcessor.cs`,
  `Components/Pages/ConferenceRooms.razor` (three lines edited in place) on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a2q.md` (four parts; the routed-preflight part is the MEDIUM);
  parents `aud-a2p.md`, `aud-a2o.md`, `aud-a2n.md`, `aud-a2g.md`, `aud-a2f.md`, `aud-a2d.md`. ALREADY
  admitted and out of scope: `aud-a2r` (the preflight's outer catch names no step; `Import-Module`
  `HadErrors` branches are dead; a refusal says "Nothing was written" inside a partial; the stand-ins
  do not route by domain). Do not re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md`
  Architectural Invariant 7 and "Known Failure Classes" (1, 2, 3); `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat f4de7909`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. The routed preflight: the second read by objectGUID through
`ServerFromDn(dn)` - exactly-one-object and GUID checks; a DN naming no domain falling back to the
unrouted route "explicitly, with a warning" (is a logged warning a sufficient statement under the
no-guess rule, or should the row refuse); the proven server carried into the write (can the two
diverge); the preflight's cost (two reads per row in bulk). The commit-then-throw path: holding the
write error, reading back always; `DirectoryAttributesConfirmed` on the step, the bulk row and the
audit `extra` under `attributesConfirmedPresent`, and NEVER `writtenDirectoryObject` on that path;
the success message no longer saying "were written on-prem" and the admin notification carrying it
(acceptable?). The outer catch naming the step. The cloud no-op message. Tests passing for the wrong
reason: the harness DN-routing knob (D1 in the record says only the harness self-test catches M5
when the DN knob does nothing).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
