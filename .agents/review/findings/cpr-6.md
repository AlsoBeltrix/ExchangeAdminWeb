# cpr-6: A failed directory-role read rendered as "None active"

**Severity**: MEDIUM - nothing is gated on the role list, so this opens no hole. What it does is
mislead the operator at the one moment the panel exists for: deciding whether to reset an account
that may hold Global Administrator.
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: the commit that completes this record

## Evidence

`Services/CloudPasswordResetService.cs` as of `8a411c1`: `ReadDirectoryRolesAsync` returned `[]`
when the Graph read failed, with a comment arguing the fallback was safe because nothing gates on
it. `Components/Pages/CloudPasswordReset.razor` then rendered any empty list as "None active".

The safety argument was correct and beside the point. The panel is not a gate; it is what an
operator reads before committing, and "None active" is a claim the failed call did not earn.

## Predicted observable failure

The role read fails - a throttled Graph, a missing `RoleManagement.Read.Directory` consent, a
transient 5xx - on a target that holds Global Administrator. The panel says "Directory roles: None
active". The operator, who is looking at that line precisely to know what they are about to reset,
proceeds believing the account is ordinary.

## Approach

Carry the read status beside the list. `ReadDirectoryRolesAsync` returns
`(IReadOnlyList<string> Roles, bool ReadFailed)`, `CloudPasswordTarget` gains
`DirectoryRolesReadFailed`, and the panel renders three states rather than two: "Could not be
read", "None active", or the roles.

Same shape as the fix for `cpr-4` and the same underlying rule: an unanswered question is not a
negative answer. Three of this module's defects have now been that mistake in different places,
which is why the rule is stated in the plan rather than left to each site.

## Files changed

- `Services/CloudPasswordResetService.cs` - the return tuple and the record field.
- `Components/Pages/CloudPasswordReset.razor` - the three-state render.

## Guard proof

Covered by the write-path and catalog suites compiling against the new record shape, and by the
panel's three branches. No mutation probe: the change is a rendering distinction with no pure
function to invert, and claiming a probe that was not run would be worse than saying so.

## Coder dispute

None.

## Known gaps

The distinction is only as good as the read's own error handling, which is unchanged: a Graph call
that returns 200 with an empty `value` array is still legitimately "None active", and that is
correct.

## Reviewer comments

Round 1, Change review over `8a411c1^..8a411c1`:
Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard, 2026-09-23.
Capability proof passed. Verdict **unsound**, one MEDIUM and one LOW, no CRITICAL and no HIGH.
It cleared the things this slice exists to hold: the destination is not operator-influenceable,
the descriptor's route, policy, category, fail-closed flags, boolean field, default and version
all line up, and the `cpr-5` disconnect fix closes the false negative.

A MEDIUM closes on the coder-side guard proof (`.agents/decisions.md` 2026-08-31).

## Closeout

Fix and this record land with the write-path slice.
