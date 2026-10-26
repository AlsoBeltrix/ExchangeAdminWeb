# Defect review request: a route generation stops an older load of the same module overwriting the newer page (cons-6)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `d28c0d25` or later.
- The change is one commit, `bcaabba7`, on top of `554ad4da`. Review `git diff 554ad4da bcaabba7 -- . ':!.agents'` and
  read the COMBINED `Components/Pages/ModuleConfig.razor`: `routeGeneration`, the number each load keeps,
  `StillRequested(requested, generation)` requiring both the same module and the latest number, its call sites (the two
  navigations, the state application, the final status), and the re-anchored cons-4 source guard (now pinning
  `if (!StillRequested(` rather than the whole call text). Also the new case
  `AnOlderLoadOfTheSameModule_DoesNotOverwriteTheNewerPage_NorDropItsEdits`.
- You may read any file and run read-only git commands. One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/cons-6.md` (it carries three decisions and three known gaps), parents `cons-4.md`
  and `aud-a67.md`; the settings-batch-9 result that raised cons-6. ALREADY admitted and out of scope - do not
  re-raise: aud-a67 **(12)** (a save outlasting its bound holds its worker and connection; it needs a written plan),
  and the three recorded gaps as stated - one page instance only, an abandoned load still running to its end, and the
  `int` counter wrapping after about two billion route changes. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md`
  Known Failure Classes and the ClickGateRegistry line-keying rule.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat bcaabba7`. Report whether both succeeded.

## Mandate

Hunt for observable defects, and weigh the agent's three recorded decisions rather than taking them.

1. **The counter is incremented after the no-op return, not at the top**, so a parameter set for the module already
   shown cannot invalidate the load in flight for that route. Check that premise against every way
   `OnParametersSetAsync` can run: a re-render with the same id, a re-render with a different id, the first render, a
   render triggered by the page's own state change, and a prerender followed by the interactive render. Name any path
   where the increment is missed (a newer load keeping an older number) or fired when it should not be (a live load
   abandoned with nothing to finish the page).
2. **A generation rather than cancellation**, because the stores take none. Judge whether anything else in the load
   path does take a token and is now left running when it need not be.
3. **The re-anchored cons-4 guard** pins where the check sits, not what it compares. Confirm it still fails if the
   check is removed or an await is introduced after it, and that it cannot pass against a check that compares nothing.

Then the ordinary hunt: every point after an await that applies state, navigates or reports - is any one of them
missing the check; the comparison itself when the page is reused for a different module by the router without a
dispose; whether the final status of an abandoned load can still overwrite a newer status; and the new case - does it
assert the edit survives for the right reason (that the older load applied nothing) rather than incidentally.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.