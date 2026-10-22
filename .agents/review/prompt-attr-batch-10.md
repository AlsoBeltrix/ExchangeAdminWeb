# Defect review request: the protection check's exclusions read fail-closed in one read; the remaining settings writes decide per key (aud-a28p, aud-a26n)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `2318cd5e` or later.
- The change is the branch merged at `7b65d27e`: `ff78ff27` (plans), `0b92c35f` (aud-a28p), `bec91eeb`
  (aud-a26n, parts 1-8), `389a34a2` (comments only), `a904c288` (record SHAs), forked from `1db00236`.
  Review `git diff 1db00236 a904c288`, and read the COMBINED `Services/ProtectedPrincipalService.cs`
  (`LoadEffectiveConfig`, `TryGetLegacyExclusions`, `CheckAsync`), `Services/ModuleConfigService.cs`
  (`SaveChangedValues`, `changedSecretKeys` masking), `Components/Pages/AdminSettings.razor`
  (secret compare, fallback caption, `AlreadyApplied` rehydration), `Components/Pages/ExchangeOnlineConfig.razor`
  (per-key save, Save withheld when unreadable), `Components/Pages/ModuleConfig.razor` (display names
  written only for added groups), the three repositories' now-internal writers, `docs/AdminModuleDeveloperGuide.md`
  on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a28p.md`, `aud-a26n.md` (eight parts); parents `aud-a28n.md`,
  `aud-a26k.md`, `aud-a28j.md`, `aud-a28k.md`, `aud-a3b.md`, `aud-a3c.md`. ALREADY admitted and out of
  scope: `aud-a28r` (a Secret ID refused under the lock leaves the rules saved and the secret not) and
  `aud-a26p` (Secret ID unvalidated; EXO fallback-sourced fields unmarked; a casing-only edit not dirty;
  parallel PowerShell-session test classes race on start-up). Do not re-raise them. Repo rules:
  `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3), Architectural Invariant 2;
  `docs/ProjectConstitution.md` (fail-closed authorization; audit).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 7b65d27e`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a28p: the one read replacing the probe - every consumer of
`LoadEffectiveConfig` (the gates, `CheckWriteTarget`, `BlockedSenderProtectionGate`,
`PermissionValidator`, Admin Settings' load gate) still refuses on an unreadable store; the cache-hit
path (does a cached rules list ever pair with a fresh exclusions read that fails - what then); the
decision not to add a `ProtectionRead` step name. a26n: per-key EXO save (a field on the fallback not
edited stays on the fallback - and is the fallback ever written to the store by mistake); the secret
compare after the rules write (out of scope as a28r, but check the two records' wording when it
refuses); `changedSecretKeys` never leaking a value; the display-name rule (names stored only for
groups "added and picked from the directory" - a group added by SID paste); repository writers
internal and the tripwire's reach; `AlreadyApplied` rehydration from `outcome.After`; the two
comparer comments. Tests passing for the wrong reason: the module-config store that answers N reads
then fails; `SettingsPageHarness`.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
