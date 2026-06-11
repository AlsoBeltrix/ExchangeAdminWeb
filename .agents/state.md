# Agent State

First place to read for current repo state. Keep it short; update it when important
repo facts change.

## Now

- App version `2.3.5` (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`).
- **Committed** (local, ahead of `origin/master`, not pushed):
  - `00054a8` CI wiring: `ci.yml` → `.github/workflows/`, trigger `main`→`master`.
  - `54c07da` Conference Rooms room lists grouped by **Building** not City (module `2.0.3`).
  - `c8ed096` Synced-room Set-Mailbox + shared base error-bleed fix (app `2.3.5`).
- **Uncommitted work in tree** (one change, awaiting owner's commit decision):
  - Conference Rooms config extraction (module `2.0.3`→`2.0.4`). Removed all ADI
    `@analog.com` fallbacks from `ConferenceRoomService.cs`; real values moved to
    deploy-path `config/module-config-ConferenceRooms.json` (dev instance, not in repo).
    Added fail-closed preflight (`FindMissingRequiredGroups`) so Set Room Type aborts when
    required groups are unconfigured; committed placeholder example config; sanitized catalog
    description hints. Plan: `docs/ConferenceRooms-ConfigExtraction-Plan.md` (Implemented).
    370/370 tests pass; guard-revert verified. Manual EXO check not run.
  - Note: `ModuleCatalog.cs:404` still has a hardcoded `analog.com` UPN-suffix example in the
    **Test Account Pool** module (out of scope for this Conference Rooms task) — flag for a
    future cleanup.
- Latest landed work before this batch (commit `7e7c9ac`): Conference Rooms bulk CSV upload
  `@`-gate fix + versioning-governance gap closed.

## Findings

- **CI now wired up (commit pending).** The workflow file was moved from the repo root
  to `.github/workflows/ci.yml` (where GitHub Actions reads it) and its push trigger
  corrected from `branches: [main]` to `branches: [master]`. The
  build+test+PSScriptAnalyzer+Pester pipeline should run on push/PR once this lands on
  the remote. First push will confirm it actually executes — until a run is observed,
  continue to treat verification as local-only.

## Next

- None recorded. Awaiting the next request from the owner.

## Blockers

- None recorded.

## Verification

- CI has been wired up but not yet observed running (see Findings). Until a remote run
  is confirmed, treat verification as local-only and run the commands yourself before
  claiming completion.
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
