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
**Status**: Merged `d690a6c1`; closed on guard proof; codex review pending; codex misc-batch-1 clean (2026-10-23)
**Branch**: `worktree-agent-ac3fd1dd1db4f9c1d` (forked at `5eac8b25`)
**Commit**: `990a02ab` (fix, tests, record); plan `2bf76220`; comments `9fa7664d` (shared with aud-a23r, aud-a23q, aud-a23p)
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

Built as the Plan. Line numbers are at the fix commit's bytes. The fix commit carries no new code comments: the moved
helpers keep the comments they had in `MigrationService` (reworded for their new home in the comments commit), and the
comments that described removed code went with it. `Components/Pages/CalendarPermissions.razor` (762 lines) and
`Components/Pages/MailboxPermissions.razor` (808) keep their line counts: `git diff --numstat` 1/1 each, the location line
replaced in place; no registered line or snippet changed.

- `Services/ExchangeServiceBase.cs` (base app):
  - `OnPremNotConfigured` (184), `OnPremCredentialsUnavailable` (186).
  - Moved from `MigrationService` unchanged: `IsCancellation` (355), `ReadUnlessNotFound` (368), `IsNotFound` (397) with
    `NamesNotFound`, `Identifiers`, `IdentifierPart`; `ReadSize` (580). aud-a23k's `CloudMailboxSize` is the base's
    `MailboxSizes` (565), with `Unreadable(reason)` and `UnreadableReason(where)` (569).
  - `GetOnPremMailboxSizeAsync` (595-) returns `MailboxSizes`: not configured and no credentials are both sizes unreadable
    with those reasons; the mailbox read (630) and the archive read (644) through new `AddOnPremRead` (681: `Invoke-Command
    -ErrorAction Stop` over the remote script), each through `Invoke` and `ReadSize`; the archive's not-found by structure
    (648) is no archive, any other failure "Get-MailboxStatistics -Archive failed: <reason>"; a session that cannot be
    opened is both unreadable with its words. `ParseExchangeSize` is gone.
  - `GetMailboxLocationAsync` (688): a failed cloud read is "Exchange Online could not be asked: <reason>" (697).
    `LocationReadFailure` (706). `HasCloudMailboxAsync` (717): `-ErrorAction Stop`, `ReadUnlessNotFound` (725).
    `GetOnPremMailboxLocationAsync` (729): not configured (734), no credentials (741) and any failure (769) throw "on-premises
    Exchange could not be asked: <reason>"; null only when both reads answered not found. `OnPremRecipientExists` (778): no
    session throws (781); the read under Stop through `AddOnPremRead` and `ReadUnlessNotFound`; a failure is
    "<cmdlet> failed: <reason>".
  - `CheckAdGroupMembership` and its section are gone.
- `Services/MigrationService.cs`: the ToCloud size step (196-223) copies the sizes and markers, applies the quota only to a
  size that was read, and makes an Eligible verdict Undetermined with "Could not be determined: <UnreadableReason>" (218);
  the catch (241) the same with both sizes marked. `GetCloudMailboxSizeAsync` (1454) returns `MailboxSizes`. The moved
  helpers and the now-unused Regex using are gone.
- `Services/RecipientLookupService.cs` (135-141): a size is set only when it was read; the unreadable one is a warning in the
  base's words ("The on-premises mailbox size could not be read: <reason>.").
- `Components/Pages/CalendarPermissions.razor` 431, `Components/Pages/MailboxPermissions.razor` 461: `string location; try
  { location = await ...GetMailboxLocationAsync(validation.Mailbox); } catch (Exception locationEx) { result =
  PermissionResult.Fail(ExchangeServiceBase.LocationReadFailure(target, locationEx)); return; }`.
- `ExchangeAdminWeb.Tests/MigrationStubExchange.cs`: the on-premises stand-in - `OnPremConfigured` (the service is built
  with a server URI only then), `OnPremCredentials`, `OnPremMailboxes`, `OnPremRemoteMailboxes`, `Recipients` and an
  Exchange Online `Get-Recipient`; both services' `ConnectOnPrem` and `GetModuleCredentialsAsync` seams overridden
  (`DefineOnPremSession`: an Invoke-Command that runs the remote script locally, `Remove-PSSession`, and `OnPremStubs` -
  Get-MailboxStatistics, Get-Mailbox, Get-RemoteMailbox - logged "OnPrem <cmdlet>"; `Failures["OnPrem Connect"]` makes
  opening the session throw); `RecipientLookup()` builds the real Recipient Lookup service over the same stand-ins;
  `Stub` takes a label for the logged step.
- `MigrationExcludedGroupTests`, `MigrationForestLookupTests`: their size-step reason in the new words (no server
  configured there).

New `ExchangeAdminWeb.Tests/ExchangeServiceBaseOnPremReadTests.cs`, 22 tests (42 cases):

| Test | Pins |
| --- | --- |
| `AnOnPremMailboxSizeThatCannotBeRead_IsUndetermined_NeverZero` (thrown, written, no row, no size, does not parse) | **the finding, (1)/(2)**: Undetermined, the marker, "Could not be determined: the on-premises mailbox size could not be read: <reason>". Before: a size that does not parse was 0, Eligible; a failed read Ineligible "(on-prem connection unavailable)" |
| `AnOnPremArchiveThatCannotBeRead_IsUndetermined_NotNoArchive` (thrown, written, no row, does not parse) | **the finding**: the archive marker; the mailbox read as 2.5. Before: no archive, Eligible |
| `NoOnPremArchive_IsNoArchive` (category, type, identifier) | Exchange's not-found by structure: Eligible, archive 0, no marker |
| `ASizeReadThatCannotBeMade_IsUndetermined_InTheBasesWords` (not configured, no credentials, the session cannot be opened) | both sizes marked with the base's own reason, never "(on-prem connection unavailable)" |
| `ReadableSizesUnderTheQuota_AreEligible_WithTheSizes`, `AReadableSizeOverTheQuota_IsIneligible` | the controls: the quota decides as before |
| `AnIneligibleSizeAnswer_WithAnArchiveThatCannotBeRead_StaysIneligible_WithBothReasons` | a known reason stands; the unread archive named beside it |
| `ThePage_ShowsASizeThatCouldNotBeRead_AsUndetermined` | through `CheckSingleEligibility`: "could not be determined", the reason, "could not be read (...)", no "IS NOT eligible", no "0.00 GB" |
| `ACloudMailbox_IsCloud_AndNothingOnPremisesIsAsked` | the control |
| `ACloudReadThatFails_IsAFailedRead_NeverNoMailbox` (thrown, written) | **the finding, (3)**: "Exchange Online could not be asked: ..."; on-premises not asked. Before: on to on-premises, "Unknown" |
| `NotInTheCloud_OnPremisesDecides` (on-premises mailbox, remote mailbox, neither) | OnPrem, Cloud, Unknown - "Unknown" only when both sides said there is none |
| `AnOnPremReadThatFails_IsAFailedRead` (Get-Mailbox, Get-RemoteMailbox; thrown, written) | **the finding, (3)**: "on-premises Exchange could not be asked: <cmdlet> failed: ...". Before: "Unknown" |
| `AnOnPremCmdletTheSessionLacks_IsAFailedRead` | CommandNotFoundException is no answer |
| `OnPremisesThatCannotBeAsked_IsAFailedRead` (not configured, no credentials, the session cannot be opened) | each a failure in the base's words. Before: "Unknown" |
| `ALocationReadThatFailed_IsRefusedSayingNothingWasChanged` | `LocationReadFailure`'s text |
| `APermissionsPage_RefusesALocationReadThatFailed_BeforeAnyWrite` (Calendar, Mailbox) | the guarded line, and no unguarded `var location = await` left (source - neither page has a handler harness) |
| `RecipientLookup_AnOnPremSizeThatCannotBeRead_IsAWarning_NotZero` | no size, the warning naming why. Before: 0.00 GB |
| `RecipientLookup_AnOnPremArchiveReadThatFails_KeepsTheMailboxSize_AndSaysWhy` | the mailbox size, no archive size, the archive's warning |
| `RecipientLookup_ReadableOnPremSizes_AreShown`, `RecipientLookup_ACloudLocationReadThatFails_IsUnknown_SayingWhy` | the control; a failed location read reaches its existing warning |
| `TheBaseNoLongerCarries` (CheckAdGroupMembership, ParseExchangeSize) | **the finding, (4)**: both gone |

**Against the unfixed code: not runnable as a whole** - the size read's return type and the base's members changed, so each
behaviour was put back at its own call site instead, and each revert fails the tests that pin it.

Mutations at the call site, by the same driver and protocol as aud-a23r's (session scratchpad `work/mutate.ps1`,
`work/round-a23o.ps1`; fix staged, restored from git's index, touched, md5 equal, rebuilt, green), over the 1930 tests of
the same filter. Bytes (LF) before every mutation and after every restore: base `75a621657fc4a66611e98299cbd2cb48`, service
`4933f50ba497e3a39f7921ff3b0e19ad`, Recipient Lookup `5cb32b315c11692fa9e9b581260a28c1`, Calendar Permissions page
`329585d56f12ad3287f2076c26bd3ec5`.

| Mutation | File | md5 mutated | Failed (of 1930) | Restored, rerun |
| --- | --- | --- | --- | --- |
| O1 (the record's): `?? 0` again - the mailbox size `ParseSizeToGB(...) ?? 0`, no marker | base | `f48c5ee68dd8524176ae786886508095` | 5: `AnOnPremMailboxSizeThatCannotBeRead...` (no row, no size, does not parse), `ThePage_ShowsASize...`, `RecipientLookup_AnOnPremSizeThatCannotBeRead...` | equal; 1930/0 |
| O2 (the record's): the archive catch again - any archive failure no archive | base | `c70d3d3c7e2593d8a0dfc7ccc6e0c4f5` | 4: `AnOnPremArchiveThatCannotBeRead...` (thrown, written), `AnIneligibleSizeAnswer_WithAnArchive...`, `RecipientLookup_AnOnPremArchiveReadThatFails...` | equal; 1930/0 |
| O3 (the record's): `-ErrorAction Ignore` again on `HasCloudMailboxAsync` | base | `a725ee68f98060106b3c84c525f0300e` | 3: `ACloudReadThatFails...` (thrown, written), `RecipientLookup_ACloudLocationReadThatFails...` | equal; 1930/0 |
| O4: an unreadable size filed Ineligible again | service | `af27fd094d1399538c633036796ff83e` | 13: every size-unreadable case's Undetermined, the page test | equal; 1930/0 |
| O5: SilentlyContinue again in `OnPremRecipientExists` | base | `878dfac0d1fc9dd49d559d8a3b73d5fa` | 4: `AnOnPremReadThatFails...` (all four - a throw is suppressed too) | equal; 1930/0 |
| O6: `GetOnPremMailboxLocationAsync`'s catch returns null again | base | `9da11be6839776d4c371ced554bff093` | 6: `AnOnPremReadThatFails...` (4), `AnOnPremCmdletTheSessionLacks...`, `OnPremisesThatCannotBeAsked...` (session) | equal; 1930/0 |
| O7: Recipient Lookup shows an unreadable size (0) again | lookup | `5a5b5a4af82fe946b541920913b7bd37` | 1: `RecipientLookup_AnOnPremSizeThatCannotBeRead...` | equal; 1930/0 |
| O8: the Calendar page's location read unguarded again | page | `52f4ddb3415520f5126a918489777a50` | 1: `APermissionsPage_Refuses...` (Calendar) | equal; 1930/0 |

md5s (LF) of the other files at the fix commit: `MailboxPermissions.razor` `1769c207ec6aef7df9097fa2146cc857`,
`MigrationStubExchange.cs` `5bd6bed670f0c5d8122ac6394b52ebbb`, `ExchangeServiceBaseOnPremReadTests.cs`
`ed0a5003d741d5b22f41293928ea7b68`, `MigrationExcludedGroupTests.cs` `248dad8c06f0bda8b019183f563b5b5b`,
`MigrationForestLookupTests.cs` `751b25f817d9f7ba694e344c381e098b`.

On the fix commit's tree: Release build 0 errors, 59 warnings (none in a touched file); the 1930 focused tests 1930/0 (every
`ExchangeServiceBase` subclass's among them); `dotnet format --verify-no-changes` exit 0; `git diff --cached --check` exit 0;
`tools/Test-AsciiOnly.ps1` exit 0. No Exchange, Delinea or remote session was touched: every on-premises cmdlet was a
stand-in defined through the services' own `ConnectOnPrem` seam.

## Left for a follow-up (file owned by another agent)

- `Services/ConferenceRoomService.cs` 1533, a comment: "Runs in its own AD runspace (mirrors Comms10k /
  CheckAdGroupMembership), not through". `CheckAdGroupMembership` no longer exists; the comment should name
  `MigrationService.CheckExcludedGroups` (or drop the reference). Comment only - no behaviour.

## Known gaps

- **Assumptions, labelled (no live call):** on-premises Exchange's not-found, through remoting, keeps the category / reason /
  FailureCategory shape `IsNotFound` reads, for Get-Mailbox, Get-RemoteMailbox and Get-MailboxStatistics -Archive (a
  mailbox with no archive); Exchange Online's for Get-Mailbox (aud-a23h's assumption, now also carried by Calendar
  Permissions, Mailbox Permissions and Recipient Lookup through `GetMailboxLocationAsync`). If either is wrong, a ToCloud
  check of a mailbox with no archive is Undetermined, and a location read of a mailbox not on that side is refused with
  Exchange's own words - fail closed, visible; one dev check of each error record settles it before a deploy. **This is the
  change's biggest risk**: if Exchange Online's not-found is not recognised, on-premises targets in Calendar and Mailbox
  Permissions are refused until it is.
- The two permission pages' new refusal is pinned by source, not driven: neither page has a handler harness.
- The on-premises archive read that returns no row and no error is now unreadable ("returned no archive statistics");
  aud-a23k's cloud archive read, through `ReadUnlessNotFound`, still treats that as no archive (noted as a candidate).
- Behaviour outside Migration: Recipient Lookup's on-premises sizes and location warnings, and Calendar and Mailbox
  Permissions' refusal text for a failed location read (named in the hand-back for their bumps).

## Comments and branch-tip gates

Shared by the four records of this branch; the canonical copy is in `aud-a23r.md` ("Comments and branch-tip gates"):
comments `9fa7664d`, comment-only by a stripped-code comparison; at the tip, Release build 0 errors / 59 warnings (the
baseline's), full suite 6086 passed / 3 skipped / 0 failed (6089), format, the whitespace check and the ASCII lint all exit
0, page 4377 lines.

## Closeout

Fixed on branch `worktree-agent-ac3fd1dd1db4f9c1d`; not merged, not pushed.
