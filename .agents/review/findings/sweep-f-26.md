# sweep-f-26: reported times could all read "(time zone not reported)"; the Out of Office window is written as a UTC instant (sweep item 26)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 26: `OutOfOfficeRequest.ShowReported`
(`Models/LookupModels.cs`) labelled every time not of Kind Utc "(time zone not reported)", and since aud-a23g a Kind-Local time
too; results deserialized from CliXml are likely Local, so Migration, Recipient Lookup and Check Status would show the server's
clock with that label. Out of Office converts the typed window with the browser's zone and passes Kind Utc (`OutOfOfficeService.cs`).
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-f` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-f`, from master `624ff16e`)
**Commit**: `a6d9203d` (`fix(sweep-f, item 26): ...`) | **Reviewer**: implementer (candidate)

## Evidence

- Probed here (pwsh 7, this host in Eastern time): `PSSerializer.Deserialize` of `<DT>2026-11-02T09:00:00+05:30</DT>` gives
  Kind Local `11/1/2026 10:30 PM`, `ToUniversalTime()` `2026-11-02T03:30:00Z`; `...Z` gives Kind Utc; no offset gives Kind
  Unspecified. So a Local time out of the remoting layer is this process's clock for the instant the sender wrote - aud-a23g's
  "nothing proves it is this server's" is answered by the deserializer itself.

## Approach (as built)

- **(a) At the deserialization boundary.** `ReportedTime.Read` - the one reader of every reported time (Migration, Recipient
  Lookup, Out of Office) - turns a DateTime of Kind Local into its Utc instant (`AtTheBoundary`, `ToUniversalTime()` through
  the same zone the deserializer used). Downstream nothing changed: Utc is shown in the operator's zone (UTC on Migration) with
  zone and offset, ordered among the instants, and a Local CompleteAfter is a schedule. Kind Unspecified still names no zone and
  keeps "(time zone not reported)" - no blanket label, no guess. `ReportedTime.Of` (defaults, tests) is not a reader and keeps
  aud-a23g's rule for a Local value.
- **(b) Out of Office.** Already as the item asks (aud-a7e): the typed start and end are converted once from the browser zone
  the page captures (`OutOfOfficeRequest.ToUtc`, refusing a skipped or repeated hour) and passed as Kind Utc. Existing test
  `AScheduleEnteredInTheBrowsersZone_IsWrittenAsTheSameInstant_InUtc` (Kolkata, New York across DST, the repeated hour) is the
  test the item asks for; mutation M26d proves it bites. New `AnAutoReplyWindowTypedInTheBrowsersZone_ReadsBackAsTheSameClock`
  ties the write to the read: the instant written, handed back at another offset and deserialized Local, shows the clock typed.
- Doc comments in `ReportedTime`, `LookupModels`, `MigrationModels` say where Local now goes. README Troubleshooting "(time zone
  not reported)". Migration `1.27.3`, RecipientLookup `1.8.3`, OutOfOffice `1.1.6`.

## Where the code goes beside the fix shape

- aud-a23g part 7 is superseded for read values (its tests inverted: `ATimeOfKindLocal_IsShownAsTheInstantItNames`, the
  Recipient Lookup and Check Status twins, the sort test, two CompleteAfter tests lose their Local row).

## Assumptions not verified live

- That the Exchange Online module's own readers, where they are not CliXml, keep .NET's contract that Local means this
  machine's zone (an offset-bearing value converted, never a foreign clock merely marked Local).
- **No evidence** was found for how the EXO V3 `Set-MailboxAutoReplyConfiguration` carries a Kind-Utc DateTime (Microsoft's
  page speaks only of the local short-date format for typed text). It is an assumption that it is sent as that instant.

## Guard proof

| Mutation | Failing tests |
|---|---|
| M26a no boundary conversion | 14: `ReportedTimeBoundaryTests` (5), the inverted Migration, Recipient Lookup, Check Status and schedule tests, the sort test (4) |
| M26b every non-Utc read as Utc | 19, the five Unspecified-label tests among them |
| M26c Local re-marked Utc instead of converted | 14 (this host's offset is not zero) |
| M26d Out of Office writes the typed clock as Utc | 8: `AScheduleEnteredInTheBrowsersZone_IsWrittenAsTheSameInstant_InUtc` (3), `AnEditDuringTheOperation_...` (3), `AScheduleThatNamesNoInstant_...` (2) |

## Gates

Run once for the branch at its tip; see the sweep-f hand-back. Not run: a live read or write.

## Deploy-time checks

- The sweep's check 2 Kind line: the Kind of a Migration batch's `StartDateTime` and a mailbox's `WhenCreated` as the app's
  session receives them. Any Kind other than Unspecified is now shown with a zone; Unspecified still reads "(time zone not
  reported)".
- Schedule an auto-reply on a test mailbox from a browser in a non-UTC zone, then compare OWA and
  `Get-MailboxAutoReplyConfiguration <x> | Select StartTime,EndTime` with the times typed; Check Status should show the typed clock.
