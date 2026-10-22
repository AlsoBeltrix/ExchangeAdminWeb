# Defect review request: the room-list group found through the forest global catalog and written through the member attribute (aud-a2j)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `9e2b1fec` or later.
- The change is the branch merged at `04779a54`: `618f33c9` (plan), `7d863547` (fix and tests),
  `62b4a39f` (comments only), forked from `68c97ede`. Review `git diff 68c97ede 62b4a39f`, and read
  the COMBINED `Services/ConferenceRoomService.cs` on master (`ResolveAdGroupGuid`,
  `ResolveForestGlobalCatalog`, `AddToRoomListViaAdAsync`, the a2f preflight and the a2i membership
  read), plus `Services/GroupManagementService.cs` (`ServerFromDn`, `BuildMemberAttributeWrite`,
  `ResolveSearchGlobalCatalog`) and `Services/PowerShellProtectedGroupDirectory.cs` (`GlobalCatalog`),
  the two patterns the fix copies or reuses.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a2j.md` (plan, guard proof, the write-form rationale);
  parents `aud-a2f.md`, `aud-a2g.md`, `aud-a2i.md`; `docs/ConferenceRooms-OnPremRoomListAdd-Plan.md`
  (step 3 still names the old cmdlets - the record says it needs an amendment pointer);
  `docs/GroupMemberNesting-Plan.md` (the 2026-09-03 live failure the write form answers). Repo
  rules: `AGENTS.md`; `.agents/repo-guidance.md` Architectural Invariant 7 and "Known Failure
  Classes" (1, 2, 3); `docs/ProjectConstitution.md`.
- Two further candidates are ALREADY admitted from the implementer's own hand-back and are out of
  scope here: `aud-a2k` (the group is matched by display name, not by the list's address) and
  `aud-a2l` (no read-back after the write). Do not re-raise them; do look for anything else.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 04779a54`. Report whether
both succeeded.

## Mandate

Hunt for observable defects, the write form first: `Set-ADGroup -Identity <objectGUID> -Add
@{member=<room DN>} -Server <group's DC>` - does `-Identity` by GUID resolve on that server; is the
room's DN the one proven by the protection gate's objectGUID read, and can it be stale (a moved room
object between the read and the write); what happens when the room is already a member (the a2i
check decides before, but is the write idempotent if a race adds it meanwhile - does `-Add` of an
existing DN error, and is that error treated as success or failure); the right required versus
`Add-ADGroupMember`. The catalog: `Get-ADForest -Credential` under the AD module credential - which
forest does it answer for (the credential's, the host's), and does that match Invariant 7; a forest
with several GCs where `Select-Object -First 1` picks an unreachable one (refusal, not fallback -
is that the right trade); the `:3268` endpoint shape; the catalog search's `-Credential`. The
re-read by DN: `ServerFromDn` on a DN whose domain component is not a DNS name the host can reach;
the objectGUID equality check. Exact-one-match across the forest: a dir-synced list whose group
has a same-named sibling in another domain is now refused where it was previously added - is that
acceptable and is the message actionable. Tests passing for the wrong reason: the stand-in
`Get-ADGroup` models `-Server` reachability by rule - does any rule flatter the code.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
