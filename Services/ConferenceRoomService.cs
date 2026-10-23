using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Claims;
using CsvHelper;
using CsvHelper.Configuration;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class ConferenceRoomService : ExchangeServiceBase, Jobs.IConferenceRoomBulkOperations
{
    private readonly ModuleConfigService _moduleConfig;
    private const string ModuleId = "ConferenceRooms";

    // aud-a2e. The Protected Group Targets check on the room list a Room Finder row writes into (CheckRoomListTarget). Optional
    // as the base's credential service is: the container supplies both, and a host without them refuses every room-list
    // write - it does not skip the check.
    private readonly ProtectedPrincipalService? _protectedPrincipals;
    private readonly ProtectedPrincipalServicerService? _servicers;

    public ConferenceRoomService(
        ExoConnectionPool exoPool,
        DelineaService delineaService,
        ModuleConfigService moduleConfig,
        ModuleCredentialService moduleCredentials,
        OperationTraceService operationTrace,
        IConfiguration config,
        ILogger<ConferenceRoomService> logger,
        ProtectedPrincipalService? protectedPrincipals = null,
        ProtectedPrincipalServicerService? servicers = null)
        : base(exoPool, delineaService, logger,
               config["OnPremExchange:ServerUri"] ?? "",
               moduleCredentials,
               ModuleId,
               operationTrace)
    {
        _moduleConfig = moduleConfig;
        _protectedPrincipals = protectedPrincipals;
        _servicers = servicers;
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
        // aud-a2v. Neither refusal here names its parameter: an ArgumentException that does adds " (Parameter '<name>')" to
        // its Message, and the Message is the operator's - the row shows it and the preflight logs it - so the refusal ended
        // with a .NET artifact. Nothing reads ParamName.
        if (numeric is null)
            throw new ArgumentException(
                $"'{countryOrRegion}' is not a recognized ISO 3166-1 alpha-2 country code. " +
                $"Use the two-letter code (e.g. IE, US, GB). If this is a new country, add it to IsoCountryCodes.cs.");

        string co;
        try
        {
            co = new System.Globalization.RegionInfo(alpha2).EnglishName;
        }
        catch (ArgumentException)
        {
            // In the IsoCountryCodes table but unknown to this runtime's RegionInfo. Not "extremely unlikely", as this said:
            // Western Sahara (EH) is one on this machine's .NET 10 over the operating system's ICU (measured 2026-10-22,
            // aud-a2v; the only one of the table's 249). Fail closed rather than write c/countryCode without co.
            throw new ArgumentException(
                $"Country '{alpha2}' has no display name on this system; cannot set the 'co' attribute.");
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

    // aud-a2p. The on-prem add's message when its own write did not put the room on the list: the already-a-member
    // no-op, which wrote nothing, or a membership the read-back confirmed after the add was refused or reported an
    // error, which is not recorded as written (aud-a2n). Neither claims the write the message above describes. Pure +
    // static for unit testing.
    internal static string BuildOnPremRoomListNotWrittenMessage(string roomListName, bool confirmedAfterWrite)
        => confirmedAfterWrite
            ? $"In on-prem room list '{roomListName}', confirmed by reading its group back after this row's add was refused or reported an error: not recorded as written by this row."
            : $"Already in on-prem room list '{roomListName}': nothing was written.";

    // aud-a2q. The cloud add's message when its membership read found the room already on the list and nothing was
    // written: the on-prem no-op's message above, for a cloud list. That no-op said "Added to room list". Pure + static
    // for unit testing.
    internal static string BuildRoomListAlreadyInMessage(string roomListName)
        => $"Already in room list '{roomListName}': nothing was written.";

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
    /// on-premises (<c>IsDirSynced</c>), or null when Exchange did not say; and the primary SMTP
    /// address Exchange holds for it, by which its on-prem group is found (aud-a2k), as Exchange
    /// returned it - null when the list does not exist or Exchange reported none. And its
    /// <c>Guid</c>, "D" form (aud-a2m): every later Exchange call on the list names it by this, never
    /// by the name - null when the list does not exist or Exchange reported no usable one.
    /// aud-a2e. And its Entra object id (<c>ExternalDirectoryObjectId</c>), from the same object, null
    /// when Exchange reported none - with the address, what the Protected Group Targets check can match
    /// a cloud list by (<see cref="CloudListTarget"/>). And, set by the preflight only, for a list
    /// synchronized from on-premises whose on-prem group it resolved and the check cleared: that group,
    /// as its own domain answered for it - what the cloud add to the list is checked against again
    /// before it writes. Null otherwise; a synced list's cloud add without it is refused.
    /// </summary>
    /// <remarks>Internal, not private, for the tests (aud-a2p): <see cref="AddToRoomListAsync"/> takes one.</remarks>
    internal sealed record RoomListTarget(string Name, bool Exists, bool IsLegacy, bool? IsDirSynced, string? PrimarySmtpAddress, string? Guid,
        string? ExternalDirectoryObjectId = null, ResolvedDirectoryPrincipal? DirectoryGroup = null)
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
            return new RoomListTarget(canonicalName, Exists: true, IsLegacy: false, ReadIsDirSynced(canonical[0]), ReadPrimarySmtpAddress(canonical[0]), ReadListGuid(canonical[0]),
                ReadExternalDirectoryObjectId(canonical[0]));

        var legacyName = BuildLegacyRoomListName(building);
        ps.AddCommand("Get-DistributionGroup")
          .AddParameter("Identity", legacyName)
          .AddParameter("ErrorAction", "SilentlyContinue");
        var legacy = InvokeOptional(ps, tracker);
        if (legacy.Count > 0)
            return new RoomListTarget(legacyName, Exists: true, IsLegacy: true, ReadIsDirSynced(legacy[0]), ReadPrimarySmtpAddress(legacy[0]), ReadListGuid(legacy[0]),
                ReadExternalDirectoryObjectId(legacy[0]));

        return new RoomListTarget(canonicalName, Exists: false, IsLegacy: false, IsDirSynced: null, PrimarySmtpAddress: null, Guid: null);
    }

    // aud-a2m. The Guid of a list object Exchange returned - this read's, or New-DistributionGroup's for a
    // list the row creates. The name the Building derives is a search: between this read and the add, a
    // second recipient can come to answer to it (Exchange then refuses the add as ambiguous) or the name can
    // move to another object (the add lands there). The Guid can do neither, so it is what the later calls
    // pass, as aud-a2d passes the mailbox GUID. A typed Guid or a string that parses as one, as the property
    // may come back either way; anything else - absent, malformed, all zeros - is no Guid, and the callers
    // refuse rather than fall back to the name.
    private static string? ReadListGuid(PSObject list)
    {
        var value = list.Properties["Guid"]?.Value;
        if (value is PSObject wrapped)
            value = wrapped.BaseObject;
        var guid = value switch
        {
            Guid typed => typed,
            string text when System.Guid.TryParse(text, out var parsed) => parsed,
            _ => System.Guid.Empty,
        };
        return guid == System.Guid.Empty ? null : guid.ToString("D");
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

    // aud-a2k. Taken from the object the IsDirSynced read already returned, so the list whose sync
    // state the preflight decides on is the list whose address finds its on-prem group. Not checked
    // here: ResolveAdGroupGuid refuses a blank one at the point of use.
    private static string? ReadPrimarySmtpAddress(PSObject list) =>
        list.Properties["PrimarySmtpAddress"]?.Value?.ToString();

    // aud-a2e. The list's Entra object id, from the object the reads above came from: with its address, what a protected user
    // row can name a cloud object by (CloudListTarget). Null when Exchange reported none.
    private static string? ReadExternalDirectoryObjectId(PSObject list)
    {
        var value = list.Properties["ExternalDirectoryObjectId"]?.Value?.ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    // The apply's room list, read before any write (aud-a2f). Read-only: safe to retry.
    private Task<RoomListTarget> ResolveRoomListTargetAsync(string building) =>
        RunPooledQueryAsync((ps, tracker) => FindRoomList(ps, tracker, building), allowRetry: true);

    // -------------------------------------------------------------------------
    // aud-a2e. The room list as a write target (Protected Group Targets)
    // -------------------------------------------------------------------------
    // A Room Finder row writes into a GROUP - the building's room list - as well as into the room's mailbox, and that write
    // was never checked: docs/ProtectedGroupWriteTarget-Plan.md left the module out because its write target was taken to be
    // a mailbox. The list is checked with the shared write-target gate, ProtectedPrincipalServicing.ForWriteTarget - the one
    // Group Management consults - on the object each write names: before anything is written (PreflightRoomListAsync), and
    // again immediately before each write into the list (AddToRoomListAsync, AddToRoomListViaAdAsync).

    /// <summary>
    /// A room list Exchange Online holds, as the Protected Group Targets check sees it: the identifiers Exchange reported for
    /// the object the cloud add names by its <c>Guid</c> - its primary address and its Entra object id.
    /// </summary>
    /// <remarks>
    /// It has no on-premises object, so no distinguished name, objectGUID or sAMAccountName: the Group Targets, OU and pattern
    /// rules are evaluated from those and cannot match it. That is the structural limit the Constitution states for cloud-only
    /// principals (Protected Principals: protected by address, never by group or OU), not a rule skipped; the user rows apply.
    /// A group has no user principal name. Pure, for unit testing.
    /// </remarks>
    internal static ResolvedDirectoryPrincipal CloudListTarget(string name, string? primarySmtpAddress, string? externalDirectoryObjectId) =>
        new(Source: "ConferenceRoomService-EXO",
            DisplayName: name,
            UserPrincipalName: string.Empty,
            SamAccountName: null,
            PrimarySmtpAddress: string.IsNullOrWhiteSpace(primarySmtpAddress) ? null : primarySmtpAddress.Trim(),
            DistinguishedName: null,
            ObjectGuid: null,
            EntraObjectId: externalDirectoryObjectId);

    /// <summary>
    /// The write-target decision for the room list <paramref name="list"/> names: the shared gate
    /// (<see cref="ProtectedPrincipalServicing.ForWriteTarget"/>), under the Conference Rooms servicer grant the room gate uses.
    /// </summary>
    /// <remarks>
    /// Fail closed, as the gate is: a failed or errored check refuses with its reason, and a protected list refuses unless
    /// <paramref name="actingUser"/> may service protected principals in this module - a null user (a bulk row, off-circuit)
    /// never may. Without the protected-principal services there is nothing to ask, and that refuses too. An authorised
    /// servicer's override is logged here, once per check, and its note is the caller's to carry to the audit.
    /// </remarks>
    private ProtectedPrincipalServicing.WriteTargetDecision CheckRoomListTarget(ResolvedDirectoryPrincipal list, ClaimsPrincipal? actingUser)
    {
        if (_protectedPrincipals is null || _servicers is null)
            return new(false, "Protection check unavailable: the protected-principal service is not available to Conference Rooms.", null);

        var decision = ProtectedPrincipalServicing.ForWriteTarget(
            _protectedPrincipals, _servicers, list, actingUser, ConferenceRoomProtectionGate.ModuleId);
        if (decision.Allowed && decision.ServicedNote is not null)
            _logger.LogInformation("Authorised servicer adding a room to protected room list {List}: {Note}", list.DisplayName, decision.ServicedNote);
        return decision;
    }

    /// <summary>
    /// Why the room was not added to room list <paramref name="roomListName"/> when the write-target check did not allow it: a
    /// protected list the operator may not service, or a check that failed - neither ending "Nothing was written.", which the
    /// preflight adds and the add, after Set-Place, cannot claim.
    /// </summary>
    /// <remarks>
    /// The rules a protected list matched are not named to the operator, as Group Management names none; an authorised
    /// servicer's override is recorded with them, in the audit.
    /// </remarks>
    internal static string RoomListTargetRefusal(string roomListName, ProtectedPrincipalServicing.WriteTargetDecision decision) =>
        decision.FailReason is { } reason
            ? $"Room list '{roomListName}' could not be checked against Protected Group Targets, so the room was not added to it: {Sentence(reason)}"
            : $"Room list '{roomListName}' is a protected group: its membership cannot be changed here, so the room was not added to it. Operation not permitted.";

    /// <summary>
    /// Why a synchronized list's cloud add was not made: no on-prem group of the list was checked before it, because the room
    /// was already on the list when the row was checked, and so the preflight looked none up (aud-a2f).
    /// </summary>
    internal static string RoomListNotCheckedRefusal(string roomListName) =>
        $"Room list '{roomListName}' is synchronized from on-premises, and its on-premises group was not checked against Protected Group Targets before this write: the room was on the list when the row was checked, so the group was not looked up. The room was not added to it.";

    private static readonly HashSet<string> ValidRoomRecipientTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "RoomMailbox", "RemoteRoomMailbox"
    };

    /// <summary>
    /// The validated room read back by its GUID, and refused unless it is a room mailbox. Returns
    /// the room's details for the operation (its primary address included), or the refusal.
    /// </summary>
    /// <remarks>
    /// aud-a2w. The refusal says nothing of what was written, and is logged nowhere here: each caller - the Finder row and
    /// the Type path - logs it where it refuses and appends " Nothing was written.", the shape ConfirmDirectoryObject has
    /// had since aud-a2r. A line here as well would log every such refusal twice.
    /// </remarks>
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
    /// <param name="actingUser">
    /// aud-a2e. The operator, for the servicer grant on a room list protected as a write target - the
    /// room gate's rule: the page passes the signed-in operator, a bulk row null, which never services.
    /// Required, with no default, as the gate's is.
    /// </param>
    public async Task<RoomOperationResult> SetRoomMetadataAndListAsync(
        string roomEmail, ValidatedMailbox? room, ClaimsPrincipal? actingUser, string city, string building, int capacity,
        string floor, string floorLabel, string displayDevice, string videoDevice,
        string countryOrRegion, string state, string timezone)
    {
        var result = new RoomOperationResult { Email = roomEmail };

        // aud-a2v. The row's own three refusals, before its preflights, are logged as its paths' are (aud-a2t, aud-a2u): once,
        // at Warning, where the row refuses, naming the room as typed and the refusal. They reached only the result and the
        // audit. Nothing is written before any of them - the room's read is the only call - so the line says so. Here, not in
        // ValidateRoomRecipientAsync, which SetRoomTypeAsync shares.
        // aud-a2w. And each message says so at its end, as both preflights' do since aud-a2v - one rule for the module: the
        // time-zone and recipient refusals are built without " Nothing was written.", logged so, and returned with it. Not the
        // validated-mailbox refusal, which already says "so nothing was written" in its own words and is shared with other
        // modules.
        if (room is null)
        {
            result.Success = false;
            result.Message = ValidatedMailbox.Refusal(roomEmail);
            _logger.LogWarning("Room Finder apply for {Room} refused the row; nothing was written: {Refusal}", roomEmail, result.Message);
            return result;
        }

        if (!string.IsNullOrWhiteSpace(timezone))
        {
            try { TimeZoneInfo.FindSystemTimeZoneById(timezone); }
            catch (TimeZoneNotFoundException)
            {
                var refusal = $"Invalid timezone: '{timezone}'.";
                result.Success = false;
                result.Message = $"{refusal} Nothing was written.";
                _logger.LogWarning("Room Finder apply for {Room} refused the row; nothing was written: {Refusal}", roomEmail, refusal);
                return result;
            }
        }

        // Not a room mailbox, or - defensively, as GetRoomInfoAsync(ValidatedMailbox) returns a room or throws - not found:
        // one line for either.
        // aud-a2w. The read throws when Exchange returns another mailbox or none (ReadValidatedMailbox) or a cmdlet fails, and
        // nothing logged that: the pool rethrows it, and the page's catch, like the bulk row's, audits and notifies but writes
        // no log line. So the throw is logged here - once, at Warning, as the row's refusals are, the row failing closed
        // before any write - and rethrown as it was, so the outcome and the callers' audit and notification are unchanged.
        (RoomInfo? info, string? error) recipient;
        try
        {
            recipient = await ValidateRoomRecipientAsync(room, roomEmail);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Room Finder apply for {Room}: reading the room failed; nothing was written: {Error}", roomEmail, ex.Message);
            throw;
        }
        var (roomInfo, recipientError) = recipient;
        if (recipientError != null || roomInfo == null)
        {
            var refusal = recipientError ?? $"Room '{roomEmail}' not found.";
            result.Success = false;
            result.Message = $"{refusal} Nothing was written.";
            _logger.LogWarning("Room Finder apply for {Room} refused the row; nothing was written: {Refusal}", roomEmail, refusal);
            return result;
        }

        // Preflight the AD step BEFORE any mutation. Set-Place (Step 1) commits EXO-side
        // metadata; if the AD prerequisites for Step 2 are bad (unmappable country, missing/
        // unavailable credential, no validated AD object) we must fail the row here so EXO is
        // never partially written. Without this guard a bad-AD row was reported failed but had
        // already committed Set-Place - the partial-apply defect from the 2026-06-17 review.
        // aud-a2q. It also proves the server the write is to use - the object read through it - and
        // hands it over, so the write goes nowhere the preflight did not reach the object.
        var (adPreflightError, adServer) = await PreflightSyncedAttributesViaAdAsync(room, roomEmail, city, state, countryOrRegion);
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
        // aud-a2e. And the list checked as a write target (Protected Group Targets) before anything is
        // written: a list the check refuses refuses the whole row here, so Set-Place does not proceed
        // past a room list the room may not be added to.
        RoomListTarget? roomList = null;
        if (!string.IsNullOrWhiteSpace(building))
        {
            roomList = await ResolveRoomListTargetAsync(building);
            var (listPreflightError, checkedList, listServicedNote) = await PreflightRoomListAsync(room, roomInfo.Email, roomEmail, roomList, actingUser);
            if (listPreflightError != null)
            {
                result.Success = false;
                result.Message = listPreflightError;
                result.Steps.Add(new RoomOperationStep { Stage = "Room list preflight", Success = false, Error = listPreflightError });
                return result;
            }
            // aud-a2e. The list as the preflight checked it - a synced one with the on-prem group it
            // resolved and cleared, which the cloud add is checked against again - and, when a servicer's
            // grant let the row past a protected list, the note the audit records.
            roomList = checkedList;
            result.RoomListServicedNote = listServicedNote;
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
        var (adError, adWritten, adConfirmed) = await SetSyncedAttributesViaAdAsync(room, roomEmail, city, state, countryOrRegion, adServer);
        // aud-a2g. The object Set-ADUser wrote, if it wrote one - recorded now, so a later step
        // failing cannot hide it, and only from the write itself, so a row that wrote nothing in
        // Active Directory cannot claim it did.
        result.WrittenDirectoryObject = adWritten;
        // aud-a2q. And why, if Set-ADUser reported an error and the values read back in place, no
        // object is named for them: the audit records this in that place, and the step below shows it.
        result.DirectoryAttributesConfirmed = adConfirmed;
        if (adError != null)
        {
            // Set-Place already committed EXO metadata; this row is now half-configured.
            result.Success = false;
            result.Partial = true;
            result.Message = $"Partial: Building/Capacity/Floor were set in Exchange Online, but City/State/Country failed: {adError} Re-run this row after fixing the cause.";
            result.Steps.Add(new RoomOperationStep { Stage = "Set-ADUser (City/State/Country)", Success = false, Error = adError });
            return result;
        }
        result.Steps.Add(new RoomOperationStep { Stage = "Set-ADUser (City/State/Country)", Success = true, Error = adConfirmed });

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
            var listResult = await AddToRoomListAsync(room, roomInfo.Email, roomEmail, roomList, actingUser);
            // aud-a2e. The note of the check made just before a write into the list, when a servicer's grant
            // allowed one; else the preflight's.
            result.RoomListServicedNote = listResult.RoomListServicedNote ?? result.RoomListServicedNote;
            result.WrittenDirectoryObject ??= listResult.WrittenDirectoryObject; // the on-prem add's (aud-a2g)
            // aud-a2n. Why, if the list add confirmed a membership it is not known to have written, no
            // object is named for it: the audit records this in its place.
            result.DirectoryMembershipConfirmed = listResult.DirectoryMembershipConfirmed;
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
        // aud-a2q. Values the read-back found in place after Set-ADUser reported an error are not
        // claimed as written by this row. The admin notification does not carry a success's message:
        // since aud-a2r it carries the detail, on a row of its own (NotificationDetails).
        result.Message = string.IsNullOrWhiteSpace(city) && string.IsNullOrWhiteSpace(state) && string.IsNullOrWhiteSpace(countryOrRegion)
            ? "Room metadata and room list configured."
            : adConfirmed is null
                ? "Room metadata and room list configured. City/State/Country were written on-prem and will appear in Exchange/Room Finder after the next directory sync (typically ~30 min)."
                : "Room metadata and room list configured. City/State/Country read back as set on-prem after Set-ADUser reported an error, and will appear in Exchange/Room Finder after the next directory sync (typically ~30 min).";
        return result;
    }

    /// <param name="room">The validated room (aud-a2d): the member added, by GUID or proven objectGUID.</param>
    /// <param name="roomPrimary">
    /// The room's primary address as Exchange returned it for <paramref name="room"/> - compared
    /// against the list's members only, never written.
    /// </param>
    /// <param name="roomEmail">The operator's input, for the result only.</param>
    /// <param name="list">
    /// The room list resolved, and its on-prem add proven, before any write (aud-a2f) - for a synced list the preflight
    /// checked, with the on-prem group it cleared (aud-a2e).
    /// </param>
    /// <param name="actingUser">aud-a2e. The operator, for the servicer grant on a protected list; null never services.</param>
    /// <remarks>
    /// Internal, not private, for the tests (aud-a2p): its message reaches no result the row returns - the row
    /// replaces a success's - so only a direct call can pin it.
    /// </remarks>
    internal async Task<RoomOperationResult> AddToRoomListAsync(
        ValidatedMailbox room, string roomPrimary, string roomEmail, RoomListTarget list, ClaimsPrincipal? actingUser)
    {
        var result = new RoomOperationResult { Email = roomEmail };
        var (roomListName, exists, isLegacy) = (list.Name, list.Exists, list.IsLegacy);
        // aud-a2q. Whether the cloud add's membership read found the room already on the list, so its success says what
        // happened: the no-op said "Added to room list" as the add did - the claim aud-a2p took out of the on-prem
        // fallback's messages.
        var alreadyInList = false;
        // aud-a2e. Why the cloud add was not made, when the Protected Group Targets check refused the list it would write
        // into or no check covered it; and the note of a servicer's grant that allowed it, for the audit.
        string? listRefusal = null;
        string? servicedNote = null;

        var opResult = await RunAsync((ps, tracker) =>
        {
            // aud-a2m. The list by the Guid it was read with, or - for a list created here - the Guid of the
            // object the creation returned: never found again by its name.
            var listGuid = list.Guid;
            // aud-a2e. A list created here, as the creation returned it: the object the add below writes into.
            ResolvedDirectoryPrincipal? createdTarget = null;
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
                var created = Invoke(ps, tracker);
                result.Steps.Add(new RoomOperationStep { Stage = $"Create room list '{roomListName}'", Success = true });
                listGuid = created.Count == 1 ? ReadListGuid(created[0]) : null;
                if (created.Count == 1)
                    createdTarget = CloudListTarget(roomListName, ReadPrimarySmtpAddress(created[0]), ReadExternalDirectoryObjectId(created[0]));
            }
            else if (isLegacy)
            {
                result.Steps.Add(new RoomOperationStep { Stage = $"Using legacy room list '{roomListName}'", Success = true });
            }

            // Only a creation that returned no Guid reaches this: the preflight refused an existing list without
            // one, before any write. The create has committed by now, so the row is partial; a re-run reads the
            // list it made, by name, and takes its Guid from that read.
            if (listGuid is null)
                throw new InvalidOperationException($"Exchange returned no GUID for room list '{roomListName}', so the room was not added to it: a list is named only by the GUID Exchange reported for it, never by its name.");

            // Check if already a member - of the whole list (aud-a2h). Without -ResultSize Exchange
            // Online returns only its first 1000 members, so a room past them read as not a member: the
            // add went out for a member the list already held and the row came back partial, Set-Place
            // and the timezone written. The protected-principal expansion reads it the same way.
            ps.AddCommand("Get-DistributionGroupMember")
              .AddParameter("Identity", listGuid)
              .AddParameter("ResultSize", "Unlimited")
              .AddParameter("ErrorAction", "SilentlyContinue");
            var members = InvokeOptional(ps, tracker);
            var alreadyMember = IsListMember(members, roomPrimary);
            alreadyInList = alreadyMember;

            if (!alreadyMember)
            {
                // aud-a2e. The list is checked immediately before the add writes into it, as the object listGuid names,
                // described by the read that gave listGuid: a list Exchange Online holds as that read reported it; one
                // created here as the creation returned it; a synced one by the on-prem group the preflight resolved and
                // cleared - the group the list's address binds it to (aud-a2k), and which Exchange refuses this add for, the
                // write landing on-prem below, where that group is checked again. None for a synced list means none was
                // checked - the room was on the list when the row was checked - and the add is not made unchecked.
                var target = !exists
                    ? createdTarget
                    : list.TakesOnPremFallback
                        ? list.DirectoryGroup
                        : CloudListTarget(roomListName, list.PrimarySmtpAddress, list.ExternalDirectoryObjectId);
                if (target is null)
                {
                    listRefusal = RoomListNotCheckedRefusal(roomListName);
                    return;
                }
                var decision = CheckRoomListTarget(target, actingUser);
                if (!decision.Allowed)
                {
                    listRefusal = RoomListTargetRefusal(roomListName, decision);
                    return;
                }
                servicedNote = decision.ServicedNote;

                _operationTrace?.Step("AddToRoomList", backend: "EXO", command: "Add-DistributionGroupMember", target: room.Identity);
                ps.AddCommand("Add-DistributionGroupMember")
                  .AddParameter("Identity", listGuid)
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

        // aud-a2e. Recorded whatever follows: a later failure of a write a servicer's grant allowed still carries the
        // grant's note (pgwt-7).
        result.RoomListServicedNote = servicedNote;
        if (listRefusal is not null)
        {
            // The cloud add was not made: refused before its write, after Set-Place, inside the row's partial - so the
            // refusal does not say nothing was written. Logged once, here, where it is decided.
            _logger.LogWarning("Room-list add for {Room} on {List} refused before its write: {Refusal}", roomEmail, roomListName, listRefusal);
            result.Success = false;
            result.Message = listRefusal;
            result.Steps.Add(new RoomOperationStep { Stage = "Add to room list", Success = false, Error = listRefusal });
            return result;
        }

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

                    _operationTrace?.Step("AddToRoomListOnPrem", backend: "AD", command: "Set-ADGroup", target: room.DirectoryObjectGuid ?? roomEmail);
                    var (adError, adWritten, adConfirmed, adServicedNote) = await AddToRoomListViaAdAsync(room, roomEmail, list, actingUser);
                    // aud-a2e. The on-prem write's own check, made just before it, when a servicer's grant allowed it.
                    result.RoomListServicedNote = adServicedNote ?? result.RoomListServicedNote;
                    if (adError == null)
                    {
                        result.Success = true;
                        result.WrittenDirectoryObject = adWritten; // null when it was already a member (aud-a2g)
                        // aud-a2n. Confirmed only by the read-back, after the write was refused or erred: no
                        // object written, and the detail, with the write's error, on the step and the row.
                        result.DirectoryMembershipConfirmed = adConfirmed;
                        // aud-a2o. Neither a written object nor a confirmed membership is the add's already-a-member
                        // no-op, which wrote nothing: its step says the room was already there - the cloud no-op's
                        // label, on-prem - rather than reading as the add it used to share a label with.
                        var alreadyMember = adWritten is null && adConfirmed is null;
                        result.Steps.Add(new RoomOperationStep { Stage = alreadyMember ? $"Already in '{roomListName}' (on-prem)" : $"Add to '{roomListName}' (on-prem)", Success = true, Error = adConfirmed });
                        // aud-a2p. The message says what the add did: only the add whose write put the room there says it
                        // was written. The no-op wrote nothing, and a membership confirmed after a refused or failing write
                        // is not recorded as this row's (aud-a2n). The caller shows a failure's message and replaces a
                        // success's, so none of these is shown today; each is true if one ever is.
                        result.Message = adWritten is not null
                            ? BuildRoomListAddedMessage(roomListName, viaOnPrem: true)
                            : BuildOnPremRoomListNotWrittenMessage(roomListName, confirmedAfterWrite: adConfirmed is not null);
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
        // The caller shows a failure's message and replaces a success's, so neither is shown today; each is true if one
        // ever is.
        result.Message = alreadyInList
            ? BuildRoomListAlreadyInMessage(roomListName)
            : BuildRoomListAddedMessage(roomListName, viaOnPrem: false);
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
    // The whole list, as the add reads it (aud-a2h): Exchange Online's default page is 1000 members,
    // and a synced room past it was refused for want of an on-prem proof the add would not have needed.
    // By the list's Guid, as the add reads it (aud-a2m).
    private Task<bool> IsOnRoomListAsync(string listGuid, string roomPrimary) =>
        RunPooledQueryAsync((ps, tracker) =>
        {
            ps.AddCommand("Get-DistributionGroupMember")
              .AddParameter("Identity", listGuid)
              .AddParameter("ResultSize", "Unlimited")
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

        // aud-a2w. The four refusals before any write - no validated room, an invalid time zone, the recipient refused, the
        // module's groups unconfigured - are logged as the Finder row's are (aud-a2v): once, at Warning, where the path
        // refuses, naming the room as typed and the refusal. They reached only the result and the audit. And each message
        // but the validated-mailbox one, which says so already, ends " Nothing was written." - the module's one rule for a
        // refusal decided before any write.
        if (room is null)
        {
            result.Success = false;
            result.Message = ValidatedMailbox.Refusal(roomEmail);
            _logger.LogWarning("Room Type apply for {Room} refused the row; nothing was written: {Refusal}", roomEmail, result.Message);
            return result;
        }

        if (!string.IsNullOrWhiteSpace(timezone))
        {
            try { TimeZoneInfo.FindSystemTimeZoneById(timezone); }
            catch (TimeZoneNotFoundException)
            {
                var refusal = $"Invalid timezone: '{timezone}'.";
                result.Success = false;
                result.Message = $"{refusal} Nothing was written.";
                _logger.LogWarning("Room Type apply for {Room} refused the row; nothing was written: {Refusal}", roomEmail, refusal);
                return result;
            }
        }

        // aud-a2y. The room read throws when Exchange returns another mailbox or none (ReadValidatedMailbox) or a cmdlet
        // fails, and nothing logged that on this path either: the pool rethrows it, and the page's catch, like the bulk
        // row's, audits and notifies but writes no log line. So it is logged here as the Finder row's read is (aud-a2w) -
        // once, at Warning, nothing having been written - and rethrown as it was, so the outcome and the callers' audit and
        // notification are unchanged.
        string? recipientError;
        try
        {
            (_, recipientError) = await ValidateRoomRecipientAsync(room, roomEmail);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Room Type apply for {Room}: reading the room failed; nothing was written: {Error}", roomEmail, ex.Message);
            throw;
        }
        if (recipientError != null)
        {
            result.Success = false;
            result.Message = $"{recipientError} Nothing was written.";
            _logger.LogWarning("Room Type apply for {Room} refused the row; nothing was written: {Refusal}", roomEmail, recipientError);
            return result;
        }

        // Fail closed before any EXO mutation: the required group addresses come from module
        // config (no hardcoded defaults). Applying empty group values would send -User "" /
        // -BookInPolicy [""] to EXO and fail per-room mid-operation, risking partial calendar
        // state. Abort cleanly with an actionable message instead.
        var missingGroups = FindMissingRequiredGroups(key => _moduleConfig.GetValue(ModuleId, key));
        if (missingGroups.Count > 0)
        {
            var refusal = $"Conference Rooms module is not configured. Set {string.Join(", ", missingGroups)} in Module Config.";
            result.Success = false;
            result.Message = $"{refusal} Nothing was written.";
            _logger.LogWarning("Room Type apply for {Room} refused the row; nothing was written: {Refusal}", roomEmail, refusal);
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

    // aud-a2x. The module's AD credential for one of the four directory paths, and why there is none when there is none.
    // The base's GetModuleCredentialsAsync returns only the credential, so every path refused a lookup that returned none
    // with "not configured (set the AD Delinea Secret ID)" - also when the ID was set and the module's config could not be
    // read, or Delinea returned nothing for it. The credential service now says which, and the refusal says it. With no
    // credential service (a host that registered none, which the base allows) there is nothing to say why: the base's
    // lookup logs that and returns none, refused as not configured, as this file always did. Virtual, as the base's lookup
    // is, for the tests.
    protected virtual async Task<ModuleCredentialLookup> LookupDirectoryCredentialAsync(string purpose)
    {
        if (_moduleCredentials is not null)
            return await _moduleCredentials.LookupCredentialsAsync(ModuleId, purpose);

        var credentials = await GetModuleCredentialsAsync(purpose);
        return new ModuleCredentialLookup(
            credentials, credentials is null ? ModuleCredentialFailure.SecretIdNotSet : ModuleCredentialFailure.None);
    }

    // aud-a2x. The clause each path's credential refusal opens with, for why the lookup returned none; each path adds its
    // own tail, and the preflights " Nothing was written.". The operator is sent to set the AD Delinea Secret ID only when
    // none is set - those words, and so each path's refusal then, are as before to the byte. Otherwise the clause names the
    // category, and points to the application log, where DelineaService (every null return of its secret lookup) or the
    // credential service (an unreadable module config) has written the precise reason; no secret or response body is in
    // either clause.
    internal static string DirectoryCredentialRefusal(ModuleCredentialFailure failure) => failure switch
    {
        ModuleCredentialFailure.NotReturned =>
            "The on-prem AD credential for Conference Rooms could not be read from Delinea: the AD Delinea Secret ID is set, but Delinea returned no credential for it (the application log has its reason)",
        ModuleCredentialFailure.ConfigUnreadable =>
            "The on-prem AD credential for Conference Rooms could not be read: the module's configuration could not be read, so neither could its AD Delinea Secret ID (the application log records it)",
        _ => "On-prem AD credential is not configured for Conference Rooms (set the AD Delinea Secret ID in Module Config)",
    };

    // Refusal for an AD step with no validated object to write. A cloud-only room has none: the
    // gate found no on-premises object behind it, so there is nothing City/State/Country or an
    // on-prem list membership could be written to.
    private static string NoDirectoryObject(string roomEmail, string what) =>
        $"No on-premises Active Directory object was validated for '{roomEmail}' (a cloud-only room has none), so {what}.";

    // Writes the dir-synced location attributes (City/State/Country) on the on-prem AD
    // object via Set-ADUser. These attributes are AD-mastered, so EXO Set-User rejects them
    // ("being synchronized from your on-premises organization") - the root cause of the
    // Room Finder apply failure (docs/ConferenceRooms-RoomFinderMetadataApply-Plan.md).
    // Returns (null, the objectGUID written, null) once the attributes Set-ADUser wrote have read
    // back as written (aud-a2p); (null, null, null) when there is nothing to write; (null, null, a
    // detail) when Set-ADUser reported an error and the attributes read back as written all the
    // same - values in place this row is not known to have written (aud-a2q); and (the failure
    // message for the row, null, null) otherwise - the object is reported only by a write known to
    // have happened (aud-a2g). It writes through the server the preflight below proved (aud-a2q).
    // Runs in its own AD runspace, as the Comms-10k service's directory reads do, not through
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
    // Returns (an error message to abort the row, null), or (null, the server the write is to use)
    // when the AD write is expected to succeed - a null server when there is nothing to write or the
    // object's DN names no domain. (Two-system writes can never be fully atomic; a genuine
    // Set-ADUser failure after a passing preflight remains an inherent, accepted residual.)
    //
    // aud-a2q. "Readable" is through the server the write will use, not only through the module's
    // default. The first read has no -Server, so the default domain controller answers it, but the
    // write and its read-back go to the domain the object's DN names (aud-a2p). A domain this host
    // reaches through the default locator but not by its DNS name - or one the module credential
    // cannot use there - passed, Set-Place committed, and the routed write failed: the partial this
    // guard exists to prevent (aud-a2f). So the object is read again, by its objectGUID, through that
    // server, and the write is handed the server this read proved instead of deriving its own.
    private async Task<(string? error, string? server)> PreflightSyncedAttributesViaAdAsync(ValidatedMailbox room, string roomEmail, string city, string state, string countryOrRegion)
    {
        Dictionary<string, object> attrs;
        try
        {
            attrs = BuildSyncedUserAttributes(city, state, countryOrRegion);
        }
        catch (ArgumentException ex)
        {
            // aud-a2u. A refusal is logged where the path decides it, once, at Warning: the room, the list where there is
            // one, and the reason. These used to reach only the row's result and audit, so the application log held no
            // trace of why a row was refused. Not again by the row, which puts the refusal on its result - aud-a2t's
            // placement, for the resolver's refusals.
            // aud-a2v. And each of this preflight's three refusals before its runspace - this, no proven object, no
            // credential - is built without " Nothing was written.", logged so, and returned with it, as the room-list
            // preflight's are: one rule for both preflights, a refusal decided before any write saying so at its end. These
            // three said only what could not be written, so the operator read two styles for one fact. Sentence(): the
            // mapping's words are another method's.
            var refusal = Sentence(ex.Message);
            _logger.LogWarning("Set-ADUser preflight for {Room} refused the row; nothing was written: {Refusal}", roomEmail, refusal);
            return ($"{refusal} Nothing was written.", null); // unmappable country - fail the row closed
        }

        if (attrs.Count == 0)
            return (null, null); // nothing synced to write for this row - AD step will be a no-op

        if (room.DirectoryObjectGuid is not { } objectGuid)
        {
            var refusal = NoDirectoryObject(roomEmail, "City/State/Country could not be written");
            _logger.LogWarning("Set-ADUser preflight for {Room} refused the row; nothing was written: {Refusal}", roomEmail, refusal);
            return ($"{refusal} Nothing was written.", null);
        }

        // aud-a2v. A lookup that returns none - no secret ID, a corrupt module config, a PAM provider failing or holding an
        // incomplete secret - is logged by the credential services, whose lines name the module or the secret, and the
        // purpose only for a missing secret ID: none named the room. So this path's line names the room beside the purpose it
        // asked with, which ties it to theirs.
        // aud-a2x. And the refusal opens with why there is none (DirectoryCredentialRefusal), so an ID that is set is not
        // asked for again; for no ID its words are as before. The three paths below take the same clause.
        var purpose = $"Set-ADUser preflight for {roomEmail}";
        var lookup = await LookupDirectoryCredentialAsync(purpose);
        var creds = lookup.Credentials;
        if (creds is null)
        {
            var refusal = $"{DirectoryCredentialRefusal(lookup.Failure)}. City/State/Country could not be written.";
            _logger.LogWarning("Set-ADUser preflight for {Room} refused the row; nothing was written: no AD credential was returned for {Purpose}: {Refusal}", roomEmail, purpose, refusal);
            return ($"{refusal} Nothing was written.", null);
        }

        return await Task.Run<(string? error, string? server)>(() =>
        {
            // aud-a2r. The step under way, for the catch at the end, as the write below keeps it (aud-a2q): a failure of
            // any step here read "Failed to validate the AD object before writing" with no step named. Only the steps up
            // to the object read reach that catch - the routed read further down settles its own error, in a refusal
            // naming the server - and every one of them runs before Set-Place, so nothing was written.
            var step = "opening the directory runspace";
            try
            {
                using var runspace = OpenDirectoryRunspace();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                // psl-8. The shared import (ActiveDirectoryModuleImport), which this file used to keep a private copy of
                // (aud-a2r's): its throw is the step's error, named by the step, and it leaves no record in the stream for
                // the object read's own HadErrors check (ConfirmDirectoryObject) to take. The other three paths import so too.
                step = "importing the ActiveDirectory module";
                ActiveDirectoryModuleImport.Import(ps);

                step = "preparing the module's AD credential";
                var credential = CreateAdCredential(creds.Value.username, creds.Value.password, creds.Value.domain);
                step = "reading the AD object";
                var objectError = ConfirmDirectoryObject(ps, objectGuid, roomEmail, credential, out var roomDn);
                // Before any write, so the refusal says nothing was written: ConfirmDirectoryObject leaves that to its
                // caller (aud-a2r), its callers on the write paths coming after Set-Place - and leaves the log line to
                // it as well (aud-a2u).
                if (objectError != null)
                {
                    _logger.LogWarning("Set-ADUser preflight for {Room} refused the row; nothing was written: {Refusal}", roomEmail, objectError);
                    return ($"{Sentence(objectError)} Nothing was written.", null);
                }

                // aud-a2q. The server the write and its read-back will use, derived here, once.
                var server = GroupManagementService.ServerFromDn(roomDn);
                // No domain in the DN - none at all, or no DC components - names no server, so the write
                // goes to the default domain controller: the route the read above has just taken. The
                // fallback is explicit, and logged; there is nothing further to prove.
                if (server is null)
                {
                    _logger.LogWarning("Set-ADUser preflight for {Room}: the AD object {Object} was read with no distinguished name naming its domain ({Dn}), so City/State/Country go to the default domain controller, the route that read took", roomEmail, objectGuid, roomDn ?? "none");
                    return (null, null);
                }

                // Exactly the object, through exactly that server. Any failure - unreachable, refused,
                // another object or none answering - refuses the row while nothing is written, naming the
                // server. No other server is tried: only this one is where the write goes.
                string? routeError;
                try
                {
                    routeError = ReadObjectThrough(ps, objectGuid, server, credential);
                }
                catch (Exception ex)
                {
                    routeError = ex.Message;
                }
                if (routeError != null)
                {
                    _logger.LogWarning("Set-ADUser preflight for {Room}: the AD object {Object} could not be read through {Server}, where City/State/Country would be written: {Error}", roomEmail, objectGuid, server, routeError);
                    return ($"City/State/Country could not be written for '{roomEmail}': its AD object {objectGuid} is written through {server}, the domain its distinguished name names, and could not be read there: {Sentence(routeError)} Nothing was written.", null);
                }
                return (null, server);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Set-ADUser preflight for {Room} on the AD object {Object}: {Step} failed; nothing was written", roomEmail, objectGuid, step);
                return ($"City/State/Country could not be validated before writing on the AD object {objectGuid}: {step} failed: {Sentence(ex.Message)} Nothing was written.", null);
            }
        });
    }

    private async Task<(string? error, string? written, string? confirmed)> SetSyncedAttributesViaAdAsync(ValidatedMailbox room, string roomEmail, string city, string state, string countryOrRegion, string? server)
    {
        // aud-a2u. The two refusals below, unlike the preflight's, are not logged: nothing reaches them. The row runs this
        // only after the preflight passed on the same room and the same fields, and BuildSyncedUserAttributes is pure, so
        // the preflight has refused - and logged - every row they could refuse. The credential's, after them, is reached
        // and logged (aud-a2v).
        Dictionary<string, object> attrs;
        try
        {
            attrs = BuildSyncedUserAttributes(city, state, countryOrRegion);
        }
        catch (ArgumentException ex)
        {
            return (ex.Message, null, null); // unmappable country - fail the row closed
        }

        if (attrs.Count == 0)
            return (null, null, null); // nothing synced to write for this row - and nothing written

        if (room.DirectoryObjectGuid is not { } objectGuid)
            return (NoDirectoryObject(roomEmail, "City/State/Country could not be written"), null, null);

        // aud-a2v. As the preflight's, in this write's words - after Set-Place, inside the row's partial. Reached when this
        // lookup returns none though the preflight's returned one: each asks the PAM provider again.
        var purpose = $"Set-ADUser synced attributes for {roomEmail}";
        var lookup = await LookupDirectoryCredentialAsync(purpose);
        var creds = lookup.Credentials;
        if (creds is null)
        {
            var refusal = $"{DirectoryCredentialRefusal(lookup.Failure)}. City/State/Country could not be written.";
            _logger.LogWarning("Set-ADUser City/State/Country write for {Room} refused before its write: no AD credential was returned for {Purpose}: {Refusal}", roomEmail, purpose, refusal);
            return (refusal, null, null);
        }

        var serverName = server ?? "the default domain controller";
        return await Task.Run<(string? error, string? written, string? confirmed)>(() =>
        {
            // aud-a2q. The step under way, for the catch at the end. It used to log "Set-ADUser
            // synced-attribute write failed" and tell the row it had failed to write, whichever step had
            // failed - the runspace, the module, the credential, the read of the object. Only those, and
            // building the write, reach it now: the write and the read-back settle their own errors
            // below, each in its own words, so what reaches the catch has written nothing.
            var step = "opening the directory runspace";
            try
            {
                using var runspace = OpenDirectoryRunspace();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                step = "importing the ActiveDirectory module";
                ActiveDirectoryModuleImport.Import(ps);

                step = "preparing the module's AD credential";
                var credential = CreateAdCredential(creds.Value.username, creds.Value.password, creds.Value.domain);

                // aud-a2d's read of the object before writing, unchanged. Not the route: the server is the
                // one the preflight proved (aud-a2q), and this read's DN is not consulted, so the write
                // cannot move to a domain nothing proved. Its refusal is returned as it is (aud-a2r): this
                // comes after Set-Place, inside the row's partial, where "Nothing was written." would be false.
                // Logged so as well (aud-a2u): the line says this write was refused, not that nothing was written.
                step = "reading the AD object";
                var confirmError = ConfirmDirectoryObject(ps, objectGuid, roomEmail, credential);
                if (confirmError != null)
                {
                    _logger.LogWarning("Set-ADUser City/State/Country write for {Room} refused before its write: {Refusal}", roomEmail, confirmError);
                    return (confirmError, null, null);
                }

                step = "Set-ADUser";
                // Write by the validated objectGUID (immutable identity), never by a name.
                ps.AddCommand("Set-ADUser")
                  .AddParameter("Identity", objectGuid)
                  .AddParameter("Replace", new System.Collections.Hashtable(attrs))
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                // To the server the preflight read the object through (aud-a2q); none - the default domain
                // controller - only for its explicit fallback.
                if (server is not null)
                    ps.AddParameter("Server", server);
                // aud-a2q, aud-a2n's rule on this write: every outcome is held for the read-back below, not
                // only a clean return. A Set-ADUser that committed and then threw - a timeout after the server
                // took it - came back partial with the values in place. The error is the one it throws: under
                // -ErrorAction Stop an error the cmdlet writes is thrown from Invoke and leaves no record in
                // the stream (aud-a2o, measured), so there is no HadErrors check - it could see only a record
                // another command left in this shared stream (psl-2).
                Exception? writeError = null;
                try
                {
                    ps.Invoke();
                }
                catch (Exception ex)
                {
                    writeError = ex;
                }
                finally
                {
                    // The read-back runs on this pipeline: the write must not stay queued before it - it would run
                    // again, piped into the read - nor leave a record the read's own error check would take for its own.
                    ps.Commands.Clear();
                    ps.Streams.Error.Clear();
                }

                // aud-a2p. Done is the attributes reading back as written, not the write raising no error: a
                // domain controller that dropped the values, or a constraint it did not report, raises none and
                // changes nothing (Known Failure Class 2; aud-a2l for the room list). Read by the objectGUID
                // written, from the server it was sent to - whatever the write reported (aud-a2q). A
                // difference, or a read that cannot be made, fails the step naming the attribute and the server,
                // and nothing is reported written (aud-a2g). No retry: the operator re-runs.
                string? readError;
                List<string> unconfirmed;
                try
                {
                    (readError, unconfirmed) = ReadBackSyncedAttributes(ps, objectGuid, attrs, server, credential);
                }
                catch (Exception ex)
                {
                    (readError, unconfirmed) = (ex.Message, []);
                }
                var readBack = string.Join(", ", SyncedAttributesReadBack);
                // aud-a2q. The write raised an error. Read back as written, the row succeeds - the values it
                // asked for are in place - but no object is reported written (aud-a2g): after an error nothing
                // tells whether this row's write put them there or they were there already. The detail says
                // so, with the error's own text, on the step and in the audit (as attributesConfirmedPresent,
                // where the written object would have been named), as aud-a2n does for the membership. Not as
                // written, or unreadable, the row is the failure it was, in the write's own words ("Failed to
                // write ..."), followed by what the read-back found.
                if (writeError is not null)
                {
                    if (readError != null)
                    {
                        _logger.LogError(writeError, "Set-ADUser City/State/Country write failed for {Room} on {Object} through {Server}, and reading it back afterwards failed too: {ReadError}", roomEmail, objectGuid, serverName, readError);
                        return ($"Failed to write City/State/Country on the AD object: {Sentence(writeError.Message)} Reading {readBack} back from {serverName} afterwards failed too: {readError}", null, null);
                    }
                    if (unconfirmed.Count > 0)
                    {
                        var found = string.Join("; ", unconfirmed);
                        _logger.LogError(writeError, "Set-ADUser City/State/Country write failed for {Room} on {Object} through {Server}; read back afterwards: {Detail}", roomEmail, objectGuid, serverName, found);
                        return ($"Failed to write City/State/Country on the AD object: {Sentence(writeError.Message)} Read back from {serverName} afterwards, {found}.", null, null);
                    }
                    var confirmed = $"{AttributesConfirmedAfterWriteError} ({writeError.Message}): the AD object {objectGuid}, read back from {serverName}, holds them as written. No directory object is recorded as written: whether this row's write put them there is not known.";
                    _logger.LogWarning("Set-ADUser City/State/Country write for {Room} on {Object}: {Confirmed}", roomEmail, objectGuid, confirmed);
                    return (null, null, confirmed);
                }
                if (readError != null)
                {
                    _logger.LogWarning("Set-ADUser City/State/Country write for {Room} on {Object} could not be confirmed from {Server}: {Error}", roomEmail, objectGuid, serverName, readError);
                    return ($"Set-ADUser on the AD object {objectGuid} for {roomEmail} could not be confirmed: reading {readBack} back from {serverName} failed: {readError}", null, null);
                }
                if (unconfirmed.Count > 0)
                {
                    var detail = string.Join("; ", unconfirmed);
                    _logger.LogWarning("Set-ADUser City/State/Country write for {Room} on {Object} not confirmed from {Server}: {Detail}", roomEmail, objectGuid, serverName, detail);
                    return ($"Set-ADUser on the AD object {objectGuid} for {roomEmail} was not confirmed: read back from {serverName}, {detail}.", null, null);
                }

                return (null, objectGuid, null);
            }
            catch (Exception ex)
            {
                // Only the steps before the write's Invoke get here (see step), so nothing was written.
                _logger.LogError(ex, "City/State/Country for {Room} were not written on the AD object {Object}: {Step} failed", roomEmail, objectGuid, step);
                return ($"City/State/Country were not written on the AD object {objectGuid}: {step} failed: {ex.Message}", null, null);
            }
        });
    }

    // aud-a2f. Whether the room can be added to its room list, settled before any write. A list
    // that does not exist yet is created cloud-side and a cloud list takes the cloud add, so neither
    // needs anything further. A list synchronized from on-premises takes the room only on-prem
    // (AddToRoomListViaAdAsync below), so everything that add needs and can be checked without
    // writing - a proven AD object, the AD credential, that object readable by its objectGUID,
    // exactly one on-prem group in the whole forest (aud-a2j) carrying the list's address (aud-a2k),
    // and whether the room is already in it (aud-a2i) - is checked here, and its absence refuses the
    // row while nothing has been written. A list whose sync state Exchange did not report is refused
    // too: whether the row can complete is then unknown, and the old order found out by writing.
    // Returns the refusal, or null for none - and, since aud-a2e, beside it what is described below.
    // (A genuine failure of the membership write after a passing
    // preflight - missing write-members delegation, say - remains the inherent two-system residual.)
    // roomPrimary is the address Exchange returned for the validated room: the membership test's key.
    //
    // aud-a2e. And the list checked as a write target - Protected Group Targets, through the shared gate
    // (CheckRoomListTarget) - before this reports the add as possible: a list Exchange Online holds as Exchange
    // reported it, always, the check needing no directory; a synced list by the on-prem group resolved below, the
    // object the on-prem add writes into, whenever the add may write - not when Exchange already shows the room on
    // the list, which writes nothing to it and needs nothing on-prem (aud-a2f). A list the check refuses refuses the
    // whole row here, nothing written: the fail-closed answer to whether Set-Place may proceed when the room cannot be
    // added to its list. Returns, beside the refusal, the list as checked - a synced one with the group the check
    // cleared, which the add checks its cloud write against again - and a servicer's note, when one's grant allowed it.
    private async Task<(string? error, RoomListTarget list, string? servicedNote)> PreflightRoomListAsync(
        ValidatedMailbox room, string roomPrimary, string roomEmail, RoomListTarget list, ClaimsPrincipal? actingUser)
    {
        // aud-a2e. A list the row creates does not exist yet, so there is nothing to check before the writes: the add checks
        // the list it created, as the creation returned it, before it adds the room.
        if (!list.Exists)
            return (null, list, null);

        // aud-a2m. Every call on an existing list after this read names it by its Guid, cloud list or synced,
        // so a list Exchange reported without one cannot be added to. Refused here, while nothing has been
        // written, rather than at the add; and never by the name instead - "we do not guess".
        if (list.Guid is null)
        {
            var refusal = $"Exchange reported no GUID for room list '{list.Name}', so the list cannot be named by an immutable identity, and the room is not added to it by its name. Check the list in Exchange Online.";
            // aud-a2u. Logged where it is decided, as every refusal on these paths. In the room-list preflight's words, not
            // the on-prem one's: this and the next refuse an existing list of either kind, before its sync state is known.
            // Each is built without its " Nothing was written.", so the line carries the refusal as the others do and the
            // message is unchanged.
            _logger.LogWarning("Room-list preflight for {Room} on {List} refused the row; nothing was written: {Refusal}", roomEmail, list.Name, refusal);
            return ($"{refusal} Nothing was written.", list, null);
        }

        if (list.IsDirSynced is null)
        {
            var refusal = $"Exchange did not report whether room list '{list.Name}' is synchronized from on-premises, so it cannot be established before writing that the room can be added to it.";
            _logger.LogWarning("Room-list preflight for {Room} on {List} refused the row; nothing was written: {Refusal}", roomEmail, list.Name, refusal);
            return ($"{refusal} Nothing was written.", list, null);
        }

        if (!list.TakesOnPremFallback)
        {
            // aud-a2e. A list Exchange Online holds takes the cloud add, which writes into the object this list's read
            // returned: checked as that read described it (CloudListTarget) - before anything is written, and again by the
            // add just before its write. Not after a membership read: the check needs no directory, and a protected list
            // refuses the row whether or not the room is on it already - the stricter direction.
            var decision = CheckRoomListTarget(CloudListTarget(list.Name, list.PrimarySmtpAddress, list.ExternalDirectoryObjectId), actingUser);
            if (!decision.Allowed)
            {
                var refusal = RoomListTargetRefusal(list.Name, decision);
                _logger.LogWarning("Room-list preflight for {Room} on {List} refused the row; nothing was written: {Refusal}", roomEmail, list.Name, refusal);
                return ($"{refusal} Nothing was written.", list, null);
            }
            return (null, list, decision.ServicedNote);
        }

        // Already on the list, the add writes nothing - it finds the room among the members, by the
        // same test - so nothing on-prem is needed or asked for. Without this, a synced room re-run on
        // its synced list was refused whenever Active Directory could not be proven: a row that, before
        // this preflight existed, completed without touching the list at all.
        // aud-a2e. Nor is the list's group checked: no group is looked up, and nothing is written to the list. Should the
        // add find the room gone from it, its cloud add has no checked group and is not made (RoomListNotCheckedRefusal).
        if (await IsOnRoomListAsync(list.Guid, roomPrimary))
            return (null, list, null);

        if (room.DirectoryObjectGuid is not { } roomGuid)
        {
            var refusal = NoDirectoryObject(roomEmail, $"it cannot be added to room list '{list.Name}', which is synchronized from on-premises and takes members only there");
            _logger.LogWarning("On-prem room-list preflight for {Room} on {List} refused the row; nothing was written: {Refusal}", roomEmail, list.Name, refusal);
            return ($"{refusal} Nothing was written.", list, null);
        }

        // aud-a2v. As the City/State/Country preflight's: the line names the room, the list and the purpose asked with. The
        // refusal is built without its " Nothing was written.", as the three above, and the message is unchanged - but for
        // its opening clause, which since aud-a2x says why the lookup returned none, as the City/State/Country preflight's.
        var purpose = $"on-prem room-list membership preflight for {roomEmail}";
        var lookup = await LookupDirectoryCredentialAsync(purpose);
        var creds = lookup.Credentials;
        if (creds is null)
        {
            var refusal = $"{DirectoryCredentialRefusal(lookup.Failure)}, and room list '{list.Name}' is synchronized from on-premises, so the room cannot be added to it.";
            _logger.LogWarning("On-prem room-list preflight for {Room} on {List} refused the row; nothing was written: no AD credential was returned for {Purpose}: {Refusal}", roomEmail, list.Name, purpose, refusal);
            return ($"{refusal} Nothing was written.", list, null);
        }

        return await Task.Run<(string? error, RoomListTarget list, string? servicedNote)>(() =>
        {
            // aud-a2s. The step under way, for the catch at the end, as the City/State/Country paths keep it (aud-a2q,
            // aud-a2r). A throw from any step here was told as "Failed to validate the on-prem room list ... before
            // writing: <error>" with no step named but the import, which a catch of its own named (aud-a2r): the
            // runspace, the credential, the room's object read, the group search, the group's re-read and the membership
            // read all read alike. The resolver names its own three steps (ResolveAdGroupGuid). Every step here runs
            // before Set-Place, so the catch says nothing was written.
            var step = "opening the directory runspace";
            try
            {
                using var runspace = OpenDirectoryRunspace();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                step = "importing the ActiveDirectory module";
                ActiveDirectoryModuleImport.Import(ps);

                step = "preparing the module's AD credential";
                var credential = CreateAdCredential(creds.Value.username, creds.Value.password, creds.Value.domain);

                // The two objects the on-prem add names - the room's proven object and the list's
                // group - each read here exactly as the add will read them, without writing.
                step = "reading the room's AD object";
                var roomError = ConfirmDirectoryObject(ps, roomGuid, roomEmail, credential, out var roomDn);
                // Before any write: the refusal says so, as the resolver's and the membership read's below do (aud-a2r),
                // and is logged, as theirs are (aud-a2t, aud-a2u).
                if (roomError != null)
                {
                    _logger.LogWarning("On-prem room-list preflight for {Room} on {List} refused the row; nothing was written: {Refusal}", roomEmail, list.Name, roomError);
                    return ($"{Sentence(roomError)} Nothing was written.", list, null);
                }

                // The resolver's refusals are shared with the add, which meets them after the writes,
                // so here, as for the membership read below, the preflight says nothing was written.
                var (groupError, group) = ResolveAdGroupGuid(ps, list.Name, list.PrimarySmtpAddress, list.ExternalDirectoryObjectId, credential, ref step);
                if (groupError != null || group is null)
                {
                    // aud-a2t. A refusal the resolver returns is logged here, where it refuses the row, once, at Warning. It
                    // used to reach only the row's result and audit - only a throw was logged, by the catch below - so the
                    // application log held no trace of why the row was refused. Here rather than in the resolver: a static
                    // reader shared with the add, it says why, and only this path knows that nothing was written yet, as
                    // its catch's line says. Not again by the row, which puts the refusal on its result. One line for every
                    // refusal the resolver returns, the error-record ones too, which no stand-in reaches.
                    var refusal = groupError ?? $"The on-prem group for room list '{list.Name}' could not be resolved.";
                    _logger.LogWarning("On-prem room-list preflight for {Room} on {List} refused the row; nothing was written: {Refusal}", roomEmail, list.Name, refusal);
                    return ($"{refusal} Nothing was written.", list, null);
                }

                // aud-a2e. The group the on-prem add writes into, as its own domain has just answered for it, checked as a
                // write target before anything is written: by its objectGUID and DN (Protected Group Targets, OUs), its
                // sAMAccountName (patterns) and the list's address and Entra object id (user rows). Right after it is
                // resolved, whatever its own member attribute holds: Exchange shows the room off the list, so the cloud add
                // will be made, and the add checks this same group again before it.
                var decision = CheckRoomListTarget(group, actingUser);
                if (!decision.Allowed)
                {
                    var refusal = RoomListTargetRefusal(list.Name, decision);
                    _logger.LogWarning("On-prem room-list preflight for {Room} on {List} refused the row; nothing was written: {Refusal}", roomEmail, list.Name, refusal);
                    return ($"{refusal} Nothing was written.", list, null);
                }

                // aud-a2i. And whether the add will write at all: the room's membership of that group,
                // read as the add reads it. A membership that cannot be read refuses the row here, while
                // nothing has been written. It used to be read only at the add, by Get-ADGroupMember -
                // which fails on a group past the ADWS cap or with a member in another domain - after
                // Set-Place and the timezone had committed, so such a list made every row partial.
                step = "reading the membership of the list's group";
                var (memberError, _) = ReadOnPremListMembership(ps, group.ObjectGuid, group.DistinguishedName, roomDn, list.Name, credential);
                if (memberError is null)
                    return (null, list with { DirectoryGroup = group }, decision.ServicedNote);
                // aud-a2t. Logged as the resolver's refusal above. Not inside the membership read: it is also the add's
                // read-back, whose outcome the add logs with what the write reported (aud-a2n), so a line there would log
                // that outcome twice.
                _logger.LogWarning("On-prem room-list preflight for {Room} on {List} refused the row; nothing was written: {Refusal}", roomEmail, list.Name, memberError);
                return ($"{memberError} Nothing was written.", list, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "On-prem room-list preflight for {Room} on {List}: {Step} failed; nothing was written", roomEmail, list.Name, step);
                return ($"Failed to validate the on-prem room list '{list.Name}' before writing: {step} failed: {Sentence(ex.Message)} Nothing was written.", list, null);
            }
        });
    }

    // Adds a room mailbox to an on-prem-mastered (DirSync'd) room-list group via the on-prem
    // AD object. Exchange Online refuses Add-DistributionGroupMember against a synced group
    // ("out of the current user's write scope ... being synchronized from your on-premises
    // organization"), so membership must be written on-prem and then syncs up (~30 min) - the
    // same shape as the Set-ADUser City/State/Country handling. Returns (null, the room's
    // objectGUID, null) once the membership write is confirmed - the room's DN read back on the
    // group's member attribute after it (aud-a2l) - (null, null, null) for the already-a-member
    // no-op - nothing was written - (null, null, a detail) when the write was refused or reported an
    // error and the read-back found the room all the same - a membership confirmed, not one this row
    // is known to have written (aud-a2n) - and (a failure message, null, null) otherwise (aud-a2g).
    // See docs/ConferenceRooms-OnPremRoomListAdd-Plan.md, and aud-a2j for the form of the write.
    //
    // aud-a2d. The member added is the AD object the protection gate proved owns the room's
    // mailbox, by objectGUID - not a userPrincipalName match of the typed string. aud-a2k. The group
    // is found by the list's address, the primary SMTP address Exchange reported for the list before
    // any write - the address the preflight found it by.
    //
    // aud-a2e. And the group, as it resolves here, is checked as a write target (Protected Group
    // Targets) immediately before the write that names its objectGUID - after the membership read, so
    // the already-a-member no-op, which writes nothing, is not refused. The fourth value is the note of
    // a servicer's grant that allowed the write, carried on every outcome after it (pgwt-7).
    private async Task<(string? error, string? written, string? confirmed, string? servicedNote)> AddToRoomListViaAdAsync(
        ValidatedMailbox room, string roomEmail, RoomListTarget list, ClaimsPrincipal? actingUser)
    {
        var roomListName = list.Name;
        if (room.DirectoryObjectGuid is not { } roomGuid)
        {
            var refusal = NoDirectoryObject(roomEmail, "the room could not be added to the on-prem room list");
            // aud-a2u. Logged as the add's other refusals. Through the row only a race reached it: the preflight passes a
            // room with no object on a synced list only when Exchange shows the room on the list already, and the add's own
            // membership read then no longer does. Since aud-a2e not even that: no group was checked for such a list, so
            // the add refuses its cloud write (RoomListNotCheckedRefusal) and never falls back to this one.
            _logger.LogWarning("On-prem room-list add for {Room} on {List} refused before its write: {Refusal}", roomEmail, roomListName, refusal);
            return (refusal, null, null, null);
        }

        // aud-a2v. As the preflight's, in the add's words - after Set-Place, inside the row's partial. Reached when this lookup
        // returns none though the preflight's returned one, or when the preflight asked for none - the room on the list in
        // Exchange then - and the cloud add no longer finds it there.
        var purpose = $"on-prem room-list membership add for {roomEmail}";
        var lookup = await LookupDirectoryCredentialAsync(purpose);
        var creds = lookup.Credentials;
        if (creds is null)
        {
            var refusal = $"{DirectoryCredentialRefusal(lookup.Failure)}. The room could not be added to the on-prem room list.";
            _logger.LogWarning("On-prem room-list add for {Room} on {List} refused before its write: no AD credential was returned for {Purpose}: {Refusal}", roomEmail, roomListName, purpose, refusal);
            return (refusal, null, null, null);
        }

        return await Task.Run<(string? error, string? written, string? confirmed, string? servicedNote)>(() =>
        {
            // aud-a2s. The step under way, as in the preflight above. The catch at the end logged "On-prem room-list member
            // add (Set-ADGroup on the group's member attribute) failed" and told the row it had failed to add the room on
            // the AD object, whichever step had thrown - the runspace, the credential, the room's object read, the group
            // search, the group's re-read, the membership read - none of them the write. The write and the read-back
            // settle their own errors below, each in its own words (aud-a2n), so only the steps before the write's Invoke
            // reach the catch: whatever reaches it, this row did not add the room.
            var step = "opening the directory runspace";
            // aud-a2e. Set by the write-target check below when a servicer's grant allowed the write; every outcome after it
            // carries it, the catch's included.
            string? servicedNote = null;
            try
            {
                using var runspace = OpenDirectoryRunspace();
                using var ps = PowerShell.Create();
                ps.Runspace = runspace;

                step = "importing the ActiveDirectory module";
                ActiveDirectoryModuleImport.Import(ps);

                step = "preparing the module's AD credential";
                var credential = CreateAdCredential(creds.Value.username, creds.Value.password, creds.Value.domain);

                // BOTH objects by immutable GUID, fail-closed, before any write - so the wrong
                // member is never added to the wrong group. The room's is the validated one.
                step = "reading the room's AD object";
                var roomError = ConfirmDirectoryObject(ps, roomGuid, roomEmail, credential, out var roomDn);
                // After Set-Place and the timezone, inside the row's partial: the refusal is returned as it is, with no
                // "Nothing was written." (aud-a2r), and logged as the resolver's below (aud-a2t, aud-a2u).
                if (roomError != null)
                {
                    _logger.LogWarning("On-prem room-list add for {Room} on {List} refused before its write: {Refusal}", roomEmail, roomListName, roomError);
                    return (roomError, null, null, null);
                }

                var (groupError, group) = ResolveAdGroupGuid(ps, roomListName, list.PrimarySmtpAddress, list.ExternalDirectoryObjectId, credential, ref step);
                if (groupError != null || group is null)
                {
                    // aud-a2t. Logged where the add refuses, once, as in the preflight above. This is after Set-Place,
                    // inside the row's partial, so the line says the add's write was not made, not that nothing was.
                    var refusal = groupError ?? $"The on-prem group for room list '{roomListName}' could not be resolved.";
                    _logger.LogWarning("On-prem room-list add for {Room} on {List} refused before its write: {Refusal}", roomEmail, roomListName, refusal);
                    return (refusal, null, null, null);
                }
                var groupGuid = group.ObjectGuid;
                var groupDn = group.DistinguishedName;

                // Idempotency: skip the add if the room is already a member (matches the cloud
                // path's already-member no-op so a re-run is safe). aud-a2i: read from the group's
                // member attribute, as the preflight read it before anything was written - not by
                // Get-ADGroupMember. Read again here rather than carried across the writes: a stale
                // "already a member" would report success, silently, for a room taken off the group in
                // between; a fresh read adds it, or fails loudly.
                step = "reading the membership of the list's group";
                var (memberError, alreadyMember) = ReadOnPremListMembership(ps, groupGuid, groupDn, roomDn, roomListName, credential);
                if (memberError != null)
                {
                    // aud-a2t. As the resolver's refusal above. The same read as the read-back below, whose outcome is
                    // logged there, with the write's.
                    _logger.LogWarning("On-prem room-list add for {Room} on {List} refused before its write: {Refusal}", roomEmail, roomListName, memberError);
                    return (memberError, null, null, null);
                }
                if (alreadyMember)
                    return (null, null, null, null);

                // aud-a2e. The group checked immediately before the write into it, as the object the write names: the
                // snapshot resolved just above, whose objectGUID is the write's -Identity and whose DN routes it - the
                // group this add writes, whatever group the preflight cleared. Refused, the room is not added: after
                // Set-Place, inside the row's partial, so the refusal does not say nothing was written.
                var decision = CheckRoomListTarget(group, actingUser);
                if (!decision.Allowed)
                {
                    var refusal = RoomListTargetRefusal(roomListName, decision);
                    _logger.LogWarning("On-prem room-list add for {Room} on {List} refused before its write: {Refusal}", roomEmail, roomListName, refusal);
                    return (refusal, null, null, null);
                }
                servicedNote = decision.ServicedNote;

                // aud-a2j. The room goes on the group's own member attribute, written on the group's
                // domain - the form both group modules write (GroupManagementService.
                // BuildMemberAttributeWrite). Not Add-ADGroupMember: with no -Server it cannot find a
                // group outside the default domain, and routed to the group's domain it resolves
                // -Members there as well, where a room from another domain does not exist ("Cannot find
                // an object with identity ... under: ...", Group Management's live dev failure of
                // 2026-09-03). Left as it was, the preflight would pass a group the forest search found
                // in another domain and this write would fail after Set-Place - the partial aud-a2f
                // exists to prevent. The group by the objectGUID its own domain answered with; the room
                // by the DN of its proven object, the DN the membership read above compared, never null
                // here - that read refuses a room read without one. Not by a name or a UPN.
                step = "Set-ADGroup on the group's member attribute";
                var groupServer = GroupManagementService.ServerFromDn(groupDn);
                ps.AddCommand("Set-ADGroup")
                  .AddParameter("Identity", groupGuid)
                  .AddParameter("Add", GroupManagementService.BuildMemberAttributeWrite(roomDn!))
                  .AddParameter("Server", groupServer)
                  .AddParameter("Credential", credential)
                  .AddParameter("ErrorAction", "Stop");
                // aud-a2n, folded in from codex's review of aud-a2k/a2l. Every outcome of the write is held for
                // the read-back below, not only the value-already-there refusal aud-a2l read back: the row is
                // decided by the group's member attribute, never by what the write reported (Known Failure
                // Class 2 - both group modules settle their writes this way). A write the server took and then
                // failed to answer for - a timeout after the commit - used to come back partial with the room in
                // place; one refused or failing without effect is still a failed row, now carrying its error.
                // aud-a2o. The write's error is the one it throws, as GroupManagementService takes its two Set-ADGroup
                // member writes. Under -ErrorAction Stop an error the cmdlet writes is thrown from Invoke and leaves no
                // record in the stream, so it has no error-without-a-throw to report: the HadErrors check that took
                // one was unreachable for it, and would have taken any record an earlier command left in this shared
                // stream - HadErrors is true while any record remains (psl-2) - for this write's error, telling a
                // clean write as a failing one. That makes -ErrorAction Stop load-bearing: without it a refusal would
                // be a record, cleared below unread, and a raced add reported as written.
                Exception? writeError = null;
                try
                {
                    ps.Invoke();
                }
                catch (Exception ex)
                {
                    writeError = ex;
                }
                finally
                {
                    // The read-back below runs on this pipeline: the write must not stay queued before it,
                    // nor its error be the one the read reports - a record left here would make the read's
                    // HadErrors true (psl-2) and fail a read that succeeded.
                    ps.Commands.Clear();
                    ps.Streams.Error.Clear();
                }
                // Whether the error is the refusal of a value already there now decides only how the outcome is
                // told - which detail a confirmed membership carries, how a failure is worded - never whether the
                // read-back runs or what it decides. Known by the error's code first, its text only without one.
                var refusedAsPresent = writeError is not null && IsValueAlreadyPresentRefusal(writeError);

                // aud-a2l. The write is not taken as done because it raised no error - a domain controller
                // that dropped the value, or a constraint it did not report, raises none and changes nothing
                // (Known Failure Class 2). Done is the room's DN on the group's member attribute, read back
                // with the read the preflight and the check above made (aud-a2i: by the group's DN, on the
                // domain that holds it, the answer's objectGUID checked, compared by DN), on the domain the
                // write went to. Not there - or not readable - is a failure naming the group and the server
                // asked, and the audit names nothing written (aud-a2g). No retry: the operator re-runs, and
                // a re-run finds the room already a member, or writes again.
                string? readError;
                bool present;
                try
                {
                    (readError, present) = ReadOnPremListMembership(ps, groupGuid, groupDn, roomDn, roomListName, credential);
                }
                catch (Exception ex)
                {
                    // A read that threw - the domain controller unreachable, say - says nothing either way.
                    (readError, present) = (ex.Message, false);
                }
                if (readError != null)
                {
                    _logger.LogWarning("On-prem room-list add for {Room} on {List} could not be confirmed from {Server}: {Error} (the write reported: {WriteError})", roomEmail, roomListName, groupServer, readError, writeError?.Message ?? "no error");
                    return ($"Adding {roomEmail} to on-prem room list '{roomListName}' could not be confirmed: reading its group {groupDn} back from {groupServer} failed: {readError}"
                        + (writeError is null ? ""
                            : refusedAsPresent ? $" Before that, Active Directory refused the add as a value already there ({writeError.Message})."
                            : $" Before that, Set-ADGroup reported an error ({writeError.Message})."), null, null, servicedNote);
                }
                if (!present)
                {
                    _logger.LogWarning("On-prem room-list add for {Room} on {List} not confirmed: {Group} read back from {Server} does not hold the room (the write reported: {WriteError})", roomEmail, roomListName, groupDn, groupServer, writeError?.Message ?? "no error");
                    return ($"Adding {roomEmail} to on-prem room list '{roomListName}' was not confirmed: its group {groupDn}, read back from {groupServer}, does not hold the room as a member"
                        + (writeError is null ? "."
                            : refusedAsPresent ? $", though Active Directory refused the add as a value already there ({writeError.Message})."
                            : $", and Set-ADGroup reported an error ({writeError.Message})."), null, null, servicedNote);
                }
                // The room there and the write reported nothing: this row's write put it there (aud-a2g).
                if (writeError is null)
                    return (null, roomGuid, null, servicedNote);

                // aud-a2n, folded-in item 1, by aud-a2g's rule - name only what a write produced. The room is there
                // after a refused or failing write: the row succeeds, the membership it asked for being in place,
                // but no directory object is reported written. A refusal says this row's write changed nothing -
                // another actor's add got there first; after any other error nothing can tell whether it did. The
                // detail says which, with the error's own text, in the step and in the audit (as
                // membershipConfirmedPresent, where the written object would have been named).
                var confirmed = refusedAsPresent
                    ? $"{ConfirmedAfterRefusal} ({writeError.Message}): its group {groupDn}, read back from {groupServer}, holds the room. No directory object is recorded as written: this row's add did not put it there."
                    : $"{ConfirmedAfterWriteError} ({writeError.Message}): its group {groupDn}, read back from {groupServer}, holds the room. No directory object is recorded as written: whether this row's add put it there is not known.";
                _logger.LogWarning("On-prem room-list add for {Room} on {List}: {Confirmed}", roomEmail, roomListName, confirmed);
                return (null, null, confirmed, servicedNote);
            }
            catch (Exception ex)
            {
                // Only the steps before the write's Invoke get here (see step), so the room was not added by this row. This
                // comes after Set-Place and the timezone, inside the row's partial, so it does not say nothing was written.
                _logger.LogError(ex, "On-prem room-list add for {Room} on {List}: {Step} failed", roomEmail, roomListName, step);
                return ($"Failed to add the room to the on-prem room list '{roomListName}': {step} failed: {ex.Message}", null, null, servicedNote);
            }
        });
    }

    // Confirms the validated room's AD object can be read, by its objectGUID, before an AD write
    // (aud-a2d). Shared by the pre-mutation preflight and the writes, so they read the same object.
    // Returns an error message, or null when exactly that object answered. It replaced a lookup
    // of the typed string by userPrincipalName, which found whichever object held that UPN.
    // Assumes the ActiveDirectory module is already imported into the runspace.
    // aud-a2r. Its refusals say nothing of what was or was not written - the caller knows, as with
    // ResolveAdGroupGuid's. The two preflights, before any write, add "Nothing was written."; the
    // City/State/Country write and the room-list add, after Set-Place, add nothing, their refusal shown
    // inside the row's partial, which says what was written. It used to end every refusal so.
    // aud-a2u. Nor does it log them: each caller logs its refusal where it refuses (aud-a2t's placement) - only the caller
    // knows whether anything was written, which its line says - one line at each of the four for all three refusals, the
    // error record and the empty answer among them, which no stand-in produces.
    private static string? ConfirmDirectoryObject(PowerShell ps, string objectGuid, string roomEmail, PSCredential credential) =>
        ConfirmDirectoryObject(ps, objectGuid, roomEmail, credential, out _);

    // The same, also reporting the object's distinguished name as Active Directory returned it (null
    // when it returned none): the on-prem room-list membership is compared by it (aud-a2i).
    private static string? ConfirmDirectoryObject(PowerShell ps, string objectGuid, string roomEmail, PSCredential credential, out string? distinguishedName)
    {
        distinguishedName = null;
        ps.AddCommand("Get-ADUser")
          .AddParameter("Identity", objectGuid)
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var found = ps.Invoke();
        if (ps.HadErrors)
            return FirstError(ps) ?? $"AD lookup of the validated object {objectGuid} for {roomEmail} failed.";
        ps.Commands.Clear();

        if (found.Count != 1)
            return $"The AD object validated for '{roomEmail}' ({objectGuid}) could not be read ({found.Count} found).";

        var readGuid = found[0].Properties["ObjectGUID"]?.Value?.ToString();
        if (!string.Equals(readGuid, objectGuid, StringComparison.OrdinalIgnoreCase))
            return $"Active Directory answered {objectGuid} with a different object ({readGuid ?? "no ObjectGUID"}).";

        distinguishedName = found[0].Properties["DistinguishedName"]?.Value?.ToString();
        return null;
    }

    // aud-a2q. The validated object read by its objectGUID through server - the route the City/State/Country write and
    // its read-back take - for the preflight. In the shape of this file's other reads: an error record fails it, and
    // exactly one object, the one asked for, must answer. Returns why not, or null; a throw is the caller's to catch,
    // and the caller reports the reason in a refusal naming the server.
    private static string? ReadObjectThrough(PowerShell ps, string objectGuid, string server, PSCredential credential)
    {
        ps.AddCommand("Get-ADUser")
          .AddParameter("Identity", objectGuid)
          .AddParameter("Server", server)
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var found = ps.Invoke();
        if (ps.HadErrors)
            return FirstError(ps) ?? $"Get-ADUser reported an error reading {objectGuid}.";
        ps.Commands.Clear();

        var answered = found.Count == 1 ? found[0]?.Properties["ObjectGUID"]?.Value?.ToString() : null;
        return answered is not null && string.Equals(answered, objectGuid, StringComparison.OrdinalIgnoreCase)
            ? null
            : $"{found.Count} object(s) answered, ObjectGUID {answered ?? "none"}";
    }

    // aud-a2p. The attributes the City/State/Country write is confirmed by: City, State and Country as the operator
    // gave them. c and countryCode go in the same -Replace as co and are not read - *assumption, not probed*: one
    // -Replace is one LDAP modify, applied whole, so co read back as written says the request applied.
    private static readonly string[] SyncedAttributesReadBack = ["l", "st", "co"];

    // aud-a2p. The attributes Set-ADUser wrote, read back by the objectGUID written, from the server the write was
    // sent to (null: the module's default, as the write's). In the shape of this file's other reads: an error record
    // fails it - after a read, whatever left one, the answer is not known good, and failing is the safe direction -
    // and exactly one object, the one written, must answer: another object's attributes confirm nothing. Returns the
    // read's error, or what did not read back as written (empty when everything did).
    private static (string? error, List<string> unconfirmed) ReadBackSyncedAttributes(
        PowerShell ps, string objectGuid, IReadOnlyDictionary<string, object> written, string? server, PSCredential credential)
    {
        ps.AddCommand("Get-ADUser")
          .AddParameter("Identity", objectGuid)
          .AddParameter("Properties", SyncedAttributesReadBack.ToArray())
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        if (server is not null)
            ps.AddParameter("Server", server);
        var found = ps.Invoke();
        if (ps.HadErrors)
            return (FirstError(ps) ?? $"Get-ADUser reported an error reading {objectGuid}.", []);
        ps.Commands.Clear();

        var answered = found.Count == 1 ? found[0]?.Properties["ObjectGUID"]?.Value?.ToString() : null;
        if (answered is null || !string.Equals(answered, objectGuid, StringComparison.OrdinalIgnoreCase))
            return ($"{found.Count} object(s) answered, ObjectGUID {answered ?? "none"}", []);

        return (null, UnconfirmedSyncedAttributes(written, found[0]));
    }

    // aud-a2p. What the write put in its -Replace that the object read back does not hold, one description each. Only
    // what this row wrote: an attribute it left blank is not written, so whatever the object holds there is not this
    // write's to confirm. The write's own values - BuildSyncedUserAttributes's, trimmed as it trims - against the value
    // as read, nothing trimmed, compared ordinal: stricter than the directory's own matching, and a difference only
    // ever fails the row, never passes it. An empty value and an absent attribute are the same, the directory keeping
    // no empty value. Pure, for unit testing.
    internal static List<string> UnconfirmedSyncedAttributes(IReadOnlyDictionary<string, object> written, PSObject read)
    {
        var unconfirmed = new List<string>();
        foreach (var name in SyncedAttributesReadBack)
        {
            if (!written.TryGetValue(name, out var value))
                continue;
            var expected = EmptyAsAbsent(value?.ToString());
            var actual = EmptyAsAbsent(ReadDirectoryString(read, name));
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                unconfirmed.Add($"{name} ({SyncedAttributeLabel(name)}) {(actual is null ? "is not set" : $"is '{actual}'")}, not {(expected is null ? "empty" : $"'{expected}'")} as written");
        }
        return unconfirmed;
    }

    private static string SyncedAttributeLabel(string name) => name switch
    {
        "l" => "City",
        "st" => "State",
        "co" => "Country",
        _ => name,
    };

    private static string? EmptyAsAbsent(string? value) => string.IsNullOrEmpty(value) ? null : value;

    // The text ended as a sentence, so another can follow it in one message.
    private static string Sentence(string text)
    {
        var trimmed = text.TrimEnd();
        return trimmed.EndsWith('.') ? trimmed : trimmed + ".";
    }

    // A single-valued attribute as read, null when absent. *Assumption, not probed*: the module answers one with its
    // value, as ADAttributeEditorService reads its attributes (Properties[..].Value). A collection is taken as its one
    // element, so a collection-shaped answer is not told as a difference; several values are not the one written.
    private static string? ReadDirectoryString(PSObject read, string name)
    {
        var value = Unwrapped(read.Properties[name]?.Value);
        if (value is null or string)
            return (string?)value;
        if (value is System.Collections.IEnumerable values)
        {
            var items = values.Cast<object?>().Select(v => Unwrapped(v)?.ToString()).ToList();
            return items.Count switch
            {
                0 => null,
                1 => items[0],
                _ => string.Join("; ", items),
            };
        }
        return value.ToString();
    }

    private static object? Unwrapped(object? value) => value is PSObject wrapped ? wrapped.BaseObject : value;

    // Resolve a room list (distribution group) to exactly one immutable on-prem ObjectGUID, and
    // refuse on not-found or ambiguous - same fail-closed posture as ConfirmDirectoryObject so an
    // on-prem membership write can never target the wrong group. Shared by the room-list preflight
    // and the add (aud-a2f), so they resolve the same group. Also reports the group's DN, by which
    // its membership is read (aud-a2i). Assumes the ActiveDirectory module is already imported.
    //
    // aud-a2k. Found by the list's address and nothing else: the one group whose mail is the
    // primary SMTP address Exchange holds for the list (listAddress, read with its sync state before
    // any write). It used to be matched by the list's NAME as displayName or name - the mail clause
    // compared an address attribute with a name and could never match - so one unrelated group
    // carrying that name, with the synced group out of the result, was written in its place. Names
    // are not identities (aud-a2b, aud-a2c, aud-a2d), and Exchange exposes no immutable attribute
    // tying a synced group to its on-prem object, so the address is the binding. When it finds no
    // group or several, the refusal names the list and the address searched, and what to put right;
    // it does not offer a likely-looking group to choose: the data is the operator's to correct, not
    // the app's to guess at (owner, .agents/decisions.md 2026-10-15).
    //
    // aud-a2j. Searched in the forest's global catalog, not in one domain. Without -Server,
    // Get-ADGroup is answered by a domain controller of the domain the cmdlet binds to by default
    // and sees only that domain's groups, so a room list synced from a group in any other domain of
    // the forest came back "No on-prem AD group found" and a legitimate row was refused. The
    // catalog widens what can match, and the exactly-one rule holds across all of it: an address
    // that a group in each of two domains carries refuses. No catalog refuses too, before any write -
    // never a fallback to one domain, which is the shape this replaced. The one match is re-read
    // in its own domain (ReadAdGroupInItsDomain), and that read's objectGUID and DN are what the
    // membership read and the write use, so the preflight and the add resolve the same object the
    // same way.
    //
    // aud-a2s. Its three steps - the catalog, the search, the re-read - are one call to the preflight
    // and the add, so it names each in the caller's step, by ref, before running it: a throw from the
    // search or the re-read reaches the caller's catch, which names the step under way. The catalog's
    // failure never throws - ResolveForestGlobalCatalog catches it - and is refused here in words
    // naming it.
    //
    // aud-a2t. It logs nothing: each caller logs a refusal it returns where that refuses the row, once.
    //
    // aud-a2e. It returns the group as a whole snapshot (ReadAdGroupInItsDomain), not its objectGUID and DN alone: the
    // object the Protected Group Targets check is run on is the object whose objectGUID the write names and whose DN routes
    // it and the membership read. listExternalId, the list's Entra object id, rides on the snapshot for the user rows.
    private static (string? error, ResolvedDirectoryPrincipal? group) ResolveAdGroupGuid(PowerShell ps, string roomListName, string? listAddress, string? listExternalId, PSCredential credential, ref string step)
    {
        // A list Exchange reports no address for has nothing to find its group by. Refused before the
        // forest is asked anything: a search for an empty mail is not a search for this list's group,
        // whatever the directory makes of it.
        var address = listAddress?.Trim() ?? "";
        if (address.Length == 0)
            return ($"Exchange reported no primary SMTP address for room list '{roomListName}', so its on-prem group cannot be identified: the group is found by that address alone, never by its name. Check the list in Exchange Online. The room was not added to the list.", null);

        step = "resolving the forest's global catalog";
        var (catalog, catalogError) = ResolveForestGlobalCatalog(ps, credential);
        if (catalog is null)
            return ($"The forest's global catalog could not be resolved ({catalogError}), so the on-prem group for room list '{roomListName}' cannot be looked up across the forest. Refusing rather than searching one domain.", null);

        step = "searching the forest's global catalog for the list's group";
        var escaped = address.Replace("'", "''");
        ps.AddCommand("Get-ADGroup")
          .AddParameter("Filter", $"mail -eq '{escaped}'")
          .AddParameter("Server", catalog)
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var found = ps.Invoke();
        if (ps.HadErrors)
            return (FirstError(ps) ?? $"AD lookup failed for room list '{roomListName}'.", null);
        ps.Commands.Clear();

        if (found.Count == 0)
            return ($"No on-prem AD group in the forest has mail '{address}', the primary SMTP address Exchange holds for room list '{roomListName}'. Check that the list's group in on-premises Active Directory carries exactly that address in its mail attribute. The room was not added to the list.", null);
        if (found.Count > 1)
            return ($"Multiple on-prem AD groups ({found.Count}) in the forest have mail '{address}', the primary SMTP address Exchange holds for room list '{roomListName}'. Check which of them is the list's group; only it may carry that address. Refusing to write to avoid the wrong group.", null);

        var objectGuid = found[0].Properties["ObjectGUID"]?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(objectGuid))
            return ($"Resolved on-prem AD group for room list '{roomListName}' has no ObjectGUID; refusing to write.", null);

        step = "reading the list's group in its own domain";
        return ReadAdGroupInItsDomain(ps, roomListName, objectGuid, found[0].Properties["DistinguishedName"]?.Value?.ToString(), address, listExternalId, credential);
    }

    // aud-a2j. The group the catalog search found, read again by its DN on a domain controller of
    // the domain that DN names - the domain that holds the group, where its membership is read and
    // the write goes. A global catalog holds a partial, read-only copy of every domain and can lag
    // behind it: the group must be there under that DN as the same object (objectGUID), or the row
    // is refused rather than written to a group the catalog remembered. A DN that names no domain
    // cannot be routed, and refuses. GroupManagementService.ResolveMemberForWrite makes the same
    // re-read for the same reason. Assumes the ActiveDirectory module is already imported.
    //
    // aud-a2e. And the group as the Protected Group Targets check needs it - the full snapshot pgwt-2 requires of a write
    // target, never a bare DN: the objectGUID and DN this read answered (the Group Targets and OU rules, and the write's
    // identity and route), the sAMAccountName and name of its default property set (the pattern rules), its mail - the
    // list's address, which the catalog search matched it by (aud-a2k) - and the list's Entra object id (the user rows). A
    // read without a sAMAccountName refuses: a pattern rule would be skipped in silence, as a DN-only snapshot skipped it.
    private static (string? error, ResolvedDirectoryPrincipal? group) ReadAdGroupInItsDomain(
        PowerShell ps, string roomListName, string objectGuid, string? distinguishedName, string listAddress, string? listExternalId, PSCredential credential)
    {
        var server = GroupManagementService.ServerFromDn(distinguishedName);
        if (server is null)
            return ($"The on-prem AD group found for room list '{roomListName}' has no distinguished name that names its domain ({distinguishedName ?? "none"}); refusing to write.", null);

        ps.AddCommand("Get-ADGroup")
          .AddParameter("Identity", distinguishedName)
          .AddParameter("Server", server)
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var read = ps.Invoke();
        if (ps.HadErrors)
            return (FirstError(ps) ?? $"The on-prem AD group for room list '{roomListName}' could not be read in its own domain ({server}).", null);
        ps.Commands.Clear();

        var readGuid = read.Count == 1 ? read[0]?.Properties["ObjectGUID"]?.Value?.ToString() : null;
        var readDn = read.Count == 1 ? read[0]?.Properties["DistinguishedName"]?.Value?.ToString() : null;
        if (readGuid is null || !string.Equals(readGuid, objectGuid, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(readDn))
            return ($"The on-prem AD group the forest's global catalog found for room list '{roomListName}' ({objectGuid}) is not what its own domain ({server}) holds at that name: {read.Count} object(s) answered, ObjectGUID {readGuid ?? "none"}. Refusing to write.", null);

        var samAccountName = read[0]!.Properties["SamAccountName"]?.Value?.ToString();
        if (string.IsNullOrWhiteSpace(samAccountName))
            return ($"The on-prem AD group for room list '{roomListName}' ({readGuid}) was read in its own domain ({server}) without a sAMAccountName, so it cannot be checked against Protected Group Targets; refusing to write.", null);

        return (null, new ResolvedDirectoryPrincipal(
            Source: "ConferenceRoomService-AD",
            DisplayName: read[0]!.Properties["Name"]?.Value?.ToString() ?? readDn,
            UserPrincipalName: string.Empty,
            SamAccountName: samAccountName,
            PrimarySmtpAddress: listAddress,
            DistinguishedName: readDn,
            ObjectGuid: readGuid,
            EntraObjectId: listExternalId));
    }

    // aud-a2j. The forest's global catalog as "host:3268", discovered from the forest the module
    // credential signs in to - never named or configured (Architectural Invariant 7). Lifted from
    // PowerShellProtectedGroupDirectory.GlobalCatalog, whose fail-closed shape this needs, rather
    // than shared with GroupManagementService.ResolveSearchGlobalCatalog, which caches in a static
    // and falls back to the local domain: a degraded group picker beats a broken one, but a
    // membership write that searched one domain is this finding. AddScript with the projection
    // inside PowerShell, not AddCommand and a property read: GlobalCatalogs read off the returned
    // object comes back empty in a fresh runspace (measured; ADDirectorySearchService.
    // ResolveGlobalCatalog). Not cached - each preflight and add opens its own runspace. Returns
    // the endpoint, or null and the reason.
    private static (string? endpoint, string? error) ResolveForestGlobalCatalog(PowerShell ps, PSCredential credential)
    {
        try
        {
            ps.AddScript("param($c) (Get-ADForest -Credential $c -ErrorAction Stop).GlobalCatalogs | Select-Object -First 1")
              .AddArgument(credential);
            var host = ps.Invoke().FirstOrDefault(o => o is not null)?.ToString();
            if (ps.HadErrors)
                return (null, FirstError(ps) ?? "Get-ADForest reported an error");
            var endpoint = GlobalCatalogEndpoint(host);
            return endpoint is null ? (null, "the forest reported no global catalog") : (endpoint, null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
        finally
        {
            ps.Commands.Clear();
        }
    }

    // "host:3268" for a real host, null for none - never a bare ":3268". A failed host lookup once
    // produced exactly that string, and Get-ADGroup ACCEPTS it and quietly answers from the local
    // domain: the one-domain search this replaces, back without a sound (the incident is recorded at
    // ADDirectorySearchService.ResolveGlobalCatalog). Pure, so the rule is unit-tested.
    internal static string? GlobalCatalogEndpoint(string? host) =>
        string.IsNullOrWhiteSpace(host) ? null : $"{host.Trim()}:3268";

    // aud-a2i. Whether the room is a member of its room list's on-prem group, from the group's own
    // linked member attribute - Get-ADGroup -Properties member, the repo's membership read
    // (Comms10kService.ReadMemberDns, GroupManagementService.GetMembersAsync). Not Get-ADGroupMember:
    // it resolves every member server-side, so Active Directory Web Services refuses a group past
    // MaxGroupOrMemberEntries (5,000 by default) and the read faults on a member in another domain
    // that cannot be chased; the attribute resolves nothing and comes back whole by range retrieval.
    // The group is read by its DN, on the domain that DN names (routed as GetMembersAsync routes
    // it), and must answer as the objectGUID ResolveAdGroupGuid resolved. The room is the object
    // proven by objectGUID, compared by that object's DN - never by a name - so without its DN there
    // is no answer. Shared by the room-list preflight and the add - its check before the write and,
    // since aud-a2l, its read-back after it - so all three read it the same way.
    // Returns (the error, false) when the question cannot be answered, never "not a member".
    // Assumes the ActiveDirectory module is already imported. aud-a2t: it logs nothing - the two
    // checks log a refusal where it refuses the row, and the read-back's outcome is logged with the
    // write's.
    private static (string? error, bool isMember) ReadOnPremListMembership(
        PowerShell ps, string? groupGuid, string? groupDn, string? roomDn, string roomListName, PSCredential credential)
    {
        if (string.IsNullOrWhiteSpace(roomDn))
            return ($"The room's AD object was read without a distinguished name, so whether it is already a member of on-prem room list '{roomListName}' cannot be determined.", false);

        ps.AddCommand("Get-ADGroup")
          .AddParameter("Identity", groupDn)
          .AddParameter("Properties", new[] { "member" })
          .AddParameter("Credential", credential)
          .AddParameter("ErrorAction", "Stop");
        var server = GroupManagementService.ServerFromDn(groupDn);
        if (server is not null)
            ps.AddParameter("Server", server);
        var found = ps.Invoke();
        if (ps.HadErrors)
            return (FirstError(ps) ?? $"Failed to read the membership of on-prem room list '{roomListName}'.", false);
        ps.Commands.Clear();

        var answered = found.Count == 1 ? found[0]?.Properties["ObjectGUID"]?.Value?.ToString() : null;
        if (answered is null || !string.Equals(answered, groupGuid, StringComparison.OrdinalIgnoreCase))
            return ($"Reading the membership of on-prem room list '{roomListName}' did not return its group ({groupGuid}): {found.Count} object(s) answered, ObjectGUID {answered ?? "none"}.", false);

        return (null, GroupManagementService.MemberDnsOf(found[0]).Contains(roomDn, StringComparer.OrdinalIgnoreCase));
    }

    // aud-a2l. Whether an error's TEXT says a write was refused because the value it adds is already
    // there. Since aud-a2n the second signal only: IsValueAlreadyPresentRefusal consults it when the error
    // carries no directory error code, because the text is localised and the code is not. Its message is
    // looked for in every exception the error carries (ExceptionsCarriedBy), since PowerShell may wrap the
    // cmdlet's own ("The running command stopped because ...: <message>"), carry it as the inner
    // exception, or hold it in the error record. The forms, case-insensitive: "already a member" (Win32
    // 1378, "The specified account name is already a member of the group.", the server's answer for a
    // member value already present), "already exists" (Win32 8205/8323, "The specified directory service
    // attribute or value already exists." / "... value already exists.", and ADSI's "The object already
    // exists.") and ENTRY_EXISTS (the LDAP diagnostic, "problem 6005 (ENTRY_EXISTS)"). *Assumption,
    // documented error text, not probed - no live directory call is made here.* A form not matched - a
    // localised message with no code - is told as another write error; the read-back decides the row.
    internal static bool IsDuplicateMemberValue(Exception error) =>
        ExceptionsCarriedBy(error).Any(e => IsDuplicateMemberValue(e.Message));

    internal static bool IsDuplicateMemberValue(string? message) =>
        message is not null && DuplicateMemberValueMarkers.Any(marker => message.Contains(marker, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] DuplicateMemberValueMarkers = ["already a member", "already exists", "ENTRY_EXISTS"];

    // aud-a2n. Whether a write error is Active Directory refusing the add because the value is already
    // there - by the error's code when it carries one, so the refusal is known in any language: LDAP 20
    // (attributeOrValueExists); LDAP 68 (entryAlreadyExists), which a domain controller returns for a member
    // value already present, with the extended error 00000562; that error, Win32 1378
    // (ERROR_MEMBER_IN_ALIAS); 8205 (ERROR_DS_ATTRIBUTE_OR_VALUE_EXISTS); 8305, as the approach names it;
    // and 8323 (ERROR_DS_ATT_VAL_ALREADY_EXISTS). A code outside these wins over a text that says otherwise:
    // only an error with no code at all is judged by its text (IsDuplicateMemberValue). The LDAP and Win32
    // numbers share the property, so an unrelated Win32 20 or 68 would read as a duplicate; a wrong verdict
    // here mislabels the outcome's detail only, since the read-back decides the row. *Assumption, recorded
    // in aud-a2n with the dev probe that would settle it, not probed:* which code - if any - the live
    // module raises; Comms10k measured an add of a member already present as a silent success.
    internal static bool IsValueAlreadyPresentRefusal(Exception error)
    {
        var codes = ExceptionsCarriedBy(error).Select(DirectoryErrorCode).OfType<int>().ToList();
        return codes.Count > 0
            ? codes.Any(ValueAlreadyPresentCodes.Contains)
            : IsDuplicateMemberValue(error);
    }

    private static readonly HashSet<int> ValueAlreadyPresentCodes = [20, 68, 1378, 8205, 8305, 8323];

    private const string DirectoryExceptionTypeName = "Microsoft.ActiveDirectory.Management.ADException";

    // The ErrorCode of a directory exception: the type, or any base type, named
    // Microsoft.ActiveDirectory.Management.ADException - by name, as Comms10kReplaceWriter.IsRetryable
    // matches it, because the assembly ships with RSAT and is referenced nowhere, and read by reflection for
    // the same reason. 0 is no code, and so is a property that cannot be read: reading an error never throws.
    private static int? DirectoryErrorCode(Exception error)
    {
        for (var type = error.GetType(); type is not null; type = type.BaseType)
        {
            if (type.FullName != DirectoryExceptionTypeName)
                continue;
            try
            {
                return error.GetType().GetProperty("ErrorCode")?.GetValue(error) is int code && code != 0 ? code : null;
            }
            catch (Exception)
            {
                return null;
            }
        }
        return null;
    }

    // Every exception an error carries, each once: itself, its inner exceptions, and the exception of each
    // error record PowerShell attached. Under -ErrorAction Stop the cmdlet's own exception - the one with the
    // code - sits in the thrown exception's ErrorRecord, not in its InnerException; a terminating error
    // carries it in both.
    private static IEnumerable<Exception> ExceptionsCarriedBy(Exception error)
    {
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<Exception>();
        pending.Push(error);
        while (pending.Count > 0)
        {
            var e = pending.Pop();
            if (!seen.Add(e))
                continue;
            yield return e;
            if (e.InnerException is { } inner)
                pending.Push(inner);
            if (e is IContainsErrorRecord { ErrorRecord.Exception: { } recorded })
                pending.Push(recorded);
        }
    }

    // aud-a2n. How the detail of a membership the read-back confirmed - not this row's write - opens: after
    // a refusal of a value already there, or after any other write error. The two paths a reader, or a test,
    // can tell apart without parsing the error's own words.
    internal const string ConfirmedAfterRefusal = "Membership confirmed present after Active Directory refused the add as a value already there";

    internal const string ConfirmedAfterWriteError = "Membership confirmed present after Set-ADGroup reported an error";

    // aud-a2q. How the detail of City/State/Country the read-back found in place after Set-ADUser reported an error
    // opens - the attributes' sibling of ConfirmedAfterWriteError, so a reader, or a test, can tell the path without
    // parsing the error's own words.
    internal const string AttributesConfirmedAfterWriteError = "City/State/Country confirmed in place after Set-ADUser reported an error";

    /// <summary>
    /// The audit <c>extra</c> key for a room-list membership the read-back confirmed after the row's own
    /// on-prem write was refused or reported an error (aud-a2n): recorded where
    /// <see cref="ValidatedMailbox.DirectoryAuditKey"/> would have named a written object, which such a row
    /// does not (aud-a2g).
    /// </summary>
    public const string MembershipConfirmedAuditKey = "membershipConfirmedPresent";

    /// <summary>
    /// <paramref name="extra"/> with <paramref name="membershipConfirmed"/> under
    /// <see cref="MembershipConfirmedAuditKey"/>, created when absent; unchanged when there is none. Both Room
    /// Finder audit surfaces - the page and the bulk processor - call it, so they record the row alike.
    /// </summary>
    public static Dictionary<string, object?>? WithMembershipConfirmed(Dictionary<string, object?>? extra, string? membershipConfirmed)
    {
        if (membershipConfirmed is null)
            return extra;
        extra ??= new Dictionary<string, object?>();
        extra[MembershipConfirmedAuditKey] = membershipConfirmed;
        return extra;
    }

    /// <summary>
    /// The audit <c>extra</c> key for City/State/Country the read-back found in place after the row's own Set-ADUser
    /// reported an error (aud-a2q): recorded where <see cref="ValidatedMailbox.DirectoryAuditKey"/> would have named a
    /// written object, which such a row does not (aud-a2g) - the sibling of <see cref="MembershipConfirmedAuditKey"/>.
    /// </summary>
    public const string AttributesConfirmedAuditKey = "attributesConfirmedPresent";

    /// <summary>
    /// <paramref name="extra"/> with <paramref name="attributesConfirmed"/> under <see cref="AttributesConfirmedAuditKey"/>,
    /// created when absent; unchanged when there is none. Both Room Finder audit surfaces call it beside
    /// <see cref="WithMembershipConfirmed"/>, so they record the row alike.
    /// </summary>
    public static Dictionary<string, object?>? WithAttributesConfirmed(Dictionary<string, object?>? extra, string? attributesConfirmed)
    {
        if (attributesConfirmed is null)
            return extra;
        extra ??= new Dictionary<string, object?>();
        extra[AttributesConfirmedAuditKey] = attributesConfirmed;
        return extra;
    }

    /// <summary>
    /// kd-7. The step the page's ticket refusals record as <c>refusedAt</c> (<see cref="WithRefusal"/>).
    /// </summary>
    public const string TicketRefusalStep = "TicketValidation";

    /// <summary>
    /// kd-7. <paramref name="extra"/> with the aud-a28 refusal fields - <c>refusedAt</c> = <paramref name="refusedAt"/> and
    /// <c>mutationAttempted</c> = false - created when absent; unchanged when <paramref name="refusedAt"/> is null. The page's
    /// ticket refusals pass it, so their rows say that nothing was sent to Exchange or the directory, which is what tells a
    /// refusal apart from a failed write.
    /// </summary>
    public static Dictionary<string, object?>? WithRefusal(Dictionary<string, object?>? extra, string? refusedAt)
    {
        if (refusedAt is null)
            return extra;
        extra ??= new Dictionary<string, object?>();
        extra["refusedAt"] = refusedAt;
        extra["mutationAttempted"] = false;
        return extra;
    }

    /// <summary>
    /// kd-7. The refusal a blank ticket gets on the Conference Rooms page, or null when one was given; the page asks
    /// ServiceNow only about a ticket that is present.
    /// </summary>
    /// <remarks>
    /// <see cref="ServiceNowService.ValidateTicketAsync"/> passes every ticket - a blank one included - while the integration is
    /// dormant, so the page's ServiceNow check alone admitted a blank ticket on a deployment without it and the buttons'
    /// emptiness clauses were the only enforcement. The message is the one that service gives a blank ticket when it is enabled,
    /// so the operator reads the same refusal either way. It lives here rather than on the page because the page is line-keyed
    /// by ClickGateRegistry and the fix had to leave its line count alone.
    /// </remarks>
    public static TicketValidationResult? RequireTicket(string? ticket) =>
        string.IsNullOrWhiteSpace(ticket)
            ? new TicketValidationResult { IsValid = false, Message = "Ticket number is required" }
            : null;

    /// <summary>
    /// aud-a2r. The admin notification's row carrying <see cref="RoomOperationResult.DirectoryAttributesConfirmed"/>
    /// (<see cref="NotificationDetails"/>).
    /// </summary>
    public const string AttributesConfirmedNotificationRow = "City/State/Country";

    /// <summary>
    /// aud-a2r. The admin notification's row carrying <see cref="RoomOperationResult.DirectoryMembershipConfirmed"/>
    /// (<see cref="NotificationDetails"/>).
    /// </summary>
    public const string MembershipConfirmedNotificationRow = "Room list membership";

    /// <summary>
    /// aud-a2r. The detail rows of a Room Finder apply's admin notification: the room, then each directory write the
    /// read-back confirmed after the write reported an error, on a row of its own.
    /// </summary>
    /// <remarks>
    /// The page hands the notification a failure's message as its error and a success's as nothing, so a row the
    /// read-back settled after Set-ADUser or Set-ADGroup reported an error reached the administrators as a plain success,
    /// though the page, the result and the audit all said otherwise. The rows carry the text the audit records under
    /// <see cref="AttributesConfirmedAuditKey"/> and <see cref="MembershipConfirmedAuditKey"/>, by the audit's rule:
    /// whenever the result carries the detail, whatever the outcome - a partial whose City/State/Country were confirmed
    /// before a later step failed carries it beside its error. Never in the error field, which a success does not
    /// fill. A clean apply's rows are the room alone, as before. Pure, for testing.
    /// </remarks>
    public static Dictionary<string, string> NotificationDetails(string room, string? attributesConfirmed, string? membershipConfirmed)
    {
        var details = new Dictionary<string, string> { ["Room"] = room };
        if (attributesConfirmed is not null)
            details[AttributesConfirmedNotificationRow] = attributesConfirmed;
        if (membershipConfirmed is not null)
            details[MembershipConfirmedNotificationRow] = membershipConfirmed;
        return details;
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
