# prog-2: DOM `@onchange` handlers are discovered by nothing

**Severity**: MEDIUM - undercuts the filesystem-discovery guarantee that is the registry's
main claim over the hand-maintained list it replaced
**Status**: Open
**Branch**: - (direct to main)
**Commit**: -

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

Pending. Either include DOM `@onchange` in discovery and classify these handlers, or carry an
explicit allowlist with a negative test proving each excluded handler is local-only.

## Known gaps

Pending.

## Reviewer comments

Change review of `56e7f2c..ed7754f`, finding 2 of 2.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard

## Closeout

Pending.
