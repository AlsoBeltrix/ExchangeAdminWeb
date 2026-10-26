# cons-1: three modules changed behaviour in the consolidation without a version bump

**Severity**: LOW - traceability. Over `00798adb..8f55802a` Mailbox Permissions (ticket and refusal handling,
kd-18/kd-40), Migration (audit, search and denial behaviour, kd-33..kd-36) and Event Log (guarded failure
reporting, aud-a64) changed while their `Modules/ModuleCatalog.cs` versions stayed at 1.5.0, 1.24.0 and 1.3.2;
each page renders `<ModuleVersion />`, so operators would see the old number. Conference Rooms was bumped
correctly. Caused by the agents being cut off before their closing bump commits, not by any of their code.
**Status**: Complete - fixed by the supervisor at the commit this record lands in; Mailbox Permissions 1.6.0,
Migration 1.25.0, Event Log 1.4.0, each with a note naming its findings
**Branch**: - (direct to master)
**Commit**: the `fix(cons-1)` commit this record lands in
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; range 00798adb..8f55802a; 2026-10-26; capability proof succeeded (AGENTS.md read, git show --stat 8f55802a)

## Evidence

Codex consolidation-1, the single finding, at `8f55802a`: `Modules/ModuleCatalog.cs` 168, 417 and 1470.

## Approach

Catalog only: one note and one minor step per module. The base app version is deliberately NOT bumped here -
several shared services changed across the whole stream (`TicketPresence`, `ModuleCredentialService`,
`ADDirectorySearchService`, the revalidator, the log services, `ModuleConfigService`) and one base bump is
owed at the stream's close, recorded in `.agents/state.md`.

## Guard proof

Not applicable: version metadata, no behaviour. The Release build is 0 errors, `dotnet format` and the ASCII
lint are clean, and the full suite was green at `8f55802a` before this change, which touches no code path.

## Reviewer comments

codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; range 00798adb..8f55802a; 2026-10-26; capability proof succeeded (AGENTS.md read, git show --stat 8f55802a). One finding across the whole consolidation range, this one. Codex confirmed clean: the kd-52 access
refusal audits once; the Mailbox / Calendar asymmetry breaks no shared test or helper path it checked; the
Migration throwing write audits once with the expected Failed / Undetermined split; the denied bulk targets are
bounded with a count and a truncation marker; the search-route withdrawal covers both branches; the cdt-54
enumeration warns rather than blocking unless the existing binding checks would already refuse. Artefacts:
`.agents/review/consolidation-1.result.txt`, `.stream.log`, `prompt-consolidation-1.md`.