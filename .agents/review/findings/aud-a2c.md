# aud-a2c: The Exchange-target check can bind to an AD namesake of the mailbox

**Severity**: HIGH - a stable, non-racy way to modify a protected mailbox through every
Exchange-written module (Mailbox Permissions, Calendar Permissions, Out of Office, Conference
Rooms).
**Status**: On master (applied by the supervisor from the agent's verified patch; its worktree had forked from the remote tip and lacked a2b); codex review pending; dev check owed
**Branch**: `worktree-agent-a8c8880451a1663f0`, on master `b82de603`
**Commit**: `7f1a0e6e`

## Evidence

Raised by `codex / gpt-5.5-dzs / xhigh` reviewing `1633ad0..f0e9844e` (the authz batch that
landed `aud-a2b`). Verdict text: `.agents/review/authz-batch.result.txt` in the main checkout
(machine-local). One HIGH; this record.

`aud-a2b` made the Exchange-written gates ask Exchange first and then find the AD object to
check. It finds that object by the recipient's canonical address, through the same AD lookup
every other gate uses. `Services/ProtectedPrincipalService.cs` at `b82de603`:

```
572        var (canonical, canonicalStatus) =
573            string.Equals(recipient.PrimarySmtpAddress, identity, StringComparison.OrdinalIgnoreCase)
574                ? (adPrincipal, adStatus)
575                : await ResolveWithStatusAsync(recipient.PrimarySmtpAddress);
...
589        if (adStatus == ResolutionStatus.Resolved && (adPrincipal is null || !IsSameDirectoryObject(adPrincipal, canonical)))
...
598        return (canonical, ResolutionStatus.Resolved);
```

and that lookup matches the address against three attributes, none of them owned by Exchange
(`:695`):

```
695        var filter = $"(|(userPrincipalName={escaped})(mail={escaped})(sAMAccountName={escaped}))";
```

The `IsSameDirectoryObject` check at `:589` compares two AD readings with each other - AD's
reading of the typed string and AD's reading of the canonical address. Neither is tied to the
mailbox Exchange resolved. Nothing on either side carries an Exchange identity:
`ResolvedRecipient` (`Services/IIdentityResolver.cs:19-23`) holds the address, the Entra object
id, the recipient type and the sync flag, and `MapRecipient`
(`Services/ExchangeIdentityResolver.cs:110-114`) reads nothing else from `Get-Recipient`.

The write then hands the operator's string to Exchange, which resolves it by its own rules
(`Services/MailboxPermissionService.cs:51-52`):

```
51                    ps.AddCommand("Add-MailboxPermission")
52                      .AddParameter("Identity", targetMailbox)
```

### Triggering state

A synced, protected mailbox A whose own AD object does not carry its primary address in `mail`
or `userPrincipalName` within the searched domain, and another AD user B - not a recipient -
that does. Neither half needs an attacker:

- `mail` is not unique in AD and is commonly set on accounts that have no mailbox (an admin's
  second account pointed at the owner's address for alerts is the usual case).
- `userPrincipalName` is unique, but a mailbox's UPN and primary address often differ, so B may
  legitimately hold A's address as its UPN.
- The lookup runs `Get-ADUser` with no `-Server` (`:697`), so a mailbox object in another domain
  of the forest is never found at all and a namesake in the searched domain is the only match.

When AD returns exactly one object for the address, that object is B. `IsSameDirectoryObject`
passes (typed string = the address: both readings are B; typed alias: AD reads the alias as
nothing). B is checked, B is not protected, and Exchange writes A.

## Predicted observable failure

A non-servicer grants themselves FullAccess to a protected mailbox, or rewrites its calendar
permissions, Out of Office or room settings, by typing its address or any alias. The audit
trail records an allowed operation on an unprotected principal. Nothing races; it reproduces
every time while the directory is in that state.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21). Check
side only; the write side is `aud-a2d`.

**Rule.** For a synced recipient, the AD object behind the canonical address is accepted only
when Active Directory proves it is the object that owns the mailbox Exchange resolved: it must
be the one and only AD object whose `msExchMailboxGuid` equals the recipient's `ExchangeGuid`,
compared by objectGUID. Every a2b step before it stays as it is, so every a2b refusal stays a
refusal; this adds one.

**Attribute pair: Exchange `ExchangeGuid` = AD `msExchMailboxGuid`.** Chosen because:

1. It is the identifier Exchange itself uses to bind an on-premises object to a mailbox: an
   on-premises mailbox's `msExchMailboxGuid` is its `ExchangeGuid`, it is synchronized to the
   cloud object, and a hybrid move keeps it on both sides. (Microsoft's documented behaviour;
   not verified against this tenant - live probes are forbidden.)
2. It is a 128-bit value Exchange writes. No operator-typed or commonly delegated attribute -
   `mail`, `userPrincipalName`, `sAMAccountName`, `proxyAddresses` - can equal it by accident,
   and copying it onto another object is a deliberate write to an Exchange attribute.
3. It is reachable through the existing seams: `Get-Recipient` already runs in
   `IIdentityResolver.ResolveRecipientAsync` and returns `ExchangeGuid` in the same result; AD is
   read through the same `Get-AD*` runspace pattern as `ResolveViaActiveDirectory`. No new
   cmdlet, no new service, no Graph.
4. Both Exchange Online and on-premises Exchange accept it as a mailbox identity, which is what
   `aud-a2d` needs when it binds the write.

**Rejected:**

- `proxyAddresses` (`SMTP:` primary) as the locator. Still a string; AD does not enforce its
  uniqueness; and `docs/ProtectedPrincipalResolution-Plan.md` (Non-Goals) rejects adding
  `proxyAddresses` to AD resolution. The GUID locates the object by itself, so it would be a
  second mechanism for the same job.
- Entra `ExternalDirectoryObjectId` = AD `msDS-ExternalDirectoryObjectId`. Present in AD only
  with Entra Connect's Exchange hybrid writeback and a 2016+ schema, and naming an attribute the
  schema lacks in `-Properties` fails the whole query.
- objectGUID / `mS-DS-ConsistencyGuid` = the Entra source anchor. `Get-Recipient` does not
  expose `onPremisesImmutableId`; reaching it needs Microsoft Graph, a new seam. And
  `mS-DS-ConsistencyGuid` is itself an ordinary writable attribute.

**Absent or disagreeing - never `Resolved`:**

| Case | Result |
| --- | --- |
| Exchange reports no usable `ExchangeGuid` (absent, unparseable, all zeros) | `Unavailable` |
| The AD lookup by GUID could not run (credential, throttle, error, no Exchange schema) | `Unavailable` |
| No AD object holds the GUID (a remote mailbox never stamped, an object outside the searched domain) | `Unavailable` |
| Two or more AD objects hold it | `Ambiguous` |
| One holder, but it or the canonical object has no objectGUID to compare | `Unavailable` |
| One holder, and it is not the object AD reads the canonical address as (the codex case) | `Ambiguous` |
| One holder, the same object | `Resolved`; the principal carries the agreed `ExchangeGuid` |

**Shape.**

- `ResolvedRecipient` gains `ExchangeGuid` (`Guid?`); `MapRecipient` reads it. Absent stays
  null rather than failing the lookup: groups, contacts and cloud mail users legitimately have
  none, and only the synced-mailbox branch needs it.
- `ResolvedDirectoryPrincipal` gains `ExchangeGuid` (`Guid?`, trailing, defaulted, so every
  existing construction compiles unchanged). Set only when established: the proven GUID on the
  synced branch, the recipient's own on the cloud-only branch, null otherwise. It is not a match
  candidate in `MatchesIdentity`; it is the identity `aud-a2d` carries to the write.
- A new `public virtual` seam `FindMailboxGuidHoldersAsync(Guid)` runs
  `Get-ADObject -LDAPFilter (msExchMailboxGuid=<escaped bytes>)` with the same credential,
  throttle and fail-closed handling as `ResolveWithStatusAsync`. `Get-ADObject`, not
  `Get-ADUser`, so a second holder of any class is seen.
- The decision is a pure static (`SelectMailboxBacking`) so every row of the table above is
  unit-testable; the GUID parse and the filter builder are pure statics too.
- `ResolveExchangeTargetAsync` calls the proof after a2b's typed-string agreement check, so the
  extra AD query is spent only on a target a2b would have accepted.

**Not changed.** `ResolveWithExchangeFallbackAsync` (the AD-first path) and
`ResolveViaActiveDirectory`'s filter. No gate, page or write path. No `.razor` file.

**Files.** `Services/ProtectedPrincipalService.cs`, `Services/ExchangeIdentityResolver.cs`,
`Services/IIdentityResolver.cs`; tests in `ExchangeAdminWeb.Tests/ProtectedPrincipalExchangeFallbackTests.cs`
and `ExchangeAdminWeb.Tests/ExchangeIdentityResolverTests.cs`; this record and the known gaps in
`aud-a2b.md`.

**Tests.** The codex case with B holding the address and A holding the GUID, typed as the
address and as an alias - never `Resolved`, never B. No holder - `Unavailable`. The matching
case resolves to the true backing object and carries the GUID. Missing GUID on the Exchange
side, the lookup unable to run, two holders, a holder without the GUID, missing objectGUIDs.
`MapRecipient` reads the GUID as `Guid` and as a string, and maps absent / zero / garbage to
null without failing. The GUID parse reads AD's octet string; the filter escapes each byte in
AD's byte order, asserted against a hand-written literal, not recomputed. The existing a2b tests
keep their assertions; the two that expect `Resolved` for a synced recipient gain the GUID facts
the rule now requires.

**Mutations, at the call sites.** `ResolveExchangeTargetAsync` returns `canonical` without the
proof (the codex test fails); the missing-GUID branch and the lookup-could-not-run branch accept
instead of refusing (their tests fail); the selector takes the first of two holders, drops the
objectGUID comparison, or counts a returned object without the GUID as a holder (their tests
fail); `MapRecipient` stops reading the GUID, or lets all zeros through (their tests fail).

## Approach (as implemented)

Implemented as planned; nothing in the plan changed in the building.

**Attribute pair: Exchange `ExchangeGuid` (from the `Get-Recipient` result `ResolveRecipientAsync`
already fetches) = AD `msExchMailboxGuid`**, for the four reasons in the plan: it is Exchange's own
binding between an on-premises object and a mailbox, it is a 128-bit value no delegated attribute
edit produces by accident, it needs no new seam, and both Exchange Online and on-premises Exchange
accept it as a mailbox identity for `aud-a2d`.

`Services/ProtectedPrincipalService.cs`, `ResolveExchangeTargetAsync`, synced branch: a2b's code is
unchanged up to its last line, which returned `canonical`. That line now returns
`ProveMailboxBackingAsync(identity, recipient, canonical)`:

1. No `ExchangeGuid` on the recipient - `Unavailable`, logged; AD is not asked.
2. `FindMailboxGuidHoldersAsync(guid)` - `Get-ADObject -LDAPFilter (msExchMailboxGuid=\hh...)`,
   requesting `DistinguishedName`, `ObjectGUID`, `msExchMailboxGuid`. Any error, including an error
   record returned beside results, is "could not run" - `Unavailable`.
3. `SelectMailboxBacking(guid, canonical, holders)`, pure. Only objects AD returned WITH the GUID
   count as holders. None - `Unavailable`. More than one - `Ambiguous`. One without an objectGUID,
   or a canonical object without one - `Unavailable`. One that is not the canonical object by
   objectGUID - `Ambiguous`. Otherwise `Resolved`, and the principal is `canonical` with
   `ExchangeGuid` set to the agreed GUID.

Every refusal logs the reason, the address and the GUID. The "no holder" reason names the two
likely causes (a remote mailbox whose ExchangeGuid was never stamped on-premises; an object outside
the searched domain), because the validator's operator-facing text for `Unavailable` is generic.

**When an attribute is absent, the answer is never `Resolved`:**

| Absent or unreadable | Result |
| --- | --- |
| `ExchangeGuid` on the Exchange side (property missing, unparseable, all zeros) | `Unavailable` |
| `msExchMailboxGuid` on every AD object (never stamped, or outside the domain searched) | `Unavailable` |
| `msExchMailboxGuid` on the object AD returned (it is then not a holder) | `Unavailable` |
| `msExchMailboxGuid` unknown to the schema (no Exchange schema in the forest) | `Unavailable` - the query fails |
| objectGUID on the holder or on the canonical object | `Unavailable` |

`MapRecipient` reads `ExchangeGuid` as a `Guid`, a string, or either wrapped in a `PSObject`; all
zeros and anything unparseable are null. It does not fail the lookup on a missing GUID - unlike
`IsDirSynced` - because groups, contacts and mail users have none and only this branch needs one.

The cloud-only principal now also carries the recipient's own `ExchangeGuid`, for `aud-a2d`. It
is data only: `ExchangeGuid` is not a `MatchesIdentity` candidate, so no protection rule changes.

## Guard proof

| Test | What it pins |
| --- | --- |
| `ProtectedPrincipalExchangeFallbackTests.ExchangeTarget_AnAdNamesakeOfTheAddress_IsNeverTheObjectChecked` (address typed; alias typed) | the finding: an account carrying the mailbox's address in `mail`, with the mailbox's own object holding the GUID, is never the object checked - `Ambiguous` for both strings |
| `ExchangeTarget_NoAdObjectHoldsTheMailboxGuid_IsUnavailable_NotTheNamesake` | only the namesake in AD (mailbox object elsewhere, or GUID never stamped) - `Unavailable`, never the namesake |
| `ExchangeTarget_TheTrueBackingObject_ResolvesAndCarriesTheAgreedMailboxGuid`, `ExchangeTarget_TheProvenMailbox_IsCheckedAsItself` | the matching case resolves to the true backing object with its DN and objectGUID, carries the GUID, and is protected by its objectGUID row when reached by an alias - so the refusals cannot pass by refusing everything |
| `ExchangeTarget_ExchangeReportsNoMailboxGuid_IsUnavailable` | missing on the Exchange side - `Unavailable`, and AD is not asked |
| `ExchangeTarget_TheMailboxGuidLookupCannotRun_IsUnavailable` | the AD lookup could not run - `Unavailable` |
| `ExchangeTarget_TwoAdObjectsHoldTheMailboxGuid_IsAmbiguous` | a copied GUID - `Ambiguous` even though the canonical object is one of the holders |
| `ExchangeTarget_ACloudOnlyRecipient_CarriesExchangesOwnMailboxGuid` | the cloud-only branch carries Exchange's GUID for a2d and does not query AD for it |
| `SelectMailboxBacking_AnObjectReturnedWithoutTheMailboxGuid_IsNotProof` | missing or different GUID on the AD side is not proof, even on the canonical object itself |
| `SelectMailboxBacking_WithoutObjectGuidsToCompare_IsUnavailable` | two blank objectGUIDs are not "the same object" |
| `ParseMailboxGuid_ReadsAdsOctetStringInWindowsGuidByteOrder` | AD's byte layout, asserted against hand-written bytes |
| `BuildMailboxGuidFilter_EscapesEachByteInAdsOrder` | the binary LDAP filter, asserted against a hand-written literal |
| `ExchangeIdentityResolverTests.MapRecipient_CarriesTheMailboxGuid` | the GUID is read as `Guid`, as a string, and wrapped |
| `MapRecipient_NoUsableMailboxGuid_IsNull_NotAFailedLookup` (4 cases), `MapRecipient_AnAllZeroMailboxGuidObject_IsNull` | absent, empty, garbage and all zeros are null, and none of them fails the lookup |

**Existing tests changed.** `ExchangeTarget_AgreeingDirectories_ResolveToTheMailboxUser` and
`ExchangeTarget_AnAliasAdDoesNotKnow_ResolvesToTheObjectExchangeWillWrite` keep their assertions;
their synced recipient now carries an `ExchangeGuid` and the scripted AD has the mailbox user hold
it, because `Resolved` now requires exactly that. The scripted service gains
`FindMailboxGuidHoldersAsync`; unscripted, it answers "no holder", so a fixture that forgets the
GUID fails closed instead of passing. Every other a2b test passes unmodified.

**Mutations**, each at its call site where a unit test can reach it, each applied by a script that
refuses unless the old text occurs exactly once and the file's md5 changes. Rebuilt, the named
test run, the file restored from a saved copy and touched (an untouched restore can keep the old
mtime and leave the mutated binary in place), rebuilt, and the named test run again. Every
restored md5 equals the "before" md5. The md5s are of the files as they stood for the round:
explanatory comments were added after it, so the committed files hash differently.

| Mutation | File, md5 before (= restored) | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| M1: `ResolveExchangeTargetAsync` returns `canonical` without the proof (the a2b line) | `ProtectedPrincipalService.cs` `684b72db353163d0b34bce27e241c979` | `fb85d6e4eb6dacea8376f904e4aaaa53` | `ExchangeTarget_AnAdNamesakeOfTheAddress_IsNeverTheObjectChecked` (both cases) | FAIL: expected Ambiguous, got Resolved |
| M2: no `ExchangeGuid` returns `canonical` as Resolved | same `684b72db...` | `b86c50269d372678354163ae0c70f82b` | `ExchangeTarget_ExchangeReportsNoMailboxGuid_IsUnavailable` | FAIL: expected Unavailable, got Resolved |
| M3: a lookup that could not run is Resolved | same | `a14f02b4d76f6087d9e2aede7ae339ac` | `ExchangeTarget_TheMailboxGuidLookupCannotRun_IsUnavailable` | FAIL: expected Unavailable, got Resolved |
| M4: a second holder is ignored | same | `e928a47bcd7ae5f0826033aa6754691f` | `ExchangeTarget_TwoAdObjectsHoldTheMailboxGuid_IsAmbiguous` | FAIL: expected Ambiguous, got Resolved |
| M5: the holder is not compared with the canonical object | same | `c3c3c34d695c6962c91f7b41a31b59ff` | `ExchangeTarget_AnAdNamesakeOfTheAddress_IsNeverTheObjectChecked` (both cases) | FAIL: expected Ambiguous, got Resolved |
| M6: every returned object counts as a holder | same | `3adae7582e3569eda474fb833ef7ce99` | `SelectMailboxBacking_AnObjectReturnedWithoutTheMailboxGuid_IsNotProof` | FAIL: expected Unavailable, got Resolved |
| M6b: no objectGUID check before comparing | same | `939aa04fea67f9b55724ec380ef44479` | `SelectMailboxBacking_WithoutObjectGuidsToCompare_IsUnavailable` | FAIL: expected Unavailable, got Resolved |
| M7: `MapRecipient` stops reading `ExchangeGuid` | `ExchangeIdentityResolver.cs` `3c4797e6041a8e92462b36eb09b20ee1` | `49adad08cb5baebfc6293d4e2258bbea` | `MapRecipient_CarriesTheMailboxGuid` | FAIL: expected the GUID, got null |
| M8: all zeros passes through as a GUID | same `3c4797e6...` | `ed92ce1395291db3eda5001461634d9f` | `MapRecipient_AnAllZeroMailboxGuidObject_IsNull`, `MapRecipient_NoUsableMailboxGuid_IsNull_NotAFailedLookup` (zeros case) | FAIL: expected null, got 00000000-... |
| M9: filter escapes the text form's byte order | `ProtectedPrincipalService.cs` `684b72db...` | `5226d1b5cd03396e9dbea73770bcbed6` | `BuildMailboxGuidFilter_EscapesEachByteInAdsOrder` | FAIL: strings differ |
| M10: parser reads the octet string big-endian | same | `eb45b23ec83e2bfd67cee862a939d2da` | `ParseMailboxGuid_ReadsAdsOctetStringInWindowsGuidByteOrder` | FAIL: got 33221100-5544-7766-... |

M9 and M10 mutate the helper, not its caller: the caller is the live `Get-ADObject` runspace,
which no unit test can reach (see Known gaps).

## Known gaps

**Source-level and fake-driven, not live.** Probing Active Directory or the tenant is forbidden
without a direct owner go. Three facts the design leans on are Microsoft's documented behaviour,
not observed here - each labelled an assumption, and each fails closed if wrong:

- *Assumption:* `Get-Recipient` returns `ExchangeGuid` in the module version this app runs. If it
  does not, every synced target in the four modules refuses - loudly, on the first dev check.
- *Assumption:* on-premises mailboxes and migrated remote mailboxes hold their `ExchangeGuid` in
  `msExchMailboxGuid`. Where one does not, that mailbox refuses.
- *Assumption:* `msExchMailboxGuid` is indexed in AD. If not, the lookup is a domain scan per
  check - latency, not safety.

**Who this refuses that a2b did not - an operator-visible change, refusing direction.** In Mailbox
Permissions, Calendar Permissions, Out of Office and Conference Rooms, a synced mailbox now refuses
(`Unavailable`, "identity resolution is unavailable") when:

- its on-premises object does not hold the GUID - most likely a remote mailbox created in the cloud
  with `New-RemoteMailbox` whose ExchangeGuid was never stamped back. The remedy is on-premises
  (`Set-RemoteMailbox <id> -ExchangeGuid <the cloud mailbox's ExchangeGuid>`), not here;
- the forest has no Exchange schema (`msExchMailboxGuid` does not exist, the query fails);
- two AD objects hold the GUID (`Ambiguous`).

How many mailboxes in this tenant fall into the first group cannot be known without a live probe.
**A dev acceptance check is needed before this ships**: open Mailbox Permissions on dev against
an ordinary synced mailbox, one that was migrated and, if any exist, one created with
`New-RemoteMailbox`, and confirm each resolves. The log line names the cause of each refusal.
Each validated synced target also costs one more AD query.

**Live-only code.** `FindMailboxGuidHoldersViaActiveDirectory` (the runspace query) cannot be
reached by a unit test, exactly as `ResolveViaActiveDirectory` cannot. Its filter builder and its
parser are pinned (M9, M10); that it calls them is not mutation-provable without a directory.

**Residuals, recorded so nobody reads them as covered:**

- A forged GUID. An actor able to write `msExchMailboxGuid` on another object can copy a mailbox's
  GUID. While the real owner still holds it, that is two holders - `Ambiguous`. When the real owner
  does not (never stamped) AND does not carry the address in `mail`/UPN in the searched domain, a
  forged stand-in that does carry it would be accepted. That needs write access to an Exchange
  attribute; binding to the on-premises SID or source anchor would close it, but Exchange does not
  expose either and Microsoft Graph is a new seam.
- The holder search, like every lookup in this resolver, covers the default domain only. A second
  holder elsewhere in the forest is not seen. It cannot be admitted - the canonical object comes
  from the same domain - so a duplicate there goes unnoticed rather than unprotected.
- **The same namesake class on the AD-first entry point.** `ResolveWithExchangeFallbackAsync` is
  unchanged, and Migration and Blocked Senders use it while Exchange acts on the address
  (`New-MigrationBatch`, `Remove-BlockedSenderAddress`). a2b's reason for leaving them - "already
  `Ambiguous` in AD" - holds only when AD sees both objects. **Candidate finding, reported for
  admission; not fixed here.** Recorded in `aud-a2b.md`, Known gaps.

## What aud-a2d can rely on

`ResolveExchangeTargetAsync` returns `Resolved` only with `principal.ExchangeGuid` set as follows:

- synced recipient: the GUID Exchange reported and AD proved, never null on this branch;
- cloud-only recipient: Exchange's own GUID, null if it reported none (a cloud recipient without a
  mailbox - `EntraObjectId` still carries `ExternalDirectoryObjectId`);
- no recipient: null - Exchange has no such recipient, so there is nothing for a write to land on.

a2d's work: carry it out of `PermissionValidator.ValidateTargetMailboxAsync` (`TargetValidation`
returns no identity today) and `ConferenceRoomProtectionGate.GuardThenRunAsync` (`onAllowed`
receives only the serviced-audit note), pass it to the write as `-Identity` in place of the typed
string, and refuse a write without one. Which identity forms each write cmdlet accepts, on Exchange
Online and on-premises, is a2d's to verify.

## Reviewer comments

`Reviewer: codex / gpt-5.5-dzs / xhigh over 1633ad0..f0e9844e, 2026-10-21`. One HIGH; raw verdict
`.agents/review/authz-batch.result.txt` in the main checkout (machine-local). The reviewer's
suggested shape was an exact `SMTP:` primary `proxyAddresses` lookup plus an immutable identifier;
the identifier is used, the `proxyAddresses` lookup is not (Plan, Rejected).

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent. Independent
review of the fix pending.

## Closeout

Fixed on branch, not merged, not pushed. Shared infrastructure (`ProtectedPrincipalService`,
`ExchangeIdentityResolver`, `IIdentityResolver`) moves the base app version at merge. Module
behaviour changes, version bumps due at merge: Mailbox Permissions, Calendar Permissions, Out of
Office and Conference Rooms (target resolution now requires the mailbox GUID proof).
