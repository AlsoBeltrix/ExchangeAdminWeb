# ppv-3: Save can persist the list while a validation is still in flight, silently dropping the pending entry

**Severity**: MEDIUM — the operator is told the protected principals were saved while the
entry they just added is absent from the store; it appears in the page afterward, so the
loss is invisible until a reload.
**Status**: Verified
**Branch**: default-branch mode (one commit per finding, per repo policy)
**Commit**: (filled in after commit)

## Evidence

The Save button is disabled on `saving` only, never on `ppValidating`
(`Components/Pages/AdminSettings.razor:99`):

```razor
<button class="btn btn-primary btn-sm" @onclick="SaveProtectedPrincipals" disabled="@saving">
```

`AddValidatedAsync` sets `ppValidating`, then `await`s the lookup on a background task
(`:534-543`), so the circuit is free to process another click meanwhile. Slices 2-3
disabled the per-field Add buttons and inputs during validation but did not extend that to
Save. `SaveProtectedPrincipals` (`:581-590`) snapshots `ppUsers` / `ppGroups` / `ppOus`
with no check on `ppValidating`.

## Predicted observable failure

1. Operator clicks Add on a valid principal; the button reads "Checking...".
2. Before it returns, they click Save.
3. `SaveConfig` persists the list **without** the pending entry, and the status reads
   "Protected principals saved."
4. Validation completes, the entry is appended to the in-memory list and the input clears
   — so the page now shows an entry that is not in the store.
5. The next page load loses it. The intended protection was never active.

Worse than a plain lost edit because the post-save UI state actively asserts the entry is
there.

## What

Slices 2-3 reasoned about concurrency per field — disable that Add button, disable that
input — and missed that Save reads the same lists those validations mutate.

## Approach

Disable Save while `ppValidating` is true, and have `SaveProtectedPrincipals` refuse with
a retry message if it is somehow entered anyway. Both: the disabled attribute is the UX,
and the server-side check is the guarantee, since "UI hiding is not security" is already
this repo's rule for the protection path.

## Files changed

- `Components/Pages/AdminSettings.razor` — Save button `disabled` expression and a guard
  at the top of `SaveProtectedPrincipals`.

## Guard proof

The rule was extracted to `ProtectedPrincipalEntryValidator.ShouldBlockSave` rather than
left inline, precisely so it is testable — page markup is not (no bUnit harness).
`ProtectedPrincipalEntryValidatorTests.ShouldBlockSave_ValidationInFlight_Refuses`:
forcing the helper to `=> false` fails **1**; restored, 27/27 in the file.

What that does **not** cover: the disabled-button half, and the actual timing of the
race in a browser. Manual check — add an entry, click Save while "Checking..." shows,
confirm the save is refused rather than silently dropping the entry. **Not run.**

## Coder dispute (if any)

None on the mechanism. One scope note: the reviewer's stronger option ("await or cancel
the pending validation before taking the snapshot") is deliberately **not** taken —
refusing is simpler, has no cancellation semantics to get wrong, and the operator's next
click succeeds. Recorded so the choice is visible rather than silently narrowed.

## Known gaps

The race needs a click inside the validation window (sub-second when AD is healthy,
up to 30s when the lock is contended). Rare, not impossible — and the 30s case is exactly
when an operator is most likely to click again.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard`
Harness: codex-cli 0.146.0 (`codex exec --json`, `-s read-only`).
Reviewed SHA: `521bb6e62741c7433a827079d1c53eef0b3b4fec`
Base SHA: `10d159363eeed955d825bf304a143594686b034b`
`capability_ok`: true. Verdict: **findings** (4). 2026-07-31 UTC.

Reviewer's better_approach: "Disable Save while ppValidating is true, or have
SaveProtectedPrincipals refuse with a retry message while validation is pending. For
stronger behavior, await or cancel the pending validation before taking the list
snapshot." First two adopted; the third declined as above.
