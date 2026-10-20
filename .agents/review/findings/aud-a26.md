# aud-a26: undoing an attribute edit told nobody

**Severity**: MEDIUM. The audit record was always written, so the reversal was never
invisible to someone who went looking. What was missing is the push: administrators were told
about the change and not about its retraction, which is the worse half of a pair to know.
**Status**: Verified, fixed, independently reviewed, review findings remediated
**Branch**: -- (direct-to-main)
**Commit**: `02ba918`

## Evidence

Finding 26 of the 2026-10-16 audit, verified against the code.

`ADAttributeEditorUndoService` had no `EmailService` at all - not an unused field, not a
disabled call. `ExecuteUndoAsync` wrote `LogUndoAudit` on both outcomes and returned.

Dispatch for attribute edits lives in the forward-edit **page**,
`Components/Pages/ADAttributeEditor.razor`, which calls `Email.SendAdminNotificationAsync`
at three points around its save. Undo calls `ADAttributeEditorService.SaveAsync` directly,
so it never passes through any of them.

The directory write itself is identical - undo IS a save, with the old values as the
proposed ones - which is what makes the asymmetry easy to miss: the operation is audited the
same way, performed the same way, and announced only one way round.

## Predicted observable failure

An administrator receives mail that someone changed a user's `title` and `department`. A
second administrator reverts it an hour later. No mail is sent. The first administrator
continues to believe the change is in place, and the only way to learn otherwise is to go
and read the audit log for an event they have no reason to suspect exists.

## Approach

The undo service dispatches its own notification, on both outcomes, around the existing
`LogUndoAudit` calls.

**The action is `ADAttributeEditor_Undo`, not the forward action name.** Reusing
`ADAttributeEditor_Update` would make a change and its retraction indistinguishable in a
mailbox, which defeats the reason for sending it. The notice names what was reverted and the
operation id it reverses, so the two mails can be paired by eye.

**A failed send is logged and swallowed**, matching the forward path. The directory has
already been written and the audit record already exists; a mail server being down must not
turn a completed reversal into a reported failure.

### What this deliberately does not do

The finding names the root cause - dispatch belongs to the page, and anything that is not
the page is silent by construction. Moving dispatch into `ADAttributeEditorService.SaveAsync`
would close the whole class, but it means removing the page's three call sites in the same
change, and any miss there is a duplicate notification rather than a missing one. That is a
larger change than this finding authorises. Filed as `aud-a26b`.

## Files changed

- `Services/ADAttributeEditorUndoService.cs`
- `ExchangeAdminWeb.Tests/UndoNotificationTests.cs`
- `Modules/ModuleCatalog.cs` - ADAttributeEditor 1.6.0 -> 1.7.0. Module-scoped; no shared
  code changed, so no base app bump.

## Guard proof

Split, and the split is declared in the test file. The dispatch is driven for real against a
substituted `EmailService`; that it is *called* on both paths is a source scan, because
`ExecuteUndoAsync` reaches a live directory through its lookup long before it gets there.

| Test | What it pins |
| --- | --- |
| `AReversalIsAnnouncedAsAnUndo_NotAsAnotherUpdate` | the action name, observed on the substitute |
| `TheNoticeNamesWhatWasRevertedAndWhichOperation` | target, reverted attributes, original operation id, and no spurious Error row |
| `AFailedReversalSaysSo_AndCarriesTheError` | the failure path sends too, with `success: false` and the error |
| `AnUnsendableNoticeDoesNotFailTheReversal` | a throwing mail server does not propagate |
| `BothOutcomesOfExecuteUndo_DispatchTheNotice` | the wiring the four above cannot reach |

The test service is constructed with nulls for every dependency the method does not touch.
That is deliberate: if the dispatch ever starts reaching for another one, these throw rather
than quietly covering less than they claim.

| Mutation | Result |
| --- | --- |
| success-path dispatch deleted | FAIL - `BothOutcomesOfExecuteUndo_DispatchTheNotice` |
| action renamed to `ADAttributeEditor_Update` | FAIL - 2 tests |
| `throw;` added to the catch | FAIL - `AnUnsendableNoticeDoesNotFailTheReversal` |

Source restored byte-identical (md5 verified), touched, rebuilt.

Gate: Release 0 errors; suite 4413 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean.

## Known gaps

- **No end-to-end undo was driven.** The lookup needs a directory. Nothing here proves the
  two call sites execute at runtime in the order the source shows.
- **No SMTP was exercised**, and the recipient is whatever `Email:AdminNotificationEmail`
  holds. Unlike `aud-a27`, this module has no recipient of its own, so there was no
  wrong-recipient half to fix - but it also means the notice goes to the general admin list
  by design, not by accident.
- **The root cause is untouched.** Any future non-page caller of `SaveAsync` will be silent
  the same way. `aud-a26b`.
- The notice is not recorded as having been sent. If it fails, the log knows and the audit
  record does not - the same gap `aud-a27` closed for Emergency Disable, left open here
  because this finding did not reach it.

## Closeout

Fixed directly on `master`. Independent review pending - and currently impossible: both
review harnesses are returning 401 (`.agents/state.md`).

## Independent review - `gemini-4-argon-high`, 2026-10-20

Reviewed as batch 3 of the backlog. Prompt kept at `.agents/review/prompt-backlog-3.md`.

**Any earlier claim in this record that review was impossible was wrong** - the probes had
been invoked incorrectly. `.agents/state.md` carries that correction.

**One MEDIUM defect.** An exception out of `SaveAsync` - or anywhere else in
`ExecuteUndoAsync` - left no audit record AND no notification. The forward path audits every
outcome, and a reversal that threw is exactly the event someone later asks about, because it
may have written to the directory before it failed. The catch now writes the undo audit and
notifies, with a null serviced note (the catch cannot know one).

Remediation landed in the follow-up commit, every fix mutation-proven.
