# cons-4: an out-of-order module load can restore an older module after a newer route has won

**Severity**: MEDIUM - a wrong privileged module's configuration can be displayed and edited after rapid navigation or a
slow authorization read. `Components/Pages/ModuleConfig.razor` about 1106 to 1141: start loading module B, route to
module C while B is still awaiting its authorization, let C finish, then let B resume; `module = next` and
`LoadState()` apply B's state although the route is C. `loadedModuleId` is set earlier in the bookkeeping, and
nothing compares the request with the latest one immediately before the state is applied. aud-a67 (9) and (10) closed
the double-load and the OU-answer cases; this ordering is a third.
**Status**: Open - admitted 2026-10-26 from codex settings-batch-8 on aud-a64/aud-a67; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 620e9d5a head f08a664a (merged fe67668d, with d0fc4c70); 2026-10-26; capability proof succeeded (AGENTS.md read, git show --stat f08a664a)

## Evidence

Codex settings-batch-8 finding 1 at `fe67668d`. The existing test mutates `ModuleId` during one load but never starts
and completes the newer load, so it passes while this order stays untested.

## Approach (to be written by the implementing agent before code)

- A load generation, or a comparison of the current `ModuleId` with the one this load requested, immediately before
  `module` is assigned, before `LoadState()`, before any navigation and before the final messages; a stale load is
  abandoned silently. Test: hold B, open and complete C, then release B and assert C's state survives and B's load wrote
  nothing. Mutation: the comparison removed, which must fail that test.

## Guard proof

Pending.