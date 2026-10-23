# cdt-46: ConvertTo-ComparablePhysicalPath does not resolve 8.3 short names or junctions, so the same folder can read as a move

**Severity**: LOW - fails closed. IIS may store `physicalPath` as an 8.3 spelling (`D:\INETPU~1\EXCHAN~1`) or a junction path while `-PublishPath` names the same directory in long or final form; `tools/AppPoolControl.psm1` about 268 expands environment variables, normalises slashes, dot segments and trailing separators but not short names or final paths, so the cdt-36 guard (and `Assert-AppPoolServesPath`, which shares the helper) treats the same folder as a move and demands `-AllowRepoint` with a misleading "MOVES it" warning.
**Status**: Open - admitted 2026-10-23 evening from codex ops-batch-12 on cdt-36; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base eb017102 head 493c270d (merged 72430407); 2026-10-23T19:06-19:13Z; capability proof: AGENTS.md read and git show --stat 493c270d both succeeded

## Evidence

Codex ops-batch-12 finding 3 at `72430407`.

## Approach (to be written by the implementing agent before code)

- Where both paths exist on disk, canonicalise to the long-name / final path (`GetFinalPathNameByHandle` semantics or `(Get-Item -LiteralPath).FullName` after short-name expansion) before comparing; keep the helper shared; if canonicalisation cannot be proven (a path that does not exist yet), keep failing closed and say which spelling differed in the message. Pester with a short-name fixture where the host supports 8.3 (skip with a reason where it does not), and a junction fixture.
- Mutation: the canonicalisation removed.

## Guard proof

Pending.