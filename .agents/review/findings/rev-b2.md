# rev-b2: callers that build an on-premises target without its SID fall back to label matching, so a renamed protected user is writable

**Severity**: HIGH - fail-open. `ProtectedPrincipalService` compares a protected user row by SID when the target carries
one and otherwise by the stored label (UPN or mail). Licensing Updates and the AD Attribute Editor build their target
snapshot without `ObjectSid`. Store a protected user, change the user's UPN and mail, and those two modules compare the
stale label, get NotProtected and write (`Set-ADUser`). The plan's rule is that the new build never matches names.
**Status**: Admitted; awaiting fix on branch
**Lines** (master `145f513f`): `Services/ProtectedPrincipalService.cs:1934-1936`; `Services/LicensingUpdatesService.cs:262-270`, `:382-387`; `Services/ADAttributeEditorService.cs:1317-1325`.
**Reviewer**: codex-commercial / gpt-6-astra / xhigh / standard (end-of-work defect review B, range `5c4c2970..82a67e2f`, dispatched 2026-11-06 from master `145f513f`; report `.agents/review/rev-b.result.txt`, gitignored)

## Fix shape (reviewer's, to be verified by the implementer)

Every on-premises target snapshot carries `objectSid` (both callers already read the object from AD); the check refuses
(incomplete, named reason) when a target lacks the identity the rule needs, and the label branch is removed. The test
that expects label matching for a missing SID encodes the defect and must change; add the renamed-user case for both callers.
Check every other `ProtectedPrincipalTarget` constructor for the same omission.
