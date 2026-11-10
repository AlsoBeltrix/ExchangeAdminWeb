using System.Text.Json;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// The ConferenceRooms per-row work behind the bulk job runner (docs/BulkJobRunner-Plan.md, Slice 4).
/// The runner owns lifecycle/queue/persistence; this owns "what one row does" - off the browser
/// circuit, using ONLY the submission context captured on the job (submitter, ip, ticket), never any
/// ambient circuit which no longer exists. The runner re-checks the module's enablement and the
/// submitter's captured authorization snapshot before every row, and a row it refuses comes to
/// <see cref="RefuseRow"/>, never here.
///
/// Per row it: (1) binds the row's room on BOTH Finder and Type paths via the
/// shared <see cref="ConferenceRoomBinder"/> (one binding point for the whole module) - the write runs
/// only inside its onBound delegate, so a row with no room to bind never reaches a side effect;
/// (2) opens a CLEAN ROOT trace scope so the row is not nested
/// under leaked async-local context; (3) calls the SAME ConferenceRoomService methods the live page
/// uses (per-row Exchange/AD logic is unchanged); (4) audits per row with the captured
/// actor/ip/ticket. The completion admin notification fires from the job at terminal state.
///
/// Registered scoped so ConferenceRoomService (scoped) can be a dependency; the runner resolves this
/// from a fresh scope per job.
/// </summary>
public sealed class ConferenceRoomBulkProcessor : IBulkJobProcessor
{
    private readonly IConferenceRoomBulkOperations _rooms;
    private readonly ConferenceRoomBinder _roomBinder;
    private readonly OperationTraceService _trace;
    private readonly AuditService _audit;
    private readonly EmailService _email;
    private readonly ILogger<ConferenceRoomBulkProcessor> _logger;
    private readonly IBulkJobCancelRequests _cancelRequests;

    /// <summary>kd-58 A9 (docs/CancellationDepth-Plan.md 3.9). A Room Finder row the operator's cancel stopped before its first write.</summary>
    internal const string CancelledRow = AuditService.CancelledByTheOperator + ". Nothing was written.";

    /// <summary>kd-58 A9 (3.9). A Room Finder row the host stopping stopped there: not the operator's cancel, so Failed.</summary>
    internal const string HostStoppedRow = "The service stopped before this row was written. Nothing was written.";

    public const string ModuleName = "ConferenceRooms";

    public string ModuleId => ModuleName;

    public ConferenceRoomBulkProcessor(
        IConferenceRoomBulkOperations rooms,
        ConferenceRoomBinder roomBinder,
        OperationTraceService trace,
        AuditService audit,
        EmailService email,
        ILogger<ConferenceRoomBulkProcessor> logger,
        IBulkJobCancelRequests cancelRequests)
    {
        _rooms = rooms;
        _roomBinder = roomBinder;
        _trace = trace;
        _audit = audit;
        _email = email;
        _logger = logger;
        _cancelRequests = cancelRequests;
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
        var (target, actionAudit) = RowIdentity(payload, rowIndex);
        var ticket = job.Ticket ?? "";

        // A job with no ticket is refused, row by row, before the room binding and any write. The
        // page refuses a blank ticket itself, but a job is checked again here because this is the
        // writer that runs it.
        if (RefuseWithoutTicket(job, actionAudit, target) is { } noTicket)
            return noTicket;

        // 1) Room binding - on BOTH Finder AND Type paths, through the SHARED ConferenceRoomBinder so
        // the room is bound exactly once per row and no path can write without it. The write (steps
        // 2-4) lives inside onBound, so a row with no room to bind never reaches the trace scope or any
        // side effect (Known Failure Class #1). A refusal is audited here under the _Bulk action with
        // the captured job actor/ip/ticket. No protection check and no operator: protected principals
        // do not apply to Conference Rooms (owner ruling 2026-11-03), so a bulk row needs no principal.
        return await _roomBinder.BindThenRunAsync(target,
            onRefused: refusal =>
            {
                Audit(job, actionAudit, target, success: false, ticket, refusal.AuditDetail);
                return Failed(target, refusal.Message);
            },
            onBound: async room =>
            {
                // 2) Clean-root trace scope + 3) same ConferenceRoomService methods the live page uses,
                // bound to the room the binder bound for THIS row, never the row's string.
                var traceAction = isFinder ? "SetMetadata_Bulk" : "SetType_Bulk";
                using var op = _trace.BeginRootOperation(ModuleName, traceAction, job.SubmittedBy, job.SubmittedIp, target, ticket);
                try
                {
                    RoomOperationResult r = isFinder
                        ? await ApplyFinderRowAsync(payload.FinderRows![rowIndex], room, cancellationToken) // the job's token, as the page's
                        : await ApplyTypeRowAsync(payload.TypeRows![rowIndex], room);

                    op.Complete(r.Success, r.Message);

                    // 4) Per-row audit with captured actor/ip/ticket (same as the live page), plus
                    // the identity written and - only when the service reports one written - the
                    // Active Directory object; `target` stays the row as typed.
                    Audit(job, actionAudit, target, r.Success, ticket, r.Success ? null : r.Message,
                        newValues: isFinder ? FinderNewValues(payload.FinderRows![rowIndex]) : TypeNewValues(payload.TypeRows![rowIndex]),
                        room: room, writtenDirectoryObject: r.WrittenDirectoryObject, membershipConfirmed: r.DirectoryMembershipConfirmed,
                        attributesConfirmed: r.DirectoryAttributesConfirmed);

                    return new BulkJobRowOutcome
                    {
                        Target = target,
                        Status = r.Partial ? BulkJobRowStatus.Partial : (r.Success ? BulkJobRowStatus.Success : BulkJobRowStatus.Failed),
                        // A job shows each row's message and not its steps, so a room-list membership the
                        // on-prem read-back confirmed - not one this row is known to have written - is told
                        // in the message too, in the words the page's step and the audit use; without it the row
                        // reads as any applied add. City/State/Country the read-back found in place after
                        // Set-ADUser reported an error are told the same way, first - the order the steps ran in.
                        Message = string.Join(" ", new[] { r.Message, r.DirectoryAttributesConfirmed, r.DirectoryMembershipConfirmed }.OfType<string>())
                    };
                }
                catch (OperationCanceledException ex) when (isFinder && cancellationToken.IsCancellationRequested)
                {
                    // The job's token stopped the row before its first write - it reaches only the service's
                    // steps before Set-Place (SetRoomMetadataAndListAsync) - so nothing was written, and it is not a row that
                    // failed. The operator's cancel request (the persisted flag, set before the token is signalled) is filed
                    // Cancelled in the app's phrase, under the job's captured actor, address and ticket; the host stopping is
                    // filed Failed in its own words. Either way the cancel goes on to the runner, which stops the batch without
                    // recording the row - as it does for a row the cancel stopped before it started.
                    var byTheOperator = _cancelRequests.IsCancelRequested(job.Id);
                    var said = byTheOperator ? CancelledRow : HostStoppedRow;
                    op.Complete(false, said, ex);
                    Audit(job, actionAudit, target, success: false, ticket, said, room: room, cancelled: byTheOperator);
                    throw;
                }
                catch (Exception ex)
                {
                    op.Complete(false, ex.Message, ex);
                    // No result, so no Active Directory object reported written.
                    Audit(job, actionAudit, target, success: false, ticket, ex.Message, room: room);
                    return Failed(target, ex.Message);
                }
            });
    }

    public BulkJobRowOutcome RefuseRow(BulkJob job, int rowIndex, JobRefusal refusal)
    {
        var (target, actionAudit) = RowIdentity(Deserialize(job), rowIndex);

        // "The module is off" is true regardless of who submitted the job, and saying so is a
        // better audit record than "authorization denied".
        if (refusal == JobRefusal.ModuleDisabled)
        {
            Audit(job, actionAudit, target, success: false, job.Ticket ?? "", "Conference Rooms is currently disabled.");
            return Failed(target, "Conference Rooms is currently disabled.");
        }

        // A job with no ticket says so before it says its access is gone - the page's order.
        if (RefuseWithoutTicket(job, actionAudit, target) is { } noTicket)
            return noTicket;

        Audit(job, actionAudit, target, success: false, job.Ticket ?? "", "Authorization denied (captured snapshot lacks access).");
        return Failed(target, "Authorization denied.");
    }

    public async Task OnJobCompletedAsync(BulkJob job)
    {
        // Completion admin notification fires from the job (moves the email off the closed circuit).
        // Fail-safe: the runner has already persisted the terminal state and swallows any throw here.
        var action = job.JobType == ConferenceRoomJobPayload.FinderJobType
            ? "ConferenceRooms_SetMetadata_Bulk"
            : "ConferenceRooms_SetType_Bulk";
        // Success only when the job ran to completion AND every row fully succeeded - a partial row
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
    // Per-row application - delegates to the unchanged ConferenceRoomService methods.
    // -------------------------------------------------------------------------

    private async Task<RoomOperationResult> ApplyFinderRowAsync(FinderCsvRow row, ValidatedMailbox room, CancellationToken cancellationToken) =>
        await _rooms.SetRoomMetadataAndListAsync(
            row.Email, room, row.City, row.Building, row.Capacity,
            row.Floor, row.FloorLabel, row.DisplayDeviceName, row.VideoDeviceName,
            row.CountryOrRegion, row.State, row.TimeZone, cancellationToken);

    private async Task<RoomOperationResult> ApplyTypeRowAsync(TypeCsvRow row, ValidatedMailbox room)
    {
        // aud-a2y. Refused before the service is called, so it says nothing was written, as every refusal the service
        // decides before a write does (aud-a2w). kd-28: by the type's name only - Enum.TryParse took "4" for CEO.
        if (!RoomTypeNames.TryParse(row.Type, ignoreCase: true, out var roomType))
            return new RoomOperationResult { Email = row.Email, Success = false, Message = $"Invalid type: {row.Type}. Nothing was written." };

        return await _rooms.SetRoomTypeAsync(
            row.Email, room, roomType, row.TimeZone,
            row.Site, string.IsNullOrWhiteSpace(row.Arbiter) ? null : row.Arbiter,
            row.RemoveExistingPermissions);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static (string Target, string AuditAction) RowIdentity(ConferenceRoomJobPayload payload, int rowIndex) =>
        payload.Kind == ConferenceRoomJobPayload.FinderJobType
            ? (payload.FinderRows![rowIndex].Email, "ConferenceRooms_SetMetadata_Bulk")
            : (payload.TypeRows![rowIndex].Email, "ConferenceRooms_SetType_Bulk");

    // A job with no ticket is refused row by row, with the page's own check, and its audit row says
    // nothing was attempted. Null when the job carries a ticket.
    private BulkJobRowOutcome? RefuseWithoutTicket(BulkJob job, string actionAudit, string target)
    {
        if (ConferenceRoomService.RequireTicket(job.Ticket) is not { } missingTicket)
            return null;

        var refusal = $"Ticket validation failed: {missingTicket.Message}. Nothing was written.";
        try
        {
            _audit.LogConferenceRoomAction(job.SubmittedBy, job.SubmittedIp, actionAudit, target, false, job.Ticket ?? "",
                errorDetail: refusal, extra: ConferenceRoomService.WithRefusal(null, ConferenceRoomService.TicketRefusalStep));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit failed for bulk job {Job} target {Target}", job.Id, target);
        }

        return Failed(target, refusal);
    }

    private void Audit(BulkJob job, string action, string target, bool success, string ticket,
        string? error = null, Dictionary<string, object?>? newValues = null, ValidatedMailbox? room = null,
        string? writtenDirectoryObject = null, string? membershipConfirmed = null, string? attributesConfirmed = null,
        bool cancelled = false)
    {
        try
        {
            // The identity the row's writes were bound to (aud-a2d), and the Active Directory
            // object the service reports it wrote, when it wrote one (aud-a2g). That used to be
            // recorded on every Finder row, whether or not Active Directory was written. And a
            // room-list membership confirmed without this row's write, in that object's place
            // (aud-a2n) - added as the page adds it, through the same helper - and City/State/Country
            // confirmed in place after a write error, likewise (aud-a2q).
            _audit.LogConferenceRoomAction(job.SubmittedBy, job.SubmittedIp, action, target, success, ticket,
                errorDetail: error, newValues: newValues,
                extra: Filed(ConferenceRoomService.WithAttributesConfirmed(
                    ConferenceRoomService.WithMembershipConfirmed(ValidatedMailbox.WithWrittenIdentity(null, room, writtenDirectoryObject), membershipConfirmed),
                    attributesConfirmed), cancelled));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Audit failed for bulk job {Job} target {Target}", job.Id, target);
        }
    }

    // kd-58 A9. A row's extra, filed Cancelled when the operator's cancel stopped it (kd-57's FiledCancelled).
    private static Dictionary<string, object?>? Filed(Dictionary<string, object?>? extra, bool cancelled) =>
        cancelled ? AuditService.FiledCancelled(extra) : extra;

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
