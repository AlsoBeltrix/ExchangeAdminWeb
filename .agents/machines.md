# Machine-Specific Facts

Per-machine local facts (CLI paths, tool versions, host layout). Not portable — keep
portable process/rules in `AGENTS.md` and `.agents/repo-guidance.md`.

## ASHBIAMWEB1 (Windows Server 2022, primary dev/IIS host)

_First recorded 2026-07-21._

### Cross-harness review (`codereview` / `openreview`) reviewers

- **codex is the review harness, and codex-commercial is retired** (owner, 2026-10-20: reviews
  move to plain `codex`; `CODEX_HOME=~/.codex-commercial` answers 401 on workspace routing
  discovery). Its wrapper, MCP-server registration and sandbox notes are archived verbatim in
  `docs/history/state-archive.md` (Archived 2026-10-23). Two of its 2026-08-31 observations may
  apply to any harness - an assumption, not re-tested: a smoke "run a command" probe can pass
  through a synced MCP server without touching the native exec sandbox (a valid probe shows
  `exec_command` items in the transcript, not `mcp_tool_call`), and a multi-line prompt passed as
  cmd-shim argument text is dropped, so prompts ride STDIN.
- **codex sandbox for guard-proof verifications (observed 2026-08-27):** `-s read-only`
  denies creating the isolated proof tree, so per-finding verification dispatches honestly
  return `guard_confirmed: false` (one round even returned "invalid" purely on that
  fail-closed rule). Dispatch verifications with `-s workspace-write` - it grants the
  disposable-worktree capability the codereview playbook's self-permissioning contract
  expects - and check `git status` afterwards; both 2026-08-27 runs left the tree untouched.
  Generation (defect-hunt) passes stay `-s read-only`.
- **codex** (codex-cli `0.163.0`, re-probed 2026-10-23;
  `C:\Users\mcoelho\AppData\Roaming\npm\codex.ps1`) - Portkey gateway, API-key auth.
  **Run reviews as `codex exec` with NO `--model` flag** (verified 2026-10-20). The configured
  model carries its provider route: `~/.codex/config.toml` sets
  `model = "@azure-openai-eus2-global/gpt-5.5-dzs"`, `model_provider = "portkey"` and
  `model_reasoning_effort = "xhigh"` (re-read 2026-10-23). A bare slug such as `--model gpt-5.5`
  strips the `@.../` prefix and fails with `Either x-portkey-config or x-portkey-provider header
  is required`, which looks exactly like the harness being down; read this config before
  recording codex as unavailable. If `--model` is ever passed, it must be the full prefixed slug.
  The `refresh_token_reused` OAuth errors it prints are harmless noise on the API-key path.
  **`--output-schema` is validated STRICTLY by this gateway (observed 2026-09-30):** every key in
  `properties` must also appear in `required`, or the call dies before the model sees anything
  with `Invalid schema for response_format 'codex_output_schema': ... Missing '<key>'`. An
  optional field is therefore not expressible - make it required and let the reviewer write an
  empty string. The failure looks like a review outcome in the stream log but is a transport
  error; `-o <file>` is never written, so a missing result file distinguishes the two.
  **A generation (defect-hunt) pass runs `-s read-only`, so its capability-proof command must be
  read-only too** - `git diff --stat` works, `dotnet build`/`dotnet test` are denied because they
  write to `bin/` and `obj/`. Supply the coder's own gate results in the prompt as claims to be
  checked by reading, rather than asking the reviewer to re-run them.
- **agy (Gemini)** (`C:\Users\mcoelho\AppData\Local\agy\bin\agy.exe`, present 2026-10-23) - the
  owner-dispatched third harness for contested findings (`.agents/repo-guidance.md` Token Budget);
  `gemini-4-argon-high` reviewed the 2026-10-20 backlog batch 3, assumed through this CLI. It
  runs headlessly only with `--dangerously-skip-permissions` (owner authorised 2026-10-20):
  print mode soft-denies every tool confirmation regardless of the allow-list.

### Deploy

- `deploy.ps1` / `deploy-pipeline.ps1 -Dev` must run in **Windows PowerShell 5.1** on this box
  (the `WebAdministration` IIS provider does not load under PowerShell 7). Dev app root:
  `D:\inetpub\ExchangeAdminWebDev`.
- `sqlite3.exe` is NO LONGER an ops-script dependency (ConfigDbToolInApp-Plan, decision
  2026-10-22): the scripts run the app's `db` mode through `<ProgramFiles>\dotnet\dotnet.exe`.
  A per-user, unsigned copy still sits on PATH (re-checked 2026-10-23) at
  `C:\Users\mcoelho\AppData\Local\Microsoft\WinGet\Packages\SQLite.SQLite_Microsoft.Winget.Source_8wekyb3d8bbwe\sqlite3.exe`
  (finding `aud-sqlite3-path`); removing it is the owner's host action, pending.
- **Shared config database path** (SharedConfigDb-Plan, decision 2026-09-04):
  `D:\inetpub\ExchangeAdminWebShared\config\exchangeadmin.db` - the default `-SharedDbPath` of
  `tools/Move-ConfigDbToShared.ps1` and the value both instances' `appsettings.json` carry
  under `ConfigStore:Path` after the cutover. Prod app root: `D:\inetpub\ExchangeAdminWeb`.
  **Cutover configuration re-verified 2026-10-23: both deployed appsettings files name this
  path under `ConfigStore:Path`, and the file exists.** Only that one field was read.
  Startup-log and cross-instance refresh acceptance checks have never been run.

### Directory environment (owner statements, for triage only)

- 2026-10-22, owner: "all users are synced" - every mailbox user has an on-prem AD object synced to
  Entra/Exchange Online; there are no cloud-only users. Use it to triage live errors (an on-prem
  Add-ADPermission trustee always has an AD object). Never bake it into code or a safety argument
  (Architectural Invariant 7); the code still fails closed when an object is not found.

### Owner's issue queue

- queue-file: `C:\Users\mcoelho\Desktop\queue.txt`

Machine-local, outside the repo, and **the owner's file - never write to it**, including status
markers; its own first line says so. It owns the enumeration of outstanding items and their
owner-maintained status markers. `.agents/state.md` points here rather than copying the list.

### Deployment and configuration receipt (2026-09-14, as of `a16c316`)

Read-only evidence: DLL FileVersion/LastWriteTime, the `ConfigStore:Path` field in
both deployed appsettings files, and selected rows in the shared config database.
No secret values or directory data were read.

**Deployed base versions re-measured 2026-10-23 as of `d881ea58`**, from FileVersion and
LastWriteTime off each assembly and nothing else. The 2026-10-12 reading (both at `2.27.1.0`,
written 2026-10-09) is archived verbatim in `docs/history/state-archive.md` (Archived
2026-10-23).

- Dev DLL `D:\inetpub\ExchangeAdminWebDev\ExchangeAdminWeb.dll` - FileVersion `2.27.1.0`,
  LastWriteTime `2026-10-14 12:53` (host local time).
- Prod DLL `D:\inetpub\ExchangeAdminWeb\ExchangeAdminWeb.dll` - FileVersion `2.27.1.0`,
  LastWriteTime `2026-10-09 14:38`.
- **Dev's assembly was rewritten on 2026-10-14 at the same FileVersion**, so the 2026-10-12
  reading that both instances run one identical build no longer holds. Neither carries the
  fix-everything work stream, which began 2026-10-21.
- Both instances shipped during the 2026-10-01 deployment freeze (both on 2026-10-02 and
  2026-10-09, and dev again on 2026-10-14). That freeze is `.agents/decisions.md` 2026-10-01 and
  is the owner's to restate or lift.
- **Method, and what it does not cover.** FileVersion and LastWriteTime identify what shipped;
  they do not prove runtime behaviour, consent or configuration, and no module-version scan of
  the DLLs has been repeated since 2026-10-05.
- `ExchangeAdminWeb.csproj` owns the base version number and `Modules/ModuleCatalog.cs` owns
  the module versions; neither is copied here.

ServiceHealth, RiskyUsers and IntuneDevices are enabled; each has a nonblank,
nonzero `GraphDelineaSecretId` and at least one section-access row. These checks do
not prove secret validity, Graph consent, live authentication or exact deployed
module versions. AccountLockoutRemediation remains disabled in `module_enablement`.
The old separate dev/prod and missing-initial-configuration records are superseded.

### Cloud owner diagnostic files (2026-09-14)

**Re-measured 2026-10-23: NEITHER file is in the workspace** (as on 2026-10-01 and 2026-10-05).
A recursive search of the repo for `CloudAccountOwnerCoverage*`
returns nothing, so the 2026-09-14 reading below is stale as to location. Both were
gitignored, so git cannot say where they went and this sweep did not look outside the repo;
treat their disposition as unknown rather than as settled.

The 2026-09-14 record, kept for what it says about the files themselves: the original ignored
`CloudAccountOwnerCoverage-20260911-154815.csv` was then in the workspace. The earlier record
mentioned three original exports; the other two were not located. The owner-authorized runtime
investigation read the surviving export and produced the ignored
`CloudAccountOwnerCoverage-20260914-runtime-investigation.csv`.
The latter is a diagnostic receipt, not application configuration or an authoritative
owner map. It contains current candidate recipients and worker classifications for
inspection. Its SHA-256 is
`AD1E4475E9462EC8E8D9BD8E2EA20C9D61C91D7E409EBE54810E6589FB49F02E`.
Findings and limits are canonical in `.agents/research/cloud-password-owner-runtime.md`.
Disposition of the retained CSVs remains with the owner.

### Graph and AD connection for this repo's survey tooling

- m365-connections-module: `D:\source\scripts\Modules\M365Connections.psm1`

**This is how Graph is connected for anything run against this tenant, not a one-off for the
2026-09-14 investigation below.** Owner, 2026-09-22: *"I don't log in to graph like this. I use
the connection module's GraphConnect."* The module exports `GraphConnect`, which authenticates the
existing app registration through its Delinea credential helper and is **app-only**, so a script
must not ask for delegated `-Scopes`; and `ADImport`, which loads the ActiveDirectory module. Do
not call `Connect-AllM365Services` for Graph-only work - it also runs module updates and opens
other service connections unless controlled.

The path above is machine-specific and lives here rather than in any script; read it the way
`tools/Get-TokenUsage.ps1` reads `transcript-root:`, and let the caller override it with a
parameter. The script that first used it has been deleted, so nothing reads this entry today - it
stays because the connection rule it records applies to whatever comes next.

### Graph connection for the owner investigation (2026-09-14)

The owner directed use of `D:\source\scripts\Modules\M365Connections.psm1` through
PTK. Import that module and call `GraphConnect`; it authenticates the existing app
`16866221-3e2b-4ea5-8aae-157b043b6d7c` through its Delinea credential helper. Do not print
credential values. The successful PTK session imported the installed Graph
Authentication module by its absolute manifest path first because the worker did not
autoload it; discover the installed path/version rather than copy a fixed version.
Only `GraphConnect` was called. `Connect-AllM365Services` also invokes module updates
and other service connections unless controlled, so it was not used for this Graph-only
work. AD queries succeeded in the native PowerShell execution environment.
The app-only Graph context was left available in PTK's default session.

### Test tooling

**Run the xUnit suite with `-- xUnit.MaxParallelThreads=4` on this box.** At full parallelism
it spreads over every core and each worker holds its own fixtures, which peaked near 27GB and
got a run killed by the harness for memory pressure on 2026-09-25. Capped, it costs nothing
measurable: re-timed 2026-10-05 as of `25bb108` at 5m08s for the full Release suite
(3579 passed / 0 failed / 3 skipped).

_Re-probed 2026-10-23._ Pester `6.2.0` (with `6.0.1`, `5.7.1` and `3.4.0` also installed; the
local gates and CI import `6.2.0` by `-RequiredVersion`, `cdt-40`), PSScriptAnalyzer `1.25.0`
(with `1.24.0`; CI pins `1.25.0`, `cdt-39`) and ActiveDirectory `1.0.1.0`. The PowerShell suites
run under `pwsh` (`C:\Program Files\PowerShell\7\pwsh.exe`), not 5.1.

### Machine-local helper scripts (not in the repo)

- `capture-exchangeadminweb-memory.ps1`, the memory capture the two dated memory sections below
  rely on, and `Test-DefenderLocationExport.ps1`, a post-deploy checker that answers the three
  Defender location unknowns from an exported CSV (validated against the owner's 2026-10-13
  export), were both recorded under `%TEMP%`. On 2026-10-23 neither was at the root of
  `C:\Users\mcoelho\AppData\Local\Temp` or of any numbered session folder under it; a deeper
  search was not completed.

### Claude Code transcript root (`tools/Get-TokenUsage.ps1`)

- transcript-root: `C:\Users\mcoelho\.claude\projects\D--source-ExchangeAdminWeb`

This project's transcripts only — the default scope the token baseline is measured on.
Point the tool at `C:\Users\mcoelho\.claude\projects` with `-TranscriptRoot` for every
local project. The script reads the first `transcript-root:` entry in this file; if a
second machine is ever recorded here, pass `-TranscriptRoot` explicitly on it.

## macOS observations (historical, not re-probed 2026-09-14)

Earlier state recorded nondeterministic xUnit collection loss from a missing Windows COM
DLL, with CI unaffected. Windows-targeting builds require `-p:EnableWindowsTargeting=true`;
Pester required `pwsh` and `DOTNET_ROOT=/opt/homebrew/opt/dotnet/libexec` on that machine.
No current macOS host or tool state was checked in this sweep.


## 2026-10-12 - the 24-hour memory reading was attempted and is NOT takeable from an unelevated shell

Owner asked for it now rather than at +24h (that window was a weekend). Taken on
`ASHBIAMWEB1` at 2026-10-12 ~08:30 host local.

**What blocked the definitive reading.** Hosting is `hostingModel="inprocess"`, so the app
runs inside `w3wp.exe` and there is no `ExchangeAdminWeb.exe` process to read - the
2026-10-09 entry's "`ExchangeAdminWeb`, PID 14212" names the APP POOL, not a process image.
Mapping a worker PID to its pool needs elevation, and all three routes refused without it:
`appcmd list wp` returns "WAS service is not available", `Import-Module WebAdministration`
returns "Process should have elevated status", and the `W3SVC_W3WP` and `Process` performance
counter sets both enumerate ZERO instances for this user while `Get-Process` lists four live
`w3wp.exe`. Treat that empty counter enumeration as a rights artefact, not as evidence that no
worker is running.

**What was readable, and it is not nothing.** All four workers, with uptime and private bytes:

| PID | Started | Uptime | Private | Working set |
|---|---|---|---|---|
| 7672 | 2026-10-06 13:14 | 139.2 h | 0.01 GB | 0.02 GB |
| 16204 | 2026-10-11 18:07 | 14.3 h | 0.03 GB | 0.07 GB |
| 5152 | 2026-10-12 04:48 | 3.7 h | 0.30 GB | 0.46 GB |
| 10812 | 2026-10-12 08:27 | new | 0.01 GB | 0.01 GB |

Every worker on the host totals under 0.35 GB private. Pre-fix prod was **15.0 GB private /
13.8 GB working set at 1.0 days**. Even assigning prod the largest of these, it is three
orders of magnitude below the pre-fix trajectory.

**Two findings that outlive the reading:**

1. **The "clock since the deploy" framing in the 2026-10-09 entry is wrong and should not be
   used again.** No worker has been up since the 2026-10-09 14:38 deploy; the longest-lived
   candidate is 14.3 hours. IIS recycles the pool on its own schedule, so a multi-day
   accumulation window was never going to exist. What the measurement needs is simply **a
   worker with ~24 hours of uptime under operator traffic**, which recurs on its own and does
   not need a deploy to start it.
2. **The reading is cheap to take correctly and needs one elevated shell.** `appcmd list wp`
   gives the pool-to-PID map; the rest is `Get-Process`. The existing
   `capture-exchangeadminweb-memory.ps1` under `%TEMP%` resolves the pool by performance
   counter, which is the route that fails unelevated - its step 1 is still the right next
   capture IF the growth is still there.

**Reading of the evidence, stated as an inference and not as proof:** nothing on this host
resembles the pre-fix leak. It is not proof, because the pool identity is unconfirmed and no
worker has 24 hours of traffic behind it. It is enough that the leak should not be treated as
live unless a later reading contradicts it.

## 2026-10-09 - prod deploy, and the memory clock restarts here

*The dev-only baseline taken earlier that day is superseded by this section and was pruned by
the 2026-10-12 drift sweep; it is verbatim in `docs/history/state-archive.md` (Archived
2026-10-12).*

Prod (`ExchangeAdminWeb`, PID 14212) and dev (PID 8748) both redeployed, each at 0.12 GB
from cold. **This build is the first in production carrying the circuit-lifetime leak fix**
(`98b29d1`, `c6c73d2`), and it also carries the Remove-completed control (`c57b41e` plus six
review fixes).

**This reading supersedes the 2026-10-09 dev-only baseline above**, which was taken when
prod was still unpatched.

**The measurement.** Pre-fix: prod reached **13.8 GB working set / 15.0 GB private after 1.0
days** (2026-10-07), against dev on the same build and uptime at 0.89 GB - the difference
was operator traffic. Take prod's private bytes after ~24 hours of normal use:

- Growth largely gone -> the circuit leak was the dominant cause and the incident closes.
- Growth continues near ~14 GB/day -> it was not, and the next step is step 0 of
  `docs/ProductionMemory-Plan.md`: the managed-versus-native counter reading, which has
  still never been taken. `capture-exchangeadminweb-memory.ps1` is under `%TEMP%` on the
  owner's machine and needs an elevated shell.

A recycle or redeploy resets this clock. Take the reading before the next one.

**Owner decision, 2026-10-09: acceptance testing happens in production.** Extended and rehomed
2026-10-12 - the decision now lives in `.agents/decisions.md`, which is its correct home; this
file keeps only the pointer. The dev manual pass in `docs/ProgressCoverage-Plan.md` and
`docs/MigrationRemoveCompleted-Plan.md` was deliberately skipped, not forgotten.
