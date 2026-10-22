# aud-a23e: Migration and Recipient Lookup show times with no zone, an absent creation time shows year 0001, and sorts ignore Kind

**Severity**: LOW - display and ordering, no wrong write. (1) Migration renders readable times in
the server's zone (`ToLocalTime`) with no zone named, and Recipient Lookup prints them as read, so a
`Local` and a `Utc` value of the same instant show different clocks and the operator cannot tell
which zone a time is in - the aud-a7f ambiguity in two more modules. (2) An absent
`CreationDateTime` still shows "Jan 01, 0001 00:00" in the batch header and CSV and sorts first
ascending (`ReportedTime.Of(DateTime.MinValue)` preserved the old default). (3) Readable times are
compared ignoring their Kind, so a list mixing kinds could sort wrongly (hypothetical; the live
module's kind is unverified).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23d/a7i agent, 2026-10-22, candidates 1, 2 and 4 of its hand-back)

## Evidence

At `4c54621b`: `Components/Pages/Migration.razor` `FormatBatchDate` (`ToLocalTime`, no zone text),
the Created/LastSync sorts (`ReportedTime.SortGroup` then the raw `DateTime`); `Services/MigrationService.cs`
`?? ReportedTime.Of(DateTime.MinValue)` for an absent `CreationDateTime`; `Components/Pages/RecipientLookup.razor`
lines ~73-74 (`Show` as read).

## Approach (to be written by the implementing agent before code)

- Render every readable time through `ShowReported` with a named zone: the operator's browser zone
  where the page already captures one (aud-a7e's interop lives on the Out of Office page; reuse the
  same JS helper and read pattern, or render UTC labelled "UTC" where capturing a zone would be a
  larger page change - state the choice per page). Never an unlabelled clock.
- An absent `CreationDateTime` is `null`, shown as "(not reported)", and sorts with `SortGroup` 2
  (none), not as year 0001. Keep the CSV cell empty for absent, the marker for unreadable.
- Compare readable times as instants (`ToUniversalTime` for `Local`/`Utc`; `Unspecified` grouped
  separately, since it names no instant), so Kind-mixed lists sort by instant.
- `Migration.razor` has line-keyed `ClickGateRegistry` entries and `ExpectedLineCount` 4377: edit in
  place or re-anchor per the playbook. Tests: render-tree and CSV assertions for the zone label; the
  absent creation time; a Kind-mixed sort. Mutations at the call site: drop the zone label; restore
  the MinValue default; compare raw `DateTime` again.

## Guard proof

Pending.
