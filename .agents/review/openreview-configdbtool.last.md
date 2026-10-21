**Goal And Approach**
Goal: remove the deploy-time privilege escalation and fragility from elevated scripts executing `sqlite3.exe` found through PATH, while preserving verified config DB backups, integrity checks, and active-job warnings.

I would achieve it the same broad way this plan does: add a tiny pre-host `db` CLI to the shipped app assembly, dispatch it before Serilog and `WebApplication.CreateBuilder`, use raw `Microsoft.Data.Sqlite` connections for backup/check/job reads, and have PowerShell call an explicit app assembly through `<ProgramFiles>\dotnet\dotnet.exe`, never PATH.

Capability proof: reading `AGENTS.md` succeeded, and `git show --stat 49e8bb0` succeeded.

**Comparison**
The replacement approach is better than hardening `sqlite3.exe`: it removes a separate host binary, removes PATH lookup, uses the app’s own SQLite stack, and lets Pester/CI test the same maintenance path deploy will use. The plan is also appropriately scoped: backup failures remain fatal, job warnings remain soft, no arbitrary SQL console is added, and manual `.recover` stays with the real sqlite shell.

Two required plan corrections:

1. [docs/ConfigDbToolInApp-Plan.md](D:/source/ExchangeAdminWeb/docs/ConfigDbToolInApp-Plan.md:175) and [docs/ConfigDbToolInApp-Plan.md](D:/source/ExchangeAdminWeb/docs/ConfigDbToolInApp-Plan.md:276): `deploy-pipeline.ps1 -Dev -PlanOnly` is required to show a resolved tool path, but current dev plan mode skips `deploy.ps1` and creates no staging publish folder. Condition: dry run before staging exists. Consequence: AC7/S2 cannot be satisfied without either creating files in plan mode or pretending a missing assembly resolved. Severity: Medium. Required change: revise the dry-run contract to print the intended host/assembly path without requiring staging to exist, or add a true no-side-effect deploy plan mode that does so explicitly.

2. [docs/ConfigDbToolInApp-Plan.md](D:/source/ExchangeAdminWeb/docs/ConfigDbToolInApp-Plan.md:239): the PowerShell CI job is planned to run `dotnet build`, but the current workflow only sets up .NET in the separate build-test job, not the PowerShell job. Condition: runner lacks the needed `10.0.x` SDK. Consequence: Pester can fail before exercising the replacement backup tests. Severity: Medium. Required change: add `actions/setup-dotnet@v4` with `10.0.x` to the PowerShell job before its build.

**Verdict**
Acceptable with changes: the approach stands; fix the dry-run contract and pin .NET setup in the PowerShell CI job before implementation. Pending step: revise the plan, then proceed with S1.