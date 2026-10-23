# Defect review request: the remaining AD module imports clear their error stream through the shared helper and the directory search tests probe a stand-in (psl-8); the Room Type path logs its refusals and a throwing room read, one suffix rule (aud-a2w); credential refusals name whether the secret ID is unset or Delinea returned nothing (aud-a2x)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `cb20834a` or later.
- The change is the branch merged at `9293b77a`: `ee4c8c5d` (plans), `f5e14730` (psl-8), `06c68f89` (aud-a2w),
  `295c4d19` (aud-a2x), `4e0f61a1` (comments only), `c81e0a0b` (record SHAs), forked from `e9886df7` (131 commits
  behind master at merge; the merge-tree was clean and a trial merge built and passed). Review
  `git diff e9886df7 c81e0a0b -- . ':!.agents'`, and read the COMBINED `Services/ConferenceRoomService.cs` (the four
  directory paths through `ActiveDirectoryModuleImport.Import`; `SetRoomTypeAsync`'s four logged refusals; the Finder
  row's room read catch-log-rethrow; the suffix rule; the credential refusal clauses), `Services/EmergencyDisableService.cs`
  (the four steps through the helper; `CreateDirectoryRunspace`), `Services/SelfServiceGroups/SelfServiceGroupService.cs`
  (`PrepareAdRunspace` through the helper; `GroupWithIdentityExists`; `ResolveGroupByGuid`),
  `Services/ModuleCredentialService.cs` (`LookupCredentialsAsync`, `ModuleCredentialFailure`; `GetCredentialsAsync`
  delegating), `ExchangeAdminWeb.Tests/ADDirectorySearchServiceTests.cs` (the stand-in probe; the `CreateService` guard),
  `DirectoryModuleImportTests.cs`, `ModuleCredentialLookupTests.cs`, `ConferenceRoomWriteIdentityTests.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/psl-8.md` (its audit table of every import site), `aud-a2w.md`, `aud-a2x.md`; parents
  `psl-7.md`, `aud-a2r.md`, `aud-a2v.md`. ALREADY admitted and out of scope: `psl-9` (the EXO pool's import clears only
  the commands), `psl-10` (Emergency Disable's `VerifyBoundObject`, Self-Service's caller/group reads, the DHCP remote
  script, the live-directory test classes), `aud-a2y` (the Type path's throwing room read unlogged; the one credential
  text at seven sites in other modules; two refusals outside the service without the suffix). Do not re-raise them. Repo
  rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3) and Architectural Invariant 7;
  `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 9293b77a`. Report whether both succeeded.

## Mandate

Hunt for observable defects. psl-8: the helper's `finally` clearing the error stream after the import - the Emergency
Disable steps that WRITE (disable, password reset) and then check `HadErrors`: with the stream cleared after the import,
does the write's own non-terminating error still reach the check (it must); the four steps made `internal` and the new
runspace seam - production path unchanged (the seam's default builds the same session as before); Self-Service's
`GroupWithIdentityExists` split out - any caller that relied on the old probe's side effects (a loaded variable, a
left record); the `ADDirectorySearchServiceTests` stand-in - the production probe path is now untested by the default
suite: name whether any test still exercises `ProbeAvailability`'s real import path (the live classes do, on this host
only); the `CreateService` guard asserting the directory unavailable - a test that NEEDS the directory available on the
stand-in. aud-a2w: the Finder row's room read logs with the exception and rethrows - the log line's content (a secret
field? an address - fine); the four Type refusals logged "before any write" - confirm the groups-unconfigured refusal is
reached before any write on the Type path; the shared validated-mailbox refusal left alone - does that leave the Type
path with two wording styles still (acceptable if the record says so). aud-a2x: `LookupCredentialsAsync`'s three failures -
is `NotReturned` distinguishable from a Delinea call that THREW (the record says Delinea logs every reason; does the
caller's refusal say "see the application log" only for `NotReturned`, and what does a throw produce); `GetCredentialsAsync`
delegating - the 38 callers' log lines byte-identical (the record claims it; spot-check two); the refusal clause for
`ConfigUnreadable` - the aud-a26t service remedy wording (no "reload this page"). Tests passing for the wrong reason: the
stand-in `Import-Module` cmdlet vs the real module's binding; `ModuleCredentialLookupTests`' `OfflineCredentials` and
`UnreadableModuleConfigStore` helpers vs the production store's failure shape.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
