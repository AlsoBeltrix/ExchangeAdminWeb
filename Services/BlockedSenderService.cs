using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Models.BlockedSenders;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Reads and clears Exchange Online blocked senders via <c>Get-BlockedSenderAddress</c> and
/// <c>Remove-BlockedSenderAddress</c>. Cloud-only: both cmdlets exist only in Exchange Online,
/// so there is no on-prem path here. EXO auth is owned by the ExchangeOnline parent module and
/// the shared <see cref="ExoConnectionPool"/>; this service needs no module-specific credential.
/// </summary>
public class BlockedSenderService : ExchangeServiceBase
{
    /// <summary>
    /// kd-6. How long <see cref="UnblockSenderAsync"/> waits for Exchange Online before it stops
    /// waiting. Longer than the pool's own admission can legitimately take - up to two minutes for a
    /// free connection (<c>ExoConnectionPool.BorrowAsync</c>), then a connect
    /// behind its ninety-second gate - plus a generous allowance for one Remove-BlockedSenderAddress,
    /// so a bound that runs out is a stall and not a busy pool.
    /// </summary>
    public static readonly TimeSpan UnblockTimeout = TimeSpan.FromMinutes(5);

    public BlockedSenderService(
        ExoConnectionPool exoPool,
        DelineaService delineaService,
        ILogger<BlockedSenderService> logger,
        OperationTraceService operationTrace)
        : base(exoPool, delineaService, logger, onPremServerUri: "", moduleCredentials: null, moduleId: "BlockedSenders", operationTrace: operationTrace)
    {
    }

    /// <summary>The bound in force: <see cref="UnblockTimeout"/>. A test seam, because a test cannot wait five minutes for a stall.</summary>
    internal TimeSpan UnblockBound { get; init; } = UnblockTimeout;

    /// <summary>
    /// Returns the current blocked-sender list from Exchange Online. Read-only, so it is safe to
    /// retry once on a dead pooled session (<c>allowRetry: true</c>).
    /// </summary>
    public Task<IReadOnlyList<BlockedSenderInfo>> GetBlockedSendersAsync()
    {
        return RunPooledQueryAsync<IReadOnlyList<BlockedSenderInfo>>((ps, tracker) =>
        {
            ps.AddCommand("Get-BlockedSenderAddress");
            var results = Invoke(ps, tracker);

            return results
                .Select(BlockedSenderInfo.FromPSObject)
                .Where(s => s is not null)
                .Select(s => s!)
                .OrderBy(s => s.SenderAddress, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }, allowRetry: true);
    }

    /// <summary>
    /// Unblocks a single sender by SMTP address via <c>Remove-BlockedSenderAddress</c>. A single
    /// write, so retry-on-dead-session is safe (<c>allowRetry: true</c>): the EXO pool only
    /// replays when the pre-cmdlet check proves the cmdlet never ran.
    /// </summary>
    /// <remarks>
    /// <para>
    /// kd-6. Bounded by <see cref="UnblockBound"/> and by <paramref name="cancellationToken"/>, the
    /// caller's. It had neither, so a stalled Exchange Online call held the page's operation - its
    /// busy flag and its activity - for as long as the call stalled, with no recovery but a reload.
    /// </para>
    /// <para>
    /// Stopping the wait does not stop the call: the pooled pipeline runs on, and the pool returns
    /// or discards its connection when it ends, which is logged. What the caller is told depends on
    /// whether the command had been sent, settled once by <see cref="UnblockSubmission"/>: a call
    /// abandoned before it was sent never sends afterwards, and says nothing was changed - a wait that
    /// ran out as a failed result, a cancel (kd-57) as an <see cref="OperationCanceledException"/> in
    /// the app's phrase for one, which the page files Cancelled; one abandoned after may have
    /// unblocked the sender, so it throws <see cref="BlockedSenderUnblockUnconfirmedException"/>,
    /// which the page files Undetermined (the aud-a73 rule: a write whose outcome is unknown is not a
    /// failure - and a cancel that came after the send does not make it a cancel).
    /// </para>
    /// </remarks>
    public async Task<PermissionResult> UnblockSenderAsync(string senderAddress, string reason,
        CancellationToken cancellationToken = default)
    {
        var address = NormalizeAddress(senderAddress);
        if (address is null)
            return PermissionResult.Fail("A sender address is required.");

        var submission = new UnblockSubmission();
        var run = RunAsync((ps, tracker) =>
        {
            // Checked BEFORE the command is built, so a call its caller has given up on leaves the
            // borrowed pipeline exactly as it found it.
            if (!submission.TrySend())
                throw new OperationCanceledException("The unblock was abandoned before it was sent; nothing was sent.");

            ps.AddCommand("Remove-BlockedSenderAddress")
              .AddParameter("SenderAddress", address)
              .AddParameter("ErrorAction", "Stop");
            if (!string.IsNullOrWhiteSpace(reason))
                ps.AddParameter("Reason", reason.Trim());

            Invoke(ps, tracker);
        }, () => ($"Unblocked sender {address}.", null), allowRetry: true);

        try
        {
            return await run.WaitAsync(UnblockBound, cancellationToken);
        }
        catch (Exception ex) when (ex is TimeoutException
            || (ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            // A run that ended in the same instant the wait gave up keeps its own outcome - or its own
            // throw, which is then not this method's to reclassify.
            if (run.IsCompleted)
                return await run;

            ObserveAbandoned(run, address);
            var timedOut = ex is TimeoutException;

            if (submission.TryAbandon())
            {
                // kd-57. A cancel before the send is a cancel, not a failure: thrown as one, in the app's phrase for it, for the
                // page to file Cancelled. A wait that ran out before the send stays a failed result.
                if (!timedOut)
                {
                    throw new OperationCanceledException(
                        $"{AuditService.CancelledByTheOperator} before the unblock of {address} was sent, so nothing was changed and the sender is still blocked.",
                        ex, cancellationToken);
                }

                return PermissionResult.Fail(
                    $"The unblock of {address} could not start within {DescribeBound(UnblockBound)}, so nothing was changed and the sender is still blocked. Try again.");
            }

            throw new BlockedSenderUnblockUnconfirmedException(address,
                (timedOut
                    ? $"Exchange Online did not answer the unblock of {address} within {DescribeBound(UnblockBound)}"
                    : $"The wait for the unblock of {address} was cancelled after it was sent")
                + ", so whether the sender was unblocked could not be confirmed. Refresh the list to see whether it is still blocked.",
                ex);
        }
    }

    private static string DescribeBound(TimeSpan bound) =>
        bound >= TimeSpan.FromMinutes(1) ? $"{bound.TotalMinutes:0.#} minutes" : $"{bound.TotalSeconds:0.#} seconds";

    /// <summary>
    /// The call the caller stopped waiting for still ends on its own. What it ended with is logged
    /// rather than lost, and its exception is observed rather than left unobserved.
    /// </summary>
    private void ObserveAbandoned(Task<PermissionResult> run, string address) =>
        _ = run.ContinueWith(ended =>
        {
            if (ended.IsFaulted)
                _logger.LogWarning(ended.Exception, "An unblock of {Address} failed after its caller stopped waiting", address);
            else if (ended.IsCompletedSuccessfully)
                _logger.LogWarning("An unblock of {Address} ended after its caller stopped waiting: {Outcome}",
                    address, ended.Result.Success ? "unblocked" : ended.Result.Message);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    /// <summary>
    /// kd-6. The race between the pooled call sending the unblock and its caller giving up on it,
    /// settled once: whichever comes first wins. So "nothing was changed" is never said of a call that
    /// sends afterwards, and a call that sent is never reported as one that did not. A pool retry of a
    /// call that already sent stays sent, as aud-a76's mark does.
    /// </summary>
    internal sealed class UnblockSubmission
    {
        private const int Pending = 0;
        private const int Sent = 1;
        private const int Abandoned = 2;
        private int _state;

        /// <summary>The call is about to send. False once its caller has given up, and then it must not.</summary>
        public bool TrySend() => Interlocked.CompareExchange(ref _state, Sent, Pending) != Abandoned;

        /// <summary>The caller gives up. False when the call has already sent, so its outcome is unknown.</summary>
        public bool TryAbandon() => Interlocked.CompareExchange(ref _state, Abandoned, Pending) != Sent;
    }

    /// <summary>
    /// Trims and validates an operator-supplied sender address. Pure and side-effect free so it is
    /// unit-testable without a live connection. Returns null for null/blank input.
    /// </summary>
    public static string? NormalizeAddress(string? senderAddress)
    {
        var trimmed = senderAddress?.Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? null : trimmed;
    }
}

/// <summary>
/// kd-6. An unblock its caller stopped waiting for - the bound ran out, or the caller's token was
/// cancelled - after the command had been sent, so whether the sender was unblocked is not known. The
/// message is the operator-facing sentence; the cause is the timeout or the cancellation.
/// </summary>
public sealed class BlockedSenderUnblockUnconfirmedException(string address, string message, Exception cause)
    : TimeoutException(message, cause)
{
    /// <summary>The sender address the unconfirmed unblock was for.</summary>
    public string Address { get; } = address;
}
