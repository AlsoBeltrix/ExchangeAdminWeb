using System.Text.Json;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// Runs a Comms-10k membership replace on the background runner: resolve the uploaded addresses,
/// then replace the group's membership.
/// </summary>
/// <remarks>
/// TWO ROWS, ONE PER STAGE. One member is deliberately NOT one row, and that is the whole design
/// decision here (docs/GlobalProgressSystem-Plan.md S3, option a).
///
/// The runner aggregates rows into "9,998 of 10,000 succeeded". For a clear-then-refill write
/// that is a lie waiting to happen: the operation is not 10,000 independent writes, it is one
/// write that empties the list and refills it, and a failure partway leaves the list broken
/// rather than 99.98% correct. Reporting it per member would tell an operator the thing went
/// almost perfectly about a run that left a company-wide broadcast list incomplete.
///
/// So the rows are the STAGES, the per-member truth lives in the operation's own five-outcome
/// result, and that result is what reaches the operator. Coarse and honest beats granular and
/// wrong.
///
/// The five outcomes are the module's existing contract and survive the move unchanged:
/// succeeded, succeeded with unremovable primary-group members, partly applied, could not
/// confirm, refused before any change. The two worth an operator's attention are "partly
/// applied" and "could not confirm", because those are the ones saying the list may be broken -
/// see docs/Comms10kBulkResolveScale-Plan.md.
///
/// Audit and the admin notification moved here WITH the operation. They are not optional extras
/// that stayed behind on the page: the Constitution requires both for every mutating action, and
/// the page no longer performs the mutation.
/// </remarks>
public sealed class Comms10kReplaceProcessor : IBulkJobProcessor
{
    /// <summary>The module id, as a constant so Program.cs can register it without constructing one.</summary>
    public const string ModuleName = "Comms10k";

    internal const int ResolveRow = 0;
    internal const int ReplaceRow = 1;
    internal const int StageCount = 2;

    private readonly Comms10kService _comms;
    private readonly AuditService _audit;
    private readonly EmailService _email;
    private readonly ILogger<Comms10kReplaceProcessor> _logger;

    // Carried from the resolve stage to the replace stage. Safe because the runner resolves ONE
    // processor instance per job and calls ProcessRowAsync in order on it (BulkJobService, the
    // row loop). A processor shared across jobs would make this a cross-job leak.
    private List<string>? _resolvedDns;
    private Comms10kUpdateResult? _result;

    public Comms10kReplaceProcessor(
        Comms10kService comms,
        AuditService audit,
        EmailService email,
        ILogger<Comms10kReplaceProcessor> logger)
    {
        _comms = comms;
        _audit = audit;
        _email = email;
        _logger = logger;
    }

    public string ModuleId => ModuleName;

    /// <summary>
    /// Always the stage count. The payload is still parsed here so an unreadable one fails the
    /// job up front rather than halfway through a write.
    /// </summary>
    public int CountRows(BulkJob job)
    {
        _ = Payload(job);
        return StageCount;
    }

    public async Task<BulkJobRowOutcome> ProcessRowAsync(BulkJob job, int rowIndex, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);

        var payload = Payload(job);

        return rowIndex switch
        {
            ResolveRow => await ResolveAsync(payload),
            ReplaceRow => await ReplaceAsync(job, payload),
            _ => new BulkJobRowOutcome
            {
                Target = payload.SourceFileName ?? "membership",
                Status = BulkJobRowStatus.Failed,
                Message = $"Unexpected stage {rowIndex}.",
            },
        };
    }

    private async Task<BulkJobRowOutcome> ResolveAsync(Comms10kReplaceJobPayload payload)
    {
        var target = payload.SourceFileName ?? "uploaded list";
        var resolve = await _comms.ResolveEmailsAsync(payload.Emails);

        if (!resolve.Success)
        {
            // Nothing has been written, so this is a clean refusal rather than a partial state.
            // Leaving _resolvedDns null is what stops the replace stage running on a bad set.
            return new BulkJobRowOutcome
            {
                Target = target,
                Status = BulkJobRowStatus.Failed,
                Message = resolve.Message,
            };
        }

        _resolvedDns = resolve.ResolvedDns;

        // Partial, not Success, when addresses were dropped: the operator asked for a list and
        // is getting a smaller one. The count difference IS the finding, and burying it in a
        // success would hide exactly the "missing email addresses" complaint this module
        // already has against it.
        var skipped = resolve.SkippedEmails.Count;

        return new BulkJobRowOutcome
        {
            Target = target,
            Status = skipped > 0 ? BulkJobRowStatus.Partial : BulkJobRowStatus.Success,
            Message = skipped > 0
                ? $"{resolve.ResolvedDns.Count:N0} of {payload.Emails.Count:N0} matched in AD; {skipped:N0} not found"
                : $"{resolve.ResolvedDns.Count:N0} of {payload.Emails.Count:N0} matched in AD",
        };
    }

    private async Task<BulkJobRowOutcome> ReplaceAsync(BulkJob job, Comms10kReplaceJobPayload payload)
    {
        var target = payload.SourceFileName ?? "uploaded list";

        if (_resolvedDns is null)
        {
            // The resolve stage failed and the runner still reached this row. Refuse rather than
            // writing: an empty or stale set here would CLEAR the broadcast list.
            return new BulkJobRowOutcome
            {
                Target = target,
                Status = BulkJobRowStatus.Failed,
                Message = "Addresses were not resolved, so nothing was written.",
            };
        }

        var result = await _comms.ExecuteReplaceAsync(_resolvedDns, job.SubmittedBy);
        _result = result;

        SafeAudit(() => _audit.LogModuleAction(
            job.SubmittedBy, job.SubmittedIp, "Comms10k_Replace", "Comms10k",
            target, result.Success, job.Ticket ?? "", result.Success ? null : result.Message,
            // The outcome rides `extra`, which is the ONLY channel that survives a SUCCESSFUL
            // operation - LogModuleAction discards errorDetail on success, so "succeeded with
            // exceptions", the one success an operator may need to act on, would otherwise be
            // indistinguishable from a clean one in the trail.
            extra: new Dictionary<string, object?>
            {
                ["Outcome"] = result.Outcome.ToString(),
                ["MemberCount"] = _resolvedDns.Count,
                ["MembersWritten"] = result.MembersWritten,
                // Absent rather than guessed. A read-back failure is exactly when a number
                // invented here would be believed.
                ["FinalCount"] = result.FinalCount?.ToString() ?? "UNKNOWN - read-back failed",
                ["SourceFile"] = payload.SourceFileName,
            }));

        // The runner's three row states cannot express five outcomes, so the row status is the
        // coarse answer and the message carries the real one. "Could not confirm" maps to
        // Partial rather than Failed on purpose: Failed reads as "nothing happened", and the
        // whole point of that outcome is that something may well have.
        var status = result.Outcome switch
        {
            Comms10kOutcome.Succeeded => BulkJobRowStatus.Success,
            Comms10kOutcome.SucceededWithExceptions => BulkJobRowStatus.Partial,
            Comms10kOutcome.PartlyApplied => BulkJobRowStatus.Partial,
            Comms10kOutcome.CouldNotConfirm => BulkJobRowStatus.Partial,
            _ => BulkJobRowStatus.Failed,
        };

        return new BulkJobRowOutcome
        {
            Target = target,
            Status = status,
            Message = result.Message,
        };
    }

    /// <summary>
    /// The admin notification, moved off the browser circuit and into the job - which is the
    /// point of running here at all, since the circuit may be long gone by now.
    /// </summary>
    public async Task OnJobCompletedAsync(BulkJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var outcome = _result?.Outcome.ToString() ?? "did not complete";
        var detail = _result?.Message ?? job.Message ?? "No detail was recorded.";

        try
        {
            await _email.SendAdminNotificationAsync(
                job.SubmittedBy,
                job.SubmittedIp,
                "Comms10k_Replace",
                _result?.Success ?? false,
                job.Ticket ?? "",
                new Dictionary<string, string>
                {
                    // The outcome word is the part an administrator reads first: "partly
                    // applied" and "could not confirm" are the two that need acting on.
                    ["Outcome"] = outcome,
                    ["Detail"] = detail,
                    ["Members written"] = _result?.MembersWritten.ToString() ?? "0",
                    // Never a removal count: clear-then-fill does not read the prior
                    // membership, so it knows what it wrote and what the group now holds,
                    // and nothing about what left.
                    ["Final membership"] = _result?.FinalCount?.ToString() ?? "UNKNOWN - read-back failed",
                },
                _result?.Success == true ? null : detail);
        }
        catch (Exception ex)
        {
            // Never changes the job result. The runner has already persisted the terminal state,
            // and a failed email must not turn a completed write into a failure.
            _logger.LogError(ex, "Comms-10k completion notification failed for job {JobId}", job.Id);
        }
    }

    private static Comms10kReplaceJobPayload Payload(BulkJob job) =>
        JsonSerializer.Deserialize<Comms10kReplaceJobPayload>(job.PayloadJson)
        ?? throw new InvalidOperationException("The Comms-10k job payload could not be read.");

    // Audit failure must not make a completed operation look failed (Constitution, Auditing).
    private void SafeAudit(Action write)
    {
        try
        {
            write();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit write failed for a Comms-10k replace");
        }
    }
}
