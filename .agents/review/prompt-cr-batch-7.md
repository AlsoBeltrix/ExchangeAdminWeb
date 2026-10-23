# Defect review request: the room list a Room Finder row writes into is checked against Protected Group Targets (aud-a2e)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `3e43b35b` or later.
- The change is the branch merged at `3e43b35b`: one commit `ebd66b91` forked from `eb017102`. Review
  `git diff eb017102 ebd66b91 -- . ':!.agents'` and read the COMBINED `Services/ConferenceRoomService.cs` (room-list
  resolution, preflight, `AddToRoomListAsync`, the cloud add, the on-prem fallback and its group re-read; the new
  write-target check call sites), `Services/ConferenceRoomProtectionGate.cs`, `Services/Jobs/ConferenceRoomBulkProcessor.cs`
  and `IConferenceRoomBulkOperations.cs` (bulk rows pass `actingUser: null`), `Models/ConferenceRoomModels.cs`,
  `Components/Pages/ConferenceRooms.razor` (two lines edited in place), `Services/ProtectedPrincipalServicing.cs`
  (`ForWriteTarget`) and `Services/ProtectedPrincipalService.cs` (`CheckWriteTarget`) which the change calls, and the tests
  `ConferenceRoomListWriteTargetTests.cs` (new), `ConferenceRoomRoomListPreflightTests.cs`, `ConferenceRoomWriteIdentityTests.cs`,
  `ConferenceRoomDirectoryAuditTests.cs`, `ConferenceRoomBulkProcessorTests.cs`, `ExchangeWriteHarness.cs`.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command per
  call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a2e.md`; the shape it follows: `aud-a2b.md`, `aud-a2c.md`, `aud-a2d.md` (the checked
  object is provably the written one), `aud-a2f.md`, `aud-a2h.md`, `aud-a2p.md`. ALREADY admitted and out of scope - do not
  re-raise: `aud-a2z` (the re-read does not compare `mail`), `aud-a2aa` (every Ambiguous result says "multiple AD users"),
  `aud-a2y` (Type-path read unlogged; credential wording; two refusals lacking the suffix), and the open owner question
  whether cloud-only principals can be protected at all (Constitution line 94 vs `ProtectedPrincipalEntryValidator.cs:90`).
  Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3 - fail-closed, the checked object is
  the written one) and Architectural Invariant 7; `docs/ProjectConstitution.md` (protected principals, audit, servicer).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat ebd66b91`. Report whether both succeeded.

## Mandate

Hunt for observable defects. Identity binding: for each of the three list shapes (synced list via its on-prem group,
cloud-only list as Exchange reports it, a list the row creates) name exactly which identifiers the check receives and
whether the object later written is bound by the same identifiers - any path where the check runs on a name or address
and the write runs on a different object; the TOCTOU between the second check and the add (what is re-read, what is
not); the `ForWriteTarget` call: is the result consulted before every mutating command on every path (cloud add, on-prem
`Set-ADGroup`/`Add-ADGroupMember`, list creation, legacy-name list), and is there any early return, retry or fallback that
reaches a write with the check skipped or with a stale decision; fail-closed: no credential, a thrown directory read, an
`Ambiguous` or `NotFound` resolution of the list - each must refuse, never fall through; the servicer exemption: page-only,
bulk passes `actingUser: null` - confirm the bulk processor cannot be given an acting user by any caller, and that the
servicer note reaches the audit `extra` exactly once; the whole-row refusal: is `Set-Place` really unreachable after a
refused list on every path (the record claims "Nothing was written."), including the row that creates the list; the new
sAMAccountName requirement on a synced group read-back - which cmdlet, which property set, and whether a group returned
without it is a plausible production shape (the agent flagged this as a first-dev-check risk); the "already on the list"
asymmetry (a protected cloud list refuses a no-op row; a protected synced list does not) - is either side an
authorization hole or an audit-evidence loss; bulk aggregation (Known Failure Class 2): a refused list row inside a batch
must be a per-row failure, never a blanket result; the page's two in-place edits - any ClickGate/Progress registry entry
whose line text changed; and tests that pass for the wrong reason (a stand-in that always returns Allowed, a mutation set
that never removed the second check).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.