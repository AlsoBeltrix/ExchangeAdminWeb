# prog-2: DOM `@onchange` handlers are discovered by nothing

**Severity**: MEDIUM - undercuts the filesystem-discovery guarantee that is the registry's
main claim over the hand-maintained list it replaced
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: the commit carrying this record

## Evidence

`ExchangeAdminWeb.Tests/ProgressRegistryTests.cs:22-31` scans `@onclick`, `@onsubmit`,
`@onkeydown`, `@bind:after` and component `OnChange`, and **deliberately excludes DOM
`@onchange`** - documented at the time as a limitation outside the plan's named surfaces.

The exclusion is not safe. `Components/Pages/Migration.razor:510` wires
`@onchange="ToggleSelectAllBatches"` and `:641` wires
`@onchange="e => ToggleBatchSelected(...)"`. Both reach `AdoptSelectionAsOpenBatch`
(`:2367`, `:2307`), which can call `LoadMailboxesFor` (`:1805`). The registry already
registers comparable CLICK paths through `AdoptSelectionAsOpenBatch`
(`ProgressRegistry.cs:801-807`) - so the same work is classified when reached by a click and
unclassified when reached by a checkbox.

Triggering condition: a slow call added before that delegation, or a broken delegation from
one of these unchecked handlers.

Predicted observable failure: the suite reports every operation handler classified while
real operator-triggered handlers on the Migration status grid are not classified at all. An
operator waits with no progress and no test fails.

## Coder dispute

None. The exclusion was recorded rather than hidden, which is why it was reviewable - but
"documented limitation" was the wrong call when live handlers of that shape already reach
slow work in the same file.

## Approach

The first option: DOM `@onchange` is a sixth entry in `ProgressScan.HandlerAttributes`, and
everything it turns up is classified. The allowlist option was not taken - an allowlist is the
hand-maintained list this registry exists to replace, one layer down.

**It surfaced 25 handlers across seven pages**, and **none of them is a new defect.** Every one
is a tick box, radio, select or text input that edits page state, which is what the DOM
`@onchange` surface is used for in this app. Counts, with the verdict and why:

| Page | Handlers | Verdict |
| --- | --- | --- |
| Migration | `ToggleBatchSelected`, `ToggleSelectAllBatches` | Exempt, `DelegatesTo` `AdoptSelectionAsOpenBatch` |
| Migration | `ToggleMailboxSelected`, `ToggleSelectAllMailboxes`, `OnBatchSortColumnChanged`, `OnMailboxSortColumnChanged` | Exempt, local |
| ModuleConfig | `ToggleModuleEnabled`, `SetBooleanConfigValue`, `ToggleOU`, eight `UpdateAttr*` | Exempt, local; the write is a `Save*` |
| GroupManagement | `ToggleSelected`, `ToggleSelectAll` | Exempt, local |
| SelfServiceGroups | `ToggleSelected`, `ToggleSelectAll` | Exempt, local |
| MessageTrace | `ToggleRowSelection`, `ToggleSelectAll` | Exempt, local |
| AdminSettings | `ToggleModule` | Exempt, local |
| ADAttributeEditor | `SetEditValue` | Exempt, local |

**New `KnownGap` entries: 0.** Each body was read, and so were the helpers each one calls -
`CanRemove`, `SelectableMembers`, `SetConfigValue`, `RecountConfig`, `RecountModules`,
`ClampMailboxPages`, `ClearStagedPreview`, `FilteredSortedMailboxes`, `GetSortedBatches`. All are
in-memory. `AdminSettings.ToggleModule` was the one worth care, because four of that page's
handlers are `KnownGap` for reaching an AD validation path; it does not, and its exemption says
so against those siblings by name.

The two handlers the finding is about are NOT a bare exemption. `ToggleBatchSelected` and
`ToggleSelectAllBatches` reach `LoadMailboxesFor` by exactly the route `SelectOnlyBatch` does, so
they carry `DelegatesTo = "AdoptSelectionAsOpenBatch"`, and
`ADispatchersExemptionNamesAMethodItActuallyCallsAndThatIsItselfRegistered` then forces the whole
chain to stay registered: the handler must really call it, that method must carry its own entry,
and its entry delegates on to `LoadMailboxesFor`, which Reports. Breaking any link fails a test.

Three DOM `@onchange` attributes resolve to no page method and are dropped silently, which is the
pre-existing policy for all six surfaces: `CalendarPermissions:118,120`,
`MailboxPermissions:119,121`, `ExchangeOnlineConfig:62` and `ServiceHealth:109,120,132` are
inline lambdas assigning a field. None awaits, so `NoHandlerIsAnInlineLambdaThatAwaits` is
satisfied; one that did await would fail it.

## Files changed

- `ExchangeAdminWeb.Tests/ProgressRegistryTests.cs` (the sixth pattern)
- `ExchangeAdminWeb.Tests/ProgressRegistry.cs` (25 entries, two shared reasons, remarks)

## Guard proof

A probe handler added to `Components/Pages/RecipientLookup.razor`:
`<input type="text" @onchange="ProbeUnclassifiedHandler" />` with a matching page method.

| Scanner | Result |
| --- | --- |
| with the new `@onchange` pattern | FAIL - `EveryOperationHandlerOnACoveredPageIsClassified`: "RecipientLookup.razor:121 ProbeUnclassifiedHandler" |
| with the pattern deleted again | PASS, 12/12 - the defect, reproduced |

Same probe, only the scanner differs, so what catches it is the pattern and not the probe. Probe
removed, page restored and `touch`ed, 12/12.

## Known gaps

Bypasses attempted against the new pattern. Both of these SURVIVED, both were demonstrated
rather than argued, and both are the same root cause - **regex over source cannot see control
flow.**

- **An unquoted attribute value is invisible.** `<input @onchange=ProbeUnquotedHandler />`
  compiles, runs, and is discovered by nothing: the probe above was run a second time in this
  form and the suite stayed green while a second probe on the same page was caught. This is NOT
  specific to `@onchange` - all six patterns require a quoted value, so it is a pre-existing hole
  in `@onclick` too, and no page in `Components/` uses the unquoted form today. It IS closable
  with regex: a third alternative `|([^\s"'>]+)` plus a group-3 arm in `ProgressScan.Handlers`.
  Not done here, because it is a different finding from this one and it widens six patterns at
  once; recorded as the next action rather than folded in.
- **A method group passed to a dispatcher is invisible.**
  `@onchange="e => ProbeDispatch(ProbeMethodGroupTarget)"` discovers `ProbeDispatch` and not
  `ProbeMethodGroupTarget`, because `CallSite` matches `name(` and a method group has no
  parentheses. Observed: the failure named `ProbeDispatch` alone. If the dispatcher then carries
  a plain "local dispatcher" exemption, the target's work is held by nothing. This one is NOT
  closable with regex in any honest way - deciding which identifier in a lambda is a handler and
  which is a variable is the control-flow question, and `.agents/decisions.md` already has a
  pending question about moving a guard of this family to Roslyn. The partial mitigation that
  exists is `Exempted.DelegatesTo`, which forces a NAMED delegation target to be registered; it
  does nothing for a target nobody named.

Unchanged and still true, inherited rather than introduced: the scan covers
`Components/Pages/*.razor` only, so a handler wired inside a shared component or handed to a
child as an `EventCallback` parameter is outside it entirely.

## Reviewer comments

Change review of `56e7f2c..ed7754f`, finding 2 of 2.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard

## Closeout

Fixed directly on `master` in the commit that carries this record, on top of the prog-1 fix.
Verification on that commit: build Release 0 errors, `dotnet test ExchangeAdminWeb.slnx`
**3661 passed / 0 failed / 3 skipped, 3664 total**,
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` clean, `git diff --check`
clean, ASCII only. Test-project only, so no version bump.

Row `[v]` in `.agents/review/index.md`: on main and guard-proved, push outstanding
(`.agents/push-policy.md` is ask). No verification round dispatched - MEDIUM, and
`.agents/decisions.md` 2026-08-31 closes everything below CRITICAL on the coder-side guard proof.

Next action proposed, not taken: close the unquoted-attribute hole across all six handler
patterns as its own finding.
