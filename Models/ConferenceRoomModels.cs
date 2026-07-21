namespace ExchangeAdminWeb.Models;

public class RoomInfo
{
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string City { get; set; } = "";
    public string Building { get; set; } = "";
    public int Capacity { get; set; }
    public string Floor { get; set; } = "";
    public string FloorLabel { get; set; } = "";
    public string DisplayDeviceName { get; set; } = "";
    public string VideoDeviceName { get; set; } = "";
    public string CountryOrRegion { get; set; } = "";
    public string State { get; set; } = "";
    public string TimeZone { get; set; } = "";
    public string? CustomAttribute9 { get; set; }
    public string? MailTip { get; set; }
    public string RecipientTypeDetails { get; set; } = "";
}

public enum RoomType
{
    Standard,
    Video,
    Restricted,
    Exception,
    CEO,
    Executive
}

public class RoomTypeDefinition
{
    public RoomType Type { get; init; }
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
    public bool AllBookInPolicy { get; init; }
    public string[] BookInPolicyGroups { get; init; } = [];
    public bool AddAdditionalResponse { get; init; }
    public string AdditionalResponse { get; init; } = "";
    public string? MailTip { get; init; }
    public int BookingWindowInDays { get; init; } = 180;
    public bool ProcessExternalMeetingMessages { get; init; }
    public bool ForceRemoveExistingPermissions { get; init; }
    public string DefaultAccessRights { get; init; } = "LimitedDetails";
    public string AnonymousAccessRights { get; init; } = "AvailabilityOnly";
    public CalendarPermissionEntry[] AdditionalPermissions { get; init; } = [];
}

public class CalendarPermissionEntry
{
    public string GroupConfigKey { get; set; } = "";
    public string FallbackAddress { get; set; } = "";
    public string AccessRights { get; set; } = "";
}

public class RoomFinderPreviewRow
{
    public int RowIndex { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string City { get; set; } = "";
    public string Building { get; set; } = "";
    public int Capacity { get; set; }
    public string Floor { get; set; } = "";
    public string FloorLabel { get; set; } = "";
    public string DisplayDeviceName { get; set; } = "";
    public string VideoDeviceName { get; set; } = "";
    public string CountryOrRegion { get; set; } = "";
    public string State { get; set; } = "";
    public string TimeZone { get; set; } = "";
    public string RoomListName { get; set; } = "";
    public bool RoomListExists { get; set; }
    public bool IsLegacyRoomList { get; set; }
    // Set when a stray city-named list (e.g. "Durham Conference Rooms") exists that differs
    // from the building-named target. Drives an operator warning; nothing is auto-deleted.
    public string? StrayCityRoomListName { get; set; }
    public bool RoomResolved { get; set; }
    public string? ResolveError { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public class RoomTypePreviewRow
{
    public int RowIndex { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    // Nullable: a preview row built for a parse/validation failure (skipped row,
    // invalid type, missing timezone) has no type. Without nullability the enum
    // defaulted to its first value, so failed rows rendered a phantom "Standard"
    // in the preview while the Status column said the type was empty/invalid.
    public RoomType? Type { get; set; }
    public string TimeZone { get; set; } = "";
    public string Site { get; set; } = "none";
    public string Arbiter { get; set; } = "";
    public bool RemoveExistingPermissions { get; set; }
    public string MailTip { get; set; } = "";
    public string AdditionalResponse { get; set; } = "";
    public string CustomAttribute9Tag { get; set; } = "";
    public string DefaultCalPermission { get; set; } = "";
    public string AnonymousCalPermission { get; set; } = "";
    public List<string> BookInPolicyGroups { get; set; } = [];
    public List<CalendarPermissionPreview> CalendarPermissions { get; set; } = [];
    public bool RoomResolved { get; set; }
    public string? ResolveError { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public class CalendarPermissionPreview
{
    public string User { get; set; } = "";
    public string AccessRights { get; set; } = "";
}

// -----------------------------------------------------------------------------
// CSV bulk-upload row models + parse results
//
// Email selection mirrors the working SetupRoomType.ps1 reference: take the
// first non-blank identifier column and hand it to Exchange (Get-Mailbox), which
// resolves SMTP, alias, DN, or canonical name. We do NOT require an '@' - real
// Exchange exports put a non-SMTP canonical name in the Identity column, and
// rejecting those was the cause of the "no Apply button" bug (rows silently
// dropped -> empty preview). See docs/ConferenceRooms-CsvFix-Plan.md.
// -----------------------------------------------------------------------------

public class FinderCsvRow
{
    public string Email { get; set; } = "";
    public string City { get; set; } = "";
    public string CountryOrRegion { get; set; } = "";
    public string State { get; set; } = "";
    public string Building { get; set; } = "";
    public int Capacity { get; set; } = 1;
    public string Floor { get; set; } = "";
    public string FloorLabel { get; set; } = "";
    public string DisplayDeviceName { get; set; } = "";
    public string VideoDeviceName { get; set; } = "";
    public string TimeZone { get; set; } = "";
}

public class TypeCsvRow
{
    public string Email { get; set; } = "";
    public string Type { get; set; } = "";
    public string TimeZone { get; set; } = "";
    public string Site { get; set; } = "none";
    public string Arbiter { get; set; } = "";
    public bool RemoveExistingPermissions { get; set; }
}

/// <summary>
/// One CSV data row's parse outcome: either a populated <see cref="Row"/>, or a
/// skip with a human-readable <see cref="SkipReason"/> (the row is never silently
/// dropped). <see cref="AvailableColumns"/> lists the header names found, to help
/// the user diagnose a wrong/missing identity column.
/// </summary>
public class FinderCsvParseResult
{
    public int RowIndex { get; set; }
    public FinderCsvRow? Row { get; set; }
    public string? SkipReason { get; set; }
    public List<string> AvailableColumns { get; set; } = [];
    public bool Skipped => Row == null;
}

/// <inheritdoc cref="FinderCsvParseResult"/>
public class TypeCsvParseResult
{
    public int RowIndex { get; set; }
    public TypeCsvRow? Row { get; set; }
    public string? SkipReason { get; set; }
    public List<string> AvailableColumns { get; set; } = [];
    public bool Skipped => Row == null;
}

public class RoomOperationResult
{
    public string Email { get; set; } = "";
    public bool Success { get; set; }
    // True when an earlier mutating step already committed but a later step failed, so the
    // room is left half-configured (e.g. Set-Place wrote EXO metadata, then the room-list or
    // AD step failed). Distinct from Success=false with no writes. Two-system, multi-step
    // applies are not transactional; the operator/audit record must show the real state
    // rather than a plain failure. Re-running the row is safe (every step is idempotent).
    public bool Partial { get; set; }
    public string Message { get; set; } = "";
    public List<RoomOperationStep> Steps { get; set; } = [];
    public bool AdAttributeFixRequired { get; set; }
    public string? AdAttributeDetail { get; set; }
}

public class RoomOperationStep
{
    public string Stage { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
}
