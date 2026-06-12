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

    // Environment-specific values come from module config — no hardcoded defaults. When a
    // required group is unconfigured the operation fails closed (see SetRoomTypeAsync
    // preflight); the real values live in deploy-path config/module-config-ConferenceRooms.json.
    private string ArbiterGroup => Cfg("DefaultArbiterGroup");
    private string ExecCoordinatorsGroup => Cfg("ExecConfCoordinatorsGroup");
    private string ExecAdminsGroup => Cfg("ConfExecAdminsGroup");
    private string ExecVPsGroup => Cfg("ConfExecVPsGroup");
    private string ConfAdminsGroup => Cfg("ConfAdminsGroup");
    private string CeoGroup => Cfg("ConfCEOGroup");
    private string ExceptionGroup => Cfg("ConfExceptionGroup");
    private string AdgtAdminsGroup => Cfg("ADGTAdminsGroup");
    private string RoomListOU => Cfg("RoomListOU");

    private string RestrictedMailTip => Cfg("RestrictedMailTip",
        $"This is a restricted room. Email {Cfg("RestrictedContactEmail")} for assistance with booking this room.");
    private string ExecMailTip => Cfg("ExecMailTip",
        $"Only exec admins may book this room.");
    private string AdgtRestrictedMailTip =>
        $"This is a restricted room. Email {Cfg("ADGTContactEmail")} if you need assistance with booking this room.";

    private string ExecRoomResponse =>
        $"This is an executive only room. Email {Cfg("ExecContactEmail")} if you need assistance.";
    private string RestrictedRoomResponse =>
        $"This is a restricted room. Email {Cfg("RestrictedContactEmail")} for assistance with booking this room.";
    private string AdgtRestrictedRoomResponse =>
        $"For room requests, please send complete details to {Cfg("ADGTContactEmail")} (Note: Reservations are only allowed 14 days before the event date - subject to availability.)";
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

    // Room-list naming. The room list IS the building in Microsoft Room Finder; the
    // city above it is derived from each room's Place metadata (set via Set-Place /
    // Set-User), NOT from the list name. Keep these pure + static so the naming rule is
    // unit-testable without a live EXO connection. See
    // docs/ConferenceRooms-BuildingRoomList-Plan.md.
    public static string BuildRoomListName(string building) => $"{building.Trim()} Conference Rooms";
    public static string BuildLegacyRoomListName(string key) => $"RoomList-{key.Trim()}";

    // True when an EXO write was rejected because the object is mastered on-premises
    // (DirSync'd) and must be written via Set-RemoteMailbox instead. Pure + static so the
    // classification is unit-testable. The two phrases are EXO's stable wording for this
    // condition. See docs/ConferenceRooms-SyncedRoomSetMailbox-Plan.md.
    public static bool IsOnPremMasteredWriteError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return false;
        return message.Contains("out of the current user's write scope", StringComparison.OrdinalIgnoreCase)
            || message.Contains("being synchronized from your on-premises organization", StringComparison.OrdinalIgnoreCase);
    }

    // Whether an on-prem write that was SKIPPED (on-prem not configured) should count as
    // success. It is success only when the cloud write already set the attribute. If the
    // cloud write was deferred because the room is on-prem mastered, a skip means the
    // attribute is written nowhere — that is a failure. Pure + static for unit testing.
    public static bool OnPremSkipCountsAsSuccess(bool cloudAttrDeferredToOnPrem)
        => !cloudAttrDeferredToOnPrem;

    // Step result for "Remove existing permissions": any captured error makes the
    // step fail so the failedSteps aggregation surfaces it. A CEO conversion must
    // never report success while previous permission holders retain access.
    // Pure + static for unit testing.
    public static RoomOperationStep BuildPermissionRemovalStep(IReadOnlyList<string> errors)
        => errors.Count == 0
            ? new RoomOperationStep { Stage = "Remove existing permissions", Success = true }
            : new RoomOperationStep
            {
                Stage = "Remove existing permissions",
                Success = false,
                Error = string.Join("; ", errors)
            };

    // The config keys whose values are required before any Room Type operation may run.
    // RoomListOU is excluded (already IsNullOrWhiteSpace-guarded at the room-list step) and
    // the contact-email keys are excluded (used only in cosmetic response text).
    public static readonly string[] RequiredGroupConfigKeys =
    [
        "DefaultArbiterGroup", "ExecConfCoordinatorsGroup", "ConfExecAdminsGroup",
        "ConfExecVPsGroup", "ConfAdminsGroup", "ConfCEOGroup", "ConfExceptionGroup",
        "ADGTAdminsGroup"
    ];

    // Pure, unit-testable: given a (key -> value) view of the required group config, return
    // the keys that are missing/blank. Empty result means the module is configured enough to
    // run a Room Type operation. See docs/ConferenceRooms (fail-closed when unconfigured).
    public static List<string> FindMissingRequiredGroups(Func<string, string?> getValue)
    {
        var missing = new List<string>();
        foreach (var key in RequiredGroupConfigKeys)
        {
            if (string.IsNullOrWhiteSpace(getValue(key)))
                missing.Add(key);
        }
        return missing;
    }

    /// <summary>
    /// Resolve the target room list for a room, keyed on <paramref name="building"/>.
    /// <paramref name="city"/> is used only to detect a stray city-named list that differs
    /// from the building target (so the UI can warn the operator — Option ii). Returns the
    /// canonical-or-legacy target name, whether it already exists, whether the match was the
    /// legacy form, and the name of any differing city-named list found (else null).
    /// </summary>
    public async Task<(string? roomListName, bool exists, bool isLegacy, string? strayCityListName)> ResolveRoomListAsync(string building, string city)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            var canonicalName = BuildRoomListName(building);
            var legacyName = BuildLegacyRoomListName(building);

            // Stray city-named list detection (Option ii): only meaningful when the city
            // would produce a different list name than the building target.
            string? strayCityListName = null;
            if (!string.IsNullOrWhiteSpace(city))
            {
                var cityListName = BuildRoomListName(city);
                if (!string.Equals(cityListName, canonicalName, StringComparison.OrdinalIgnoreCase))
                {
                    ps.AddCommand("Get-DistributionGroup")
                      .AddParameter("Identity", cityListName)
                      .AddParameter("ErrorAction", "SilentlyContinue");
                    var cityList = InvokeOptional(ps, tracker);
                    if (cityList.Count > 0)
                        strayCityListName = cityListName;
                }
            }

            ps.AddCommand("Get-DistributionGroup")
              .AddParameter("Identity", canonicalName)
              .AddParameter("ErrorAction", "SilentlyContinue");
            var canonical = InvokeOptional(ps, tracker);
            if (canonical.Count > 0)
                return (canonicalName, true, false, strayCityListName);

            ps.AddCommand("Get-DistributionGroup")
              .AddParameter("Identity", legacyName)
              .AddParameter("ErrorAction", "SilentlyContinue");
            var legacy = InvokeOptional(ps, tracker);
            if (legacy.Count > 0)
                return (legacyName, true, true, strayCityListName);

            return (canonicalName, false, false, strayCityListName);
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

        // Step 4: Room list — keyed on building (the room list IS the building; city is
        // metadata only). A room with no building is not added to any list.
        if (!string.IsNullOrWhiteSpace(building))
        {
            var listResult = await AddToRoomListAsync(roomEmail, building, city);
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

    public async Task<RoomOperationResult> AddToRoomListAsync(string roomEmail, string building, string city = "")
    {
        var result = new RoomOperationResult { Email = roomEmail };

        var (roomListName, exists, isLegacy, _) = await ResolveRoomListAsync(building, city);
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

        // Fail closed before any EXO mutation: the required group addresses come from module
        // config (no hardcoded defaults). Applying empty group values would send -User "" /
        // -BookInPolicy [""] to EXO and fail per-room mid-operation, risking partial calendar
        // state. Abort cleanly with an actionable message instead.
        var missingGroups = FindMissingRequiredGroups(key => _moduleConfig.GetValue(ModuleId, key));
        if (missingGroups.Count > 0)
        {
            result.Success = false;
            result.Message = $"Conference Rooms module is not configured. Set {string.Join(", ", missingGroups)} in Module Config.";
            return result;
        }

        var arbiterGroup = arbiter ?? ArbiterGroup;
        var customAttr9 = BuildCustomAttribute9(roomType, site);
        var mailTip = GetMailTipForType(roomType, site);
        var additionalResponse = GetAdditionalResponseForType(roomType, site);

        // Set when the cloud CustomAttribute9/MailTip write was rejected because the room is
        // on-prem mastered (DirSync'd). When true, the on-prem Set-RemoteMailbox (Step 9)
        // becomes the ONLY path that writes these attributes, so it is mandatory — see Step 9.
        bool cloudAttrDeferredToOnPrem = false;

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

            // Step 3: Remove existing permissions if requested (always for CEO).
            // Errors must surface: a CEO conversion that reports success while old
            // Editor grants survive would leave unauthorized access to the CEO
            // calendar. InvokeBestEffort captures per-command errors that the old
            // SilentlyContinue + InvokeOptional combination discarded.
            if (removeExistingPermissions || roomType == RoomType.CEO)
            {
                _operationTrace?.Step("RemoveExistingPermissions", backend: "EXO", command: "Remove-MailboxFolderPermission", target: calFolderPath);
                var removalErrors = new List<string>();
                try
                {
                    ps.AddCommand("Get-MailboxFolderPermission")
                      .AddParameter("Identity", calFolderPath);
                    var existingPerms = InvokeBestEffort(ps, tracker, out var readErrors);
                    removalErrors.AddRange(readErrors);
                    foreach (var perm in existingPerms)
                    {
                        var user = perm.Properties["User"]?.Value?.ToString() ?? "";
                        if (user.Equals("Default", StringComparison.OrdinalIgnoreCase) ||
                            user.Equals("Anonymous", StringComparison.OrdinalIgnoreCase))
                            continue;

                        ps.AddCommand("Remove-MailboxFolderPermission")
                          .AddParameter("Identity", calFolderPath)
                          .AddParameter("User", user)
                          .AddParameter("Confirm", false);
                        InvokeBestEffort(ps, tracker, out var removeErrors);
                        foreach (var err in removeErrors)
                            removalErrors.Add($"{user}: {err}");
                    }
                }
                catch (Exception ex)
                {
                    removalErrors.Add(ex.Message);
                }
                result.Steps.Add(BuildPermissionRemovalStep(removalErrors));
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
            // Step 5: Set CustomAttribute9 + MailTip on the cloud mailbox. For a DirSync'd
            // (on-prem mastered) room this write is rejected cloud-side and must be done
            // on-prem via Set-RemoteMailbox (Step 9) — so run it best-effort and classify the
            // result. The cloud rejection is expected and informational; any OTHER error is a
            // real failure and is surfaced as such. Best-effort also avoids leaving a failed
            // command queued on the shared pipeline (which previously poisoned later steps).
            _operationTrace?.Step("SetMailbox", backend: "EXO", command: "Set-Mailbox", target: roomPrimary);
            var mbxCmd = ps.AddCommand("Set-Mailbox")
              .AddParameter("Identity", roomPrimary)
              .AddParameter("CustomAttribute9", customAttr9)
              .AddParameter("ErrorAction", "SilentlyContinue");
            if (mailTip != null)
                mbxCmd.AddParameter("MailTip", mailTip);
            InvokeBestEffort(ps, tracker, out var setMbxErrors);

            if (setMbxErrors.Count == 0)
            {
                result.Steps.Add(new RoomOperationStep { Stage = "Set-Mailbox (CustomAttribute9/MailTip)", Success = true });
            }
            else if (setMbxErrors.Any(IsOnPremMasteredWriteError))
            {
                cloudAttrDeferredToOnPrem = true;
                // Informational, not a failure: the attribute is written on-prem in Step 9.
                // (RoomOperationStep has no info severity; represent as Success=true with text.)
                result.Steps.Add(new RoomOperationStep
                {
                    Stage = "CustomAttribute9/MailTip set on-prem (synced room — cloud write skipped)",
                    Success = true
                });
            }
            else
            {
                // Unexpected cloud error — keep it visible as a failure.
                result.Steps.Add(new RoomOperationStep
                {
                    Stage = "Set-Mailbox (CustomAttribute9/MailTip)",
                    Success = false,
                    Error = string.Join(" | ", setMbxErrors)
                });
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

        // Step 9: Set-RemoteMailbox on-prem (separate runspace). When the cloud write was
        // deferred to on-prem (synced room), this becomes the mandatory authoritative write:
        // a skip or failure must fail the operation, not be reported as success.
        await SetRemoteMailboxAsync(roomEmail, customAttr9, mailTip, result, cloudAttrDeferredToOnPrem);

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

        // Signal (don't throw) when the module isn't configured — Apply will fail closed.
        var missingGroups = FindMissingRequiredGroups(key => _moduleConfig.GetValue(ModuleId, key));
        if (missingGroups.Count > 0)
            preview.Warnings.Add($"Module not configured: set {string.Join(", ", missingGroups)} in Module Config before applying.");

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

    private async Task SetRemoteMailboxAsync(string roomEmail, string customAttr9, string? mailTip, RoomOperationResult result, bool required = false)
    {
        if (string.IsNullOrWhiteSpace(_onPremServerUri))
        {
            // Normally on-prem is optional (cloud write already set the attribute). But when
            // the cloud write was deferred because the room is on-prem mastered, skipping here
            // means the attribute is written NOWHERE — that must fail, not report success.
            result.Steps.Add(new RoomOperationStep
            {
                Stage = "Set-RemoteMailbox",
                Success = OnPremSkipCountsAsSuccess(required),
                Error = required
                    ? "Room is on-prem mastered (cloud write rejected) but on-prem is not configured — CustomAttribute9/MailTip could not be written anywhere."
                    : "Skipped — on-prem not configured"
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
