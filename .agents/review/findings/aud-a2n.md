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

## Folded in: two LOW candidates from the codex review of aud-a2k/a2l (2026-10-22)

Both belong to the add's error handling this record already owns (`prompt-a2k-a2l.md`,
`a2k-a2l.result.txt`):

1. **A refused duplicate must not be audited as this operation's AD write.** When `Set-ADGroup`
   is refused as "already there" and the read-back finds the room, the row succeeds and returns
   `roomGuid`, so the page and bulk audit record `writtenDirectoryObject` although this operation's
   write was refused (aud-a2g rule: name only what a write produced). Keep the row successful;
   return no written directory object for a refused write; add a "membership confirmed present"
   detail instead. Test: the race case's audit carries no `writtenDirectoryObject`.
2. **Every write error is reconciled by the read-back, not only the duplicate.** A `Set-ADGroup`
   that applied the change and then raised a timeout or transport error is reported PARTIAL even
   though the membership is present; `GroupManagementService` reconciles all write outcomes by
   read-back. Catch the write error, clear the pipeline, read back on the group's own domain, decide
   the row from the read-back, and keep the original error text in the step and audit detail. A
   read-back that fails or misses stays a failed row carrying the write error. Test: a stand-in
   whose `Set-ADGroup` applies the add and then throws a non-duplicate error is settled as success
   with the error recorded in the detail; one that throws without applying stays a failure.

Mutations at the call site for both: return `roomGuid` on the refused-duplicate path; read back
only duplicate errors again.
