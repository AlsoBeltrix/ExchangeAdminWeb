using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using CsvHelper;
using CsvHelper.Configuration;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class ConferenceRoomService : ExchangeServiceBase, Jobs.IConferenceRoomBulkOperations
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
    // Config helpers - all group addresses come from module config with fallbacks
    // -------------------------------------------------------------------------

    private string Cfg(string key, string fallback = "")
        => _moduleConfig.GetValue(ModuleId, key) ?? fallback;

    // Environment-specific values come from module config - no hardcoded defaults. When a
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

    /// <summary>A room by the string given - for previews and lookups that no write follows.</summary>
    public async Task<RoomInfo?> GetRoomInfoAsync(string roomEmail)
    {
        // Read-only: safe to retry on a dead pooled session.
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            ps.AddCommand("Get-Mailbox")
              .AddParameter("Identity", roomEmail)
              .AddParameter("ErrorAction", "Stop");
            var mbx = Invoke(ps, tracker).FirstOrDefault();
            return mbx == null ? null : ReadRoomDetails(ps, tracker, mbx, roomEmail);
        }, allowRetry: true);
    }

    /// <summary>
    /// The room the protection gate validated, read by its GUID (aud-a2d) - the room every write
    /// of the operation is bound to. Throws unless Exchange returns exactly that mailbox.
    /// </summary>
    public async Task<RoomInfo?> GetRoomInfoAsync(ValidatedMailbox room)
    {
        // Read-only: safe to retry on a dead pooled session.
        return await RunPooledQueryAsync(
            (ps, tracker) => ReadRoomDetails(ps, tracker, ReadValidatedMailbox(ps, tracker, room), room.Identity),
            allowRetry: true);
    }

    /// <summary>The rest of a room read, for the mailbox <paramref name="mbx"/> already read by <paramref name="identity"/>.</summary>
    private static RoomInfo ReadRoomDetails(PowerShell ps, ConnectionErrorTracker tracker, PSObject mbx, string identity)
    {
        ps.AddCommand("Get-Place")
          .AddParameter("Identity", identity)
          .AddParameter("ErrorAction", "SilentlyContinue");
        var placeResults = InvokeOptional(ps, tracker);
        var place = placeResults.FirstOrDefault();

        string timezone = "";
        try
        {
            ps.AddCommand("Get-MailboxRegionalConfiguration")
              .AddParameter("Identity", identity)
              .AddParameter("ErrorAction", "SilentlyContinue");
            var regionalResults = InvokeOptional(ps, tracker);
            var regional = regionalResults.FirstOrDefault();
            timezone = regional?.Properties["TimeZone"]?.Value?.ToString() ?? "";
        }
        catch { /* best-effort */ }

        return new RoomInfo
        {
            Email = mbx.Properties["PrimarySmtpAddress"]?.Value?.ToString() ?? identity,
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
    }

    // Room-list naming. The room list IS the building in Microsoft Room Finder; the
    // city above it is derived from each room's Place metadata (set via Set-Place /
    // Set-User), NOT from the list name. Keep these pure + static so the naming rule is
    // unit-testable without a live EXO connection. See
    // docs/ConferenceRooms-BuildingRoomList-Plan.md.
    public static string BuildRoomListName(string building) => $"{building.Trim()} Conference Rooms";
    public static string BuildLegacyRoomListName(string key) => $"RoomList-{key.Trim()}";

    // -------------------------------------------------------------------------
    // Room Finder synced-attribute mapping (City/State/Country -> on-prem AD)
    // -------------------------------------------------------------------------
    // City, StateOrProvince and CountryOrRegion are on-prem-AD-mastered and dir-synced, so
    // EXO Set-User rejects them ("being synchronized from your on-premises organization").
    // They are written on-prem with Set-ADUser instead. EXO Set-User -CountryOrRegion wrote
    // THREE AD attributes as a unit - c (alpha-2), co (English name), countryCode (ISO
    // numeric) - so the replacement must set all three to match existing rooms. See
    // docs/ConferenceRooms-RoomFinderMetadataApply-Plan.md.

    /// <summary>
    /// Maps an ISO 3166-1 alpha-2 country code (as Shaun's CSV provides, e.g. "IE") to the
    /// three AD country attributes EXO's Set-User -CountryOrRegion wrote as a unit:
    /// c (alpha-2, uppercased), co (English short name from RegionInfo, matching existing
    /// room objects), and countryCode (ISO 3166-1 numeric, from the static IsoCountryCodes
    /// table since .NET cannot supply it). Returns null for blank input (nothing to write).
    /// Throws ArgumentException for a non-blank value that is not a recognized alpha-2 code,
    /// so the row fails closed rather than writing a partial/inconsistent country.
    /// </summary>
    public static (string c, string co, int countryCode)? BuildCountryAttributes(string? countryOrRegion)
    {
        if (string.IsNullOrWhiteSpace(countryOrRegion))
            return null;

        var alpha2 = countryOrRegion.Trim().ToUpperInvariant();
        var numeric = IsoCountryCodes.GetNumeric(alpha2);
        if (numeric is null)
            throw new ArgumentException(
                $"'{countryOrRegion}' is not a recognized ISO 3166-1 alpha-2 country code. " +
                $"Use the two-letter code (e.g. IE, US, GB). If this is a new country, add it to IsoCountryCodes.cs.",
                nameof(countryOrRegion));

        string co;
        try
        {
            co = new System.Globalization.RegionInfo(alpha2).EnglishName;
        }
        catch (ArgumentException)
        {
            // In the IsoCountryCodes table but unknown to this runtime's RegionInfo
            // (extremely unlikely). Fail closed rather than write c/countryCode without co.
            throw new ArgumentException(
                $"Country '{alpha2}' has no display name on this system; cannot set the 'co' attribute.",
                nameof(countryOrRegion));
        }

        return (alpha2, co, numeric.Value);
    }

    /// <summary>
    /// Builds the AD attribute set to write via Set-ADUser for a Room Finder row's synced
    /// location fields: l (City), st (StateOrProvince), and the c/co/countryCode country
    /// triple. Blank fields are omitted (not written as empty). Returns an empty dictionary
    /// when nothing is set, so the caller can skip the Set-ADUser call entirely rather than
    /// invoking it with no attributes. Throws ArgumentException for an unmappable country.
    /// </summary>
    public static Dictionary<string, object> BuildSyncedUserAttributes(string? city, string? state, string? countryOrRegion)
    {
        var attrs = new Dictionary<string, object>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(city))
            attrs["l"] = city.Trim();
        if (!string.IsNullOrWhiteSpace(state))
            attrs["st"] = state.Trim();

        var country = BuildCountryAttributes(countryOrRegion);
        if (country is not null)
        {
            attrs["c"] = country.Value.c;
            attrs["co"] = country.Value.co;
            attrs["countryCode"] = country.Value.countryCode;
        }

        return attrs;
    }

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
    // attribute is written nowhere - that is a failure. Pure + static for unit testing.
    public static bool OnPremSkipCountsAsSuccess(bool cloudAttrDeferredToOnPrem)
        => !cloudAttrDeferredToOnPrem;

    // Classifies a failed cloud room-list add into one of three actions. Pure + static so the
    // branch is unit-testable without a live AD/EXO connection.
    //  - AdAttributeFix: the room's own AD attributes are wrong (not a room mailbox); on-prem
    //    membership add cannot fix it, so keep the existing guidance and do NOT fall back.
    //  - OnPremFallback: the list is on-prem mastered (DirSync'd) and EXO rejected the cloud
    //    add; retry the membership write on-prem.
    //  - Surface: any other error - report as-is, no fallback, no masking.
    // See docs/ConferenceRooms-OnPremRoomListAdd-Plan.md.
    public enum RoomListAddAction { Surface, AdAttributeFix, OnPremFallback }

    public static RoomListAddAction ClassifyRoomListAddFailure(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return RoomListAddAction.Surface;
        if (message.Contains("NonRoomMailboxAddToRoomList", StringComparison.OrdinalIgnoreCase)
            || message.Contains("isn't a room mailbox", StringComparison.OrdinalIgnoreCase))
            return RoomListAddAction.AdAttributeFix;
        if (IsOnPremMasteredWriteError(message))
            return RoomListAddAction.OnPremFallback;
        return RoomListAddAction.Surface;
    }

    // Success message for a room-list add, distinguishing the direct cloud add from the
    // on-prem-mastered fallback (which only appears after the next directory sync). Pure +
    // static for unit testing.
    public static string BuildRoomListAddedMessage(string roomListName, bool viaOnPrem)
        => viaOnPrem
            ? $"Added to on-prem room list '{roomListName}'. Membership was written on-prem and will appear in Exchange/Room Finder after the next directory sync (typically ~30 min)."
            : $"Added to room list '{roomListName}'.";

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
    // The contact-email keys are excluded (used only in cosmetic response text).
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
    /// from the building target (so the UI can warn the operator - Option ii). Returns the
    /// canonical-or-legacy target name, whether it already exists, whether the match was the
    /// legacy form, and the name of any differing city-named list found (else null).
    /// </summary>
    public async Task<(string? roomListName, bool exists, bool isLegacy, string? strayCityListName)> ResolveRoomListAsync(string building, string city)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            var canonicalName = BuildRoomListName(building);

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

            var list = FindRoomList(ps, tracker, building);
            return (list.Name, list.Exists, list.IsLegacy, strayCityListName);
        }, allowRetry: true);
    }

    /// <summary>
    /// A building's room list as Exchange reported it (aud-a2f): its name, whether it exists, whether
    /// that is the legacy name, and - for an existing list - whether it is synchronized from
    /// on-premises (<c>IsDirSynced</c>), or null when Exchange did not say.
    /// </summary>
    private sealed record RoomListTarget(string Name, bool Exists, bool IsLegacy, bool? IsDirSynced)
    {
        /// <summary>
        /// An existing list synchronized from on-premises. Exchange Online refuses the cloud add to
        /// it, so the room is added on-prem - and only to such a list, once the preflight proved it can.
        /// </summary>
        public bool TakesOnPremFallback => Exists && IsDirSynced == true;
    }

    // The canonical name first, then the legacy form: the order the preview has always used, so the
    // preview and the apply name the same list.
    private static RoomListTarget FindRoomList(PowerShell ps, ConnectionErrorTracker tracker, string building)
    {
        var canonicalName = BuildRoomListName(building);
        ps.AddCommand("Get-DistributionGroup")
          .AddParameter("Identity", canonicalName)
          .AddParameter("ErrorAction", "SilentlyContinue");
        var canonical = InvokeOptional(ps, tracker);
        if (canonical.Count > 0)
            return new RoomListTarget(canonicalName, Exists: true, IsLegacy: false, ReadIsDirSynced(canonical[0]));

        var legacyName = BuildLegacyRoomListName(building);
        ps.AddCommand("Get-DistributionGroup")
          .AddParameter("Identity", legacyName)
          .AddParameter("ErrorAction", "SilentlyContinue");
        var legacy = InvokeOptional(ps, tracker);
        if (legacy.Count > 0)
            return new RoomListTarget(legacyName, Exists: true, IsLegacy: true, ReadIsDirSynced(legacy[0]));

        return new RoomListTarget(canonicalName, Exists: false, IsLegacy: false, IsDirSynced: null);
    }

    // True, false, or null when the property is absent or not a boolean - which the preflight refuses
    // rather than guesses (aud-a2f).
    private static bool? ReadIsDirSynced(PSObject list) =>
        list.Properties["IsDirSynced"]?.Value switch
        {
            bool value => value,
            PSObject { BaseObject: bool wrapped } => wrapped,
            string text when bool.TryParse(text, out var parsed) => parsed,
            _ => null,
        };

    // The apply's room list, read before any write (aud-a2f). Read-only: safe to retry.
    private Task<RoomListTarget> ResolveRoomListTargetAsync(string building) =>
        RunPooledQueryAsync((ps, tracker) => FindRoomList(ps, tracker, building), allowRetry: true);

    private static readonly HashSet<string> ValidRoomRecipientTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "RoomMailbox", "RemoteRoomMailbox"
    };

    /// <summary>
    /// The validated room read back by its GUID, and refused unless it is a room mailbox. Returns
    /// the room's details for the operation (its primary address included), or the refusal.
    /// </summary>
    private async Task<(RoomInfo? info, string? error)> ValidateRoomRecipientAsync(ValidatedMailbox room, string roomEmail)
    {
        var info = await GetRoomInfoAsync(room);
        if (info == null)
            return (null, $"Room '{roomEmail}' not found.");
        if (!ValidRoomRecipientTypes.Contains(info.RecipientTypeDetails))
            return (null, $"'{roomEmail}' is a {info.RecipientTypeDetails}, not a room mailbox. Conference Room operations require RoomMailbox or RemoteRoomMailbox.");
        return (info, null);
    }

    // -------------------------------------------------------------------------
    // Room Finder - set metadata + add to room list
    // -------------------------------------------------------------------------

    /// <param name="roomEmail">The operator's input, for the result and messages only.</param>
    /// <param name="room">
    /// The room the protection gate validated (aud-a2d): every Exchange write takes its GUID and the
    /// Active Directory steps its proven objectGUID. Null refuses before anything is read or written.
    /// </param>
    public async Task<RoomOperationResult> SetRoomMetadataAndListAsync(
        string roomEmail, ValidatedMailbox? room, string city, string building, int capacity,
        string floor, string floorLabel, string displayDevice, string videoDevice,
        string countryOrRegion, string state, string timezone)
    {
        var result = new RoomOperationResult { Email = roomEmail };

        if (room is null)
        {
            result.Success = false;
            result.Message = ValidatedMailbox.Refusal(roomEmail);
            return result;
        }

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

        var (roomInfo, recipientError) = await ValidateRoomRecipientAsync(room, roomEmail);
        if (recipientError != null || roomInfo == null)
        {
            result.Success = false;
            result.Message = recipientError ?? $"Room '{roomEmail}' not found.";
            return result;
        }

        // Preflight the AD step BEFORE any mutation. Set-Place (Step 1) commits EXO-side
        // metadata; if the AD prerequisites for Step 2 are bad (unmappable country, missing/
        // unavailable credential, no validated AD object) we must fail the row here so EXO is
        // never partially written. Without this guard a bad-AD row was reported failed but had
        // already committed Set-Place - the partial-apply defect from the 2026-06-17 review.
        var adPreflightError = await PreflightSyncedAttributesViaAdAsync(room, roomEmail, city, state, countryOrRegion);
        if (adPreflightError != null)
        {
            result.Success = false;
            result.Message = adPreflightError;
            result.Steps.Add(new RoomOperationStep { Stage = "Set-ADUser preflight (City/State/Country)", Success = false, Error = adPreflightError });
            return result;
        }

        // aud-a2f. The room list, the same way and for the same reason: resolved here, and when it
        // is synchronized from on-premises its on-prem add proven here, before Set-Place. Checked
        // only at the add, a proven AD object, the AD credential, that object and the list's on-prem
        // group were all first tested after Set-Place and the timezone had committed - so a
        // cloud-only room on a synced list came back partial on every re-run.
        RoomListTarget? roomList = null;
        if (!string.IsNullOrWhiteSpace(building))
        {
            roomList = await ResolveRoomListTargetAsync(building);
            var listPreflightError = await PreflightRoomListAsync(room, roomInfo.Email, roomEmail, roomList);
            if (listPreflightError != null)
            {
                result.Success = false;
                result.Message = listPreflightError;
                result.Steps.Add(new RoomOperationStep { Stage = "Room list preflight", Success = false, Error = listPreflightError });
                return result;
            }
        }

        // Step 1: Set-Place (EXO) - Building/Capacity/Floor/devices are NOT dir-synced, so
        // EXO accepts them. City is intentionally NOT sent here; it is a synced attribute
        // written on-prem in Step 2.
        // Single-write (Set-Place): safe to retry on a dead pooled session. This is the step
        // that failed mid-batch in the observed bug (CR-BUG-1) before any cmdlet committed.
        var placeResult = await RunAsync((ps, tracker) =>
        {
            _operationTrace?.Step("SetPlace", backend: "EXO", command: "Set-Place", target: room.Identity);
            var placeCmd = ps.AddCommand("Set-Place")
              .AddParameter("Identity", room.Identity)
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
        }, allowRetry: true);

        if (!placeResult.Success)
        {
            result.Success = false;
            result.Message = placeResult.Message;
            result.Steps.Add(new RoomOperationStep { Stage = "Set-Place", Success = false, Error = placeResult.Message });
            return result;
        }

        // Step 2: Set-ADUser (on-prem AD) - City/State/Country are dir-synced AD attributes
        // that EXO Set-User rejects. Written on the on-prem object instead (the bug fix).
        _operationTrace?.Step("SetADUser", backend: "AD", command: "Set-ADUser", target: room.DirectoryObjectGuid ?? roomEmail);
        var (adError, adWritten) = await SetSyncedAttributesViaAdAsync(room, roomEmail, city, state, countryOrRegion);
        // aud-a2g. The object Set-ADUser wrote, if it wrote one - recorded now, so a later step
        // failing cannot hide it, and only from the write itself, so a row that wrote nothing in
        // Active Directory cannot claim it did.
        result.WrittenDirectoryObject = adWritten;
        if (adError != null)
        {
            // Set-Place already committed EXO metadata; this row is now half-configured.
            result.Success = false;
            result.Partial = true;
            result.Message = $"Partial: Building/Capacity/Floor were set in Exchange Online, but City/State/Country failed: {adError} Re-run this row after fixing the cause.";
            result.Steps.Add(new RoomOperationStep { Stage = "Set-ADUser (City/State/Country)", Success = false, Error = adError });
            return result;
        }
        result.Steps.Add(new RoomOperationStep { Stage = "Set-ADUser (City/State/Country)", Success = true });

        // Step 3: Timezone + WorkDays (EXO) - mailbox regional config is not synced.
        if (!string.IsNullOrWhiteSpace(timezone))
        {
            var tzResult = await RunAsync((ps, tracker) =>
            {
                _operationTrace?.Step("SetTimezone", backend: "EXO", command: "Set-MailboxRegionalConfiguration", target: room.Identity);
                ps.AddCommand("Set-MailboxRegionalConfiguration")
                  .AddParameter("Identity", room.Identity)
                  .AddParameter("TimeZone", timezone)
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps, tracker);

                ps.AddCommand("Set-MailboxCalendarConfiguration")
                  .AddParameter("Identity", room.Identity)
                  .AddParameter("WorkingHoursTimeZone", timezone)
                  .AddParameter("WorkDays", "None")
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps, tracker);
                result.Steps.Add(new RoomOperationStep { Stage = "Set-Timezone/WorkDays", Success = true });
            });

            if (!tzResult.Success)
            {
                // Earlier steps (Set-Place, and City/State/Country) already committed.
                result.Success = false;
                result.Partial = true;
                result.Message = $"Partial: metadata and City/State/Country were set, but timezone/working-hours failed: {tzResult.Message} Re-run this row after fixing the cause.";
                result.Steps.Add(new RoomOperationStep { Stage = "Set-Timezone/WorkDays", Success = false, Error = tzResult.Message });
                return result;
            }
        }

        // Step 4: Room list - keyed on building (the room list IS the building; city is
        // metadata only). A room with no building is not added to any list. The list is the one
        // resolved and proven before the writes (aud-a2f), not looked up again.
        if (roomList is not null)
        {
            var listResult = await AddToRoomListAsync(room, roomInfo.Email, roomEmail, roomList);
            result.WrittenDirectoryObject ??= listResult.WrittenDirectoryObject; // the on-prem add's (aud-a2g)
            result.Steps.AddRange(listResult.Steps);
            if (!listResult.Success)
            {
                if (listResult.AdAttributeFixRequired)
                {
                    result.AdAttributeFixRequired = true;
                    result.AdAttributeDetail = listResult.AdAttributeDetail;
                }
                // Metadata (and City/State/Country) already committed; only the room-list
                // membership failed, so the room is half-configured.
                result.Success = false;
                result.Partial = true;
                result.Message = $"Partial: room metadata was set, but adding the room to its room list failed: {listResult.Message} Re-run this row after fixing the cause.";
                return result;
            }
        }

        result.Success = true;
        result.Message = string.IsNullOrWhiteSpace(city) && string.IsNullOrWhiteSpace(state) && string.IsNullOrWhiteSpace(countryOrRegion)
            ? "Room metadata and room list configured."
            : "Room metadata and room list configured. City/State/Country were written on-prem and will appear in Exchange/Room Finder after the next directory sync (typically ~30 min).";
        return result;
    }

    /// <param name="room">The validated room (aud-a2d): the member added, by GUID or proven objectGUID.</param>
    /// <param name="roomPrimary">
    /// The room's primary address as Exchange returned it for <paramref name="room"/> - compared
    /// against the list's members only, never written.
    /// </param>
    /// <param name="roomEmail">The operator's input, for the result only.</param>
    /// <param name="list">The room list resolved, and its on-prem add proven, before any write (aud-a2f).</param>
    private async Task<RoomOperationResult> AddToRoomListAsync(
        ValidatedMailbox room, string roomPrimary, string roomEmail, RoomListTarget list)
    {
        var result = new RoomOperationResult { Email = roomEmail };
        var (roomListName, exists, isLegacy) = (list.Name, list.Exists, list.IsLegacy);

        var opResult = await RunAsync((ps, tracker) =>
        {
            if (!exists)
            {
                // Room lists are created cloud-side (New-DistributionGroup runs against the
                // EXO pool), so NO -OrganizationalUnit is set: Exchange Online does not know
                // on-prem OUs and rejects an on-prem OU path ("organizational unit not found").
                // The list is created in EXO's default location. This makes the room list a
                // cloud-only object (not synced from on-prem like other DLs) - accepted by the
                // owner 2026-06-18, consistent with the pending on-prem Exchange decommission.
                _operationTrace?.Step("CreateRoomList", backend: "EXO", command: "New-DistributionGroup", target: roomListName);
                ps.AddCommand("New-DistributionGroup")
                  .AddParameter("Name", roomListName)
                  .AddParameter("RoomList")
                  .AddParameter("ErrorAction", "Stop");
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
            var alreadyMember = IsListMember(members, roomPrimary);

            if (!alreadyMember)
            {
                _operationTrace?.Step("AddToRoomList", backend: "EXO", command: "Add-DistributionGroupMember", target: room.Identity);
                ps.AddCommand("Add-DistributionGroupMember")
                  .AddParameter("Identity", roomListName)
                  .AddParameter("Member", room.Identity)
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
            switch (ClassifyRoomListAddFailure(opResult.Message))
            {
                // The room's own AD attributes are wrong (not a room mailbox). An on-prem
                // membership add cannot fix this, so keep the existing guidance and do NOT
                // fall back.
                case RoomListAddAction.AdAttributeFix:
                    result.Success = false;
                    result.Message = opResult.Message;
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
                    return result;

                // On-prem-mastered (DirSync'd) room list: EXO rejects the cloud add. Fall back
                // to adding the membership on-prem, exactly as City/State/Country are written
                // on-prem and synced up.
                case RoomListAddAction.OnPremFallback:
                    // aud-a2f. Only a list Exchange reported as synchronized from on-premises - the
                    // one whose on-prem add the preflight proved before anything was written - is
                    // added on-prem. Any other list refusing the cloud add is surfaced: nothing
                    // proved that add could complete, and an on-prem group found by the list's name
                    // would not be the list Exchange holds.
                    if (!list.TakesOnPremFallback)
                    {
                        result.Success = false;
                        result.Message = $"{opResult.Message} Exchange reported room list '{roomListName}' as not synchronized from on-premises, so the room was not added on-prem instead.";
                        result.Steps.Add(new RoomOperationStep { Stage = "Add to room list", Success = false, Error = result.Message });
                        return result;
                    }

                    _operationTrace?.Step("AddToRoomListOnPrem", backend: "AD", command: "Add-ADGroupMember", target: room.DirectoryObjectGuid ?? roomEmail);
                    var (adError, adWritten) = await AddToRoomListViaAdAsync(room, roomEmail, roomListName);
                    if (adError == null)
                    {
                        result.Success = true;
                        result.WrittenDirectoryObject = adWritten; // null when it was already a member (aud-a2g)
                        result.Steps.Add(new RoomOperationStep { Stage = $"Add to '{roomListName}' (on-prem)", Success = true });
                        result.Message = BuildRoomListAddedMessage(roomListName, viaOnPrem: true);
                        return result;
                    }

                    result.Success = false;
                    result.Message = adError;
                    result.Steps.Add(new RoomOperationStep { Stage = $"Add to '{roomListName}' (on-prem)", Success = false, Error = adError });
                    return result;

                // Any other error: surface as-is, no fallback, no masking.
                default:
                    result.Success = false;
                    result.Message = opResult.Message;
                    result.Steps.Add(new RoomOperationStep { Stage = "Add to room list", Success = false, Error = opResult.Message });
                    return result;
            }
        }

        result.Success = true;
        result.Message = BuildRoomListAddedMessage(roomListName, viaOnPrem: false);
        return result;
    }

    // Whether a room list's members include the room. Compared with the address Exchange returned
    // for the validated room, not the typed string (aud-a2d): an alias typed for a room already on
    // the list used to read as "not a member". The add and the room-list preflight both ask this,
    // so they cannot disagree about whether the add will write anything (aud-a2f).
    private static bool IsListMember(IEnumerable<PSObject> members, string roomPrimary) =>
        members.Any(m =>
            string.Equals(m.Properties["PrimarySmtpAddress"]?.Value?.ToString(), roomPrimary, StringComparison.OrdinalIgnoreCase));

    // aud-a2f. Whether the room is already on the list, read before any write. Read-only: safe to retry.
    private Task<bool> IsOnRoomListAsync(string roomListName, string roomPrimary) =>
        RunPooledQueryAsync((ps, tracker) =>
        {
            ps.AddCommand("Get-DistributionGroupMember")
              .AddParameter("Identity", roomListName)
              .AddParameter("ErrorAction", "SilentlyContinue");
            return IsListMember(InvokeOptional(ps, tracker), roomPrimary);
        }, allowRetry: true);

    // -------------------------------------------------------------------------
    // Room Type - full 6-type implementation matching PS script
    // -------------------------------------------------------------------------

    /// <param name="roomEmail">The operator's input, for the result and messages only.</param>
    /// <param name="room">
    /// The room the protection gate validated (aud-a2d): every cmdlet below takes its GUID and the
    /// calendar folder is <c>&lt;GUID&gt;:\&lt;folder&gt;</c>. Null refuses before anything is read.
    /// </param>
    public async Task<RoomOperationResult> SetRoomTypeAsync(
        string roomEmail, ValidatedMailbox? room, RoomType roomType, string timezone,
        string site = "none", string? arbiter = null,
        bool removeExistingPermissions = false)
    {
        var result = new RoomOperationResult { Email = roomEmail };

        if (room is null)
        {
            result.Success = false;
            result.Message = ValidatedMailbox.Refusal(roomEmail);
            return result;
        }

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

        var (_, recipientError) = await ValidateRoomRecipientAsync(room, roomEmail);
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
        // becomes the ONLY path that writes these attributes, so it is mandatory - see Step 9.
        bool cloudAttrDeferredToOnPrem = false;

        var opResult = await RunAsync((ps, tracker) =>
        {
            // Step 1: Read the validated room back by its GUID, refusing unless Exchange returns that
            // mailbox (aud-a2d). Every step below writes room.Identity; this resolved the typed
            // string and wrote its primary address, which Exchange resolved again per cmdlet.
            _operationTrace?.Step("ResolveRoom", backend: "EXO", command: "Get-Mailbox", target: room.Identity);
            ReadValidatedMailbox(ps, tracker, room);
            result.Steps.Add(new RoomOperationStep { Stage = "Resolve room", Success = true });

            // Get calendar folder name - of the validated room
            _operationTrace?.Step("GetCalendarFolder", backend: "EXO", command: "Get-MailboxFolderStatistics", target: room.Identity);
            ps.AddCommand("Get-MailboxFolderStatistics")
              .AddParameter("Identity", room.Identity)
              .AddParameter("ErrorAction", "Stop");
            var folderStats = Invoke(ps, tracker);
            var calFolder = folderStats
                .FirstOrDefault(f => f.Properties["FolderType"]?.Value?.ToString() == "Calendar");
            var calFolderName = calFolder?.Properties["Name"]?.Value?.ToString() ?? "Calendar";
            var calFolderPath = $"{room.Identity}:\\{calFolderName}";
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
            _operationTrace?.Step("SetCalendarProcessing", backend: "EXO", command: "Set-CalendarProcessing", target: room.Identity);
            var procCmd = ps.AddCommand("Set-CalendarProcessing")
              .AddParameter("Identity", room.Identity)
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
            // on-prem via Set-RemoteMailbox (Step 9) - so run it best-effort and classify the
            // result. The cloud rejection is expected and informational; any OTHER error is a
            // real failure and is surfaced as such. Best-effort also avoids leaving a failed
            // command queued on the shared pipeline (which previously poisoned later steps).
            _operationTrace?.Step("SetMailbox", backend: "EXO", command: "Set-Mailbox", target: room.Identity);
            var mbxCmd = ps.AddCommand("Set-Mailbox")
              .AddParameter("Identity", room.Identity)
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
                    Stage = "CustomAttribute9/MailTip set on-prem (synced room - cloud write skipped)",
                    Success = true
                });
            }
            else
            {
                // Unexpected cloud error - keep it visible as a failure.
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
                _operationTrace?.Step("SetTimezone", backend: "EXO", command: "Set-MailboxRegionalConfiguration", target: room.Identity);
                ps.AddCommand("Set-MailboxRegionalConfiguration")
                  .AddParameter("Identity", room.Identity)
                  .AddParameter("TimeZone", timezone)
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps, tracker);

                ps.AddCommand("Set-MailboxCalendarConfiguration")
                  .AddParameter("Identity", room.Identity)
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
    // CSV parsing - shared, testable. Email selection matches SetupRoomType.ps1:
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
                    SkipReason = "Missing identity - no value in any of: " + string.Join(", ", IdentityColumns),
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
                    SkipReason = "Missing identity - no value in any of: " + string.Join(", ", IdentityColumns),
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

        // Signal (don't throw) when the module isn't configured - Apply will fail closed.
        var missingGroups = FindMissingRequiredGroups(key => _moduleConfig.GetValue(ModuleId, key));
        if (missingGroups.Count > 0)
            preview.Warnings.Add($"Module not configured: set {string.Join(", ", missingGroups)} in Module Config before applying.");

        return preview;
    }

    // -------------------------------------------------------------------------
    // Type-specific settings - mirrors SetupRoomType.ps1 switch block
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
                Stage = $"Permission: {user} -> {accessRights}",
                Success = true
            });
        }
        catch (Exception ex)
        {
            result.Steps.Add(new RoomOperationStep
            {
                Stage = $"Permission: {user} -> {accessRights}",
                Success = false,
                Error = ex.Message
            });
        }
    }

    // On-prem Exchange is being decommissioned (owner decision 2026-06-12; see
    // docs/ProdReadiness-Plan.md Q1/AC14) and all conference rooms are cloud
    // mailboxes. The live Set-RemoteMailbox path was retired rather than repaired:
    // its credential lookup never worked anyway (the catalog exposed
    // OnPremDelineaSecretId, which no code read). A cloud-mastered room skips this
    // step as success; an on-prem mastered room (cloud write rejected) fails
    // explicitly so the failure is visible and audited with the operation result.
    private Task SetRemoteMailboxAsync(string roomEmail, string customAttr9, string? mailTip, RoomOperationResult result, bool required = false)
    {
        result.Steps.Add(new RoomOperationStep
        {
            Stage = "Set-RemoteMailbox",
            Success = OnPremSkipCountsAsSuccess(required),
            Error = required
                ? $"Room {roomEmail} is on-prem mastered (cloud write rejected), but on-prem Exchange is decommissioned - CustomAttribute9/MailTip could not be written. Migrate the room mailbox to Exchange Online."
                : "Skipped - on-prem Exchange decommissioned"
        });
        return Task.CompletedTask;
    }

    // virtual: a test seam (aud-a2d) so the Active Directory steps can be driven against an
    // in-process runspace of stand-ins. No behaviour change: this is the runspace the three AD
    // steps below opened for themselves.
    protected virtual Runspace OpenDirectoryRunspace()
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        var runspace = RunspaceFactory.CreateRunspace(iss);
        runspace.Open();
        return runspace;
    }

    // Refusal for an AD step with no validated object to write. A cloud-only room has none: the
    // gate found no on-premises object behind it, so there is nothing City/State/Country or an
    // on-prem list membership could be written to.
    private static string NoDirectoryObject(string roomEmail, string what) =>
        $"No on-premises Active Directory object was validated for '{roomEmail}' (a cloud-only room has none), so {what}.";

    // Writes the dir-synced location attributes (City/State/Country) on the on-prem AD
    // object via Set-ADUser. These attributes are AD-mastered, so EXO Set-User rejects them
    // ("being synchronized from your on-premises organization") - the root cause of the
    // Room Finder apply failure (docs/ConferenceRooms-RoomFinderMetadataApply-Plan.md).
    // Returns (null, the objectGUID written) once Set-ADUser has succeeded, (null, null) when
    // there is nothing to write, and (the failure message for the row, null) otherwise - the
    // object is reported only by a write that happened (aud-a2g).
    // Runs in its own AD runspace (mirrors Comms10k / CheckAdGroupMembership), not through
    // the EXO pool.
    //
    // aud-a2d. The object is the one the protection gate PROVED owns the room's mailbox - the
    // only AD object holding its ExchangeGuid in msExchMailboxGuid (aud-a2c) - written by that
    // objectGUID. It used to be found by matching the TYPED string against userPrincipalName, on
    // the reasoning that UPN equals the address "in this environment"; a different object holding
    // the typed string as its UPN was written, and nothing had checked it.
    //
    // Pre-mutation validation for the AD step. Runs every check that can be made WITHOUT
    // mutating anything - country mapping, a validated object to write, credential
    // availability, and that object being readable - so the caller can fail the row BEFORE the
    // EXO Set-Place write commits. This is the all-or-nothing guard: without it, a row whose
    // AD prerequisites are bad (unmappable country, no validated object, missing/unavailable
    // credential) would already have committed EXO metadata before failing.
    // Returns an error message to abort the row, or null when the AD write is expected to
    // succeed. (Two-system writes can never be fully atomic; a genuine Set-ADUser failure
    // after a passing preflight remains an inherent, accepted residual.)
    private async Task<string?> PreflightSyncedAttributesViaAdAsync(ValidatedMailbox room, string roomEmail, string city, string state, string countryOrRegion)
    {
        Dictionary<string, object> attrs;
        try
        {
            attrs = BuildSyncedUserAttributes(city, state, countryOrRegion);
        }
        catch (ArgumentException ex)
        {
            return ex.Message; // unmappable country - fail the row closed
        }

        if (attrs.Count == 0)
            return null; // nothing synced to write for this row - AD step will be a no-op

        if (room.DirectoryObjectGuid is not { } objectGuid)
            return NoDirectoryObject(roomEmail, "City/State/Country could not be written");

        var creds = await GetModuleCredentialsAsync($"Set-ADUser preflight for {roomEmail}");
        if (creds is null)
            return "On-prem AD credential is not configured for Conference Rooms (set the AD Delinea Secret ID in Module Config). City/State/Country could not be written.";

        return await Task.Run(() =>
        {
            try
            {
                using var runspace = OpenDirectoryRunspace();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                if (ps.HadErrors)
                    return FirstError(ps) ?? "Failed to load the ActiveDirectory module.";
                ps.Commands.Clear();

                var credential = CreateAdCredential(creds.Value.username, creds.Value.password, creds.Value.domain);
                return ConfirmDirectoryObject(ps, objectGuid, roomEmail, credential); // null = readable, AD write expected to succeed
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Set-ADUser preflight failed for {Room}", roomEmail);
                return $"Failed to validate the AD object before writing: {ex.Message}";
            }
        });
    }

    private async Task<(string? error, string? written)> SetSyncedAttributesViaAdAsync(ValidatedMailbox room, string roomEmail, string city, string state, string countryOrRegion)
    {
        Dictionary<string, object> attrs;
        try
        {
            attrs = BuildSyncedUserAttributes(city, state, countryOrRegion);
        }
        catch (ArgumentException ex)
        {
            return (ex.Message, null); // unmappable country - fail the row closed
        }

        if (attrs.Count == 0)
            return (null, null); // nothing synced to write for this row - and nothing written

        if (room.DirectoryObjectGuid is not { } objectGuid)
            return (NoDirectoryObject(roomEmail, "City/State/Country could not be written"), null);

        var creds = await GetModuleCredentialsAsync($"Set-ADUser synced attributes for {roomEmail}");
        if (creds is null)
            return ("On-prem AD credential is not configured for Conference Rooms (set the AD Delinea Secret ID in Module Config). City/State/Country could not be written.", null);

        return await Task.Run<(string? error, string? written)>(() =>
        {
            try
            {
                using var runspace = OpenDirectoryRunspace();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                if (ps.HadErrors)
                    return (FirstError(ps) ?? "Failed to load the ActiveDirectory module.", null);
                ps.Commands.Clear();

                var credential = CreateAdCredential(creds.Value.username, creds.Value.password, creds.Value.domain);

                var confirmError = ConfirmDirectoryObject(ps, objectGuid, roomEmail, credential);
                if (confirmError != null)
                    return (confirmError, null);

                // Write by the validated objectGUID (immutable identity), never by a name.
                ps.AddCommand("Set-ADUser")
                  .AddParameter("Identity", objectGuid)
                  .AddParameter("Replace", new System.Collections.Hashtable(attrs))
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                if (ps.HadErrors)
                    return (FirstError(ps) ?? $"Set-ADUser failed for {roomEmail}.", null);

                return (null, objectGuid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Set-ADUser synced-attribute write failed for {Room}", roomEmail);
                return ($"Failed to write City/State/Country on the AD object: {ex.Message}", null);
            }
        });
    }

    // aud-a2f. Whether the room can be added to its room list, settled before any write. A list
    // that does not exist yet is created cloud-side and a cloud list takes the cloud add, so neither
    // needs anything further. A list synchronized from on-premises takes the room only on-prem
    // (AddToRoomListViaAdAsync below), so everything that add needs and can be checked without
    // writing - a proven AD object, the AD credential, that object readable by its objectGUID, and
    // exactly one on-prem group for the list - is checked here, and its absence refuses the row
    // while nothing has been written. A list whose sync state Exchange did not report is refused
    // too: whether the row can complete is then unknown, and the old order found out by writing.
    // Returns the refusal, or null. (A genuine Add-ADGroupMember failure after a passing preflight -
    // missing write-members delegation, say - remains the inherent two-system residual.)
    // roomPrimary is the address Exchange returned for the validated room: the membership test's key.
    private async Task<string?> PreflightRoomListAsync(ValidatedMailbox room, string roomPrimary, string roomEmail, RoomListTarget list)
    {
        if (!list.Exists)
            return null;

        if (list.IsDirSynced is null)
            return $"Exchange did not report whether room list '{list.Name}' is synchronized from on-premises, so it cannot be established before writing that the room can be added to it. Nothing was written.";

        if (!list.TakesOnPremFallback)
            return null;

        // Already on the list, the add writes nothing - it finds the room among the members, by the
        // same test - so nothing on-prem is needed or asked for. Without this, a synced room re-run on
        // its synced list was refused whenever Active Directory could not be proven: a row that, before
        // this preflight existed, completed without touching the list at all.
        if (await IsOnRoomListAsync(list.Name, roomPrimary))
            return null;

        if (room.DirectoryObjectGuid is not { } roomGuid)
            return NoDirectoryObject(roomEmail, $"it cannot be added to room list '{list.Name}', which is synchronized from on-premises and takes members only there. Nothing was written");

        var creds = await GetModuleCredentialsAsync($"Add-ADGroupMember preflight for {roomEmail}");
        if (creds is null)
            return $"On-prem AD credential is not configured for Conference Rooms (set the AD Delinea Secret ID in Module Config), and room list '{list.Name}' is synchronized from on-premises, so the room cannot be added to it. Nothing was written.";

        return await Task.Run(() =>
        {
            try
            {
                using var runspace = OpenDirectoryRunspace();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                if (ps.HadErrors)
                    return FirstError(ps) ?? "Failed to load the ActiveDirectory module.";
                ps.Commands.Clear();

                var credential = CreateAdCredential(creds.Value.username, creds.Value.password, creds.Value.domain);

                // The two objects the on-prem add names - the room's proven object and the list's
                // group - each read here exactly as the add will read them, without writing.
                var roomError = ConfirmDirectoryObject(ps, roomGuid, roomEmail, credential);
                if (roomError != null)
                    return roomError;

                var (groupError, _) = ResolveAdGroupGuid(ps, list.Name, credential);
                return groupError;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "On-prem room-list preflight failed for {Room} on {List}", roomEmail, list.Name);
                return $"Failed to validate the on-prem room list '{list.Name}' before writing: {ex.Message} Nothing was written.";
            }
        });
    }

    // Adds a room mailbox to an on-prem-mastered (DirSync'd) room-list group via the on-prem
    // AD object. Exchange Online refuses Add-DistributionGroupMember against a synced group
    // ("out of the current user's write scope ... being synchronized from your on-premises
    // organization"), so membership must be written on-prem and then syncs up (~30 min) - the
    // same shape as the Set-ADUser City/State/Country handling. Returns (null, the room's
    // objectGUID) once Add-ADGroupMember has succeeded, (null, null) for the already-a-member
    // no-op - nothing was written - and (a failure message, null) otherwise (aud-a2g). See
    // docs/ConferenceRooms-OnPremRoomListAdd-Plan.md.
    //
    // aud-a2d. The member added is the AD object the protection gate proved owns the room's
    // mailbox, by objectGUID - not a userPrincipalName match of the typed string.
    private async Task<(string? error, string? written)> AddToRoomListViaAdAsync(ValidatedMailbox room, string roomEmail, string roomListName)
    {
        if (room.DirectoryObjectGuid is not { } roomGuid)
            return (NoDirectoryObject(roomEmail, "the room could not be added to the on-prem room list"), null);

        var creds = await GetModuleCredentialsAsync($"Add-ADGroupMember on-prem room list for {roomEmail}");
        if (creds is null)
            return ("On-prem AD credential is not configured for Conference Rooms (set the AD Delinea Secret ID in Module Config). The room could not be added to the on-prem room list.", null);

        return await Task.Run<(string? error, string? written)>(() =>
        {
            try
            {
                using var runspace = OpenDirectoryRunspace();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                if (ps.HadErrors)
                    return (FirstError(ps) ?? "Failed to load the ActiveDirectory module.", null);
                ps.Commands.Clear();

                var credential = CreateAdCredential(creds.Value.username, creds.Value.password, creds.Value.domain);

                // BOTH objects by immutable GUID, fail-closed, before any write - so the wrong
                // member is never added to the wrong group. The room's is the validated one.
                var roomError = ConfirmDirectoryObject(ps, roomGuid, roomEmail, credential);
                if (roomError != null)
                    return (roomError, null);

                var (groupError, groupGuid) = ResolveAdGroupGuid(ps, roomListName, credential);
                if (groupError != null)
                    return (groupError, null);

                // Idempotency: skip the add if the room is already a member (matches the cloud
                // path's already-member no-op so a re-run is safe).
                ps.AddCommand("Get-ADGroupMember")
                  .AddParameter("Identity", groupGuid)
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                var members = ps.Invoke();
                if (ps.HadErrors)
                    return (FirstError(ps) ?? $"Failed to read membership of on-prem room list '{roomListName}'.", null);
                ps.Commands.Clear();

                var alreadyMember = members.Any(m =>
                    string.Equals(m.Properties["ObjectGUID"]?.Value?.ToString(), roomGuid, StringComparison.OrdinalIgnoreCase));
                if (alreadyMember)
                    return (null, null);

                // Add by GUID on both sides (immutable identity), not by name/UPN.
                ps.AddCommand("Add-ADGroupMember")
                  .AddParameter("Identity", groupGuid)
                  .AddParameter("Members", roomGuid)
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                ps.Invoke();
                if (ps.HadErrors)
                    return (FirstError(ps) ?? $"Add-ADGroupMember failed for {roomEmail} on '{roomListName}'.", null);

                return (null, roomGuid);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Add-ADGroupMember on-prem room-list write failed for {Room} on {List}", roomEmail, roomListName);
                return ($"Failed to add the room to the on-prem room list on the AD object: {ex.Message}", null);
            }
        });
    }

    // Confirms the validated room's AD object can be read, by its objectGUID, before an AD write
    // (aud-a2d). Shared by the pre-mutation preflight and the writes, so they read the same object.
    // Returns an error message, or null when exactly that object answered. It replaced a lookup
    // of the typed string by userPrincipalName, which found whichever object held that UPN.
    // Assumes the ActiveDirectory module is already imported into the runspace.
    private static string? ConfirmDirectoryObject(PowerShell ps, string objectGuid, string roomEmail, PSCredential credential)
    {
        ps.AddCommand("Get-ADUser")
          .AddParameter("Identity", objectGuid)
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var found = ps.Invoke();
        if (ps.HadErrors)
            return FirstError(ps) ?? $"AD lookup of the validated object {objectGuid} for {roomEmail} failed.";
        ps.Commands.Clear();

        if (found.Count != 1)
            return $"The AD object validated for '{roomEmail}' ({objectGuid}) could not be read ({found.Count} found). Nothing was written.";

        var readGuid = found[0].Properties["ObjectGUID"]?.Value?.ToString();
        if (!string.Equals(readGuid, objectGuid, StringComparison.OrdinalIgnoreCase))
            return $"Active Directory answered {objectGuid} with a different object ({readGuid ?? "no ObjectGUID"}). Nothing was written.";

        return null;
    }

    // Resolve a room list (distribution group) to exactly one immutable on-prem ObjectGUID.
    // Matches on mail first, then displayName/name, and refuses on not-found or ambiguous -
    // same fail-closed posture as ConfirmDirectoryObject so an on-prem membership write can never
    // target the wrong group. Shared by the room-list preflight and the add (aud-a2f), so they
    // resolve the same group. Assumes the ActiveDirectory module is already imported.
    private static (string? error, string? objectGuid) ResolveAdGroupGuid(PowerShell ps, string roomListName, PSCredential credential)
    {
        var escaped = roomListName.Replace("'", "''");
        ps.AddCommand("Get-ADGroup")
          .AddParameter("Filter", $"mail -eq '{escaped}' -or displayName -eq '{escaped}' -or name -eq '{escaped}'")
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var found = ps.Invoke();
        if (ps.HadErrors)
            return (FirstError(ps) ?? $"AD lookup failed for room list '{roomListName}'.", null);
        ps.Commands.Clear();

        if (found.Count == 0)
            return ($"No on-prem AD group found for room list '{roomListName}'. The room was not added to the list.", null);
        if (found.Count > 1)
            return ($"Multiple on-prem AD groups ({found.Count}) match room list '{roomListName}'. Refusing to write to avoid the wrong group.", null);

        var objectGuid = found[0].Properties["ObjectGUID"]?.Value;
        if (objectGuid is null)
            return ($"Resolved on-prem AD group for room list '{roomListName}' has no ObjectGUID; refusing to write.", null);

        return (null, objectGuid.ToString());
    }

    private static string? FirstError(PowerShell ps) =>
        ps.Streams.Error.Select(e => e.Exception?.Message ?? e.ToString())
          .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m));

    private static PSCredential CreateAdCredential(string username, string password, string domain)
    {
        var fullUsername = username.Contains('\\') || username.Contains('@')
            ? username
            : $"{domain}\\{username}";
        var securePassword = new System.Security.SecureString();
        foreach (var c in password)
            securePassword.AppendChar(c);
        securePassword.MakeReadOnly();
        return new PSCredential(fullUsername, securePassword);
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
