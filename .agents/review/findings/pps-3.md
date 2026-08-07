# pps-3: Serviced notes computed and then discarded, losing the audit record of the override

**Severity**: HIGH -- the write is allowed and no durable record says who permitted it. This is
the exact failure the shared helper's doc comment names as "the single most repeatable mistake in
this work", and it recurred anyway in two places.

**Status**: Verified
**Branch**: -- (default-branch mode)
**Commit**: `6f7f2ac`

## Evidence

**(a) Undo execute throws the note away.**
`Services/ADAttributeEditorUndoService.cs:179-185`:

```csharp
if (protCheck.IsProtected
    && ProtectedPrincipalServicing.NoteFor(
        _servicers, actingUser, ServicerModuleId, protCheck.MatchedRules) is null)
{
    ...refuse...
}
```

`NoteFor` is evaluated inside a boolean expression and its return value is used ONLY for the null
test. When it returns a note -- the allow case -- the note is discarded. The undo proceeds and its
audit event carries nothing.

**(b) Emergency Disable keeps the note out of the audit event.**
The serviced note is recorded in the operation-trace step trail but is not passed into the
`AuditService` event. The trace is diagnostic; the audit log is the durable record, and they are
different stores with different retention and different readers.

## Predicted observable failure

An authorised servicer undoes an attribute change on a protected principal. It succeeds. The audit
log shows a successful undo against a protected user with no indication that an override was
exercised or which group authorised it. Answering "who permitted this?" afterwards is impossible
from the audit record -- which is the one question the record exists to answer.

## What

The helper was designed specifically to prevent this: `NoteFor` returns a nullable NOTE rather
than a bool so that "may they proceed" and "what should be recorded" cannot be separated. Using it
as `... is null` in a boolean condition defeats that design exactly -- it extracts the permission
and drops the record, which is the shape the nullable return was chosen to make awkward.

A type cannot stop this; only reading the call site can. Worth stating plainly: the helper made
the mistake harder and did not make it impossible, and I made it anyway in the file where the
helper's own warning is one directory away.

## Approach

Not yet implemented -- recorded first, per the one-finding-per-commit rule.

(a) Assign the note to a local, refuse on null, and pass it into the undo audit call's `extra`.
(b) Pass Emergency Disable's existing serviced note into the `AuditService` event alongside the
trace step, not instead of it.

Consider a follow-up guard: a source-level test asserting no call site uses `NoteFor(...) is null`
as a bare condition. That is a lint-shaped rule and it catches precisely this recurrence.

## Files changed

None yet.

## Guard proof

Pending. For (a), a test asserting the undo audit event carries the servicer key when a servicer
undoes a protected target -- it must fail against the current code, which emits no such key.

## Coder dispute (if any)

None. Verified by reading the current code.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh`
Range: `b351005..HEAD`. See `pps-1.md` for why this pass produced no final consolidated report
(auth token revoked mid-run); findings recovered from its reasoning trace and independently
verified.

Worth recording about the review itself: it read the commit messages as CLAIMS and checked them,
which is what caught these -- its note was "the e1547 commit message explicitly claims Mailbox
Permissions, Calendar Permissions, AD Attribute Editor, and undo all honour servicing. The current
code contradicts that." My own commit message was the thing that made the gap visible.
