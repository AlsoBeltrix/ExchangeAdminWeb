# Defect review request: failed batch read shown as a failure; counts and times hardened across Migration, Out of Office and Recipient Lookup (aud-a23g)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `4d56e92a` or later.
- The change is the branch merged at `cafef918`: `0c64d49c` (plan), `539f2ccf` (fix), forked from
  `d7d41457`. Review `git diff d7d41457 539f2ccf`, and read the COMBINED `Services/MigrationService.cs`
  (`GetMigrationBatchesAsync` with `-ErrorAction Stop`, `InvokeOptional`, `MigrationBatchList.Failed`,
  `IsCancellation`; `GetMigrationBatchUsersAsync` counts), `Models/MigrationModels.cs` (`MigrationBatchList`
  `Failure`, `ScheduledCompletionUtc`, `CompleteAfterZoneNotReported`), `Models/ReportedCount.cs` (`long?`,
  negatives unreadable), `Models/ReportedTime.cs` and `Models/LookupModels.cs` (`ShowReported` and `Order`
  treating Kind `Local` as naming no zone), `Components/Pages/Migration.razor` (the failure banner, the
  cleared list on failure, "Taken") on master. The part-7 rule also reaches the Out of Office Check
  Status tab and Recipient Lookup through the shared `ShowReported`.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a23g.md` (seven parts, three folded in); parents `aud-a23f.md`,
  `aud-a23e.md`, `aud-a23d.md`, `aud-a7h.md`, `aud-a7f.md`. ALREADY admitted and out of scope:
  `aud-a23h` (`CheckMigrationEligibilityAsync` reads under `-ErrorAction Ignore` so a failed read means
  "no migration in progress"; `ApproveMigrationUserAsync` runs `Complete-MigrationBatch` under
  `Ignore` and reports completion whether or not it ran) and `aud-a23i` (a mailbox row with no Identity
  becomes a targetable "Unknown" and a throwing row loses the list; `LoadBatchList` completes its
  progress activity as success on failure; `ExchangeServiceBase.Invoke` reports "An unknown error
  occurred" on `HadErrors` with an empty stream; the "The running command stopped" prefix on
  `RunAsync` failures; export file names unzoned). Do not re-raise them. Repo rules: `AGENTS.md`;
  `.agents/repo-guidance.md` "Known Failure Classes" (1, 2) and the ClickGateRegistry rule;
  `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat cafef918`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. Part 1: `-ErrorAction Stop` plus `InvokeOptional` - the recorded
assumption that `Get-MigrationBatch` with no batches writes no error (if it does, an empty tenant
shows the failure banner - acceptable?); the failure reason unwrapped from `ActionPreferenceStopException`;
a failed load CLEARING the list (the operator loses the selection and the open batch - right trade?);
Export withheld. Parts 2 and 5: `ReportedCount.Count` widened to `long?` (every consumer; the sorts;
the CSV). Part 4: `ScheduledCompletionUtc` only for Kind Utc. Part 6: `IsCancellation` searching the
chain through `GetValueInvocationException`. Part 7: Kind `Local` treated as naming no zone on all
three pages - the recorded assumption about the Exchange Online module's Kind; if the module does
return `Local` values that are this server's clock, every such time now reads "(time zone not
reported)" - is that the right fail-closed, and does any sort or decision now treat a `Local`
value differently from before in a way that changes behaviour (the LastSync sort; `ScheduledCompletionUtc`).
`Migration.razor` at 4377 lines - confirm no keyed line moved. Tests passing for the wrong reason:
the replaced `MigrationScheduleTests` theory; the stub runspaces.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
