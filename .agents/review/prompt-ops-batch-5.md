# Defect review request: cutover and app-pool follow-ons (aud-a38g, aud-a38f, cdt-38), Out of Office notice (aud-a7d), Migration re-wraps (aud-a23c)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `bb843973` or later.
- Five commits, all direct to master (patches applied by the supervisor):
  - `b46f13d9` aud-a38g: `tools/Move-ConfigDbToShared.ps1` asks IIS that each named pool serves its instance before any stop.
  - `c3c4b685` aud-a38f: `tools/AppPoolControl.psm1` `Assert-AppPoolServesPath` covers every IIS node at or inside the mirrored tree.
  - `2639a5a8` cdt-38: the cutover's `-SharedDbPath` check is the module's `Get-ConfigStorePathForm` rule, path normalised once.
  - `9364341c` aud-a7d: `Services/EmailService.cs` Out of Office user notification renders Scheduled with its window.
  - `a3f3ec45` aud-a23c: `Services/MigrationService.cs` and `Components/Pages/Migration.razor` re-wrap `PermissionResult` through two helpers that name every field.
  Review each with `git show <sha>`, and read the COMBINED `tools/Move-ConfigDbToShared.ps1`,
  `tools/AppPoolControl.psm1`, `tools/promote-dev-to-prod.ps1` and `deploy.ps1` on master as they
  stand (a38g and cdt-38 were written against an older master and applied on top of a38f).
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a38g.md`, `aud-a38f.md`, `cdt-38.md`, `aud-a7d.md`,
  `aud-a23c.md`; parents `aud-a38c.md`, `aud-a38e.md`, `aud-a38h.md`, `cdt-35.md`, `cdt-37.md`,
  `aud-a7b.md`, `aud-a23b.md`. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md`
  "Architectural Invariants" (1, 2, 4, 5, 6) and "Known Failure Classes" (1, 2, 4);
  `docs/ProjectConstitution.md` notification rules; `Services/Storage/ConfigStorePath.cs` is the
  app rule the module's path validation must match.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat c3c4b685`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a38f: the directory-boundary match (`...\App` versus `...\AppDev`,
trailing separators, case, `/` versus `\`, a UNC physical path, a path with `..`); the virtual
directory read through `Get-WebConfiguration` (a vdir whose pool is inherited from its
application; a listing with no root vdir treated as a missed read - can a legitimate listing look
like that); the "something must be AT the folder" rule; a38g and promote both call it - does a38g's
call pass the right parameters for dev and prod; 5.1 compatibility of every construct; the
recorded owner gap (a nested node under the SAME pool passes and is still mirrored) - is there a
cheaper refusal that should have been taken. a38g: both checks before both stops in apply mode;
plan mode's read-only check when elevated (can it mutate anything; can it mislead when not
elevated); the refusal text. cdt-38: the module rule versus `ConfigStorePath.Resolve` (both
directions; a `D:/` path, a UNC path, a Kelvin-sign or other non-ASCII drive letter, `\\?\`,
relative); normalisation happening exactly once and the normalised spelling being what is written
to both appsettings files; 5.1 behaviour. a7d: the Scheduled rendering (time zone of From/Until -
whose zone, and is it stated; an Until before From; Enabled/Disabled unchanged); the virtual
seam; the ProgressRegistry pin. a23c: any remaining field-by-field `PermissionResult` re-wrap in
`Services/` or `Components/Pages/` outside Migration; the reflection test's coverage claim; the
ClickGate `ExpectedLineCount` change versus the registry's line-keyed entries. And tests that pass
for the wrong reason.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
