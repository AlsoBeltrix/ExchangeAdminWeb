namespace ExchangeAdminWeb.Services;

/// <summary>
/// What a replace actually did. <b>A bool cannot say this</b>, and collapsing these into one is
/// the success-aggregation failure class this repository names explicitly.
/// </summary>
public enum Comms10kOutcome
{
    /// <summary>
    /// Nothing was written. Every refusal that happens BEFORE the clear command is issued lands
    /// here - lock timeout, resolution failure, the post-lock re-read mismatch, an empty list -
    /// and the membership is provably untouched.
    /// </summary>
    RefusedBeforeAnyChange,

    /// <summary>The read-back matched the intended membership exactly.</summary>
    Succeeded,

    /// <summary>
    /// The read-back matched, allowing for members a <c>member</c> write cannot evict: anyone
    /// whose membership comes from their primary group. They are named in the result.
    /// </summary>
    SucceededWithExceptions,

    /// <summary>
    /// The clear was issued and the read-back shows a membership that is not the intended one.
    /// The list is incomplete and re-running the same CSV repairs it.
    /// </summary>
    PartlyApplied,

    /// <summary>
    /// The clear was issued and the read-back could not be performed, so the membership is
    /// unknown. <b>Not a shade of the other two post-clear outcomes.</b> Calling it "partly
    /// applied" claims knowledge of what landed; calling it "refused" claims the list is intact,
    /// which may be false and is the more dangerous error, because it tells the operator to walk
    /// away from a list that might be empty.
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
/// the directory refuses it at this module's size, so the write became clear-then-fill. That buys
/// speed (7.9s measured at 10,001 members, against 53.2s for the add-before-remove alternative)
/// and gives up atomicity: for about eight seconds the list is empty and then partial, and a
/// failure in that window leaves it incomplete. Everything in this file exists to make that
/// window REPORTABLE rather than silent.
/// </para>
/// <para>
/// <b>The outcome is derived by one procedure, never assigned at an error site.</b> Three review
/// rounds each found a different corner of the taxonomy contradicting another, because each error
/// site was deciding for itself what had happened. <see cref="Derive"/> is the only thing that
/// decides, and the dividing line it applies is whether the clear was ATTEMPTED - not whether it
/// reported failure.
/// </para>
/// </remarks>
public static class Comms10kReplaceWriter
{
    /// <summary>
    /// Members per <c>Set-ADGroup -Add</c> request.
    /// </summary>
    /// <remarks>
    /// <b>Not a number to raise casually.</b> The measured single-operation ceiling is 10,000
    /// entries and write cost rises steeply with group size; at 2,000 each batch lands in 0.7s to
    /// 1.4s because every write is against a group of 10,000 or less. It bounds one REQUEST and
    /// is not a limit on membership - the loop writes every batch.
    /// </remarks>
    public const int WriteBatchSize = 2000;

    /// <summary>
    /// The membership observed by the read-back, kept in its two parts because they behave
    /// differently: <paramref name="MemberAttribute"/> is what the write controls, and
    /// <paramref name="PrimaryGroup"/> is what it cannot evict.
    /// </summary>
    public sealed record ReadBack(IReadOnlyList<string> MemberAttribute, IReadOnlyList<string> PrimaryGroup);

    /// <summary>
    /// Partitions the resolved member list into write-sized batches, in order, covering every
    /// element exactly once. Pagination, never truncation.
    /// </summary>
    public static IEnumerable<IReadOnlyList<string>> Batch(IReadOnlyList<string> distinguishedNames)
    {
        ArgumentNullException.ThrowIfNull(distinguishedNames);

        for (var i = 0; i < distinguishedNames.Count; i += WriteBatchSize)
        {
            var take = Math.Min(WriteBatchSize, distinguishedNames.Count - i);
            var batch = new List<string>(take);
            for (var j = 0; j < take; j++)
                batch.Add(distinguishedNames[i + j]);
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
    /// measured: clearing an already-empty group succeeds, and <c>-Add</c> naming a member who is
    /// already present succeeds silently, so a retried batch that had partly applied cannot
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
    /// Per OPERATION - the clear, or one add batch - never per run. Re-running the clear after
    /// batches have landed would wipe them. <b>Honest limit:</b> the fault repeated twice
    /// consecutively during measurement, so three attempts will not always clear it. This reduces
    /// the failure rate; it does not eliminate it.
    /// </remarks>
    public const int MaxAttempts = 3;

    /// <summary>
    /// Runs one directory write, retrying only a classified-transient failure.
    /// </summary>
    /// <param name="operation">The single write - the clear, or ONE add batch. Never a whole run.</param>
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

    // ----- the outcome procedure ---------------------------------------------------------------

    /// <summary>
    /// The clear-then-fill sequence, with the locks already held, as a pure orchestration over
    /// its three directory steps.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two properties live here and nowhere else.</b> The clear is issued exactly once and
    /// before every add - a second clear partway through would wipe what had already been
    /// written. And EVERY post-clear path attempts the read-back, including one where an add
    /// batch threw, because the membership is what decides the outcome, not the error.
    /// </para>
    /// <para>
    /// Callers reach this only after everything that can refuse has refused. Nothing in here
    /// returns "refused before any change".
    /// </para>
    /// </remarks>
    public static Comms10kUpdateResult ExecuteUnderLock(
        string groupName,
        IReadOnlyList<string> target,
        Action clear,
        Action<IReadOnlyList<string>> addBatch,
        Func<ReadBack> readBack)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(clear);
        ArgumentNullException.ThrowIfNull(addBatch);
        ArgumentNullException.ThrowIfNull(readBack);

        string? writeError = null;
        try
        {
            clear();
            foreach (var batch in Batch(target))
                addBatch(batch);
        }
        catch (Exception ex)
        {
            // Recorded, not acted on. The read-back still runs and still decides.
            writeError = ex.Message;
        }

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

        return Derive(groupName, target, refusalBeforeClear: null, observed, readBackError, writeError);
    }

    /// <summary>
    /// The one place an outcome is decided.
    /// </summary>
    /// <param name="groupName">For the operator-facing message only.</param>
    /// <param name="target">The membership the write intended to produce.</param>
    /// <param name="refusalBeforeClear">
    /// Non-null when the sequence stopped before the clear command was issued. Its presence, not
    /// its text, is what makes this "refused before any change".
    /// </param>
    /// <param name="observed">The read-back, or null when the read-back itself failed.</param>
    /// <param name="readBackError">Why the read-back failed, when it did.</param>
    /// <param name="writeError">
    /// A failure raised by the clear or an add batch, for the message. It does NOT decide the
    /// outcome: a failed batch whose read-back matches the target is a success, because the batch
    /// may have applied before the error surfaced and the membership is what decides.
    /// </param>
    public static Comms10kUpdateResult Derive(
        string groupName,
        IReadOnlyList<string> target,
        string? refusalBeforeClear,
        ReadBack? observed,
        string? readBackError,
        string? writeError)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (refusalBeforeClear is not null)
        {
            return new Comms10kUpdateResult
            {
                Outcome = Comms10kOutcome.RefusedBeforeAnyChange,
                Success = false,
                MembersWritten = 0,
                FinalCount = null,
                Message = $"Refused before any change - {groupName} was not modified. {refusalBeforeClear}"
            };
        }

        if (observed is null)
        {
            var why = string.IsNullOrWhiteSpace(readBackError) ? "the membership could not be read" : readBackError;
            var alsoFailed = string.IsNullOrWhiteSpace(writeError) ? "" : $" The write reported: {writeError}";
            return new Comms10kUpdateResult
            {
                Outcome = Comms10kOutcome.CouldNotConfirm,
                Success = false,
                MembersWritten = target.Count,
                FinalCount = null,
                Message =
                    $"COULD NOT CONFIRM. The replace of {groupName} was started and its result could not be read "
                    + $"back, so the membership is unknown - it may or may not have completed.{alsoFailed} "
                    + $"Read-back failure: {why}. Check the group directly before deciding, and note that "
                    + "re-running the same CSV is safe and repairs it either way."
            };
        }

        var expected = new HashSet<string>(target, StringComparer.OrdinalIgnoreCase);

        // A `member` write cannot evict somebody whose membership comes from their primary group,
        // so they are part of the expected final state rather than a discrepancy. Without this
        // the read-back could never match on a group that has one.
        var unremovable = observed.PrimaryGroup
            .Where(dn => !string.IsNullOrWhiteSpace(dn) && !expected.Contains(dn))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var dn in observed.PrimaryGroup)
            if (!string.IsNullOrWhiteSpace(dn)) expected.Add(dn);

        var actual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dn in observed.MemberAttribute)
            if (!string.IsNullOrWhiteSpace(dn)) actual.Add(dn);
        foreach (var dn in observed.PrimaryGroup)
            if (!string.IsNullOrWhiteSpace(dn)) actual.Add(dn);

        if (actual.SetEquals(expected))
        {
            if (unremovable.Count == 0)
            {
                return new Comms10kUpdateResult
                {
                    Outcome = Comms10kOutcome.Succeeded,
                    Success = true,
                    MembersWritten = target.Count,
                    FinalCount = actual.Count,
                    Message = $"Successfully updated {groupName}: {actual.Count} members."
                };
            }

            var named = string.Join("; ", unremovable.Take(5));
            var more = unremovable.Count > 5 ? $" and {unremovable.Count - 5} more" : "";
            return new Comms10kUpdateResult
            {
                Outcome = Comms10kOutcome.SucceededWithExceptions,
                Success = true,
                MembersWritten = target.Count,
                FinalCount = actual.Count,
                UnremovableMembers = unremovable,
                Message =
                    $"Updated {groupName}: {actual.Count} members. {unremovable.Count} could not be removed "
                    + $"because the group is their PRIMARY group, which a membership write cannot change: "
                    + $"{named}{more}."
            };
        }

        // Reported from the OBSERVED membership, never from which batches were believed to have
        // run. This is the one outcome that demands operator action, so it says what state the
        // list is in and what repairs it.
        var writeNote = string.IsNullOrWhiteSpace(writeError) ? "" : $" The write reported: {writeError}";
        return new Comms10kUpdateResult
        {
            Outcome = Comms10kOutcome.PartlyApplied,
            Success = false,
            MembersWritten = target.Count,
            FinalCount = actual.Count,
            Message =
                $"PARTLY APPLIED. {groupName} now holds {actual.Count} members and the uploaded file asked for "
                + $"{target.Count}, so the list is INCOMPLETE.{writeNote} Re-run the same CSV to repair it - "
                + "the write does not depend on prior state, so a repeat run converges."
        };
    }
}
