# Agent State

First place to read for current repo state. Keep it short; update it when important
repo facts change.

## Now

- App version `2.3.5` (`<VersionPrefix>` in `ExchangeAdminWeb.csproj`).
- **Uncommitted work in tree** (three independent changes, awaiting owner's commit decision):
  1. CI wiring: `ci.yml` moved to `.github/workflows/ci.yml`, push trigger `main`→`master`.
  2. Conference Rooms: room lists now grouped by **Building**, not City (module `2.0.2`→`2.0.3`).
     Plan: `docs/ConferenceRooms-BuildingRoomList-Plan.md` (Implemented). Manual EXO check not run.
  3. Conference Rooms synced-room Set-Mailbox + shared base error-bleed fix (app `2.3.5`,
     module `2.0.3`). Plan: `docs/ConferenceRooms-SyncedRoomSetMailbox-Plan.md` (Implemented,
     rev 2 after external review). Base `ExchangeServiceBase.Invoke` now clears the pipeline
     before throwing; synced-room cloud write is best-effort with success coupled to the
     on-prem write. 365/365 tests pass; guard-revert verified. Manual EXO check not run.
- Latest landed work (commit `7e7c9ac`): Conference Rooms bulk CSV upload `@`-gate fix +
  versioning-governance gap closed.

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
