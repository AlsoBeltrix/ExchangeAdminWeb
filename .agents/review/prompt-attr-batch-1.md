# Defect review request: AD Attribute Editor and Admin Event Log batch (aud-a26b, aud-a28b, psl-6, aud-a28c, aud-a26c, aud-a28d)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `2e457409` or later.
- The change is the branch merged at `5e55e6aa`: commits `277f2b9a` (aud-a26b), `5b5bd6f6`
  (aud-a28b, tests only), `b7ef9d39` (psl-6), `df6a378d` (aud-a28c), `9cfd996c` (aud-a26c),
  `db140a29` (aud-a28d), forked from `ca6575a2`. Review `git diff ca6575a2 db140a29`, and read
  the COMBINED `Services/ADAttributeEditorService.cs`, `Services/ADAttributeEditorUndoService.cs`,
  `Services/ReversedOperations.cs`, `Components/Pages/ADAttributeEditor.razor` and
  `Components/Pages/AdminEventLog.razor` on master as they stand (master moved during the
  branch; the merge was clean but was not reviewed).
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a26b.md`, `aud-a28b.md`, `psl-6.md`, `aud-a28c.md`,
  `aud-a26c.md`, `aud-a28d.md`; parents `aud-a26.md`, `aud-a28.md`, `psl-5.md`. Repo rules:
  `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3) and Architectural
  Invariant 7; `docs/ProjectConstitution.md` audit, notification and authorization rules
  (every refusal of a protected target must be audited; manager/admin notices, never per-user
  mail storms).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 5e55e6aa`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a26b: a save outcome that is announced twice or not at all
(page and service, undo and update, the outer exception catches); a notice sent while the AD
throttle is still held; a failed send that fails the save; the deliberate content change to the
`Changed` row. a28b: harness tests that pass for the wrong reason (stand-ins that cannot throw,
assertions on message text, refusals that pass because the harness refuses everything).
psl-6: a base whose error is non-terminating versus terminating, the per-base error-stream clear,
a user found in one base while another fails (the record says: success with a logged warning -
can a failed base hide a second match from the ambiguity check, and is that acceptable under
fail-closed), the no-search-base path. a28c: the try boundary - any exception path out of
`SaveAsync` that still writes no audit record (protection check, credential fetch, before
`PerformSave`); the `Count == 0` branch. a26c: the `Success` rule in `ReversedOperations` -
a successful undo whose record lacks `originalOperationId`; a refused undo now audited by a28d
that could still mark the edit Reversed; the page using the rule for both the marker and the
button. a28d: the eight refusals - any that still writes nothing or writes after a mutation;
`mutationAttempted` correctness; the ordering dependency on a26c. Also: ClickGateRegistry
entries for the two pages (the record says both are NotYetConverted); any Invariant 7 fact
baked into the search-base logic.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
