using System.Text.Json;
using ExchangeAdminWeb.Authorization;

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
/// The outcome contract retains its legacy succeeded-with-exceptions value, but an unapproved
/// primary-group member now refuses before clear or mismatches after clear. The outcomes worth
/// an operator's attention are "partly
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
    private readonly SectionAccessService _sectionAccess;
    private readonly ModuleEnablementService _enablement;
    private readonly ILogger<Comms10kReplaceProcessor> _logger;

    // Carried from the resolve stage to the replace stage. Safe because the runner resolves ONE
    // processor instance per job and calls ProcessRowAsync in order on it (BulkJobService, the
    // row loop). A processor shared across jobs would make this a cross-job leak.
    private IReadOnlyList<Comms10kAddressResolver.ResolvedMember>? _resolvedMembers;
    private Comms10kUpdateResult? _result;
    private bool _mutationAudited;

    public Comms10kReplaceProcessor(
        Comms10kService comms,
        AuditService audit,
        EmailService email,
        SectionAccessService sectionAccess,
        ModuleEnablementService enablement,
        ILogger<Comms10kReplaceProcessor> logger)
    {
        _comms = comms;
        _audit = audit;
        _email = email;
        _sectionAccess = sectionAccess;
        _enablement = enablement;
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

        if (rowIndex is ResolveRow or ReplaceRow && AuthorizationDenial(job) is { } denial)
            return Refuse(job, payload, denial);

        return rowIndex switch
        {
            ResolveRow => await ResolveAsync(job, payload),
            ReplaceRow => await ReplaceAsync(job, payload),
            _ => new BulkJobRowOutcome
            {
                Target = payload.SourceFileName ?? "membership",
                Status = BulkJobRowStatus.Failed,
                Message = $"Unexpected stage {rowIndex}.",
            },
        };
    }

    private async Task<BulkJobRowOutcome> ResolveAsync(BulkJob job, Comms10kReplaceJobPayload payload)
    {
        var target = payload.SourceFileName ?? "uploaded list";
        _resolvedMembers = null;
        if (!Comms10kMemberSet.IsSupportedFingerprint(payload.ApprovedMemberFingerprint))
            return Refuse(job, payload, "The approved member fingerprint is missing, malformed or unsupported. Validate the file and resubmit.");

        Comms10kResolveResult resolve;
        try
        {
            resolve = await _comms.ResolveEmailsAsync(payload.Emails);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Comms10k resolution failed for job {JobId}", job.Id);
            return Refuse(job, payload, $"Address resolution failed before any change: {ex.Message}");
        }

        if (!resolve.Success || resolve.Rejections.Count > 0)
        {
            var reason = resolve.Rejections.Count > 0
                ? $"Address validation rejected {resolve.Rejections.Count:N0} address(es). Fix the file and re-upload. {resolve.Message}"
                : resolve.Message;
            return Refuse(job, payload, reason);
        }

        if (resolve.ResolvedMembers.Count == 0)
            return Refuse(job, payload, "No members resolved. An empty list cannot replace the broadcast list.");

        string fingerprint;
        try
        {
            fingerprint = Comms10kMemberSet.Fingerprint(resolve.ResolvedMembers.Select(member => member.ObjectGuid));
        }
        catch (ArgumentException)
        {
            return Refuse(job, payload, "Resolved member identities were invalid. Validate the file and resubmit.");
        }
        if (!string.Equals(fingerprint, payload.ApprovedMemberFingerprint, StringComparison.Ordinal))
            return Refuse(job, payload, "The resolved member set has changed since validation. Nothing was written. Validate the file and resubmit.");

        // Keep GUID/DN pairs together. Only the complete operator-approved GUID set becomes
        // executable state; descriptive DN changes cannot retarget the later member write.
        _resolvedMembers = resolve.ResolvedMembers.ToArray();

        // cdr-followup-1: addresses and distinct members are named separately. Two aliases of one
        // person are two addresses and one member, and "1 of 2 matched in AD" read as a miss. A
        // rejection has already refused above, so every uploaded address is one that resolved.
        return new BulkJobRowOutcome
        {
            Target = target,
            Status = BulkJobRowStatus.Success,
            Message = Comms10kAddressResolver.DescribeResolved(
                payload.Emails.Count - resolve.Rejections.Count, _resolvedMembers.Count, resolve.Rejections.Count),
        };
    }

    private async Task<BulkJobRowOutcome> ReplaceAsync(BulkJob job, Comms10kReplaceJobPayload payload)
    {
        var target = payload.SourceFileName ?? "uploaded list";

        if (_resolvedMembers is null || _resolvedMembers.Count == 0)
        {
            // The resolve stage failed and the runner still reached this row. Refuse rather than
            // writing: an empty or stale set here would CLEAR the broadcast list.
            return Refuse(job, payload, "Addresses were not resolved, so nothing was written.");
        }

        Comms10kUpdateResult result;
        try
        {
            result = await _comms.ExecuteReplaceAsync(
                _resolvedMembers, job.SubmittedBy, () => AuthorizationDenial(job));
        }
        catch (Exception ex)
        {
            // The service converts known preparation failures to refusals. An arbitrary
            // exception here cannot prove that clear was never dispatched.
            _logger.LogError(ex, "Comms10k replace failed with an unconfirmed write state for job {JobId}", job.Id);
            result = new Comms10kUpdateResult
            {
                Success = false,
                Outcome = Comms10kOutcome.CouldNotConfirm,
                Message = $"The replace failed and its write state could not be confirmed: {ex.Message}",
            };
        }
        RecordResult(job, payload, result);

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

    // Re-read the live stores on every call, including after asynchronous write preparation
    // and before each retry. This checks configuration, not Windows-token membership refresh.
    private string? AuthorizationDenial(BulkJob job)
    {
        var snapshot = JobAuthorizationSnapshot.FromJson(job.AuthSnapshotJson);
        if (snapshot is null)
            return "The Comms-10k job authorization snapshot is missing or unreadable.";
        if (!string.Equals(snapshot.Section, ModuleName, StringComparison.Ordinal))
            return "The job authorization snapshot is not for Comms10k.";

        try
        {
            if (!_enablement.IsModuleEnabled(ModuleName))
                return "Comms-10k is currently disabled.";
            var groups = _sectionAccess.GetGroupsForSection(ModuleName)
                .Where(SectionAccessGroupIdentity.IsUsableGroupSid).ToArray();
            // Only an original AuthorizedGroups grant may survive. A newly allowed RoleClaims
            // SID is not a replacement for the decision captured when the job was submitted.
            return snapshot.IsStillAuthorized(groups) ? null
                : "The group that authorized this Comms-10k job is no longer allowed or section access is unavailable.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Comms-10k job authorization could not be verified for {JobId}", job.Id);
            return "Comms-10k job authorization could not be verified.";
        }
    }

    // Also used by the later authorization and approved-set gates. The runner proceeds after
    // a failed row, so preserve the first refusal and emit its mutation audit only once.
    private BulkJobRowOutcome Refuse(BulkJob job, Comms10kReplaceJobPayload payload, string reason)
    {
        var requested = _resolvedMembers?.Count;
        _resolvedMembers = null;
        if (_result is null)
        {
            RecordResult(job, payload, new Comms10kUpdateResult
            {
                Success = false,
                Outcome = Comms10kOutcome.RefusedBeforeAnyChange,
                MembersRequested = requested,
                MembersWritten = 0,
                Message = string.IsNullOrWhiteSpace(reason) ? "Address resolution failed before any change." : reason,
            });
        }

        return new BulkJobRowOutcome
        {
            Target = payload.SourceFileName ?? "uploaded list",
            Status = BulkJobRowStatus.Failed,
            Message = _result!.Message,
        };
    }

    private void RecordResult(BulkJob job, Comms10kReplaceJobPayload payload, Comms10kUpdateResult result)
    {
        result.MembersRequested ??= _resolvedMembers?.Count;
        _result = result;
        if (_mutationAudited)
            return;
        _mutationAudited = true;

        SafeAudit(() => _audit.LogModuleAction(
            job.SubmittedBy, job.SubmittedIp, "Comms10k_Replace", "Comms10k",
            payload.SourceFileName ?? "uploaded list", result.Success, job.Ticket ?? "", result.Success ? null : result.Message,
            // Success discards errorDetail, so the outcome must also ride extra.
            extra: new Dictionary<string, object?>
            {
                ["Outcome"] = result.Outcome.ToString(),
                ["MembersRequested"] = (object?)result.MembersRequested ?? "UNKNOWN - resolved set unavailable",
                ["MembersWritten"] = (object?)result.MembersWritten ?? "UNKNOWN - write completion not confirmed",
                ["WrittenCountBasis"] = "Acknowledged adds; final membership is a separate observation",
                ["FinalCount"] = result.FinalCount?.ToString() ?? "UNKNOWN - no confirmed read-back",
                ["SourceFile"] = payload.SourceFileName,
            }));
    }

    /// <summary>
    /// The admin notification, moved off the browser circuit and into the job - which is the
    /// point of running here at all, since the circuit may be long gone by now.
    /// </summary>
    public async Task OnJobCompletedAsync(BulkJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        var outcome = _result?.Outcome.ToString()
            ?? (job.Status == BulkJobStatus.Interrupted ? "Interrupted" : $"No replace result recorded ({job.Status})");
        var detail = _result?.Message ?? job.Message ?? "No detail was recorded.";
        if (_result is null && job.Status == BulkJobStatus.Interrupted)
            detail += " The replace was interrupted without a recorded result. The group may be empty or partial. Check the group directly before deciding how to recover.";

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
                    ["Members requested"] = _result?.MembersRequested?.ToString() ?? "UNKNOWN - resolved set unavailable",
                    ["Members written (acknowledged adds)"] = _result?.MembersWritten?.ToString() ?? "UNKNOWN - write completion not confirmed",
                    // Never a removal count: clear-then-fill does not read the prior
                    // membership. Write acknowledgement and observed final membership are
                    // separate evidence, and neither establishes what left.
                    ["Final membership"] = _result?.FinalCount?.ToString() ?? "UNKNOWN - no confirmed read-back",
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
