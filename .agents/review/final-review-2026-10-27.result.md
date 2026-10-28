Five findings, including one explicitly identified as pre-existing.

1. **MEDIUM — Conference Rooms can start its first write after cancellation.** [ConferenceRoomService.cs:630](D:/source/ExchangeAdminWeb/Services/ConferenceRoomService.cs:630)

   Exact lines:
   - 630: `cancellationToken.ThrowIfCancellationRequested();`
   - 637: `var placeResult = await RunAsync((ps, tracker) =>`
   - 652: `Invoke(ps, tracker);`

   **Mechanism:** missing cancellation check after an asynchronous pool wait. `RunAsync` supplies no cancellation token, and [ExoConnectionPool.cs:507](D:/source/ExchangeAdminWeb/Services/ExoConnectionPool.cs:507) executes `var pooled = await borrow();` before invoking this callback.

   **Scenario:** room preflights succeed and line 630 passes; all pool connections are occupied. The operator cancels while the request waits. When a connection becomes available, `Set-Place` starts and the remaining room writes proceed. No write had been sent when cancellation occurred.

   **Fix shape:** check cancellation inside the pooled callback immediately before the first write, preserving a pre-write cancellation as `Cancelled` rather than letting `RunAsync` convert its exception to `Failed`.

2. **MEDIUM — Blocked Sender starts work before checking an already-cancelled token.** [BlockedSenderService.cs:86](D:/source/ExchangeAdminWeb/Services/BlockedSenderService.cs:86)

   Exact lines:
   - 86: `var run = RunAsync((ps, tracker) =>`
   - 90: `if (!submission.TrySend())`
   - 104: `return await run.WaitAsync(UnblockBound, cancellationToken);`
   - 172: `public bool TrySend() => Interlocked.CompareExchange(ref _state, Sent, Pending) != Abandoned;`

   **Mechanism:** cancellation is observed by the waiter only after the underlying operation has started. The submission guard checks its own state, not the token.

   **Scenario:** the operator cancels during the page’s awaited protection check. [BlockedSenders.razor:470](D:/source/ExchangeAdminWeb/Components/Pages/BlockedSenders.razor:470) subsequently passes that cancelled token to this method. With a warm pool, the worker can claim `Sent` and invoke `Remove-BlockedSenderAddress` before `WaitAsync` processes cancellation. The sender is unblocked despite cancellation preceding service entry; the page can record an unknown outcome or success.

   **Fix shape:** reject cancellation before starting `RunAsync`, and make cancellation participate in the submission guard before a write is claimed.

3. **LOW — Concurrent removal records a completed job as unfinished.** [BulkJobService.cs:292](D:/source/ExchangeAdminWeb/Services/Jobs/BulkJobService.cs:292)

   Exact lines:
   - 292: `var job = _repository.Get(jobId);`
   - 305: `var deleted = _repository.Delete(jobId);`
   - 309: `outcome = deleted ? BulkJobRemoval.Removed : BulkJobRemoval.NotFinished;`

   [AdminBulkJobs.razor:386](D:/source/ExchangeAdminWeb/Components/Pages/AdminBulkJobs.razor:386) maps that outcome to:
   ```csharp
   _ => "it has not finished."
   ```

   **Mechanism:** separate read and delete transactions, followed by an incorrect inference from a zero-row delete. The repository returns false both for a nonterminal job and for an absent job.

   **Scenario:** two administrators remove the same completed job. Both initial reads find it; the first deletes it. The second delete affects zero rows and produces “it has not finished” in both the banner and audit row. Previously, the page’s subsequent read distinguished an absent job.

   **Fix shape:** determine the removal outcome atomically in the repository instead of equating every unsuccessful delete with `NotFinished`.

4. **LOW — Cutover’s sharing report hides nested IIS applications that it stops.** [Move-ConfigDbToShared.ps1:239](D:/source/ExchangeAdminWeb/tools/Move-ConfigDbToShared.ps1:239)

   Exact lines:
   ```powershell
   $null = Assert-AppPoolServesPath -Name $Name -PhysicalPath $PublishPath -NestedNodes Allow
   ```
   At line 265:
   ```powershell
   $others = @(Get-AppPoolOtherApplication -Name $Name -PhysicalPath $PublishPath)
   ```
   But [AppPoolControl.psm1:628](D:/source/ExchangeAdminWeb/tools/AppPoolControl.psm1:628) excludes descendants:
   ```powershell
   if (Test-PhysicalPathInside $lexical $target) { continue }
   ```

   **Mechanism:** the serving check allows nested applications, while the reporting helper filters them out. With no other applications remaining, line 271 says **“stopping it stops nothing else.”**

   **Scenario:** an independent IIS application under `D:\sites\Dev\Reports` shares the Dev pool and has active bulk jobs. Cutover accepts it, omits its jobs check, prints the reassurance, and stops both applications.

   **Fix shape:** let cutover exclude only the instance itself from the sharing report, retaining nested applications and their job warnings. This confirms the unresolved nested-node gap already recorded in `cdt-58`.

5. **LOW — `-PlanOnly` still executes a live jobs-database query; this predates the range.** [JobStateWarning.psm1:151](D:/source/ExchangeAdminWeb/tools/JobStateWarning.psm1:151)

   Exact lines:
   ```powershell
   $jobs = @(Get-ActiveBulkJobs -ConfigDir $ConfigDir -AppPath $AppPath)

   if ($PlanOnly) {
   ```

   The called function executes at line 88:
   ```powershell
   $output = & $tool.Host $tool.Assembly 'db' 'active-jobs' $dbPath 2>&1
   ```

   **Mechanism:** the plan-mode branch occurs after the live read. [Move-ConfigDbToShared.ps1:313](D:/source/ExchangeAdminWeb/tools/Move-ConfigDbToShared.ps1:313) and promotion’s dry-run branch explicitly reach it.

   **Scenario:** running cutover with `-PlanOnly` against existing instances launches the application’s database tool and queries their actual jobs databases. This is a real read, although the database command opens them read-only.

   **Fix shape:** branch on `PlanOnly` before `Get-ActiveBulkJobs` and print the intended check without executing it.

Other requested priority areas:

- **Authorization/protected principals:** no verified defect; Conference Rooms’ room-list exemption and retained room-mailbox gate are consistent with the rulings.
- **Bulk success aggregation:** no verified defect in the touched loops.
- **kd-72 snapshots, busy guards and input binding:** no verified defect.
- **Merge interactions:** no additional duplicated or missing mechanism verified.
- **PowerShell native exit checks and encoding:** no additional unchecked native exit or non-ASCII defect verified.
- **Functionality preservation:** no additional unapproved removal or significant restriction verified.

No files were changed, and no builds or tests were run. The pending step is correction of these paths, beginning with cancellation admission.

**DO NOT SHIP — the new cancellation handling still permits writes to start after cancellation, and the audit and deployment-reporting defects remain.**