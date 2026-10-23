# cdt-51: -AllowRepoint strands an old default config database when the old appsettings.json is gone

**Severity**: MEDIUM - Architectural Invariant 3. `deploy.ps1` about 333 and 983: the cdt-44 stranded-database guard and the fresh-install backup are both gated on `Test-Path` of the old folder's `appsettings.json`. With the IIS app serving `<old>` whose `appsettings.json` is missing but whose `config\exchangeadmin.db` still exists, `-ConfirmFreshInstall -AllowRepoint` (or `-Force -AllowRepoint`) passes the move check, skips both, re-points IIS to `<new>` and writes new appsettings without `ConfigStore:Path`, so the app opens a fresh default database while the old operational one is left behind, not verified-backed-up.
**Status**: Open - admitted 2026-10-23 evening from codex ops-batch-13 on cdt-42..46; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 493c270d head 42c2690b (merged 0c9917d5); 2026-10-23 evening; capability proof: AGENTS.md read and git show --stat 42c2690b both succeeded

## Evidence

Codex ops-batch-13 finding 1 at `0c9917d5`.

## Approach (to be written by the implementing agent before code)

- For a folder move, treat "no old appsettings" like "appsettings names no `ConfigStore:Path`": compute the old default database path and, if it exists, refuse before any write with an actionable message (restore appsettings or move the database to a shared path first). With neither an old appsettings nor an old default database, proceed as the half-removed case intends. Pester for both; mutation: the default-path check removed.

## Guard proof

Pending.