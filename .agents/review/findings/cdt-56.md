# cdt-56: the 8.3 test's positive branch is bypassed by an if, not a skip, when no short name can be made

**Severity**: LOW - guard weakness. `tests/ps/AppPoolControl.Tests.ps1` about 1236, 1241, 1276: when `fsutil file setshortname` fails (not elevated, 8.3 disabled) and Program Files has no short alias, `` is false and the real-8.3 assertions are skipped by an `if`; the suite reports green with 0 skipped while only the synthetic case ran.
**Status**: Open - admitted 2026-10-23 evening from codex ops-batch-14 on cdt-47..51; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 42c2690b head 7f1890f3 (merged fb2b288f); 2026-10-23 evening; capability proof succeeded (AGENTS.md read, git show --stat 7f1890f3)

## Evidence

Codex ops-batch-14 finding 2 at `fb2b288f`.

## Approach

- Fail the test when no real short spelling can be created or found (name the cause), rather than bypassing the positive branch; keep the suite-integrity guard; mutation: the if restored.

## Guard proof

Pending.