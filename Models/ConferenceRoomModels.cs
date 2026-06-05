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
    public bool RoomResolved { get; set; }
    public string? ResolveError { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public class RoomTypePreviewRow
{
    public int RowIndex { get; set; }
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public RoomType Type { get; set; }
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

public class RoomOperationResult
{
    public string Email { get; set; } = "";
    public bool Success { get; set; }
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
