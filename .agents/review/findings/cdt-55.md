# cdt-55: with the serving appsettings.json missing and no old default database, a move still drops a live shared ConfigStore

**Severity**: MEDIUM - the cdt-51 class, Architectural Invariant 3. `deploy.ps1` about 337 and 994, `Program.cs` about 59: an existing application whose process started with `ConfigStore:Path` on a shared database, whose `appsettings.json` was then deleted while it ran, and whose old folder has no default `config\exchangeadmin.db`, passes the cdt-51 guard ("nothing stranded"); the run skips backup and carry-forward, generates appsettings without `ConfigStore:Path`, and the restarted app opens a new default database. The shared one still exists but the app silently leaves it, with no verified pre-deploy backup of the store it was using.
**Status**: Open - admitted 2026-10-23 evening from codex ops-batch-14 on cdt-47..51; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 42c2690b head 7f1890f3 (merged fb2b288f); 2026-10-23 evening; capability proof succeeded (AGENTS.md read, git show --stat 7f1890f3)

## Evidence

Codex ops-batch-14 finding 1 at `fb2b288f`.

## Approach (to be written by the implementing agent before code)

- For an EXISTING IIS application, a missing serving `appsettings.json` is fail-closed regardless of the old default database: refuse with an actionable message (restore the file, supply or carry the config-store path explicitly, or move by hand). Pester; mutation: the fail-closed branch removed.

## Guard proof

Pending.