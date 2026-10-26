# aud-a2aa: every Ambiguous protection result is reported as "matches multiple AD users", which is often wrong

**Severity**: LOW - diagnostic text. `Services/ConferenceRoomProtectionGate.cs` about 149: since aud-a2b/a2c an `Ambiguous` result can come from groups, lists or mixed object kinds, but the operator text always says "matches multiple AD users".
**Status**: In progress - admitted 2026-10-23 evening from the aud-a2e agent's hand-back; inside the released scope
**Branch**: `fix/a2e` (worktree `D:\source\ExchangeAdminWeb-wt\a2e`), continuing from `170b182f`
**Commit**: -
**Reviewer**: implementer (aud-a2e agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `3e43b35b`. Trigger: an address or name that resolves to two directory objects that are not both users. Predicted failure: the refusal names the wrong object kind, sending the operator to look at user accounts.

## Approach

Written before the code, against `170b182f`. The brief: carry the object kinds, or a neutral "directory objects", into
the message; pin the wording for a group/group and a user/group ambiguity; mutation: the fixed text restored.

**Why the kinds cannot be named, and the neutral wording is taken.** `ResolveExchangeTargetAsync` returns a status and
nothing else - on `Ambiguous` the principal is null - so the gate does not know which objects collided. There are four
ways to reach the status and only the oldest is users:

- a `Get-ADUser` filter matching twice (`ProtectedPrincipalService.ResolveViaActiveDirectory`) - users, the case the old
  text described;
- aud-a2b: Exchange reports a cloud-only recipient and Active Directory resolves the same string to an on-premises
  object, so the string names two objects - of any class, and across two directories;
- aud-a2b: Active Directory reads the typed string as one object and the recipient's canonical address as another;
- aud-a2c: two Active Directory objects hold the mailbox's `ExchangeGuid` - found with `Get-ADObject` precisely so a
  holder of any class is seen.

Naming the kinds would mean widening the resolution's return, which every other gate shares (`PermissionValidator` and
the four Exchange-written modules). That is a shared-infrastructure change for a LOW diagnostic, and it would move the
base app version. The brief's second option is taken: one message, true of all four sources, naming neither an object
class nor a single directory, and saying what to look at. The specific reason is already in the application log, which
every one of those refusals writes.

**The change.** `ConferenceRoomProtectionGate.AmbiguousMessage`, an internal constant beside `NoRoomDenial` so a test
can name it rather than copy it, replaces the literal at `:149`. The `Unavailable` branch beside it is untouched: it is
not an ambiguity and must not borrow its words.

**Deliberately out of scope.** The same literal stands in `GroupManagementService:68`, `M365GroupManagementService:217`,
`MigrationService:592` and `Components/Pages/MfaReset.razor:360`. Those resolve by other entry points and
`MigrationMessageTests` pins Migration's text; the finding names this gate, and the others are recorded in Known gaps as
the same candidate elsewhere rather than swept up here.

**Files to change.** `Services/ConferenceRoomProtectionGate.cs`; a new
`ExchangeAdminWeb.Tests/ConferenceRoomAmbiguityMessageTests.cs`; `Modules/ModuleCatalog.cs` (2.16.0 -> 2.17.0); this
record.

**Mutations planned.** The fixed text restored; the `Unavailable` branch made to borrow the ambiguity's words.

## Files changed

- `Services/ConferenceRoomProtectionGate.cs` - the `AmbiguousMessage` constant and its use at the deny.
- `ExchangeAdminWeb.Tests/ConferenceRoomAmbiguityMessageTests.cs` - new.
- `Modules/ModuleCatalog.cs` - Conference Rooms `2.17.0`.

## Guard proof

Both ambiguity shapes are driven through the REAL `ResolveExchangeTargetAsync` with only its two directory seams
scripted - the Active Directory lookup and the Exchange resolver - so each reaches `Ambiguous` the way the live code
reaches it, by a collision in which no user pair is involved. Neither test scripts the status.

| Test (`ConferenceRoomAmbiguityMessageTests`) | What it pins |
| --- | --- |
| `TwoGroups_AreNotReportedAsMultipleAdUsers` | group/group: Exchange Online holds the name as a cloud distribution group and Active Directory holds an on-premises group of that name (aud-a2b's cloud-only branch). The denial is the neutral message and says nothing of AD users |
| `AUserAndAGroup_AreNotReportedAsMultipleAdUsers` | user/group: the typed string reads as a user, the recipient's canonical address as a group, so the agreement check refuses. Two classes, and the old text named one of them |
| `TheMessage_NamesNeitherAClassNorADirectory_AndSaysWhatToLookAt` | the wording itself: no object class, and an instruction the operator can act on |
| `AnUnavailableResolution_StillSaysTheCheckCouldNotBeMade` | the sibling status keeps its own words - a check that could not be made is not a collision |

**Mutations**, each at its call site, applied by a script that refuses unless the old text occurs exactly once and the
md5 changes; rebuilt; the named tests run; restored from the saved bytes with a fresh mtime; rebuilt; run again.
`ConferenceRoomProtectionGate.cs` md5 before = restored = `21d12f045592bbfa24badfb97aaa1447` for both.

| # | Mutation | md5 mutated | Named tests | Mutated / restored |
| --- | --- | --- | --- | --- |
| AA1 | the fixed text restored - every ambiguity is "matches multiple AD users" again | `f304bff0614e02b822ee8a2db09927ab` | all four | FAIL 3 of 4, PASS 1 / PASS 4 |
| AA2 | the `Unavailable` branch borrows the ambiguity's words | `beb13d006ed2fe035161a0f4ad2fed98` | the unavailable case and one shape | FAIL 1 of 2, PASS 1 / PASS 2 |

Under AA1 the one that still passes is the unavailable case, which the mutation does not touch; under AA2 it is the
group/group shape, whose own message is unchanged. Each set discriminates.

## Coder dispute

None.

## Known gaps

**The same literal elsewhere, not swept up.** `Services/GroupManagementService.cs:68`,
`Services/M365GroupManagementService.cs:217`, `Services/MigrationService.cs:592` and
`Components/Pages/MfaReset.razor:360` still say "matches multiple AD users". Group Management and Migration resolve
through `ResolveWithExchangeFallbackAsync`, which reaches `Ambiguous` by the same aud-a2b routes, so the text is as
wrong there; `MigrationMessageTests:114` pins Migration's wording, so changing it is a test change too. **Candidate,
reported for admission; not fixed here** - one finding per commit, and the finding names this gate.

**The reason is still only in the log.** The operator is told that the identity is ambiguous, not which two objects
collided; the application log carries that, as each refusal already logs it. Naming them in the message needs the
resolution to report them, which is the shared-service change this deliberately did not make.

## Reviewer comments

Candidate raised by the aud-a2e implementer in its hand-back; admitted by the supervisor 2026-10-23 evening.
Implemented under the owner's 2026-10-21 go, released by the 2026-10-23 evening ruling, by an Opus implementation agent
on `fix/a2e`. Independent review pending.

## Closeout

Fixed on branch, not merged, not pushed. Module-scoped: Conference Rooms `2.17.0`. No shared infrastructure changed, so
this does not move the base app version.

Gates at the branch tip, over all four findings on it (aud-a2ab, aud-a2ac, aud-a2z, aud-a2aa):
`dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental` 0 errors, 19 warnings, none in a file this branch
touches; `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4` 6731 passed, 0 failed, 0 skipped;
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` clean; `git diff --check HEAD` clean;
`tools/Test-AsciiOnly.ps1` passed. `Components/Pages/ConferenceRooms.razor` is untouched by this branch and still 1758
lines, so no `ClickGateRegistry` or `ProgressRegistry` entry moves.

## Reviewer comments (supervisor, cr-batch-10)

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base ebd66b91 head 26e8cd9b (merged 3bfdc1af); 2026-10-26; capability proof succeeded (AGENTS.md read, git show --stat 26e8cd9b). One candidate, admitted as kd-68 (against aud-a2ab). Codex confirmed clean: the missing-list flow returns before Set-Place or any creation; the bulk path maps the refusal to a failed ROW, not a failed batch; `ReadRoomList` treats only a structural not-found as absence; the mail comparison is trimmed and case-insensitive and refuses an unset mail; the ambiguity text is centralised on every service Ambiguous path. Artefacts: `.agents/review/cr-batch-10.result.txt`, `.stream.log`, `prompt-cr-batch-10.md`.
