# aud-a26g: The attribute editor's Set-ADUser write keeps a HadErrors branch its own comment calls unreachable

**Severity**: LOW - dead code, fails closed. `ADAttributeEditorService`'s `Set-ADUser` write (around
line 1243 at `15de2fcf`) is invoked with `-ErrorAction Stop`, so an error is thrown and leaves no
record in the stream; the `if (ps.HadErrors)` branch after it cannot fire on the write's own error.
It can fire on a stale record an earlier command left (psl-2/psl-3 class), in which case a clean
write would be reported as a failure. The comment above it already says it is unreachable. Same
pattern aud-a2o removed from the Conference Rooms group write.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2o agent, 2026-10-22, candidate 3 of its hand-back)

## Evidence

At `15de2fcf`, `Services/ADAttributeEditorService.cs`, the `Set-ADUser` write in `PerformSave`:
`-ErrorAction Stop`, then `if (ps.HadErrors) ...` with a comment calling it unreachable.

## Approach (to be written by the implementing agent before code)

- Remove the branch as aud-a2o did: try/catch/finally, the thrown error is the write's error, the
  `finally` clears the command and the error stream before the next command, and the aud-a28 /
  a28c audit of a failed write stays exactly as it is. Comment says why (measured: an error
  promoted by `-ErrorAction Stop` throws and leaves zero records).
- Tests through `AttributeEditorHarness`: a stand-in `Set-ADUser` that writes a non-terminating
  error (its own `-ErrorAction Continue`) and applies the change is not taken for a failed write; a
  thrown error is still the audited failure (aud-a28c). Mutation at the call site: restore the
  branch.

## Guard proof

Pending.
