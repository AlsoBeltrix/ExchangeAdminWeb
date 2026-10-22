# aud-a26j: The attribute editor's Set-ADUser write is not confirmed by reading the attributes back

**Severity**: LOW - no wrong object is written. Since aud-a26g the write's success rests on the
absence of a throw; `PerformSave` does not read the attributes back after `Set-ADUser`, so a write
the directory accepted without applying (or a value a server normalised differently) is recorded as
a success with the proposed values as "new", and the Undo button offers to reverse a change that
did not happen as recorded. Known Failure Class 2; the Conference Rooms twin was fixed in aud-a2p.
**Status**: Plan written; in progress
**Branch**: `worktree-agent-a762d0851374626dc`
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a28l/a26g/a26i hand-back
(`8f3aa99d`). One commit `fix(ad-attribute-editor): ... (aud-a26j)`, second of two on this branch.
Behaviour: AD Attribute Editor (module version; the supervisor owns the bump). Evidence re-read at
`762c774c`, `Services/ADAttributeEditorService.cs` PerformSave 1325-1459: the re-read before the write, by DN
(1355-1360), returns the object whose objectGUID is checked against the lookup's (1373-1380); the write
`Set-ADUser -Identity <DN> -Credential -ErrorAction Stop [-Replace] [-Clear]` (1418-1424) has no `-Server`
and runs in try/catch/finally (1432-1454, aud-a26g); the success record with the proposed values follows
(1456). Nothing reads the attributes back.

**Rule.** A save is done when every attribute it wrote reads back as written from the object it wrote (Known
Failure Class 2; aud-a2p's rule for the Conference Rooms twin). No retry: the operator saves again.

1. **The read-back.** After the write's finally, `ConfirmWrite` reads and compares, returning the failure
   message or the changes as read:
   - `Get-ADUser -Identity <objectGUID> -Properties <the written attribute names> -Credential -ErrorAction
     Stop`, with no `-Server`: the write sends none, so the read goes where the write went - the module's
     default for this session; the brief's "on the same -Server". The editor routes no call: its lookup, its
     re-read and its write are all unrouted. By the objectGUID the re-read returned for the DN written, so it
     reads that object and no other. In the shape of aud-a2p's read: a throw, an error record (HadErrors), or
     anything but exactly one object with that objectGUID is a failed read; so is a re-read that carried no
     objectGUID.
   - `UnconfirmedWrites(changes, read)`, internal static and pure: each change's NewValue (null for a clear)
     against the value read, ordinal. Nothing is trimmed, because the write trims nothing (the brief's "trim as
     the write trims"). "" and absent are equal; a one-element collection reads as its element, several are
     joined - aud-a2p's ReadDirectoryString, as a sibling here because ConferenceRoomService's is private and
     that file is not edited. Returns each difference - `<name> (<label>) is '<read>'` or `is not set`, then
     `, not '<written>' as written` or `, not cleared as written` - and the changes with NewValue the value read.
   - Not confirmed: "Set-ADUser on <upn> was not confirmed: read back by objectGUID <guid>, <each>." The read
     failed: "Set-ADUser on <upn> was sent but could not be confirmed: reading <names> back by objectGUID
     <guid> failed: <error>". Each logs a warning and is recorded as `LogAudit(target, [], ..., false,
     <message>)` - a failed ADAttributeEditor_Update with `targetObjectGuid` and no old/new pair, the shape of
     the pre-write failures - so no Undo is offered (CanUndo needs Success and a pair). SaveAsync announces it,
     as it announces every result, as a failure.
   - Confirmed: the success record and the result carry the changes as read. Under the ordinal rule a
     confirmed value is the value sent; taken from the read, the record states what the directory holds rather
     than what was proposed.
   - A server that stores a value in another form - a DN in its canonical case, a Boolean's or an integer's own
     text - is not confirmed, and the failed record names the value read and the value written: loud, never a
     false success. Recorded as a known gap.
2. **The write's finally becomes load-bearing**: the read runs on the same pipeline, so the write must not stay
   queued in front of it, and a record the write left past -ErrorAction Stop must not fail the read's error
   check (psl-2). No code change; its comment, which says nothing runs after the write, is corrected after the
   round.
3. Comments after the mutation round.

Tests, through `AttributeEditorHarness`. LocalAd gains, all off by default: `SetChangesNothing` (Set-ADUser
takes the call and applies nothing), `SetStoresAs` (Set-ADUser stores each -Replace value as this returns
it), `ReadBackFailure` (a Get-ADUser after a Set-ADUser in the same session fails, terminating) and `Reads`
(each Get-ADUser's identity and -Properties). ADAttributeEditorSaveTests: (a) a write that applies - success,
a Get-ADUser by the objectGUID after the Set-ADUser asking for exactly the written attributes, the record's
and the result's new values those the directory holds; (b) one that raises nothing and applies nothing -
failed, the message naming the attribute, the value read and the value written, one Failed
ADAttributeEditor_Update with no old/new pair, the undo service's CanUndo false on it, one failure notice;
(c) a read-back that fails - failed, the read's error named, the write sent, the same record; (d) a server
that normalises the value - failed, the record's error carrying the value read and the value written; (e)
pure - ordinal, "" and absent equal, a clear against "", a one-element collection, a value not set; (f) a
harness self-test of the three knobs under -ErrorAction Stop. The existing happy-path test's exact call list
gains the read-back.

Mutations at the call site (as aud-a28m): M4 the read-back dropped - `ConfirmWrite` not called, the proposed
values recorded as before; extra E3 the write's finally no longer clearing the error stream.

Files: `Services/ADAttributeEditorService.cs`, `ExchangeAdminWeb.Tests/AttributeEditorHarness.cs`,
`ExchangeAdminWeb.Tests/ADAttributeEditorSaveTests.cs`, this record.

## Guard proof

Pending.
