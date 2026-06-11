# Agent State

First place to read for current repo state. Keep it short; update it when important
repo facts change.

## Now

- No active feature work in progress.
- App version `2.3.4` (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`).
- Latest landed work (commit `7e7c9ac`): Conference Rooms bulk CSV upload fix +
  versioning-governance gap closed.

## Findings

- **CI is not running.** The workflow file `ci.yml` is in the repo root, but GitHub
  Actions only runs workflows under `.github/workflows/`. There is no `.github/`
  directory, so the build+test+PSScriptAnalyzer+Pester pipeline has never executed on
  push or PR. The file also targets `branches: [main]` while the repo's branch is
  `master`. Until this is fixed, verification is local-only — run the commands yourself.
  Fixing it (moving the file and correcting the branch) is a build-pipeline change and
  is out of scope for this governance migration; raise it as its own task.

## Next

- None recorded. Awaiting the next request from the owner.

## Blockers

- None recorded.

## Verification

- Verification is local-only — there is no working CI (see Findings). Run the commands
  yourself before claiming completion.
- Code changes: `dotnet build -c Release` then `dotnet test`. Add
  `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore` and
  `git diff --check HEAD` where practical.
- PowerShell changes: `Invoke-ScriptAnalyzer -Path . -Recurse` and `Invoke-Pester tests/ps`.
- Full policy and the manual-check list live in `.agents/repo-map.json` and `AGENTS.md`.

## Active Sources

- `AGENTS.md`
- `docs/ProjectConstitution.md` (highest engineering authority)
- `.agents/repo-map.json`
- `.agents/decisions.md`

## Unrecorded Repo Memory

- None known. Engineering rules live in `docs/ProjectConstitution.md`; module contract
  in `docs/AdminModuleSpec.md`; work-stream history in `docs/*-Plan.md`.
