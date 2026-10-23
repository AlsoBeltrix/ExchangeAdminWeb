# Defect review request: the self-grant check identifies the operator by SID through the forest catalog, compares against the directory on both paths including group membership, the string-only synchronous check is removed (aud-a43); the pool's borrow fails closed on an unreadable enablement store and the pages' audit-failure banner carries the validator's refusal (aud-a44)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `d4bb2efc` or later.
- The change is the branch merged at `fce22146`: `1272a8cf` (plans), `4cc5756b` (aud-a43), `8a90e8fa` (aud-a44),
  `bce8d047` (comments only), `01662a8f` (record SHAs), forked from `1410013e`. Review `git diff 1410013e 01662a8f`,
  and read the COMBINED `Services/PermissionValidator.cs` (`ValidateSelfGrantAsync` taking the principal; the
  operator identity; the directory comparison; the group branch; the once-guard), `Services/SelfGrantDirectory.cs`
  (new: the SID lookup through the forest catalog, the grantee search by upn/mail/proxy/alias/sAMAccountName/name/DN,
  the member-link read in the group's domain, the primary group, `DefaultRunspace`, the strict row parsing),
  `Services/ExoConnectionPool.cs` (`BorrowAsync` through `TryGetModuleEnabled`, `UnreadableEnablement`),
  `Components/Pages/MailboxPermissions.razor` 431 and 450-451, `Components/Pages/CalendarPermissions.razor` 406 and
  420-421, `Services/MailboxPermissionService.cs` and `Services/CalendarPermissionService.cs` (the bulk loops passing
  `actingUser`), and the tests `PermissionValidatorSelfGrantTests.cs`, `PermissionPagesSelfGrantTests.cs`,
  `SelfGrantDirectoryStandIn.cs`, `ExoConnectionPoolConfigReadTests.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a43.md`, `aud-a44.md`; parents `aud-a28y.md`, `aud-a28x.md`, `aud-a28t.md`,
  `aud-a2d.md`, `aud-a23r.md`; `.agents/decisions.md` 2026-07-31 (directory reads under the app pool identity) and
  2026-09-11 (Invariant 7). ALREADY admitted and out of scope: `aud-a50` (the on-prem write not re-checked for
  self-grant right before writing; the stored-value warning flood; blank-User bulk rows misrecorded; the audit
  methods returning void; no `ServiceEnablement` text). Do not re-raise them. Repo rules: `AGENTS.md`;
  `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3) and Architectural Invariant 7;
  `docs/ProjectConstitution.md` (authorization; "OU and group checks must be based on resolved directory objects").

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat fce22146`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a43: the operator found by SID through the GC - `Get-ADForest` to discover the GC at
runtime: what happens when the forest call itself fails (refuse - confirm), and does anything cache the GC name
across checks (a stale GC after a DC retirement); the grantee search by seven attributes plus Exchange's canonical
address - a search that matches TWO objects (an alias equal to another user's sAMAccountName): refused as
ambiguous, or the first taken (fail-open); `smtp:` proxy matching - case; the group branch reads `member` in the
group's own domain - a group whose members are foreign security principals (cross-forest) or whose membership is
nested: the record says nested is a known gap - confirm the record and README say so where an operator reads it;
the primary group check - by `primaryGroupID` vs the group's RID: correct domain; "a group Exchange names that the
directory doesn't hold (cloud-only) refuses as incomplete" - does that block every grant to a cloud-only group
(Microsoft 365 groups are cloud-only by nature) - if so that is a functional block worth naming with severity; a
fresh runspace and AD module import per check, once per bulk row - cost (name the per-row time if the tests show
it) and whether the EXO connection pool's throttle also serialises it; the principal passed from the pages
(`validationAuthState.User`) - the same principal the authorization check used, or a stale one; null principal in
the bulk loops refusing grant rows - is a REVOKE row with a null principal still allowed (should it be). a44: the
unreadable-enablement exception type reused (`ExoConfigUnreadableException`) - any catch that now shows the config
text for an enablement failure; the pages' catch logging the exception - PII in the message. Tests passing for the
wrong reason: `SelfGrantDirectoryStandIn` vs the real cmdlets' parameter binding (`-Server <gc>:3268`,
`-LDAPFilter`, `-Properties`); the "whole suite with DefaultRunspace rigged to throw" proof - does it also prove no
test reaches a live directory through another seam.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
