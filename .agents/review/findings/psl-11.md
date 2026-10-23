# psl-11: the validate-exists object-kind theory now proves only the unavailable stand-in path

**Severity**: LOW - coverage false confidence. `ExchangeAdminWeb.Tests/ADDirectoryValidateExistsTests.cs` about 245: `Search_AllObjectKindsIncludingOu_DoNotThrow` uses `ADDirectorySearchServiceTests.CreateService()`, whose helper asserts AD is unavailable, so `Search` returns at the availability guard before `ExecuteSearch`; a live-only regression in the User/Group/OU command path (the `Get-ADOrganizationalUnit` branch especially) ships green.
**Status**: Open - admitted 2026-10-23 evening from codex cr-batch-8 on aud-a2y/kd-5/psl-10; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 3e43b35b head f7b794cb (merged 07f2d371); 2026-10-23 evening; capability proof succeeded (AGENTS.md read, git show --stat f7b794cb)

## Evidence

Codex cr-batch-8 finding 2 at `07f2d371`.

## Approach

- An available non-live stand-in runspace with fake `Get-ADUser`, `Get-ADGroup`, `Get-ADOrganizationalUnit` (the psl-8 / `CreateDirectoryRunspace` seam) asserting each object kind reaches its command; real directory coverage stays opt-in (psl-10 (5)); mutation: one kind's command removed.

## Guard proof

Pending.