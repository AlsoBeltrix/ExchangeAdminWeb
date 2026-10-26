# cons-7: a duplicate same-target load can strand the module page with no live load left to apply

**Severity**: MEDIUM - the configuration page can be left blank and stuck after rapid or duplicate routing during the
save-drain window, and with `authChecked` false the unsaved-changes guard is not rendered.
`Components/Pages/ModuleConfig.razor` about 1147: while module A has a save in flight, routing to B increments
`routeGeneration` and the first B load waits in `WaitForThisLoadsSavesAsync()`. If the same B parameters arrive
again before that wait ends, a second B load takes a newer generation. The older B continuation resumes first, sets
`loadedModuleId = requested`, clears `authChecked` and `statusMessage`, then fails `StillRequested` on its stale
generation. The newer B load then resumes, sees `requested == loadedModuleId` and returns at the no-op. Nothing
reaches `module = next; LoadState()`. This is precisely the "a live load abandoned with nothing to finish the page"
case cons-6's counter placement was meant to avoid.
**Status**: Open - admitted 2026-10-26 from codex settings-batch-10 on cons-6; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 554ad4da head bcaabba7 (merged d28c0d25); 2026-10-26; capability proof succeeded (AGENTS.md read, git show --stat bcaabba7)

## Evidence

Codex settings-batch-10, its single finding, at `d28c0d25`.

## Approach (to be written by the implementing agent before code)

- Do not let a stale or merely claimed load satisfy the no-op: either delay `loadedModuleId = requested` until the load
  is still current and about to apply state, or record the generation that claimed and applied it and let the
  same-module return fire only for the current generation or for page state already applied. Test the exact order - a
  save in flight on A, route to B, the same B parameters again before the drain ends, the older B resuming first - and
  assert the page shows B; mutation: the claim moved back, which must strand the page again.

## Guard proof

Pending.