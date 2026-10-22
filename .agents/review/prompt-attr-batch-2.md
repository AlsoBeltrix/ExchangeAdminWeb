# Defect review request: allowlist pattern refusal and no-op save audit (aud-a28e, aud-a26d)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `337ccff7` or later.
- The change is the branch merged at `20ef8984`: `6b74d40d` (plans), `5d2a27ba` (aud-a28e),
  `511df4f8` (aud-a26d), `1cd5f60f` (comments only), forked from `a64416fa`. Review
  `git diff a64416fa 1cd5f60f`, and read the COMBINED `Services/ADAttributeEditorService.cs` on
  master (`ValidateProposedValues`, `SaveAllowlist`, `DescribeUncompilablePattern`, `PerformSave`,
  `LogNoChangeAudit`, `SaveAndAuditAsync`, `SaveAsync`).
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a28e.md`, `aud-a26d.md`; parents `aud-a28c.md`,
  `aud-a26b.md`, `aud-a28.md`, `aud-a26.md`. Two follow-ups are ALREADY admitted from the
  implementer's own hand-back and are out of scope: `aud-a28f` (the Module Configuration allowlist
  save audits no refusal and records an empty "before" state) and `aud-a26e` (no Pattern input on
  the page; legacy import bypasses the compile check). Do not re-raise them. Repo rules:
  `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3);
  `docs/ProjectConstitution.md` audit and notification rules.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 20ef8984`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a28e: the catch at the regex call - does it fail closed in every
case (an unusable pattern never counts as a match); does `PreviewAsync` behave acceptably now;
`DescribeUncompilablePattern` versus `Regex.IsMatch` semantics (a pattern that compiles under
`RegexOptions.None` but the save uses another option set, or the reverse); a pattern that is
valid but catastrophic (timeout) at allowlist save time passes the compile check - is that the
right boundary; the operator message (does it say what the admin must do without guessing).
a26d: the no-op row - does it reconcile exactly to the notice (same action, target, ticket,
success flag); any path where the no-op row and a real-change row are both written for one save;
the undo interaction the record describes (an undo whose save finds nothing left to change);
`CanUndo` on the new row; the operation trace (no `AttributeWriteCompleted` for a non-write).
Tests passing for the wrong reason: harness stand-ins that cannot throw; assertions on message
text; the "exactly one notice" assertions counting the right dispatcher.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
