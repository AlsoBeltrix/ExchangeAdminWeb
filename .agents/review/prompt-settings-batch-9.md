# Defect review request: a module load whose route moved on applies nothing; a refused second save says so; the last busy-wait ceiling; the plan's measured CLI wait (cons-4, cons-5, cons-2, cons-3)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `4afa745d` or later.
- The change is the range merged at `4afa745d`: commits `64365a78` (cons-4), `b70b46e9` (cons-5), `e61411b4` (cons-2),
  `554ad4da` (cons-3), on top of `f08a664a`. Review `git diff f08a664a 554ad4da -- . ':!.agents'` and read the COMBINED
  `Components/Pages/ModuleConfig.razor` (`StillRequested(requested)` after each of the two awaits that can span a route
  change; the shared `SaveAlreadyRunning` constant), `Components/Pages/AdminSettings.razor` and
  `Components/Pages/ExchangeOnlineConfig.razor` (the same refusal message), the busy-wait tests with the shared 25 s
  `NotTheRetryLoop` bound, the two new SOURCE guards (`TheLoad_ChecksTheRouteHasNotMovedOn_...` and
  `TheLoad_ReadsItsModuleParameterOnce_AndNeverAgainAfterAnAwait`), the changed aud-a67 (9) case, and
  `docs/ConfigDbToolInApp-Plan.md`.
- You may read any file and run read-only git commands. One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/cons-4.md`, `cons-5.md`, `cons-2.md`, `cons-3.md`; parents `aud-a67.md` (items 4, 7,
  9, 10, 13) and the settings-batch-8 result that raised cons-4 and cons-5. ALREADY admitted and out of scope - do not
  re-raise: aud-a67 **(12)** (a save outlasting its bound holds its worker and connection; it needs a written plan).
  Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` Known Failure Classes and the ClickGateRegistry line-keying rule;
  `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 554ad4da`. Report whether both succeeded.

## Mandate

Hunt for observable defects, and weigh three judgement calls the agent made and recorded rather than hid.

1. **cons-4's two checks, not three.** The agent wrote the third check (immediately before `module = next`), found no
   await separates it from the previous one so no mutant could fail for it, removed it, and replaced the property with a
   source guard that fails if the check goes or if an `await` is added after it. Verify the premise: is there really no
   await, on any path, between the last `StillRequested` and the assignment - including anything reached through a
   property getter, a `StateHasChanged` with a render that awaits, or a call that yields. If one exists, the guard is
   false comfort and the finding is not closed.
2. **cons-4 supersedes what aud-a67 (9) asserted.** (9) asserted a stale load still completes for its own module; now it
   must apply nothing. Confirm the changed case still proves (9)'s own mechanism and that nothing (9) protected is now
   unprotected, and that the source guard pinning it is not tautological.
3. **cons-2 does not bite under its own defect mutant,** by the agent's own account: the lock releases after a second, so
   the retry loop costs that case nothing, and its two siblings catch the defect instead. Judge whether the ceiling it
   keeps is worth anything or is now decoration, and whether the sibling coverage it leans on is real.

Then the ordinary hunt: cons-5's refusal on three pages - is a press really never taken (no partial state, no audit, no
cleared dirty), does Exchange Online Config's "the fields are the state" claim hold, and can the message persist after
the running save finishes; `StillRequested` itself - what it compares, and whether a route change back to the ORIGINAL
module during a load is now wrongly treated as stale; the shared `NotTheRetryLoop` bound applied to tests that measure
different things; and anything in the plan's new paragraph that overstates what was measured.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.