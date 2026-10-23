# cdt-45: a run the cdt-36 check refuses has already created the backup folder and copied appsettings.json

**Severity**: LOW - `deploy.ps1 -Force` with an `appsettings.json` under the run's `-PublishPath` creates `` (about 259) and copies `appsettings.json` (about 268) before the IIS comparison at about 296; the run then refuses with "Nothing was deployed" while filesystem artefacts remain, possibly for the wrong folder. Nothing in IIS moves; the "read-only check first" expectation is broken and misleading residue is left.
**Status**: Open - admitted 2026-10-23 evening from codex ops-batch-12 on cdt-36; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base eb017102 head 493c270d (merged 72430407); 2026-10-23T19:06-19:13Z; capability proof: AGENTS.md read and git show --stat 493c270d both succeeded

## Evidence

Codex ops-batch-12 finding 2 at `72430407`. Lines as above.

## Approach (to be written by the implementing agent before code)

- Move the cdt-36 IIS read and comparison ahead of `` creation and the pre-force appsettings copy (and ahead of anything else that writes), so the refusal truly precedes every write; extend the structural-order Pester test to cover these two writes.
- Mutation: the check moved back below the copy.

## Guard proof

Pending.