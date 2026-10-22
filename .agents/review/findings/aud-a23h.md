# aud-a23h: Migration eligibility reads fail-open under -ErrorAction Ignore, and the approve path reports completion whether or not Complete-MigrationBatch ran

**Severity**: MEDIUM - two decisions taken from reads or writes whose failures are suppressed
(Known Failure Class 2; the aud-a23g part-1 class on the module's other paths). (1)
`CheckMigrationEligibilityAsync` (`Services/MigrationService.cs` around 76-89) reads
`Get-MigrationUser -Identity` and `Get-Mailbox -Identity` under `-ErrorAction Ignore`, so a failed
read - a throw included - reads as "no migration in progress" or "no cloud mailbox", and the
operator is told a user is eligible when the directory could not be asked. (2)
`ApproveMigrationUserAsync` runs `Set-MigrationBatch` twice and `Complete-MigrationBatch` through
`Ignore` / `InvokeOptional`, so none of them can fail; the page reports "initiated completion"
whether or not `Complete-MigrationBatch` ran.
**Status**: Merged `ab5361c8`; closed on guard proof; codex review pending
**Branch**: `worktree-agent-a3e674616251be4de` (forked at `34c7ccd4`)
**Commit**: `5b5936eb` (fix, tests, record); plan `4cf6b629`; comments `033a1a54` (shared with aud-a23i)
**Reviewer**: implementer (surfaced by the aud-a23g agent, 2026-10-22, candidates 1 and 2 of its hand-back)

## Evidence

At `cafef918`, `Services/MigrationService.cs`: `CheckMigrationEligibilityAsync` (the two `Ignore`
reads and how their empty results are interpreted); `ApproveMigrationUserAsync` (the two
`Set-MigrationBatch` calls and `Complete-MigrationBatch`, each through `InvokeOptional`). Compare
aud-a23g part 1: `-ErrorAction Stop`, a failed call reported as a failure, never as an empty answer.

## Approach (to be written by the implementing agent before code)

- Eligibility: run both reads under `-ErrorAction Stop`; a failure is an eligibility result of
  "could not be determined: <reason>" that the page shows and that blocks the action (fail closed,
  no guess); a genuine not-found (the cmdlet's own not-found error, recognised by its category or
  `FullyQualifiedErrorId`, not by text) keeps its current meaning. Label the recognition rule an
  assumption until a dev check.
- Approve: each write under `-ErrorAction Stop`; a failure is the row's failure naming the step
  (`Set-MigrationBatch` / `Complete-MigrationBatch`), audited as failed, never "initiated completion".
  Where the write's success must be confirmed, read the batch back (aud-a2l/a2p shape) before
  reporting.
- Tests through the Migration stub runspace: a throwing `Get-MigrationUser` yields "could not be
  determined" and blocks; a not-found yields "no migration in progress"; a throwing
  `Complete-MigrationBatch` yields a failed, audited row; a clean run reports completion. Mutations
  at the call site: `Ignore` again on the eligibility read; `InvokeOptional` again on the complete
  call. `Migration.razor` has line-keyed `ClickGateRegistry` entries - edit in place or re-anchor
  per the playbook.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `cef232a1`. One commit `fix(migration): ...
(aud-a23h)`, before aud-a23i on the same branch. Module-scoped: Migration (its bump is the supervisor's;
`Modules/ModuleCatalog.cs`, `ExchangeAdminWeb.csproj` not edited). No new handler, JS interop or lifecycle method.
Written before the code; line numbers at `34c7ccd4`.

**Found while planning** (a probe through this host's PowerShell 7.6 SDK, script functions; no Exchange): under
`-ErrorAction Stop` a written error throws `ActionPreferenceStopException` whose `ErrorRecord` keeps the cmdlet's
category, reason (the exception's type name) and `FullyQualifiedErrorId` (`Write-Error -ErrorId X` in F gives `X,F`);
`ThrowTerminatingError` throws `CmdletInvocationException` carrying the record; `throw "text"` throws
`RuntimeException` (`WasThrownFromThrowStatement`, category OperationStopped) whose FQID IS its text; a cmdlet the
session lacks throws `CommandNotFoundException`, category **ObjectNotFound**, FQID `CommandNotFoundException`. Under
`Ignore` a written error and a throw both come back as no rows - the finding. So: Stop on the reads, and "not found"
read from the record's structure, with `CommandNotFoundException` (a role not granted, a module not loaded) never an
answer about the mailbox.

**Single-user batches are named for the mailbox.** `CreateSingleMigrationBatch` (Migration.razor 2041-2043) defaults
the batch name to the address, and the approve path's three batch steps target `-Identity <address>`. For a mailbox
in a multi-user batch no such batch exists, which `Ignore` hid; under Stop every such approval would fail. So whether
the batch exists is READ first, by the not-found rule, and the batch steps run only when it does.

1. `Models/Enums.cs` 28-32: `MigrationStatus.Undetermined`, appended.
2. `Services/MigrationService.cs` `CheckMigrationEligibilityAsync` (64-149):
   - 76-79, 86-89: `-ErrorAction Stop`, through new `ReadUnlessNotFound` (`InvokeOptional`, the aud-a23g read idiom:
     it fails on an exception, not on `HadErrors` alone): a not-found is no rows, any other failure throws.
   - a local names the read in flight (`Get-MigrationUser`, `Get-Mailbox`; none once both are in).
   - 141-146: the catch turns an Eligible verdict into `Undetermined` (an Ineligible one stays: its reason is known)
     and adds "Could not be determined: <read> failed: <reason>", or "Could not be determined: <reason>" past the
     reads. The reason through `FailureReason` (750-751), extended one level into `Invoke`'s wrapper: no Stop prefix.
   - Unchanged: what an answered read means; the AD group, size and protection steps. Undetermined is not Eligible,
     so the size step (164) and every creation path (146, 2036, 2183) pass it by.
3. New `internal static IsNotFound(Exception)`. **Assumption, unverified (no live call):** the Exchange Online
   module's "no such object" from `Get-MigrationUser`, `Get-Mailbox` and `Get-MigrationBatch -Identity` is
   recognisable by structure - the record (the exception's, or one level in) has category `ObjectNotFound`, or its
   `CategoryInfo.Reason` names a `*NotFoundException` type, or an identifier in its `FullyQualifiedErrorId` does (the
   module's `[FailureCategory=Cmdlet-ManagementObjectNotFoundException]` form). Never text: no message is read, and a
   throw statement's FQID, which is its text, is not read. Never `CommandNotFoundException`. If the module reports
   not-found some other way, a mailbox with no migration reads "could not be determined" - visible and blocking.
4. `CheckBulkMigrationEligibilityAsync` (251-262): `IneligibleUsers` counts Ineligible only; new
   `MigrationBatchResult.UndeterminedUsers` (`Models/MigrationModels.cs` 44-45).
5. `ApproveMigrationUserAsync` (898-948), not retry-eligible as before:
   - First `Get-MigrationBatch -Identity <address>` (Stop, `ReadUnlessNotFound`): none - not in a batch of its own;
     one - it is; more than one, or a failed read - the row fails before any write, "Nothing was changed."
   - Every write under Stop through new `ApproveStep` (`Invoke`): a failure throws "<step> failed for <address>:
     <reason>. Already applied: <steps>." (or "Nothing was changed before it."), which `RunAsync` returns as the
     row's failure, and both callers' existing audits record as Failed with that text (`ExecuteUserAction` 3373,
     `ExecuteBulkMailboxAction` 3739-3740).
   - The three batch steps (`Set-MigrationBatch` twice, `Complete-MigrationBatch`) only when the batch exists; the
     writes' order otherwise unchanged.
   - Read-back (aud-a2l/a2p shape) after `Complete-MigrationBatch`: `Get-MigrationBatch -Identity <address>` (Stop)
     must return the one batch with a completion time set (`CompleteAfter`, which the `Set-MigrationBatch
     -CompleteAfter` step wrote, or `CompleteAfterUTC`); otherwise "Completion of the migration batch <address> was
     not confirmed: ...", or "could not be confirmed" when the read fails. **Assumption, unverified:** Exchange keeps
     `CompleteAfter` set on a batch told to complete. Completion itself is asynchronous (the batch actions' "queued"
     wording, 986-989), so what is confirmed is the accepted request and the batch's setting. No retry.
   - Success: the message unchanged ("Approved skipped items and initiated completion for <address>."); the Detail
     says which - its batch was told to complete and reads back set to complete, or it is not in a batch of its own
     and no batch was completed.
6. `Components/Pages/Migration.razor`, in place, 4377 lines; none of the edited lines is registered (91, 256, 2003,
   2005):
   - 91: an Undetermined result reads "Whether <address> can migrate <direction> could not be determined. Nothing can
     be created for it until a check succeeds.", not "IS NOT eligible"; its reasons follow (92-102). The create card
     (146) and `CreateSingleMigrationBatch`'s guard (2036) already require Eligible.
   - 256: "| Could not be determined: N" after the Ineligible count, when N > 0.
   - 2003, 2005: the page's own catch (a throw before the service returns) is Undetermined, "Could not be determined:
     <message>", where it was Ineligible, "Error: ...". Its audit (2007-2008) unchanged.

**Tests** - new `ExchangeAdminWeb.Tests/MigrationEligibilityAndApproveTests.cs`: the real service on a real pool whose
connect seam hands out a runspace of stub cmdlets (advanced functions) driven by a failure table (a throw, or an
`ErrorRecord` the stub writes) over in-memory migration users, mailboxes and batches; the page's render tree and
handlers by reflection (the `MigrationBatchReadTests` idiom). Fakes only.
- **the finding, eligibility**: `Get-MigrationUser` that throws, and one that writes an error, ToOnPrem over a cloud
  mailbox (before: Eligible): Undetermined, "Could not be determined: Get-MigrationUser failed: <message>", no Stop
  prefix; the same for `Get-Mailbox`; a session without `Get-MigrationUser`: Undetermined.
- not-found keeps its meaning, by category, by type, by FQID identifier: no "Migration already in progress", ToOnPrem
  Eligible; `Get-Mailbox` not found ToOnPrem: "Not a cloud mailbox"; a migration user present: "Migration already in
  progress".
- text is never read: a written error whose message says it "couldn't be found", and a `throw` whose text names
  `ManagementObjectNotFoundException`: Undetermined. A later read failing after an Ineligible answer: Ineligible,
  both reasons.
- the page: Undetermined renders "could not be determined" and the reason, no "IS NOT eligible", no "Create Migration
  Batch"; `CreateSingleMigrationBatch` with it does nothing, the Eligible control calls through; the page's catch
  (a connect that fails) is Undetermined; bulk counts an undetermined row apart and the summary names it.
- **the finding, approve**: a clean run with its own batch: success, each step once in order, then the read-back;
  `Complete-MigrationBatch` that throws: failed, naming it and the six applied steps, and through `ExecuteUserAction`
  one audit row, Failed, error naming `Complete-MigrationBatch`; `Set-MigrationBatch -CompleteAfter` failing: named,
  `Complete-MigrationBatch` never called; no batch of its own: success, no batch step called, the Detail says so; the
  existence read failing: failed, nothing written; a `Set-MigrationBatch` that changes nothing: "was not confirmed".
- `IsNotFound` over constructed exceptions: the three shapes, `CommandNotFoundException` (top level and inside
  `Invoke`'s wrapper), a throw statement, a plain error.

**Mutations at the call site** (md5 before/after, failing tests named, restored, md5 equal, touched, rebuilt, green):
H1 (the brief's (1)) `-ErrorAction Ignore` again on the `Get-MigrationUser` read; H2 (the brief's (2)) `Ignore` +
`InvokeOptional` again on `Complete-MigrationBatch`. Extras: H1b `Ignore` on `Get-Mailbox`; H1c `IsNotFound` accepts
`CommandNotFoundException`; H1d the throw statement's FQID read; H1e the page's catch Ineligible again; H1f line 91
"IS NOT eligible" for every non-Eligible verdict; H2b the batch steps without the existence read; H2c no read-back.

**Gates**: Release build; full suite with `--blame-hang`; `dotnet format --verify-no-changes`; `git diff --check HEAD`;
ASCII lint after `git add -A`; `ClickGate*`/`ProgressRegistry*` green, `ExpectedLineCount` unchanged. Comments written
after the mutation round.

## Guard proof

Built as the Plan. Line numbers are at the fix commit's bytes, which carry no new comments (they come after aud-a23i,
in their own commit, and move these lines). `Components/Pages/Migration.razor` is 4377 lines before and after:
`git diff --numstat` 4/4, each changed line replaced in place (91, 256, 2003, 2005); none is registered.

- `Models/Enums.cs`: `MigrationStatus.Undetermined`, appended, so the two existing values keep their numbers.
- `Models/MigrationModels.cs`: `MigrationBatchResult.UndeterminedUsers`.
- `Services/MigrationService.cs`:
  - `CheckMigrationEligibilityAsync` (66-): both reads `-ErrorAction Stop` (81, 92) through `ReadUnlessNotFound` (82,
    93); `reading` (76, 94) names the read in flight; the catch turns an Eligible verdict into `Undetermined` (149),
    keeps an Ineligible one, and adds "Could not be determined: <read> failed: <reason>" (or, past the reads, "Could
    not be determined: <reason>").
  - `CheckBulkMigrationEligibilityAsync`: `IneligibleUsers` counts Ineligible only (261); `UndeterminedUsers` (262,
    273).
  - `FailureReason` (761) looks one level in, at `Invoke`'s wrapper; `ReadUnlessNotFound` (766); `IsNotFound` (778):
    `CommandNotFoundException` excluded first (the exception, one level in, or the record's identifier), then category
    `ObjectNotFound`, a `*NotFoundException` reason, or a `*NotFoundException` identifier in the FQID unless a throw
    statement raised it; `NamesNotFound` (797), `Identifiers` (802).
  - `ApproveMigrationUserAsync` (952-): the existence read (962-980) before any write; `hasOwnBatch` (982); every write
    through `ApproveStep` (989, 995, 1003, 1009, 1016, 1021, 1029), the three batch steps only with a batch of its own;
    `ConfirmBatchSetToComplete` (1031) after `Complete-MigrationBatch`; the Detail (1034). `ApproveStep` (1040) throws
    "<step> failed for <address>: <reason>. Already applied: ..." / "Nothing was changed before it.";
    `ConfirmBatchSetToComplete` (1057) - "could not be confirmed" for a failed read-back, "was not confirmed" for one that
    shows no completion time.
- `Components/Pages/Migration.razor`: 91 the undetermined text; 256 "| Could not be determined: N"; 2003, 2005 the
  page's catch Undetermined, "Could not be determined: <message>".

New `ExchangeAdminWeb.Tests/MigrationStubExchange.cs`: the stand-in Exchange (stub cmdlets as advanced functions over
in-memory users, mailboxes and batches; a failure table of thrown text or written `ErrorRecord`s; the not-found shapes;
the page with its services set by hand; the audit file read back). New
`ExchangeAdminWeb.Tests/MigrationEligibilityAndApproveTests.cs`, 17 tests (32 cases):

| Test | Pins |
| --- | --- |
| `AnEligibilityReadThatFails_CouldNotBeDetermined_NeverAGuess` (Get-MigrationUser, Get-Mailbox; throw, written error) | **the finding**: ToOnPrem over a cloud mailbox. Undetermined; "Could not be determined: <read> failed: <message>", no Stop prefix; neither "Migration already in progress" nor "Not a cloud mailbox" |
| `ASessionWithoutTheCmdlet_CouldNotBeDetermined` | `CommandNotFoundException` (ObjectNotFound to PowerShell) is not an answer: Undetermined |
| `TextThatSaysNotFound_IsNotReadAsAnAnswer` (a written error saying "couldn't be found"; a throw naming the type) | never text: Undetermined |
| `AReadThatFailsAfterAnIneligibleAnswer_StaysIneligible_WithBothReasons` | a known reason stands: Ineligible, both reasons in order |
| `ExchangesNotFound_KeepsItsMeaning` (category, type, identifier) | no migration user: Eligible, no reasons; no mailbox: "Not a cloud mailbox ..." - the old meanings |
| `AMigrationUserThatExists_IsStillAMigrationInProgress` | the control: "Migration already in progress" |
| `IsNotFound_ReadsTheRecordsStructure_NeverItsText` (7 bodies) | the recogniser over exceptions PowerShell raised under Stop, bare and inside `Invoke`'s wrapper: ObjectNotFound, a `ManagementObjectNotFoundException` reason, the FQID form, `ThrowTerminatingError` - not found; a plain error, a throw's text, a missing cmdlet - not |
| `AnUndeterminedAnswer_IsShownAsSuch_AndOffersNoBatch` | through the page's own `CheckSingleEligibility`: "Whether <address> can migrate to On-Premises could not be determined.", the reason, no "IS NOT eligible", no "IS eligible", no "Create Migration Batch"; the audit row's status Undetermined, reasons "Could not be determined ..." |
| `CreatingABatchFromTheAnswer_GoesThroughOnlyWhenEligible` (Undetermined, Eligible) | the handler's own guard: an undetermined answer creates nothing (`singleCreateResult` stays null), the Eligible control reaches the service; no `New-MigrationBatch` either way (this harness has no protection service, so the fail-closed protection check excludes the control's target) |
| `ACheckThatFailsBeforeTheServiceReturns_CouldNotBeDetermined` | the page's catch (a connect that throws): Undetermined, "Could not be determined: <message>", no "IS NOT eligible" |
| `ABulkCheck_CountsAnUndeterminedRowApart` | (2, 1, 0, 1) total/eligible/ineligible/undetermined; the summary "Could not be determined:", the row "Undetermined" and its reason, "1 user(s) eligible to migrate" |
| `ACleanApproval_OfAMailboxInItsOwnBatch_RunsEveryStep_AndReadsTheBatchBack` | success, the message unchanged, the Detail; the nine calls in order, existence read first, read-back last |
| `ACompleteMigrationBatchThatFails_IsAFailedAuditedRow_NamingIt` (throw, written error) | **the finding**: through the page's `ExecuteUserAction`: failed, "Complete-MigrationBatch failed for <address>: <message>. Already applied: <the six steps>."; no read-back; one audit row, `ApproveMigrationUser`, Failed, the error naming the step |
| `ASetMigrationBatchThatFails_IsNamed_AndNothingAfterItRuns` | named, the three applied steps; the calls stop there |
| `AMailboxNotInABatchOfItsOwn_IsApprovedWithoutTouchingABatch_AndSaysSo` | success; the Detail says no batch was completed; no `Set-MigrationBatch`, no `Complete-MigrationBatch` |
| `AnExistenceReadThatFails_ChangesNothing` (throw, written error) | "Could not tell whether ... Nothing was changed."; the existence read is the only call |
| `AWriteThatChangesNothing_IsNotConfirmed` | every write reports success and the batch reads back with no completion time: "was not confirmed: Exchange reports no completion time set on it" |

**Against the unfixed code: not runnable as a whole** - the model gained a status and a count and the service an
internal recogniser, so the tests cannot compile against `34c7ccd4`. Each behaviour was put back at its own call site
instead, and each revert fails the tests that pin it.

Mutations at the call site, each by exact-text replacement asserted to match once
(`a23h-a23i-work/run-mutations.ps1`, scratchpad): md5 recorded, `dotnet build ExchangeAdminWeb.slnx -c Release`, the
975 tests of `Migration*|ClickGate*|ProgressRegistry*|ExchangeServiceBase*|AuditService*|GlobalProgressWiring*` run
(failures read from the TRX), the file restored from its saved bytes, touched, md5 re-confirmed equal, rebuilt, and the
975 re-run green - after every mutation. Pre-comment bytes (LF): service `b5156cdb0672f7e3ed16a6b8b6bbae6d`, page
`893f55217e032813bfbca4ffada5ee9d`.

| Mutation | File | md5 after | Failed (of 975) | Restored, rerun |
| --- | --- | --- | --- | --- |
| H1 (the brief's (1)): `-ErrorAction Ignore` again on the `Get-MigrationUser` read | service | `0b3fc7529cfde5e4cfd7a091b1a0f744` | 6: `AnEligibilityReadThatFails...` (Get-MigrationUser, both), `TextThatSaysNotFound...` (both), `AnUndeterminedAnswer...`, `ABulkCheck...` - the fail-open: Eligible | equal; 975/0 |
| H2 (the brief's (2)): `Ignore` + `InvokeOptional` again on `Complete-MigrationBatch` | service | `68e86bf6b653626dafcef88b4c677379` | 2: `ACompleteMigrationBatchThatFails...` (both) - "initiated completion" again | equal; 975/0 |
| H1b: `Ignore` on the `Get-Mailbox` read | service | `28d1dde375b7aa4c38363f4193e55726` | 3: `AnEligibilityReadThatFails...` (Get-Mailbox, both), `AReadThatFailsAfterAnIneligibleAnswer...` | equal; 975/0 |
| H1c: `IsNotFound` without its `CommandNotFoundException` exclusion | service | `6b040af25b2fe6f692105ec01274b031` | 2: `ASessionWithoutTheCmdlet...`, the recogniser's missing-cmdlet row | equal; 975/0 |
| H1d: a throw statement's FQID read | service | `6087a5331f1048ec73e89dc4586bc88a` | 2: `TextThatSaysNotFound...` (the throw), the recogniser's throw row | equal; 975/0 |
| H1g: the catch's verdict Ineligible again | service | `7eeabc7c43f7b5437ee987c4e85bf21a` | 9: every Undetermined expectation in the service and page tests | equal; 975/0 |
| H1h: `IneligibleUsers` counts every non-eligible row again | service | `f2cbf493d86b5a3bbb6a8c9ad5cab25b` | 1: `ABulkCheck...` | equal; 975/0 |
| H1e: the page's catch Ineligible, "Error: ..." again | page | `e56471f5ee1055ede4ddbde3bc10acee` | 1: `ACheckThatFailsBeforeTheServiceReturns...` | equal; 975/0 |
| H1f: line 91 "IS NOT eligible" for every non-Eligible verdict | page | `a8122ae4cb3670c3d826363dd3aed5ad` | 2: `AnUndeterminedAnswer...`, `ACheckThatFailsBeforeTheServiceReturns...` | equal; 975/0 |
| H2b: the batch steps without the existence read (`hasOwnBatch` always true) | service | `1178aa311ddb74cf59c7356b846747f9` | 1: `AMailboxNotInABatchOfItsOwn...` | equal; 975/0 |
| H2c: no read-back | service | `f00c1a29e3bc8457c766d739254183b4` | 2: `AWriteThatChangesNothing...`, `ACleanApproval...` | equal; 975/0 |

H1 and H2 are the brief's two. md5s (LF) of the other files at the fix commit: `Models/Enums.cs`
`0dbd1bffa1a0cad4df83e2bceec0c10e`, `Models/MigrationModels.cs` `7d94f0a63565e703d33dc82c7889ec5c`, the tests
`MigrationStubExchange.cs` `d550fd7903cc9e5f299cfbbdba5fc62c`, `MigrationEligibilityAndApproveTests.cs`
`f3f27d898f845010e64827dc176a55b6`.

**Pins.** `ClickGateRegistry`: 4377 lines before and after, no registered line among 91, 256, 2003, 2005, no registered
snippet changed, `ExpectedLineCount` untouched - the `ClickGate*` tests pass. `ProgressRegistry`: no handler, lifecycle
method or `Complete(` added; `CheckSingleEligibility` still pins `MigrationSvc.CheckMigrationEligibilityAsync`.

## Gates (the fix commit's tree, on `4cf6b629`)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors.
- `dotnet test ExchangeAdminWeb.slnx -c Release --no-build --blame-hang --blame-hang-timeout 10m`: 5624 passed, 3 skipped
  (the known `ADDirectorySearchServiceTests ...WhenAdUnavailable` cases), 0 failed - 5627 in all, 32 of them new;
  5 m 25 s, no hang. Baseline at `34c7ccd4`: 5592 / 3 / 0, 5595.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
- The branch-tip gates, after aud-a23i and the comments, are below.

## Known gaps

- **Assumptions, unverified (no live Exchange call):** (a) the not-found recogniser - that the Exchange Online module
  reports "no such object" for `Get-MigrationUser`, `Get-Mailbox` and `Get-MigrationBatch -Identity` with category
  ObjectNotFound, a `*NotFoundException` reason, or the FQID's `FailureCategory=Cmdlet-...NotFoundException` part. If it
  does none of these, every check of a mailbox with no migration reads "could not be determined" and the operator cannot
  create a batch: visible and fail-closed, but it blocks the module, so one dev check settles it before a deploy; (b)
  that a batch told to complete reads back with `CompleteAfter` (or `CompleteAfterUTC`) set - if Exchange clears it on
  completion, a successful approval of a single-user batch reads "was not confirmed"; (c) the SDK behaviour above was
  probed with script functions on this host's PowerShell 7.6, not with the module's own proxies.
- **A mailbox in a batch of many is approved without any batch step**, as before (then the steps failed unseen under
  `Ignore`); the Detail now says so. The batch steps target a batch named for the mailbox, the default single-user
  batch name; a single-user batch given another name is treated the same way.
- The audit's `result` for an undetermined check is "Ineligible": `AuditService.LogMigrationCheck` files anything not
  Eligible there, and the Admin Event Log's result filter (another page) offers only Success and Ineligible. Its
  `status` field says Undetermined and `reasons` carries the cause.
- No circuit or browser ran: the page's compiled render tree and handlers were driven directly.

## Comments (`033a1a54`)

Written after both mutation rounds, at the call sites they proved load-bearing; comments only - every changed `.cs`
line a comment or blank, and each of the page's 17 changed lines the line it was plus a trailing comment, so its 4377
lines and every registered line key hold. Final md5s (LF) at the branch tip: `Services/MigrationService.cs`
`1899365d604775f7672321f08c7f32ee`, `Services/ExchangeServiceBase.cs` `e06caa1c89eacfdbbd0bc548329f6671`,
`Components/Pages/Migration.razor` `5bcc6493e840f35baec9c4ee39eb5e24`, `Models/Enums.cs`
`f1b3966c785807b77bf1d5cfb777c6a2`, `Models/MigrationModels.cs` `a78d64044b4415fa1b779d7ded4b68fb`; the tests are as
their fix commits left them. Focused rerun after the comments: 1056 / 0.

## Branch-tip gates (`033a1a54`: both fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the count at `34c7ccd4`.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): 5658 passed, 3 skipped (the known `ADDirectorySearchServiceTests ...WhenAdUnavailable` cases), 0 failed - 5661 in all, 66 of them new across both findings; 5 m 22 s, no hang. Baseline at `34c7ccd4`: 5592 / 3 / 0, 5595.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No live Exchange, Active Directory, Graph, DHCP or IIS call was made: every Exchange cmdlet was a stub in an in-process
runspace, and no excluded-group setting was configured, so the eligibility check's Active Directory step never ran. No
PowerShell source changed (PSScriptAnalyzer and Pester not run). No coder self-review verdict is claimed. Version bumps
are the supervisor's: Migration for both findings, and the base app for aud-a23i's `ExchangeServiceBase` change.

## Closeout

Fixed on branch `worktree-agent-a3e674616251be4de`; not merged, not pushed.
