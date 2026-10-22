# aud-a2j: The room-list AD group lookup searches only the credential's default domain

**Severity**: MEDIUM - fails closed, so no wrong write, but on a multi-domain forest the on-prem
room-list add refuses every valid synced list whose group lives in another domain, and the logic
rests on the forest's shape (Architectural Invariant 7).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (batch-6, `prompt-batch-6.md`, 2026-10-22)

## Evidence

At `bc24d7d1`, `Services/ConferenceRoomService.cs`, `ResolveAdGroupGuid` (shared by the a2f
room-list preflight and the on-prem add, and the source of the DN that a2i's membership read
uses):

```
1748        ps.AddCommand("Get-ADGroup")
1749          .AddParameter("Filter", $"mail -eq '{escaped}' -or displayName -eq '{escaped}' -or name -eq '{escaped}'")
1750          .AddParameter("Credential", credential)
1751          .AddParameter("ErrorAction", "Stop");
```

No `-Server`. Without it `Get-ADGroup -Filter` is answered by a domain controller of the
credential's own domain and returns only that domain's groups. A room list synced from a group
in another domain of the forest comes back as "No on-prem AD group found", the preflight refuses
the row before any write, and the room cannot be added to a valid list.

`GroupManagementService` already solved this for its group search: it resolves the forest global
catalog once (`ResolveSearchGlobalCatalog`, `Get-ADForest ... GlobalCatalogs`, "host:3268"),
searches there, refuses ambiguity, then re-reads the chosen object by DN against
`ServerFromDn(dn)` before any write. Its comments record the incident in which a bare `":3268"`
was accepted by `Get-ADGroup` and quietly served from the local domain.

## Approach (to be written by the implementing agent before code)

- Search via the forest global catalog as `GroupManagementService` does (reuse or lift its
  resolver; the GC endpoint must be a real host, never a bare `:3268`; a failed GC resolution is
  a refusal, not a fallback to the default domain).
- Keep the exact-one-match rule; a GC search widens the match set, so ambiguity across domains
  must still refuse.
- Re-read the chosen group by DN against `ServerFromDn(groupDn)` and carry that GUID and DN into
  the a2i membership read and the add, so the preflight and the add resolve the same object.
- Tests: a group in a second domain found via the GC; the default-domain-only read would have
  missed it (mutation at the call site: drop `-Server`); cross-domain ambiguity refuses; GC
  resolution failure refuses before any write. No live AD.

## Guard proof

Pending.

## Known gaps

Dev acceptance owed with a2f/a2i: a room list whose group is in a second domain.
