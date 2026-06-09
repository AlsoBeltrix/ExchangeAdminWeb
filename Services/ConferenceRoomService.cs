using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using CsvHelper;
using CsvHelper.Configuration;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class ConferenceRoomService : ExchangeServiceBase
{
    private readonly ModuleConfigService _moduleConfig;
    private const string ModuleId = "ConferenceRooms";

    public ConferenceRoomService(
        ExoConnectionPool exoPool,
        DelineaService delineaService,
        ModuleConfigService moduleConfig,
        ModuleCredentialService moduleCredentials,
        OperationTraceService operationTrace,
        IConfiguration config,
        ILogger<ConferenceRoomService> logger)
        : base(exoPool, delineaService, logger,
               config["OnPremExchange:ServerUri"] ?? "",
               moduleCredentials,
               ModuleId,
               operationTrace)
    {
        _moduleConfig = moduleConfig;
    }

    // -------------------------------------------------------------------------
    // Config helpers — all group addresses come from module config with fallbacks
    // -------------------------------------------------------------------------

    private string Cfg(string key, string fallback = "")
        => _moduleConfig.GetValue(ModuleId, key) ?? fallback;

    private string ArbiterGroup => Cfg("DefaultArbiterGroup", "ConfSiteAdmins@analog.com");
    private string ExecCoordinatorsGroup => Cfg("ExecConfCoordinatorsGroup", "ExecConfCoordinators@analog.com");
    private string ExecAdminsGroup => Cfg("ConfExecAdminsGroup", "ConfExecAdmins@analog.com");
    private string ExecVPsGroup => Cfg("ConfExecVPsGroup", "ConfExecVPs@analog.com");
    private string ConfAdminsGroup => Cfg("ConfAdminsGroup", "ConfAdmins@analog.com");
    private string CeoGroup => Cfg("ConfCEOGroup", "ConfCEO@analog.com");
    private string ExceptionGroup => Cfg("ConfExceptionGroup", "ConfException@analog.com");
    private string AdgtAdminsGroup => Cfg("ADGTAdminsGroup", "ADGTMeetingRoomAdmins@analog.com");
    private string RoomListOU => Cfg("RoomListOU", "ad.analog.com/Exchange/Analog/Recipients/Conference Rooms");

    private string RestrictedMailTip => Cfg("RestrictedMailTip",
        $"This is a restricted room. Email {Cfg("RestrictedContactEmail", "confadmins@analog.com")} for assistance with booking this room.");
    private string ExecMailTip => Cfg("ExecMailTip",
        $"Only exec admins may book this room.");
    private string AdgtRestrictedMailTip =>
        $"This is a restricted room. Email {Cfg("ADGTContactEmail", "adgtmeetingroomadmins@analog.com")} if you need assistance with booking this room.";

    private string ExecRoomResponse =>
        $"This is an executive only room. Email {Cfg("ExecContactEmail", "confexecadmins@analog.com")} if you need assistance.";
    private string RestrictedRoomResponse =>
        $"This is a restricted room. Email {Cfg("RestrictedContactEmail", "confadmins@analog.com")} for assistance with booking this room.";
    private string AdgtRestrictedRoomResponse =>
        $"For room requests, please send complete details to {Cfg("ADGTContactEmail", "adgtmeetingroomadmins@analog.com")} (Note: Reservations are only allowed 14 days before the event date - subject to availability.)";
    private const string VideoRoomMailTip = "This is a video conference room.";
    private const string VideoRoomResponse = "This is a video conference room.";
    private const string CeoRoomResponse = "This is an executive only room.";

    // -------------------------------------------------------------------------
    // Resolve / Preview
    // -------------------------------------------------------------------------

    public async Task<RoomInfo?> GetRoomInfoAsync(string roomEmail)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            ps.AddCommand("Get-Mailbox")
              .AddParameter("Identity", roomEmail)
              .AddParameter("ErrorAction", "Stop");
            var mbxResults = Invoke(ps, tracker);
            var mbx = mbxResults.FirstOrDefault();
            if (mbx == null) return null;

            ps.AddCommand("Get-Place")
              .AddParameter("Identity", roomEmail)
              .AddParameter("ErrorAction", "SilentlyContinue");
            var placeResults = InvokeOptional(ps, tracker);
            var place = placeResults.FirstOrDefault();

            string timezone = "";
            try
            {
                ps.AddCommand("Get-MailboxRegionalConfiguration")
                  .AddParameter("Identity", roomEmail)
                  .AddParameter("ErrorAction", "SilentlyContinue");
                var regionalResults = InvokeOptional(ps, tracker);
                var regional = regionalResults.FirstOrDefault();
                timezone = regional?.Properties["TimeZone"]?.Value?.ToString() ?? "";
            }
            catch { /* best-effort */ }

            return new RoomInfo
            {
                Email = mbx.Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? roomEmail,
                DisplayName = mbx.Properties["DisplayName"]?.Value?.ToString() ?? "",
                RecipientTypeDetails = mbx.Properties["RecipientTypeDetails"]?.Value?.ToString() ?? "",
                CustomAttribute9 = mbx.Properties["CustomAttribute9"]?.Value?.ToString(),
                City = place?.Properties["City"]?.Value?.ToString() ?? "",
                Building = place?.Properties["Building"]?.Value?.ToString() ?? "",
                Capacity = int.TryParse(place?.Properties["Capacity"]?.Value?.ToString(), out var cap) ? cap : 0,
                Floor = place?.Properties["Floor"]?.Value?.ToString() ?? "",
                FloorLabel = place?.Properties["FloorLabel"]?.Value?.ToString() ?? "",
                DisplayDeviceName = place?.Properties["DisplayDeviceName"]?.Value?.ToString() ?? "",
                VideoDeviceName = place?.Properties["VideoDeviceName"]?.Value?.ToString() ?? "",
                CountryOrRegion = place?.Properties["CountryOrRegion"]?.Value?.ToString() ?? "",
                State = place?.Properties["State"]?.Value?.ToString() ?? "",
                TimeZone = timezone
            };
        });
    }

    public async Task<(string? roomListName, bool exists, bool isLegacy)> ResolveRoomListAsync(string city)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            var canonicalName = $"{city} Conference Rooms";
            var legacyName = $"RoomList-{city}";

            ps.AddCommand("Get-DistributionGroup")
              .AddParameter("Identity", canonicalName)
              .AddParameter("ErrorAction", "SilentlyContinue");
            var canonical = InvokeOptional(ps, tracker);
            if (canonical.Count > 0)
                return (canonicalName, true, false);

            ps.AddCommand("Get-DistributionGroup")
              .AddParameter("Identity", legacyName)
              .AddParameter("ErrorAction", "SilentlyContinue");
            var legacy = InvokeOptional(ps, tracker);
            if (legacy.Count > 0)
                return (legacyName, true, true);

            return (canonicalName, false, false);
        });
    }

    private static readonly HashSet<string> ValidRoomRecipientTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "RoomMailbox", "RemoteRoomMailbox"
    };

    public async Task<string?> ValidateRoomRecipientAsync(string roomEmail)
    {
        var info = await GetRoomInfoAsync(roomEmail);
        if (info == null)
            return $"Room '{roomEmail}' not found.";
        if (!ValidRoomRecipientTypes.Contains(info.RecipientTypeDetails))
            return $"'{roomEmail}' is a {info.RecipientTypeDetails}, not a room mailbox. Conference Room operations require RoomMailbox or RemoteRoomMailbox.";
        return null;
    }

    // -------------------------------------------------------------------------
    // Room Finder — set metadata + add to room list
    // -------------------------------------------------------------------------

    public async Task<RoomOperationResult> SetRoomMetadataAndListAsync(
        string roomEmail, string city, string building, int capacity,
        string floor, string floorLabel, string displayDevice, string videoDevice,
        string countryOrRegion, string state, string timezone)
    {
        var result = new RoomOperationResult { Email = roomEmail };

        if (!string.IsNullOrWhiteSpace(timezone))
        {
            try { TimeZoneInfo.FindSystemTimeZoneById(timezone); }
            catch (TimeZoneNotFoundException)
            {
                result.Success = false;
                result.Message = $"Invalid timezone: '{timezone}'.";
                return result;
            }
        }

        var recipientError = await ValidateRoomRecipientAsync(roomEmail);
        if (recipientError != null)
        {
            result.Success = false;
            result.Message = recipientError;
            return result;
        }

        var opResult = await RunAsync((ps, tracker) =>
        {
            // Step 1: Set-Place
            _operationTrace?.Step("SetPlace", backend: "EXO", command: "Set-Place", target: roomEmail);
            var placeCmd = ps.AddCommand("Set-Place")
              .AddParameter("Identity", roomEmail)
              .AddParameter("City", city)
              .AddParameter("Building", building)
              .AddParameter("Capacity", capacity)
              .AddParameter("Floor", floor)
              .AddParameter("ErrorAction", "Stop");
            if (!string.IsNullOrWhiteSpace(floorLabel))
                placeCmd.AddParameter("FloorLabel", floorLabel);
            if (!string.IsNullOrWhiteSpace(displayDevice))
                placeCmd.AddParameter("DisplayDeviceName", displayDevice);
            if (!string.IsNullOrWhiteSpace(videoDevice))
                placeCmd.AddParameter("VideoDeviceName", videoDevice);
            Invoke(ps, tracker);
            result.Steps.Add(new RoomOperationStep { Stage = "Set-Place", Success = true });

            // Step 2: Set-User (country, state, city)
            _operationTrace?.Step("SetUser", backend: "EXO", command: "Set-User", target: roomEmail);
            ps.AddCommand("Set-User")
              .AddParameter("Identity", roomEmail)
              .AddParameter("City", city)
              .AddParameter("ErrorAction", "Stop");
            if (!string.IsNullOrWhiteSpace(countryOrRegion))
                ps.AddParameter("CountryOrRegion", countryOrRegion);
            if (!string.IsNullOrWhiteSpace(state))
                ps.AddParameter("StateOrProvince", state);
            Invoke(ps, tracker);
            result.Steps.Add(new RoomOperationStep { Stage = "Set-User", Success = true });

            // Step 3: Timezone + WorkDays
            if (!string.IsNullOrWhiteSpace(timezone))
            {
                _operationTrace?.Step("SetTimezone", backend: "EXO", command: "Set-MailboxRegionalConfiguration", target: roomEmail);
                ps.AddCommand("Set-MailboxRegionalConfiguration")
                  .AddParameter("Identity", roomEmail)
                  .AddParameter("TimeZone", timezone)
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps, tracker);

                ps.AddCommand("Set-MailboxCalendarConfiguration")
                  .AddParameter("Identity", roomEmail)
                  .AddParameter("WorkingHoursTimeZone", timezone)
                  .AddParameter("WorkDays", "None")
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps, tracker);
                result.Steps.Add(new RoomOperationStep { Stage = "Set-Timezone/WorkDays", Success = true });
            }
        });

        if (!opResult.Success)
        {
            result.Success = false;
            result.Message = opResult.Message;
            result.Steps.Add(new RoomOperationStep { Stage = "Metadata", Success = false, Error = opResult.Message });
            return result;
        }

        // Step 4: Room list
        if (!string.IsNullOrWhiteSpace(city))
        {
            var listResult = await AddToRoomListAsync(roomEmail, city);
            result.Steps.AddRange(listResult.Steps);
            if (!listResult.Success)
            {
                if (listResult.AdAttributeFixRequired)
                {
                    result.AdAttributeFixRequired = true;
                    result.AdAttributeDetail = listResult.AdAttributeDetail;
                }
                result.Success = false;
                result.Message = $"Metadata set but room list failed: {listResult.Message}";
                return result;
            }
        }

        result.Success = true;
        result.Message = "Room metadata and room list configured.";
        return result;
    }

    public async Task<RoomOperationResult> AddToRoomListAsync(string roomEmail, string city)
    {
        var result = new RoomOperationResult { Email = roomEmail };

        var (roomListName, exists, isLegacy) = await ResolveRoomListAsync(city);
        if (roomListName == null)
        {
            result.Success = false;
            result.Message = "Could not determine room list name.";
            return result;
        }

        var opResult = await RunAsync((ps, tracker) =>
        {
            if (!exists)
            {
                _operationTrace?.Step("CreateRoomList", backend: "EXO", command: "New-DistributionGroup", target: roomListName);
                var newDl = ps.AddCommand("New-DistributionGroup")
                  .AddParameter("Name", roomListName)
                  .AddParameter("RoomList")
                  .AddParameter("ErrorAction", "Stop");
                if (!string.IsNullOrWhiteSpace(RoomListOU))
                    newDl.AddParameter("OrganizationalUnit", RoomListOU);
                Invoke(ps, tracker);
                result.Steps.Add(new RoomOperationStep { Stage = $"Create room list '{roomListName}'", Success = true });
            }
            else if (isLegacy)
            {
                result.Steps.Add(new RoomOperationStep { Stage = $"Using legacy room list '{roomListName}'", Success = true });
            }

            // Check if already a member
            ps.AddCommand("Get-DistributionGroupMember")
              .AddParameter("Identity", roomListName)
              .AddParameter("ErrorAction", "SilentlyContinue");
            var members = InvokeOptional(ps, tracker);
            var alreadyMember = members.Any(m =>
                string.Equals(m.Properties["PrimarySmtpAddress"]?.Value?.ToString(), roomEmail, StringComparison.OrdinalIgnoreCase));

            if (!alreadyMember)
            {
                _operationTrace?.Step("AddToRoomList", backend: "EXO", command: "Add-DistributionGroupMember", target: roomEmail);
                ps.AddCommand("Add-DistributionGroupMember")
                  .AddParameter("Identity", roomListName)
                  .AddParameter("Member", roomEmail)
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps, tracker);
                result.Steps.Add(new RoomOperationStep { Stage = $"Add to '{roomListName}'", Success = true });
            }
            else
            {
                result.Steps.Add(new RoomOperationStep { Stage = $"Already in '{roomListName}'", Success = true });
            }
        });

        if (!opResult.Success)
        {
            result.Success = false;
            result.Message = opResult.Message;

            // Detect NonRoomMailboxAddToRoomListException
            if (opResult.Message.Contains("NonRoomMailboxAddToRoomList", StringComparison.OrdinalIgnoreCase)
                || opResult.Message.Contains("isn't a room mailbox", StringComparison.OrdinalIgnoreCase))
            {
                result.AdAttributeFixRequired = true;
                result.AdAttributeDetail = "Room has incorrect AD attributes (msExchRemoteRecipientType). " +
                    "Recommended: Set msExchRemoteRecipientType to 36 and msExchRecipientDisplayType to -2147481850. " +
                    "Contact an AD administrator to fix this before retrying.";
                result.Steps.Add(new RoomOperationStep
                {
                    Stage = "AD attribute check",
                    Success = false,
                    Error = result.AdAttributeDetail
                });
            }
            else
            {
                result.Steps.Add(new RoomOperationStep { Stage = "Add to room list", Success = false, Error = opResult.Message });
            }
            return result;
        }

        result.Success = true;
        result.Message = $"Added to room list '{roomListName}'.";
        return result;
    }

    // -------------------------------------------------------------------------
    // Room Type — full 6-type implementation matching PS script
    // -------------------------------------------------------------------------

    public async Task<RoomOperationResult> SetRoomTypeAsync(
        string roomEmail, RoomType roomType, string timezone,
        string site = "none", string? arbiter = null,
        bool removeExistingPermissions = false)
    {
        var result = new RoomOperationResult { Email = roomEmail };

        if (!string.IsNullOrWhiteSpace(timezone))
        {
            try { TimeZoneInfo.FindSystemTimeZoneById(timezone); }
            catch (TimeZoneNotFoundException)
            {
                result.Success = false;
                result.Message = $"Invalid timezone: '{timezone}'.";
                return result;
            }
        }

        var recipientError = await ValidateRoomRecipientAsync(roomEmail);
        if (recipientError != null)
        {
            result.Success = false;
            result.Message = recipientError;
            return result;
        }

        var arbiterGroup = arbiter ?? ArbiterGroup;
        var customAttr9 = BuildCustomAttribute9(roomType, site);
        var mailTip = GetMailTipForType(roomType, site);
        var additionalResponse = GetAdditionalResponseForType(roomType, site);

        var opResult = await RunAsync((ps, tracker) =>
        {
            // Step 1: Resolve room and calendar folder
            _operationTrace?.Step("ResolveRoom", backend: "EXO", command: "Get-Mailbox", target: roomEmail);
            ps.AddCommand("Get-Mailbox")
              .AddParameter("Identity", roomEmail)
              .AddParameter("ErrorAction", "Stop");
            var mbx = Invoke(ps, tracker).FirstOrDefault()
                ?? throw new InvalidOperationException($"Room '{roomEmail}' not found.");
            var roomPrimary = mbx.Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? roomEmail;
            result.Steps.Add(new RoomOperationStep { Stage = "Resolve room", Success = true });

            // Get calendar folder name
            _operationTrace?.Step("GetCalendarFolder", backend: "EXO", command: "Get-MailboxFolderStatistics", target: roomPrimary);
            ps.AddCommand("Get-MailboxFolderStatistics")
              .AddParameter("Identity", roomPrimary)
              .AddParameter("ErrorAction", "Stop");
            var folderStats = Invoke(ps, tracker);
            var calFolder = folderStats
                .FirstOrDefault(f => f.Properties["FolderType"]?.Value?.ToString() == "Calendar");
            var calFolderName = calFolder?.Properties["Name"]?.Value?.ToString() ?? "Calendar";
            var calFolderPath = $"{roomPrimary}:\\{calFolderName}";
            result.Steps.Add(new RoomOperationStep { Stage = "Get calendar folder", Success = true });

            // Step 2: Resolve arbiter group
            string arbiterEmail = arbiterGroup;
            try
            {
                ps.AddCommand("Get-Recipient")
                  .AddParameter("Identity", arbiterGroup)
                  .AddParameter("ErrorAction", "SilentlyContinue");
                var arbResult = InvokeOptional(ps, tracker).FirstOrDefault();
                if (arbResult != null)
                    arbiterEmail = arbResult.Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? arbiterGroup;
            }
            catch { /* use raw value */ }

            // Step 3: Remove existing permissions if requested (always for CEO)
            if (removeExistingPermissions || roomType == RoomType.CEO)
            {
                _operationTrace?.Step("RemoveExistingPermissions", backend: "EXO", command: "Remove-MailboxFolderPermission", target: calFolderPath);
                try
                {
                    ps.AddCommand("Get-MailboxFolderPermission")
                      .AddParameter("Identity", calFolderPath)
                      .AddParameter("ErrorAction", "SilentlyContinue");
                    var existingPerms = InvokeOptional(ps, tracker);
                    foreach (var perm in existingPerms)
                    {
                        var user = perm.Properties["User"]?.Value?.ToString() ?? "";
                        if (user.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                            user.Equals("Anonymous", StringComparison.OrdinalIgnoreCase))
                            continue;

                        ps.AddCommand("Remove-MailboxFolderPermission")
                          .AddParameter("Identity", calFolderPath)
                          .AddParameter("User", user)
                          .AddParameter("Confirm", false)
                          .AddParameter("ErrorAction", "SilentlyContinue");
                        InvokeOptional(ps, tracker);
                    }
                    result.Steps.Add(new RoomOperationStep { Stage = "Remove existing permissions", Success = true });
                }
                catch (Exception ex)
                {
                    result.Steps.Add(new RoomOperationStep { Stage = "Remove existing permissions", Success = false, Error = ex.Message });
                }
            }

            // Step 4: Calendar processing settings
            _operationTrace?.Step("SetCalendarProcessing", backend: "EXO", command: "Set-CalendarProcessing", target: roomPrimary);
            var procCmd = ps.AddCommand("Set-CalendarProcessing")
              .AddParameter("Identity", roomPrimary)
              .AddParameter("AllowConflicts", false)
              .AddParameter("AutomateProcessing", "AutoAccept")
              .AddParameter("DeleteSubject", true)
              .AddParameter("AddOrganizerToSubject", true)
              .AddParameter("DeleteNonCalendarItems", true)
              .AddParameter("DeleteAttachments", true)
              .AddParameter("AllowRecurringMeetings", true)
              .AddParameter("MaximumDurationInMinutes", 0)
              .AddParameter("EnforceSchedulingHorizon", true)
              .AddParameter("DeleteComments", false)
              .AddParameter("ErrorAction", "Stop");

            ApplyTypeSpecificProcessingSettings(procCmd, roomType, site,
                arbiterEmail, ExecCoordinatorsGroup);

            Invoke(ps, tracker);
            result.Steps.Add(new RoomOperationStep { Stage = "Set-CalendarProcessing", Success = true });

            // Step 5: Set CustomAttribute9 + MailTip on cloud mailbox
            _operationTrace?.Step("SetMailbox", backend: "EXO", command: "Set-Mailbox", target: roomPrimary);
            try
            {
                var mbxCmd = ps.AddCommand("Set-Mailbox")
                  .AddParameter("Identity", roomPrimary)
                  .AddParameter("CustomAttribute9", customAttr9)
                  .AddParameter("ErrorAction", "Stop");
                if (mailTip != null)
                    mbxCmd.AddParameter("MailTip", mailTip);
                Invoke(ps, tracker);
                result.Steps.Add(new RoomOperationStep { Stage = "Set-Mailbox (CustomAttribute9/MailTip)", Success = true });
            }
            catch (Exception ex)
            {
                result.Steps.Add(new RoomOperationStep { Stage = "Set-Mailbox (CustomAttribute9/MailTip)", Success = false, Error = ex.Message });
            }

            // Step 6: Calendar folder permissions
            _operationTrace?.Step("SetCalendarPermissions", backend: "EXO", command: "Set-MailboxFolderPermission", target: calFolderPath);
            SetCalendarPermissionsForType(ps, tracker, calFolderPath, roomType, site,
                arbiterEmail, result);

            // Step 7: ExecConfCoordinators for all non-CEO rooms
            if (roomType != RoomType.CEO)
            {
                SetCalendarPermission(ps, tracker, calFolderPath, ExecCoordinatorsGroup, "Editor", result);
            }

            // Step 8: Timezone + WorkDays
            if (!string.IsNullOrWhiteSpace(timezone))
            {
                _operationTrace?.Step("SetTimezone", backend: "EXO", command: "Set-MailboxRegionalConfiguration", target: roomPrimary);
                ps.AddCommand("Set-MailboxRegionalConfiguration")
                  .AddParameter("Identity", roomPrimary)
                  .AddParameter("TimeZone", timezone)
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps, tracker);

                ps.AddCommand("Set-MailboxCalendarConfiguration")
                  .AddParameter("Identity", roomPrimary)
                  .AddParameter("WorkingHoursTimeZone", timezone)
                  .AddParameter("WorkDays", "None")
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps, tracker);
                result.Steps.Add(new RoomOperationStep { Stage = "Set-Timezone/WorkDays", Success = true });
            }
        });

        if (!opResult.Success)
        {
            result.Success = false;
            result.Message = opResult.Message;
            return result;
        }

        // Step 9: Set-RemoteMailbox on-prem (separate runspace, best-effort)
        await SetRemoteMailboxAsync(roomEmail, customAttr9, mailTip, result);

        var failedSteps = result.Steps.Where(s => !s.Success).ToList();
        if (failedSteps.Count > 0)
        {
            result.Success = false;
            result.Message = $"Partially configured as {roomType}" + (site != "none" ? $" ({site})" : "")
                + $". {failedSteps.Count} step(s) failed: {string.Join("; ", failedSteps.Select(s => s.Stage))}.";
            return result;
        }

        result.Success = true;
        result.Message = $"Configured as {roomType}" + (site != "none" ? $" ({site})" : "") + ".";
        return result;
    }

    // -------------------------------------------------------------------------
    // CSV parsing — shared, testable. Email selection matches SetupRoomType.ps1:
    // first non-blank identifier column wins; no '@' requirement (Exchange
    // resolves SMTP/alias/DN/canonical). Skipped rows carry a reason and the
    // header names found, and are never silently dropped.
    // -------------------------------------------------------------------------

    // Identity columns in priority order. PrimarySmtpAddress first yields the
    // cleanest resolution + display name; Identity (often a canonical name in
    // Exchange exports) is the documented primary column and resolves fine too.
    private static readonly string[] IdentityColumns =
        ["PrimarySmtpAddress", "Identity", "EmailAddress", "Mail", "Email"];

    private static string? SelectIdentity(CsvReader csv)
    {
        foreach (var col in IdentityColumns)
        {
            if (csv.TryGetField<string>(col, out var val) && !string.IsNullOrWhiteSpace(val))
                return val.Trim();
        }
        return null;
    }

    private static CsvReader OpenCsv(Stream stream)
    {
        var reader = new StreamReader(stream);
        var cfg = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null
        };
        var csv = new CsvReader(reader, cfg);
        return csv;
    }

    /// <summary>
    /// Parse the Room Finder bulk CSV. Returns one result per data row, in file
    /// order. A result is either a populated row or a skip with a reason.
    /// </summary>
    public static async Task<List<FinderCsvParseResult>> ParseFinderCsvAsync(Stream stream)
    {
        var results = new List<FinderCsvParseResult>();
        using var csv = OpenCsv(stream);
        await csv.ReadAsync();
        csv.ReadHeader();
        var columns = csv.HeaderRecord?.ToList() ?? [];
        int rowNum = 1;

        while (await csv.ReadAsync())
        {
            var email = SelectIdentity(csv);
            if (string.IsNullOrWhiteSpace(email))
            {
                results.Add(new FinderCsvParseResult
                {
                    RowIndex = rowNum++,
                    SkipReason = "Missing identity — no value in any of: " + string.Join(", ", IdentityColumns),
                    AvailableColumns = columns
                });
                continue;
            }

            results.Add(new FinderCsvParseResult
            {
                RowIndex = rowNum++,
                AvailableColumns = columns,
                Row = new FinderCsvRow
                {
                    Email = email,
                    City = csv.GetField("City")?.Trim() ?? "",
                    CountryOrRegion = csv.GetField("CountryOrRegion")?.Trim() ?? "",
                    State = csv.GetField("State")?.Trim() ?? "",
                    Building = csv.GetField("Building")?.Trim() ?? "",
                    Capacity = int.TryParse(csv.GetField("Capacity")?.Trim(), out var c) ? c : 1,
                    Floor = csv.GetField("Floor")?.Trim() ?? "",
                    FloorLabel = csv.GetField("FloorLabel")?.Trim() ?? "",
                    DisplayDeviceName = csv.GetField("DisplayDeviceName")?.Trim() ?? "",
                    VideoDeviceName = csv.GetField("VideoDeviceName")?.Trim() ?? "",
                    TimeZone = csv.GetField("TimeZone")?.Trim() ?? ""
                }
            });
        }

        return results;
    }

    /// <summary>
    /// Parse the Room Type bulk CSV. Returns one result per data row, in file
    /// order. A result is either a populated row or a skip with a reason
    /// (missing identity, or an invalid RemoveExistingPermissions value).
    /// </summary>
    public static async Task<List<TypeCsvParseResult>> ParseTypeCsvAsync(Stream stream)
    {
        var results = new List<TypeCsvParseResult>();
        using var csv = OpenCsv(stream);
        await csv.ReadAsync();
        csv.ReadHeader();
        var columns = csv.HeaderRecord?.ToList() ?? [];
        int rowNum = 1;

        while (await csv.ReadAsync())
        {
            var email = SelectIdentity(csv);
            if (string.IsNullOrWhiteSpace(email))
            {
                results.Add(new TypeCsvParseResult
                {
                    RowIndex = rowNum++,
                    SkipReason = "Missing identity — no value in any of: " + string.Join(", ", IdentityColumns),
                    AvailableColumns = columns
                });
                continue;
            }

            var removePermField = csv.GetField("RemoveExistingPermissions")?.Trim();
            bool removePerm = false;
            if (!string.IsNullOrWhiteSpace(removePermField))
            {
                if (string.Equals(removePermField, "True", StringComparison.OrdinalIgnoreCase))
                    removePerm = true;
                else if (!string.Equals(removePermField, "False", StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(new TypeCsvParseResult
                    {
                        RowIndex = rowNum++,
                        SkipReason = $"Invalid value '{removePermField}' for RemoveExistingPermissions. Use True or False.",
                        AvailableColumns = columns
                    });
                    continue;
                }
            }

            var site = csv.GetField("Site")?.Trim();
            results.Add(new TypeCsvParseResult
            {
                RowIndex = rowNum++,
                AvailableColumns = columns,
                Row = new TypeCsvRow
                {
                    Email = email,
                    Type = csv.GetField("Type")?.Trim() ?? "",
                    TimeZone = csv.GetField("TimeZone")?.Trim() ?? "",
                    Site = string.IsNullOrWhiteSpace(site) ? "none" : site,
                    Arbiter = csv.GetField("Arbiter")?.Trim() ?? "",
                    RemoveExistingPermissions = removePerm
                }
            });
        }

        return results;
    }

    // -------------------------------------------------------------------------
    // Preview builders for the UI
    // -------------------------------------------------------------------------

    public RoomTypePreviewRow BuildTypePreview(
        string email, string displayName, RoomType type, string timezone,
        string site, string arbiter, bool removeExisting)
    {
        var arbiterGroup = string.IsNullOrWhiteSpace(arbiter) ? ArbiterGroup : arbiter;
        var customAttr9 = BuildCustomAttribute9(type, site);
        var mailTip = GetMailTipForType(type, site) ?? "";
        var additional = GetAdditionalResponseForType(type, site) ?? "";

        var preview = new RoomTypePreviewRow
        {
            Email = email,
            DisplayName = displayName,
            Type = type,
            TimeZone = timezone,
            Site = site,
            Arbiter = arbiterGroup,
            RemoveExistingPermissions = removeExisting || type == RoomType.CEO,
            MailTip = mailTip,
            AdditionalResponse = additional,
            CustomAttribute9Tag = customAttr9,
            RoomResolved = true
        };

        // Build permission preview
        var (defaultRights, anonRights) = GetDefaultAnonymousRights(type);
        preview.DefaultCalPermission = defaultRights;
        preview.AnonymousCalPermission = anonRights;
        preview.BookInPolicyGroups = GetBookInPolicyGroups(type, site, arbiterGroup);
        preview.CalendarPermissions = GetCalendarPermissionPreviews(type, site, arbiterGroup);

        if (type == RoomType.CEO)
            preview.Warnings.Add("CEO type always removes all existing calendar permissions.");
        if (removeExisting && type != RoomType.CEO)
            preview.Warnings.Add("Existing calendar permissions will be removed before applying new ones.");

        return preview;
    }

    // -------------------------------------------------------------------------
    // Type-specific settings — mirrors SetupRoomType.ps1 switch block
    // -------------------------------------------------------------------------

    private void ApplyTypeSpecificProcessingSettings(
        PowerShell procCmd, RoomType type, string site,
        string arbiterEmail, string execCoord)
    {
        switch (type)
        {
            case RoomType.Standard:
                procCmd.AddParameter("AllBookInPolicy", true);
                procCmd.AddParameter("AddAdditionalResponse", false);
                procCmd.AddParameter("BookingWindowInDays", 180);
                procCmd.AddParameter("ProcessExternalMeetingMessages", false);
                break;

            case RoomType.Video:
                procCmd.AddParameter("AllBookInPolicy", true);
                procCmd.AddParameter("AddAdditionalResponse", true);
                procCmd.AddParameter("AdditionalResponse", VideoRoomResponse);
                procCmd.AddParameter("BookingWindowInDays", 180);
                procCmd.AddParameter("ProcessExternalMeetingMessages", false);
                break;

            case RoomType.Restricted:
                procCmd.AddParameter("AllBookInPolicy", false);
                procCmd.AddParameter("AddAdditionalResponse", true);
                procCmd.AddParameter("BookingWindowInDays", 180);
                procCmd.AddParameter("ProcessExternalMeetingMessages", site == "ADGT");
                if (site == "ADGT")
                {
                    procCmd.AddParameter("BookInPolicy", new[] { AdgtAdminsGroup, ExecVPsGroup, ExecAdminsGroup, arbiterEmail, execCoord });
                    procCmd.AddParameter("AdditionalResponse", AdgtRestrictedRoomResponse);
                    procCmd.AddParameter("ResourceDelegates", AdgtAdminsGroup);
                }
                else
                {
                    procCmd.AddParameter("BookInPolicy", new[] { ConfAdminsGroup, ExecVPsGroup, ExecAdminsGroup, arbiterEmail, execCoord });
                    procCmd.AddParameter("AdditionalResponse", RestrictedRoomResponse);
                }
                break;

            case RoomType.Exception:
                procCmd.AddParameter("AllBookInPolicy", false);
                procCmd.AddParameter("BookInPolicy", new[] { ExceptionGroup });
                procCmd.AddParameter("AddAdditionalResponse", false);
                procCmd.AddParameter("BookingWindowInDays", 1080);
                procCmd.AddParameter("ProcessExternalMeetingMessages", false);
                break;

            case RoomType.CEO:
                procCmd.AddParameter("AllBookInPolicy", false);
                procCmd.AddParameter("BookInPolicy", new[] { CeoGroup });
                procCmd.AddParameter("AddAdditionalResponse", true);
                procCmd.AddParameter("AdditionalResponse", CeoRoomResponse);
                procCmd.AddParameter("BookingWindowInDays", 1080);
                procCmd.AddParameter("ProcessExternalMeetingMessages", true);
                break;

            case RoomType.Executive:
                procCmd.AddParameter("AllBookInPolicy", false);
                procCmd.AddParameter("BookInPolicy", new[] { ExecVPsGroup, ExecAdminsGroup, execCoord });
                procCmd.AddParameter("AddAdditionalResponse", true);
                procCmd.AddParameter("AdditionalResponse", ExecRoomResponse);
                procCmd.AddParameter("BookingWindowInDays", 1080);
                procCmd.AddParameter("ProcessExternalMeetingMessages", true);
                break;
        }
    }

    private void SetCalendarPermissionsForType(
        PowerShell ps, ConnectionErrorTracker tracker,
        string calFolderPath, RoomType type, string site,
        string arbiterEmail, RoomOperationResult result)
    {
        switch (type)
        {
            case RoomType.Standard:
                SetCalendarPermission(ps, tracker, calFolderPath, "Anonymous", "AvailabilityOnly", result);
                SetCalendarPermission(ps, tracker, calFolderPath, "Default", "LimitedDetails", result);
                SetCalendarPermission(ps, tracker, calFolderPath, arbiterEmail, "Editor", result);
                SetCalendarPermission(ps, tracker, calFolderPath, ExecAdminsGroup, "Reviewer", result);
                SetCalendarPermission(ps, tracker, calFolderPath, ExecVPsGroup, "Editor", result);
                break;

            case RoomType.Video:
                SetCalendarPermission(ps, tracker, calFolderPath, "Anonymous", "AvailabilityOnly", result);
                SetCalendarPermission(ps, tracker, calFolderPath, "Default", "LimitedDetails", result);
                SetCalendarPermission(ps, tracker, calFolderPath, arbiterEmail, "Editor", result);
                SetCalendarPermission(ps, tracker, calFolderPath, ExecAdminsGroup, "Reviewer", result);
                SetCalendarPermission(ps, tracker, calFolderPath, ExecVPsGroup, "Editor", result);
                break;

            case RoomType.Restricted:
                SetCalendarPermission(ps, tracker, calFolderPath, "Anonymous", "AvailabilityOnly", result);
                SetCalendarPermission(ps, tracker, calFolderPath, "Default", "AvailabilityOnly", result);
                SetCalendarPermission(ps, tracker, calFolderPath, ExecAdminsGroup, "Editor", result);
                SetCalendarPermission(ps, tracker, calFolderPath, ExecVPsGroup, "Editor", result);
                SetCalendarPermission(ps, tracker, calFolderPath, arbiterEmail, "Editor", result);
                if (site == "ADGT")
                    SetCalendarPermission(ps, tracker, calFolderPath, AdgtAdminsGroup, "Editor", result);
                break;

            case RoomType.Exception:
                SetCalendarPermission(ps, tracker, calFolderPath, "Anonymous", "Reviewer", result);
                SetCalendarPermission(ps, tracker, calFolderPath, "Default", "Reviewer", result);
                SetCalendarPermission(ps, tracker, calFolderPath, ConfAdminsGroup, "Editor", result);
                break;

            case RoomType.CEO:
                SetCalendarPermission(ps, tracker, calFolderPath, "Anonymous", "None", result);
                SetCalendarPermission(ps, tracker, calFolderPath, "Default", "None", result);
                SetCalendarPermission(ps, tracker, calFolderPath, CeoGroup, "Editor", result);
                break;

            case RoomType.Executive:
                SetCalendarPermission(ps, tracker, calFolderPath, "Anonymous", "AvailabilityOnly", result);
                SetCalendarPermission(ps, tracker, calFolderPath, "Default", "AvailabilityOnly", result);
                SetCalendarPermission(ps, tracker, calFolderPath, ExecAdminsGroup, "Editor", result);
                SetCalendarPermission(ps, tracker, calFolderPath, ExecVPsGroup, "Editor", result);
                break;
        }
    }

    private void SetCalendarPermission(
        PowerShell ps, ConnectionErrorTracker tracker,
        string calFolderPath, string user, string accessRights,
        RoomOperationResult result)
    {
        try
        {
            // Try Add first, then Set to handle both new and existing
            ps.AddCommand("Add-MailboxFolderPermission")
              .AddParameter("Identity", calFolderPath)
              .AddParameter("AccessRights", accessRights)
              .AddParameter("User", user)
              .AddParameter("ErrorAction", "SilentlyContinue");
            InvokeOptional(ps, tracker);

            ps.AddCommand("Set-MailboxFolderPermission")
              .AddParameter("Identity", calFolderPath)
              .AddParameter("AccessRights", accessRights)
              .AddParameter("User", user)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);

            result.Steps.Add(new RoomOperationStep
            {
                Stage = $"Permission: {user} → {accessRights}",
                Success = true
            });
        }
        catch (Exception ex)
        {
            result.Steps.Add(new RoomOperationStep
            {
                Stage = $"Permission: {user} → {accessRights}",
                Success = false,
                Error = ex.Message
            });
        }
    }

    private async Task SetRemoteMailboxAsync(string roomEmail, string customAttr9, string? mailTip, RoomOperationResult result)
    {
        if (string.IsNullOrWhiteSpace(_onPremServerUri))
        {
            result.Steps.Add(new RoomOperationStep
            {
                Stage = "Set-RemoteMailbox",
                Success = true,
                Error = "Skipped — on-prem not configured"
            });
            return;
        }

        var creds = await GetModuleCredentialsAsync("Set-RemoteMailbox");
        if (creds is null)
        {
            result.Steps.Add(new RoomOperationStep
            {
                Stage = "Set-RemoteMailbox",
                Success = false,
                Error = "On-prem credentials unavailable from Delinea"
            });
            return;
        }

        _operationTrace?.Step("SetRemoteMailbox", backend: "OnPremExchange", command: "Set-RemoteMailbox", target: roomEmail);

        try
        {
            await ThrottledAsync(() => Task.Run<bool>(() =>
            {
                var iss = InitialSessionState.CreateDefault();
                iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
                using var runspace = RunspaceFactory.CreateRunspace(iss);
                runspace.Open();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                try
                {
                    ConnectOnPrem(ps, creds.Value.username, creds.Value.password, creds.Value.domain);

                    var scriptText = mailTip != null
                        ? "param($Identity, $Attr9, $Tip) Set-RemoteMailbox -Identity $Identity -CustomAttribute9 $Attr9 -MailTip $Tip -ErrorAction Stop"
                        : "param($Identity, $Attr9) Set-RemoteMailbox -Identity $Identity -CustomAttribute9 $Attr9 -ErrorAction Stop";

                    var scriptBlock = ScriptBlock.Create(scriptText);
                    var args = mailTip != null
                        ? new object[] { roomEmail, customAttr9, mailTip }
                        : new object[] { roomEmail, customAttr9 };

                    ps.AddCommand("Invoke-Command")
                      .AddParameter("Session", ps.Runspace.SessionStateProxy.GetVariable("onpremSession"))
                      .AddParameter("ScriptBlock", scriptBlock)
                      .AddParameter("ArgumentList", args);
                    Invoke(ps);

                    _logger.LogInformation("Set-RemoteMailbox succeeded for {Room}: CustomAttribute9={Attr9}", roomEmail, customAttr9);
                    return true;
                }
                finally
                {
                    try
                    {
                        ps.Commands.Clear();
                        var session = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");
                        if (session != null)
                        {
                            ps.AddCommand("Remove-PSSession").AddParameter("Session", session);
                            ps.Invoke();
                        }
                    }
                    catch { }
                }
            }));

            result.Steps.Add(new RoomOperationStep { Stage = "Set-RemoteMailbox (on-prem)", Success = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Set-RemoteMailbox failed for {Room} — cloud attributes are set, on-prem may be stale", roomEmail);
            result.Steps.Add(new RoomOperationStep
            {
                Stage = "Set-RemoteMailbox (on-prem)",
                Success = false,
                Error = ex.Message
            });
        }
    }

    // -------------------------------------------------------------------------
    // Helpers for preview/config
    // -------------------------------------------------------------------------

    private string BuildCustomAttribute9(RoomType type, string site)
    {
        if (type == RoomType.Restricted && site != "none")
            return $"RoomType:{site}-Restricted";
        return $"RoomType:{type}";
    }

    private string? GetMailTipForType(RoomType type, string site)
    {
        return type switch
        {
            RoomType.Standard => null,
            RoomType.Video => VideoRoomMailTip,
            RoomType.Restricted when site == "ADGT" => AdgtRestrictedMailTip,
            RoomType.Restricted => RestrictedMailTip,
            RoomType.Exception => null,
            RoomType.CEO => null,
            RoomType.Executive => ExecMailTip,
            _ => null
        };
    }

    private string? GetAdditionalResponseForType(RoomType type, string site)
    {
        return type switch
        {
            RoomType.Standard => null,
            RoomType.Video => VideoRoomResponse,
            RoomType.Restricted when site == "ADGT" => AdgtRestrictedRoomResponse,
            RoomType.Restricted => RestrictedRoomResponse,
            RoomType.Exception => null,
            RoomType.CEO => CeoRoomResponse,
            RoomType.Executive => ExecRoomResponse,
            _ => null
        };
    }

    private (string defaultRights, string anonRights) GetDefaultAnonymousRights(RoomType type)
    {
        return type switch
        {
            RoomType.Standard => ("LimitedDetails", "AvailabilityOnly"),
            RoomType.Video => ("LimitedDetails", "AvailabilityOnly"),
            RoomType.Restricted => ("AvailabilityOnly", "AvailabilityOnly"),
            RoomType.Exception => ("Reviewer", "Reviewer"),
            RoomType.CEO => ("None", "None"),
            RoomType.Executive => ("AvailabilityOnly", "AvailabilityOnly"),
            _ => ("LimitedDetails", "AvailabilityOnly")
        };
    }

    private List<string> GetBookInPolicyGroups(RoomType type, string site, string arbiterEmail)
    {
        return type switch
        {
            RoomType.Standard => ["(AllBookInPolicy)"],
            RoomType.Video => ["(AllBookInPolicy)"],
            RoomType.Restricted when site == "ADGT" =>
                [AdgtAdminsGroup, ExecVPsGroup, ExecAdminsGroup, arbiterEmail, ExecCoordinatorsGroup],
            RoomType.Restricted =>
                [ConfAdminsGroup, ExecVPsGroup, ExecAdminsGroup, arbiterEmail, ExecCoordinatorsGroup],
            RoomType.Exception => [ExceptionGroup],
            RoomType.CEO => [CeoGroup],
            RoomType.Executive => [ExecVPsGroup, ExecAdminsGroup, ExecCoordinatorsGroup],
            _ => []
        };
    }

    private List<CalendarPermissionPreview> GetCalendarPermissionPreviews(
        RoomType type, string site, string arbiterEmail)
    {
        var perms = new List<CalendarPermissionPreview>();

        switch (type)
        {
            case RoomType.Standard:
            case RoomType.Video:
                perms.Add(new() { User = arbiterEmail, AccessRights = "Editor" });
                perms.Add(new() { User = ExecAdminsGroup, AccessRights = "Reviewer" });
                perms.Add(new() { User = ExecVPsGroup, AccessRights = "Editor" });
                break;
            case RoomType.Restricted:
                perms.Add(new() { User = ExecAdminsGroup, AccessRights = "Editor" });
                perms.Add(new() { User = ExecVPsGroup, AccessRights = "Editor" });
                perms.Add(new() { User = arbiterEmail, AccessRights = "Editor" });
                if (site == "ADGT")
                    perms.Add(new() { User = AdgtAdminsGroup, AccessRights = "Editor" });
                break;
            case RoomType.Exception:
                perms.Add(new() { User = ConfAdminsGroup, AccessRights = "Editor" });
                break;
            case RoomType.CEO:
                perms.Add(new() { User = CeoGroup, AccessRights = "Editor" });
                break;
            case RoomType.Executive:
                perms.Add(new() { User = ExecAdminsGroup, AccessRights = "Editor" });
                perms.Add(new() { User = ExecVPsGroup, AccessRights = "Editor" });
                break;
        }

        // ExecConfCoordinators for all non-CEO
        if (type != RoomType.CEO)
            perms.Add(new() { User = ExecCoordinatorsGroup, AccessRights = "Editor" });

        return perms;
    }
}
