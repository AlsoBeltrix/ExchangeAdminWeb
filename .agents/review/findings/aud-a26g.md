# aud-a26g: The attribute editor's Set-ADUser write keeps a HadErrors branch its own comment calls unreachable

**Severity**: LOW - dead code, fails closed. `ADAttributeEditorService`'s `Set-ADUser` write (around
line 1243 at `15de2fcf`) is invoked with `-ErrorAction Stop`, so an error is thrown and leaves no
record in the stream; the `if (ps.HadErrors)` branch after it cannot fire on the write's own error.
It can fire on a stale record an earlier command left (psl-2/psl-3 class), in which case a clean
write would be reported as a failure. The comment above it already says it is unreachable. Same
pattern aud-a2o removed from the Conference Rooms group write.
**Status**: Plan written; in progress
**Branch**: `worktree-agent-ad10b5ab24c8ce164`
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the aud-a2o hand-back. One
commit `fix(ad-attribute-editor): the Set-ADUser write is judged by its throw alone, as the group
writes are (aud-a26g)`, second of three on this branch. Behaviour: AD Attribute Editor (module
version; the supervisor owns the bump). Evidence re-read at `374718b3`,
`Services/ADAttributeEditorService.cs` PerformSave: Set-ADUser with `-ErrorAction Stop` (1437-1442);
`try { ps.Invoke(); } catch { the aud-a28c failure record; return }` (1449-1458); `ps.Commands.Clear()`
(1459); `if (ps.HadErrors)` taking the stream's first record as the write's error, recording it and
returning failed (1461-1466).

1. **The branch goes; the write is try/catch/finally**, the shape of aud-a2o's room-list write and
   GroupManagementService's member writes: `try { ps.Invoke(); } catch (Exception ex) { the failure
   record and result, unchanged (aud-a28c) } finally { ps.Commands.Clear(); ps.Streams.Error.Clear(); }`.
   The thrown error is the write's error. Why the branch could not fire on the write's own error: an
   error a command writes under `-ErrorAction Stop` is thrown out of Invoke and leaves no record in the
   stream (measured for aud-a2o, pwsh 7.6.6; pinned on this SDK by the harness test below). It could
   fire only on an error written past the preference - a nested command's own `-ErrorAction
   Continue`, which a compiled cmdlet such as Set-ADUser does not run (*assumption, not probed*) - or
   on a record an earlier command left (psl-2: HadErrors stays true while one remains); then a clean
   write was reported and audited as failed while the directory held the change, and a failed record
   offers no undo. With the branch gone `-ErrorAction Stop` on the write is load-bearing - a
   non-terminating refusal is seen only because it is thrown - so it is pinned (M4). The finally's
   error-stream clear has no reader today, the write being the session's last command; it keeps a
   command added after it from inheriting the write's record.
2. Comments after the mutation round.

Tests. Harness (`AttributeEditorHarness.cs`, `LocalAd`), both default off:
`SetReportsErrorWithoutThrowing` (a message) - the stand-in Set-ADUser applies the change, then writes
the message with its own `-ErrorAction Continue`, so the caller's `-ErrorAction Stop` does not throw
it; `SetFailureNonTerminating` - `SetFailure` is reported through `$PSCmdlet.WriteError` and nothing is
applied, a compiled cmdlet's non-terminating refusal, thrown only by the caller's `-ErrorAction Stop`.
ADAttributeEditorSaveTests: (a) an error the write reports without a throw is not its error - success,
the change applied once, one Success `ADAttributeEditor_Update` with its old/new values, one success
notice, the reported text nowhere in the result; (b) a refusal the write throws is the audited failure
(aud-a28c), terminating (`SetFailure`) and non-terminating under Stop (`SetFailureNonTerminating`)
alike - failed, the refusal's text in the error, nothing applied, one Failed `ADAttributeEditor_Update`
naming the change with neither refusal field (the write was tried), one failure notice. Harness
self-test: in a harness session under `-ErrorAction Stop`, Set-ADUser with the first knob does not
throw, sets HadErrors, leaves exactly one record carrying the message and applies the change; with the
second it throws and applies nothing. Conditions asserted, not wording.

Mutations at the call site (as aud-a28l): M3 the HadErrors branch restored (the lines as at
`374718b3`); extra M4 `-ErrorAction Stop` dropped from the write; extra H1 the harness's first knob
ignored - (a) then passes either way, and only the self-test stops it.

Files: `Services/ADAttributeEditorService.cs`, `ExchangeAdminWeb.Tests/AttributeEditorHarness.cs`,
`ExchangeAdminWeb.Tests/ADAttributeEditorSaveTests.cs`, this record.

## Guard proof

Pending.
