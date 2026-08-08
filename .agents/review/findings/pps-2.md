# pps-2: Page-level gates block the servicer before the serviced service gate can run

**Severity**: HIGH -- the servicer capability is unreachable through the UI for AD Attribute
Editor and for undo preview, in modules whose commit message claims it works. Same class as the
2026-08-06 defect where the capability shipped "implemented" and no operator could reach it.

**Status**: Fixed
**Branch**: -- (default-branch mode)
**Commit**: `6f7f2ac`

## Evidence

**(a) AD Attribute Editor page blocks at lookup.**
`Components/Pages/ADAttributeEditor.razor:313-316`:

```csharp
if (protectionCheck.IsProtected)
{
    protectedBlocked = true;
    return;
}
```

No servicer consultation. `protectedBlocked` then hides the edit UI (`:82`, `:205`), so the
serviced save gate at `Services/ADAttributeEditorService.cs:533` is never reached from the page.

**(b) Undo preview blocks before execute.**
The undo interface gives PREVIEW no acting principal while EXECUTE has one, so the preview refuses
a protected target and the operator never reaches the execute path that would have serviced it.

## Predicted observable failure

A member of the servicer group opens AD Attribute Editor, searches a protected user, and sees the
blocked banner with no edit form -- identical to what an unauthorised operator sees. The grant is
configured, the service honours it, and nothing in the UI will ever ask.

## What

This is the shape I explicitly recorded from Emergency Disable -- "two gates, the page hides the
write UI and the service blocks the write, both must honour servicing" -- and then failed to
apply to the other module that has it. The state file even instructed: "Check every remaining
module for this." I checked the six remaining SERVICES and not the pages of the nine already done.

The generalisable rule is stronger than the one I wrote: **a page gate that hides UI is part of
the authorization decision, not a display detail.** Any module where the page checks protection
independently needs the servicer decision in both places, and the two must agree -- if they can
disagree, the stricter one wins silently and the capability evaporates.

## Approach

(a) The page's lookup gate consults the servicer for the same module id and, when serviced, allows
the edit UI while surfacing that an override is in effect. The operator must SEE that they are
acting under a grant; a silent allow is its own hazard.

(b) `IUndoableModule.PreviewUndoAsync` now takes the acting principal, as execute already did, so
preview and execute reach the same decision. A preview that refuses what execute would allow is a
UI lie either way.

The note is deliberately DISCARDED in preview, and commented as such at the call site: a preview
performs no write and emits no audit event, so there is no record for it to belong to. That is the
one legitimate use of the `NoteFor(...) is null` shape; on a write path it is finding pps-3.

## Files changed

- `Components/Pages/ADAttributeEditor.razor` -- lookup gate consults the servicer; new
  `protectedServiced` flag, reset alongside `protectedBlocked`; override banner.
- `Services/IUndoableModule.cs` -- `PreviewUndoAsync` takes the acting principal, with the reason
  recorded on the parameter.
- `Services/ADAttributeEditorUndoService.cs` -- preview honours the grant.
- `Components/Pages/AdminEventLog.razor` -- passes the current principal into preview.
- `ExchangeAdminWeb.Tests/PageProtectionGateServicingTests.cs` -- new.

## Guard proof

`PageProtectionGateServicingTests`, source-level tripwires (no bUnit harness exists, so no test can
render a page). Restoring the defect fails **3 of 5** page guards; the 2 that still pass are the
Emergency Disable theory case and the state-reset guard, neither of which depends on that branch.
Revert confirmed applied at the file before trusting the verdict, and confirmed removed after.

The interface guard is weaker and says so in its own comment: removing the parameter breaks the
BUILD, so the compiler catches that revert before the test runs. It exists to state intent, so a
later change making the parameter optional or unused has something to fail against.

Emergency Disable is pinned by the same theory test despite already being correct -- it is the
module whose two-gate shape was recorded and then not applied here, so the pair is held together.

## Coder dispute (if any)

None. Verified by reading the current code.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh`
Range: `b351005..HEAD`. See `pps-1.md` for why this pass produced no final consolidated report
(auth token revoked mid-run); findings recovered from its reasoning trace and independently
verified.
