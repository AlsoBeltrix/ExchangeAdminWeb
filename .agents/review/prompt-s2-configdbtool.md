# Defect review request: ConfigDbToolInApp S2

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`.
- Base: `9e7f9f9`. Head: `b1577f7`. Review `git diff 9e7f9f9..b1577f7` - a single commit.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- The plan this commit implements: `docs/ConfigDbToolInApp-Plan.md` (sections 3.2, 3.3
  "Pester", 3.4, 5 "S2", 6). The finding record `.agents/review/findings/aud-sqlite3-path.md`
  lists where the implementer departed from the plan and why. The C# side it calls landed
  in `86970a5` and `872f211` (`Services/Storage/ConfigDbCli.cs`, `ConfigDbMaintenance.cs`).
- Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Architectural Invariants" (1: the
  installer is standalone; 4: every step supports -PlanOnly; 5: error model, native exit
  codes become throws; 6: pure ASCII and Windows PowerShell 5.1 compatible for deploy.ps1
  and the modules it imports) and "Known Failure Classes".

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat b1577f7`. Report whether
both succeeded.

## Mandate

Hunt for observable defects: a deploy, promotion, installer run or cutover that proceeds
without a verified backup or with a wrong one; a path that still resolves a binary from
PATH or from a user-writable location; a plan-mode (`-PlanOnly`, no `-Apply`, `-WhatIf`)
path that creates, deletes or executes anything; a 5.1 incompatibility in `deploy.ps1` or
the modules it imports; an exit-code or stdout handling error against the C# contract;
a Pester test that passes for the wrong reason or depends on the machine it runs on; a CI
change that cannot work on `windows-latest`; and departures from the plan that change
behaviour in a way the record does not justify. Judge the implementer's additions: the
file-version floor replacing the exit-1 detection, the fail-fast resolve after publish in
`deploy.ps1` that deletes staging on failure, the post-deploy check reading the tool from
`$PublishPath`, and the Pester fixture loading of `Microsoft.Data.Sqlite` from build output.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
