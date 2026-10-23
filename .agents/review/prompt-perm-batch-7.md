# Defect review request: a Microsoft 365 group is a group and self-grant membership reads run under the directory-read credential or refuse (aud-a60); one membership read per bulk run, a named nesting bound, a distinct write-time refusal (aud-a61); guarded notifications and step-named catches on the permission pages (aud-a53); outcome-aware denials, a blank Target as an invalid row, the harness throws on unset members (aud-a59); the Exchange Online pool's import clears its error stream (psl-9)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `702407f4` or later.
- The change is the branch merged at `c89f639c`: `fa1ed15c` (plans), `e06c1c2d` (aud-a60), `d945d8c7` (aud-a61),
  `4580c404` (aud-a53), `7328135e` (aud-a59), `4614080e` (psl-9 plan), `69720893` (psl-9), `1c982906` (comments only),
  `ebf7c6cf` (record SHAs), forked from `a7300dee`. Review `git diff a7300dee ebf7c6cf -- . ':!.agents'`, and read the
  COMBINED `Services/PermissionValidator.cs` (`IsGroupRecipient` by `RecipientTypeDetails`; the credentialed membership
  reads and the three refusals without the credential; `CompareInExchangeGroupAsync`; the run overload),
  `Services/SelfGrantRun.cs` (new), `Services/ExchangeGroupMembers.cs` (`MaxNestedGroups = 250`,
  `ExchangeGroupNestingBoundException`), `Services/PowerShellProtectedGroupDirectory.cs` (the session-identity factory
  removed), `Services/SelfGrantDirectory.cs`, `Services/IIdentityResolver.cs` (`ResolvedRecipient.RecipientTypeDetails`,
  `ExchangeRecipientKinds`), `Services/SelfGrantRefusalRecord.cs` (`Banner` with the record kind; `RefusedAtWrite`;
  `AuditRowNotWritten`), `Services/PermissionChangeFailure.cs` (new), `Services/MailboxPermissionService.cs` and
  `Services/CalendarPermissionService.cs` (the run per bulk loop; the blank-Target row), `Components/Pages/MailboxPermissions.razor`
  and `Components/Pages/CalendarPermissions.razor` (the guarded notifications and success record; the step-named
  catches; 704/658), `Services/ExoConnectionPool.cs` (`CreateConnected`'s import in try/finally; `ConnectRunspace`,
  `CertificateThumbprint` seams), and the tests `PermissionValidatorSelfGrantTests.cs`, `PermissionPagesSelfGrantTests.cs`,
  `ExchangeGroupMembersTests.cs`, `ExchangeIdentityResolverTests.cs`, `ProtectedGroupMembershipTests.cs`,
  `ExchangeWriteHarness.cs`, `ExoConnectionPoolConnectTests.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a60.md` (plan item 2: the credential is the protected-principal directory-read
  credential through `ProtectedPrincipalService.GetDirectoryReadSecretId` and `DelineaService`, not `ModuleCredentialService`
  - the supervisor accepted that reading), `aud-a61.md`, `aud-a53.md`, `aud-a59.md`, `psl-9.md`; parents `aud-a52.md`,
  `aud-a50.md`, `aud-a43.md`, `aud-a28y.md`, `psl-7.md`, `psl-8.md`, `aud-a6b.md`; `.agents/decisions.md` 2026-07-31 and
  2026-09-11. ALREADY admitted and out of scope: `aud-a69` (the grantee object lookup still runs under the session
  identity; an invisible group is allowed with no membership read), `aud-a70` (the on-prem success records unguarded;
  README and Admin Settings hint texts). Do not re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md`
  "Known Failure Classes" (1, 2, 3) and Architectural Invariant 7; `docs/ProjectConstitution.md` (authorization;
  credential isolation: "a module's credential is used only for that module's purpose" - check whether using the
  protected-principal credential for the self-grant check is within that rule or needs a decision entry).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat c89f639c`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a60: the credential choice - the protected-principal directory-read credential now also
serves the self-grant check: the Constitution's credential-isolation rule (quote it) - is a shared read-only directory
credential across two authorization checks within the rule; with no credential configured EVERY group grant on both
pages is refused: the deployment note says configure it first - is the refusal text actionable (names the setting
and the page); a Microsoft 365 group by `RecipientTypeDetails=GroupMailbox` - `ExchangeRecipientKinds` list: any group
kind missing (`RoomList`, `DynamicDistributionGroup`, `MailNonUniversalGroup`, `MailUniversalSecurityGroup`,
`MailUniversalDistributionGroup`); a dynamic distribution group - members are a filter, `Get-DistributionGroupMember`
does not apply: what happens (throw -> incomplete -> refused: a functional block on dynamic groups - name severity).
a61: `SelfGrantRun` keyed by the operator's Exchange identity and the group's address, with `Lazy` per key - a failure
cached for the run: a transient throttle on row 1 refuses all 200 rows (acceptable? the record says kept as the
failure - name it as a trade-off if not stated); the credential fetched once per run - a secret rotated mid-run (fine);
250 nested groups - each read `-ResultSize Unlimited`: 250 reads per check worst case: cost named. a53: the catch's
"not made / could not be confirmed / was written, but" three-way: is "could not be confirmed" reachable (the write
threw AFTER Exchange may have applied it) and does the audit row for that case say so (Known Failure Class 1). a59:
`Banner` with record kinds - the log line for a denial names "protected-target denial" (no PII beyond the target?);
the blank-Target row before the blank-User row - a row blank in both reports which. psl-9: the try/finally around the
import - `ReadConnectionIds` still reads `ps.Streams.Error.Count > 0` for its OWN failure: confirm the connect
command's own non-terminating error still reaches it. Tests passing for the wrong reason: `SelfGrantDirectoryStandIn`
modelling the credentialed vs session-identity visibility difference (how does the stand-in decide what each identity
sees); the M365 stub now in the real shape - any other test still using `RecipientType` containing "Group".

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
