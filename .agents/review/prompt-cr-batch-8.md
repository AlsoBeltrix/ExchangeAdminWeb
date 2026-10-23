# Defect review request: credential refusal wording, refusal suffixes, Conference Rooms CancelJob audit, error-stream isolation, validate-exists stand-in (aud-a2y, kd-5, psl-10 parts 1-4)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `07f2d371` or later.
- The change is the branch merged at `07f2d371`: commits `3f641ce4`, `2de45069`, `d70497d9` (aud-a2y 1-3), `18e601f8` (kd-5),
  `c4374938`, `370e32f3`, `313775a0`, `91559b4a` (psl-10 1-4), `f7b794cb` (records), forked from `3e43b35b`. Review
  `git diff 3e43b35b f7b794cb -- . ':!.agents'` and read the COMBINED `Services/ConferenceRoomService.cs` (the Type path's
  Warning-and-rethrow), `Services/SelfServiceGroups/SelfServiceGroupService.cs` (the six credential refusals now worded from
  `ModuleCredentialService.LookupCredentialsAsync`; `ResolveCallerDn` now internal; the shared `ReadGroups` with its error-stream
  clear; the `CreateDirectoryRunspace` seam), `Services/GroupManagementService.cs` (the seventh refusal), `Services/Jobs/ConferenceRoomBulkProcessor.cs`
  ("Invalid type ... Nothing was written."), `Components/Pages/ConferenceRooms.razor` ("Invalid room type" suffix in place; `CancelJob`
  about 837 writing RemoveJob's audit row), `Services/Jobs/BulkJobService.cs` (`CancelJob` returns its bool), `Services/EmergencyDisableService.cs`
  (`VerifyBoundObject` clears the error stream in a finally), `Services/DhcpAuthorizationService.cs` (the remote import is now
  `Import-Module DhcpServer -ErrorAction Stop 2>$null`), `ExchangeAdminWeb.Tests/ADDirectoryValidateExistsTests.cs` (on the psl-8
  stand-in, out of `LiveDirectoryCollection`), and the tests the records name. Note master merged other Conference Rooms work in
  between (aud-a2e `ebd66b91`, kd-7/kd-8 `aed153c9`); review this range's diff but read the combined files.
- You may read any file and run read-only git commands. One command per call. No writes, no builds, no network, no directory.
- Records: `.agents/review/findings/aud-a2y.md`, `kd-5.md`, `psl-10.md` (part 5 is an owner call, held); shapes: `aud-a2w.md`,
  `aud-a2x.md`, `psl-8.md`. ALREADY admitted and out of scope - do not re-raise: `kd-26` (Admin Bulk Jobs audits a cancel as
  Success), `kd-27` (CancelJob/RemoveJob never re-check authorization), `kd-28` (Enum.TryParse accepts numeric types), `kd-29`
  (the page's invalid-type refusal writes no audit row), `kd-23` (the bulk processor trusts a blank persisted ticket), `kd-24`
  (control-only tickets), psl-10 (5). Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` Known Failure Classes (1, 2, 3),
  Architectural Invariant 7; `docs/ProjectConstitution.md` (audit, credential isolation).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat f7b794cb`. Report whether both succeeded.

## Mandate

Hunt for observable defects. Error-stream isolation (psl-10 1-2): does each `finally` clear run on the cancellation and
exception paths, can a clear in one read erase a record a LATER `HadErrors` check in the same invocation legitimately
needed (a clear that hides a real failure is worse than the defect), and is the shared `ReadGroups` used by every group read
that a `CallerCanManageMembers` check follows; the DHCP import `2>$null` - does it also hide a genuine import failure that
`-ErrorAction Stop` should surface (what does Stop do under a redirected error stream in a remote script block), and is the
script text still pure ASCII; credential wording (aud-a2y 2): do the seven sites choose the clause from the SAME failure
shape `LookupCredentialsAsync` returns, and can any site now say "no secret ID set" when one is set but Delinea refused;
the Type path's rethrow - does the Warning leak the exception text or a credential; kd-5: does `CancelJob` audit before or
after the cancel is confirmed (Known Failure Class 1), does the refused/failed row carry `mutationAttempted` correctly,
and does the returned bool from `BulkJobService.CancelJob` change any other caller's behaviour; the validate-exists theory on
the stand-in - does it still prove the production probe does not throw for every object kind, or only the stand-in; the
page edits in place - any ClickGate / Progress key whose text changed; and tests that pass for the wrong reason (a stub
`LookupCredentialsAsync` returning a shape production never returns; the `ReadGroups` test asserting on a cleared stream
without a record ever having been left).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.