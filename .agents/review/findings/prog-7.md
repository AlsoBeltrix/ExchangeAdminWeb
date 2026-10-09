# prog-7: A ClickGate exemption's written condition is false

**Severity**: LOW - the code is safe as written; the CLAIM that keeps the exemption honest
is untrue, which is the part that matters for a registry whose whole value is its conditions
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: see `.agents/review/index.md`

## Evidence

`ExchangeAdminWeb.Tests/ClickGateRegistry.cs`, the exemption at
`MailboxPermissions.razor:179` / `CalendarPermissions.razor:178`, states:

> "SubmitSingle and ExecuteOnPrem both hold their outcome in a local opResult and only ever
> assign to result"

**`ExecuteOnPrem` reads it back on both pages**, in its `finally`:
`onPremActivity.Complete(result?.Success ?? false, result?.Message)`.

It is SAFE as written - no await sits between the last `result` write and that read, and
`?.` cannot throw if the dismiss control nulls it. But the written condition is what keeps
the dismiss-banner exemption honest, and it is false.

Predicted observable failure: none today. The risk is that a future edit introduces an await
in that gap, and the registry's own condition says no handler reads `result` back, so nobody
checks.

## Coder dispute

None. Found by the agent implementing `f2a8c75`/`63cd170` while verifying it could safely
NOT complete from a local - it read the registered condition, checked it, and found it
untrue. Recorded rather than fixed: outside that slice.

## Approach

**Taken: the second option - `ExecuteOnPrem` now holds its outcome in a local on both pages,
and the `finally` completes the activity from that local.** The first option (reword the
condition to describe the read and argue it safe) was rejected, and the argument is the point
of the finding rather than a style preference:

- The two candidate conditions are not the same size. "No handler reads `result` back" is
  checkable by grep in one command (below). "There is no `await` between the last write to
  `result` and the `finally`'s read of it, and `?.` cannot throw" is a two-clause argument a
  reader has to re-derive over a 110-line handler, on two pages, every time either changes.
  A registry condition is only worth what a reader can cheaply re-check.
- The clause most likely to break is the one a reword would have enshrined. Adding an `await`
  inside a `finally` - `await InvokeAsync(StateHasChanged)` is the obvious one - is an
  ordinary edit that no guard refuses, and it would silently re-open the hazard while the
  registry said the hazard had been considered.
- **The read was introduced by ordinary work, not designed in.** `onPremActivity` is part of
  the recent global-progress sweep; before it, `ExecuteOnPrem`'s `finally` read nothing. That
  is direct evidence this invariant erodes under routine change, which is exactly the case
  for removing the hazard rather than documenting it.
- The exemption's own text names the consequence class - the DhcpAuthorization:40 defect, "a
  write that SUCCEEDED audited and emailed as failed". Here the same interleaving would report
  a successful on-prem write in the global progress panel as a failure. Deriving the activity
  outcome from the operation's own local removes that coupling by construction instead of
  resting it on await placement.

The condition's wording was corrected too, but only to name the two locals (`opResult` in
`SubmitSingle`, `outcome` in `ExecuteOnPrem`) and to record that the `finally` read the field
until this finding. The load-bearing sentence - no handler READS `result` back - is unchanged
and is now true.

## Files changed

- `Components/Pages/MailboxPermissions.razor` - `ExecuteOnPrem`: `PermissionResult? outcome`
  declared before the `try`; all four outcome writes become `result = outcome = ...`; the
  `finally` completes from `outcome`.
- `Components/Pages/CalendarPermissions.razor` - the same four sites on the twin.
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` - the `:179` / `:178` exemption condition
  reworded to name both locals; `ExpectedLineCount` 800 -> 808 and 754 -> 762.
- `Modules/ModuleCatalog.cs` - MailboxPermissions and CalendarPermissions 1.2.3 -> 1.2.4.

## Guard proof

There is no guard to mutate, and that is a property of the finding rather than an omission:
`ConditionThatKeepsItTrue` is prose by design, stated in `ClickGateTests.cs:1576` ("move the
note to ConditionThatKeepsItTrue, where it is honestly prose"). What replaces a mutation probe
is the mechanical re-check the new wording is meant to make cheap - every whole-word `result`
in both `@code` blocks, enumerated:

```
awk 'f{print NR": "$0} /^@code \{/{f=1}' Components/Pages/MailboxPermissions.razor \
  | grep -E "(^|[^A-Za-z0-9_])result([^A-Za-z0-9_]|$)"
```

25 hits on MailboxPermissions, 26 on CalendarPermissions. Every one is the field declaration,
an assignment `result = ...`, or the word "result" inside a comment or a string literal. No
read remains. Before the change the same command returned
`onPremActivity.Complete(result?.Success ?? false, result?.Message);` on each page.

Behaviour is unchanged on every path reachable today: `outcome` and `result` are assigned in
the same statement at all four sites, nothing awaits between the last write and the `finally`,
and both are null if an exception escapes the `catch` - so `Complete(false, null)` either way.
The change only differs in the interleaving the exemption makes possible and that no await
currently permits, which is the hazard being removed.

Incidental, and reported rather than hidden: `ClickGateTests.TheRegisteredLineCountStillMatchesTheFile`
fired for real on the first run (808 vs 800, 762 vs 754). Every registered line on both pages
is markup at or below 212/211 and the edit is inside `@code` (opens at 219/218), so no entry
moved and only `ExpectedLineCount` needed updating - verified by extracting the registered
line numbers from each page's registry block, not by assumption.

## Known gaps

This is the second registry in this repo found to carry a line-keyed or written claim nobody
asserts - `ProgressRegistry`'s line pointers were the first (nine found stale in one slice).
A registry is only worth its conditions; a false one is worse than none, because it stops the
next reader looking.

## Reviewer comments

Not yet reviewed - raised by an implementing agent, outside the slice it was found in.

## Closeout

Fixed on main. Release build 0 errors; `dotnet test ExchangeAdminWeb.slnx` 3674 passed, 0
failed, 3 skipped (unchanged); `dotnet format --verify-no-changes` exit 0; `git diff --check
HEAD` clean; every added line ASCII. Push outstanding, as with the other `[v]` rows.
