using ExchangeAdminWeb.Services;
using AdException = Microsoft.ActiveDirectory.Management.ADException;
using AdServerDownException = Microsoft.ActiveDirectory.Management.ADServerDownException;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The Comms-10k clear-then-fill write (docs/Comms10kBulkResolveScale-Plan.md S4, tests 10-15
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

    // ----- test 11: the clear is issued once, and before every add ------------------------------

    [Fact]
    public void TheClearIsIssuedExactlyOnceAndBeforeEveryAdd()
    {
        // Asserted on call ORDER. A second clear partway through would wipe what had already
        // been written, and a count alone would not catch it.
        var calls = new List<string>();
        var target = Dns(5000);

        Comms10kReplaceWriter.ExecuteUnderLock(
            Group, target,
            clear: () => calls.Add("clear"),
            addBatch: b => calls.Add($"add:{b.Count}"),
            readBack: () => Matching(target));

        Assert.Equal("clear", calls[0]);
        Assert.Single(calls, c => c == "clear");
        Assert.Equal(new[] { "clear", "add:2000", "add:2000", "add:1000" }, calls);
    }

    // ----- tests 12 and 14: the procedure, and the attempted-or-not dividing line ---------------

    [Fact]
    public void AFailureBeforeTheClear_RefusesWithNoWriteAndNoReadBack()
    {
        var readBacks = 0;

        var result = Comms10kReplaceWriter.Derive(
            Group, Dns(10), refusalBeforeClear: "the lock could not be acquired.",
            observed: null, readBackError: null, writeError: null);

        Assert.Equal(Comms10kOutcome.RefusedBeforeAnyChange, result.Outcome);
        Assert.False(result.Success);
        Assert.Null(result.FinalCount);
        Assert.Equal(0, result.MembersWritten);
        Assert.Equal(0, readBacks);
        Assert.Contains("not modified", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryPostClearPathAttemptsTheReadBack_IncludingOneWhereAnAddBatchThrew()
    {
        // The whole reason the read-back is outside the write's try block.
        var readBacks = 0;
        var target = Dns(3000);

        Comms10kReplaceWriter.ExecuteUnderLock(
            Group, target,
            clear: () => { },
            addBatch: _ => throw new InvalidOperationException("the directory refused the batch"),
            readBack: () => { readBacks++; return Matching(target); });

        Assert.Equal(1, readBacks);
    }

    [Fact]
    public void AClearThatThrew_StillAttemptsTheReadBack()
    {
        // From the moment the clear is ISSUED, its effect is unknown - a timeout proves neither
        // that it cleared nor that it did not - so the read-back is what settles it.
        var readBacks = 0;
        var target = Dns(10);

        Comms10kReplaceWriter.ExecuteUnderLock(
            Group, target,
            clear: () => throw new InvalidOperationException("timed out"),
            addBatch: _ => { },
            readBack: () => { readBacks++; return Matching(target); });

        Assert.Equal(1, readBacks);
    }

    // ----- test 13: outcomes come from the read-back and nothing else ---------------------------

    [Fact]
    public void ReadBackFails_IsCouldNotConfirm_AndReportsNoFinalCount()
    {
        var result = Comms10kReplaceWriter.Derive(
            Group, Dns(10), refusalBeforeClear: null,
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
            Group, target, refusalBeforeClear: null,
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
            Group, Dns(20), refusalBeforeClear: null,
            observed: null, readBackError: "the group could not be read",
            writeError: "a batch of members could not be added");

        Assert.Equal(Comms10kOutcome.CouldNotConfirm, result.Outcome);
    }

    [Fact]
    public void AReadBackThatDiffers_IsPartlyApplied_AndCountsComeFromTheObservedMembership()
    {
        var target = Dns(100);
        var observed = new Comms10kReplaceWriter.ReadBack(Dns(37), Array.Empty<string>());

        var result = Comms10kReplaceWriter.Derive(
            Group, target, refusalBeforeClear: null, observed, readBackError: null, writeError: null);

        Assert.Equal(Comms10kOutcome.PartlyApplied, result.Outcome);
        Assert.False(result.Success);
        // 37, from the read-back - never 100 from the input, and never a number derived from
        // which batches were believed to have run.
        Assert.Equal(37, result.FinalCount);
        Assert.Equal(100, result.MembersWritten);
        Assert.Contains("INCOMPLETE", result.Message, StringComparison.Ordinal);
        Assert.Contains("Re-run the same CSV", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PrimaryGroupMembersSurviveTheClear_AndAreASuccessWithExceptions_NotADiscrepancy()
    {
        // A `member` write cannot evict somebody whose membership comes from their primary
        // group. Without allowing for them the read-back could never match on such a group, and
        // every replace would report as partly applied.
        var target = Dns(10);
        var primary = new[] { "CN=Svc,OU=Service,DC=example,DC=test" };
        var observed = new Comms10kReplaceWriter.ReadBack(target, primary);

        var result = Comms10kReplaceWriter.Derive(
            Group, target, refusalBeforeClear: null, observed, readBackError: null, writeError: null);

        Assert.Equal(Comms10kOutcome.SucceededWithExceptions, result.Outcome);
        Assert.True(result.Success);
        Assert.Equal(11, result.FinalCount);
        Assert.Equal(primary, result.UnremovableMembers);
        Assert.Contains("PRIMARY group", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APrimaryGroupMemberThatIsAlsoInTheUploadedFile_IsNotReportedAsUnremovable()
    {
        // It was asked for, so its presence is the intended state, not an exception to it.
        var target = Dns(10);
        var observed = new Comms10kReplaceWriter.ReadBack(target, new[] { target[0] });

        var result = Comms10kReplaceWriter.Derive(
            Group, target, refusalBeforeClear: null, observed, readBackError: null, writeError: null);

        Assert.Equal(Comms10kOutcome.Succeeded, result.Outcome);
        Assert.Empty(result.UnremovableMembers);
    }

    // ----- test 15: the five outcomes are distinguishable ---------------------------------------

    [Fact]
    public void AllFiveOutcomesProduceDistinctMessages()
    {
        var target = Dns(10);

        var messages = new[]
        {
            Comms10kReplaceWriter.Derive(Group, target, "no lock.", null, null, null),
            Comms10kReplaceWriter.Derive(Group, target, null, Matching(target), null, null),
            Comms10kReplaceWriter.Derive(Group, target, null,
                new Comms10kReplaceWriter.ReadBack(target, new[] { "CN=Svc,DC=example,DC=test" }), null, null),
            Comms10kReplaceWriter.Derive(Group, target, null,
                new Comms10kReplaceWriter.ReadBack(Dns(3), Array.Empty<string>()), null, null),
            Comms10kReplaceWriter.Derive(Group, target, null, null, "unreadable", null),
        };

        Assert.Equal(5, messages.Select(m => m.Outcome).Distinct().Count());
        Assert.Equal(5, messages.Select(m => m.Message).Distinct(StringComparer.Ordinal).Count());
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
    public void ARetriedAddBatchNeverReIssuesTheClear()
    {
        // Retry is per OPERATION, never per run. Re-running the clear after batches have landed
        // would wipe them, which is strictly worse than the failure it is recovering from.
        var calls = new List<string>();
        var target = Dns(4000);
        var addAttempts = 0;

        Comms10kReplaceWriter.ExecuteUnderLock(
            Group, target,
            clear: () => calls.Add("clear"),
            addBatch: b => Comms10kReplaceWriter.WithRetry(
                () =>
                {
                    addAttempts++;
                    if (addAttempts == 1) throw new AdException();
                    calls.Add($"add:{b.Count}");
                },
                (_, _, _, _) => { }),
            readBack: () => Matching(target));

        Assert.Single(calls, c => c == "clear");
        Assert.Equal(new[] { "clear", "add:2000", "add:2000" }, calls);
    }

    // ----- harness -------------------------------------------------------------------------

    private static List<string> Dns(int count) =>
        Enumerable.Range(0, count).Select(i => $"CN=User{i},OU=People,DC=example,DC=test").ToList();

    private static Comms10kReplaceWriter.ReadBack Matching(IReadOnlyList<string> target) =>
        new(target, Array.Empty<string>());
}
