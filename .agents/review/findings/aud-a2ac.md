# aud-a2ac: an ambiguous room-list lookup is treated as a missing list

**Severity**: MEDIUM - a fail-closed gap. `Services/ConferenceRoomService.cs` about 402 (`FindRoomList`) resolves the canonical and legacy names with `Get-DistributionGroup -ErrorAction SilentlyContinue` through `ExchangeServiceBase.InvokeOptional` (about 326), which returns the empty collection and clears the error stream on a non-terminating ambiguity or lookup error. The row then falls through to the legacy name or to `Exists=false` and can proceed to `Set-Place`, write a legacy list, or try to create a list instead of refusing. Trigger: an ambiguous identity (two groups answer the name) or a lookup error. Predicted failure: a write against the wrong list or a partial row.
**Status**: Open - admitted 2026-10-23 evening from codex cr-batch-7 on aud-a2e; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base eb017102 head ebd66b91 (merged 3e43b35b); 2026-10-23 ~20:0xZ; capability proof: AGENTS.md read and git show --stat ebd66b91 both succeeded

## Evidence

Codex cr-batch-7 finding 2 at `3e43b35b`.

## Approach (to be written by the implementing agent before code)

- Resolve lists with `-ErrorAction Stop`; treat only a confirmed ObjectNotFound as absence; any other error or an ambiguity is a preflight refusal before any mutation ("Nothing was written."). Tests: an ambiguous stand-in answer refuses; a not-found proceeds as absent; mutation: the ambiguity branch collapsed back to absent.

## Guard proof

Pending.