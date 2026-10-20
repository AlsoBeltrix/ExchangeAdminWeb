# aud-a9: undo resolves a name, not an object, and can restore values onto the wrong account

**Severity**: HIGH - a directory write aimed at whoever holds a reused name, carrying another
person's old attribute values.
**Status**: Verified and fixed; review pending
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 9 of the 2026-10-16 audit, verified against the code.

`Services/ADAttributeEditorUndoService` takes its target from the audit event
(`GetString(auditEvent, "target")`) and resolves it with `_editorService.LookupAsync(target)`,
on both the preview path and the execute path. That target is a **UPN**.

A UPN is a label. It can be freed and reassigned - a leaver's name handed to a new starter is
the ordinary case, not an exotic one. Nothing compared the object the undo resolved against
the object the original edit had changed, so the undo wrote one person's old values onto
another person's account.

**The audit event could not have supported that check, because the GUID was never recorded.**
`ADAttributeEditorService.LogAudit` wrote `target.UserPrincipalName` and the changed
attributes, and nothing else identifying.

Same lesson as the Comms-10k C4 fingerprint and `aud-a1`: bind to the immutable id. The
editor itself already does exactly this at `ADAttributeEditorService.cs:744`, re-reading the
GUID before its own write - the undo path simply never got the same treatment.

## Predicted observable failure

An attribute change is made for a leaver. Months later their UPN is reassigned to a new
starter. An administrator finds the original change in the audit log and clicks Undo. The new
starter's attributes are overwritten with the leaver's old values, and every record of the
undo names the UPN, so the trail reads as correct.

## Approach

Two halves, and both are needed.

1. **Record it.** `LogAudit` now adds `targetObjectGuid` to the audit extra when the resolved
   principal has one.
2. **Check it.** `IdentityMismatch` runs on both the preview and the execute path, before the
   protected-principal check, and refuses unless the recorded GUID matches the resolved one.

**A record written before the GUID was captured is REFUSED, not waved through.** That is a
deliberate behaviour change and the most arguable thing in this slice: undo stops working for
every existing audit row. It is the right way round - an undo that cannot prove its target is
precisely the failure being fixed - and the operator can still re-apply the values by hand,
since the record shows them. The refusal says so rather than just failing.

## Files changed

- `Services/ADAttributeEditorService.cs`
- `Services/ADAttributeEditorUndoService.cs`
- `ExchangeAdminWeb.Tests/UndoIdentityTests.cs`

## Guard proof

The decision is a pure function and is tested as one: five cases covering a match, a
case-different match, a reassigned name, a record with no id, and a directory that returns no
id. Case-insensitivity is tested deliberately - AD hands back whatever casing the provider
used, and refusing on that would make undo unusable for no safety gain.

**There were no behavioural tests of this service at all before this**, which is why adding
the refusal broke nothing: nothing exercised either path. That absence is why the sixth test
is a source scan asserting the check appears on **both** call sites - the service reaches a
live directory before either, so nothing here drives them.

| Mutation | Result |
| --- | --- |
| delete the GUID comparison | FAIL - `AReassignedName_IsRefused`, and only it |

Source restored byte-identical (md5 verified), touched, rebuilt.

Gate: Release 0 errors; suite 4358 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean. `ADAttributeEditor` 1.4.4 -> 1.5.0 - minor, because undo's contract
changed for existing records.

## Known gaps

- **Undo is now unavailable for every audit row written before this commit.** Intended, and
  the operator is told why, but it is a real loss of function and the owner may want to know.
- Neither call site is driven end to end; only the decision is.
- The GUID is recorded only when the resolved principal carries one. A principal resolved
  without a GUID writes a record that can never be undone - fail-closed, but silent at write
  time rather than explained.

## Closeout

Fixed directly on `master`. Independent review pending.

## Independent review, 2026-10-20 - no changes required

**Fallback harness** (`gpt-5.5` via Portkey) - see `aud-a21-a22` for the caveat.

The reviewer traced the one thing that would have made this fix worse than the defect: whether
`targetObjectGuid` actually survives from the editor to the undo page. It does -
`LookupAsync` populates `ObjectGuid`, `LogAudit` writes the key, `AuditService.MergeExtra`
preserves it, the JSONL writer emits the dictionary, and `AdminEventLog` clones every property
into `RawFields`, which both undo paths receive. Had any step dropped it, undo would have been
permanently broken rather than safer.

They also checked that `ProtectedPrincipalServicing.Extra` returns a new dictionary or null,
so this fix mutating it cannot affect other callers.
