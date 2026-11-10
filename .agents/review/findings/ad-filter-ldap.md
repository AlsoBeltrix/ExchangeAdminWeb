# ad-filter-ldap: three Get-ADGroup searches put typed text into a PowerShell-syntax `-Filter` by quote-doubling

**Severity**: MEDIUM - the Active Directory module parses a `-Filter` string as an expression and expands `$name` in it
as a variable, so a group name or address holding a dollar sign searched for something else (the repo's own comment
in `SectionAccessGroupDirectory.cs` says so), and an asterisk in a search term was a wildcard. The result was a wrong
group or no group: a wrong answer to a lookup, in two modules. Not HIGH: each write that follows re-reads the group it
resolved (Group Management's write path does not use these two filters; Conference Rooms requires exactly one match
and re-reads it in its own domain), and no input reached a second command.
**Status**: Fixed on branch; awaiting merge
**Branch**: `fix/p0-b-phase0-mediums` (worktree `D:\source\ExchangeAdminWeb-wt\p0-b`, from master 4cb7942b)
**Commit**: 2e687204 (the three filters, tests), 851c10db (versions) | **Implementer**: Claude (supervised agent)
**Reviewer**: Fable 5.2 audit 2026-11-10, confirmed by Gemini 4 Argon (High) through agy; orchestrator triage
**Plan**: `docs/SharedPiecesAndSlop-Plan.md` slice 0.4 (defect #3)

## Evidence (master 4cb7942b)

- `Services/GroupManagementService.cs:135` (`SearchGroupsAsync`):
  `-Filter "Name -like '*{t}*' -or SamAccountName -like '*{t}*' -or Mail -like '*{t}*'"`, `t` the typed term with `'`
  doubled.
- `Services/GroupManagementService.cs:1552` (`ResolveAdGroupIdentity`, reached from `GetMembersAsync` when the group is
  given by name rather than DN): `-Filter "SamAccountName -eq '{c}' -or Name -eq '{c}' -or Mail -eq '{c}'"`.
- `Services/ConferenceRoomService.cs:2666` (`ResolveAdGroupGuid`): `-Filter "mail -eq '{address}'"`, the address
  Exchange holds for the room list.
- The same file already resolves a group for the write path with
  `-LDAPFilter "(|(sAMAccountName={esc})(name={esc})(mail={esc}))"` (`ResolveGroupForWrite`), escaped by
  `SelfServiceGroups.AdOwnershipFilter.EscapeLdapFilterValue`.

## Fix

| Site | Before (`-Filter`) | Now (`-LDAPFilter`) |
|---|---|---|
| Group search | `Name -like '*t*' -or SamAccountName -like '*t*' -or Mail -like '*t*'` | `(\|(name=*t*)(sAMAccountName=*t*)(mail=*t*))` |
| Group by name | `SamAccountName -eq 'c' -or Name -eq 'c' -or Mail -eq 'c'` | `(\|(sAMAccountName=c)(name=c)(mail=c))` |
| Room list's group | `mail -eq 'a'` | `(mail=a)` |

- The value goes through `SelfServiceGroups.AdOwnershipFilter.EscapeLdapFilterValue` (RFC 4515: `\` `*` `(` `)` NUL),
  the escaper `GroupManagementService` already used at every other `-LDAPFilter`. No escaper added. Conference Rooms
  had no `-LDAPFilter` before; it calls the same one, not `ProtectedPrincipalService.EscapeLdapFilter`, so the module
  that runs no protected-principal check takes nothing from that service.
- Same cmdlet (`Get-ADGroup`, so the object class condition is the cmdlet's own, as before), same attributes, same
  order, same other parameters (`-Server`, `-Credential`, `-Properties`, `-ResultSetSize`, `-ErrorAction`). Matching
  stays case-insensitive. A quote and a dollar sign need no escape in an LDAP filter and are passed as typed.
- App code: +30 / -32 lines (`git diff --numstat 2ea5908d 2e687204 -- Services`); six of them are the filters, the rest
  the two methods' comments with their finding IDs and history removed.

## Behaviour that moves

1. A `$` in the text: was expanded as a PowerShell variable (usually to nothing); now a character. The finding.
2. A `*` in a Group Management search term: was a wildcard inside the term (`fin*ops`); now a character, as the brief
   asks ("asterisk ... must match literally and never widen"). The term is still matched anywhere in the name, logon
   name or address. No page help or README text described a wildcard, so none changed.
3. A `\`, `(`, `)` or NUL in the text: whatever the module's expression parser made of it; now that character.

Nothing else: ordinary text selects the same groups.

## Tests

- `GroupManagementDirectoryFilterTests` (new, 21): the real `SearchGroupsAsync` and `GetMembersAsync` over a local
  session (the `CreateDirectoryRunspace` seam) whose `Get-ADGroup` records its parameters and answers with the
  stand-in groups its `-LDAPFilter` matches. Ordinary input: a search finds by name, logon name and address; a group
  given by logon name, name or address (any case) is the one read; the logon name is tried first and part of a name
  resolves nothing. Special input, search and by-name (8 each): `'`, `$`, `\`, `(` `)`, `*`, NUL, and text shaped to
  close the clause and open another - each asserts the exact filter text and the one group (or none) it selects,
  beside a decoy an unescaped `*` would also match.
- `ConferenceRoomRoomListPreflightTests.AListAddressWithACharacterAFilterGivesMeaningTo_FindsOnlyTheGroupCarryingExactlyIt`
  (new, 6): through `SetRoomMetadataAndListAsync`, the list's group is found and written when its address holds each
  such character, with a second group whose address a wildcard would match - two matches refuse.
- `StandInLdapFilter` (test support): reads a filter as a directory does - `\XX` is one character, a bare `*` a
  wildcard, a bare parenthesis, backslash or NUL in a value unreadable - so an unescaped value widens or errors in the
  stand-ins as it would in a directory. The Conference Rooms harness's `Get-ADGroup` evaluates `-LDAPFilter` through it
  and refuses a search by `-Filter`.

Changed, not deleted (same tests, the filter they assert or send is now the LDAP one):

| Test | Change |
|---|---|
| `TheListsGroup_IsTheOneCarryingItsAddress_NotAGroupThatSharesItsName` | asserts `(mail=<address>)` on `LDAPFilter`, and no `Filter` |
| `TheHarness_AGroupSearchMatchesOnlyTheAttributesItsFilterNames` | the same nine cases in LDAP form (the "any other shape is an error" case is now a bare parenthesis, a bare backslash and a `-Filter` expression), plus a wildcard and an escaped asterisk |
| `TheHarness_FailuresAfterSetPlace_AndTheMemberReadKey_FailOnlyWhatTheyName`, `SearchGroups` helper | send `LDAPFilter` |

No test deleted.

## Guard proof (reverted, targeted run of 1653 tests, restored)

| Reverted | Failed | Tests that failed |
|---|---|---|
| All three sites back on `-Filter` (the two service files as at 2ea5908d) | 115 | Group search: `ASearch_FindsEveryGroup...` 1, `ASearchTermWithACharacter...` 8. Group by name: `AGroupGivenByItsLogonName...` 3, `TheLogonNameIsTriedBeforeTheIdentity...` 1, `AGroupNameWithACharacter...` 8. Conference Rooms: 94 across `ConferenceRoomRoomListPreflightTests`, `ConferenceRoomDirectoryAuditTests`, `ConferenceRoomListGroupAddressTests`, `ConferenceRoomWriteIdentityTests`, `ConferenceRoomCancellationTests`, `ConferenceRoomSetupCancellationTests` - every test that reaches the group search, the six new cases among them |
| `-LDAPFilter` kept, the escaper removed at all three sites | 16 | Group search 6 (`\`, `(Day)`, `*`, `Ops*Star`, NUL, clause-closing text); group by name 6 (`\`, `(Day)`, `Ops*`, `Ops*Star`, NUL, clause-closing text); Conference Rooms 4 (`\`, `(` `)`, `*`, NUL) |

Restored: 1653 of 1653 pass (full gates in the hand-back). The `'` and `$` cases pass without the escaper because
LDAP gives neither a meaning; they fail with the `-Filter` form only through the stand-in refusing `-Filter`, so what
they pin is that the text reaches the directory as an LDAP value, unchanged.

## Other variable-built `-Filter` sites found, not changed

Searched `Services/` and `Components/` for `-Filter` and for a `Filter` parameter:

- `Components/Shared/RecipientAutocomplete.razor:123,148` - `Get-Recipient -Filter "Name -like '*{t}*' -or
  PrimarySmtpAddress -like '*{t}*'"`, the typed term with `'` doubled. Exchange Online, not an AD cmdlet: there is no
  `-LDAPFilter` to move to. The same quote-doubling shape; a `$` or `*` in the term is the open question. Typeahead
  only: it gates nothing.

Constant filters, no variable: `AccountLockoutRemediationService.cs:659` (`Enabled -eq $true`),
`ADOrganizationalUnitService.cs:115` (`*`), `DhcpAuthorizationService.cs:232` (`*`), `TrueLastLogonService.cs:62`
(`Get-ADDomainController -Filter *`), `AccountLockoutRemediationService.cs:598` (`Get-WinEvent -FilterHashtable`).
No other AD cmdlet in either folder takes a `-Filter` built from a variable.

## Left alone, and why

- `ResolveGroupForWrite` builds the same by-name filter text inline three hundred lines above `ResolveAdGroupIdentity`.
  Two copies of one expression; consolidating them is phase 3's, not a defect fix.
- `ResolveAdGroupIdentity` still searches the default domain with `-ErrorAction SilentlyContinue`; unchanged.
- Conference Rooms' exemption from protected-principal checks: untouched, none added.
- An empty search term would now send `(name=**)`. The page does not search a blank term (button disabled, Enter
  ignored) and nothing else calls `SearchGroupsAsync`.
- No base version bump: no shared code changed. Group Management 2.16.7, Conference Rooms 2.20.5.
- Finding IDs and history were stripped from the comments of `SearchGroupsAsync`, the comment block of
  `ResolveAdGroupGuid`, and the stand-in `Get-ADGroup`'s comments in the test harness, and nowhere else.
