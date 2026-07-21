using System.Text.Json;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// The ConferenceRooms per-row work behind the bulk job runner (docs/BulkJobRunner-Plan.md, Slice 4).
/// The runner owns lifecycle/queue/persistence; this owns "what one row does" — off the browser
/// circuit, using ONLY the submission context captured on the job (submitter, ip, ticket, auth
/// snapshot), never any ambient circuit which no longer exists.
///
/// Per row it: (1) re-checks the submitter's captured authorization snapshot against the section's
/// current allowed groups (option (a)); (2) enforces the protected-principal gate on BOTH Finder and
/// Type paths via the shared <see cref="ConferenceRoomProtectionGate"/> (one enforcement point for
/// the whole module) — the write runs only inside the gate's onAllowed delegate, so a protected
/// target never reaches a side effect; (3) opens a CLEAN ROOT trace scope so the row is not nested
/// under leaked async-local context; (4) calls the SAME ConferenceRoomService methods the live page
/// uses (per-row Exchange/AD logic is unchanged); (5) audits per row with the captured
/// actor/ip/ticket. The completion admin notification fires from the job at terminal state.
///
/// Registered scoped so ConferenceRoomService (scoped) can be a dependency; the runner resolves this
/// from a fresh scope per job.
/// </summary>
public sealed class ConferenceRoomBulkProcessor : IBulkJobProcessor
{
    private readonly IConferenceRoomBulkOperations _rooms;
    private readonly ConferenceRoomProtectionGate _protectionGate;
    private readonly SectionAccessService _sectionAccess;
    private readonly OperationTraceService _trace;
    private readonly AuditService _audit;
    private readonly EmailService _email;
    private readonly ILogger<ConferenceRoomBulkProcessor> _logger;

    public const string ModuleName = "ConferenceRooms";
    private const string Section = "ConferenceRooms";

    public string ModuleId => ModuleName;

    public ConferenceRoomBulkProcessor(
        IConferenceRoomBulkOperations rooms,
        ConferenceRoomProtectionGate protectionGate,
        SectionAccessService sectionAccess,
        OperationTraceService trace,
        AuditService audit,
        EmailService email,
        ILogger<ConferenceRoomBulkProcessor> logger)
    {
        _rooms = rooms;
        _protectionGate = protectionGate;
        _sectionAccess = sectionAccess;
        _trace = trace;
        _audit = audit;
        _email = email;
        _logger = logger;
    }

    public int CountRows(BulkJob job)
    {
        var payload = Deserialize(job);
        return payload.Kind == ConferenceRoomJobPayload.FinderJobType
            ? payload.FinderRows?.Count ?? 0
            : payload.TypeRows?.Count ?? 0;
    }

    public async Task<BulkJobRowOutcome> ProcessRowAsync(BulkJob job, int rowIndex, CancellationToken cancellationToken)
    {
        var payload = Deserialize(job);
        var isFinder = payload.Kind == ConferenceRoomJobPayload.FinderJobType;
        var target = isFinder
            ? payload.FinderRows![rowIndex].Email
            : payload.TypeRows![rowIndex].Email;
        var ticket = job.Ticket ?? "";
        var actionAudit = isFinder ? "ConferenceRooms_SetMetadata_Bulk" : "ConferenceRooms_SetType_Bulk";

        // 1) Off-circuit authorization re-check (option (a)): the submitter's captured claims must
        // still satisfy the section's allowed groups. Fail closed — a job whose snapshot lacks
        // access is denied on every row and audited, never processed.
        var snapshot = JobAuthorizationSnapshot.FromJson(job.AuthSnapshotJson);
        var allowed = _sectionAccess.GetGroupsForSection(Section);
        if (snapshot is null || !snapshot.IsStillAuthorized(allowed))
        {
            Audit(job, actionAudit, target, success: false, ticket, "Authorization denied (captured snapshot lacks access).");
            return Failed(target, "Authorization denied.");
        }

        // 2) Protected-principal gate — enforced on BOTH Finder AND Type paths (no carve-out;
        // owner 2026-07-02), through the SHARED ConferenceRoomProtectionGate so the check runs
        // exactly once per row and no path can write without it. The write (steps 3-5) lives inside
        // onAllowed, so a protected/fail-closed target never reaches the trace scope or any side
        // effect (Known Failure Class #1). A denial is audited here under the _Bulk action with the
        // captured job actor/ip/ticket.
        return await _protectionGate.GuardThenRunAsync(target,
            onDenied: denial =>
            {
                Audit(job, actionAudit, target, success: false, ticket, denial.AuditDetail);
                return Failed(target, denial.Message);
            },
            onAllowed: async () =>
            {
                // 3) Clean-root trace scope + 4) same ConferenceRoomService methods the live page uses.
                var traceAction = isFinder ? "SetMetadata_Bulk" : "SetType_Bulk";
                using var op = _trace.BeginRootOperation(ModuleName, traceAction, job.SubmittedBy, job.SubmittedIp, target, ticket);
                try
                {
                    RoomOperationResult r = isFinder
                        ? await ApplyFinderRowAsync(payload.FinderRows![rowIndex])
                        : await ApplyTypeRowAsync(payload.TypeRows![rowIndex]);

                    op.Complete(r.Success, r.Message);

                    // 5) Per-row audit with captured actor/ip/ticket (same as the live page).
                    Audit(job, actionAudit, target, r.Success, ticket, r.Success ? null : r.Message,
                        newValues: isFinder ? FinderNewValues(payload.FinderRows![rowIndex]) : TypeNewValues(payload.TypeRows![rowIndex]));

                    return new BulkJobRowOutcome
                    {
                        Target = target,
                        Status = r.Partial ? BulkJobRowStatus.Partial : (r.Success ? BulkJobRowStatus.Success : BulkJobRowStatus.Failed),
                        Message = r.Message
                    };
                }
                catch (Exception ex)
                {
                    op.Complete(false, ex.Message, ex);
                    Audit(job, actionAudit, target, success: false, ticket, ex.Message);
                    return Failed(target, ex.Message);
                }
            });
    }

    public async Task OnJobCompletedAsync(BulkJob job)
    {
        // Completion admin notification fires from the job (moves the email off the closed circuit).
        // Fail-safe: the runner has already persisted the terminal state and swallows any throw here.
        var action = job.JobType == ConferenceRoomJobPayload.FinderJobType
            ? "ConferenceRooms_SetMetadata_Bulk"
            : "ConferenceRooms_SetType_Bulk";
        // Success only when the job ran to completion AND every row fully succeeded — a partial row
        // is not a success (matches the live page, which reports bulk success only when all rows
        // succeed). A cancelled/interrupted job, or any failed/partial row, notifies as not-success.
        var success = job.Status == BulkJobStatus.Completed && job.FailedCount == 0 && job.PartialCount == 0;
        var summary =
            $"{job.SuccessCount} succeeded, {job.PartialCount} partial, {job.FailedCount} failed " +
            $"of {job.TotalRows} ({job.Status}).";

        await _email.SendAdminNotificationAsync(
            job.SubmittedBy, job.SubmittedIp, action, success, job.Ticket ?? "",
            new Dictionary<string, string>
            {
                ["Rows"] = $"{job.TotalRows}",
                ["Result"] = summary,
                ["JobId"] = job.Id
            },
            success ? null : summary);
    }

    // -------------------------------------------------------------------------
    // Per-row application — delegates to the unchanged ConferenceRoomService methods.
    // -------------------------------------------------------------------------

    private async Task<RoomOperationResult> ApplyFinderRowAsync(FinderCsvRow row) =>
        await _rooms.SetRoomMetadataAndListAsync(
            row.Email, row.City, row.Building, row.Capacity,
            row.Floor, row.FloorLabel, row.DisplayDeviceName, row.VideoDeviceName,
            row.CountryOrRegion, row.State, row.TimeZone);

    private async Task<RoomOperationResult> ApplyTypeRowAsync(TypeCsvRow row)
    {
        if (!Enum.TryParse<RoomType>(row.Type, ignoreCase: true, out var roomType))
            return new RoomOperationResult { Email = row.Email, Success = false, Message = $"Invalid type: {row.Type}" };

        return await _rooms.SetRoomTypeAsync(
            row.Email, roomType, row.TimeZone,
            row.Site, string.IsNullOrWhiteSpace(row.Arbiter) ? null : row.Arbiter,
            row.RemoveExistingPermissions);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void Audit(BulkJob job, string action, string target, bool success, string ticket,
        string? error = null, Dictionary<string, object?>? newValues = null)
    {
        try
        {
            _audit.LogConferenceRoomAction(job.SubmittedBy, job.SubmittedIp, action, target, success, ticket,
                errorDetail: error, newValues: newValues);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit failed for bulk job {Job} target {Target}", job.Id, target);
        }
    }

    private static BulkJobRowOutcome Failed(string target, string message) =>
        new() { Target = target, Status = BulkJobRowStatus.Failed, Message = message };

    private static Dictionary<string, object?> FinderNewValues(FinderCsvRow row) => new()
    {
        ["city"] = row.City,
        ["building"] = row.Building,
        ["capacity"] = row.Capacity,
        ["floor"] = row.Floor,
        ["floorLabel"] = row.FloorLabel,
        ["timezone"] = row.TimeZone,
        ["countryOrRegion"] = row.CountryOrRegion,
        ["state"] = row.State
    };

    private static Dictionary<string, object?> TypeNewValues(TypeCsvRow row) => new()
    {
        ["roomType"] = row.Type,
        ["timezone"] = row.TimeZone,
        ["site"] = row.Site
    };

    private static ConferenceRoomJobPayload Deserialize(BulkJob job)
    {
        var payload = JsonSerializer.Deserialize<ConferenceRoomJobPayload>(job.PayloadJson)
            ?? throw new InvalidOperationException("ConferenceRooms job payload is empty or invalid.");
        return payload;
    }
}
