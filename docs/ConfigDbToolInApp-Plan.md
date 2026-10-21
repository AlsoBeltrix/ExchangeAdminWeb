# The app backs up its own database: retire the sqlite3.exe host dependency

Status: Approved 2026-10-21 (owner: "okay, plan it"; the approach was ruled in chat the same
day after the deploy-host finding recorded in `.agents/state.md`). Not started.
Owner: Michael
Last verified against code: `45f3ca9` / 2026-10-21
Versions: base app `2.33.0` -> `2.34.0` in S1 (Program.cs gains a command-line entry point and
a new shared storage service). No module bumps. S2 and S3 are ops-script, test, CI and docs
changes and bump nothing (precedent `7ba6b25`, `a1103cc`, `8657b57`, `f0658c0`).
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`. The Constitution
requires a written plan: deployment scripts, persistence/storage and startup behaviour change.
Review: owner asked for a plan review before implementation (dispatch recorded in section 9).

## Exec summary

Every deploy, promotion and cutover makes a verified backup of the configuration database
before it touches anything. Today that backup is made by a separate program, `sqlite3.exe`,
which the scripts find by searching the PATH of whoever is running them. On the deploy host
that search lands on a copy sitting inside a user profile, unsigned, writable by three
accounts. An elevated deploy will run whatever is at that path. That is a privilege
escalation waiting for someone to use it, and it was installed exactly the way our own
docs say to install it.

This plan removes the separate program entirely. The app already ships its own SQLite
engine; it gets a small command-line mode that does the same backup, the same integrity
check and the same "are bulk jobs running" query the scripts do today. The scripts call the
app binary they are about to deploy, through the .NET host that lives in Program Files and
only administrators can change.

What you get: no PATH search, no user-writable binary in an elevated path, nothing to install
or keep updated on the deploy host, the same engine writing and backing up the database, and
the CI argument about how to install sqlite goes away because CI no longer needs it. What it
costs: three slices, one small app change with a version bump, a pass over the ops scripts and
their tests, and a docs sweep. The biggest risk is that a bug in the new backup mode would
leave a deploy with no rollback snapshot; the plan treats any failure there as a stop, exactly
as the current code does, and proves it with tests that drive the real binary.

Approving this authorises the three slices in section 5 and nothing else. The manual
disaster-recovery recipe that needs the real sqlite3 shell stays documented as a
by-hand step with an admin-only install location; it is not run by any script.

## 1. Owner decisions

None pending. Settled by the chat ruling of 2026-10-21 and existing decisions:

- **D1 (RULED): replace, do not harden.** The owner rejected a pinned vendored exe (the
  version-freeze objection of 2026-10-20) and the chat recommendation to drop the dependency
  rather than filter PATH results was accepted with "okay, plan it".
- **D2 (existing, unchanged): integrity failure aborts the deploy** (2026-06-18). Carried
  into the new tool: a non-"ok" integrity result is exit code 2 and the module throws.
- **D3 (existing, unchanged): active bulk jobs warn, never block** (JobStateWarning policy).
  Carried: the jobs query failing for any reason is a soft note, as today.

Assumptions (verify on first touch, fail closed if wrong):

- A1: `dotnet.exe` exists at `<ProgramFiles>\dotnet\dotnet.exe` on every host that runs
  these scripts, because the ASP.NET Core Hosting Bundle puts it there and the app cannot
  run without that bundle. Verified on the deploy host 2026-10-21. The scripts never search
  PATH for it; a missing host at that path is a stop with the path in the message.
- A2: the app is framework-dependent (no `SelfContained`/`RuntimeIdentifier` in the csproj,
  verified 2026-10-21), so `dotnet ExchangeAdminWeb.dll` works from any publish or build
  output folder.
- A3: running the web assembly in command-line mode must not read `appsettings.json`,
  create a logger that writes files, open Kestrel, or touch IIS. The dispatch sits before
  `WebApplication.CreateBuilder` and before the bootstrap logger.

## 2. Scope

In scope:

- A command-line mode in the app: `dotnet ExchangeAdminWeb.dll db <verb> ...` with verbs
  `backup`, `check`, `active-jobs`, `init`.
- `tools/SqliteConfigBackup.psm1` and `tools/JobStateWarning.psm1` call that mode instead of
  `sqlite3.exe`. Their public contract otherwise unchanged (same function names where the
  semantics are the same, same throw-vs-warn behaviour, same return values).
- The five callers pass the folder that holds the app binary: `deploy.ps1`,
  `tools/deploy-pipeline.ps1` (indirectly, via deploy.ps1), `tools/promote-dev-to-prod.ps1`,
  `tools/Install-ExchangeAdminWeb.ps1`, `tools/Move-ConfigDbToShared.ps1`.
- Pester suites stop skipping: no `-Skip:(-not $HasSqlite)` anywhere; they drive the real
  built assembly. CI replaces the `choco install sqlite` step with a build of the app.
- Docs: every statement that `sqlite3.exe` is a deploy dependency is replaced; the manual
  recipes that genuinely need the sqlite3 shell say so and say where to install it.

Out of scope (record, do not do):

- Making `dotnet publish` in the scripts use the resolved host instead of PATH. Same class
  of lookup, but `dotnet` on PATH resolves to Program Files on every host these scripts
  support, and changing publish invocation is a separate finding. Logged in section 10.
- Any arbitrary-SQL verb in the app. Operators who need ad-hoc queries use the sqlite3
  shell by hand; the app must not become a SQL console reachable from a deploy script.
- `.recover` (corruption recovery). CLI-only; stays a documented manual step.
- The `SQLitePCLRaw` advisory, the `-NarrowPublishAcl` default, and the ACLs on the
  existing per-user sqlite3 copy on the deploy host. Separate owner items in `state.md`.

## 3. Design

### 3.1 The command-line mode (C#)

Files: `Services/Storage/ConfigDbMaintenance.cs` (the operations, no console I/O),
`Services/Storage/ConfigDbCli.cs` (argument parsing, exit codes, stdout/stderr),
`Program.cs` (dispatch, three lines).

Dispatch, placed immediately after `RegisterProvider` and before `Log.Logger = ...`:

```csharp
if (ConfigDbCli.TryRun(args, out var cliExitCode))
    return cliExitCode;
```

`TryRun` returns false unless `args.Length > 0 && args[0] == "db"`, so normal hosting is
untouched (ANCM passes no such argument). Top-level statements returning `int` make the
implicit `Main` return `int`; the existing fall-through path must `return 0` after the
`finally` or the compiler rejects the mix. Verify it compiles under the existing
try/catch/finally shape; if the shape fights, wrap the hosting body in a local function
returning `int`.

Verbs and contracts (all paths taken literally, no globbing, no relative-path resolution
beyond `Path.GetFullPath`):

| Verb | Args | Does | stdout on success | Exit |
| --- | --- | --- | --- | --- |
| `backup` | `--source <db> --dest <file>` | Open source `Mode=ReadOnly`, `PRAGMA busy_timeout=5000`, `VACUUM INTO '<dest>'`. Then open dest `ReadOnly`, `PRAGMA integrity_check`. Dest must not already exist. On any failure delete a partial dest. | `ok` then the dest path on line 2 | 0 ok; 2 failure (source missing, dest exists, VACUUM error, integrity not "ok") |
| `check` | `<db>` | Open `ReadOnly`, `PRAGMA integrity_check`. | `ok` | 0 ok; 2 not ok or unreadable |
| `active-jobs` | `<jobsdb>` | Open `ReadOnly`, the exact query JobStateWarning runs today. | pipe-delimited rows, same five columns, no header; nothing when none | 0; 2 on any error |
| `init` | `<db>` | Create an empty database file (open `ReadWriteCreate`, `PRAGMA user_version`). Refuses if the file exists. | the path | 0; 2 |

Any other argument shape: usage to stderr, exit 1. Exit codes are the only success signal
the PowerShell side trusts; stdout is parsed only after exit 0.

Rules the implementation must keep:

- Never apply `PRAGMA journal_mode=WAL` in this mode. The connection factory does that for
  the live app; the backup copy must be left exactly as `VACUUM INTO` wrote it, with no
  `-wal`/`-shm` sidecars created beside it.
- `SqliteConnectionFactory` is not used here: it creates directories and sets WAL. The CLI
  opens raw `SqliteConnection`s with explicit modes.
- Single-quote escaping for the `VACUUM INTO` path (double embedded quotes) as the module
  does today; `VACUUM INTO` does not take a parameter.
- `integrity_check` is compared to exactly `ok` (trimmed). Anything else is a failure
  with the first lines of the result in the message.
- No Serilog, no `IConfiguration`, no DI. `Console.Out`/`Console.Error` only.

### 3.2 The PowerShell side

`tools/SqliteConfigBackup.psm1`:

- `Resolve-ConfigDbTool -AppPath <folder>`: returns `@{ Host = <ProgramFiles>\dotnet\dotnet.exe; Assembly = <folder>\ExchangeAdminWeb.dll }`.
  Throws naming the missing file if either is absent. `$env:ProgramFiles` only; never
  `Get-Command dotnet`. Replaces `Get-Sqlite3Path` and `Assert-Sqlite3Available`, which are
  removed from the export list (the installer's bare `Assert-Sqlite3Available` call becomes
  `Resolve-ConfigDbTool`).
- `Invoke-ConfigDbTool -Tool $tool -Arguments @(...)`: `& $tool.Host $tool.Assembly db @Arguments 2>&1`,
  captures stdout lines and `$LASTEXITCODE`, returns both. Private.
- `Backup-SqliteConfigDb`, `Copy-SqliteDbFile`, `Test-SqliteConfigDbIntegrity` gain a
  mandatory `-AppPath` parameter and call the tool. Behaviour contract unchanged: a missing
  source returns `$null` from Backup (caller decides), every other failure throws, Copy
  still goes via a GUID temp file and still deletes dest sidecars before the move.
- Pure ASCII, 5.1-compatible, as today (deploy.ps1 imports it under 5.1).

`tools/JobStateWarning.psm1`: `Get-ActiveBulkJobs` and `Assert-NoActiveBulkJobsBeforeRecycle`
gain `-AppPath`; the query moves into the app. The module keeps the warn-never-block policy
and the soft notes for "tool missing" and "could not read". Parsing is unchanged because the
output format is unchanged.

Which folder each caller passes (the binary that is about to run, or already runs):

| Caller | `-AppPath` | Why |
| --- | --- | --- |
| `deploy.ps1` pre-deploy backup and post-deploy check | `$StagingPath` | published at line ~409, before the backup at ~455; still present at the post-deploy check |
| `deploy.ps1` job warning before pool stop | `$StagingPath` | same binary; the jobs DB schema is additive-only so the new binary reads the old file |
| `tools/Install-ExchangeAdminWeb.ps1` | `$stagingPath` | published at ~786, backup at ~850 |
| `tools/promote-dev-to-prod.ps1` | `$dev` | the dev publish folder is the build being promoted |
| `tools/Move-ConfigDbToShared.ps1` | `$DevPublishPath` | the deployed dev build |

Plan mode: every caller that prints a plan line for the backup prints the host and assembly
path it INTENDS to use, computed from the folder it would publish to or read from, without
requiring either file to exist (plan mode creates nothing, and a staging folder does not
exist until publish runs). `Resolve-ConfigDbTool` therefore takes `-PlanOnly`: in plan mode
it returns the intended paths and notes which are absent; in action mode an absent file is a
throw. `tools/deploy-pipeline.ps1 -Dev -PlanOnly` skips `deploy.ps1` entirely today (its
line ~85 says so) and so prints no tool path; that is pre-existing behaviour and is not
changed by this plan. `-Prod -PlanOnly` runs promote's native dry run and does print it.

Trust argument, recorded so it is not re-derived: the scripts execute code from the publish
folder they are about to deploy or have already deployed. Anyone who can write that folder
can already change what IIS runs as the app pool identity; this adds no new principal and
no new write path. The .NET host comes from Program Files, which only administrators can
write. The PATH search is gone.

### 3.3 Tests

xUnit (`ExchangeAdminWeb.Tests/ConfigDbMaintenanceTests.cs`, `ConfigDbCliTests.cs`):

- backup of a WAL-mode database that has an open writer connection with an uncommitted
  transaction produces a copy that passes `integrity_check` and contains only committed rows.
- backup when dest exists: exit 2, dest untouched.
- backup of a corrupted source (overwrite bytes inside a page after creating several rows):
  exit 2, no dest file left behind.
- `check` on good and corrupt files; `active-jobs` returns Running and Queued rows only, in
  submission order, five pipe-delimited columns; `init` creates a file and refuses an
  existing one.
- the copy has no `-wal`/`-shm` sidecars and its journal mode is not WAL-opened by us.
- `TryRun` returns false for no args and for a first arg other than `db`.
- a text tripwire on `Program.cs`: the dispatch appears before `CreateBuilder` and before
  `Log.Logger` (same style as the existing wiring tests that read `Program.cs`).

Pester:

- `tests/ps/SqliteConfigBackup.Tests.ps1`, `JobStateWarning.Tests.ps1`,
  `MoveConfigDbToShared.Tests.ps1`: the `$HasSqlite` prelude and every `-Skip:` go. A shared
  helper `tests/ps/Get-BuiltApp.ps1` locates the build output (`bin/Release/<tfm>/` then
  `bin/Debug/<tfm>/`; if neither has `ExchangeAdminWeb.dll`, runs `dotnet build
  ExchangeAdminWeb.csproj -c Release` once per run) and returns the folder. The suites pass
  that as `-AppPath`. Test databases are created with the built tool (`init`) plus
  `Microsoft.Data.Sqlite` loaded from the same build output for inserts, so no suite needs
  the sqlite3 shell.
- `tests/ps/DeployInvariants.Tests.ps1`: the `installs sqlite3 before running Pester` guard
  becomes `builds the app before running Pester` (pins the build command and its position);
  the skip-is-failure guard stays; new guards: no tracked `.ps1`/`.psm1` under `tools/` or
  the root mentions `Get-Command sqlite3`, and `SqliteConfigBackup.psm1` resolves the host
  from `$env:ProgramFiles` and never from `Get-Command dotnet`.
- `tests/ps/InstallerConfigBackup.Tests.ps1`: the "missing sqlite3 is fatal" test becomes
  "missing app binary is fatal".

Mutation round (coder-side proof, after the tests exist, before comments are written):

| Mutation | Must fail |
| --- | --- |
| `backup` skips the integrity check | corrupt-source test |
| `backup` opens source `ReadWriteCreate` | a test asserting the source file's bytes and mtime are unchanged after backup |
| dispatch moved below `CreateBuilder` | Program.cs tripwire |
| `Resolve-ConfigDbTool` uses `Get-Command dotnet` | the resolver guard |
| module throws replaced by `Write-Warning` on integrity failure | the Pester corrupt-DB test |
| one `-Skip:` reintroduced | CI `SkippedCount` gate (local `Invoke-Pester -PassThru` shows Skipped 1) |
| `active-jobs` filter drops `Queued` | the jobs Pester and xUnit tests |

Discipline from `.agents/state.md`: mutate the call site, not the helper; assert condition
plus statement, not message text; confirm each mutation applied (md5 before/after, touch,
rebuild) before trusting a result.

### 3.4 CI

`.github/workflows/ci.yml`, `powershell` job: delete the `Install sqlite3` step; add
`actions/setup-dotnet@v4` with `dotnet-version: '10.0.x'` (the `build-test` job already has
it; the `powershell` job does not, and the runner's default SDK is not guaranteed to be 10)
and then `dotnet build ExchangeAdminWeb.csproj -c Release` before Pester. The `SkippedCount -gt 0` gate stays. The 5.1 parse step is unchanged and
still names `SqliteConfigBackup.psm1`.

### 3.5 Docs

- `.agents/repo-guidance.md` Commands: replace the "Deploy-host dependency" bullet with: no
  host dependency beyond the Hosting Bundle; the scripts call the app's `db` mode through
  `<ProgramFiles>\dotnet\dotnet.exe`.
- `README.md` ~534-538: same replacement.
- `docs/ConfigDbOperations.md`: the dependency note at the top becomes two notes. The
  backup, integrity and "create an empty shared database" recipes use the app tool. The
  ad-hoc SQL recipes and `.recover` keep the sqlite3 shell and gain: install machine-wide
  under Program Files as an administrator, never per-user and never via a per-user package
  manager; never run it from an elevated script; `.recover` needs the shell and has no
  in-app equivalent.
- `docs/Containerization-Feasibility.md` line ~432: drop sqlite3 from the host list.
- `tools/Install-ExchangeAdminWeb.ps1` help text, both modules' header comments.
- `.agents/decisions.md`: one entry, "The app is its own backup tool; no sqlite3 on the
  deploy host", pointing here and to the finding record.
- `.agents/review/findings/aud-sqlite3-path.md`: the finding record for the deploy-host
  evidence currently in `state.md`, written in S1 so the evidence leaves state.

## 4. Acceptance criteria

- AC1: no tracked `.ps1`/`.psm1` resolves `sqlite3` by any means; grep for `sqlite3` under
  `tools/`, the root and `.github/` returns only comments that say the shell is not used.
- AC2: `dotnet ExchangeAdminWeb.dll db backup` on a database being written by another
  process yields a copy that passes `integrity_check`; on a corrupt source it exits 2 and
  leaves no file.
- AC3: `Invoke-Pester tests/ps` reports 0 skipped on a machine with no sqlite3 anywhere.
- AC4: a deploy with the app binary missing from the staging folder stops before any mirror,
  naming the path (`deploy.ps1` and the installer).
- AC5: `dotnet ExchangeAdminWeb.dll` with no arguments behaves exactly as before (the
  Program.cs tripwire plus the existing startup tests).
- AC6: CI green on the first push, with the Pester job showing the build step and 0 skipped.
- AC7: plan mode on the installer, promote (no `-Apply`), cutover and `deploy-pipeline.ps1
  -Prod -PlanOnly` prints the intended host and assembly path and creates nothing, including
  when the assembly does not exist yet. (`-Dev -PlanOnly` skips the dev deploy entirely,
  pre-existing; not an AC7 surface.)

## 5. Slices

Each slice is one commit, verified, reviewed under the standing review harness (codex,
no `--model` flag) before the next starts. Token-log line in the same commit.

**S1 - the app's `db` mode.** `ConfigDbMaintenance.cs`, `ConfigDbCli.cs`, the `Program.cs`
dispatch, xUnit tests, the Program.cs tripwire, base version `2.34.0`, the finding record
`aud-sqlite3-path.md`. No script changes; `sqlite3.exe` still works for the scripts after S1.
Gates: build, `dotnet test` on the solution, `dotnet format --verify-no-changes`, `git diff
--check`, mutation round rows 1-3.

**S2 - the scripts use it.** Both modules, the five callers, `Get-BuiltApp.ps1`, every Pester
change in 3.3, the CI change in 3.4, the installer help text. This slice is one commit
because the module's export list changes and its importers must move with it. Gates:
PSScriptAnalyzer 0 errors, `Invoke-Pester tests/ps` 0 failed 0 skipped, ASCII lint, the
5.1 parse of `deploy.ps1` and both modules under real Windows PowerShell 5.1
(`powershell.exe -NoProfile -Command` the parser call CI uses), `git diff --check`, mutation
round rows 4-7, and a manual `tools/deploy-pipeline.ps1 -Prod -PlanOnly` from an elevated
shell showing the intended tool path from the dev publish folder (AC7). State what was not
run.

**S3 - docs and decision.** Section 3.5 minus the installer help text (S2) and the finding
record (S1). `git diff --check`. Update this plan's Status to Implemented with the three
SHAs.

Then: `catchup`-style re-ground is not needed; `state.md` is updated as each slice lands.
The first real dev deploy after S2 is the owner's and is the only end-to-end proof that an
elevated deploy no longer touches `sqlite3.exe`; record its result in `state.md`.

## 6. Risks

- **A bug in the new backup leaves a deploy without a snapshot.** Mitigated by exit-code
  discipline (non-zero is a throw in every caller, as today), the corrupt-source and
  open-writer tests, and S1 landing before any script depends on it.
- **The CLI mode accidentally starts the host or reads config.** Mitigated by placing the
  dispatch before the builder and the Program.cs tripwire; AC5.
- **Pester runtime grows** because the suites now need a built app. Accepted; the
  alternative is a stub that proves nothing about the seam, which is the exact gap this
  plan closes.
- **An old deployed binary without the `db` verb** when promote or cutover runs against a
  publish folder that predates S2. The resolver cannot see verbs; the tool returns exit 1
  (usage) and the module throws with "the deployed app is older than the backup tool;
  deploy dev first". Promote and cutover are always run after a dev deploy in practice,
  and the message says what to do.

## 7. Verification entry points

`dotnet build ExchangeAdminWeb.slnx -c Release`; `dotnet test ExchangeAdminWeb.slnx`;
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`;
`Invoke-ScriptAnalyzer -Path . -Recurse`; `Invoke-Pester tests/ps`; `git diff --check HEAD`;
ASCII lint over tracked `.cs/.ps1/.psm1`.

## 8. Status log

- 2026-10-21: plan written. Codex openreview returned Acceptable with changes; both
- 2026-10-21: S1 landed `86970a5`; codex review -> cdt-1 (MEDIUM), fixed `872f211`. S2 next.
  changes folded in (section 9). Not started.

## 9. Review

openreview codex (configured default model, resolved id not emitted in the `codex exec
--json` envelope; effort xhigh; standard tier) over `45f3ca9..49e8bb0`, 2026-10-21:
**Acceptable with changes.** Capability proof passed (read `AGENTS.md`, ran
`git show --stat`). The reviewer independently arrived at the same approach and judged it
better than hardening the exe lookup. Two required changes, both verified against the
files and folded in before implementation:

1. AC7 and the S2 manual check named `deploy-pipeline.ps1 -Dev -PlanOnly`, which skips
   `deploy.ps1` and never creates staging, so no tool path could be shown without creating
   files or faking a resolution. Fixed: plan mode prints the intended path without needing
   the file; the manual check is `-Prod -PlanOnly`; `-Dev -PlanOnly` is named as out of AC7.
2. The CI `powershell` job has no `setup-dotnet`; `dotnet build` there could fail before
   Pester ran. Fixed: 3.4 adds `actions/setup-dotnet@v4` with `10.0.x`.

Prompt: `.agents/review/prompt-openreview-configdbtool.md`. Verdict text:
`.agents/review/openreview-configdbtool.last.md`.

## 10. Follow-ups logged, not authorised

- `dotnet publish` in `deploy.ps1` and the installer resolves `dotnet` from PATH. Same
  class as the finding, lower risk (Program Files on every supported host). Candidate
  finding, not in this plan.
- The per-user `sqlite3.exe` on the deploy host should be removed once S2 is deployed; the
  owner owns that host action.
