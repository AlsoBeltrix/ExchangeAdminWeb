# aud-a26b: notification dispatch for attribute edits belongs to the page, not the operation

**Severity**: MEDIUM. Not a live defect after `aud-a26` - both known callers now notify. It
is the reason `aud-a26` existed, and it is still there.
**Status**: Recorded follow-up; not approved
**Branch**: --
**Commit**: --

## Evidence

`ADAttributeEditorService.SaveAsync` performs the write and writes the audit record. It does
not notify. Notification is sent by `Components/Pages/ADAttributeEditor.razor`, at three
points around its own call to `SaveAsync`.

So the rule is "callers must remember", and the undo service did not - which was `aud-a26`.
That fix added a second remembering caller rather than removing the requirement.

## Predicted observable failure

A third caller of `SaveAsync` - a bulk job, a scheduled remediation, an API surface - writes
to the directory and notifies nobody, and nothing in the type system, the tests or the
review checklist asks whether it should have.

## Approach (proposed, not approved)

Move dispatch into `SaveAsync`, which already has the applied changes, the target, the
operator, the ip and the ticket, and remove the page's success and failure calls. The page
keeps its outermost catch, which notifies on an exception `SaveAsync` never sees.

The risk is in the removal, not the addition: a missed call site is a DUPLICATE notification,
which is noisy but harmless, while removing one too many is silent. Both call sites and the
undo service's would need a guard before the move, not after.

`aud-a27` did something adjacent for Emergency Disable but is not a precedent for this - it
changed a recipient and a claim, and left dispatch where it was.

## Known gaps

- Scope is this one service. Whether other modules put dispatch in the page was not
  surveyed, and the same shape is likely elsewhere.
