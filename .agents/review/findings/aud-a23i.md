# aud-a23i: Migration - an identity-less mailbox row is a targetable "Unknown", a throwing row loses the list, failed loads report progress as success, two generic error texts, and unzoned export file names

**Severity**: LOW. (1) `GetMigrationBatchUsersAsync`: a mailbox row with no Identity becomes
"Unknown", which an action can target; a row whose getter throws loses the whole mailbox list (the
aud-a23f shape on the mailbox list). (2) `LoadBatchList` and every load on the Migration page
complete their progress activity as `true` on failure, so the status bar reports success after a
failed load. (3) `ExchangeServiceBase.Invoke` fails with "An unknown error occurred." whenever
`HadErrors` is set with an empty error stream, which a nested suppressed error causes (not seen
live). (4) Under `-ErrorAction Stop` the error stream is empty, so app-wide `RunAsync` failure
messages carry the "The running command stopped because..." prefix instead of the cmdlet's own
message (aud-a23g unwrapped it for one path only). (5) The Migration export file names use the
server's clock with no zone.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
**Branch**: `worktree-agent-a3e674616251be4de` (forked at `34c7ccd4`)
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23g agent, 2026-10-22, candidates 3 to 7 of its hand-back)

## Evidence

At `cafef918`: `Services/MigrationService.cs` `GetMigrationBatchUsersAsync` (identity default,
per-row throw); `Components/Pages/Migration.razor` `LoadBatchList` and the other loads' progress
completion; `Services/ExchangeServiceBase.cs` `Invoke` (the empty-stream branch) and `RunAsync`'s
failure message; the export file-name composition.

## Approach (to be written by the implementing agent before code)

- Mailbox rows: the aud-a23f shape - an identity-less row is listed as unreadable (not targetable);
  a throwing row is listed with its identity as far as read and the reason; the rest of the list
  loads.
- Progress: a failed load completes its activity as not succeeded with the failure text (the
  aud-a26h drain precedent); every load on the page.
- `ExchangeServiceBase`: when `HadErrors` is set with an empty stream, report that shape honestly
  ("a nested command reported an error it did not surface") rather than "unknown"; unwrap
  `ActionPreferenceStopException.ErrorRecord` in `RunAsync`'s failure path once, for every caller (the
  aud-a23g part-1 unwrapping moves into the base).
- Export file names in UTC with a `Z` suffix.
- Tests: identity-less and throwing mailbox rows; a failed load's activity outcome; the base class
  message shapes; a file name. Mutations at the call site per part. `Migration.razor` line-keyed
  entries: edit in place or re-anchor per the playbook.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `cef232a1`. One commit `fix(migration,shared): ...
(aud-a23i)`, after aud-a23h on the same branch. Behaviour: Migration (parts 1, 2, 5) and the base app (parts 3, 4,
`Services/ExchangeServiceBase.cs`: every `ExchangeServiceBase` subclass's `Invoke` failure text, and every `RunAsync`
caller's failure message - Migration, Blocked Senders, Calendar Permissions, Conference Rooms, Out of Office). Bumps
are the supervisor's. Written before the code; line numbers at `34c7ccd4`; the page in place, 4377 lines, no
registered line or snippet among the edits.

1. **Mailbox rows, the aud-a23f shape.**
   - `Models/MigrationModels.cs`: new `UnreadableMigrationMailbox(string? Identity, string Reason)` (`Shown`;
     `Warning` - "N mailbox(es) could not be read: <name>: <reason>; ...") and `MigrationMailboxList(Mailboxes,
     Unreadable)`, positional, so the page's one-line hand-off to `ReplaceBatchUsers` holds.
   - `Services/MigrationService.cs` `GetMigrationBatchUsersAsync` (763-813) returns it. Per row, Identity first: absent
     or blank - unreadable, "a row with no Identity" (it was a mailbox "Unknown", 782, which an action could target); a
     row that throws while read - unreadable with its identity as far as read (or "a row whose Identity could not be
     read: <message>") and the message, `when (!IsCancellation(ex))`; the rest load. A failed query still throws.
   - Page: 1639 (blank) the `unreadableMailboxes` field; `ReplaceBatchUsers` (3962) takes the list and deconstructs it
     on a line freed by rewrapping its comment (3964-3973, ten lines to nine) - `batchUsers = users;` (3978) stays as
     written, pinned by `MigrationStatusPageTests` - and assigns `unreadableMailboxes` at 3980 (blank); the two failure
     calls (1885, 1948) pass an empty list. 1026 (blank): the warning, `alert-warning`, `role="alert"`, when the list
     is loaded and any row was unreadable. 1049, 1054: neither "N pending. Batch not started." nor "This batch
     contains no mailboxes." when rows were unreadable; 1060 then reads "No mailbox in this batch could be read; the
     warning above names each one." Unreadable rows are not in `batchUsers`, so no tick box, planner or action
     reaches them.
2. **Progress: every load completes its activity with its real outcome** - not succeeded, with the failure text (the
   aud-a26h drain precedent; from a local where an await sits between the failure and the `finally`):
   `LoadBatchList` (2304, from `batchListFailure`, which both paths write with no await before the `finally`);
   `LoadMailboxesFor` (1878 local; 1883 `catch (Exception ex)`; 1886-1887 become `mailboxLoadFailed |= ...` and the
   failure text; 1893); `SyncOpenBatchFromUrl` (1938, 1946, 1949-1950, 1956, the same); `CheckSingleEligibility`
   (1973 local; 1977 an Undetermined result; 2006 the catch; 2013); `CheckBulkEligibility` (2114; 2123 any
   undetermined row - "N of M eligibility checks could not be determined"; 2150 the catch; 2157); `SearchUser` (3844;
   3869 and 3896, the two "Search failed" results - "no match" and "several match" are answers; 3905);
   `FetchUserReport` (4040; 4064-4065, the failure recorded before the superseded-load return; 4078). The comment at
   1555-1562 says so, rewritten in its eight lines. The writes still complete true (ENDED): not this finding.
   `ActivityProgressService.Finish` discards both today; the handle is still the module's statement of how it ended.
3. **`ExchangeServiceBase.Invoke`** (227-237): `HadErrors` with an empty stream fails with new `internal const
   ErrorNotSurfaced` - the command flagged an error but surfaced none, which an error suppressed by `-ErrorAction
   SilentlyContinue` or `Ignore` (on a command it ran, or on itself) causes - not "An unknown error occurred.". Still a
   failure. The text names neither a connection nor a session, so `IsConnectionError` (message-based) still reads it
   as no connection fault.
4. **`RunAsync`'s failure message** (74): through new `internal static ResolveFailure(Exception)` beside
   `ResolvePsErrors` (194): an `ActionPreferenceStopException` with a record - the exception, or one level in, which is
   `Invoke`'s wrapper - gives the record's own text, any captured records as the detail; otherwise `ResolvePsErrors`,
   as before. One place, every caller. `MigrationService.FailureReason` (750-751) goes; `GetMigrationBatchesAsync`
   (729) and aud-a23h's callers use `ResolveFailure(ex).primary`.
5. **Export file names in UTC, with `Z`** (`yyyyMMdd_HHmmss'Z'`, invariant culture): the batch CSV (4238), the report
   text (4314: the report's UTC fetch time, no `ToLocalTime()`), the report zip (2995).

**Tests** (fakes only):
- `MigrationBatchReadTests`, extended: a mailbox row with no Identity and a blank one beside readable rows - not
  listed, unreadable, no "Unknown" mailbox; a row whose Status getter throws, and one whose Identity getter throws -
  unreadable with identity and reason, the rest load; through the page's `LoadMailboxesFor`, the warning, and no row
  for them in `batchUsers`; only unreadable rows - "No mailbox in this batch could be read", not "contains no
  mailboxes"; a cancellation while reading a mailbox row escapes. `GetMigrationBatchUsersAsync`'s callers in
  `MigrationBatchReadTests`, `MigrationMailboxLoadTests` and `MigrationReportedTimeTests` take `.Mailboxes`,
  expectations unchanged.
- new `MigrationLoadProgressTests`: a recording `IActivityProgress` whose first Complete wins, as the real one's: each
  load above, failing - not succeeded, with the failure text; succeeding - succeeded.
- `ExchangeServiceBaseInvokeTests`: a nested `-ErrorAction SilentlyContinue` error - `ErrorNotSurfaced`.
- new `ExchangeServiceBaseRunFailureTests`, the real pool over stub runspaces: outside Migration,
  `BlockedSenderService.UnblockSenderAsync` over a `Remove-BlockedSenderAddress` that writes an error under Stop - the
  message is the error's own text, no "The running command stopped"; one that only suppresses a nested error -
  `ErrorNotSurfaced`; inside, `MigrationService.StopMigrationUserAsync` the same. aud-a23g's `AFailedBatchQuery...`
  stays green.
- new `MigrationExportFileNameTests`, a recording `IJSRuntime`: each name `..._\d{8}_\d{6}Z.<ext>`, the stamp inside
  the run's UTC window (this host is Eastern, so the server's clock is hours off); the report text's stamp is its fetch
  time exactly.

**Mutations at the call site** (md5 before/after, failing tests named, restored, md5 equal, touched, rebuilt, green):
I3 (the brief's (3)) `?? "Unknown"` identity again; I4 (the brief's (4)) `LoadBatchList` completes true again; I5 (the
brief's (5)) `RunAsync` back to `ResolvePsErrors`. Extras: I3b the per-row catch gone; I3c the warning line blank
again; I3d the unreadable empty-state text dropped; I4b `LoadMailboxesFor` true again; I4c `CheckSingleEligibility`
true again; I5b `ResolveFailure` without its Stop branch; I6 "An unknown error occurred." again; I7 the CSV name on the
server's clock again; I7b the report text's name `ToLocalTime()` again.

**Gates** as aud-a23h. Comments written after the mutation round.

## Guard proof

Pending.
