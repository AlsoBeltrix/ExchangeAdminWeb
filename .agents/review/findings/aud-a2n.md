# aud-a2n: The value-already-there detection matches English error text, and the add's failure log still names the old cmdlet

**Severity**: LOW - on a non-English domain controller or module locale the aud-a2l
"value already exists" refusal is not recognised, so the race falls back to the old PARTIAL result
after Exchange metadata committed (no wrong write; a re-run says "already a member"). The outer
catch's log line "Add-ADGroupMember on-prem room-list write failed" names a cmdlet the code has
not used since aud-a2j, which misleads whoever reads the log.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2k/a2l agent, 2026-10-22, candidates 1 and 3 of its hand-back)

## Evidence

At `28263a6c`, `Services/ConferenceRoomService.cs`, `AddToRoomListViaAdAsync`: the duplicate
check matches the thrown error's message against "already a member", "already exists" and
`ENTRY_EXISTS`. The ActiveDirectory module surfaces `ADException` with an `ErrorCode` (the LDAP
result code; `ENTRY_EXISTS` is 68 / `0x44`) and, for linked-attribute adds, an
"attribute or value exists" error from the DC that carries the Win32 code 8305
(`ERROR_DS_ATTRIBUTE_OR_VALUE_EXISTS`) or the LDAP code 20 (`attributeOrValueExists`). The text is
localised; the codes are not. Which code the live module raises for this case is an assumption until
a dev probe confirms it (owner go required for any AD probe).

## Approach (to be written by the implementing agent before code)

- Decide by error code, not text: read the `ErrorRecord`'s exception chain for an `ADException`
  `ErrorCode` (LDAP 20 `attributeOrValueExists`, 68 `entryAlreadyExists`) or a Win32 8305, and treat
  the text match only as a secondary signal. Record the assumption about which code the live module
  raises and name the dev probe that would confirm it; the aud-a2l read-back already makes the
  outcome correct whenever the error is recognised, and a miss is still a safe failure.
- Correct the outer catch's log text to name `Set-ADGroup` and the member attribute.
- Tests through `ExchangeWriteHarness`: a stand-in raising an error with the LDAP code and a
  non-English message is settled by the read-back; a stand-in raising an unrelated code with a
  message that happens to contain "already exists" stays a failure (the code wins). Mutation at the
  call site: match text only.

## Guard proof

Pending.
