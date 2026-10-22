# aud-a2s: The room-list preflight's and add's outer catches name no step but the import, and the add's catch blames Set-ADGroup for any step

**Severity**: LOW - wording in the log and the row message; no wrong write. The aud-a2q/a2r
outer-catch class on the two room-list paths: `PreflightRoomListAsync`'s catch and
`AddToRoomListViaAdAsync`'s catch name only the import step; the add's catch logs
"Set-ADGroup ... failed" and reports a write failure when the runspace, credential, catalog
resolution, re-read, membership read or read-back failed instead.
**Status**: Merged to master (`3ff4a6d4`); closed on guard proof; codex review pending (batched with aud-a23g)
**Branch**: `worktree-agent-a0b892d21bfcfd31b`
**Commit**: `cf9ae39e` (fix, tests, comments, record); plan `1e52277d`; merged `3ff4a6d4`
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

## Approach (as implemented)

- `PreflightRoomListAsync` and `AddToRoomListViaAdAsync` (`Services/ConferenceRoomService.cs`) keep `step`, set before
  each step: "opening the directory runspace", "importing the ActiveDirectory module", "preparing the module's AD
  credential", "reading the room's AD object", then the resolver's three, then "reading the membership of the list's
  group". The add sets "Set-ADGroup on the group's member attribute" before it builds the write and keeps it to the end:
  the write's error is held and the read-back's caught (aud-a2n), so the catch never sees either.
- `ResolveAdGroupGuid(ps, roomListName, listAddress, credential, ref string step)`: "resolving the forest's global
  catalog" before `ResolveForestGlobalCatalog`, "searching the forest's global catalog for the list's group" before the
  search, "reading the list's group in its own domain" before `ReadAdGroupInItsDomain`. Its refusals are unchanged; its
  two callers pass their `step`.
- The preflight's catch logs `"On-prem room-list preflight for {Room} on {List}: {Step} failed; nothing was written"` with
  the exception and returns "Failed to validate the on-prem room list '<list>' before writing: <step> failed: <error,
  ended as a sentence> Nothing was written."; the add's logs `"On-prem room-list add for {Room} on {List}: {Step}
  failed"` with the exception and returns "Failed to add the room to the on-prem room list '<list>': <step> failed:
  <error>". The callers' handling is unchanged: the preflight's one failed step `Room list preflight`, not partial; the
  add's failed step `Add to '<list>' (on-prem)` inside the partial.
- aud-a2r's catch around each import is gone. For the import step the two catches give its message and its rendered log
  line exactly as before, on each path.
- Harness (`ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`), test-only and additive, off by default:
  `FailuresAfterSetPlace` (a Hashtable, passed to the directory runspace as `Failures` is): the directory
  `Write-StandInCall` throws its entry for the command once `Test-StandInAfterSetPlace`, after `Failures`' own check; the
  stand-in `Get-ADGroup` takes its `Get-ADGroup -Identity` entry when `Failures` has none, once after a Set-Place.
  `MemberReadFailureKey` (`"Get-ADGroup -Properties member"`, public const): the stand-in `Get-ADGroup` fails a read by
  identity whose `-Properties` holds `member`, after finding the group, with the entry of `Failures` - or, when it has
  none and a Set-Place is recorded, of `FailuresAfterSetPlace` - as Write-Error then return, as its other errors.
- `ExchangeAdminWeb.Tests/ConferenceRoomRoomListPreflightTests.cs`: the `Service` takes an optional logger and
  `IncompleteCredentialFor`; a private `CapturingLogger`, `ConferenceRoomWriteIdentityTests`' shape. New -
  `AFailureAtEachStepOfTheRoomListPreflight_NamesThatStep` (8), `AFailureAtEachStepOfTheOnPremRoomListAdd_NamesThatStep`
  (10), `TheHarness_FailuresAfterSetPlace_AndTheMemberReadKey_FailOnlyWhatTheyName` (1): 19 cases. No existing test or
  assertion changed; on the fix the 311 `ConferenceRoom*` cases passed, the 292 existing among them.

## Guard proof

| Test | What it pins |
| --- | --- |
| `AFailureAtEachStepOfTheRoomListPreflight_NamesThatStep` (runspace, import, credential, room object read, group search, group re-read, membership read) | the finding on the preflight: each throw before Set-Place refused, not partial, nothing written, the one step `Room list preflight` with the message as its `Error`; the message "Failed to validate the on-prem room list '<list>' before writing: <step> failed: <error> Nothing was written.", carrying the injected text; an error logged naming the step |
| the same (catalog) | unchanged, passing before the fix: the catalog named by the resolver's own refusal, ending "Nothing was written." |
| `AFailureAtEachStepOfTheOnPremRoomListAdd_NamesThatStep` (runspace, import, credential, room object read, group search, group re-read, membership read) | the finding on the add: each throw after Set-Place a failed, partial row, nothing recorded as written, no confirmed membership, the step `Add to '<list>' (on-prem)` failed; "Failed to add the room to the on-prem room list '<list>': <step> failed: <error>" in the partial, carrying the injected text, "Nothing was written" nowhere; an error logged naming the step, no log line naming Set-ADGroup, no `Set-ADGroup` made |
| the same (catalog, write, read-back) | unchanged, passing before the fix - the steps that never reached the catch: the catalog's refusal, no write made; the write's "... was not confirmed: ..., and Set-ADGroup reported an error (...)." and the read-back's "... could not be confirmed: reading its group <DN> back from contoso.test failed: ...", each logged as a warning in its own words, one `Set-ADGroup` made |
| `TheHarness_FailuresAfterSetPlace_AndTheMemberReadKey_FailOnlyWhatTheyName` | the knobs the cases above rest on, under `-ErrorAction Stop`: the member key in `Failures` fails a member read at any time, and neither the search nor the re-read; each key of `FailuresAfterSetPlace` (`Get-ADForest`, `Get-ADGroup`, `Get-ADGroup -Identity`, the member key) fails nothing before a Set-Place is recorded and its own call after one; after one, the member key fails neither the search nor the re-read, and the read-by-identity key not the search |
| `ConferenceRoomWriteIdentityTests.AFailedImportOnTheOnPremRoomListPaths_NamesThatStep` (aud-a2r's; unchanged) | the import's message and log line on each path, now through the step catch |

**Mutations** (`scratchpad/a2s-round/round.ps1`, the a2r runner on this worktree, an edit able to replace a whole file, with
`rows-a2s.ps1`; log `round-a2s.log` beside them; 2026-10-22 15:11-15:15). Each row ran the 311 `ConferenceRoom*` cases, the
test project rebuilt each time, the failures read from its TRX. md5 before = restored: `Services/ConferenceRoomService.cs`
`50ffb33dd25f7dafbee9716c1ec820c0` (M rows, REV); `ExchangeAdminWeb.Tests/ExchangeWriteHarness.cs`
`e224831b19dfb31e4ce513c27bd0e0d3` (H rows). Each file was touched once mutated and once restored, so each build compiled
the file as it then was. Test names are in `ConferenceRoomRoomListPreflightTests` unless qualified (`Identity.` is
`ConferenceRoomWriteIdentityTests`).

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| M1 | the brief's (1), at the call site: the preflight's step naming collapsed - its catch as at `225e61fc` (log "On-prem room-list preflight failed for {Room} on {List}", message "Failed to validate the on-prem room list '<list>' before writing: <error> Nothing was written.") | `bcfc145a76f30f49877b7910ae31d609` | `AFailureAtEachStepOfTheRoomListPreflight_NamesThatStep` (runspace, import, credential, room object read, group search, group re-read, membership read); `Identity.AFailedImportOnTheOnPremRoomListPaths_NamesThatStep` (preflight) | FAIL, 8 of 311: no step named. PASS: the catalog case, whose refusal is not the catch's |
| M2 | the brief's (2), at the call site: the add's step naming collapsed - its catch as at `225e61fc` (log "On-prem room-list member add (Set-ADGroup on the group's member attribute) failed for {Room} on {List}", message "Failed to add the room to the on-prem room list on the AD object: <error>") | `d7bebb59b877e285ab8425ad94bc535b` | `AFailureAtEachStepOfTheOnPremRoomListAdd_NamesThatStep` (runspace, import, credential, room object read, group search, group re-read, membership read); `Identity.AFailedImportOnTheOnPremRoomListPaths_NamesThatStep` (add) | FAIL, 8 of 311: "on the AD object", no step named. PASS: catalog, write, read-back - none reaches the catch |

Beyond the brief's two:

| # | Mutation | md5 mutated | Named test | Result |
| --- | --- | --- | --- | --- |
| REV | the fix reverted: the service whole as at `225e61fc`, the new tests and the harness kept (they name nothing the fix adds to the service) | `bf67aebfd2dbb1048ff08f92a39a0d6d` | both theories (runspace, credential, room object read, group search, group re-read, membership read) | FAIL, 12 of 311. PASS: the import on both paths - aud-a2r's catch of its own named it - and catalog, write, read-back |
| M3 | the resolver's own step updates dropped: the search and the re-read run under the catalog's step | `a0e177cfc701ccb947092631f01e6151` | both theories (group search, group re-read) | FAIL, 4 of 311: told as "resolving the forest's global catalog failed" |
| H1 | harness: `FailuresAfterSetPlace` ignored (its check in `Write-StandInCall` and both in `Get-ADGroup` removed) | harness `d68346cd22758745968f56984701874f` | the harness self-test; the add theory (catalog, group search, group re-read, membership read) | FAIL, 5 of 311: "No exception was thrown"; each of those adds a success, nothing having failed - the wrong-reason pass the self-test and the outcome assertions both stop |
| H2 | harness: the member-read key ignored, in `Failures` and `FailuresAfterSetPlace` alike | harness `fd0065505130b4537330047227a7068c` | the harness self-test; both theories (membership read) | FAIL, 3 of 311 |

Every restored md5 equal to "before"; after the last row the restored tree rebuilt and the 311 `ConferenceRoom*` cases green.

**Comments after the round.** The round ran on the fix with the comments the change made false removed (aud-a2r's two import
catches' and the add's catch's, which named Set-ADGroup as the line's subject) and none of the new explanatory comments. They
were written afterwards and checked by `scratchpad/a2s-round/comment_only.ps1` (aud-a2q's checker: blank, `//`, `///` and
PowerShell `#` lines dropped from both versions, the code lines identical in order): service
`50ffb33dd25f7dafbee9716c1ec820c0` -> `52fdc56d7abb05fe8c0a67863c5fde69` (code lines 1771 = 1771; comment and blank lines 937
-> 957); harness `e224831b19dfb31e4ce513c27bd0e0d3` -> `e3f934bddcceb3d2503b0bee571fbe65` (688 = 688; 456 -> 461); the test
file `1bd7c2a6aebfde2edf3c69d834681bd9` -> `84b7022da2bb5d76b5d4775a815a547e` (1191 = 1191; 437 -> 437: one comment, written
with the tests, reworded). Among them aud-a2r's `ImportActiveDirectoryModule` header, whose "the room-list paths in a catch of
their own" the fix made untrue, corrected.

## Known gaps

- The credential step is reached only through the test seam. `CreateAdCredential` throws only for a null username or
  password, and the PAM provider returns no credential at all for a secret missing either
  (`DelineaService.GetCredentialsBySecretIdAsync`), which the paths refuse before the runspace as "not configured". The two
  credential cases pin the step's name, not a failure seen live.
- Two steps are named for accurate tracking but no test can make them reach the catch: "resolving the forest's global
  catalog" (`ResolveForestGlobalCatalog` catches every error and the resolver refuses in words naming the catalog, which the
  catalog cases pin) and, on the add, "Set-ADGroup on the group's member attribute" (building the write: `ServerFromDn` and
  `BuildMemberAttributeWrite` are pure, and the DN is never null there).
- A throw after the write's `Invoke` - only logging and string building follow it, the read-back catching its own - would be
  told as "Set-ADGroup on the group's member attribute failed" though the write might have applied: aud-a2q's accepted
  residual on the City/State/Country write, the same here.
- Not changed, outside this record: the City/State/Country paths name a credential step that no test reaches (aud-a2q's and
  aud-a2r's step theories cover the runspace, the import and the object read). Reported in the hand-back.
- Not changed, outside this record: the refusals the resolver and the membership read return rather than throw (the catalog
  unresolved, no group or several, a group answering as another object) are logged nowhere by the service on either path;
  the row's audit carries the message. Reported in the hand-back.

**Operator-visible changes.**

- A room-list preflight failing at the runspace, the credential, the room's object read, the group search, the group's
  re-read in its own domain or the membership read says "Failed to validate the on-prem room list '<list>' before writing:
  <step> failed: <error>. Nothing was written." and logs the step; it said the same with no step. The import's message is
  unchanged.
- The on-prem add failing at those steps says "Failed to add the room to the on-prem room list '<list>': <step> failed:
  <error>" inside the row's partial, and logs "On-prem room-list add for <room> on <list>: <step> failed"; it said "Failed to
  add the room to the on-prem room list on the AD object: <error>" and logged a failed Set-ADGroup. The import's message is
  unchanged.
- Nothing else: no outcome, write, audit or notification changes.

**Dev acceptance:** nothing new beyond the Room Finder check owed with aud-a2f..a2r: a clean row's path is unchanged, and the
changed messages are failures, which are not to be induced on dev. Not run here: no directory, Exchange or SMTP call was made,
and the owner's standing instruction is not to probe Active Directory.

## Reviewer comments

Implemented under the owner's 2026-10-21 go by an Opus 5.5 implementation agent on `worktree-agent-a0b892d21bfcfd31b`.
Independent review pending.

For the reviewer:

- One fix commit for one finding. The evidence's list of steps included two that never reached the add's catch - the
  catalog resolution and the read-back each catch their own error - corrected in the plan; their cases stay in the add's
  theory as pins of the words they already had.
- `ResolveAdGroupGuid` takes the caller's `step` by `ref`, not a split of the resolver into its parts at both call sites:
  its three steps are one call to each caller, and the resolver is the one place that knows where one ends and the next
  begins.
- aud-a2r's catch around each import is removed, not kept beside the step catch: the step catch renders the import's message
  and log line exactly as that catch did, so keeping both would be two places for one sentence.
  `AFailedImportOnTheOnPremRoomListPaths_NamesThatStep` passes unchanged, and M1/M2 show it now rests on the step catch.
- The add's message names the list (it said "on the AD object" and no list) and drops "on the AD object": the catch is
  reached by steps that read the room's object, the catalog and the group, not only the room's object.
- "Set-ADGroup on the group's member attribute" keeps aud-a2n's wording for the one step it is true of - building the write.
- Tests in `ConferenceRoomRoomListPreflightTests`, the room list's own file, which gains a capturing logger in the shape
  `ConferenceRoomWriteIdentityTests` keeps; aud-a2r's import test stays where it is.
- Beyond the brief's two mutation rows: a whole-file revert of the service (possible here, unlike a2q/a2r, because the tests
  name nothing the fix adds to it), one row for the resolver's own step updates, and two harness rows for the new knobs.
- Outside the service and its tests: the harness only (test-only, additive: one Hashtable and one key, off by default).

## Closeout

Fixed on branch, not merged, not pushed: the plan `1e52277d`, then this fix, tests, comments and record in one commit. A
module-scoped behaviour change (Conference Rooms: the room-list preflight's and on-prem add's failure messages and log
lines): the Conference Rooms module version bump is due at merge - not made here, `Modules/ModuleCatalog.cs` being outside
this brief. No shared code changed (the service is the module's, the harness test-only), so no base app bump.

Gates, run on the final files this commit holds (the mutation round ran before the comments, which the comment-only check
shows changed no code line):

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count - none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: 5464 passed, 0 failed, 3 skipped (the known AD
  skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`), 5467 total, 5 m 1 s; nothing hung, no Sequence
  file. This record's new cases are 19; the 316 `ClickGate*` cases are among those passed (and, run alone, 316 passed).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD`: clean.
- `tools/Test-AsciiOnly.ps1` after `git add -A`: clean.

PSScriptAnalyzer and Pester were not run: no `.ps1` or `.psm1` changed (the harness's stand-ins are PowerShell inside a C#
string, run by the tests; the scratch runner and checker are not in the repository).

Base `225e61fc`. The local and remote master have since moved to `f9a6beb8` - one commit, aud-a2r's review record and its
index row - touching none of this record's files; the totals above are this branch's.
