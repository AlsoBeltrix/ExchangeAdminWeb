# Defect review request: a room list the row would create is refused before any write; an ambiguous resolve refuses; the re-read compares mail; one neutral ambiguity message (aud-a2ab, aud-a2ac, aud-a2z, aud-a2aa)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `3bfdc1af` or later.
- The change is the range merged at `3bfdc1af`: commits `c38defa0` (aud-a2ab, HIGH), `fefb3e6a` (aud-a2ac), `170b182f`
  (aud-a2z), `26e8cd9b` (aud-a2aa), on top of `ebd66b91`. Review `git diff ebd66b91 26e8cd9b -- . ':!.agents'` and read
  the COMBINED `Services/ConferenceRoomService.cs` (the preflight refusal for a building whose list does not exist;
  `ReadRoomList` with -ErrorAction Stop plus the base's ReadUnlessNotFound; the synced group re-read comparing `mail`;
  the neutral `AmbiguousMessage`), `Services/ExchangeServiceBase.cs`, `Services/ConferenceRoomProtectionGate.cs`, and the
  tests `ConferenceRoomListWriteTargetTests.cs`, `ConferenceRoomListLookupTests.cs`, `ConferenceRoomListGroupAddressTests.cs`,
  `ConferenceRoomWriteIdentityTests.cs`, `ExchangeWriteHarness.cs`. The supervisor resolved `Modules/ModuleCatalog.cs` to
  Conference Rooms 2.17.0 keeping both note sets, and updated three stand-in overrides of
  `GetModuleCredentialsAsync` for the cancellation parameter kd-37 added on master while this branch was forked.
- You may read any file and run read-only git commands. One command per call. No writes, no builds, no network, no directory.
- Records: `.agents/review/findings/aud-a2ab.md`, `aud-a2ac.md`, `aud-a2z.md`, `aud-a2aa.md`; parent `aud-a2e.md`; the
  cr-batch-7 result that raised a2ab and a2ac. ALREADY admitted and out of scope - do not re-raise: `kd-66` (the preview
  still marks a missing list "new"), `kd-67` (the "multiple AD users" literal in four other services), the preview's
  stray-city-list probe keeping SilentlyContinue, and the deliberate removal of list creation itself (an owner call,
  recorded). Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` Known Failure Classes (1, 2, 3) and Architectural
  Invariant 7; `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 26e8cd9b`. Report whether both succeeded.

## Mandate

Hunt for observable defects. aud-a2ab: the row now refuses before any write when the building's list does not exist -
confirm NOTHING is written on that path (no Set-Place, no attribute fix, no partial bulk row), that the refusal ends
"Nothing was written.", that it is audited once with the refusal fields, and that a bulk run files it per row rather
than failing the batch (Known Failure Class 2); the post-creation re-check the agent kept as the add's own guard - is it
now unreachable dead code, and if a list IS created out of band between preflight and add, does the row still check it;
aud-a2ac: `ReadRoomList` with -ErrorAction Stop plus ReadUnlessNotFound - which exact Exchange error shapes count as
"no such object", can a permission error or a transient failure be read as not-found (a false absence now refuses
rather than creating, so say what the operator sees), and does the legacy-name lookup follow the same rule; the second
answer throwing - a list name that legitimately returns two objects in a hybrid directory; aud-a2z: the `mail`
comparison - trimmed and case-insensitive, and what happens when the group has no `mail` at all (refuse or proceed);
aud-a2aa: the neutral message - does it still tell the operator enough to act, and is it used on every Ambiguous path in
this service; and across all four, tests passing for the wrong reason, plus the three stand-in overrides the supervisor
edited for kd-37's parameter (they must still exercise the same seam).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.