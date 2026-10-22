# aud-a2r: Conference Rooms - the preflight's outer catch names no step, Import-Module HadErrors branches are dead, a refusal says "Nothing was written" inside a partial, and the directory stand-ins do not route by domain

**Severity**: LOW - wording, dead code and a test gap; no wrong write. (1) `PreflightSyncedAttributesViaAdAsync`'s
outer catch reports "Failed to validate the AD object before writing: <error>" for any step with no
step named (the aud-a2q outer-catch class, on the preflight). (2) The `Import-Module` `HadErrors`
branches on the preflight, write and room-list paths return the first error record with no step
named; under `-ErrorAction Stop` they are effectively dead (the aud-a2o/a26g pattern). (3)
`ConfirmDirectoryObject`'s refusals end "Nothing was written." even when called from the write after
`Set-Place` committed, which is false inside the partial message. (4) Test gap: the harness stand-in
`Get-ADUser` and `Set-ADUser` answer on any server they can reach, so a wrong `-Server` is caught only
by pinning the parameter value, not by behaviour.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
**Branch**: `worktree-agent-ae910e9c92bf1c2aa`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2q agent, 2026-10-22, its four candidates)

## Evidence

At the aud-a2q branch tip `df084c68`, `Services/ConferenceRoomService.cs`: the preflight's outer catch;
the `Import-Module` invocations followed by `if (ps.HadErrors)`; `ConfirmDirectoryObject`'s message
suffix; `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs` user stand-ins (compare the group stand-in
`Get-ADGroup`, which routes by `-Server` since aud-a2j).

## Approach (to be written by the implementing agent before code)

- Name the step in the preflight's catch as aud-a2q did for the write's catch.
- Remove the dead `Import-Module` `HadErrors` branches (try/catch/finally; the thrown error is the
  step's error, named), as aud-a2o/a26g did; or pin them with a non-terminating stand-in if a reason
  to keep them is found - say which.
- `ConfirmDirectoryObject` takes the suffix from its caller (the preflight appends "Nothing was
  written."; the write appends nothing), so the message is true where it appears.
- Harness: `Get-ADUser`/`Set-ADUser` stand-ins route by `-Server` as `Get-ADGroup` does (an object
  answers only on its own domain's name, the catalog, or no `-Server` for the default domain), with
  a harness self-test; re-run the aud-a2p/a2q routing tests to confirm they now fail on behaviour, not
  only on the pinned value.
- Mutations at the call site: collapse the preflight step naming; restore one `HadErrors` branch;
  restore the fixed suffix; make the stand-ins answer on any server again (harness self-test fails).

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs includes ALL the bugs ... EVERYTHING", `.agents/decisions.md`
2026-10-21); the admitted approach above, the folded-in part at the end of this record and the supervising session's
brief. One finding, so one fix commit for its five parts. Base `d4c588b8`; the line numbers below are that tree's.

**Rule.** A failure names the step it came from; an import's error is the one it throws; a message says nothing was
written only where nothing was; a wrong `-Server` in a test fails by what the stand-in directory does with it, not only
by the value pinned; and the admin notification says what the row's audit says when a read-back confirmed a directory
write after an error.

1. **Preflight step naming.** `PreflightSyncedAttributesViaAdAsync` (`Services/ConferenceRoomService.cs` 1548-1625)
   keeps `step`, as the write does since aud-a2q - "opening the directory runspace", "importing the ActiveDirectory
   module", "preparing the module's AD credential", "reading the AD object" - set before each. Its outer catch
   (1619-1623: log "Set-ADUser preflight failed for {Room}", message "Failed to validate the AD object before writing:
   <error>") logs "Set-ADUser preflight for {Room} on the AD object {Object}: {Step} failed; nothing was written" with the
   exception and returns "City/State/Country could not be validated before writing on the AD object <guid>: <step>
   failed: <error, ended as a sentence> Nothing was written." The routed read (aud-a2q's `ReadObjectThrough`) keeps its
   own catch and refusal, which name the server; nothing after it reaches the outer catch.
2. **Import errors: the `HadErrors` branches removed, not pinned** (1578-1582, 1665-1669, 1831-1835, 1900-1904). This
   matches every other directory import in the app - `GroupManagementService` (119, 295, 403, 684),
   `ADAttributeEditorService`, `Comms10kService` - none of which checks `HadErrors` after it, and aud-a2o/a26g's
   treatment of the writes. Why dead, measured (`scratchpad/a2r-round/probe-import.ps1`, pwsh 7.6.6, an in-process
   runspace, no directory): a real `Import-Module` of a module that does not exist, under `-ErrorAction Stop`, throws
   `ActionPreferenceStopException` from `Invoke` and leaves 0 records, so the check after `Invoke` is never reached for
   its own error; without Stop it does not throw and leaves 1. The import is each runspace's first command, so no
   earlier record exists either. Kept, the branch could fire only on an error written past the preference - a module's
   load code with its own `-ErrorAction Continue` (*assumption, not probed: the ActiveDirectory module's import writes
   none*) - and would refuse a row whose module loaded, in no step's name. A new private static
   `ImportActiveDirectoryModule(ps)`: the import, `-ErrorAction Stop`, in `try { Invoke } finally { ps.Commands.Clear();
   ps.Streams.Error.Clear(); }` - the clear keeps a record the import left from being taken by the next read's own error
   check (`ConfirmDirectoryObject`'s `HadErrors`, psl-2) for that read's error. The thrown error is the import step's,
   named: the City/State/Country preflight and write by their outer catches (`step`); the room-list preflight and add,
   which keep no `step`, by a catch around the import - "Failed to validate the on-prem room list '<list>' before
   writing: importing the ActiveDirectory module failed: <error, ended as a sentence> Nothing was written." and "Failed
   to add the room to the on-prem room list '<list>': importing the ActiveDirectory module failed: <error>", each logged
   naming the step.
3. **The suffix from the caller.** `ConfirmDirectoryObject` (2051-2077) returns its three refusals without "Nothing was
   written." - the local shape of `ResolveAdGroupGuid` and `ReadOnPremListMembership`, whose callers add it. The two
   preflights, before any write, append it (`Sentence(<refusal>) + " Nothing was written."`; 1585-1587, 1841-1843); the
   City/State/Country write (1677-1680) and the room-list add (1910-1912), both after `Set-Place`, append nothing, so
   their partial ("Partial: ... were set in Exchange Online, but ... failed: <refusal> Re-run this row ...") no longer
   says nothing was written.
4. **Harness routing.** The stand-in `Get-ADUser` and `Set-ADUser` (`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`
   854-882) route by `-Server` through aud-a2j's `Get-StandInReach` and `Test-StandInReaches`, as `Get-ADGroup` does. An
   object lives in the forest domain its DistinguishedName ends with, or in the default domain when the DN names none
   of them (no DN, or no DC components - aud-a2q's fallback fixtures). It is found through that domain's DNS name, the
   catalog endpoint (a read: a `Set-ADUser` sent there is refused "The server is unwilling to process the request.", as
   the stand-in `Set-ADGroup` refuses one), or - default domain only - no `-Server` or a bare `:port`. Anywhere else:
   "Cannot find an object with identity", thrown by `Get-ADUser -Identity` and written then returned by `Set-ADUser`,
   as each one's not-found already is. `DirectoryServerFailures` and an unknown server are refused first, as now. The
   one existing assertion that pinned the old behaviour (aud-a2q's harness test: the default domain's room answering
   through `fabrikam.test`) becomes an object of `fabrikam.test` answering there while `contoso.test` fails - the
   failure still scoped to the server it names.
5. **Notification.** `Components/Pages/ConferenceRooms.razor`, three lines edited in place (1758 before and after; no
   `ClickGateRegistry` entry moves): the Finder handler's call (1080) passes `r.DirectoryAttributesConfirmed` and
   `r.DirectoryMembershipConfirmed`; `NotifyRoomAdminAsync` (1665) takes them as optional parameters; its details (1670)
   are `ConferenceRoomService.NotificationDetails(target, attributesConfirmed, membershipConfirmed)` - new, public
   static, pure: `Room`, then each detail present on a row of its own, `AttributesConfirmedNotificationRow`
   ("City/State/Country") and `MembershipConfirmedNotificationRow` ("Room list membership"), new public consts. The error
   field is untouched (`success ? null : errorDetail`). The rows follow the audit's rule - the page's `AuditFinderAction`
   records both details whatever the outcome - so a partial whose City/State/Country were confirmed before a later step
   failed carries the row beside its error. A clean success's notification, and a clean failure's, is `Room` only, as
   now. The other callers (Type, the catch) pass nothing. The bulk completion notice is a counts summary whose rows
   already carry the details (aud-a2o, aud-a2q): unchanged.
6. Comments after the mutation round, checked code-identical by a comment-only diff; aud-a2q's comment that "the admin
   notification carries this message" (652-653), untrue - the notification drops a success's message - corrected
   there.

**Tests** (through `ExchangeWriteHarness`; no directory, Exchange or network call).
- Harness, test-only and additive, every knob off by default: `DirectoryRunspaceFailure` and `ImportModuleFailure`
  (at any time - the preflight's steps, before `Set-Place`); `ImportModuleReportsErrorWithoutThrowing` (a message the
  stand-in `Import-Module` - now an advanced function, so `-ErrorAction Stop` acts on it as on the real cmdlet - writes
  with its own `-ErrorAction Continue`, then carries on); `ObjectReadAnswersAs` and `ObjectReadAnswersAsAfterSetPlace`
  (the objectGUID a `Get-ADUser -Identity` without `-Properties` - the read that proves the object - answers with, at any
  time or only after a `Set-Place`). The routing of part 4.
- `ConferenceRoomWriteIdentityTests`:
  - each preflight step failing (runspace, import, object read through `Failures['Get-ADUser']`): refused, not partial,
    `Writes` empty, the one step `Set-ADUser preflight (City/State/Country)` failed with the message as its `Error`,
    naming the step, carrying the injected text, ending "Nothing was written."; a logged error naming the step;
  - the import failing on the room-list preflight and on the add: the step named in the message and the log; refused
    before any write, or partial;
  - an error the import reports without a throw, on all four directory paths in one row (City/State/Country and a synced
    list): success, not partial, the room's object written, both directory writes made, the text in no message or step;
  - `ConfirmDirectoryObject` refusing (another object answering) on each of the four paths: a preflight refused, ending
    "Nothing was written."; a write partial, the refusal in its step and the message, "Nothing was written" nowhere;
  - harness: the routing self-test - an object of each domain answering and written only where part 4 says, the catalog
    answering a read and refusing a write, no DN or no DC components the default domain; each knob above pinned; and a
    real `Import-Module` of a module that does not exist, in a default runspace, under `-ErrorAction Stop`, throwing and
    leaving no record - the measurement of part 2 on this SDK (7.6.1).
- `ConferenceRoomDirectoryAuditTests`, the page, its email substitute kept: the admin notification of a clean success
  (both directory writes clean) - `Room` only, no error; City/State/Country confirmed after an error - `Room` and the
  row equal to the audit's `attributesConfirmedPresent`, no error; membership confirmed - its row equal to
  `membershipConfirmedPresent`; both; a partial whose City/State/Country were confirmed before the timezone failed - the
  row beside its error.

**Routing re-run.** The aud-a2p routing mutations (its M5, the read-back without `-Server`; its M6, the write without it)
and aud-a2q's (its M1, the routed preflight read dropped; its M5, the write deriving its server from its own read's DN)
re-applied at their call sites on this tree; each reported by the assertion that now fails - behaviour (the row's outcome)
or only the `-Server` pin.

**Mutations, at the call sites** (md5 before and after, the named failing tests, restored, md5 matched, touched, rebuilt,
green): M1 the preflight's step naming collapsed to its old catch; M2a-d one `HadErrors` branch restored, at each of the
four imports in turn; M2e the import's `finally` without its error-stream clear; M3 the write's caller restoring the fixed
suffix (M3b the room-list add's; M3c the preflights' suffix dropped); M4 the stand-ins answering on any server again; M5
the notification's details without the confirmed rows; harness rows for the knobs the tests rest on.

**Files.** `Services/ConferenceRoomService.cs`, `Components/Pages/ConferenceRooms.razor` (three lines in place),
`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`, `ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomDirectoryAuditTests.cs`, this record.

## Guard proof

Pending.

## Folded in: the success-after-error detail is dropped from the admin notification (codex, a2q review, 2026-10-22)

`Components/Pages/ConferenceRooms.razor` around line 1669: `NotifyRoomAdminAsync` passes
`success ? null : errorDetail` to `EmailService`, so when `Set-ADUser` applied City/State/Country, then
threw, and the read-back confirmed the values (aud-a2q), the page, result and audit say so
(`attributesConfirmedPresent`, no `writtenDirectoryObject`) but the admin notification is a generic
SUCCESS email with the room only. Carry the `DirectoryAttributesConfirmed` (and the aud-a2n
`DirectoryMembershipConfirmed`) detail into the success notification as its own line, not through the
error field; the page has line-keyed `ClickGateRegistry` entries, so edit in place. Test: the
confirmed-after-error case's notification names the condition; a clean success's does not. Mutation
at the call site: drop the detail from the notification.
