# Defect review request: the audited allowlist save and the editable pattern (aud-a28f, aud-a26e)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `2ae665a5` or later.
- The change is the branch merged at `488e535e`: `0e071e0a` (plans), `07c366ef` (aud-a28f),
  `fc117a1c` (aud-a26e), `23df8afe` (comments only), forked from `b38a6861`. Review
  `git diff b38a6861 23df8afe`, and read the COMBINED `Services/ADAttributeEditorService.cs`
  (`SaveAllowlistAndAudit`, `LoadAllowlistFromDisk`, `LogAllowlistSaveRefused`,
  `LogAllowlistSaveFailed`, `DescribeUncompilablePattern`, `ImportLegacyIfPresent`),
  `Components/Pages/ModuleConfig.razor` (`SaveAllowlistAsync`, the Editable Attributes table,
  `UpdateAttrPattern`), `Services/AuditService.cs` (`LogSettingsChange`, `LogSettingsChangeFailed`)
  on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a28f.md`, `aud-a26e.md`; parents `aud-a28e.md`, `aud-a26d.md`,
  `aud-a28.md`. Three follow-ups are ALREADY admitted from the implementer's hand-back and are out
  of scope: `aud-a28g` (the attribute editor page's own refusals and catch unaudited), `aud-a28h`
  (the other settings saves audit only successes), `aud-a26f` (remediation text names the retired
  JSON file; blank and duplicate rows silently dropped). Do not re-raise them. Repo rules:
  `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3) and Architectural
  Invariant 2 (one shared config database); `docs/ProjectConstitution.md` audit rules.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 488e535e`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a28f: the single fresh read serving as both the corruption re-check
and the before-state - a store that changes between that read and the write (the other instance
saving section access or the allowlist at the same moment; Invariant 2); the `changed` list's
field comparison (null versus empty pattern, case of names, choices order); the success record
written "even when nothing changed" (is that a record of an attempt or noise); the refusal and
failure records' action/category/section matching the success record; the page's authorization
re-check still on the page - can a refusal there and a service refusal both be written for one
attempt; the audit-failure-is-logged-not-reported rule (a save whose audit write fails is reported
as a success with no record - is that acceptable for an authorization-adjacent store). a26e: the
Pattern input kept "exactly as typed" (whitespace-only, leading/trailing spaces compiled as part
of the pattern); the import refusing the whole file and leaving it in place while the allowlist
loads as corrupt - what the admin sees and whether the message tells them which row; `.razor` line
movement versus the registries. Tests passing for the wrong reason: the harness `WritesFail`
switch; page tripwires that grep source text.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
