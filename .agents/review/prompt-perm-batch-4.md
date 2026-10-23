# Defect review request: the self-grant check refuses when Exchange cannot answer and never throws on a malformed setting (aud-a28y); the Exchange Online pool reads its rows once and fails closed, one fallback-rule owner (aud-a28x); service remedy text and validator test hygiene (aud-a26t)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `a82ca336` or later.
- The change is the branch merged at `448b02c8`: `7e65ce6a` (plans), `b5ab1859` (aud-a28y), `8ee81571` (a merge of
  master `8151de3e` into the branch - not authored there), `da615264` (aud-a28x plus the aud-a26s pool follow-up),
  `7c7c1d59` (aud-a26t), `5c32669e` (comments only), `0c9e67d5` (record SHAs), forked from `18530b58`. Review
  `git diff 18530b58 0c9e67d5 -- . ':!.agents'` MINUS what `8ee81571` brought from master (compare against
  `git diff 18530b58 8151de3e` where unsure), and read the COMBINED `Services/PermissionValidator.cs`
  (`ValidateSelfGrantAsync`, `LookUpRecipientAsync`, `SameRecipient`, `SelfGrantCheckIncomplete`, `GetPreventSelfGrant`),
  `Services/IIdentityResolver.cs`, `Services/ExchangeIdentityResolver.cs`, `Services/ExoConnectionPool.cs` (the single
  `TryGetModuleConfig` read, `ExoConfigUnreadableException`, `BorrowAsync`, `DrainPool`, `IsConfigured`,
  `TryGetIsConfigured`, the `ExoConfigFallback.Resolve` calls), `Services/SettingsStoreText.cs`
  (`ServiceDatabaseRemedy`, `ServiceModuleConfig`), the four `*_SelfGrantAttempt` call sites
  (`Components/Pages/MailboxPermissions.razor`, `Components/Pages/CalendarPermissions.razor`,
  `Services/MailboxPermissionService.cs`, `Services/CalendarPermissionService.cs`), and the tests
  `PermissionValidatorSelfGrantTests.cs`, `ExoConnectionPoolConfigReadTests.cs`, `StoreTextSurfaceTests.cs`,
  `PermissionValidatorTests.cs` (the `IDisposable` shape) on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a28y.md`, `aud-a28x.md`, `aud-a26t.md`; parents `aud-a28t.md`, `aud-a26q.md`,
  `aud-a26s.md`, `aud-a2b.md`, `aud-a6b.md`. ALREADY admitted and out of scope: `aud-a43` (the operator reaches Exchange
  as the domain-qualified Windows logon name; the on-prem path compares by string only; no group expansion; the
  synchronous string-only check remains), `aud-a44` (`BorrowAsync` reads enablement through the fail-open
  `IsModuleEnabled`; the pages' self-grant audit-failure banner wording and raw exception). Do not re-raise them. Repo
  rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3); `docs/ProjectConstitution.md`
  (credential isolation; "Legacy appsettings fallback is allowed only ... when the module-specific config is absent").

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 448b02c8`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a28y: `SameRecipient` - a shared non-blank object id OR the same primary SMTP: can two
distinct recipients share a primary SMTP in any Exchange shape (a mail contact and a mailbox, a soft-deleted mailbox)
- a false self-grant refusal is safe, a false allow is not: find any; "Exchange reports no such recipient is an answer,
the string match decides" - with the operator passed as the logon name (aud-a43 covers the identity form) is there a
path where BOTH sides come back "no such recipient" so the check silently degrades to the string match while the
module is healthy - name it if it is not exactly aud-a43; the removal of `ResolveToObjectIdAsync` - any remaining
caller in a page or a job (the build passed, so none compiles - check reflection/strings); `GetPreventSelfGrant` with an
unparseable appsettings value -> true with a warning: is the warning emitted once or on every check (log flood); the
four `errorDetail: selfGrantError` edits - same-line, so the page line counts hold - confirm `ClickGateRegistry`
entries for those pages point at unchanged lines. a28x: the single read through `TryGetModuleConfig` - a store that
is readable but returns no rows (fresh install) is "absent" and falls back (allowed) - is a store whose TABLE is
missing (schema not yet migrated) read as unreadable (refuse) or as absent (fall back); `ExoConfigUnreadableException`
thrown from `BorrowAsync` before a slot - every borrower's catch: does any treat an `InvalidOperationException` as
"not configured" and show the wrong text; `DrainPool` throwing before retiring - the Exchange Online Config page's
save path (aud-a26p/a28r) when the drain throws after a saved connection: is the save reported as saved; `IsConfigured`
as `TryGetIsConfigured && c` - any caller that showed "not configured" and now cannot distinguish unreadable (the
record says callers can use `TryGetIsConfigured`; find a caller that should and does not). a26t: `IDisposable` test
class with `Dispose` clearing SQLite pools and deleting directories - a test that fails mid-way leaves what; the
tripwire's new `Audit:LogRoot` rule - a value built from a variable that holds the temp path escapes it (name the
shape, as before). Tests passing for the wrong reason: the stand-in resolvers in `PermissionValidatorSelfGrantTests`
vs `ExchangeIdentityResolver`'s real null/throw contract; the source checks (comment-blanked) as the only guard for
the four call sites.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
