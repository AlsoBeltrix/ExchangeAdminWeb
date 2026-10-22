# Defect review request: Conference Rooms operability and the City/State/Country read-back (aud-a2o, aud-a2p)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `cdd9697e` or later.
- Two merged branches:
  - aud-a2o: `e53a2e2d` (plan), `df579d08` (fix), forked from `501ea43e`, merged at `15de2fcf`. Review
    `git diff 501ea43e df579d08`.
  - aud-a2p: `8ce77ddf` (plan), `0bbd302f` (fix), forked from `e1dc552a`, merged at `28ffde25`. Review
    `git diff e1dc552a 0bbd302f`.
  Read the COMBINED `Services/ConferenceRoomService.cs` on master (`SetSyncedAttributesViaAdAsync`,
  `ReadBackSyncedAttributes`, `UnconfirmedSyncedAttributes`, `AddToRoomListAsync`,
  `BuildOnPremRoomListNotWrittenMessage`, `AddToRoomListViaAdAsync`) and
  `Services/Jobs/ConferenceRoomBulkProcessor.cs`.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a2o.md`, `aud-a2p.md`; parents `aud-a2n.md`, `aud-a2l.md`,
  `aud-a2j.md`, `aud-a2g.md`, `aud-a2d.md`. ALREADY admitted and out of scope: `aud-a2q` (the cloud
  no-op still builds an "Added" message; the outer catch's text covers surrounding steps; a
  `Set-ADUser` that commits then throws leaves a partial row). Do not re-raise them. Repo rules:
  `AGENTS.md`; `.agents/repo-guidance.md` Architectural Invariant 7 and "Known Failure Classes"
  (1, 2, 3); `docs/ProjectConstitution.md` audit rules.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 28ffde25`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a2o: the no-op recognised from the add's two null returns (any other
path that returns the same shape and would be mislabelled); the bulk message composition (double
detail; a failed row with a confirmed detail). a2p: `-Server` added to the `Set-ADUser` write -
`ServerFromDn` on the room's DN (a DN whose domain the host cannot reach; the "default domain
controller" fallback when the DN names no domain - is that a guess); the read-back's comparison
(ordinal; trim; empty equals absent; a one-element collection; `co` versus `c`/`countryCode` not
read - can `co` match while `c` did not apply); exactly-one-object and objectGUID checks; the
try/finally versus a2o's try/catch/finally (a thrown write error reaches the outer catch - message
accurate?); `writtenDirectoryObject` only on a matched read-back; the `internal` test seam on
`AddToRoomListAsync`/`RoomListTarget`. Tests passing for the wrong reason: the harness `Set-ADUser`
applier's `-Replace` handling and its knobs.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
