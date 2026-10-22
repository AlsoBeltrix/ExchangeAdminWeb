# Defect review request: the room-list group bound by the list's address, and the on-prem add confirmed by read-back (aud-a2k, aud-a2l)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `10a482bb` or later.
- The change is the branch merged at `28263a6c`: `b836c6d7` (plans), `3341e112` (aud-a2k),
  `a38df458` (aud-a2l), `8b5a3aff` (record SHAs), forked from `1049f839`. Review
  `git diff 1049f839 8b5a3aff`, and read the COMBINED `Services/ConferenceRoomService.cs` on master
  (`FindRoomList`, `RoomListTarget`, `ResolveAdGroupGuid`, `ResolveForestGlobalCatalog`, the a2f
  preflight, `ReadOnPremListMembership`, `AddToRoomListViaAdAsync`).
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a2k.md`, `aud-a2l.md`; parents `aud-a2j.md`, `aud-a2i.md`,
  `aud-a2f.md`, `aud-a2g.md`, `aud-a2d.md`; `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md`
  (now carrying an amendment pointer). Two follow-ups are ALREADY admitted from the implementer's
  hand-back and are out of scope: `aud-a2m` (the Exchange side of the list is re-resolved by name)
  and `aud-a2n` (duplicate detection by English text; stale log text). Do not re-raise them. Repo
  rules: `AGENTS.md`; `.agents/repo-guidance.md` Architectural Invariant 7 and "Known Failure
  Classes" (1, 2, 3); `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 28263a6c`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a2k: the address taken from `Get-DistributionGroup` - which property,
how it is normalised (SMTP: prefix, case, trailing whitespace), and whether the on-prem `mail`
comparison is case-insensitive as AD's is; a list whose primary address differs from the on-prem
group's `mail` because of a proxy-address change (is the refusal message actionable); the blank
address refusal ordering (before the catalog resolve - good - but does any caller still reach the
old name-based path, including the CSV preview); a dir-synced list that Exchange reports with no
`PrimarySmtpAddress`. a2l: the read-back - same `-Server` as the write; the DN comparison
(case, escaping, a room whose DN changed between the gate's read and the read-back); the audit when
the read-back misses (`writtenDirectoryObject` omitted; is the row's failure message distinguishable
from a write error); the duplicate-value branch being settled by the read-back (can an unrelated
error that happens to match the text be mis-settled as success when the room IS present - is that
acceptable); "every other write error stays a failure with no read-back" (should a write error with
the room present be a failure); the a2g rule tension the record names (a refused write audited as
written when the room is present). Tests passing for the wrong reason: the stand-in filter
evaluator and `Set-ADGroup` applier - any rule that flatters the code; the harness self-tests.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
