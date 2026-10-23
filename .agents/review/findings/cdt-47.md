# cdt-47: docs/Containerization-Feasibility.md still carries stale deploy.ps1:<line> pointers

**Severity**: LOW - stale operator-doc pointers (the cdt-43 shape). `docs/Containerization-Feasibility.md` names `deploy.ps1` by line numbers that have moved. (`.agents/state.md` 210 has the same and is the supervisor's at close.)
**Status**: Verified on branch; awaiting merge (docs only) - admitted 2026-10-23 evening from the cdt-36 agent's second hand-back; inside the released scope
**Branch**: `fix/cdt36` (worktree `D:\source\ExchangeAdminWeb-wt\cdt36`), continuing from `42c2690b`
**Commit**: the `docs(cdt-47)` commit on `fix/cdt36` that carries this record (not merged)
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `0c9917d5` (the document there is the file at `42c2690b`). Trigger: a reader follows the pointer.
Predicted failure: lands on unrelated code. Measured against `deploy.ps1` at `42c2690b`:
`deploy.ps1:287` (`loadUserProfile`) is now 483; `:289-295` (identity type 3) is 485;
`:372-394` (the authentication settings) is 568-590; `:381` (the Negotiate comment) is 577;
`:392-394` (anonymous off) is 588-590; `:323` (the key ACL) is 519; `:45-48` (the
WebAdministration import and the `IIS:` drive check) is 51-55. The installer pointers on the same
rows have moved the same way: `:595` is 884, the identity type 4 at "line 605" is 896, `:691-695`
is 990-994, and the key ACL `:621-639` is the step "Grant ... read access to certificate private
key" at 918-940. The pipeline pointer `tools/deploy-pipeline.ps1:82` is now a
blank line, one above its `-PlanOnly` branch at 83-85.

## Approach

Written before the edit. Point by the code's own words, which survive edits, as cdt-43 did. The
scope is every ops-script line pointer in the document. That is the `deploy.ps1` pointers the
finding names, plus the installer and pipeline pointers that share their rows and sentences:
fixing half a row would leave the other half to go stale the same way.

| Line | Now | Becomes |
| --- | --- | --- |
| 64 | `deploy.ps1:287`, `tools/Install-ExchangeAdminWeb.ps1:595` | `deploy.ps1` and `tools/Install-ExchangeAdminWeb.ps1`, each at `Set-ItemProperty ... processModel.loadUserProfile` |
| 67 | `deploy.ps1:372-394`, `tools/Install-ExchangeAdminWeb.ps1:691-695` | `deploy.ps1` and `tools/Install-ExchangeAdminWeb.ps1`, their `windowsAuthentication` and `anonymousAuthentication` `Set-WebConfigurationProperty` calls |
| 68 | `deploy.ps1:289-295`; "the installer also supports `identityType 4` ... at line 605" | `deploy.ps1`, its "Configuring app pool" step; "... `identityType 4` ... in its app pool step" |
| 73 | (`deploy.ps1:323`, `tools/Install-ExchangeAdminWeb.ps1:621-639`) - missed by the first listing, which cut the row | (the certificate private-key ACL step of `deploy.ps1` and of `tools/Install-ExchangeAdminWeb.ps1`) |
| 77 | `deploy.ps1:45-48` | `deploy.ps1`'s opening `Import-Module WebAdministration` and its `IIS:` drive check |
| 155 | "the comment on `deploy.ps1:381`" | "the comment above the Negotiate `Remove-WebConfigurationProperty` call in `deploy.ps1`" |
| 214 | (`deploy.ps1:392-394`) | (`deploy.ps1`'s `anonymousAuthentication` `enabled` `$false`) |
| 237-238 | (`deploy.ps1:287`, `tools/Install-ExchangeAdminWeb.ps1:595`) | (the `processModel.loadUserProfile` setting in `deploy.ps1` and `tools/Install-ExchangeAdminWeb.ps1`) |
| 315 | at `deploy.ps1:323` and `tools/Install-ExchangeAdminWeb.ps1:621-639` | in the certificate private-key ACL step of `deploy.ps1` and of `tools/Install-ExchangeAdminWeb.ps1` |
| 415 | (`tools/Install-ExchangeAdminWeb.ps1:11-17`) | (the "Fresh install" list in `tools/Install-ExchangeAdminWeb.ps1`'s help) |
| 446 | `tools/deploy-pipeline.ps1:82` currently warns | `tools/deploy-pipeline.ps1`'s `-PlanOnly` branch currently warns |
| 562 | (`deploy.ps1:381`) | (the Negotiate provider removal in `deploy.ps1`) |

The claims themselves are unchanged and still hold; each was re-read against the code at
`42c2690b` (Evidence). The document's pointers into C# and `Program.cs` (for example
`Services/ExoConnectionPool.cs:507-529`, `Program.cs:375`) are a different class, app code rather
than ops scripts, and are out of scope. Files: `docs/Containerization-Feasibility.md`, this record.
Guard: none beyond a doc check - no `deploy.ps1:<digit>`, `Install-ExchangeAdminWeb.ps1:<digit>` or
`deploy-pipeline.ps1:<digit>` pointer, and no "at line <n>", is left, and every quoted code word
occurs in its script.

## Files changed

- `docs/Containerization-Feasibility.md` - before `01d7307c6665`, md5 `38099DE2FBCF0667801F8C4AD09577D3`
  (at `42c2690b`); after `C9E1B630F2BFA2DF329C68973725711B`. Eighteen pointers, on the twelve rows
  of the table above; line wrapping adjusted where the words are longer. The document's status (a
  2026-09-18 draft) and every claim are unchanged.
- this record - copied from master's (`8a984e94`) and filled in

## Guard proof

Not applicable as a test (docs only). The doc check, against the worktree's files with absolute
paths: no `deploy.ps1:<digit>`, `Install-ExchangeAdminWeb.ps1:<digit>` or `deploy-pipeline.ps1:<digit>`
pointer and no "at line <n>" is left. Every code word the new text leans on occurs in its script:
`processModel.loadUserProfile` (deploy.ps1 1, installer 1); `authentication/windowsAuthentication`
(9, 4) and `authentication/anonymousAuthentication` (2, 1); `Write-Step "Configuring app pool"`
(deploy.ps1 1); `processModel.identityType -Value 4` (installer 1); `Import-Module WebAdministration`
and `Get-PSDrive -Name IIS` (deploy.ps1 1 each); `# Remove Negotiate provider` (1) above
`-AtElement @{value="Negotiate"}`; `# Certificate private key` (deploy.ps1 1) and "read access to
certificate private key" (installer 1); `Fresh install:` (installer 1); "deploy.ps1 has no native plan
mode yet" (pipeline 1). The file stays LF and ASCII; `git diff --check HEAD` clean. No PowerShell file
changed.

## Coder dispute

None. The scope widened from the `deploy.ps1` pointers to the installer and pipeline pointers that
share their rows and sentences, for the reason in Approach.

## Known gaps

- The document's pointers into app code - `Program.cs`, `Services/*.cs`, `web.config`,
  `ExchangeAdminWeb.csproj` - were not checked and may have moved too. That is a different class from
  ops-script pointers, and outside this finding.

## Reviewer comments

None yet.

## Closeout

Pending: commit on `fix/cdt36`, merge into `master`, branch deletion.

## Reviewer comments (supervisor, ops-batch-14)

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 42c2690b head 7f1890f3 (merged fb2b288f); 2026-10-23 evening; capability proof succeeded (AGENTS.md read, git show --stat 7f1890f3). Two candidates across the range, admitted as cdt-55 and cdt-56. Codex confirmed: cdt-49 uses the staged build's db active-jobs per repo guidance and missing or unreadable jobs databases soft-warn; the cdt-48 module import predates the change; no ops-script line pointer remains in the containerization doc; the changed PowerShell is ASCII-only. Artefacts: `.agents/review/ops-batch-14.result.txt`, `.stream.log`, `prompt-ops-batch-14.md`.
