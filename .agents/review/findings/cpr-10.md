# cpr-10: A revealed password, and an armed confirmation, survived a new lookup

**Severity**: MEDIUM - "shown once" is not true if it stays on screen under the next account's
preflight, and it is easy to misread whose password it is.
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: the commit that completes this record

## Evidence

`LookupAsync` cleared `errorMessage`, `target` and `destination`, and nothing else. The page
renders `resultMessage`, and renders `revealedPassword` whenever it is non-null.
`resetConfirmPending` also persisted, and the Confirm button was gated on `isBusy` alone.

## Predicted observable failure

An operator reveals a password for account A, then searches account B. B's preflight renders with
A's password still displayed beneath it and A's result banner above it. Nothing on screen says
which account the password belongs to.

Separately: arm "Reset password" for A, then look up B without confirming. The confirmation state
carries over, so B's panel opens with an enabled Confirm button the operator never armed for B.

## Approach

Clear every per-attempt field in `LookupAsync` - `revealedPassword`, `resultMessage`,
`resultSuccess`, `resetConfirmPending` - rather than only the ones that method happens to set. And
gate the Confirm button on `CanReset` as well as `isBusy`, so it cannot be live for a target that
could not be reset anyway.

## Files changed

- `Components/Pages/CloudPasswordReset.razor`.
- `ExchangeAdminWeb.Tests/CloudPasswordResetWritePathTests.cs` - two guards.

## Guard proof

`A_new_lookup_clears_every_per_attempt_field` enumerates all four fields;
`The_confirm_button_is_gated_on_CanReset_not_only_on_busy` pins the button. Removing any one clear
from `LookupAsync` fails the first.

## Coder dispute

None.

## Known gaps

State is cleared on a new LOOKUP. A revealed password still persists while the operator stays on
the same account, which is intended - they have to be able to read it - and is bounded by the
circuit's lifetime. A "dismiss" control was considered and not added: it is UI that can be
skipped, where navigating away already clears it.

## Reviewer comments

Round 1, Change review over `3b29e5f^..3b29e5f`. Same dispatch as `cpr-8`.

## Closeout

Fix and this record land with cpr-8, cpr-9 and cpr-11 in one commit.
