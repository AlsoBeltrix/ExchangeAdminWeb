# aud-a26j: The attribute editor's Set-ADUser write is not confirmed by reading the attributes back

**Severity**: LOW - no wrong object is written. Since aud-a26g the write's success rests on the
absence of a throw; `PerformSave` does not read the attributes back after `Set-ADUser`, so a write
the directory accepted without applying (or a value a server normalised differently) is recorded as
a success with the proposed values as "new", and the Undo button offers to reverse a change that
did not happen as recorded. Known Failure Class 2; the Conference Rooms twin was fixed in aud-a2p.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28l/a26g/a26i agent, 2026-10-22, candidate 6 of its hand-back)

## Evidence

At `f0cccea8`, `Services/ADAttributeEditorService.cs` `PerformSave`: `Set-ADUser` in
try/catch/finally (aud-a26g), then the success record with the proposed values; no `Get-ADUser`
of the written attributes after the write. Compare aud-a2p's `ReadBackSyncedAttributes` /
`UnconfirmedSyncedAttributes`.

## Approach (to be written by the implementing agent before code)

- After the write, re-read the written attributes by objectGUID on the same `-Server` (the write
  already targets the bound object, aud-a2d), compare ordinal to what was sent (trim as the write
  trims; empty equals absent; a one-element collection reads as its element), and on a mismatch or a
  failed read record a failed `ADAttributeEditor_Update` naming the attribute, the value read and the
  value written (no old/new pairs, so no Undo is offered for a change not confirmed). The success
  record's new values are the values read back.
- Reuse the aud-a2p comparison helper if it can be shared without widening visibility carelessly;
  otherwise a sibling. Mutation at the call site: drop the read-back.
- Tests through `AttributeEditorHarness`: a `Set-ADUser` that applies (success, values confirmed);
  one that throws nothing and applies nothing (failed record, no undo); a read-back that fails
  (failed record); a server that normalises a value (the record carries the read value).

## Guard proof

Pending.
