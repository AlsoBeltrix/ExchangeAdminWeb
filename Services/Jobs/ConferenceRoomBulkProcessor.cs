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
/// Type paths — closing the pre-existing Finder gap (GAP 3); (3) opens a CLEAN ROOT trace scope so
/// the row is not nested under leaked async-local context; (4) calls the SAME ConferenceRoomService
/// methods the live page uses (per-row Exchange/AD logic is unchanged); (5) audits per row with the
/// captured actor/ip/ticket. The completion admin notification fires from the job at terminal state.
///
/// Registered scoped so ConferenceRoomService (scoped) can be a dependency; the runner resolves this
/// from a fresh scope per job.
/// </summary>
public sealed class ConferenceRoomBulkProcessor : IBulkJobProcessor
{
    private readonly IConferenceRoomBulkOperations _rooms;
    private readonly ProtectedPrincipalService _protectedPrincipals;
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
        ProtectedPrincipalService protectedPrincipals,
        SectionAccessService sectionAccess,
        OperationTraceService trace,
        AuditService audit,
        EmailService email,
        ILogger<ConferenceRoomBulkProcessor> logger)
    {
        _rooms = rooms;
        _protectedPrincipals = protectedPrincipals;
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
        // owner 2026-07-02). This is the in-job home of the check, closing the pre-existing Finder
        // gap. Fail closed on Unavailable/Ambiguous/CheckFailed/exception, audited as a denial.
        var ppDenial = await CheckProtectedPrincipalAsync(job, actionAudit, target, ticket);
        if (ppDenial is not null)
            return ppDenial;

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
    // Protected-principal gate (mirrors the page's CheckProtectedPrincipalAsync, off-circuit).
    // -------------------------------------------------------------------------

    private async Task<BulkJobRowOutcome?> CheckProtectedPrincipalAsync(BulkJob job, string action, string identity, string ticket)
    {
        try
        {
            var (resolved, status) = await _protectedPrincipals.ResolveWithStatusAsync(identity);
            if (status is ProtectedPrincipalService.ResolutionStatus.Unavailable or ProtectedPrincipalService.ResolutionStatus.Ambiguous)
            {
                var reason = status == ProtectedPrincipalService.ResolutionStatus.Ambiguous
                    ? "Identity is ambiguous — matches multiple AD users."
                    : "Protection check unavailable.";
                Audit(job, action, identity, success: false, ticket, $"{reason} — blocked");
                return Failed(identity, reason);
            }
            if (resolved != null)
            {
                var check = await _protectedPrincipals.CheckAsync(resolved);
                if (check.CheckFailed)
                {
                    Audit(job, action, identity, success: false, ticket, $"Protection check failed: {check.Reason}");
                    return Failed(identity, $"Protection check failed: {check.Reason}");
                }
                if (check.IsProtected)
                {
                    Audit(job, action, identity, success: false, ticket, $"Protected principal — matched rules: {string.Join(", ", check.MatchedRules)}");
                    return Failed(identity, "This is a protected principal. Operation not permitted.");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Protected principal check failed for {Identity} — blocking as precaution", identity);
            Audit(job, action, identity, success: false, ticket, $"Protection check exception: {ex.Message}");
            return Failed(identity, $"Protection check error: {ex.Message}");
        }
        return null;
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
