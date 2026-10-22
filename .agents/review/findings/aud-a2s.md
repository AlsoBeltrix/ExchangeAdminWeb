# aud-a2s: The room-list preflight's and add's outer catches name no step but the import, and the add's catch blames Set-ADGroup for any step

**Severity**: LOW - wording in the log and the row message; no wrong write. The aud-a2q/a2r
outer-catch class on the two room-list paths: `PreflightRoomListAsync`'s catch and
`AddToRoomListViaAdAsync`'s catch name only the import step; the add's catch logs
"Set-ADGroup ... failed" and reports a write failure when the runspace, credential, catalog
resolution, re-read, membership read or read-back failed instead.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
**Branch**: `worktree-agent-a0b892d21bfcfd31b`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2r agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `027c3309`, `Services/ConferenceRoomService.cs`: the two room-list paths' outer catches, compared
with the City/State/Country paths' step tracking added by aud-a2q (write) and aud-a2r (preflight).

## Approach (to be written by the implementing agent before code)

- Apply the same step tracking to both room-list paths: runspace, import, credential, catalog,
  group search, re-read, membership read, write, read-back; the catch names the step in the log line
  and in the row message; the write's own failure keeps its text. Tests through `ExchangeWriteHarness`:
  a failure injected at each step names that step on both paths. Mutation at the call site: collapse
  the step naming on each path.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs includes ALL the bugs ... EVERYTHING", `.agents/decisions.md`
2026-10-21); the admitted approach above and the supervising session's brief. One finding, so one fix commit. Base
`225e61fc`; the line numbers below are that tree's.

**Rule.** A failure on the room list's two on-prem paths names the step it came from, in the log line and in the row
message, as the City/State/Country paths do since aud-a2q (the write) and aud-a2r (the preflight). The write's and the
read-back's own failures keep their text.

*Correction to the evidence, read at `225e61fc`:* two of the steps it lists never reached the add's catch. The catalog
resolution catches its own error (`ResolveForestGlobalCatalog`, 2347-2367) and is refused in words naming it ("The
forest's global catalog could not be resolved (<error>) ..."), and the read-back catches its own (aud-a2n, 2012-2020).
The steps that reach the catches by a throw are the runspace, the import (named since aud-a2r by a catch of its own),
the credential, the room's object read, the group search, the group's re-read in its own domain and the membership
read - and, on the add, building the write.

1. **Steps.** `PreflightRoomListAsync` (`Services/ConferenceRoomService.cs` 1796-1876) and `AddToRoomListViaAdAsync`
   (1894-2063) keep `step`, set before each: "opening the directory runspace", "importing the ActiveDirectory module",
   "preparing the module's AD credential", "reading the room's AD object" (1851, 1927); then, inside
   `ResolveAdGroupGuid` (2269-2303), which takes the caller's `step` by `ref` because its three steps are one call,
   "resolving the forest's global catalog" (2278), "searching the forest's global catalog for the list's group" (2283)
   and "reading the list's group in its own domain" (2302, `ReadAdGroupInItsDomain`); then "reading the membership of
   the list's group" (`ReadOnPremListMembership`, 1867, 1943); and on the add only, before the write is built (1960),
   "Set-ADGroup on the group's member attribute".
2. **The catches.** The preflight's (1870-1874: log "On-prem room-list preflight failed for {Room} on {List}", message
   "Failed to validate the on-prem room list '<list>' before writing: <error> Nothing was written.") logs "On-prem
   room-list preflight for {Room} on {List}: {Step} failed; nothing was written" with the exception and returns "Failed
   to validate the on-prem room list '<list>' before writing: <step> failed: <error, ended as a sentence> Nothing was
   written." The add's (2053-2061: log "On-prem room-list member add (Set-ADGroup on the group's member attribute)
   failed for {Room} on {List}", message "Failed to add the room to the on-prem room list on the AD object: <error>")
   logs "On-prem room-list add for {Room} on {List}: {Step} failed" with the exception and returns "Failed to add the
   room to the on-prem room list '<list>': <step> failed: <error>". True of every step that reaches it: the write's
   error is held and the read-back's caught (aud-a2n), so only the steps before the write's `Invoke` get there. It comes
   after Set-Place, inside the row's partial, so it does not say nothing was written (aud-a2r).
3. **The import's own catches folded in.** aud-a2r's catch around each import (1835-1845, 1911-1921), there because
   these paths kept no `step`, is removed: the step catch tells the import as it did - the same message and the same
   rendered log line on each path - so aud-a2r's `AFailedImportOnTheOnPremRoomListPaths_NamesThatStep` stands unchanged.
4. **Unchanged.** The catalog's refusal; every refusal a step returns rather than throws; the write's failure (aud-a2n:
   "... was not confirmed: ..., and Set-ADGroup reported an error (<error>).") and the read-back's ("... could not be
   confirmed: reading its group <DN> back from <server> failed: <error>"), each with its own log line; the
   City/State/Country paths.

**Tests** (`ExchangeAdminWeb.Tests/ConferenceRoomRoomListPreflightTests.cs`, through `ExchangeWriteHarness`; no
directory, Exchange or network call). Its `Service` gains an optional logger - a capturing one, the shape
`ConferenceRoomWriteIdentityTests` keeps - and `IncompleteCredentialFor`: for the purpose it names, a credential with no
password, so `CreateAdCredential` throws in its step. *Not reachable live:* the PAM provider refuses such a secret before
this point (`DelineaService.GetCredentialsBySecretIdAsync` returns none for a missing username or password), so that case
pins the step's name, not a failure seen live.
- `AFailureAtEachStepOfTheRoomListPreflight_NamesThatStep` (runspace, import, credential, room object read, catalog,
  group search, group re-read, membership read), before Set-Place: refused, not partial, nothing written, the one step
  `Room list preflight` with the message as its `Error`, naming the list, carrying the injected text, ending "Nothing
  was written."; for each step but the catalog the message opens "Failed to validate the on-prem room list '<list>'
  before writing: <step> failed: " and an error is logged naming the step; the catalog in its own refusal.
- `AFailureAtEachStepOfTheOnPremRoomListAdd_NamesThatStep` (the same eight, then the write and the read-back), after
  Set-Place: failed, partial, nothing recorded as written, no confirmed membership, the step `Add to '<list>' (on-prem)`
  failed, its `Error` in the message and carrying the injected text, "Nothing was written" nowhere; the seven that reach
  the catch open "Failed to add the room to the on-prem room list '<list>': <step> failed: ", are logged as errors naming
  the step, with no log line naming Set-ADGroup and no `Set-ADGroup` made; the catalog in its own refusal, no
  `Set-ADGroup`; the write and the read-back in their own words, one `Set-ADGroup` made.
- Harness (`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`), test-only and additive, off by default:
  `FailuresAfterSetPlace`, `Failures`' keys and behaviour once a Set-Place is recorded, so the add's steps fail and the
  preflight's do not (`Failures` 63, the directory `Write-StandInCall` 850, `Get-ADGroup` 1023-1051, `Define` 589-643);
  and `MemberReadFailureKey`, "Get-ADGroup -Properties member", honoured in both, failing only a group read that asks for
  the member attribute - the membership read, which `Failures['Get-ADGroup -Identity']` cannot reach, failing the re-read
  before it. `TheHarness_FailuresAfterSetPlace_AndTheMemberReadKey_FailOnlyWhatTheyName`: each key of
  `FailuresAfterSetPlace` failing nothing before a Set-Place and its own call after one; the member key failing a member
  read, not the search or the re-read.

**Mutations, at the call sites** (md5 before and after, the named failing tests, restored, md5 matched, touched,
rebuilt, green): M1 the preflight's step naming collapsed - its catch as at `225e61fc`; M2 the add's step naming
collapsed - its catch as at `225e61fc`. Comments after the round, checked code-identical by a comment-only diff.

**Files.** `Services/ConferenceRoomService.cs`, `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`,
`ExchangeAdminWeb.Tests/ConferenceRoomRoomListPreflightTests.cs`, this record.

## Guard proof

Pending.
