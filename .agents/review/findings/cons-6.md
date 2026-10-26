# cons-6: routing A then B then back to A lets the older A load overwrite the newer A page

**Severity**: MEDIUM - administrator configuration edits disappear with no save, refusal or unsaved-changes prompt.
`Components/Pages/ModuleConfig.razor` about 1088: `StillRequested(requested)` compares the current `ModuleId`
with the captured one, so after A, B, A the FIRST A load passes the check although it is not the latest route. It
reaches `module = next; LoadState();`, reloads from the store, clears `dirty`, increments `load` and drops the
edits made on the newer A page. cons-4 closed the A-then-B case; identity alone cannot close this one.
**Status**: Open - admitted 2026-10-26 from codex settings-batch-9 on cons-4; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base f08a664a head 554ad4da (merged 4afa745d); 2026-10-26; capability proof succeeded (AGENTS.md read, git show --stat 554ad4da)

## Evidence

Codex settings-batch-9, its single finding, at `4afa745d`.

## Approach (to be written by the implementing agent before code)

- A monotonically increasing route or load generation, or cancellation of the previous load; both the same module id AND
  the latest generation are required before navigation, before the state is applied and before the final status. Test
  the A, B, A order with an edit on the newer page and assert the edit survives; mutation: the generation comparison
  removed, which must drop the edit again. Keep cons-4's source guard honest - it pins that a check exists and that no
  await follows it, so it must still hold with the generation added.

## Guard proof

Pending.