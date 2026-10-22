# Defect review request: unreadable reported times shown with their text; Migration and recipient-lookup timestamps through one typed reader (aud-a7i, aud-a23d)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `c69bcd4e` or later.
- The change is the branch merged at `4c54621b`: `0c91a1f6` (plans), `1622f027` (aud-a7i), `86cd680f`
  (aud-a23d), forked from `f1460447`. Review `git diff f1460447 86cd680f`, and read the COMBINED
  `Models/ReportedTime.cs` (new; `Read`, `Of`, `SortGroup`, `Show`), `Models/LookupModels.cs`,
  `Models/MigrationModels.cs` (`AutoComplete`, `ScheduledCompletionUtc`, `CompleteAfterUnreadable`),
  `Services/MigrationService.cs`, `Services/RecipientLookupService.cs`, `Components/Pages/Migration.razor`
  (header, "Batch not started", mailbox row, the two sorts, the CSV), `Components/Pages/RecipientLookup.razor`,
  `Components/Pages/OutOfOffice.razor` (Check Status line) on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a7i.md` (with the nested-wrapper fold-in), `aud-a23d.md`;
  parents `aud-a7h.md`, `aud-a7f.md`, `aud-a23c.md`. ALREADY admitted and out of scope: `aud-a23e`
  (Migration shows times in the server's zone unnamed and Recipient Lookup as read; an absent
  `CreationDateTime` shows year 0001 and sorts first; Kind-mixed sorting) and `aud-a23f`
  (`GetMigrationBatchesAsync` drops a batch whose row throws while being read, logging only). Do not
  re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1, 2)
  and the ClickGateRegistry rule; `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 4c54621b`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a7i: the raw text in the marker (HTML-encoded by Blazor - and in the
CSV? in the notice?), the 80-character cap, the nested-wrapper type name; the schedule line shown for
either end with "(not reported)" for the missing one - the record notes a start-only schedule's end
now also reads "(not reported)" where it showed nothing (acceptable?). a23d: `AutoComplete` true for
an unreadable `CompleteAfter` (is "set to finalise" the right reading of an unknown schedule, or
should the batch be flagged as undecidable); `ScheduledCompletionUtc` null plus `CompleteAfterUnreadable`
- every consumer checked; "Batch not started" no longer shown for an unreadable start - what IS shown;
`SortGroup` ordering (unreadable after readable, before none) in both directions; the CSV cells;
`Migration.razor` edited in place at 4377 lines - confirm no keyed entry's line moved (compare the
registry's keyed lines against the diff hunks); the `ReportedTime.Of(DateTime.MinValue)` default for
an absent creation time (preserved on purpose - out of scope as a23e, but check it did not change).
Tests passing for the wrong reason: the stub runspaces returning what each test sets; render-tree
assertions.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
