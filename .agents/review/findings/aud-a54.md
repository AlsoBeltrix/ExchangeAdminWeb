# aud-a54: Recipient Lookup reads Get-Mailbox under Ignore and swallows the cloud archive statistics failure

**Severity**: LOW/MEDIUM - the aud-a23h/a23o class in Recipient Lookup. `RecipientLookupService` runs its own
`Get-Mailbox -ErrorAction Ignore`, so a failed cloud read shows as "not a mailbox", and wraps the cloud
archive statistics read in `catch { }`, so any failure shows as no archive. aud-a23o fixed the on-prem size
path this service consumes; these two cloud reads are its own.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23r/o/q/p agent, 2026-10-22, candidate 2 of its hand-back)

## Evidence

At `d690a6c1`: `Services/RecipientLookupService.cs` lines 50-53 (the `Ignore` read) and 99-101 (the bare
catch); `Services/ExchangeServiceBase.cs` `ReadUnlessNotFound` / `IsNotFound` (aud-a23o, the shape to use).

## Approach (to be written by the implementing agent before code)

- Both reads run under Stop through `ReadUnlessNotFound`: not-found by structure is "no mailbox" / "no
  archive"; any other failure is shown on the lookup as a warning naming the read and the reason (the aud-a23o
  wording), never as an absence.
- Tests through the Recipient Lookup stand-in; mutations at the call site: `Ignore` again; the bare catch
  again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `59061f01`. One commit `fix(recipient-lookup): ...
(aud-a54)` on branch `worktree-agent-ae9cc385925087b25` (forked at `a7300dee`), after aud-a55. Module-scoped: Recipient
Lookup (the bump is the supervisor's; `Modules/ModuleCatalog.cs`, `ExchangeAdminWeb.csproj` not edited).
`Services/ExchangeServiceBase.cs` is read and its helpers called (`ReadUnlessNotFound`, `IsNotFound`, `IsCancellation`,
`ResolveFailure`, `Invoke`), not changed. Line numbers at `a7300dee`.

1. **The cloud mailbox read** (`Services/RecipientLookupService.cs` 50-54): `Get-Mailbox -ErrorAction Stop` through
   `ReadUnlessNotFound` - the base's own location read's shape (`HasCloudMailboxAsync`, aud-a23o). Rows: the mailbox, as
   before. None - Exchange said there is no such mailbox (not-found by structure, `IsNotFound`): not a mailbox, as before
   (no forwarding, statistics or archive read; the page's "None" / "Disabled" stand). Any other failure (not a
   cancellation, the aud-a23g rule): new `RecipientInfoResult.MailboxUnreadable` = "Get-Mailbox failed: <reason>" (the
   cmdlet's own words, `ResolveFailure`), `ArchiveUnreadable` the same, a warning "The Exchange Online mailbox could not be
   read: Get-Mailbox failed: <reason>.", and nothing more of the mailbox is read - where `-ErrorAction Ignore` made every
   failure, a throw included, read as "not a mailbox". `r.Error` is not set: the recipient was read, and the location and
   on-premises steps run as before.
2. **The cloud archive statistics read** (82-101): `-ErrorAction Stop` through `Invoke`, as now. Exchange's not-found, by
   structure (`IsNotFound`, caught first), is no archive, as before (logged, as the base's on-premises archive read logs
   it); any other failure (not a cancellation) is `ArchiveUnreadable` = "Get-MailboxStatistics -Archive failed: <reason>";
   a read that answers with no row and no error is `ArchiveUnreadable` = "Exchange returned no archive statistics" - not a
   not-found by structure, so not an absence (the marker both size readers already give that answer: aud-a23o's
   on-premises read, and Migration's cloud read after aud-a55). Either is a warning "The Exchange Online archive could not
   be read: <reason>." - where `catch { }` made every failure no archive.
3. **`Models/LookupModels.cs` `RecipientInfoResult`**: `MailboxUnreadable`, `ArchiveUnreadable` - why each could not be
   read; null when it was read, or when Exchange said there is none.
4. **The page shows neither as an absence** (`Components/Pages/RecipientLookup.razor`; not line-keyed - "tier 4, not
   approved" in `ClickGateRegistry.NotYetConverted`; `ProgressRegistry` pins `RunLookup` only, which does not change):
   the Archive row (91) reads "Could not be read" when `ArchiveUnreadable` is set, else Enabled / Disabled as before; the
   Forwarding row (98) "Could not be read" when `MailboxUnreadable` is set, else the address or "None" as before. Each in
   place on its line. A warning beside "Disabled" or "None" would still state the absence the finding is about; the
   reason is in the warning, as aud-a23o's size warnings carry theirs.
5. **Assumptions, labelled (no live call):** Exchange Online reports a mailbox with no archive, from
   `Get-MailboxStatistics -Archive`, in one of the not-found shapes `IsNotFound` reads (aud-a23k's assumption, the same
   read), and a recipient with no Exchange Online mailbox, from `Get-Mailbox`, likewise (aud-a23h/a23o's). If either is
   wrong, such a recipient reads "Could not be read" with a warning in Exchange's own words - visible, never a guess; one
   dev lookup of a mailbox without an archive settles the first.
6. Unchanged, and outside this finding: `Get-Recipient` and the mailbox statistics read (their failures already reach
   `Error` / a warning), the location read and the on-premises size read (aud-a23o).

**Tests** - new `ExchangeAdminWeb.Tests/RecipientLookupCloudReadTests.cs`, the real service over
`MigrationStubExchange.RecipientLookup()` (Exchange Online `Get-Recipient`, `Get-Mailbox`, `Get-MailboxStatistics` with
`-Archive` - `Behaviour["ArchiveSize"]` null is no row, `Behaviour["NoArchive"]` the not-found) and the page's compiled
render tree (the `RecipientLookupReportedTimeTests` idiom), fakes only:
- `AMailboxReadThatFails_IsAWarning_NeverNoMailbox` (thrown, written): **the finding** - no `Error`; the warning naming
  `Get-Mailbox` and the reason; both markers set; no statistics asked; rendered Forwarding and Archive "Could not be read",
  never "None" / "Disabled". Before: no warning, "None", "Disabled".
- `NoMailbox_IsNoMailbox` (Exchange's not-found by category, type, identifier): no marker, no mailbox warning, no
  statistics asked; "None", "Disabled" - the old meaning.
- `AnArchiveReadThatFails_IsAWarning_NotNoArchive` (thrown, written): **the finding** - the archive marker and warning;
  `ArchiveEnabled` false; rendered "Could not be read", never "Disabled"; the mailbox's own statistics still read.
- `AnArchiveReadWithNoRow_IsAWarning_NotNoArchive`: "Exchange returned no archive statistics".
- `NoArchive_IsNoArchive` (category, type, identifier): no marker, no warning, "Disabled".
- `AnArchive_IsShown`: the control - "Enabled", its size.
- Existing tests that reach the changed reads stay as they are: `ExchangeServiceBaseOnPremReadTests`'
  `RecipientLookup_*` (their cloud `Get-Mailbox` answers not-found, or fails and is asserted by the location warning it
  also reaches) and `RecipientLookupReportedTimeTests` (its stand-in's archive read answers no row: now a warning; those
  tests assert the times and no `Error`).

**Mutations at the call site** (md5 before/mutated/after, failing tests named, restored from git, touched, rebuilt, md5
equal, filtered tests green): B1 (the record's) `-ErrorAction Ignore` again on `Get-Mailbox` (through `InvokeOptional`,
the catch never reached); B2 (the record's) the bare `catch { }` again on the archive read. Extras: B3 an archive read with
no row as no archive again; B4 the page's Archive row ignoring `ArchiveUnreadable` ("Disabled"); B5 the page's Forwarding
row ignoring `MailboxUnreadable` ("None").

**Gates** as the brief; comments after the mutation round, in their own commit.

## Guard proof

Pending.
