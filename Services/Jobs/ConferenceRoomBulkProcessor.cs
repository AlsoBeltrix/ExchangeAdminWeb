using System.Text.Json;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services.Jobs;

/// <summary>
/// The ConferenceRooms per-row work behind the bulk job runner (docs/BulkJobRunner-Plan.md, Slice 4).
/// The runner owns lifecycle/queue/persistence; this owns "what one row does" - off the browser
/// circuit, using ONLY the submission context captured on the job (submitter, ip, ticket, auth
/// snapshot), never any ambient circuit which no longer exists.
///
/// Per row it: (1) re-checks the submitter's captured authorization snapshot against the section's
/// current allowed groups (option (a)); (2) enforces the protected-principal gate on BOTH Finder and
/// Type paths via the shared <see cref="ConferenceRoomProtectionGate"/> (one enforcement point for
/// the whole module) - the write runs only inside the gate's onAllowed delegate, so a protected
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
    private readonly ModuleEnablementService _enablement;
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
        ModuleEnablementService enablement,
        OperationTraceService trace,
        AuditService audit,
        EmailService email,
        ILogger<ConferenceRoomBulkProcessor> logger)
    {
        _rooms = rooms;
        _protectionGate = protectionGate;
        _sectionAccess = sectionAccess;
        _enablement = enablement;
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
        // still satisfy the section's allowed groups. Fail closed - a job whose snapshot lacks
        // access is denied on every row and audited, never processed.
        // Audit finding A8. This checked the section's allowed groups and nothing else, so a
        // DISABLED Conference Rooms module kept changing rooms: queued jobs and every
        // subsequent row ran on, because live authorization rejects a disabled module and this
        // off-circuit path never asked. Same gap Comms-10k closed as aud-c2, same shape, so
        // this reuses it rather than inventing a second one.
        //
        // Checked before the snapshot: "the module is off" is true regardless of who submitted
        // the job, and saying so is a better audit record than "authorization denied".
        if (!_enablement.IsModuleEnabled(ModuleName))
        {
            Audit(job, actionAudit, target, success: false, ticket, "Conference Rooms is currently disabled.");
            return Failed(target, "Conference Rooms is currently disabled.");
        }

        var snapshot = JobAuthorizationSnapshot.FromJson(job.AuthSnapshotJson);
        var allowed = _sectionAccess.GetGroupsForSection(Section);
        if (snapshot is null || !snapshot.IsStillAuthorized(allowed))
        {
            Audit(job, actionAudit, target, success: false, ticket, "Authorization denied (captured snapshot lacks access).");
            return Failed(target, "Authorization denied.");
        }

        // 2) Protected-principal gate - enforced on BOTH Finder AND Type paths (no carve-out;
        // owner 2026-07-02), through the SHARED ConferenceRoomProtectionGate so the check runs
        // exactly once per row and no path can write without it. The write (steps 3-5) lives inside
        // onAllowed, so a protected/fail-closed target never reaches the trace scope or any side
        // effect (Known Failure Class #1). A denial is audited here under the _Bulk action with the
        // captured job actor/ip/ticket.
        // NO SERVICER BYPASS HERE, and the null is explicit rather than incidental. A job runs
        // off-circuit: the submitting operator's circuit is gone, and this row is executing later
        // under no one. The job record carries SubmittedBy as a STRING, which cannot answer
        // "is this principal in the servicing group" - IsInRole needs a real token, and
        // reconstructing one from a name would be inventing an identity. So a protected room in a
        // bulk CSV still refuses, whoever submitted it, and the operator re-runs that row
        // interactively where their own token is present.
        return await _protectionGate.GuardThenRunAsync(target, user: null,
            onDenied: denial =>
            {
                Audit(job, actionAudit, target, success: false, ticket, denial.AuditDetail);
                return Failed(target, denial.Message);
            },
            onAllowed: async (_, room) =>
            {
                // 3) Clean-root trace scope + 4) same ConferenceRoomService methods the live page uses,
                // bound to the room the gate validated for THIS row (aud-a2d), never the row's string.
                var traceAction = isFinder ? "SetMetadata_Bulk" : "SetType_Bulk";
                using var op = _trace.BeginRootOperation(ModuleName, traceAction, job.SubmittedBy, job.SubmittedIp, target, ticket);
                try
                {
                    RoomOperationResult r = isFinder
                        ? await ApplyFinderRowAsync(payload.FinderRows![rowIndex], room)
                        : await ApplyTypeRowAsync(payload.TypeRows![rowIndex], room);

                    op.Complete(r.Success, r.Message);

                    // 5) Per-row audit with captured actor/ip/ticket (same as the live page), plus
                    // the identity written and - only when the service reports one written - the
                    // Active Directory object (aud-a2g); `target` stays the row as typed.
                    Audit(job, actionAudit, target, r.Success, ticket, r.Success ? null : r.Message,
                        newValues: isFinder ? FinderNewValues(payload.FinderRows![rowIndex]) : TypeNewValues(payload.TypeRows![rowIndex]),
                        room: room, writtenDirectoryObject: r.WrittenDirectoryObject, membershipConfirmed: r.DirectoryMembershipConfirmed,
                        attributesConfirmed: r.DirectoryAttributesConfirmed);

                    return new BulkJobRowOutcome
                    {
                        Target = target,
                        Status = r.Partial ? BulkJobRowStatus.Partial : (r.Success ? BulkJobRowStatus.Success : BulkJobRowStatus.Failed),
                        // aud-a2o. A job shows each row's message and not its steps, so a room-list membership the
                        // on-prem read-back confirmed - not one this row is known to have written (aud-a2n) - is told
                        // in the message too, in the words the page's step and the audit use; without it the row
                        // read as any applied add. aud-a2q: City/State/Country the read-back found in place after
                        // Set-ADUser reported an error are told the same way, first - the order the steps ran in.
                        Message = string.Join(" ", new[] { r.Message, r.DirectoryAttributesConfirmed, r.DirectoryMembershipConfirmed }.OfType<string>())
                    };
                }
                catch (Exception ex)
                {
                    op.Complete(false, ex.Message, ex);
                    // No result, so no Active Directory object reported written (aud-a2g).
                    Audit(job, actionAudit, target, success: false, ticket, ex.Message, room: room);
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

    // aud-a2e. No operator, explicitly, for the room list's write-target check as for the room gate above:
    // a protected room list in a bulk CSV refuses whoever submitted it, and no servicer note can arise for
    // the row's audit to record.
    private async Task<RoomOperationResult> ApplyFinderRowAsync(FinderCsvRow row, ValidatedMailbox room) =>
        await _rooms.SetRoomMetadataAndListAsync(
            row.Email, room, actingUser: null, row.City, row.Building, row.Capacity,
            row.Floor, row.FloorLabel, row.DisplayDeviceName, row.VideoDeviceName,
            row.CountryOrRegion, row.State, row.TimeZone);

    private async Task<RoomOperationResult> ApplyTypeRowAsync(TypeCsvRow row, ValidatedMailbox room)
    {
        // aud-a2y. Refused before the service is called, so it says nothing was written, as every refusal the service
        // decides before a write does (aud-a2w).
        if (!Enum.TryParse<RoomType>(row.Type, ignoreCase: true, out var roomType))
            return new RoomOperationResult { Email = row.Email, Success = false, Message = $"Invalid type: {row.Type}. Nothing was written." };

        return await _rooms.SetRoomTypeAsync(
            row.Email, room, roomType, row.TimeZone,
            row.Site, string.IsNullOrWhiteSpace(row.Arbiter) ? null : row.Arbiter,
            row.RemoveExistingPermissions);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private void Audit(BulkJob job, string action, string target, bool success, string ticket,
        string? error = null, Dictionary<string, object?>? newValues = null, ValidatedMailbox? room = null,
        string? writtenDirectoryObject = null, string? membershipConfirmed = null, string? attributesConfirmed = null)
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
                extra: ConferenceRoomService.WithAttributesConfirmed(
                    ConferenceRoomService.WithMembershipConfirmed(ValidatedMailbox.WithWrittenIdentity(null, room, writtenDirectoryObject), membershipConfirmed),
                    attributesConfirmed));
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
