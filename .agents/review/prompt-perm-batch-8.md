# Defect review request: a cloud permission write that throws writes a Failed audit row (aud-a71); the self-grant grantee lookup runs under the directory-read credential and an unresolved grantee refuses (aud-a69); dynamic distribution groups are read through their calculated membership (aud-a72)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `06fc6487` or later.
- The change is the branch merged at `6a971048`: `a8fdcbf5` (a71 plan), `bc1183ab` (aud-a71), `9d3697ee` (a71 census test
  follow-up), `5d120fbc` (a69 plan), `828fc33a` (aud-a69), `8391a40a` (a72 plan), `6a9f3fe7` (aud-a72), `a9ce01f5` (comments
  only), `69392927` and `b280bfee` (records), forked from `15eb1449`. Review `git diff 15eb1449 b280bfee -- . ':!.agents'`,
  and read the COMBINED `Components/Pages/MailboxPermissions.razor` and `Components/Pages/CalendarPermissions.razor`
  (`SubmitSingle`'s catch: the Failed row through `SelfGrantRefusalRecord.Banner(..., FailedSingleChangeRecord)`),
  `Services/SelfGrantRefusalRecord.cs` (`FailedSingleChangeRecord`), `Services/PermissionValidator.cs`
  (`CompareInDirectoryAsync`: the credential had first, `FindNamedBy` under it, the no-credential refusal for a grantee
  Exchange does not name, `FindNamedByUnderSessionIdentity` for a grantee Exchange names, the empty-lookup refusal;
  `CompareInExchangeGroupAsync`'s synced-nested lookup under the credential), `Services/SelfGrantDirectory.cs`
  (`Search` with `-Credential`), `Services/ExchangeGroupMembers.cs` (`DynamicGroupKind`,
  `Get-DynamicDistributionGroupMember`, the empty-membership throw), and the tests `PermissionPagesSelfGrantTests.cs`,
  `PermissionValidatorSelfGrantTests.cs`, `SelfGrantDirectoryStandIn.cs` (`HiddenFromSession`), `ExchangeGroupMembersTests.cs`,
  `ExchangeWriteHarness.cs`, `ValidatedWriteCallSiteTests.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a71.md`, `aud-a69.md` (its choice: without the credential, a grantee Exchange
  names is still looked up as the app pool, an answer that can only add a refusal - the supervisor accepted it),
  `aud-a72.md` (an empty dynamic membership refuses, since it cannot be told from "not yet calculated"); parents
  `aud-a60.md`, `aud-a61.md`, `aud-a53.md`, `aud-a59.md`, `aud-a52.md`. ALREADY admitted and out of scope: `aud-a70`
  (the on-prem success records unguarded; README and Admin Settings hint texts; the on-prem failure row without the
  servicer note; `SubmitSingle` refusals before the write with no audit row; the aud-a69 residual for a grantee Exchange
  names without the credential; the operator's own lookups as the app pool; a dynamic group's membership as of the last
  calculation; `activity.Complete` throwing inside the catch). Do not re-raise them. Repo rules: `AGENTS.md`;
  `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3) and Architectural Invariant 7; `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 6a971048`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a71: the Failed row at the write step says "could not be confirmed" - a reviewer
reconciling audit to Exchange sees Failed + "could not be confirmed": is the result value `false` the right filing
for "unknown" (the event log filter has Success/Failed/Ineligible/Undetermined since aud-a28z part 5 - should an
unconfirmed write be filed Undetermined rather than Failed? name it if the filter cannot find these rows); the row
carries the written identity and the servicer note - PII beyond what the success row carries? (same fields - fine);
the census test counting `WithWrittenIdentity` sites "now 3, deliberately" - does the census still guard what it was
for. a69: the credential had FIRST, once per run - for a grant to a PERSON Exchange names, is the secret now fetched
needlessly on every single grant (cost; the record says a person needs no credential - confirm the fetch is lazy);
"a grantee Exchange names is looked up as the app pool, an answer that can only add a refusal" - trace: a person
Exchange names whose directory object the app pool CANNOT see -> no directory row -> compared by Exchange only ->
allowed if not the operator: correct and unchanged from before; the empty-lookup refusal text "neither Exchange Online
nor the on-premises directory holds '<x>'" - is that reachable for a mistyped grantee the operator will see often
(a typo now reads as a self-grant-check refusal rather than "not found" - wording for the operator); the synced-nested
lookup under the credential - without the credential refuses "at once": a cloud group nesting a synced group now
refuses every grant on a deployment without the credential (expected under aud-a60's rule; confirm the text names
the setting). a72: `Get-DynamicDistributionGroupMember` - does the cmdlet exist in Exchange Online PowerShell v3 under
that name and accept `-ResultSize` (the record labels it an assumption); the empty-membership refusal - a legitimately
empty dynamic group (a filter matching nobody) can never be granted: name severity; nested dynamic groups counted
against the 250 bound. Tests passing for the wrong reason: `HiddenFromSession` in the stand-in - does it hide the
object from the uncredentialed read AND its links from the in-chain query the way AD's security trimming does (no
rows, no error); the harness `Get-DynamicDistributionGroupMember` stand-in's parameter set vs the real cmdlet's.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
