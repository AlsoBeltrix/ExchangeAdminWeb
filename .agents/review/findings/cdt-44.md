# cdt-44: -AllowRepoint takes the verified config backup from the NEW -PublishPath, not from the folder the live application serves

**Severity**: MEDIUM - with `deploy.ps1 -Force -AllowRepoint -PublishPath <new>` where the new folder has no `appsettings.json`, the cdt-36 guard warns and proceeds, but the backup and config carry-forward read from the new path (`deploy.ps1` about 893, 900, 916), so the OLD live `appsettings.json` and its `ConfigStore:Path` database are neither verified-backed-up nor carried; the app is then re-pointed (about 947-948) and gets generated appsettings (about 1103), likely without the old `ConfigStore:Path`. Explicit consent lowers the likelihood; the impact is a live app moved onto a fresh or empty config store with no verified backup of the store it was serving (Architectural Invariant 3).
**Status**: Open - admitted 2026-10-23 evening from codex ops-batch-12 on cdt-36; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base eb017102 head 493c270d (merged 72430407); 2026-10-23T19:06-19:13Z; capability proof: AGENTS.md read and git show --stat 493c270d both succeeded

## Evidence

Codex ops-batch-12 finding 1 at `72430407`. Trigger and predicted failure as above.

## Approach (to be written by the implementing agent before code)

- When an existing application is found and `-AllowRepoint` is given, derive the pre-deploy config path from the application's EXISTING physical path (the one IIS serves), take the verified backup from that live target, and carry its `ConfigStore:Path` into the regenerated appsettings, or refuse the move unless an explicit config-store value is supplied. Pester proves the backup source is the old folder.
- Mutation: the backup source back to `-PublishPath`.

## Guard proof

Pending.