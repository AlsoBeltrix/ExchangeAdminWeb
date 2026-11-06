using ExchangeAdminWeb.Services;
using AdException = Microsoft.ActiveDirectory.Management.ADException;
using AdServerDownException = Microsoft.ActiveDirectory.Management.ADServerDownException;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The Comms-10k add-first write (docs/Comms10kBulkResolveScale-Plan.md S4, tests 10-15
/// and 20): batching, the outcome procedure, and transient-failure classification.
/// </summary>
/// <remarks>
/// <para>
/// <b>The single most important property here is that the outcome is DERIVED, not assigned.</b>
/// Three review rounds of the plan each found a different corner of the taxonomy contradicting
/// another, because each error site was deciding for itself what had happened. These tests are
/// written against the procedure, never against individual error sites, for that reason.
/// </para>
/// <para>
/// Two consequences look wrong at first glance and are the point. A failed add batch is NOT
/// automatically "partly applied" - if the read-back then also fails, the answer is "could not
/// confirm". And a failed add batch whose read-back matches the target IS a success: the batch
/// may have applied before the error surfaced, and the membership is what decides.
/// </para>
/// </remarks>
public class Comms10kReplaceWriterTests
{
    private const string Group = "Broadcast-List";

    // ----- test 10: write batching ------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(1999)]
    [InlineData(2000)]
    [InlineData(2001)]
    [InlineData(10001)]
    public void BatchingCoversEveryMemberExactlyOnceAndInOrder(int count)
    {
        var input = Dns(count);

        var batches = Comms10kReplaceWriter.Batch(input).ToList();

        Assert.Equal(input, batches.SelectMany(b => b).ToList());
        Assert.All(batches, b => Assert.True(b.Count <= Comms10kReplaceWriter.WriteBatchSize));
        // Pagination, never truncation: no batch may be empty and none may be dropped.
        Assert.All(batches, b => Assert.NotEmpty(b));
    }

    // ----- sweep-d item 19: add first, remove only after every add succeeded -----------------

    [Fact]
    public void EveryAddBatchRunsBeforeAnyRemoval_AndNothingIsEverCleared()
    {
        // Asserted on call ORDER. The list must never be emptied before the new membership is
        // proven writable, so every add precedes every removal.
        var calls = new List<string>();
        var target = Dns(5000);
        var current = Current(Others(4500));

        Execute(target, current, calls);

        Assert.Equal(new[] { "add:2000", "add:2000", "add:1000", "remove:2000", "remove:2000", "remove:500" }, calls);
    }

    [Fact]
    public void AFailedAdd_StopsTheRun_BeforeAnyRemoval_AndNamesTheBatchAndTheError()
    {
        var calls = new List<string>();
        var target = Dns(5000);
        var before = Others(300);
        var current = Current(before);

        var result = Execute(target, current, calls, failAdd: 2,
            readBack: () => new Comms10kReplaceWriter.ReadBack(before.Concat(target.Take(2000)).ToList(), Array.Empty<Guid>()));

        Assert.Equal(new[] { "add:2000", "add:2000!" }, calls);
        Assert.Equal(Comms10kOutcome.PartlyApplied, result.Outcome);
        Assert.Null(result.MembersWritten);
        Assert.Contains("Add batch 2 of 3 (2,000 members) failed: the directory refused the GUID form", result.Message, StringComparison.Ordinal);
        Assert.Contains("nobody who was on the list before was removed", result.Message, StringComparison.Ordinal);
        Assert.Contains("still on it", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFirstAddRefused_WithTheListReadBackUnchanged_IsRefusedBeforeAnyChange_NotAnEmptyList()
    {
        // The sweep's case: ADWS refuses the <GUID=...> form on the very first batch. Clear-first
        // left the list empty here; add-first leaves it exactly as it was, and says so.
        var calls = new List<string>();
        var before = Others(300);
        var current = Current(before);

        var result = Execute(Dns(10), current, calls, failAdd: 1,
            readBack: () => new Comms10kReplaceWriter.ReadBack(before, Array.Empty<Guid>()));

        Assert.Equal(new[] { "add:10!" }, calls);
        Assert.Equal(Comms10kOutcome.RefusedBeforeAnyChange, result.Outcome);
        Assert.False(result.Success);
        Assert.Equal(0, result.MembersWritten);
        Assert.Equal(300, result.FinalCount);
        Assert.Contains("not modified", result.Message, StringComparison.Ordinal);
        Assert.Contains("Add batch 1 of 1 (10 members) failed", result.Message, StringComparison.Ordinal);
        Assert.Contains("exactly as it was (300 members)", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedRemove_LeavesEveryFileMember_AndReportsTheExtras()
    {
        var calls = new List<string>();
        var target = Dns(100);
        var before = Others(50);

        var result = Execute(target, Current(before), calls, failRemove: 1,
            readBack: () => new Comms10kReplaceWriter.ReadBack(target.Concat(before).ToList(), Array.Empty<Guid>()));

        Assert.Equal(new[] { "add:100", "remove:50!" }, calls);
        Assert.Equal(Comms10kOutcome.PartlyApplied, result.Outcome);
        Assert.Equal(100, result.MembersWritten);
        Assert.Contains("Remove batch 1 of 1 failed", result.Message, StringComparison.Ordinal);
        Assert.Contains("every member the file names is on it, and 50 member(s) the file does not name are still on it", result.Message, StringComparison.Ordinal);
        Assert.Contains("Every add succeeded", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AMemberBothPresentAndInTheFile_IsNeitherAddedNorRemoved_AndTheFinalMembershipIsTheFile()
    {
        // The overlap the add-first order must handle explicitly: neither set may contain it.
        var target = Dns(10);
        var stale = Others(3);
        var current = Current(target.Take(4).Concat(stale).ToList());
        var directory = new HashSet<Guid>(current.Members);

        var result = Comms10kReplaceWriter.ExecuteSequence(
            Group, target, current,
            addBatch: b => { Assert.DoesNotContain(b, directory.Contains); directory.UnionWith(b); },
            removeBatch: b => { Assert.All(b, g => Assert.DoesNotContain(g, target)); directory.ExceptWith(b); },
            removeByDnBatch: _ => throw new InvalidOperationException("none expected"),
            readBack: () => new Comms10kReplaceWriter.ReadBack(directory.ToList(), Array.Empty<Guid>()));

        var plan = Comms10kReplaceWriter.Plan(target, current);
        Assert.Equal(target.Skip(4), plan.ToAdd);
        Assert.Equal(stale, plan.ToRemove);
        Assert.True(directory.SetEquals(target));
        Assert.Equal(Comms10kOutcome.Succeeded, result.Outcome);
        Assert.Equal(10, result.MembersWritten);
    }

    [Fact]
    public void UnidentifiedCurrentValues_AreRemovedByValue_AfterTheGuidRemovals()
    {
        var calls = new List<string>();
        var target = Dns(5);
        var current = new Comms10kReplaceWriter.CurrentMembership(Others(2), ["CN=Elsewhere,DC=other,DC=test"]);

        Execute(target, current, calls);

        Assert.Equal(new[] { "add:5", "remove:2", "remove-dn:1" }, calls);
    }

    [Fact]
    public void AnUnchangedList_WritesNothing_AndSucceedsOnTheReadBack()
    {
        var calls = new List<string>();
        var target = Dns(20);

        var result = Execute(target, Current(target), calls, dispatched: () => false);

        Assert.Empty(calls);
        Assert.Equal(Comms10kOutcome.Succeeded, result.Outcome);
    }

    // ----- tests 12 and 14: the procedure, and the attempted-or-not dividing line ---------------

    [Fact]
    public void AFailureBeforeTheClear_RefusesWithNoWriteAndNoReadBack()
    {
        var readBacks = 0;

        var result = Comms10kReplaceWriter.Derive(
            Group, Dns(10), refusalBeforeWrite: "the lock could not be acquired.",
            observed: null, readBackError: null, writeError: null);

        Assert.Equal(Comms10kOutcome.RefusedBeforeAnyChange, result.Outcome);
        Assert.False(result.Success);
        Assert.Null(result.FinalCount);
        Assert.Equal(0, result.MembersWritten);
        Assert.Equal(0, readBacks);
        Assert.Contains("not modified", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryPostWritePathAttemptsTheReadBack_IncludingOneWhereAnAddBatchThrew()
    {
        // The whole reason the read-back is outside the write's try block.
        var readBacks = 0;
        var target = Dns(3000);

        Comms10kReplaceWriter.ExecuteSequence(
            Group, target, Current([]),
            addBatch: _ => throw new InvalidOperationException("the directory refused the batch"),
            removeBatch: _ => { },
            removeByDnBatch: _ => { },
            readBack: () => { readBacks++; return Matching(target); });

        Assert.Equal(1, readBacks);
    }

    [Fact]
    public void ARemoveThatThrew_StillAttemptsTheReadBack()
    {
        // From the moment a write is ISSUED, its effect is unknown - a timeout proves neither
        // that it applied nor that it did not - so the read-back is what settles it.
        var readBacks = 0;
        var target = Dns(10);

        Comms10kReplaceWriter.ExecuteSequence(
            Group, target, Current(Others(3)),
            addBatch: _ => { },
            removeBatch: _ => throw new InvalidOperationException("timed out"),
            removeByDnBatch: _ => { },
            readBack: () => { readBacks++; return Matching(target); });

        Assert.Equal(1, readBacks);
    }

    [Fact]
    public void AFailureBeforeAnyWriteWasDispatched_RefusesWithoutAReadBack()
    {
        var readBacks = 0;

        var result = Comms10kReplaceWriter.ExecuteSequence(
            Group, Dns(10), Current([]),
            addBatch: _ => throw new ArgumentException("no dispatch"),
            removeBatch: _ => { },
            removeByDnBatch: _ => { },
            readBack: () => { readBacks++; return Matching(Dns(10)); },
            writeWasDispatched: () => false);

        Assert.Equal(0, readBacks);
        Assert.Equal(Comms10kOutcome.RefusedBeforeAnyChange, result.Outcome);
    }

    // ----- test 13: outcomes come from the read-back and nothing else ---------------------------

    [Fact]
    public void ReadBackFails_IsCouldNotConfirm_AndReportsNoFinalCount()
    {
        var result = Comms10kReplaceWriter.Derive(
            Group, Dns(10), refusalBeforeWrite: null,
            observed: null, readBackError: "the server did not respond", writeError: null);

        Assert.Equal(Comms10kOutcome.CouldNotConfirm, result.Outcome);
        Assert.False(result.Success);
        // Absent rather than guessed. A number here would be a claim the module cannot support.
        Assert.Null(result.FinalCount);
        Assert.Contains("may or may not", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("re-running the same CSV is safe", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AFailedAddBatch_WhoseReadBackMatches_IsASuccess()
    {
        // Looks wrong and is right: the batch may have applied before the error surfaced. The
        // membership decides, not the error.
        var target = Dns(20);

        var result = Comms10kReplaceWriter.Derive(
            Group, target, refusalBeforeWrite: null,
            observed: Matching(target), readBackError: null,
            writeError: "a batch of members could not be added: transient fault");

        Assert.Equal(Comms10kOutcome.Succeeded, result.Outcome);
        Assert.True(result.Success);
        Assert.Equal(20, result.FinalCount);
    }

    [Fact]
    public void AFailedAddBatch_WhoseReadBackAlsoFails_IsCouldNotConfirm_NotPartlyApplied()
    {
        // The corner the earlier per-error-site rules kept getting wrong.
        var result = Comms10kReplaceWriter.Derive(
            Group, Dns(20), refusalBeforeWrite: null,
            observed: null, readBackError: "the group could not be read",
            writeError: "a batch of members could not be added");

        Assert.Equal(Comms10kOutcome.CouldNotConfirm, result.Outcome);
    }

    [Fact]
    public void AReadBackThatDiffers_IsPartlyApplied_AndCountsComeFromTheObservedMembership()
    {
        var target = Dns(100);
        var observed = new Comms10kReplaceWriter.ReadBack(Dns(37), Array.Empty<Guid>());

        var result = Comms10kReplaceWriter.Derive(
            Group, target, refusalBeforeWrite: null, observed, readBackError: null, writeError: null);

        Assert.Equal(Comms10kOutcome.PartlyApplied, result.Outcome);
        Assert.False(result.Success);
        // 37, from the read-back - never 100 from the input, and never a number derived from
        // which batches were believed to have run.
        Assert.Equal(37, result.FinalCount);
        Assert.Equal(100, result.MembersRequested);
        Assert.Null(result.MembersWritten);
        Assert.Contains("INCOMPLETE", result.Message, StringComparison.Ordinal);
        Assert.Contains("Re-run the same CSV", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnexpectedPrimaryGroupMembersAfterClearAreAMismatch()
    {
        // Preflight can race a primary-group change. Its observation never expands approval.
        var target = Dns(10);
        var primary = new[] { Guid.Parse("ed192cad-c7bf-491b-93b0-09fe2648d2c8") };
        var observed = new Comms10kReplaceWriter.ReadBack(target, primary);

        var result = Comms10kReplaceWriter.Derive(
            Group, target, refusalBeforeWrite: null, observed, readBackError: null, writeError: null);

        Assert.Equal(Comms10kOutcome.PartlyApplied, result.Outcome);
        Assert.False(result.Success);
        Assert.Equal(11, result.FinalCount);
        Assert.Empty(result.UnremovableMembers);
    }

    [Fact]
    public void APrimaryGroupMemberThatIsAlsoInTheUploadedFile_IsNotReportedAsUnremovable()
    {
        // It was asked for, so its presence is the intended state, not an exception to it.
        var target = Dns(10);
        var observed = new Comms10kReplaceWriter.ReadBack(target, new[] { target[0] });

        var result = Comms10kReplaceWriter.Derive(
            Group, target, refusalBeforeWrite: null, observed, readBackError: null, writeError: null);

        Assert.Equal(Comms10kOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.UnremovableMembers);
    }

    // ----- test 15: the five outcomes are distinguishable ---------------------------------------

    [Fact]
    public void AllCurrentOutcomesProduceDistinctMessages()
    {
        var target = Dns(10);

        var messages = new[]
        {
            Comms10kReplaceWriter.Derive(Group, target, "no lock.", null, null, null),
            Comms10kReplaceWriter.Derive(Group, target, null, Matching(target), null, null),
            Comms10kReplaceWriter.Derive(Group, target, null,
                new Comms10kReplaceWriter.ReadBack(Dns(3), Array.Empty<Guid>()), null, null),
            Comms10kReplaceWriter.Derive(Group, target, null, null, "unreadable", null),
        };

        Assert.Equal(4, messages.Select(m => m.Outcome).Distinct().Count());
        Assert.Equal(4, messages.Select(m => m.Message).Distinct(StringComparer.Ordinal).Count());
    }

    // ----- test 20: retry classification --------------------------------------------------------

    [Theory]
    [InlineData("Microsoft.ActiveDirectory.Management.ADException", true)]
    [InlineData("Microsoft.ActiveDirectory.Management.ADServerDownException", false)]
    [InlineData("Microsoft.ActiveDirectory.Management.ADIdentityNotFoundException", false)]
    [InlineData("Microsoft.ActiveDirectory.Management.ADInvalidOperationException", false)]
    [InlineData("System.UnauthorizedAccessException", false)]
    [InlineData("Some.Type.NobodyHasSeenBefore", false)]
    [InlineData(null, false)]
    public void OnlyTheObservedTransientFaultIsRetryable(string? typeName, bool expected)
    {
        // ADServerDownException is the signature an OVERSIZED request produced during
        // measurement. Retrying it would repeat a request the directory refuses every time.
        // An unrecognised name is not retried either: an unknown fault is not evidence of a
        // transient one.
        Assert.Equal(expected, Comms10kReplaceWriter.IsRetryable(typeName));
    }

    [Fact]
    public void TheClassificationIsPinnedForBothShapes_RawAndCmdletWrapped()
    {
        // A cmdlet failing under -ErrorAction Stop surfaces WRAPPED: the directory's exception
        // is the inner one. A classifier matching the OUTER type would match nothing, every
        // fault would be non-retryable, the retry would silently never fire, and nothing would
        // look broken. So the unwrap is guarded here, not just the type list.
        var raw = new AdException();
        var wrapped = new InvalidOperationException("a batch of members could not be added", new AdException());

        Assert.Equal("Microsoft.ActiveDirectory.Management.ADException",
            Comms10kReplaceWriter.InnermostTypeName(raw));
        Assert.Equal("Microsoft.ActiveDirectory.Management.ADException",
            Comms10kReplaceWriter.InnermostTypeName(wrapped));
        Assert.True(Comms10kReplaceWriter.IsRetryable(Comms10kReplaceWriter.InnermostTypeName(raw)));
        Assert.True(Comms10kReplaceWriter.IsRetryable(Comms10kReplaceWriter.InnermostTypeName(wrapped)));

        var rawDown = new AdServerDownException();
        var wrappedDown = new InvalidOperationException("the clear failed", new AdServerDownException());

        Assert.False(Comms10kReplaceWriter.IsRetryable(Comms10kReplaceWriter.InnermostTypeName(rawDown)));
        Assert.False(Comms10kReplaceWriter.IsRetryable(Comms10kReplaceWriter.InnermostTypeName(wrappedDown)));
    }

    [Fact]
    public void ARetryableFailureIsRetriedUpToTheLimitAndThenReportedAsAFailure()
    {
        var attempts = 0;
        var logged = new List<int>();

        Assert.Throws<AdException>(() =>
            Comms10kReplaceWriter.WithRetry(
                () => { attempts++; throw new AdException(); },
                (attempt, _, _, _) => logged.Add(attempt)));

        Assert.Equal(Comms10kReplaceWriter.MaxAttempts, attempts);
        // Every attempt logs, not just the last: ADException is a broad base type and a
        // permanent fault being retried three times has to be visible rather than inferred.
        Assert.Equal(new[] { 1, 2, 3 }, logged);
    }

    [Fact]
    public void AServerDownFailureIsNotRetried_EvenThoughItComesFromTheSameNamespace()
    {
        // It is the signature an OVERSIZED request produced during measurement; retrying repeats
        // a request the directory refuses every time.
        var attempts = 0;

        Assert.Throws<AdServerDownException>(() =>
            Comms10kReplaceWriter.WithRetry(
                () => { attempts++; throw new AdServerDownException(); },
                (_, _, _, _) => { }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void ANonRetryableFailureFailsOnTheFirstAttempt()
    {
        var attempts = 0;

        Assert.Throws<UnauthorizedAccessException>(() =>
            Comms10kReplaceWriter.WithRetry(
                () => { attempts++; throw new UnauthorizedAccessException(); },
                (_, _, _, _) => { }));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void ARetryThatEventuallySucceedsCompletesTheOperation()
    {
        var attempts = 0;

        Comms10kReplaceWriter.WithRetry(
            () => { if (++attempts < 2) throw new AdException(); },
            (_, _, _, _) => { });

        Assert.Equal(2, attempts);
    }

    [Fact]
    public void ARetriedAddBatchIsRetriedAlone_AndNoRemovalRunsUntilItSucceeds()
    {
        // Retry is per OPERATION, never per run.
        var calls = new List<string>();
        var target = Dns(4000);
        var addAttempts = 0;

        Comms10kReplaceWriter.ExecuteSequence(
            Group, target, Current(Others(1)),
            addBatch: b => Comms10kReplaceWriter.WithRetry(
                () =>
                {
                    addAttempts++;
                    if (addAttempts == 1) throw new AdException();
                    calls.Add($"add:{b.Count}");
                },
                (_, _, _, _) => { }),
            removeBatch: b => calls.Add($"remove:{b.Count}"),
            removeByDnBatch: _ => { },
            readBack: () => Matching(target));

        Assert.Equal(new[] { "add:2000", "add:2000", "remove:1" }, calls);
    }

    // ----- harness -------------------------------------------------------------------------

    private static List<Guid> Dns(int count) =>
        Enumerable.Range(0, count).Select(i => new Guid(i + 1, 0, 0, new byte[8])).ToList();

    // Members the file does not name: a disjoint range.
    private static List<Guid> Others(int count) =>
        Enumerable.Range(0, count).Select(i => new Guid(i + 1, 1, 0, new byte[8])).ToList();

    private static Comms10kReplaceWriter.CurrentMembership Current(IReadOnlyList<Guid> members) =>
        new(members, Array.Empty<string>());

    // Records "add:N" / "remove:N" / "remove-dn:N"; a trailing "!" marks the batch that threw.
    private static Comms10kUpdateResult Execute(
        IReadOnlyList<Guid> target, Comms10kReplaceWriter.CurrentMembership current, List<string> calls,
        int failAdd = 0, int failRemove = 0, Func<Comms10kReplaceWriter.ReadBack>? readBack = null,
        Func<bool>? dispatched = null)
    {
        var adds = 0;
        var removes = 0;
        return Comms10kReplaceWriter.ExecuteSequence(
            Group, target, current,
            addBatch: b =>
            {
                if (++adds == failAdd) { calls.Add($"add:{b.Count}!"); throw new InvalidOperationException("the directory refused the GUID form"); }
                calls.Add($"add:{b.Count}");
            },
            removeBatch: b =>
            {
                if (++removes == failRemove) { calls.Add($"remove:{b.Count}!"); throw new InvalidOperationException("the directory refused the removal"); }
                calls.Add($"remove:{b.Count}");
            },
            removeByDnBatch: b => calls.Add($"remove-dn:{b.Count}"),
            readBack: readBack ?? (() => Matching(target)),
            writeWasDispatched: dispatched);
    }

    private static Comms10kReplaceWriter.ReadBack Matching(IReadOnlyList<Guid> target) =>
        new(target, Array.Empty<Guid>());
}
