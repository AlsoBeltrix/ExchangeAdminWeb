# edl-3: A fresh lookup inherits the previous account's lockdown opt-out

**Severity**: MEDIUM - a security decision made about one person is silently applied to
the next, with the checkbox agreeing with the stale value so nothing looks wrong
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: <fix commit>

## Evidence

`docs/EmergencyDisableLockdownOU-Plan.md` S2 step 1 says it outright: "`ResetForm` and
`PerformLookup` reset the checkbox to its default, not to `false` - check both, they
both clear state today." S2 implemented only `ResetForm`.

`Components/Pages/EmergencyDisable.razor` `PerformLookup` cleared `lookupError`,
`protectedBlocked`, `servicedNote`, `resolvedPrincipal`, `disableResult`,
`operationResult`, `confirmed` and `ticketNumber`, and left `moveToLockdownOu`
untouched. `ExecuteDisable` then sends the current field value.

Triggering condition: with a lockdown OU configured, disable account A with the box
unticked, then type account B into the search box still on screen and click Lookup -
rather than "New Operation", which is the path that calls `ResetForm`.

Predicted observable failure: account B's confirm panel opens with the box unticked,
inherited from A. The operator sees an unticked box and may well not notice it should
have been ticked, because it looks exactly like a deliberate choice. `DisableAsync`
receives `false` and B is disabled WITHOUT the lockdown move - re-enablable by routine
helpdesk rights - on a decision nobody made about B.

## Approach

`PerformLookup` re-arms with `moveToLockdownOu = lockdownOuConfigured;` alongside the
rest of the state it already clears.

Two guards rather than one: a scoped assertion on the `PerformLookup` body, and a
counting assertion over the whole `@code` block tying the number of re-arm sites to
the number of paths that clear `confirmed`. The second exists because the defect is
not "PerformLookup was missed", it is "a state-clearing path was missed" - a third one
added later would reintroduce it.

## Files changed

- `Components/Pages/EmergencyDisable.razor`
- `ExchangeAdminWeb.Tests/EmergencyDisableServiceTests.cs`

## Guard proof

Full Release suite 3634 passed / 0 failed / 3 skipped. Format and
`git diff --check` clean.

Deleting the `PerformLookup` re-arm fails both new guards (2 failed / 0 passed).

**The counting guard was VACUOUS on its first probe and the probe is what found it.**
Written as `reArmSites >= clearingSites`, it passed with the fix deleted: two clearing
paths against three re-arm sites, because `OnInitializedAsync` re-arms without clearing
`confirmed` and inflated the count. Now pinned at `clearingSites + 1`, with the reason
in the test. This is the fifth time this repo's probe step has caught a test rather
than a bug.

## Coder dispute

None. The plan named this exact trap and I implemented half of it.

## Known gaps

Source-level, like every other S2 guard - there is no bUnit harness, so nothing here
proves the rendered checkbox state. Manual acceptance step 5 is the behavioural check.

## Reviewer comments

Change review of `2d0494c..b164d7b` (S2), sole finding.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
Harness: codex-cli 0.159.0. No escalation triggers matched.
Raw output: `.agents/review/ed-s2.result.json`.

It also graded the other four S2 guards, and its criticisms are recorded rather than
dismissed: the unconfigured guard was unscoped and satisfiable by the `ResetForm`
assignment (now scoped to the checkbox markup), the pass-through guard would miss a
positional `false`, and the default and confirm-text guards prove only that literals
exist. All five remain source scans; that limit is real and is why the manual
acceptance list exists.

## Closeout

<pending verdict round>
