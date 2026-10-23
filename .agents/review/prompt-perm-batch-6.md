# Defect review request: the on-prem write re-checks for a self-grant, blank-User bulk rows are invalid rows, the stored-setting warning fires once, the pages learn the refusal's audit outcome, the pool names an unreadable enablement store (aud-a50); nested group membership is detected and cloud-only groups are checked in Exchange (aud-a52)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `0aad2d07` or later.
- The change is the branch merged at `81809833`: `1d7346ca` and `43c47893` (plans), `fcac67d7` (a50 part 1),
  `29d9b16f` (part 3), `b1ec0b5d` (part 2), `f7921ce8` (aud-a52), `43a82568` (part 4), `0e00ab23` (part 5), `dcfec67c`
  (comments only), `790edb8d` (record SHAs), forked from `132d6e97`. Review `git diff 132d6e97 790edb8d`, and read the
  COMBINED `Services/PermissionValidator.cs` (`ValidateSelfGrantAsync`, `CompareInDirectory`, the group branch calling
  `ProtectedGroupMembership.Evaluate`, the cloud-only path through `ReadGroupMembersAsync`, `GetPreventSelfGrant`),
  `Services/SelfGrantDirectory.cs`, `Services/SelfGrantRefusalRecord.cs` (new), `Services/ExchangeGroupMembers.cs` (new:
  `Get-Recipient`, `Get-UnifiedGroupLinks`, `Get-DistributionGroupMember`, the nested bound of 50),
  `Services/PowerShellProtectedGroupDirectory.cs` (`UnderSessionIdentity`), `Services/IIdentityResolver.cs` and
  `Services/ExchangeIdentityResolver.cs` (`ReadGroupMembersAsync`), `Services/AuditService.cs` (`WriteAuditEvent` with
  the `out` outcome; the two methods returning it), `Services/SettingsStoreText.cs` (`ServiceEnablement`),
  `Services/ExoConnectionPool.cs` (`UnreadableEnablement`), `Services/MailboxPermissionService.cs` and
  `Services/CalendarPermissionService.cs` (the blank-User rows), `Components/Pages/MailboxPermissions.razor` 704 and
  443-446, `Components/Pages/CalendarPermissions.razor` 658 and 413-416, and the tests `PermissionPagesSelfGrantTests.cs`,
  `PermissionValidatorSelfGrantTests.cs`, `SelfGrantDirectoryStandIn.cs`, `ExchangeGroupMembersTests.cs`,
  `ProtectedGroupMembershipTests.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a50.md`, `aud-a52.md`; parents `aud-a43.md`, `aud-a44.md`, `aud-a28y.md`,
  `aud-a28u.md`, `aud-a26t.md`; `.agents/decisions.md` 2026-07-31 and 2026-09-11. ALREADY admitted and out of scope:
  `aud-a53` (the SubmitSingle catch's wording and unguarded notifications), `aud-a59` (the protected-target denial's
  audit outcome ignored; `ExecuteOnPrem`'s catch shows a raw exception; a blank-Target bulk row misfiled; the harness
  stub's default group members). Do not re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known
  Failure Classes" (1, 2, 3) and Architectural Invariant 7; `docs/ProjectConstitution.md` (authorization; "OU and
  group checks must be based on resolved directory objects").

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 81809833`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a50 part 1: the re-check in `ExecuteOnPrem` uses `onPremAuthState.User` - the principal
captured when the write was staged or re-read at Continue; a refusal there writes the SubmitSingle record - is the
operator shown that the EARLIER check passed and this one refused (state changed in between), or just the refusal;
`SelfGrantRefusalRecord.Banner` taking a `Func<JsonlWriteOutcome>` - a throw inside the Func. Part 3: the blank-User
check before the target check - a REVOKE row with a blank User (what should happen; is it the same refusal). Part 4:
`WriteAuditEvent(evt, out outcome)` with the telemetry call still last - the outcome is set before telemetry runs:
confirm nothing after the out-assignment can throw and leave the out unset for a caller (C# requires assignment,
but a thrown exception after assignment is fine - check the assignment precedes the first throwable statement
after the write). a52: `ProtectedGroupMembership.Evaluate` called over the session identity - the evaluator's
in-chain queries at the catalog for universal groups: with no credential, does the session identity reach the
catalog the same way `SelfGrantDirectory` discovered it; the evaluator's "incomplete" vs the validator's refusal -
every incomplete path refuses (trace each); `ExchangeGroupMembers.Read` - `Get-UnifiedGroupLinks -LinkType Members`
lists members not owners: an OWNER who is not a member of a Microsoft 365 group can still read the mailbox? (name it
if Exchange grants owners access); a dynamic distribution group (`DynamicDistributionGroup`) - "any other kind
throws" -> refused as incomplete: a functional block for grants to dynamic groups, name severity; the nested bound of
50 - exceeded throws -> refused: name it; a synced nested group "listed and not expanded, found in the directory by its
address" - a synced group whose address Exchange reports differently from the directory's `mail` (proxy vs
primary) -> not found -> incomplete: fail-closed, but a false block; the operator's recipient compared by object id
or primary address or "their account's" address - which attribute; the `ResultSize Unlimited` reads on a large
group per grant - cost per bulk row. Tests passing for the wrong reason: `SelfGrantDirectoryStandIn`'s in-chain rule
implementation vs the real server's (the stand-in follows only links "the server asked holds" - does the real GC
hold universal group membership for the chain the test builds); `ExchangeGroupMembersTests` stub cmdlet shapes vs the
real cmdlets' output objects (`PrimarySmtpAddress` as string vs SmtpAddress, `ExternalDirectoryObjectId` presence).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
