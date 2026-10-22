# Defect review request: protected principals, module config and module admins decided under the lock; equal changes as no-ops; writers internal; unreadable secret; drain action (aud-a28n, aud-a26k)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `02115458` or later.
- The change is the branch merged at `3ef12e60`: `738cef45` (plans), `650a359b` (aud-a28n), `a4115132`
  (aud-a26k), `639d721b` (comments only), `463e00ac` (record SHAs), forked from `cc4f9c5a`. Review
  `git diff cc4f9c5a 463e00ac`, and read the COMBINED `Services/ProtectedPrincipalService.cs`
  (`RulesVersion`, `LoadRulesForEditing`, `ReplaceConfig`, `TryGetDirectoryReadSecretId`),
  `Services/Storage/ProtectedPrincipalRepository.cs`, `Services/ModuleConfigService.cs`
  (`SaveChangedValues`, `TryGetModuleConfig`), `Services/Storage/ModuleConfigRepository.cs` (`UpdateModuleIf`),
  `Services/ModuleAdminService.cs` (`ApplyAdminChanges`), `Services/Storage/ModuleAdminRepository.cs`
  (`ApplyChanges`), `Services/SectionAccessService.cs` (`SaveEditedAliases` with `AlreadyApplied`),
  `Services/AuditService.cs` (`LogConnectionPoolDrainFailed`), `Components/Pages/AdminSettings.razor`,
  `Components/Pages/ModuleConfig.razor`, `Components/Pages/ExchangeOnlineConfig.razor`,
  `docs/AdminModuleDeveloperGuide.md` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a28n.md`, `aud-a26k.md`; parents `aud-a28j.md`, `aud-a28k.md`,
  `aud-a28i.md`, `aud-a26h.md`, `aud-a33b.md`. ALREADY admitted and out of scope: `aud-a28p`
  (`GetLegacyExclusions` reads the ExcludedUsers list fail-open after the corruption probe) and
  `aud-a26n` (the EXO connection save and the secret write still overwrite unseen changes; masked
  secret fields never show a change; repository whole writers stay public; clearing the secret shows
  the fallback; two wrong comments). Do not re-raise them. Repo rules: `AGENTS.md`;
  `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3), Architectural Invariant 2;
  `docs/ProjectConstitution.md` (fail-closed authorization; audit).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 3ef12e60`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a28n: the rules version (SHA-256 over five kinds, sorted, plus the
marker - case handling; an entry differing only by case; the version carried back to the page on
`AlreadyApplied`); `AlreadyApplied` for the whole rule set (the implementer applied the a26k rule
beyond section access - is "the store already holds exactly the save" safe for protection rules);
`UpdateModuleIf` per-key decisions (a key the page does not show - `ExcludedUsers` - never written;
`StoreCorrupt` when values are unreadable under the lock; `NothingToSave` rollback); module admins
with no conflict refusal (the implementer's two-state argument - a group removed elsewhere and
re-added by this save: present, `AlreadyApplied`? correct?); the `RecountConfig` diff as the set of
changed keys. a26k: `AlreadyApplied` versus `ConcurrentChange` on section access (an alias whose
rows equal the edit but whose display names differ); the five writers made `internal` and the
tripwire's source scan (does it cover `.razor` and `Modules/`); `TryGetDirectoryReadSecretId` and the
page's `ppSecretUnreadable` (a rules-only save with the secret unreadable still writes the rules -
is the secret's presence marker touched); `LogConnectionPoolDrainFailed` category `AdminSettings`
on the Exchange Online page. Tests passing for the wrong reason: `SettingsPageHarness`; fixtures
seeding through the now-internal writers.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
