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
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
**Branch**: `worktree-agent-a3e674616251be4de` (forked at `34c7ccd4`)
**Commit**: -
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

Pending.
