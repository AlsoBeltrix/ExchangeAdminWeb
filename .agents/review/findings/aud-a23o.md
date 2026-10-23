# aud-a23o: Exchange base - the on-prem size read turns an unparseable size into 0 and an archive failure into "no archive", two recipient reads run under Ignore/SilentlyContinue, and the dead group-membership method keeps the substring match

**Severity**: MEDIUM - the aud-a23h/a23k class in shared code. (1) `ExchangeServiceBase.GetOnPremMailboxSizeAsync`:
an unparseable `TotalItemSize` reads as 0 (`ParseSizeToGB ?? 0`) and any archive-read failure reads as
"no archive", so the ToCloud quota check in `MigrationService` can pass a mailbox whose size could not be
read. (2) `MigrationService` files a failed ToCloud size read as Ineligible ("Could not verify on-prem
mailbox size...") rather than Undetermined, and names "on-prem connection unavailable" as the cause when the
base returns null for ANY failure - a guess. (3) `HasCloudMailboxAsync` reads `Get-Mailbox` under
`-ErrorAction Ignore` and `OnPremRecipientExists` under `SilentlyContinue`: a failed read is "no mailbox".
(4) `CheckAdGroupMembership` has no caller since aud-a23j but still carries the substring match and the
Eligible-when-no-user return; a future caller inherits the defect.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: `worktree-agent-ac3fd1dd1db4f9c1d` (forked at `5eac8b25`)
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23j-n agent, 2026-10-22, candidates 1, 2, 3 and 6 of its hand-back)

## Evidence

At `09d3bacb`: `Services/ExchangeServiceBase.cs` `GetOnPremMailboxSizeAsync` (around 521 and 797, the
`?? 0`; 538-541 the archive catch), `HasCloudMailboxAsync` (588-591), `OnPremRecipientExists` (649),
`CheckAdGroupMembership` (703-748); `Services/MigrationService.cs` around 199-240 (the quota check and the
Ineligible filing with the guessed cause).

## Approach (to be written by the implementing agent before code)

- The size read returns a typed outcome (sizes, or unreadable with the reason; archive not-found by
  structure is "no archive", any other archive failure is unreadable - the aud-a23k shape). The ToCloud quota
  check treats unreadable as Undetermined with the base's reason in the cmdlet's own words; never Ineligible,
  never a guessed cause.
- The two recipient reads run under Stop with `IsNotFound` (structure, never text) deciding "no mailbox";
  any other failure is a failed read the caller reports as such.
- `CheckAdGroupMembership` is removed (base-app change; the aud-a23j check replaced it).
- Tests through `MigrationStubExchange` and the base's stand-in; mutations at the call site: `?? 0` again;
  the archive catch again; `Ignore` again at one site.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `3a948129`. One commit `fix(shared,migration): ...
(aud-a23o)` on branch `worktree-agent-ac3fd1dd1db4f9c1d` (forked at `5eac8b25`), after aud-a23r. Base app
(`Services/ExchangeServiceBase.cs`) and Migration; the callers outside Migration that the finding reaches are named in 6.
Bumps are the supervisor's (`ExchangeAdminWeb.csproj`, `Modules/ModuleCatalog.cs` not edited). Line numbers at `5eac8b25`.

1. **One not-found rule and one size reader, in the base.** aud-a23h's `IsNotFound` (with `NamesNotFound`, `Identifiers`,
   `IdentifierPart`), `ReadUnlessNotFound`, aud-a23g's `IsCancellation` and aud-a23k's `ReadSize` move from
   `MigrationService` (920-989, 1474-1488) to `ExchangeServiceBase` unchanged, so the base's reads decide "not found" by
   the same structure; aud-a23k's `CloudMailboxSize` (1427) becomes the base's `MailboxSizes`, the shape both size reads
   return. `MigrationService` uses them as before.
2. **The on-prem size read returns `MailboxSizes`, never 0** (`GetOnPremMailboxSizeAsync`, 484-565):
   - no server configured, no credentials, or a session that cannot be opened: both sizes unreadable with that reason -
     `OnPremNotConfigured`, `OnPremCredentialsUnavailable`, or the connection's failure in its own words
     (`ResolveFailure`) - where each was `null`;
   - the mailbox, `Invoke-Command -ErrorAction Stop` over `Get-MailboxStatistics -ErrorAction Stop` through `Invoke`: a
     failure is "Get-MailboxStatistics failed: <reason>"; no row, no TotalItemSize, or a size `ParseSizeToGB` cannot read
     are `ReadSize`'s markers - where a size it could not read was 0 (521, `ParseExchangeSize`'s `?? 0` at 797);
   - the archive, the same read with `-Archive`: on-premises Exchange's not-found, by structure (`IsNotFound`), is no
     archive; any other failure is "Get-MailboxStatistics -Archive failed: <reason>"; no row, or a size that does not
     parse, is its marker - where every failure was no archive (538-541). Neither catch takes a cancellation.
   - `ParseExchangeSize` (795-798) loses its two callers and goes.
3. **The ToCloud quota check reads it** (`MigrationService` 192-221 and the catch 234-241): the sizes and their markers go
   into the result (the page already shows a marker as "could not be read (<reason>)", aud-a23k); a readable size over the
   quota is Ineligible as before; an unreadable size makes an Eligible verdict Undetermined - an Ineligible one stands, its
   reason known (the aud-a23h rule) - with "Could not be determined: the on-premises mailbox size could not be read:
   <the base's reason>" (or "archive size", or "mailbox and archive sizes" for one reason for both). Never Ineligible, never
   "(on-prem connection unavailable)", which named a cause the base never reported. The catch the same, both sizes marked.
4. **The two recipient reads run under Stop.** `HasCloudMailboxAsync` (583-593): `Get-Mailbox -ErrorAction Stop` through
   `ReadUnlessNotFound`. `OnPremRecipientExists` (644-655): `-ErrorAction Stop` in the remote script and on
   `Invoke-Command`, through `ReadUnlessNotFound`; no session is a failure, not "no recipient" (646-647). Not found, by
   structure, is "no mailbox"; any other failure throws. Their callers report it:
   - `GetMailboxLocationAsync` (567-573) throws "Exchange Online could not be asked: <reason>" for a failed cloud read;
   - `GetOnPremMailboxLocationAsync` (595-642) throws "on-premises Exchange could not be asked: <reason>" for a failed
     read (naming the cmdlet), a connection that fails, no server configured or no credentials, where each was `null`
     (632-636, 597-608), which `GetMailboxLocationAsync` turned into "Unknown".
   "Unknown" now means both sides answered that they hold no such mailbox. The pool's own handling of a thrown pooled
   query (retry, discard - psl-4) is unchanged: `HasCloudMailboxAsync` throws the original exception.
5. **`CheckAdGroupMembership` (699-748) is removed** - no caller since aud-a23j, and it kept the substring match and the
   Eligible-when-no-user return; with its section header.
6. **Callers outside Migration, where the finding reaches them:**
   - `Services/RecipientLookupService.cs` (131-149): a size is set only when it was read; each unreadable one is a warning
     naming why ("Could not read the on-premises mailbox size: <reason>."), where an unparseable size showed 0 GB, a failed
     archive read 0 GB, and any failure "On-premises mailbox size unavailable (connection not configured)." Its location
     read (117-128) already catches a throw as "Unknown" with a warning naming it: a failed read now reaches that warning.
   - `Components/Pages/CalendarPermissions.razor` 431 and `Components/Pages/MailboxPermissions.razor` 461, each in place
     on its one line (both line-keyed by `ClickGateRegistry`; their line counts held, no registered line touched): a
     location read that fails is refused, before any write, as "Could not determine mailbox location for <target>:
     <reason>. Nothing was changed." through new `ExchangeServiceBase.LocationReadFailure`. A throw there reached each
     page's catch, "Operation may have succeeded but audit logging failed: ...", which says of a read before any write that
     a write may have happened. Blocked Senders, Conference Rooms and Out of Office call none of the changed members.
7. **Labelled assumptions (no live call).** On-premises Exchange's not-found, through remoting, keeps the category, reason
   or FailureCategory shape `IsNotFound` reads, for Get-Mailbox, Get-RemoteMailbox and Get-MailboxStatistics -Archive (a
   mailbox with no archive); Exchange Online's for Get-Mailbox is aud-a23h's assumption, now also under Calendar
   Permissions, Mailbox Permissions and Recipient Lookup. If either is wrong, a ToCloud check of a mailbox with no archive
   reads Undetermined, and a location read of a mailbox not on that side fails closed with Exchange's own words - visible,
   never a guess; one dev check of each record before a deploy settles it.

**Tests** (fakes only). `MigrationStubExchange` gains an on-premises stand-in: the service's `ConnectOnPrem` and
`GetModuleCredentialsAsync` seams overridden (no Delinea, no session), a server URI only when a test asks for one, and in the
service's own runspace an `Invoke-Command` that runs the script locally, on-prem `Get-MailboxStatistics` (with `-Archive`),
`Get-Mailbox` and `Get-RemoteMailbox` logged as "OnPrem ..." with their own failure keys; a `Get-Recipient` and a
`RecipientLookupService` built over the same stand-ins. New `ExchangeServiceBaseOnPremReadTests.cs`:
- **the size read, through a ToCloud check**: a size that cannot be read (thrown, written, no row, no TotalItemSize,
  "Unlimited") - Undetermined, the marker, the reason; never Eligible (before: an unparseable size was 0 and Eligible) and
  never Ineligible "(on-prem connection unavailable)" (a failed read); an archive that cannot be read (thrown, written,
  "Unlimited") - Undetermined (before: no archive, Eligible); no archive by each not-found shape - Eligible, 0, no marker;
  not configured, no credentials, a connection that fails - Undetermined with the base's words; readable sizes over and
  under the quota (controls); an Ineligible size answer with an unreadable archive - Ineligible, both reasons; the page's
  single result for an unreadable size - "could not be determined", the marker, no "IS NOT eligible".
- **the recipient reads, through `GetMailboxLocationAsync`**: a cloud mailbox - Cloud, nothing on-premises asked; a cloud
  read that fails (thrown, written) - "Exchange Online could not be asked: ..." (before: on to on-premises, "Unknown"); not
  in the cloud - on-premises Get-Mailbox answers OnPrem, Get-RemoteMailbox Cloud, neither Unknown; an on-premises read that
  fails, each cmdlet (thrown, written), a cmdlet the session lacks, no server, no credentials, a connection that fails -
  "on-premises Exchange could not be asked: ..." (before: "Unknown"); `LocationReadFailure`'s text.
- **Recipient Lookup**: an on-premises mailbox whose size cannot be read - no size, a warning naming why (before: 0 GB); one
  whose archive read fails - the mailbox size, the archive's warning; a readable one - both sizes, no warning.
- **the pages**: each page's location read is the guarded one-line form, refusing through `LocationReadFailure` before any
  write (source, as `ValidatedWriteCallSiteTests` reads those lines - neither page has a handler harness).
- `CheckAdGroupMembership` and `ParseExchangeSize` no longer exist on the base (reflection).
- Existing tests that pinned the old size wording (`MigrationExcludedGroupTests`' size-step reason) take the new one.

**Mutations at the call site** (md5 before/mutated/after, failing tests named, restored, touched, rebuilt, green): O1 (the
record's) `?? 0` again on the on-prem mailbox size; O2 (the record's) the archive catch again (any failure no archive); O3
(the record's) `-ErrorAction Ignore` again on `HasCloudMailboxAsync`. Extras: O4 an unreadable size filed Ineligible again;
O5 `SilentlyContinue` again on `OnPremRecipientExists`; O6 `GetOnPremMailboxLocationAsync`'s catch returns null again; O7
Recipient Lookup shows an unreadable size as 0 again; O8 a page's location read unguarded again.

**Gates** as the brief, plus every `ExchangeServiceBase` subclass's tests (Blocked Senders, Calendar Permissions,
Conference Rooms, Out of Office, Mailbox Permissions, Recipient Lookup, Delegation Report, Message Trace); comments after
the mutation round.

## Guard proof

Pending.
