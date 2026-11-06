namespace ExchangeAdminWeb.Services;

/// <summary>
/// What a replace actually did. <b>A bool cannot say this</b>, and collapsing these into one is
/// the success-aggregation failure class this repository names explicitly.
/// </summary>
public enum Comms10kOutcome
{
    /// <summary>
    /// Nothing was written. Every refusal that happens BEFORE the first write is issued lands
    /// here - resolution failure, the re-read mismatch, an empty list, the authorization check -
    /// and the membership is provably untouched. Also an add that failed when the read-back then
    /// shows the membership exactly as it was before the write (sweep-d item 19).
    /// </summary>
    RefusedBeforeAnyChange,

    /// <summary>The read-back matched the intended membership exactly.</summary>
    Succeeded,

    /// <summary>
    /// Legacy persisted outcome. New replaces refuse unapproved primary members before the write
    /// and treat unexpected primary members observed after it as a mismatch.
    /// </summary>
    SucceededWithExceptions,

    /// <summary>
    /// A write was issued and the read-back shows a membership that is not the intended one.
    /// Re-running the same CSV repairs it. Under the add-first order an add failure leaves every
    /// previous member in place and a remove failure leaves every file member in place.
    /// </summary>
    PartlyApplied,

    /// <summary>
    /// A write was issued and the read-back could not be performed, so the membership is
    /// unknown. <b>Not a shade of the other two post-write outcomes.</b> Calling it "partly
    /// applied" claims knowledge of what landed; calling it "refused" claims the list is intact,
    /// which may be false and is the more dangerous error, because it tells the operator to walk
    /// away from a list that might not match the file.
    /// </summary>
    CouldNotConfirm,
}

/// <summary>
/// The pure half of the Comms-10k membership WRITE (docs/Comms10kBulkResolveScale-Plan.md S4):
/// write batching, transient-failure classification, and the single procedure that derives the
/// outcome. The directory operations themselves are the service's seams.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the write changed shape at all.</b> A single <c>Set-ADGroup -Replace</c> is atomic and
/// the directory refuses it at this module's size. The write is add-first (sweep-d item 19): the
/// file's new members are added in batches, and only after every add succeeded are the members
/// the file does not name removed. It is not atomic, but the list is never emptied and a failure
/// leaves either every previous member or every file member in place. Clear-then-fill, the
/// earlier shape, was faster on a full swap (7.9s against 53.2s measured at 10,001 members) and
/// emptied the list if the directory refused the add form. Everything in this file exists to make
/// the non-atomic window REPORTABLE rather than silent.
/// </para>
/// <para>
/// <b>The outcome is derived by one procedure, never assigned at an error site.</b> Three review
/// rounds each found a different corner of the taxonomy contradicting another, because each error
/// site was deciding for itself what had happened. <see cref="Derive"/> is the only thing that
/// decides, and the dividing line it applies is whether a write was ATTEMPTED - not whether it
/// reported failure.
/// </para>
/// </remarks>
public static class Comms10kReplaceWriter
{
    /// <summary>
    /// Members per <c>Set-ADGroup -Add</c> or <c>-Remove</c> request.
    /// </summary>
    /// <remarks>
    /// <b>Not a number to raise casually.</b> The measured single-operation ceiling is 10,000
    /// entries and write cost rises steeply with group size: a 2,000 batch lands in 0.7s to 1.4s
    /// against a group of 8,000 or less and in 9.0s to 10.8s against one of 10,000 to 20,000,
    /// which the add-first order reaches on a near-disjoint swap. It bounds one REQUEST and is not
    /// a limit on membership - the loop writes every batch.
    /// </remarks>
    public const int WriteBatchSize = 2000;

    /// <summary>
    /// The membership observed by the read-back, kept in its two parts because they behave
    /// differently: <paramref name="MemberAttribute"/> is what the write controls, and
    /// <paramref name="PrimaryGroup"/> is what it cannot evict.
    /// </summary>
    public sealed record ReadBack(IReadOnlyList<Guid> MemberAttribute, IReadOnlyList<Guid> PrimaryGroup);

    /// <summary>
    /// Partitions the resolved member list into write-sized batches, in order, covering every
    /// element exactly once. Pagination, never truncation.
    /// </summary>
    public static IEnumerable<IReadOnlyList<T>> Batch<T>(IReadOnlyList<T> members)
    {
        ArgumentNullException.ThrowIfNull(members);

        for (var i = 0; i < members.Count; i += WriteBatchSize)
        {
            var take = Math.Min(WriteBatchSize, members.Count - i);
            var batch = new List<T>(take);
            for (var j = 0; j < take; j++)
                batch.Add(members[i + j]);
            yield return batch;
        }
    }

    // ----- transient-failure classification --------------------------------------------------

    /// <summary>
    /// The full type name of the innermost exception, which is the one worth classifying.
    /// </summary>
    /// <remarks>
    /// A cmdlet failing under <c>-ErrorAction Stop</c> surfaces through
    /// <c>PowerShell.Invoke()</c> WRAPPED: the directory's own exception is the inner one. A
    /// classifier matching the outer type would match nothing, every fault would be treated as
    /// non-retryable, the retry would silently never fire, and nothing would look broken. This
    /// repository already works around the same wrapping at
    /// <c>ADAttributeEditorService.cs</c>.
    /// </remarks>
    public static string? InnermostTypeName(Exception? ex)
    {
        if (ex is null) return null;

        var current = ex;
        while (current.InnerException is not null)
            current = current.InnerException;

        return current.GetType().FullName;
    }

    /// <summary>
    /// Whether a directory write that failed with this exception type is worth attempting again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Matched on the type NAME as a string, deliberately, never on the type itself.</b>
    /// <c>Microsoft.ActiveDirectory.Management</c> appears nowhere in this solution - not a
    /// package reference, not an assembly reference, not a using. The app reaches AD entirely
    /// through the PowerShell SDK and command strings, so a <c>catch (ADException)</c> would
    /// require an assembly that ships with RSAT rather than NuGet and may be absent on the CI
    /// agent. Comparing strings keeps this compiling and testable anywhere.
    /// </para>
    /// <para>
    /// <b>Retrying is safe only because both write operations are idempotent</b>, which was
    /// measured: <c>-Add</c> naming a member who is already present and <c>-Remove</c> naming one
    /// who is absent both succeed silently, so a retried batch that had partly applied cannot
    /// double-apply. Re-check that if the write shape ever changes.
    /// </para>
    /// <para>
    /// An unrecognised name is NOT retryable. An unknown fault is not evidence of a transient
    /// one.
    /// </para>
    /// </remarks>
    public static bool IsRetryable(string? exceptionTypeName) =>
        // The observed transient fault - "a required audit event could not be generated for the
        // operation", three of nine large writes during measurement - surfaces as exactly this.
        // It is a broad base type and some permanent faults land here too, which is why every
        // retry logs its message: a non-transient fault being retried three times has to be
        // visible in the log rather than inferred.
        exceptionTypeName == "Microsoft.ActiveDirectory.Management.ADException";

    /// <summary>Attempts per directory write operation, including the first.</summary>
    /// <remarks>
    /// Per OPERATION - one add batch or one remove batch - never per run. <b>Honest limit:</b> the
    /// fault repeated twice consecutively during measurement, so three attempts will not always
    /// clear it. This reduces the failure rate; it does not eliminate it.
    /// </remarks>
    public const int MaxAttempts = 3;

    /// <summary>
    /// Runs one directory write, retrying only a classified-transient failure.
    /// </summary>
    /// <param name="operation">The single write - ONE add or remove batch. Never a whole run.</param>
    /// <param name="onAttemptFailed">
    /// Called for every failed attempt with (attempt, innermost type name, retryable, exception).
    /// Every attempt is reported, not just the last, because a permanent fault landing on
    /// <c>ADException</c> and being retried three times has to be visible in the log.
    /// </param>
    /// <param name="backoff">The pause between attempts. Omitted in tests so they do not sleep.</param>
    public static void WithRetry(
        Action operation,
        Action<int, string?, bool, Exception> onAttemptFailed,
        Action? backoff = null)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(onAttemptFailed);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                operation();
                return;
            }
            catch (Exception ex)
            {
                var typeName = InnermostTypeName(ex);
                var retryable = IsRetryable(typeName);
                onAttemptFailed(attempt, typeName, retryable, ex);

                if (!retryable || attempt >= MaxAttempts)
                    throw;

                backoff?.Invoke();
            }
        }
    }

    // ----- the add-first plan ------------------------------------------------------------------

    /// <summary>
    /// The group's <c>member</c> attribute as read immediately before the write.
    /// </summary>
    /// <param name="Members">Every member value mapped to its objectGUID.</param>
    /// <param name="UnidentifiedDns">
    /// Member values the host's directory could not map to an object (for example a member held
    /// in another domain of the forest, read without a global catalog). They cannot be members
    /// the file names - every file member was resolved by the same unscoped query that failed to
    /// map these - so they are stale by construction and are removed by the exact value read.
    /// </param>
    public sealed record CurrentMembership(IReadOnlyList<Guid> Members, IReadOnlyList<string> UnidentifiedDns);

    /// <summary>What an add-first replace writes, derived from the file and the pre-write read.</summary>
    /// <param name="ToAdd">File members not already in the <c>member</c> attribute, in file order.</param>
    /// <param name="ToRemove">Current members the file does not name.</param>
    /// <param name="ToRemoveByDn">Current member values that could not be mapped (see <see cref="CurrentMembership"/>).</param>
    public sealed record WritePlan(IReadOnlyList<Guid> ToAdd, IReadOnlyList<Guid> ToRemove, IReadOnlyList<string> ToRemoveByDn);

    /// <summary>
    /// The add set and the remove set. A member who is both present and in the file is in
    /// neither: it is never written, so it is never at risk, and the add phase cannot fail on it.
    /// </summary>
    public static WritePlan Plan(IReadOnlyList<Guid> target, CurrentMembership current)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(current);

        var present = new HashSet<Guid>(current.Members);
        var wanted = new HashSet<Guid>(target);
        var seen = new HashSet<Guid>();
        var toAdd = target.Where(guid => !present.Contains(guid) && seen.Add(guid)).ToArray();
        var toRemove = current.Members.Distinct().Where(guid => !wanted.Contains(guid)).ToArray();
        var byDn = current.UnidentifiedDns.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return new WritePlan(toAdd, toRemove, byDn);
    }

    /// <summary>Which phase of the add-first write stopped on a failure, if any.</summary>
    public enum StopPhase
    {
        /// <summary>No write failed.</summary>
        None,
        /// <summary>An add batch failed; no removal was attempted.</summary>
        Add,
        /// <summary>Every add batch succeeded and a remove batch failed.</summary>
        Remove,
    }

    // ----- the outcome procedure ---------------------------------------------------------------

    /// <summary>
    /// The add-first sequence, as a pure orchestration over its directory steps
    /// (sweep-d item 19).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The list is never emptied.</b> Every member the file names that is not already present
    /// is added first, in <see cref="WriteBatchSize"/> batches. Only when EVERY add batch has
    /// succeeded are the members the file does not name removed. A failed add stops the run
    /// there: nobody has been removed, so everyone on the list before is still on it. A failed
    /// remove leaves every member the file names on the list, plus some it does not. Neither
    /// failure can leave the list empty, or without a member it held before that the file keeps.
    /// </para>
    /// <para>
    /// <b>Why not clear-then-fill.</b> It was 7x faster on a full disjoint swap (7.9 s against
    /// 53.2 s measured, docs/Comms10kBulkResolveScale-Plan.md), but if the directory refuses the
    /// add form - the <c>&lt;GUID=...&gt;</c> value has never been observed through ADWS - the first
    /// live replace empties the list and every re-run repeats it. Unchanged members cost nothing
    /// here (3.8 s measured for 1,000 in and 1,000 out of 12,000).
    /// </para>
    /// <para>
    /// EVERY path that dispatched a write attempts the read-back, including one where a batch
    /// threw, because the membership is what decides the outcome, not the error. Authorization is
    /// the caller's, once, before the first write (sweep-d item 20): nothing in here refuses
    /// partway through.
    /// </para>
    /// </remarks>
    public static Comms10kUpdateResult ExecuteSequence(
        string groupName,
        IReadOnlyList<Guid> target,
        CurrentMembership current,
        Action<IReadOnlyList<Guid>> addBatch,
        Action<IReadOnlyList<Guid>> removeBatch,
        Action<IReadOnlyList<string>> removeByDnBatch,
        Func<ReadBack> readBack,
        Func<bool>? writeWasDispatched = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(addBatch);
        ArgumentNullException.ThrowIfNull(removeBatch);
        ArgumentNullException.ThrowIfNull(removeByDnBatch);
        ArgumentNullException.ThrowIfNull(readBack);

        var plan = Plan(target, current);
        int? membersWritten = null;
        string? writeError = null;
        var stop = StopPhase.None;

        var adds = Batch(plan.ToAdd).ToList();
        for (var i = 0; i < adds.Count && writeError is null; i++)
        {
            try
            {
                addBatch(adds[i]);
            }
            catch (Exception ex)
            {
                // Recorded, not acted on. The read-back still runs and still decides.
                writeError = $"Add batch {i + 1} of {adds.Count} ({adds[i].Count:N0} members) failed: {ex.Message}";
                stop = StopPhase.Add;
            }
        }

        if (writeError is null)
        {
            // Every file member is now covered: present before, or an acknowledged add. A
            // throwing call may have applied all, some or none of its batch; read-back cannot
            // recover that provenance, so a failure leaves this unset.
            membersWritten = target.Count;

            var removes = Batch(plan.ToRemove).Select(b => (Action)(() => removeBatch(b)))
                .Concat(Batch(plan.ToRemoveByDn).Select(b => (Action)(() => removeByDnBatch(b))))
                .ToList();
            for (var i = 0; i < removes.Count && writeError is null; i++)
            {
                try
                {
                    removes[i]();
                }
                catch (Exception ex)
                {
                    writeError = $"Remove batch {i + 1} of {removes.Count} failed: {ex.Message}";
                    stop = StopPhase.Remove;
                }
            }
        }

        if (writeError is not null && writeWasDispatched?.Invoke() == false)
            return Derive(groupName, target, writeError, observed: null, readBackError: null, writeError: null);

        ReadBack? observed = null;
        string? readBackError = null;
        try
        {
            observed = readBack();
        }
        catch (Exception ex)
        {
            readBackError = ex.Message;
        }

        return Derive(groupName, target, refusalBeforeWrite: null, observed, readBackError, writeError, membersWritten,
            stop, current);
    }

    /// <summary>
    /// The one place an outcome is decided.
    /// </summary>
    /// <param name="groupName">For the operator-facing message only.</param>
    /// <param name="target">The membership the write intended to produce.</param>
    /// <param name="refusalBeforeWrite">
    /// Non-null when the sequence stopped before any write was dispatched. Its presence, not its
    /// text, is what makes this "refused before any change".
    /// </param>
    /// <param name="observed">The read-back, or null when the read-back itself failed.</param>
    /// <param name="readBackError">Why the read-back failed, when it did.</param>
    /// <param name="writeError">
    /// A failure raised by an add or remove batch, for the message. It does NOT decide the
    /// outcome: a failed batch whose read-back matches the target is a success, because the batch
    /// may have applied before the error surfaced and the membership is what decides.
    /// </param>
    /// <param name="membersWritten">
    /// The total established by acknowledged add calls, or null when that evidence is absent.
    /// Observed membership alone cannot establish how many members this operation wrote.
    /// </param>
    /// <param name="stop">Which phase a failure stopped, for the message.</param>
    /// <param name="before">
    /// The membership read before the write. When an add failed and the read-back equals it
    /// exactly, nothing changed, and that is reported as refused rather than partly applied.
    /// </param>
    public static Comms10kUpdateResult Derive(
        string groupName,
        IReadOnlyList<Guid> target,
        string? refusalBeforeWrite,
        ReadBack? observed,
        string? readBackError,
        string? writeError,
        int? membersWritten = null,
        StopPhase stop = StopPhase.None,
        CurrentMembership? before = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (refusalBeforeWrite is not null)
        {
            return new Comms10kUpdateResult
            {
                Outcome = Comms10kOutcome.RefusedBeforeAnyChange,
                Success = false,
                MembersRequested = target.Count,
                MembersWritten = 0,
                FinalCount = null,
                Message = $"Refused before any change - {groupName} was not modified. {refusalBeforeWrite}"
            };
        }

        if (observed is null)
        {
            var why = string.IsNullOrWhiteSpace(readBackError) ? "the membership could not be read" : readBackError;
            var alsoFailed = string.IsNullOrWhiteSpace(writeError) ? "" : $" The write reported: {writeError}";
            var nothingRemoved = stop == StopPhase.Add ? " The run stopped before any removal, so nobody was removed." : "";
            return new Comms10kUpdateResult
            {
                Outcome = Comms10kOutcome.CouldNotConfirm,
                Success = false,
                MembersRequested = target.Count,
                MembersWritten = membersWritten,
                FinalCount = null,
                Message =
                    $"COULD NOT CONFIRM. The replace of {groupName} was started and its result could not be read "
                    + $"back, so the membership is unknown - it may or may not have completed.{alsoFailed}{nothingRemoved} "
                    + $"Read-back failure: {why}. Check the group directly before deciding, and note that "
                    + "re-running the same CSV is safe and repairs it either way."
            };
        }

        // Primary membership is observed, never approved by the observation itself. Preflight
        // refused extras before the write; a concurrent extra remains a discrepancy here.
        var expected = new HashSet<Guid>(target);
        var actual = new HashSet<Guid>(observed.MemberAttribute);
        actual.UnionWith(observed.PrimaryGroup);

        var writeNote = string.IsNullOrWhiteSpace(writeError) ? "" : $" The write reported: {writeError}";
        if (actual.SetEquals(expected))
        {
            return new Comms10kUpdateResult
            {
                Outcome = Comms10kOutcome.Succeeded,
                Success = true,
                MembersRequested = target.Count,
                MembersWritten = membersWritten,
                FinalCount = actual.Count,
                Message = $"Successfully updated {groupName}: {actual.Count} members.{writeNote}"
            };
        }

        // An add failed and the list reads back exactly as it was before the write: nothing
        // changed. Claiming "partly applied" would send the operator looking for damage that is
        // not there; this is the state the add-first order exists to produce.
        if (stop == StopPhase.Add && before is not null && before.UnidentifiedDns.Count == 0
            && new HashSet<Guid>(observed.MemberAttribute).SetEquals(before.Members))
        {
            return new Comms10kUpdateResult
            {
                Outcome = Comms10kOutcome.RefusedBeforeAnyChange,
                Success = false,
                MembersRequested = target.Count,
                MembersWritten = 0,
                FinalCount = actual.Count,
                Message =
                    $"Refused before any change - {groupName} was not modified. {writeError} The read-back shows "
                    + $"the membership exactly as it was ({actual.Count} members): nobody was added and nobody was removed."
            };
        }

        // Reported from the OBSERVED membership, never from which batches were believed to have
        // run. This is the one outcome that demands operator action, so it says what state the
        // list is in and what repairs it.
        var missing = expected.Count(guid => !actual.Contains(guid));
        var extra = actual.Count(guid => !expected.Contains(guid));
        var state = missing > 0
            ? $"{missing:N0} of the file's members are not on it, so the list is INCOMPLETE"
            : "every member the file names is on it";
        if (extra > 0)
            state += $", and {extra:N0} member(s) the file does not name are still on it";
        var phase = stop switch
        {
            StopPhase.Add => " The run stopped before any removal, so nobody who was on the list before was removed.",
            StopPhase.Remove => " Every add succeeded; the run stopped while removing members the file does not name.",
            _ => "",
        };
        var hasUnapprovedPrimary = observed.PrimaryGroup.Any(guid => !expected.Contains(guid));
        var recovery = hasUnapprovedPrimary
            ? "The primary-group memberships outside the approved set cannot be removed by this operation. "
                + "Correct those primary memberships before validating the file and resubmitting."
            : "Re-run the same CSV to repair it - the write does not depend on prior state, so a repeat run converges.";
        return new Comms10kUpdateResult
        {
            Outcome = Comms10kOutcome.PartlyApplied,
            Success = false,
            MembersRequested = target.Count,
            MembersWritten = membersWritten,
            FinalCount = actual.Count,
            Message =
                $"PARTLY APPLIED. {groupName} now holds {actual.Count} members and the uploaded file asked for "
                + $"{target.Count}: {state}.{phase}{writeNote} {recovery}"
        };
    }
}
