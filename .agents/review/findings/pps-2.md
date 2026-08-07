# pps-2: Page-level gates block the servicer before the serviced service gate can run

**Severity**: HIGH -- the servicer capability is unreachable through the UI for AD Attribute
Editor and for undo preview, in modules whose commit message claims it works. Same class as the
2026-08-06 defect where the capability shipped "implemented" and no operator could reach it.

**Status**: Verified
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

Not yet implemented -- recorded first, per the one-finding-per-commit rule.

(a) The page's lookup gate consults the servicer for the same module id and, when serviced, allows
the edit UI while surfacing that an override is in effect. The operator must SEE that they are
acting under a grant; a silent allow is its own hazard.

(b) Pass the current `ClaimsPrincipal` into undo preview so preview and execute make the same
decision. A preview that refuses what execute would allow is a UI lie either way.

## Files changed

None yet.

## Guard proof

Pending. Source-level tripwires, since no bUnit harness exists: the page's protection branch must
reference the servicer service, and preview/execute must take the same principal parameter.

## Coder dispute (if any)

None. Verified by reading the current code.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh`
Range: `b351005..HEAD`. See `pps-1.md` for why this pass produced no final consolidated report
(auth token revoked mid-run); findings recovered from its reasoning trace and independently
verified.
