# pps-3: Serviced notes computed and then discarded, losing the audit record of the override

**Severity**: HIGH -- the write is allowed and no durable record says who permitted it. This is
the exact failure the shared helper's doc comment names as "the single most repeatable mistake in
this work", and it recurred anyway in two places.

**Status**: Fixed
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

(a) The note is bound to a local, refused on null, and passed into `LogUndoAudit`, which merges it
into the `extra` dictionary it already builds.

(b) Emergency Disable's note is threaded into `LogAudit` alongside the trace step, not instead of
it. A comment claiming the step trail was "this module's durable record" was corrected in the same
change -- it is diagnostic; the audit log is the durable record.

The follow-up guard was built rather than deferred: `NoWritePath_TestsTheServicedNoteAndThrowsItAway`
scans every file under `Services/` and `Components/Pages/` for `NoteFor(...) is null` used as a
bare condition. One narrow allowlisted exemption, and the exemption itself is bounded to a single
occurrence so a regression in the same file still fails.

## Files changed

- `Services/ADAttributeEditorUndoService.cs` -- note bound and carried to the audit; the preview
  path's deliberate discard commented as the one legitimate case.
- `Services/EmergencyDisableService.cs` -- note reaches `LogAudit` and rides `extra`; the
  incorrect "durable record" comment corrected.
- `ExchangeAdminWeb.Tests/AuditExtraChannelTests.cs` -- the call-site guard.
- `ExchangeAdminWeb.Tests/EmergencyDisableServiceTests.cs` -- servicing test plus the
  audit-threading guard; harness gained an opt-in servicer grant.

## Guard proof

The call-site guard is the strong one, and it is behavioural about the source: restoring the
discarding shape in the undo write path fails it with the precise message ("2 inline NoteFor
null-tests; only the no-audit PREVIEW path may discard the note"). Revert confirmed applied before
trusting the verdict, and confirmed removed after.

Emergency Disable's audit threading is guarded source-level, and the reason is recorded in the test
rather than left implicit: `LogAudit` sits past a Delinea credential fetch and two live backends,
so no unit test can drive it. The accompanying behavioural test asserts what IS reachable -- that a
granted servicer reaches `SERVICED` where an ordinary operator is `BLOCKED` -- and states plainly
that the run stops before any audit is written. Reverting the threading fails 1.

## Known gaps

`DisableAsync` has early-return paths (blank ticket, protected-and-refused, credential failures)
that return before `LogAudit` and so write no audit record at all. Pre-existing, unchanged by this
fix, and out of scope for it -- but a refused attempt on a protected principal arguably deserves an
audit row, and currently gets only an operation-trace step. Worth its own decision.

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
