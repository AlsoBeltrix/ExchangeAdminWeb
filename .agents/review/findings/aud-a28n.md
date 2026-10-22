# aud-a28n: Protected principals, module config and module admins are still replaced wholesale from the page's state

**Severity**: MEDIUM - the aud-a28i / aud-a28k class on three more authorization-adjacent stores
under the shared config database (Architectural Invariant 2). (1) The protected-principals save
replaces the whole rule set from the page's lists: a page holding at least one rule removes a rule
another admin or the other instance added since it loaded; aud-a28j refuses only the
empty-over-non-empty case and records the rest. (2) `ModuleConfig.SaveConfigAsync` replaces a
module's whole config, and `SaveModuleAdminsAsync` the whole admin list, from the page's state, with
no compare against the rows under the lock.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28j/a28k/a26h agent, 2026-10-22, candidates 1 and 2 of its hand-back)

## Evidence

At the aud-a28j/a28k/a26h branch tip `ade2a1d7`: `Services/ProtectedPrincipalService.cs`
`ReplaceConfig` (decides inside one locked write since aud-a28j, but replaces all rules);
`Components/Pages/ModuleConfig.razor` `SaveConfigAsync`, `SaveModuleAdminsAsync`. Compare aud-a28i's
content-version compare-and-swap and aud-a28k's per-row decisions under the lock.

## Approach (to be written by the implementing agent before code)

- Protected principals: a content version (SHA-256 over the rules, aud-a28i shape) captured at load
  and checked inside the locked write; a mismatch refuses `ConcurrentChange` (audited,
  `mutationAttempted: false`, reload and re-apply). Keep aud-a28j's refusals.
- Module config: write only the keys the admin changed, each compared to the row under the lock, or
  a content version over the module's config; module admins: per-entry decisions under the lock
  (added/removed entries compared against current rows), refusing on a concurrent change naming the
  entry. Nothing merged or guessed.
- Tests in the aud-a33b two-stores style: a rule added elsewhere between load and save survives (or
  the save is refused as a conflict); a config key changed elsewhere is not reverted; an admin added
  elsewhere is kept. Pages `NotYetConverted` in `ClickGateRegistry`; confirm. Mutations at the call
  site: drop the version check; write the whole config again; write the whole admin list again.

## Guard proof

Pending.
