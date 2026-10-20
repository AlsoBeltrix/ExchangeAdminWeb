# Independent review request: deployment and installer remediations (backlog batch 2)

You are reviewing, not implementing. **Do not modify any file.** Report findings only.

## How to get at the code - IMPORTANT

**Do not call `read_file`. It is denied in this headless session and your run will produce
nothing.** Everything you need is reachable through git, which is permitted.

- A commit and its full diff: `git --no-pager show dd8ee1a`
- Any file at HEAD: `git --no-pager show HEAD:tools/AppPoolControl.psm1`
- A finding record: `git --no-pager show HEAD:.agents/review/findings/aud-a35.md`
- Repo rules: `git --no-pager show HEAD:.agents/repo-guidance.md`
- The constitution: `git --no-pager show HEAD:docs/ProjectConstitution.md`

**Run ONE command per call. Do not chain with `;`, `&&` or a pipe, and do not append
`| Select-Object` or similar** - permission matching inspects the whole command string, so
a chained or piped command is denied.

## Context

`D:\source\ExchangeAdminWeb` - ASP.NET Core 10 Blazor Server app for Exchange Online,
Microsoft Graph and on-prem AD administration. These commits are on `master`, unpushed, and
**have never been independently reviewed**: the agent that wrote them wrongly believed no
review harness was available.

These four are deployment and installer changes. They are the highest-consequence group in
the backlog because their failure mode is a broken production deploy or a lost recovery
option, not a wrong screen.

| Commit | Finding | Record |
| --- | --- | --- |
| `dd8ee1a` | aud-a35 - confirm the app pool stopped before mirroring over it | `.agents/review/findings/aud-a35.md` |
| `b071fd6` | aud-a34 - an omitted service account is not a request to drop it | `.agents/review/findings/aud-a34.md` |
| `239176c` | aud-a36 - snapshot the config database before upgrading over it | `.agents/review/findings/aud-a36.md` |
| `3fc5e6e` | aud-a38 - a failed restart must not erase the promotion's verdict | `.agents/review/findings/aud-a38.md` |

Files most involved: `tools/AppPoolControl.psm1` (new), `deploy.ps1`,
`tools/Install-ExchangeAdminWeb.ps1`, `tools/promote-dev-to-prod.ps1`,
`tests/ps/AppPoolControl.Tests.ps1`, `tests/ps/InstallerConfigBackup.Tests.ps1`.

## Authority to check against

- `docs/ProjectConstitution.md` - highest engineering authority.
- `.agents/repo-guidance.md` - read the **Architectural Invariants** and the four **Known
  Failure Classes**. Invariant 1 (the installer is standalone and environment-neutral),
  Invariant 4 (every ops step supports `-PlanOnly`) and Invariant 6 (files read by Windows
  PowerShell 5.1 must be pure ASCII) all bear on these commits.

## What to attack

1. **aud-a35.** `Stop-AppPoolVerified` replaced `try { Stop-WebAppPool } catch {}` plus a
   fixed sleep. Is the polling correct? Can it return `Stopped` when the pool is not? Is
   `Absent` safe in every caller - particularly, can a mistyped pool name now read as
   "nothing to stop" and let a deploy mirror over a *running* application under a different
   name? That is the failure the fix is supposed to prevent and I want it attacked directly.
2. **aud-a34.** The installer now preserves an existing custom app-pool identity unless
   `-ServiceAccount` or `-ResetAppPoolIdentity` is given, and the folder ACLs follow the
   decision rather than the parameter. Check the three-way decision against every path
   including `-PlanOnly`, a brand-new pool, and a pool on LocalSystem/NetworkService
   (identityType 0/1/2), which I treat as "not a custom identity". Is the password handling
   still correct when the identity is preserved?
3. **aud-a36.** The installer takes a verified SQLite backup before mirroring. Check the
   ordering, that a fresh install does not acquire a hard dependency on `sqlite3.exe`, and
   that `Resolve-ConfigDbPath` picks the shared database rather than the publish folder's
   copy. Is `-SkipConfigBackup` reachable and honest?
4. **aud-a38.** The prod restart now runs inside `finally` but cannot throw out of it, and a
   new branch reports "promotion succeeded, application down". Trace the control flow for:
   promotion succeeds + restart fails; promotion fails + rollback succeeds + restart fails;
   promotion fails + rollback fails + restart succeeds. Does the operator get the true state
   in each case, and is the exit status right?
5. **The tests.** All of these are guarded by Pester tests that are mostly source scans or
   injected script blocks. Tell me which guards would pass against broken code. This stream
   has repeatedly shipped guards that assert a string while the branch around it is dead -
   look for exactly that.
6. **The records.** Each `.agents/review/findings/*.md` states what was done and a "Known
   gaps" section. Say plainly if a record claims something the code does not do.

## Output

Findings as a list: severity, file and line, what is wrong, and the concrete failure it
produces. Name any record claim that is false. If a category is clean, say so explicitly -
I need to know what you checked and found nothing in, not just what you found.
