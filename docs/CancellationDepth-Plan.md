# Cancellation Depth - Plan

Status: **DRAFT - awaiting owner go.** Nothing below is authorised yet.

Two findings the Constitution's Planning Rules hold behind a written plan - `kd-58` ("Exchange service or shared
infrastructure refactors") and item (12) of `aud-a67` ("persistence/storage changes") - and one new finding this
plan's reading turned up (section 14, item 1). Lines are at master `d8af27a8` (2026-10-27). Everything here was read,
not run: no build, test or live call was made to write it. Current state lives in `.agents/state.md`, not here.

---

## Exec summary

*This is the only part of this plan the owner reads or is bound by (`.agents/decisions.md` 2026-09-30, 2026-10-01).
Everything below it is agent-facing.*

**What it does.** When an operator leaves a page in the middle of an operation and confirms the "leaving will cancel
it" prompt, the app is meant to stop the work. In two places it often cannot. Work waiting its turn for one of the
app's shared Exchange Online connections, or for the on-premises Exchange server, goes on waiting and then runs - up to
two minutes after the cancel. And on the module settings page, a save the page has given up waiting for (it waits ten
seconds, then moves on) still lands in the settings database whenever the database answers, although the page has
just told the operator it would not. This plan makes both stop wherever stopping is safe.

**What it gets you.** In Recipient Lookup, Message Trace, Conference Rooms, Mailbox Permissions and Calendar
Permissions, a cancel takes effect while the work is still queued or between its steps, instead of after it.
Cancelling a Room Finder bulk job also stops the room it is working on, if that room has not been changed yet. Two
real problems close with it. As the code reads today, a Mailbox or Calendar Permissions change that is waiting its
turn - in Exchange Online or on-premises - is still made after the operator cancelled: the same fault fixed in
Conference Rooms on 27 October. And a slow settings save will do exactly what the page already says it does. Nothing
new to click, no new prompts; the operator sees the existing "Cancelled by the operator" wording, earlier.

**What it costs.** About fourteen small changes, landed one at a time: three to the shared Exchange machinery (with
Recipient Lookup as their first user), seven that each bring one more module, or one part of one, onto it, three for
the settings database and page, and a closing one for versions and records. Most are one module's code and its tests.
One independent review of the three shared pieces is recommended before any other module uses them, because every
Exchange Online call in the app passes through them.

**Biggest risk.** The shared Exchange connections are reused by every operator, so a connection left damaged by a
cancel would break somebody else's next action. This plan never interrupts a command once it has been sent - to
Exchange or to the database. A cancel always lands between commands, where a connection is exactly as any normal call
leaves it, and a connection ever found interrupted is thrown away rather than reused. The price is that a cancel may
wait for the one command already running, normally a few seconds.

**One thing you should know.** Because nothing already sent is interrupted, a change Exchange has been asked to make
is never left half-made, and whatever was already changed stays changed - as the prompt already says. One limit stays
as it is today: if the settings database itself hangs, the background worker waiting on it stays stuck until the
database answers. No cancel can reach a database call that is itself stuck.

**What approving authorises:** the three shared Exchange changes; their use by the five modules named above; the fix
for the two permission modules' late changes; the settings-database change; and the settings-page change for a save
the page has stopped waiting for. It does NOT authorise interrupting a command mid-flight, adding a cancel button or a
prompt to the settings pages, or extending cancellation to any other module or page.

---

## 1. Read first

For a cold implementer, in this order: `AGENTS.md`, `.agents/repo-guidance.md`, `docs/ProjectConstitution.md`,
`.agents/state.md`; then the records - `.agents/review/findings/kd-58.md` (its sizing, points 1-5, and "Also for the
plan"), `aud-a67.md` ("(12) is not fixed, and why"), and for the rules this plan keeps `kd-6.md`, `kd-57.md`,
`kd-59.md`, `kd-61.md`, `kd-69.md`, `kd-70.md`, `kd-73.md`, `kd-75.md`, `kd-76.md`. Then the files each slice names.
Re-read every file before editing it: the line numbers below will drift (Known Failure Class 4).

## 2. What cancellation does today - the rules this plan keeps

- **The operator's cancel has exactly two sources.** The navigation guard's OK on "Leaving this page will CANCEL it"
  (`Components/Shared/InFlightWorkGuard.razor` 84, `Progress.CancelAll()`), and the circuit closing
  (`Services/Progress/ActivityProgress.cs` 254-284: every activity still running is cancelled, then completed "The page
  was closed before this finished."). Nothing else cancels an activity - there is no other caller of `Cancel` or
  `CancelAll` - so only work that runs under an activity can be cancelled at all.
- **Only the activity's own token is the operator's cancel** (kd-57). A cancel is filed result `Cancelled` with the
  words `Cancelled by the operator` (`AuditService.CancelledResult`, `CancelledByTheOperator`, `FiledCancelled`); any
  other cancellation - a timeout nobody asked for - is a failure in its own words. Every module that files a cancel
  tests both sides (the kd-57, kd-59 and kd-70 controls).
- **Nothing after a first write takes a token** (kd-59, kd-73, kd-75). A module looks at its token at its last points
  before its first write. Conference Rooms: the preflights' credential fetches, a check immediately before Set-Place
  (`Services/ConferenceRoomService.cs` 632), and one inside Set-Place's pooled call before its command is built
  (647-651), thrown once the pool has the connection back (672-673). The fetches after Set-Place pass
  `CancellationToken.None` (1874, 2186).
- **"Not sent" is the service's to say** (aud-a76). In `PermissionWriteSubmission` a throw before `Submitting()` leaves
  as `PermissionWriteNotSubmittedException`; kd-59's `PermissionChangeFailure.CancelledBeforeItWasSent` files that, with
  the activity's token cancelled, as Cancelled - "Cancelled by the operator before the change was sent. Nothing was
  written." - and sends no notification.
- **Blocked Senders stops its wait, never the call** (kd-6, kd-76). `Services/BlockedSenderService.cs` 67-68:
  "Stopping the wait does not stop the call: the pooled pipeline runs on, and the pool returns or discards its
  connection when it ends." Its `UnblockSubmission` handshake (186-206) settles sent against not sent. kd-6's reason
  for not stopping the pipeline: "it cannot be verified without a live tenant."
- **A cancel may wait for the one cloud read in flight** (kd-70; kd-73: "the window under way runs to its end").
- **A cancel is looked at around synchronous reads it cannot interrupt** (kd-61, kd-69: before and after the credential
  lookup's configuration reads).

## 3. Item 1 - the Exchange Online pool and the on-premises steps (kd-58)

### 3.1 What takes no token today

Re-verified at `d8af27a8`. kd-58's sizing (points 1-5) stands; its line numbers have moved.

**The pool** (`Services/ExoConnectionPool.cs`):
- `RunWithRetryAsync` (451-456) borrows with `() => BorrowAsync()`: no token. `BorrowAsync(TimeSpan, CancellationToken)`
  (262) honours one at the slot wait (294) and, through `CreateConnected`, at the connect gate (784), but no production
  caller passes one (the recipient autocomplete passes a five-second wait only).
- `RunWithRetryCoreAsync` (492-548): the borrow is a step of every attempt (507); a throw is sorted by its words (514;
  `IsConnectionError`, 463-468); anything else is given back under `PoolFailurePolicy.Return` (523-530).
- `Return` (382-408) pools any connection of the current generation and never looks at its pipeline's state, so a
  pipeline stopped mid-command would be lent again (kd-58 point 4). Nothing in the app stops one today.

**The base** (`Services/ExchangeServiceBase.cs`):
- `RunAsync` (49-107) and `RunPooledQueryAsync` (113-135) take no token, and both carry writes as well as reads:
  Calendar Permissions' cloud change goes through `RunAsync`, Mailbox Permissions' through `RunPooledQueryAsync`.
- The command helpers - `Invoke` (248), `InvokeBestEffort` (292), `InvokeOptional` (326), `ReadUnlessNotFound` (373) -
  take a `ConnectionErrorTracker` (153-164) and no token.
- `ThrottledAsync` (137-147) waits up to two minutes with no token (141). `ConnectOnPrem` (506-557) makes three
  attempts with `Thread.Sleep(2000 * attempt)` between them (552), no token; it is virtual, a test seam with ten
  overrides in six test files.
- `GetOnPremMailboxSizeAsync` (628) and `GetOnPremMailboxLocationAsync` (780) take the token to the credential fetch
  only (kd-37); the throttle (645, 795), `runspace.Open()` (650, 800), `ConnectOnPrem` (656, 806) and the reads take
  none. `GetMailboxLocationAsync(string, CancellationToken)` (730) hands its token to the on-premises half only - the
  cloud half, `HasCloudMailboxAsync` (762, called at 735), takes none - and `GetMailboxLocationAsync(ValidatedMailbox?)`
  (759-760), the form both permission pages call, takes no token at all.

**The modules:**
- Recipient Lookup: the cloud read (`Services/RecipientLookupService.cs` 16) takes none, and its outer
  `catch (Exception ex)` (144) would turn a cancellation into a lookup error.
- Message Trace: the cloud window read (`Services/MessageTraceService.cs` 451-454) and the cloud detail read (164-167)
  take none, and their outer catches (525, 188) would swallow a cancellation. The on-premises search (551) and detail
  (209) take the token to the credential fetch only (kd-59). The window loop checks between windows (395, kd-73).
- Conference Rooms: the room read and the room-list resolution before Set-Place, and the preflights' directory work
  after their credential fetches (the `Task.Run` bodies at 1779 and 2083), take none. Set-Place's `RunAsync` (640)
  borrows with none; kd-75 looks inside the call instead (647-651). The Room Finder bulk rows pass
  `CancellationToken.None` (778-783), and the processor passes nothing (`Services/Jobs/ConferenceRoomBulkProcessor.cs`
  217).
- Mailbox Permissions: the single cloud change (`Components/Pages/MailboxPermissions.razor` 488-489, reaching
  `Services/MailboxPermissionService.cs` 53, `submission.TrackAsync(() => RunPooledQueryAsync(...))`) and the location
  read before it (461) run under the handler's activity (381) and are given no token. The on-premises change (466, 539)
  takes it to the credential fetch only (kd-59); the throttle (480), the runspace and the connect come after the fetch
  and before the send mark (493).
- Calendar Permissions: the same shape - activity 366, location read 431, cloud change 462 and 468 (reaching
  `Services/CalendarPermissionService.cs` 41 and 114, through `RunAsync`), on-premises change 407 and 480.

### 3.2 The design, as eight rules

**E1 - The borrow.** `RunWithRetryAsync` takes the caller's token and hands it to `BorrowAsync`. The slot wait (up to
two minutes, 294) and the connect gate (up to ninety seconds, 784) end at once on a cancel; the existing catch
(374-379) gives back the slot a cancelled gate wait held. A connect already past the gate (Import-Module,
Connect-ExchangeOnline and the two id snapshots) runs to its end: it cannot be interrupted, and the connection it makes
is healthy, so it is pooled for the next borrower (E2) rather than destroyed. The destroy of a stale connection keeps no
token (337-341, aud-a6d: cleanup the borrow owes).

**E2 - Before the delegate.** The retry core looks at the token before every attempt's borrow, so a retry never
borrows after a cancel, and again as the borrow returns: a connection held but not yet used goes straight back through
`Return` and the cancellation is thrown. Nothing has run on it, so it is exactly as clean as any pooled connection.

**E3 - Between a read's commands (opt-in, reads only).** A new base helper for pooled calls that write nothing - here
`RunPooledReadAsync` - takes a required token, gives it to E1 and E2, and puts it on the call's
`ConnectionErrorTracker`. The base's command helpers (`Invoke`, `InvokeBestEffort`, `InvokeOptional`,
`ReadUnlessNotFound`) look at the tracker's token before each `ps.Invoke()`; when it is cancelled they clear the
pipeline's commands and streams and throw the cancellation. So a read ends between commands, its connection idle and
clean, and goes back to the pool. A tracker made anywhere else carries no token and changes nothing. `RunAsync` and
`RunPooledQueryAsync` - which carry writes - never put the token on the tracker.

**E4 - Writes.** A write's token reaches E1 and E2 only, through an optional last parameter on `RunAsync` and
`RunPooledQueryAsync`, plus the module's own check immediately before its first write command (kd-73 and kd-75's
shape; for the permission services, immediately before `submission.Submitting()`). After that point nothing looks at
it. Every existing caller passes no token and is unchanged.

**E5 - The running command is never stopped.** No `PowerShell.Stop`, `BeginStop` or `StopAsync` is called on any
pipeline, pooled or on-premises, read or write (kd-6's rule; section 3.3). The command in flight runs to its end and
the cancel is seen at the next E2, E3 or E7 point. A source tripwire holds the rule.

**E6 - A stopped pipeline is never lent again.** `Return` destroys a connection whose
`PowerShell.InvocationStateInfo.State` reads `Stopped` or `Stopping`, exactly as it destroys one of an old generation
(384-390), releases its slot and traces `ExoConnectionDiscardedStopped`. The app stops none (E5), so this is the guard
on kd-58 point 4's hole: it decides the connection's fate from the pipeline's own state, not from what the delegate
made of the stop - `RunAsync` turns every throw into a failed result, and several read delegates catch everything.
Every route back to the pool goes through `Return`: the retry core and the recipient autocomplete's direct borrow
alike.

**E7 - On-premises.** The token reaches `ThrottledAsync`'s wait (up to two minutes; a new optional parameter), a check
before and after `runspace.Open()`, a check before each `ConnectOnPrem` attempt, the sleep between attempts (waited on
the token instead of `Thread.Sleep`), and the gaps between reads. For a write, one last check immediately before
`submission.Submitting()` (aud-a76's mark), so a cancel at any of these points leaves as "not sent". An attempt in
flight (`New-PSSession`, open timeout 10 s) runs to its end. The session is still removed in `finally`, with no token
(cleanup owed). Conference Rooms' preflights have no throttle; their steps - the directory runspace's open, the
ActiveDirectory module's import, the reads - are each checked before they start.

**E8 - Only the caller's token counts.** In the pool and the base, only a cancellation of the token the caller passed
is handled as a cancel (E2, E3, section 3.5); every other `OperationCanceledException` or `TaskCanceledException` - a
cmdlet's own timeout, say - is classified exactly as today. At the pages kd-57's rule stands: only the activity's own
token cancelled is the operator's cancel, and every adopting slice tests the control.

### 3.3 kd-6's rule - do not stop a running command - is kept

kd-6 chose not to stop the call for a write, and recorded that a stop could not be verified without a live tenant. This
plan keeps the rule and widens it to every command, read or write, cloud or on-premises:

1. **A write stopped after it was sent may have applied.** Its outcome would be unknown - Undetermined, by the aud-a73
   rule - where letting it finish gives the operator the real answer a few seconds later.
2. **For a read, a stop buys almost nothing.** E3 already ends a read at its next command, so a stop would shorten only
   the one command in flight, and the slot is held for that command either way.
3. **What a stop leaves behind cannot be known here.** Whether the Exchange Online module's cmdlets honour a stop
   promptly, and what an interrupted call leaves in a connection's session, cannot be verified without a live tenant -
   kd-6's own reason, still true. The pool is shared by every operator, so a connection in an unknown state is the
   worst outcome available to this plan.
4. **A stopped connection is not free to replace.** Each discard costs a reconnect, serialised behind the connect gate,
   which pings every live connection (`ExoConnectionPool.cs` 677-680).

Considered and not taken: **stopping only the caller's wait for a read**, as kd-6 does for its write. It would show the
cancel at once, but it would leave the read running headless after its page has finished with it - the orphaned-work
shape `ActivityProgress.cs` 268-271 names as the defect the progress system exists to remove - and it frees the slot
no sooner than E3 does. kd-70 and kd-73 already accepted the one-command wait.

### 3.4 A pipeline stopped mid-command is discarded - not returned, not recycled

This plan stops none (E5). Should one ever be found, by whatever means, it is **discarded** (E6):

- **Not returned**, because nothing can show it clean: the Exchange Online module keeps per-connection state the app
  cannot see, and the next operator to borrow the connection would inherit whatever the interrupted call left there.
- **Not recycled** (reset and probed before re-use), because a probe shows that the connection answers now, not that
  the stop left nothing behind, and it adds a round trip and failure modes of its own.
- **Discarded**, because the cost is one reconnect and correctness then depends on nothing unverifiable.

### 3.5 The retry core: a cancel is not a failure

In `RunWithRetryCoreAsync` (492-548), given the token:

- **Before each attempt:** `ThrowIfCancellationRequested` - no borrow, nothing to give back. This is also what stops a
  retry: a first attempt that came back with a retriable precheck (514-521, 532-541) never leads to a second borrow
  after a cancel.
- **After the borrow:** a cancelled token gives the unused connection back (E2) and throws.
- **A cancellation of the caller's token thrown by the delegate** - at an E3 point, or by a module's own check before
  its first write (E4) - is caught **ahead of** the connection-error filter (514): never read as a dead session, never
  discarded, never retried. The connection is given back under the caller's failure policy (`Return` for the base
  helpers; `Discard` for a caller that cannot vouch for its pipeline) and the cancellation is rethrown unchanged.
- **A stopped pipeline** (E6) is destroyed by `Return` and never retried; whatever the delegate returned or threw goes
  out as it is.
- **With no token** - every existing caller - only E6 can change anything, and only for a pipeline something stopped.

### 3.6 What the pool's lifetime and health accounting see

- **Slots.** The core's invariant - every borrow paired with exactly one return or discard (489-490) - holds on every
  new path: a cancel before the borrow takes no slot; a cancelled slot wait never held one; a cancelled gate wait or
  connect gives its slot back through `BorrowAsync`'s catch (374-379); a cancel after the borrow, or between a read's
  commands, gives the connection back; a stopped pipeline is destroyed by `Return`, which releases its slot.
- **The pooled set.** A cancel never destroys a healthy connection; it goes back for the next borrower, a freshly
  connected one included. E6's destroy is the only new one.
- **Generations, the shared-config drain (scdi-1) and `Dispose` (aud-a6c, aud-a6e, aud-a6f):** unchanged. A connection
  given back after a cancel meets the same generation test and the same stopped-pool refusal as any other.
- **Failed disconnects** (177): unchanged; E6's destroy counts a failed disconnect as every destroy does.
- **Traces.** `BorrowAsync`'s catch traces every throw `Failed` today (374-376); a cancellation of the caller's token is
  traced `Cancelled` instead, at the slot wait and at the connect. E6 has its own step. A cancel is never logged at
  Error.
- No new counter, timer or probe.

### 3.7 Which modules, in what order

The shared slices first (A1-A3), Recipient Lookup with them as their first user: it is the one module whose token
already reaches the base's location and size reads (kd-37), so it is where the shared changes take effect at once.
Then the modules whose pages already file the operator's cancel (kd-57, kd-59, kd-70, kd-73, kd-75), so each slice
extends the token's reach without new wording. Writes that can happen after a cancel come before reads that merely
finish late:

1. Mailbox Permissions and Calendar Permissions, the single Exchange Online change (A4, A5; section 14, item 1): the
   location read before it, its wait for a pool connection, and a check immediately before the send mark. As the code
   reads, a change waiting for a connection is made after the operator cancelled - kd-75's fault, in two more modules.
2. Mailbox Permissions and Calendar Permissions, the on-premises change (A6, A7): the throttle, the runspace and the
   connect, all before the send mark - kd-59's recorded gap, the same fault on-premises.
3. Conference Rooms, the single-room setup (A8): the room read and the room-list resolution before Set-Place, the
   preflights' directory steps after their credential fetches, and Set-Place's own wait for a connection (kd-75's
   check inside the call stays).
4. Conference Rooms, the Room Finder bulk job's rows (A9): the job's own token, under the single-room rules.
5. Message Trace (A10): reads - the cloud windows and details at the pool's queue and between commands, the
   on-premises search and detail at the throttle and the connect.

Not adopted, and unchanged - each passes no token, so a cancel there behaves exactly as today: Blocked Senders (kd-6 and
kd-76's handshake already settles its one write, and a token on its borrow would change which sentence the operator is
shown), Migration (closed by owner order, `.agents/state.md`), Delegation Report, Out of Office, the Exchange identity
resolver, `PermissionValidator`, the recipient autocomplete, and the Room Type bulk rows (section 14, item 5).

### 3.8 What the operator can cancel, and what they see

- **How they cancel: unchanged** - the guard's OK, or closing the tab. Nothing is added or withdrawn.
- **What the cancel now reaches**, in the adopted modules: the wait for a free Exchange Online connection (up to two
  minutes) and the queue to open one (up to ninety seconds); the gaps between a read's cloud commands; the
  on-premises server's queue (up to two minutes), the opening of its session and each connect attempt; a Room Finder
  bulk row that has not reached its first write.
- **What they see: the existing words, sooner.** "Cancelled by the operator" (Recipient Lookup, Message Trace);
  "Cancelled by the operator. Nothing was written." (Conference Rooms); "Cancelled by the operator before the change
  was sent. Nothing was written." (both permission pages). A Room Finder bulk job still ends "Cancelled by operator."
  (the job store's words, unchanged); the row it stopped is not recorded as a row outcome - as a row the cancel
  stopped before it started is not - and has its own audit row (3.9).
- **What does not change:** a command already sent runs to its end and the cancel lands when it returns; anything
  already changed stays changed, as the guard's message says.

### 3.9 Audit rows

No new action, category or field. Each module files a cancel through its existing row and kd-57's `FiledCancelled`, in
the words of 3.8: Recipient Lookup's lookup row; Message Trace's search, detail and download rows; Conference Rooms'
setup row, with the administrators told as for a row that failed (kd-59, unchanged); the permission pages' change rows,
with no notification for a change never sent (kd-59, unchanged). New: the Room Finder bulk row the job's cancel stopped
is filed `ConferenceRooms_SetMetadata_Bulk`, result Cancelled, "Cancelled by the operator. Nothing was written.", with
the job's captured actor, address and ticket - but only when the cancel was the job's own (the operator's cancel
request). A row stopped by the host shutting down is filed Failed, "The service stopped before this row was written.
Nothing was written." (kd-57's rule; the job-level wording for a shutdown is kd-57's note 4, unchanged). A cancel that
lands after a first write leaves the operation's ordinary rows, as today.

## 4. Item 2 - a settings save the page has stopped waiting for (aud-a67 (12))

### 4.1 What is true today

In `Components/Pages/ModuleConfig.razor` a route change to another module waits for the saves of the load it replaces
(`WaitForThisLoadsSavesAsync`, 976-997) for at most `saveWaitBound` - the store's five-second busy wait plus five
seconds (938) - then loads the next module and shows `UnfinishedSaveNotice` (1018-1024): "That save can change only X,
never Y: if it had not written yet it refuses, and when it ends its outcome is recorded in the audit log." A save still
before its write does refuse: its `Shows(moduleId)` check (aud-a66) files PageMoved. A save already inside its store
call (the `Task.Run` at 1484, 1623, 1780, 1967 and 2422) runs on, holding a worker thread and a connection, and commits
whenever the store answers - after the page has said it would not. aud-a67 (12) records the held resource; the commit
after the notice is the part a token can change.

### 4.2 Where the token comes from

**A token per save, owned by the page, cancelled at one moment only: when the route change stops waiting for that
save.** `savesInFlight` (931) gains a `CancellationTokenSource` per entry, made in `StartSaveAsync` (898) and handed to
the save; `WaitForThisLoadsSavesAsync` cancels the source of every save it gives up on before it returns their names.
The sources are not disposed (they own no timer), so a cancel racing a save's end is harmless.

This is not the operator's token and is never filed as one. The settings saves run under no activity -
`ExchangeAdminWeb.Tests/ProgressRegistry.cs` 159-162 exempts them on purpose ("registers as exempt so a later agent
does not 'fix' them") - so the guard never prompts for them and the operator has no cancel for a settings save. That
stays. The page's giving up is filed as the page's existing refusal for the same situation (4.6), in its own words:
kd-57's rule.

Alternatives not taken:
- **An activity per save, so the guard prompts and its OK cancels.** It would reverse a recorded exemption and add a
  prompt to the settings pages that nobody has asked for.
- **Cancelling when the page is disposed** (another page opened, the tab closed). Those saves complete today as the
  operator asked, and nothing on screen promises otherwise.

One consequence, chosen: once the page has given up on a save, the save refuses even if the page later shows its module
again (A, then B, then back to A). The notice already promised the refusal; aud-a66's `Shows` rule, by module id, still
governs every save the page has not given up on.

### 4.3 The token's path

Page, service, repository, `IConfigStore.Write(..., token)`, `SqliteConfigStore` - for the five write methods the
Module Configuration saves reach, and no others:

| Save (tab) | Service method | Repository method |
|---|---|---|
| Module status | `ModuleEnablementService.SetModulesEnabled` | `ModuleEnablementRepository.SetModules` |
| Section access | `SectionAccessService.SaveEditedAliases` | `SectionAccessRepository.ReplaceAliases` |
| Configuration | `ModuleConfigService.SaveChangedValues` | `ModuleConfigRepository.UpdateModuleIf` |
| Module admins | `ModuleAdminService.ApplyAdminChanges` | `ModuleAdminRepository.ApplyChanges` |
| Attribute allowlist | `ADAttributeEditorService.SaveAllowlistAndAudit` | `AttributeEditorRepository.ReplaceAllowlist` |

Each takes an optional last `CancellationToken` and passes it on; every other caller passes none and is unchanged.
Nothing after a commit takes it: not the access save's second write (`SaveGroupDisplayNames`, 1812, after the aliases
commit), not the success audit, the allowlist's cache invalidation or the re-baselines.
`ModuleConfigService.CompareChangedValues`, the rolled-back compare, is not touched.

`IConfigStore` (43, 46) gains two token-taking `Write` overloads as default interface members - the local precedent is
`IBulkJobProcessor.OnJobCompletedAsync` - whose default looks at the token once and then calls the token-less `Write`.
So the thirty-odd test stand-ins that implement the interface compile unchanged. `SqliteConfigStore`, the one
production implementation, implements both itself (4.4), and a tripwire pins that it does.

### 4.4 What a cancelled write means for the store

`SqliteConfigStore.Write` (29-45), given a token, looks at it at four points and nowhere else:

| Point | Where | A cancel there |
|---|---|---|
| C1 | before `_factory.Open()` (32) | nothing opened |
| C2 | after the open, before `BEGIN IMMEDIATE` (39) | nothing begun; no busy wait |
| C3 | as `BEGIN IMMEDIATE` returns, before the callback (41) | rolled back; the callback never runs |
| C4 | after the callback and the change-token bump (42), before `Commit()` (43) | rolled back |

A throw at C3 or C4 leaves the `using` with the transaction uncommitted, and `SqliteTransaction.Dispose` "Releases any
resources used by the transaction and rolls it back" (Microsoft.Data.Sqlite 10.0.7's own documentation, read from the
package's XML file in the local NuGet cache). The change-token bump is inside the same transaction, so the other
instance on the shared database sees nothing at all: no row, no token movement, no cache reload. **Nothing is
half-written: the transaction is the unit, and it commits whole or not at all.**

**After `Commit()` nothing looks at the token.** A cancel that arrives during or after the commit is not honoured: the
call returns its result and the save is reported as the success it is. The store alone decides, at C4, so the page
never guesses from the token's state whether a write landed (risk 3).

What no token reaches, by design: the busy wait inside `BEGIN IMMEDIATE` (up to five seconds) and any call blocked
inside the database file. `SqliteCommand.Cancel()` - which aud-a67's record names as the way to cancel a running
statement - "Attempts to cancel the execution of the command. Does nothing." in this provider (the same documentation),
and no native interrupt is used either (section 13). A cancel during the busy wait is seen at C3 when the wait ends; if
the lock is never freed, `BEGIN` fails busy, and busy is what the save reports (kd-59's rule: a failure of its own while
the operator happens to cancel is that failure). A save waiting on its service's in-process write lock
(`ModuleConfigService.cs` 184, `ModuleEnablementService.cs` 271, `SectionAccessService.cs` 282, `ModuleAdminService.cs`
164) is not interrupted there; it meets C1 as soon as it holds the lock.

### 4.5 What the page shows

No new words:
- When the page gives up: `UnfinishedSaveNotice`, unchanged - and now exact, since "if it had not written yet it
  refuses" becomes true inside the store as well.
- When a given-up save ends without committing: "<Module>: Not saved: the page moved to another module while this save
  waited, so nothing was written. Open the module again and save." - `OnAnotherModule` (963-964) over `PageMovedRefusal`
  (958-959), the message aud-a66 already shows for a save refused before its write. Each save's cancel catch sits ahead
  of its general catch and is taken only when the save's own page token is cancelled.
- When it had committed first: its ordinary success message, named for its module, as today.
- When the store was busy: the existing busy refusal (`SettingsSaveAudit.StoreBusyMessage`).

### 4.6 What is audited

One record per attempt, as today. A given-up save that did not commit files
`Audit.LogSettingsSaveRefused(Logger, currentUser, ip, section, "PageMoved", PageMovedRefusal)` - action SettingsChange,
result Failed, `refusedAt` PageMoved, `mutationAttempted` false, section `ModuleConfig:<module>` - the record aud-a66
files for the same refusal before the write; `mutationAttempted` false is true by construction, the transaction having
rolled back. The allowlist's records are its service's (aud-a28l): `SaveAllowlistAndAudit` takes the token and, for a
cancellation of it, writes the allowlist's own PageMoved refusal and returns it; the page writes nothing for it. A save
that committed files its ordinary success record. No new action, field or value - the audit fields are an interface
(`.agents/decisions.md` 2026-09-10). Settings saves send no notification, before or after this plan.

### 4.7 What this does not fix

The held resource aud-a67 (12) names. A save stuck in a hung store keeps its worker thread and its connection until
the store answers: no token can reach a call blocked inside the database file. The bound aud-a67 records still holds -
one save per load from the button, one more per route change past the bound, each ending when its store answers.

## 5. Invariants kept

- **A cancel never leaves a half-written target.** Exchange: a write's token is looked at only before its first command
  (E4), and no command is stopped (E5). Store: the transaction rolls back whole (4.4).
- **Nothing after a first write is cancellable:** Conference Rooms after Set-Place; the permission services after
  `Submitting()`; the access save's display names; every post-commit step - audit, cache invalidation, re-baseline,
  the Exchange Online pool drain.
- **Only the operator's own token counts:** in the pool and the base only the caller's token is a cancel (E8); at the
  pages only the activity's token (kd-57); the settings page's own token is never filed as the operator's cancel (4.2).
- Every pool borrow pairs with exactly one return or discard (3.6).
- No running command is stopped, anywhere (E5).
- Authorization is still re-checked immediately before every write and the protected-principal gate still runs, both
  unchanged in place and effect; a cancel check sits beside them and never replaces either.
- A cancellation is never read as an unreadable or corrupt store: it sets none of the page's corrupt flags and reaches
  no fallback.

## 6. Constitution rules touched

- **Planning Rules:** "Exchange service or shared infrastructure refactors" and "persistence/storage changes" - why
  this is a plan.
- **Auditing And Tracing:** every attempt keeps exactly one record (3.9, 4.6); a cancel that came after a completed
  operation never makes it look failed; operation traces carry the cancel steps (3.6).
- **Configuration:** "Config writes must be transactional (SQLite)" - kept and relied on (4.4); fail-closed stores
  unchanged.
- **Authorization** and **Protected Principals:** unchanged in order and effect (section 5); the protection gate's own
  reads take no token.
- **External Integrations:** "Exchange Online auth is owned by the Exchange Online parent module and the shared EXO
  pool" - the pool stays the only owner of connections; no module stops, disposes or keeps one.
- **Notifications:** unchanged per module (3.9); settings saves send none.
- **Deployment And Versioning:** section 7.
- **Code Change Discipline:** local patterns only - kd-57's filing helpers, kd-59's not-sent test, kd-75's in-call
  check, `IBulkJobProcessor`'s default interface member.
- **Never Do:** none touched.

## 7. Versioning

- **Base app version** (`ExchangeAdminWeb.csproj`: `VersionPrefix`, `AssemblyVersion`, `FileVersion`): one bump, owed
  from A1 and made once, in C1. Shared code changes in A1 (the pool), A2 and A3 (the Exchange base), B1 (the store) and
  B2 (four shared settings services, their repositories, and the Module Configuration page, which is app-wide -
  aud-a67 (1)).
- **Module versions** (`Modules/ModuleCatalog.cs`, which owns the numbers): a patch bump in each slice that changes a
  module's behaviour - Recipient Lookup (A2, A3), Mailbox Permissions (A4, A6), Calendar Permissions (A5, A7),
  Conference Rooms (A8, A9), Message Trace (A10), AD Attribute Editor (B3). A change touching both layers gets both
  bumps.

## 8. Slices

One finding per commit; each slice is one commit with its record section, its tests and its mutation proof. Order:
A1 to A10 as numbered; Part B is independent of Part A and may run before, after or between its slices, one compile
at a time (`.agents/decisions.md` 2026-10-26). C1 is last.

| Slice | Finding | What | Production files |
|---|---|---|---|
| A1 | kd-58 | The pool: E1, E2, E6; the core's cancel handling (3.5); traces (3.6); the no-stop tripwire | `Services/ExoConnectionPool.cs` |
| A2 | kd-58 | The base, cloud: E3, E4, `RunPooledReadAsync`, the read-only tripwire; tokens on `HasCloudMailboxAsync` and both `GetMailboxLocationAsync` forms; Recipient Lookup's cloud reads | `Services/ExchangeServiceBase.cs`, `Services/RecipientLookupService.cs` |
| A3 | kd-58 | The base, on-premises: E7 (`ThrottledAsync`, the runspace open, `ConnectOnPrem`); the base's two on-premises reads, which Recipient Lookup uses | `Services/ExchangeServiceBase.cs` |
| A4 | new (14.1) | Mailbox Permissions, the single cloud change: location read, pool wait, a check before `Submitting()`, the page's cancel catch | `Services/MailboxPermissionService.cs`, `Components/Pages/MailboxPermissions.razor` |
| A5 | new (14.1) | Calendar Permissions, the same | `Services/CalendarPermissionService.cs`, `Components/Pages/CalendarPermissions.razor` |
| A6 | kd-58 | Mailbox Permissions, the on-premises change: E7 before `Submitting()` | `Services/MailboxPermissionService.cs` |
| A7 | kd-58 | Calendar Permissions, the same | `Services/CalendarPermissionService.cs` |
| A8 | kd-58 | Conference Rooms, single-room setup: the reads before Set-Place, the preflights' directory steps, Set-Place's pool wait | `Services/ConferenceRoomService.cs` |
| A9 | kd-58 | Conference Rooms, Room Finder bulk rows: the job's token; the stopped row's record | `Services/ConferenceRoomService.cs`, `Services/Jobs/IConferenceRoomBulkOperations.cs`, `Services/Jobs/ConferenceRoomBulkProcessor.cs` |
| A10 | kd-58 | Message Trace: cloud window and detail reads, their catches; the on-premises search and detail (E7) | `Services/MessageTraceService.cs` |
| B1 | aud-a67 (12) | The store: the two `IConfigStore` overloads; `SqliteConfigStore`'s C1-C4 | `Services/Storage/IConfigStore.cs`, `Services/Storage/SqliteConfigStore.cs` |
| B2 | aud-a67 (12) | Module Configuration's status, access, configuration and admins saves: per-save token, the give-up cancel, four services and four repositories, the cancel catches | `Components/Pages/ModuleConfig.razor`, the services and repositories in 4.3 |
| B3 | aud-a67 (12) | The attribute allowlist save: its service files its own PageMoved refusal | `Services/ADAttributeEditorService.cs`, `Services/Storage/AttributeEditorRepository.cs`, `Components/Pages/ModuleConfig.razor` |
| C1 | - | The base bump; the records (`kd-58.md`, `aud-a67.md`, the new finding's record, `.agents/review/index.md`, `.agents/state.md`, `.agents/token-log.md`, this plan's Status); the full gate on the final tree | `ExchangeAdminWeb.csproj` |

Per-slice notes:

- **A1.** `RunWithRetryAsync` gains `CancellationToken cancellationToken = default`; `RunWithRetryCoreAsync` gains the
  token (its direct tests in `ExoConnectionPoolRetryTests.cs` pass none and keep their meaning). `BorrowAsync` gains a
  catch for a cancellation of its own token ahead of its general one, tracing `Cancelled` and releasing the slot.
  `Return` (382) tests the pipeline's state beside its generation test; both destroy.
- **A2.** The read helper requires a token and takes `allowRetry` as `RunPooledQueryAsync` does; only it sets the
  tracker's token. The tripwire: every `RunPooledReadAsync(` call site's delegate adds only `Get-` commands, calls no
  `AddScript` and runs no raw `ps.Invoke()`. Recipient Lookup: `RecipientLookupService.cs` 16 moves to the read helper
  and its outer catch (144) lets a cancellation pass (`when (!IsCancellation(ex))`); `GetMailboxLocationAsync(string,
  CancellationToken)` hands its token to `HasCloudMailboxAsync` (735). It is the one existing caller of that form that
  passes a token, hence Recipient Lookup's bump here.
- **A3.** `ConnectOnPrem`'s signature gains the token, and its ten test overrides in six files
  (`CalendarPermissionWriteIdentityTests`, `MailboxPermissionWriteIdentityTests`, `MigrationStubExchange`,
  `PermissionPagesCancellationTests`, `PermissionPagesSelfGrantTests`, `PermissionWriteSubmissionTests`) take it -
  mechanical, no answer changes. `ThrottledAsync`'s other six callers pass nothing until their slices. The base's two
  reads' catches already let a cancellation pass (`when (!IsCancellation(ex))`).
- **A4, A5.** The page passes its activity's token to the location read and the change (`MailboxPermissions.razor` 461,
  488-489; `CalendarPermissions.razor` 431, 462, 468). The location read's catch (`catch (Exception locationEx)`, on the
  same line) lets the operator's cancel through to a cancel catch, and a catch on
  `PermissionChangeFailure.CancelledBeforeItWasSent(ex, activity.CancellationToken)` ahead of the existing one files
  Cancelled with kd-59's words and no notification. Mailbox's change is `RunPooledQueryAsync`, so its in-call check
  before `Submitting()` throws and 3.5 gives the connection back; Calendar's is `RunAsync`, which makes every throw a
  failed result, so its in-call check marks and the method throws once `RunAsync` has returned - kd-75's shape. Both
  pages are line-keyed in `ClickGateRegistry`: re-anchor with `.agents/playbooks/clickgate-reanchor.md` if the line
  count moves. Reproduce the fault first (section 9).
- **A6, A7.** `MailboxPermissionService.cs` 466 and 539, `CalendarPermissionService.cs` 407 and 480 pass their existing
  token to `ThrottledAsync`, `ConnectOnPrem` and the E7 checks; kd-59's page catch already files the result.
- **A8.** Every catch on the way out lets the operator's cancel pass: the room read's (`ConferenceRoomService.cs`
  562-566 logs a warning and rethrows - no warning for a cancel) and the room-list resolution's (608-616 turns any throw
  into a refusal - filter it). Set-Place's `RunAsync` takes the token for its borrow; kd-75's in-call check stays.
- **A9.** `IConferenceRoomBulkOperations.SetRoomMetadataAndListAsync` gains the token, the explicit implementation
  (778-783) passes it, and the processor passes `ProcessRowAsync`'s token (68) through `ApplyFinderRowAsync` (217).
  `onAllowed` (143) catches a cancellation of that token ahead of its catch-all (177), completes the trace operation not
  succeeded, files the row (3.9) and rethrows, so `BulkJobService.ExecuteRowsAsync` stops the batch without recording the
  row (532-537). Telling the operator's cancel from the host stopping needs a seam the processor lacks (section 13). The
  two tests that double the interface (`ConferenceRoomBulkProcessorTests`, `ConferenceRoomBulkTicketTests`) take the
  parameter.
- **A10.** `GetCloudMessageTraceAsync` (451) and `GetCloudMessageDetailAsync` (164) move to the read helper with the
  search's or the detail's token; their outer catches (525, 188) let a cancellation pass, which
  `RunMessageTraceBackendAsync` (kd-59) and `RunDetailBackendAsync` already classify; the on-premises search (551) and
  detail (209) get E7. The detail job keeps reading a cancelled detail as a Failed row (kd-59, unchanged in kind).
- **B1.** As 4.3 and 4.4, with an internal after-commit test seam on `SqliteConfigStore`.
- **B2.** As 4.2 to 4.6. Each `Save*` takes the page token. `SettingsSaveAuditTests`' catch tripwire (aud-a67 (8)) learns
  the new catch: it must record through `Audit.LogSettingsSaveRefused(` with PageMoved. `StartSaveAsync` stays free of
  any catch (the same tripwire).
- **B3.** As 4.6.

## 9. Tests

Everything runs against stand-ins: no tenant, vault, directory or live database. Each new guard is proven non-vacuous by
mutation - revert the guard, watch the named test fail, restore byte-exact with a fresh mtime, confirm the mutation
applied (the method traps in `.agents/state.md`). Every slice that gives a method an optional token or a token overload
fixes the xUnit1051 warnings it raises in existing tests by passing `TestContext.Current.CancellationToken` (kd-6,
kd-37), because `dotnet format --verify-no-changes` fails on them.

**A1 - the pool, and how a stopped pipeline is proven not to poison it.** A new `ExoConnectionPoolCancellationTests`,
over the real pool with the connect step's seams (psl-9) or `MigrationStubExchange.Pool`'s stub cmdlets. Slot
accounting is checked after every case by borrowing all five slots:
- (1) All five held, a sixth borrow with a token cancelled: it throws a cancellation well inside the two-minute wait,
  asks for no connect, and all five come back. (2) A connect held at the gate by a stub Connect-ExchangeOnline: a
  second borrow cancelled at the gate throws and its slot comes back. (3) Cancelled during the connect proper: the
  connect finishes, its connection is pooled (`AvailableCount` + 1), and the caller gets the cancellation, never the
  connection. (4) Cancelled as the borrow returns: the delegate never runs and the connection is pooled. (5) A first
  attempt with a retriable precheck and a cancel before the second: no second borrow, one discard. (6) A borrow
  cancelled while the pool stops: whichever wins, the slot comes back once and nothing is pooled into the stopped pool.
- **The stopped pipeline.** A stub read blocks on a gate; the test calls `BeginStop` on the borrowed pipeline from
  outside - the only way to make one, since nothing in the app stops a pipeline - and then releases the gate, so the
  pipeline stops at its next statement. Three variants: the delegate rethrows the stop; it swallows it into a result
  (Recipient Lookup's catch-all shape); it runs through `RunAsync`, which makes it a failed result. In each: the
  connection is destroyed, not pooled (`AvailableCount` unchanged, `ExoConnectionDiscardedStopped` traced, the stub's
  disconnect seen once for its id); five borrows succeed afterwards; and **the next call connects a fresh runspace
  (connect count + 1) and returns its own results only**. Mutation: E6's check removed - the next call reuses the
  stopped runspace (connect count unchanged) and the test fails.
- Controls: an `OperationCanceledException` that is not the caller's token is classified as today (given back or
  discarded by its words); with no token, every existing pool test passes unchanged.
- Tripwire: no `.Stop(`, `.BeginStop(` or `.StopAsync(` on a `PowerShell` in `Services/` or `Components/`.
- Mutations: the token not handed to the borrow; the pre-borrow check removed (the retry borrows after a cancel); the
  post-borrow give-back removed (a slot leaks and the five-borrow check fails); the cancel branch moved below the
  connection-error filter (a cancellation whose message names a session is discarded as dead).

**A2 - a cancelled read leaves its connection fit for the next operator.** A stub read of two commands through
`RunPooledReadAsync`, cancelled after the first: the second never runs (stub log); the same runspace (connect count
unchanged) serves the next read, which finds an empty command list and clear streams at borrow and returns only its
own results. Cancelled before the first command: nothing invoked. A write through `RunAsync` or `RunPooledQueryAsync`
with a token: a cancel once the delegate has started is never seen - the stub write completes and its own result comes
back (the proof that nothing after a first write is cancellable). Recipient Lookup (extending
`RecipientLookupCancellationTests`): a cancel during the cloud read's pool wait (five connections held) and between its
commands, and during the location read's cloud half, each ends the lookup Cancelled in kd-57's words; kd-57's control
stays green. The read-only tripwire. Mutations: the helpers' check removed; the tracker token set by
`RunPooledQueryAsync` too (the write test fails); the token not passed at 735; the catch filter at 144 removed.

**A3.** Both throttle slots held by stub on-premises calls: a third, cancelled there, ends at once. A stub
`New-PSSession` failing its first attempt: a cancel during the sleep ends before the second. Cancelled after the
runspace opens: no connect. Recipient Lookup's size and location reads end Cancelled at each point. Mutations, one per
point.

**A4, A5 - reproduce, then fix.** Before the fix: all five connections held (the shape of `ConferenceRoomCancellationTests`'
`HoldEveryPoolConnection`), the page's handler started, its activity cancelled, one connection released - the change is
made: the fault. After: no command, nothing written, banner and row "Cancelled by the operator before the change was
sent. Nothing was written.", result Cancelled, no notification, the busy flag down. Also cancelled during the location
read, and just before the mark. Controls: a cancellation the operator did not ask for is the change's failure in its
own words; a cancel after the mark lets the change finish with its own result. Mutations: the token dropped at the page;
the in-call check removed; the page's cancel catch removed.

**A6, A7.** As A4's, at the throttle, the runspace open and the connect, all before the mark; kd-59's
`PermissionPagesCancellationTests` stay green.

**A8.** Cancelled during the room read, the room-list resolution, a preflight's directory step and Set-Place's pool wait
(kd-75's two tests stay): each ends before Set-Place, filed Cancelled, "Nothing was written."; kd-59's
`ACancelAfterSetPlace_LetsTheRowFinish_...` stays green. Mutations: each new check, and each catch filter.

**A9.** The job cancelled during a row's preflight: no Set-Place, the row's audit Cancelled with the job's actor and
ticket, the row not recorded, the job ends Cancelled, the next row never starts. Cancelled after Set-Place: the row
finishes and is recorded as today. The host stopping mid-preflight: Failed in its own words. Mutations: the token not
passed through the explicit implementation; the processor's cancel catch removed (the row is recorded Failed and the
batch runs on to the next row's check).

**A10.** kd-59's and kd-73's shapes pushed into the cloud reads: a cancel during a window's pool wait or between its
commands ends the search Cancelled; the detail read is marked Cancelled; the live download, window and detail tests stay
green; kd-57's controls. Mutations: the token not passed to each read; each catch filter removed.

**B1 - a cancel racing a commit.** A new `SqliteConfigStoreCancellationTests` on a real store over a temp database
(`TestConfigStore.Create`), each case read back through a second connection: (1) already cancelled - nothing written,
the change token unmoved; (2) cancelled during the busy wait while another connection holds `BEGIN IMMEDIATE`, the lock
then freed - a cancellation, the callback never ran, nothing written; (3) cancelled from inside the callback after its
inserts - a cancellation at C4, no row, the token unmoved; (4) cancelled at the after-commit seam - the call returns
its result and the rows are there; (5) cancelled during the busy wait with the lock never freed - busy
(`ConfigStoreBusy.IsBusy`), not a cancellation, nothing written; (6) a stand-in that does not implement the overloads
still refuses a token already cancelled, and `SqliteConfigStore` declares both overloads itself (reflection).
Mutations: C4 removed - case 3 commits; C3 removed - case 2's callback runs; a check added after `Commit()` - case 4
throws for a committed write.

**B2.** In `SettingsModuleMoveTests`, through `SettingsPageHarness`, whose `AttributeEditorHarness.SwitchableStore`
implements the token overloads, forwarding the token and offering a hook inside the transaction (it wraps the
callback), so a test can hold a write after `BEGIN`. Per save (status, access, configuration, admins): the write held
past `saveWaitBound`; the route change opens the next module with `UnfinishedSaveNotice`; the write released - nothing
stored, one record (`refusedAt` PageMoved, `mutationAttempted` false), the page showing "<Module>: Not saved: ..."; the
access save stores no display names. Controls: released inside the bound - saved as today; committed before the
give-up but held after the commit past the bound - reported saved, with its success record; busy as the page gives up -
the busy refusal. Mutations: the give-up cancel removed (the late save writes); the token not passed to the service;
the page's cancel catch removed ("Failed to save: The operation was canceled." filed as a failure). aud-a66's E1 and E2
and aud-a67's D1 to D3 mutants are re-run and still fail their own cases.

**B3.** As B2 for the allowlist save: one record, written by the service. Mutation: the service's cancel branch removed
gives a Failed record.

## 10. Verification

Per slice, on the slice's tree, one compile at a time: `dotnet build ExchangeAdminWeb.slnx -c Release` (0 errors, no
warning beyond the baseline), `dotnet test ExchangeAdminWeb.slnx` (the opt-in live-directory cases stay out),
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`, `git diff --check HEAD`, and
`tools/Test-AsciiOnly.ps1` after `git add -A`. No PowerShell file changes, so PSScriptAnalyzer and Pester are not
owed. Not covered by any gate: a live tenant, vault, directory or shared database, and browser rendering
(`.agents/decisions.md` 2026-10-01, "Rendered is not visible"); manual acceptance is production use (2026-10-12).
Review: one independent review of A1 to A3 before A4 starts is recommended; the arrangement and its budget are the
owner's (`.agents/decisions.md` 2026-10-26).

## 11. Cost

- **Fourteen commits** (A1 to A10, B1 to B3, C1). Under the token budget's one-slice-one-session rule, fourteen
  sessions; the twin slices (A4 and A5, A6 and A7) could each share a session at the supervisor's discretion without
  merging their commits.
- **About 25 production files:** the pool and the Exchange base; six module services and two pages; the bulk interface
  and processor; the store's interface and implementation; four settings services and five repositories;
  `ADAttributeEditorService`; the Module Configuration page; `ModuleCatalog.cs`; the csproj.
- **About 25 test files**, new and extended, plus mechanical edits: the ten `ConnectOnPrem` overrides in six files (A3),
  the two tests that double the bulk interface (A9), and an uncounted number of xUnit1051 fixes (kd-37's seam alone
  raised twenty).
- Smallest: A6, A7 (one service each). Largest: A1 (its tests) and B2 (one page and eight store classes).

## 12. Risks, biggest first

1. **A connection handed to the next operator in a bad state.** The pool is shared, so damage would surface as someone
   else's failure, possibly much later. Mitigation: no command is ever stopped (E5, tripwire); every cancel point is
   between commands, where the connection is as any normal call leaves it, and the next call is proven to reuse it
   correctly (A2); a pipeline found stopped anyway is destroyed by `Return` whatever its delegate said (E6), proven by
   the stopped-pipeline test and its mutant (A1).
2. **A change made after the operator cancelled, or left half-made.** This is the live fault in Mailbox and Calendar
   Permissions (14.1) and the reason for the order in 3.7. Mitigation: a write's token reaches only the queue and the
   module's last check before its first command (E4); the read helper, the only path that checks between commands, is
   held by a tripwire to delegates that run `Get-` commands only.
3. **A cancel racing a commit in the store.** If the token were read after the commit, a committed save could be
   reported as not saved. Mitigation: the store alone decides, at C4; after `Commit()` nothing reads the token, so the
   answer matches the database (B1 cases 3 and 4 and their mutants).
4. **A cancellation swallowed or misfiled on its way out.** Several catches take everything
   (`RecipientLookupService.cs` 144, `MessageTraceService.cs` 188 and 525, `ConferenceRoomService.cs` 608, the bulk
   processor's 177), so a cancellation could come out as an error, a refusal or a Failed row. Mitigation: every adopting
   slice reads every catch on its path and tests both the cancel and kd-57's control.
5. **New code on paths that have never run in production** (kd-58 point 3: the cancelled-borrow paths). Mitigation:
   opt-in - only the slices' callers pass a token, the first adopters already file cancels, and A1 drives every path
   with real slot accounting.
6. **A test stand-in silently dropping the store's token.** A decorator over `IConfigStore` that does not implement
   the new overloads gets the default, which looks once and then writes without the token. Production has one
   implementation, pinned by B1's reflection check; B2's harness decorator forwards the token, and its tests fail if
   it does not. The five NSubstitute doubles of `IConfigStore` (in four test files) sit on paths that never call the
   overloads.
7. **A settings save refused that would have landed.** A save the page has given up on now refuses instead of writing
   later. That is what the page's notice already promises, the operator is told and can save again, and the exec
   summary names it - but it is a change in behaviour.
8. **Extra latency from the plumbing.** Negligible by construction: a token read before and after each borrow, one per
   command for adopted reads, four per settings write; no new await or allocation on the normal path, and a
   cancellable wait registers a callback only when it actually has to wait. The existing timing tests
   (`SqliteBusyWaitTests`, the pool's) are the check and must pass unchanged.

## 13. Assumptions - not established by reading

1. That Mailbox and Calendar Permissions' single cloud change, and their on-premises change after the credential
   returns, can be made after the operator's cancel. Read from the code - the activity's token is not passed, and the
   borrow and the throttle take none; kd-59's record says the same of the on-premises half - not reproduced. A4 and A5
   reproduce it before fixing it.
2. That PowerShell reports `Stopped` (or `Stopping`) in `InvocationStateInfo` after a stop, and `Completed` or `Failed`
   after every normal end, on this host's SDK: documented behaviour, to be confirmed by A1's tests. No pooled call uses
   a local `Select-Object -First` (none was found), which ends a pipeline early without stopping it.
3. That a stub pipeline blocked inside a .NET call stops at its next statement once `BeginStop` has been called and the
   call returns - the A1 test technique.
4. How the Exchange Online module's cmdlets behave under a stop: unknown, and deliberately not relied on.
5. That a command in flight normally finishes in seconds; one that hangs holds its slot as it does today.
6. That neither SQLite's busy wait nor a call blocked in the file system can be cut short by any cancellation
   (SQLite's native interrupt is believed not to shorten the busy handler's wait). Not relied on: no interrupt is used.
7. That xUnit1051 flags every test call to a method that gained an optional token or a token overload; how many such
   calls exist was not counted.
8. That the Room Finder bulk processor needs a narrow way to ask whether a job's cancel was the operator's (the
   persisted cancel flag) - it has none today. The rule is fixed here; the mechanism is the implementer's.
9. That a late save's message appears on the next module's page when its handler completes - aud-a66's existing
   mechanism, not seen in a browser.
10. That the `ProgressRegistry` exemption for settings saves (159-162) is a settled engineering choice: it is recorded
    there and points at `docs/SqliteConfigStore-Plan.md`; it was not traced to an owner ruling.
11. File and session counts are estimates.

## 14. Found while planning

1. **In scope, to be admitted as its own row before A4 (MEDIUM, kd-75's class).** Mailbox Permissions' and Calendar
   Permissions' single Exchange Online change can be made after the operator's cancel. The handler runs under an
   activity (`MailboxPermissions.razor` 381, `CalendarPermissions.razor` 366) whose token is passed neither to the
   change (488-489; 462, 468) nor to the location read before it (461; 431), and the change's wait for a pool
   connection takes none; after the guard's OK the change is made once a connection frees, and is filed and notified as
   made. Slices A4 and A5.
2. Not in scope: Exchange Online Config's connection save runs under an activity (`ExchangeOnlineConfig.razor` 430)
   whose token neither its store write (529) nor its pool drain (596) takes, so the guard says leaving will cancel it
   and it does not (kd-70's class). After B1 the write could take the token - only the write: the drain follows a
   committed save. One slice.
3. Not in scope: Module Configuration's group confirmation (`ModuleConfig.razor` 2033) and Admin Settings' two directory
   checks (`AdminSettings.razor` 1002, 1084) run under activities whose loops take no token, so a cancel waits for every
   lookup, up to 30 s each (kd-70's class).
4. Not in scope: the on-page CSV runs of Mailbox Permissions and Calendar Permissions (`MailboxPermissions.razor` 571,
   `CalendarPermissions.razor` 539) do not look at their activity's token between rows. Stopping between rows is safe -
   each row is a whole change - but the run's totals must then say so (Known Failure Class 2): its own finding.
5. Not in scope: the Room Type bulk rows (`SetRoomTypeAsync`) pass no token, as Room Finder's do until A9.
6. Record correction for `aud-a67.md`, item (12): it says cancelling a synchronous SQLite statement "needs
   `SqliteCommand.Cancel()` from another thread". In Microsoft.Data.Sqlite 10.0.7 that method "Does nothing" (its own
   documentation). The record's conclusion stands; the mechanism it names does not exist.

## 15. Status and pending steps

Draft. Nothing is implemented. Pending: the owner's go on the exec summary. Before A4, the supervisor admits section
14's item 1 as a row. On approval the supervisor sets the arrangement (`.agents/repo-guidance.md`, Token Budget) and
logs each slice in `.agents/token-log.md`. The plan closes at C1, with every slice's record, the base bump and the full
gate on the final tree.
