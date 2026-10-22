# Defect review request: deploy and installer follow-on batch (cdt-28 .. cdt-34)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `1ec35c8` or later.
- The change is the branch merged at `a7fd331`: commits `7548b56` (cdt-28), `fce5c5e` (cdt-29),
  `e3eab89` + `957fe8b` (cdt-30), `d32a848` (cdt-31), `94eb43a` (cdt-32), `0772c2a` (cdt-33),
  `1f77f13` (cdt-34), forked from `2478ede`. Review `git diff 2478ede 957fe8b`, and read the
  COMBINED `deploy.ps1`, `tools/Install-ExchangeAdminWeb.ps1` and `tools/SqliteConfigBackup.psm1`
  on master as they stand.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/cdt-28.md` through `cdt-34.md`. The cdt-29 record states a
  rule the supervisor set under the owner's go ("a supplied -ConfigStorePath that differs from
  the existing key is refused; the move tool is the only sanctioned way to move a database") -
  judge it. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Architectural Invariants" (1-6)
  and "Known Failure Classes" (1, 4); `Services/Storage/ConfigStorePath.cs` is the app rule
  the module's path validation must match.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat a7fd331`. Report whether
both succeeded.

## Mandate

Hunt for observable defects: a path to the mirror or stop that bypasses `Assert-AppPoolServesPath`
or accepts anything but 'Stopped'; a way to repoint a shared-DB instance that the cdt-29 rule
misses, or a legitimate first configuration or re-cased key it wrongly refuses; the
`-PlanOnly` behaviour of the new refusals (prints, creates nothing, non-zero exit where
claimed); the path rule versus `ConfigStorePath.Resolve` (both directions, including the
agent's own list of odd network-share spellings the app accepts and the scripts now refuse -
is refusing in the scripts while the app accepts a problem for an existing deployment?);
the manifest wrap (can a manifest failure still mask the pool-start failure; can a manifest
failure with the pool running be swallowed); the relative-path fix under 5.1 and 7; AST
guards that pin the wrong thing; 5.1 incompatibilities.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
