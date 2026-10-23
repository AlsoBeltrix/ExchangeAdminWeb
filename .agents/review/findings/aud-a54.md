# aud-a54: Recipient Lookup reads Get-Mailbox under Ignore and swallows the cloud archive statistics failure

**Severity**: LOW/MEDIUM - the aud-a23h/a23o class in Recipient Lookup. `RecipientLookupService` runs its own
`Get-Mailbox -ErrorAction Ignore`, so a failed cloud read shows as "not a mailbox", and wraps the cloud
archive statistics read in `catch { }`, so any failure shows as no archive. aud-a23o fixed the on-prem size
path this service consumes; these two cloud reads are its own.
**Status**: Fixed on branch; closed on guard proof; not merged
**Branch**: `worktree-agent-ae9cc385925087b25` (forked at `a7300dee`)
**Commit**: the `fix(recipient-lookup): ... (aud-a54)` commit after aud-a55's (SHAs in the closeout)
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

Built as the Plan. Line numbers are at the fix commit's bytes, which carry no new code comments (they come in their own
commit after this round). `Components/Pages/RecipientLookup.razor` is 232 lines before and after (`git diff --numstat` 2/2,
each row replaced in place); it is not line-keyed, and `ProgressRegistry`'s one pin (`RunLookup` reports
`LookupService.GetRecipientInfoAsync`) is unchanged.

- `Services/RecipientLookupService.cs`:
  - The mailbox read (50-62): `Get-Mailbox -ErrorAction Stop` through `ReadUnlessNotFound` (56); a failure that is not a
    cancellation sets `MailboxUnreadable` and `ArchiveUnreadable` to "Get-Mailbox failed: <reason>" (60) and adds "The
    Exchange Online mailbox could not be read: Get-Mailbox failed: <reason>."; nothing more of the mailbox is read, `Error`
    stays null, and the location and on-premises steps run as before.
  - The archive read (90-121): through `Invoke` as before; no row is `ArchiveUnreadable` "Exchange returned no archive
    statistics" (98-100); Exchange's not-found, by structure, is caught first (111, `IsNotFound`) - no archive, logged; any
    other failure that is not a cancellation is "Get-MailboxStatistics -Archive failed: <reason>" (117); either marker adds
    "The Exchange Online archive could not be read: <reason>." (120).
- `Models/LookupModels.cs` `RecipientInfoResult`: `MailboxUnreadable` (119), `ArchiveUnreadable` (120).
- `Components/Pages/RecipientLookup.razor`: the Archive row (91) "Could not be read" when `ArchiveUnreadable` is set, else
  Enabled / Disabled; the Forwarding row (98) "Could not be read" when `MailboxUnreadable` is set, else the address or "None".

New `ExchangeAdminWeb.Tests/RecipientLookupCloudReadTests.cs`, 6 tests (12 cases), the real service over
`MigrationStubExchange.RecipientLookup()` and the page's compiled render tree:

| Test | Pins |
| --- | --- |
| `AMailboxReadThatFails_IsAWarning_NeverNoMailbox` (thrown, written) | **the finding**: no `Error`; "Get-Mailbox failed: <reason>" in both markers; the warning; no statistics asked; rendered Forwarding and Archive "Could not be read", never "None" / "Disabled". Before: no warning, "None", "Disabled" |
| `NoMailbox_IsNoMailbox` (category, type, identifier) | Exchange's not-found by structure: no marker, no warning at all, no statistics asked; "None", "Disabled" - the old meaning |
| `AnArchiveReadThatFails_IsAWarning_NotNoArchive` (thrown, written) | **the finding**: `ArchiveEnabled` false, the archive marker, exactly the one warning; the mailbox's statistics still read (1.5); rendered "Could not be read", never "Disabled". Before: "Disabled", nothing said |
| `AnArchiveReadWithNoRow_IsAWarning_NotNoArchive` | an answer with no row: "Exchange returned no archive statistics", the warning, "Could not be read" |
| `NoArchive_IsNoArchive` (category, type, identifier) | no marker, no warning, "Disabled" |
| `AnArchive_IsShown` | the control: Cloud, "Enabled", the archive's 1.5 GB, "None", no warning |

Existing tests that reach the changed reads pass unchanged: `ExchangeServiceBaseOnPremReadTests`' four `RecipientLookup_*`
(their cloud `Get-Mailbox` answers not-found, or fails and is asserted by the location warning it also reaches) and
`RecipientLookupReportedTimeTests` (its stand-in's archive read answers no row, which is now a warning; those tests assert
the times and no `Error`).

**Against the unfixed code**: B1 and B2 below put back exactly the unfixed reads at their call sites (`Ignore` through
`InvokeOptional`; the bare `catch { }`), and each fails the tests that pin it.

Mutations at the call site, by the same driver and protocol as aud-a55's (session scratchpad `work/mutate.sh`,
`work/round-a54.sh`, the texts in `work/muts/`), over the 1946 tests of the same filter. Bytes (LF) before every mutation
and after every restore: service `06eb00538881d1bd6aee1e319336796e`, page `85d1f1eb28390e9699ea2b65f30d459c`.

| Mutation | File | md5 mutated | Failed (of 1946) | Restored, rerun |
| --- | --- | --- | --- | --- |
| B1 (the record's): `-ErrorAction Ignore` again on `Get-Mailbox`, through `InvokeOptional` | service | `6a5ff2862d26566569d396164b55bdc6` | 2: `AMailboxReadThatFails...` (thrown, written) - not a mailbox again, nothing said | equal; 1946/0 |
| B2 (the record's): the bare `catch { }` again on the archive read | service | `8abe87a9c05c9763a186cb78ce9785a0` | 2: `AnArchiveReadThatFails...` (thrown, written) - no archive again | equal; 1946/0 |
| B3: an archive read with no row as no archive again | service | `e5e3da7e091092a4384bc9c5908517ab` | 1: `AnArchiveReadWithNoRow...` | equal; 1946/0 |
| B4: the page's Archive row ignoring `ArchiveUnreadable` | page | `6ddf941092eff4d3cfcd57b1e68dd6f0` | 5: `AMailboxReadThatFails...` (2), `AnArchiveReadThatFails...` (2), `AnArchiveReadWithNoRow...` - "Disabled" again | equal; 1946/0 |
| B5: the page's Forwarding row ignoring `MailboxUnreadable` | page | `e31403e4976bc2c5a150dd3dae86cd8a` | 2: `AMailboxReadThatFails...` (thrown, written) - "None" again | equal; 1946/0 |

md5s (LF) of the other files at the fix commit: `Models/LookupModels.cs` `5cb0e0d149af942d039a92164aefa21c`,
`RecipientLookupCloudReadTests.cs` `375632443fac7464e2eb544865d452cb`.

On the fix commit's tree: `dotnet build ExchangeAdminWeb.slnx -c Release` 0 errors, no warning outside the baseline's 19
(none in a touched file); the 1946 focused tests 1946/0; `dotnet format ExchangeAdminWeb.slnx --verify-no-changes
--no-restore` exit 0; `git diff --cached --check` exit 0; `tools/Test-AsciiOnly.ps1` exit 0. No Exchange was asked anything:
every Exchange Online cmdlet was a stub in an in-process runspace, every on-premises one a stub defined through the service's
own `ConnectOnPrem` seam. The full suite runs at the branch tip.

## Known gaps

- **Assumptions, labelled (no live call):** Exchange Online reports a mailbox with no archive, from `Get-MailboxStatistics
  -Archive`, in a not-found shape `IsNotFound` reads (aud-a23k's, the same read), and a recipient with no Exchange Online
  mailbox, from `Get-Mailbox`, likewise (aud-a23h/a23o's). If either is wrong, such a recipient reads "Could not be read",
  with a warning in Exchange's own words - visible, never a guess. **This is the change's biggest risk**: if the archive's
  not-found is not recognised, every cloud mailbox without an archive shows "Archive: Could not be read" until it is; one
  dev lookup of such a mailbox settles it.
- `Get-Mailbox` answering no rows and no error is still "not a mailbox", as the base's own location read
  (`HasCloudMailboxAsync`) takes it; the archive read's no-row answer is a marker, as both size readers take it.
- Outside this finding and unchanged (named as candidates in the hand-back): the mailbox statistics read's warning and the
  `Get-Recipient` failure carry the Stop preference prefix; the on-premises archive is never shown (`ArchiveEnabled` is set
  only by the cloud read).
- No circuit or browser ran: the page's compiled render tree was read directly.
