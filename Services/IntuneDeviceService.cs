using System.Net;
using System.Text.Json;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Read-only path for Microsoft Graph Intune managed devices (docs/IntuneDeviceManagement-Plan.md
/// S1). Graph v1.0 application permission ...ManagedDevices.Read.All. Delete/retire/wipe and the
/// Entra ID removal are later slices, layered on GraphTokenClient's S0 status-returning helpers.
/// </summary>
public sealed class IntuneDeviceService
{
    private readonly ModuleConfigService? _moduleConfig;
    private readonly DelineaService? _delineaService;
    private readonly IHttpClientFactory? _httpClientFactory;
    private readonly Func<Task<GraphTokenClient?>> _graphClientFactory;

    public IntuneDeviceService(ModuleConfigService moduleConfig, DelineaService delineaService, IHttpClientFactory httpClientFactory)
    {
        _moduleConfig = moduleConfig;
        _delineaService = delineaService;
        _httpClientFactory = httpClientFactory;
        _graphClientFactory = GetGraphClientAsync;
    }

    /// <summary>
    /// Test seam: drives SearchDevicesAsync/GetDeviceAsync's query construction, truncation and
    /// parsing against a canned GraphTokenClient without exercising ModuleConfigService or
    /// DelineaService (which would otherwise need a live Secret Server call). Mirrors
    /// RiskyUsersService's seam (RiskyUsersService.cs:36-39) for the same reason: the test cannot
    /// reach GraphTokenClientTests.StubHandler, which is private sealed to that class. Does not
    /// change the public DI constructor above or its Program.cs registration.
    /// </summary>
    internal IntuneDeviceService(Func<Task<GraphTokenClient?>> graphClientFactory)
    {
        _graphClientFactory = graphClientFactory;
    }

    /// <summary>
    /// Copied in shape from MfaResetService.cs:20-37: reads this module's own
    /// GraphDelineaSecretId, never another module's config.
    /// </summary>
    private async Task<GraphTokenClient?> GetGraphClientAsync()
    {
        if (_moduleConfig == null || _delineaService == null || _httpClientFactory == null)
            return null;

        var secretIdStr = _moduleConfig.GetValue("IntuneDevices", "GraphDelineaSecretId");
        if (!int.TryParse(secretIdStr, out var secretId) || secretId <= 0)
            return null;

        var fields = await _delineaService.GetSecretFieldsAsync(secretId);
        var credentials = ExtractGraphCredentials(fields);
        if (credentials == null)
            return null;

        return new GraphTokenClient(credentials.Value.TenantId, credentials.Value.ClientId, credentials.Value.ClientSecret,
            _httpClientFactory.CreateClient("MicrosoftGraph"));
    }

    /// <summary>
    /// The three fields this module's own Delinea secret must carry, or null when the secret was
    /// unreadable or is short of any one of them - a secret missing "Client Secret" yields no client
    /// at all rather than a GraphTokenClient built over an empty credential (AC14).
    /// </summary>
    /// <remarks>
    /// Extracted from GetGraphClientAsync as an internal seam because the surrounding path is not
    /// exercisable in a test: DelineaService.GetSecretFieldsAsync returns null before it issues any
    /// HTTP request when the Secret Server bootstrap credential is absent from Windows Credential
    /// Manager, so a "secret present but missing Client Secret" test driven through the real service
    /// would pass for the wrong reason. This is the field-level half of that case, tested directly
    /// (plan Test plan, GetGraphClientAsync).
    /// </remarks>
    internal static (string TenantId, string ClientId, string ClientSecret)? ExtractGraphCredentials(
        IReadOnlyDictionary<string, string>? fields)
    {
        if (fields == null)
            return null;

        var tenantId = fields.GetValueOrDefault("Tenant ID") ?? "";
        var clientId = fields.GetValueOrDefault("Application ID") ?? "";
        var clientSecret = fields.GetValueOrDefault("Client Secret") ?? "";

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            return null;

        return (tenantId, clientId, clientSecret);
    }

    public bool IsAvailable
    {
        get
        {
            var secretIdStr = _moduleConfig?.GetValue("IntuneDevices", "GraphDelineaSecretId");
            return int.TryParse(secretIdStr, out var id) && id > 0;
        }
    }

    private const string DevicesEndpoint = "/deviceManagement/managedDevices";

    // Deliberately excludes activationLockBypassCode (T4b) - the second, request-boundary half
    // of the exclusion. IntuneDevice also has no property to hold it (the first half), so a
    // $select regression here does not, on its own, put the secret on the page.
    // internal so the URL-builder tests can assert a whole composed URL by equality without
    // restating this list; the exclusion itself has its own guard test.
    internal const string SelectFields =
        "id,deviceName,managedDeviceName,userPrincipalName,userDisplayName,userId,operatingSystem," +
        "osVersion,manufacturer,model,serialNumber,imei,meid,wiFiMacAddress,ethernetMacAddress," +
        "enrolledDateTime,lastSyncDateTime,complianceState,managementAgent,managedDeviceOwnerType," +
        "deviceEnrollmentType,deviceRegistrationState,isEncrypted,isSupervised,jailBroken," +
        "azureADDeviceId,azureADRegistered,totalStorageSpaceInBytes,freeStorageSpaceInBytes,notes";

    /// <summary>
    /// Bounded search: ONE REQUEST PER FIELD, issued concurrently - a prefix match on deviceName,
    /// a prefix match on userPrincipalName, an exact match on serialNumber - merged by device id
    /// (plan T2 Revision 2026-09-03).
    /// </summary>
    /// <remarks>
    /// A single combined `or` filter across those three properties answers 200 with an EMPTY
    /// value array on this tenant - for `startswith` and for `eq` alike, and for a device the same
    /// endpoint returns in an unfiltered page. The endpoint evaluates the combined expression to
    /// nothing instead of rejecting it, so there is no 400 for T7 to surface; three
    /// single-property filters are each an expression it does honour.
    ///
    /// A failed search still never reads as "no matches": any one of the three requests failing
    /// throws (T7), naming which field's request failed, rather than contributing an empty page to
    /// the merge. Rows Graph returns that do not actually match the term are dropped and counted
    /// (FilterIgnoredCount), so the mirror-image failure - a honoured request whose filter was
    /// ignored - cannot render as matches either.
    /// </remarks>
    public async Task<IntuneDeviceSearchResult> SearchDevicesAsync(string? searchTerm)
    {
        var client = await _graphClientFactory() ?? throw new InvalidOperationException("Intune Devices Graph credentials not available.");

        var top = ClampSearchResultLimit(_moduleConfig?.GetValue("IntuneDevices", "SearchResultLimit"));

        // A blank term stays ONE unfiltered request, as before: there is no field to split it
        // across, and nothing to verify a returned row against.
        if (string.IsNullOrWhiteSpace(searchTerm))
        {
            var (doc, status) = await client.GetWithStatusAsync(BuildUnfilteredSearchUrl(top));

            // A failed request must never render as "no devices found" - an operator searching a
            // serial number who sees nothing must not conclude the device is unenrolled when the
            // real cause is a missing permission or a throttled tenant (T7).
            if (doc == null)
                throw BuildFailure(status, "Intune devices");

            using var unfilteredDoc = doc;
            var page = ReadDevicePage(unfilteredDoc);
            return new IntuneDeviceSearchResult(page.Devices, page.Truncated, page.Devices.Count, 0);
        }

        var requests = BuildSearchRequests(searchTerm, top);

        // Awaited as one Task.WhenAll rather than in sequence: three round trips at once, and
        // WhenAll observes every child exception, so a socket failure on the second request cannot
        // become an unobserved task exception while the first one's throw unwinds.
        var responses = await Task.WhenAll(requests.Select(request => client.GetWithStatusAsync(request.Url)));

        try
        {
            for (var i = 0; i < responses.Length; i++)
            {
                // T7, per field: a 403 on the serial-number request is a FAILED search, not a
                // search whose serial half happened to match nothing. There is no partial result.
                if (responses[i].Document == null)
                    throw BuildFailure(responses[i].StatusCode, requests[i].FailureContext);
            }

            var pages = responses.Select(response => ReadDevicePage(response.Document!)).ToArray();

            return MergeSearchPages(
                searchTerm,
                pages.SelectMany(page => page.Devices),
                pages.Any(page => page.Truncated));
        }
        finally
        {
            foreach (var response in responses)
                response.Document?.Dispose();
        }
    }

    /// <summary>
    /// One Graph page: the devices it carried, and whether it was cut short. @odata.nextLink is
    /// absolute and GraphTokenClient cannot follow it (T1) - its presence must still be surfaced so
    /// a capped list never looks like a complete one.
    /// </summary>
    private static (List<IntuneDevice> Devices, bool Truncated) ReadDevicePage(JsonDocument document)
    {
        var truncated = document.RootElement.TryGetProperty("@odata.nextLink", out _);

        var devices = new List<IntuneDevice>();
        foreach (var item in document.RootElement.GetProperty("value").EnumerateArray())
            devices.Add(ParseDevice(item));

        return (devices, truncated);
    }

    /// <summary>
    /// Merges the per-field pages into one result: the first occurrence of a device id wins, the
    /// order is by device name so the same search does not reshuffle between runs, Truncated is
    /// true when ANY page carried @odata.nextLink, and SearchedCount is the distinct merged count -
    /// including rows that are then dropped for not matching, because that count is what "no match
    /// in the first N devices" means on the page.
    /// </summary>
    internal static IntuneDeviceSearchResult MergeSearchPages(
        string searchTerm, IEnumerable<IntuneDevice> devicesInRequestOrder, bool truncated)
    {
        var merged = new Dictionary<string, IntuneDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devicesInRequestOrder)
            merged.TryAdd(device.Id, device);

        var matching = new List<IntuneDevice>();
        var ignored = 0;
        foreach (var device in merged.Values)
        {
            if (MatchesSearchTerm(searchTerm, device))
                matching.Add(device);
            else
                ignored++;
        }

        matching.Sort(static (left, right) =>
        {
            var byName = string.Compare(left.DeviceName, right.DeviceName, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
        });

        return new IntuneDeviceSearchResult(matching, truncated, merged.Count, ignored);
    }

    /// <summary>
    /// Whether a device Graph returned actually matches what was typed - device name or user
    /// principal name starting with the term, or a serial equal to it, all case-insensitively: the
    /// same three comparisons the three requests ask Graph for.
    /// </summary>
    /// <remarks>
    /// Deliberately redundant with the server-side filter. The 2026-09-03 finding is that this
    /// endpoint can evaluate a filter it dislikes to nothing rather than rejecting it; the
    /// mirror-image failure - honouring the request but ignoring the filter and answering with an
    /// arbitrary page - would put unrelated devices on screen as "matches", which is the worse
    /// direction on a page whose next button wipes a laptop. A dropped row is counted and stated,
    /// never silently discarded: a filter that failed must not read as a benign result.
    /// </remarks>
    internal static bool MatchesSearchTerm(string searchTerm, IntuneDevice device) =>
        device.DeviceName.StartsWith(searchTerm, StringComparison.OrdinalIgnoreCase)
        || device.UserPrincipalName.StartsWith(searchTerm, StringComparison.OrdinalIgnoreCase)
        || string.Equals(device.SerialNumber, searchTerm, StringComparison.OrdinalIgnoreCase);

    public async Task<IntuneDevice> GetDeviceAsync(string deviceId)
    {
        var client = await _graphClientFactory() ?? throw new InvalidOperationException("Intune Devices Graph credentials not available.");

        var (doc, status) = await client.GetWithStatusAsync($"{DevicesEndpoint}/{Uri.EscapeDataString(deviceId)}?$select={SelectFields}");

        if (doc == null)
            throw BuildFailure(status, "Intune device detail");

        using var responseDoc = doc;
        return ParseDevice(responseDoc.RootElement);
    }

    // ---- write path (S3) ----------------------------------------------------------------------

    private const string ReadWritePermission = "DeviceManagementManagedDevices.ReadWrite.All";

    /// <summary>
    /// Deletes one device's Intune management record (S3), over S0's status-returning DELETE so a
    /// 403, a 404 and a 5xx are three distinct reported outcomes rather than one bare "failed"
    /// (T7 / AC15). Removes the Intune record ONLY: company data stays on the device and the Entra
    /// ID device object survives (T3 / D3), which the success message states in words.
    /// </summary>
    public async Task<IntuneDeviceActionResult> DeleteDeviceAsync(string deviceId)
    {
        var client = await _graphClientFactory() ?? throw new InvalidOperationException("Intune Devices Graph credentials not available.");

        var (ok, status, safeError) = await client.DeleteWithStatusAsync($"{DevicesEndpoint}/{Uri.EscapeDataString(deviceId)}");

        return ok
            ? new IntuneDeviceActionResult(true, DeleteSuccessMessage, null)
            : BuildActionFailure("delete of the Intune record", ReadWritePermission, status, safeError);
    }

    /// <summary>
    /// T3 / AC11: delete is immediate on the Intune record, and that record is all it removes. The
    /// operator must see the two things that survive, or they will believe the device is gone from
    /// the tenant.
    /// </summary>
    internal const string DeleteSuccessMessage =
        "Deleted the Intune record. This removes the Intune record only - company data stays on the device, "
        + "and the device's Entra ID object still exists. Both are separate actions.";

    /// <summary>
    /// The delete result when the Entra ID removal ran alongside it and SUCCEEDED (S5, AC11's
    /// conditional half). The standing claim that the Entra ID object survives is false on that
    /// path, and a result that keeps making it would be telling the operator the opposite of what
    /// happened. Company data still stays on the device, which no action here changes.
    /// </summary>
    internal const string DeleteSuccessMessageEntraRemoved =
        "Deleted the Intune record, and the device's Entra ID object was removed as well - "
        + "company data still stays on the device.";

    // ---- write path (S4) ----------------------------------------------------------------------

    private const string PrivilegedPermission = "DeviceManagementManagedDevices.PrivilegedOperations.All";

    /// <summary>
    /// Retires one device - removes company data, leaving personal data (S4). Behind
    /// IntuneDevicesPrivileged, over S0's status-returning POST, with the same distinct-outcome
    /// reporting as delete.
    /// </summary>
    /// <remarks>
    /// Sends NO BODY, ever. Learn is explicit ("Do not supply a request body for this method"), so
    /// the body argument is omitted rather than passed as an empty object - and a test asserts the
    /// request carried no content at all, so a shared helper cannot quietly give retire one.
    /// </remarks>
    public async Task<IntuneDeviceActionResult> RetireDeviceAsync(string deviceId)
    {
        var client = await _graphClientFactory() ?? throw new InvalidOperationException("Intune Devices Graph credentials not available.");

        var (ok, status, safeError) = await client.PostNoContentWithStatusAsync(
            $"{DevicesEndpoint}/{Uri.EscapeDataString(deviceId)}/retire");

        return ok
            ? new IntuneDeviceActionResult(true, RetireQueuedMessage, null)
            : BuildActionFailure("retire", PrivilegedPermission, status, safeError);
    }

    /// <summary>
    /// Wipes one device - factory reset, with the flag set the operator chose (S4). Behind
    /// IntuneDevicesPrivileged, over S0's status-returning POST, with the same distinct-outcome
    /// reporting as delete. The body is ALWAYS explicit; see BuildWipeBody.
    /// </summary>
    public async Task<IntuneDeviceActionResult> WipeDeviceAsync(string deviceId, IntuneWipeOptions options)
    {
        var client = await _graphClientFactory() ?? throw new InvalidOperationException("Intune Devices Graph credentials not available.");

        var (ok, status, safeError) = await client.PostNoContentWithStatusAsync(
            $"{DevicesEndpoint}/{Uri.EscapeDataString(deviceId)}/wipe", BuildWipeBody(options));

        return ok
            ? new IntuneDeviceActionResult(true, WipeQueuedMessage, null)
            : BuildActionFailure("wipe", PrivilegedPermission, status, safeError);
    }

    /// <summary>
    /// T3 / AC11: retire and wipe are ASYNCHRONOUS. A 204 is Intune accepting the request, not the
    /// device having acted - the wording says "queued" and names the check-in, because "Retired" over
    /// a powered-off machine is the D8 defect from docs/MigrationBatchSelection-Plan.md.
    /// </summary>
    internal const string RetireQueuedMessage =
        "Queued retire of this device. Intune accepted the request; the device removes company data at its next "
        + "check-in, and a powered-off or offline device may not act for a long time.";

    /// <summary>T3 / AC11, for wipe. See RetireQueuedMessage.</summary>
    internal const string WipeQueuedMessage =
        "Queued wipe of this device. Intune accepted the request; the device factory resets at its next check-in, "
        + "and a powered-off or offline device may not act for a long time.";

    /// <summary>
    /// The wipe request body, always explicit and never left to a Graph default.
    /// </summary>
    /// <remarks>
    /// keepUserData and keepEnrollmentData are ALWAYS serialized at their chosen values - the
    /// surviving half of idm-2, because "no body means a full factory reset" was an inference stated
    /// as fact and withdrawn. The other three are included only when the operator set them: sending
    /// macOsUnlockCode: "" or an obliteration behaviour to a Windows device is meaningless, and
    /// present-and-null is not the same wire request as absent. The intent is pinned by the tests
    /// asserting this serialized body per combination, not by the plan's table.
    /// </remarks>
    internal static Dictionary<string, object?> BuildWipeBody(IntuneWipeOptions options)
    {
        var body = new Dictionary<string, object?>
        {
            ["keepUserData"] = options.KeepUserData,
            ["keepEnrollmentData"] = options.KeepEnrollmentData
        };

        if (!string.IsNullOrWhiteSpace(options.MacOsUnlockCode))
            body["macOsUnlockCode"] = options.MacOsUnlockCode.Trim();

        if (!string.IsNullOrWhiteSpace(options.ObliterationBehavior))
            body["obliterationBehavior"] = options.ObliterationBehavior;

        if (options.PersistEsimDataPlan)
            body["persistEsimDataPlan"] = true;

        return body;
    }

    // ---- write path (S5): the Entra ID device object -------------------------------------------

    /// <summary>
    /// The consent this half needs, and the widest grant in the module (D3): Device.ReadWrite.All is
    /// a DIRECTORY scope, conferring write access over every device object in the tenant, including
    /// devices this module would never show because they are not Intune-managed. Named in the 403
    /// message so a missing consent is diagnosable rather than reading as "it failed".
    /// </summary>
    internal const string EntraDeletePermission = "Device.ReadWrite.All";

    /// <summary>
    /// The Entra ID device object's URL, addressed by the ALTERNATE KEY and never by path id.
    /// </summary>
    /// <remarks>
    /// This is the trap in S5, and it fails SILENTLY if you get it wrong. managedDevice's
    /// azureADDeviceId holds the Entra deviceId, while DELETE /devices/{id} expects the directory
    /// OBJECT id - two different GUIDs on the same device (Learn's `device: get` example returns
    /// id: 000005c3-... alongside deviceId: 6fa60d52-...). Passing the former into the latter's form
    /// yields a 404 against a real, still-present device, which reads to an operator as "already
    /// gone". Both forms are documented in Graph v1.0; only the alternate key takes what this module
    /// actually has. A test asserts this URL, so building the path form must fail it (AC22).
    ///
    /// The value is escaped as an OData literal (T4c) even though IsUsableEntraDeviceId has already
    /// established it is a GUID - the same two-barrier habit as the activationLockBypassCode
    /// exclusion, so neither guard alone is load-bearing.
    /// </remarks>
    internal static string EntraDeviceEndpoint(string azureAdDeviceId) =>
        $"/devices(deviceId='{EscapeODataLiteral(azureAdDeviceId.Trim())}')";

    /// <summary>
    /// Whether a managedDevice's azureADDeviceId can address an Entra ID device object at all. A
    /// device that is not Entra-joined carries it absent, empty, or as the all-zero GUID (S5), and
    /// the option is then not OFFERED with the reason stated, never offered-and-silently-skipped
    /// (AC24). A value that is not a GUID is refused for the same fail-closed reason.
    /// </summary>
    internal static bool IsUsableEntraDeviceId(string? azureAdDeviceId) =>
        Guid.TryParse((azureAdDeviceId ?? "").Trim(), out var parsed) && parsed != Guid.Empty;

    /// <summary>
    /// Removes one device's Entra ID device object (S5 / D3), behind IntuneDevicesEntraDelete and
    /// over S0's status-returning DELETE, with the same distinct-outcome reporting as the Intune
    /// actions. No Intune action removes this object - Microsoft's own guidance is to remove it as a
    /// separate step, which is why it is a separate result here and never folded into an Intune
    /// action's verdict (Known Failure Class 2).
    /// </summary>
    /// <remarks>
    /// Takes the azureADDeviceId the CALLER captured from the device detail before running any
    /// Intune action: after a successful delete of the Intune record there is nothing left to read
    /// it from. An unusable id is refused before any request is issued.
    /// </remarks>
    public async Task<IntuneDeviceActionResult> RemoveEntraDeviceAsync(string? azureAdDeviceId)
    {
        if (!IsUsableEntraDeviceId(azureAdDeviceId))
            return new IntuneDeviceActionResult(false, NoEntraDeviceIdMessage, null);

        var client = await _graphClientFactory() ?? throw new InvalidOperationException("Intune Devices Graph credentials not available.");

        var (ok, status, safeError) = await client.DeleteWithStatusAsync(EntraDeviceEndpoint(azureAdDeviceId!));

        return ok
            ? new IntuneDeviceActionResult(true, EntraDeleteSuccessMessage, null)
            : BuildActionFailure("removal of the Entra ID device object", EntraDeletePermission, status, safeError,
                notFoundMessage:
                "Graph reports no Entra ID device object with this device id (404 Not Found), so nothing was removed. "
                + "Any Intune action performed alongside it is unaffected.");
    }

    /// <summary>
    /// Fixed starting state for the "also remove the Entra ID device object" checkbox: unticked.
    /// </summary>
    /// <remarks>
    /// Not a module-config field (owner ruling 2026-09-02, .agents/decisions.md): whether to also
    /// remove the directory object is the acting operator's call at that moment, not a
    /// deployment-wide default. It starts off because it is a second, separately permissioned
    /// deletion against a different object, so it is opted into rather than inherited.
    /// </remarks>
    internal const bool EntraRemovalStartsTicked = false;

    /// <summary>Refusal when the device carries no usable azureADDeviceId - stated, never silent.</summary>
    internal const string NoEntraDeviceIdMessage =
        "This device has no usable Entra ID device id, so it has no Entra ID device object to remove. "
        + "Nothing was attempted.";

    /// <summary>
    /// Unlike retire and wipe this one is immediate rather than queued: the directory object is gone
    /// when Graph answers. It also says what it does NOT do, because removing the directory object
    /// neither removes the Intune record nor touches data on the device.
    /// </summary>
    internal const string EntraDeleteSuccessMessage =
        "Removed the device's Entra ID device object. This affects how the device authenticates, and is the "
        + "step conditional access and compliance reporting notice. Company data on the device is unaffected.";

    // ---- affected-user notification (S6 / D2) -------------------------------------------------

    /// <summary>
    /// Fixed starting state for each action's "email the device's primary user" checkbox. null means
    /// the action offers no notification at all.
    /// </summary>
    /// <remarks>
    /// Not module-config fields (owner ruling 2026-09-02, .agents/decisions.md, superseding D2's
    /// config half): the operator running the action decides whether the user hears about it, at
    /// that moment. These states only seed the box, which is changeable before confirming - the
    /// lost-or-stolen case is exactly why the choice cannot live anywhere else.
    ///
    /// D2's reasoning for the states themselves stands: delete off (nothing changes on the user's
    /// device, so a mail would confuse), retire and wipe on.
    ///
    /// EntraDelete offers NO notification, hence null rather than false: removing a directory object
    /// changes nothing the user can observe on their device - unlike a retire or a wipe. A null here
    /// is what makes that absence deliberate rather than forgotten.
    /// </remarks>
    internal static bool? NotifyUserStartsTicked(IntuneDeviceAction action) => action switch
    {
        IntuneDeviceAction.Delete => false,
        IntuneDeviceAction.Retire => true,
        IntuneDeviceAction.Wipe => true,
        IntuneDeviceAction.EntraDelete => null,
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Unknown Intune device action.")
    };

    /// <summary>
    /// Whether to email the device's primary user, and why not when the answer is no (S6 / D2 /
    /// AC18-AC20). Pure, so every branch is testable without a mail server.
    /// </summary>
    /// <remarks>
    /// The app-wide gate is evaluated BEFORE the address, deliberately: on a deployment with
    /// affected-user mail switched off, "nothing is sent here at all" is the operator's actionable
    /// answer, and it is true of every device rather than of this one. userNotificationsEnabled
    /// comes from EmailService.UserNotificationsEnabled - the same field that gates the send - so
    /// the on-screen statement and the send cannot disagree.
    /// </remarks>
    internal static IntuneUserNotificationDecision DecideUserNotification(
        bool configuredDefault, bool operatorRequested, bool userNotificationsEnabled, string? primaryUserAddress)
    {
        if (!operatorRequested)
        {
            return configuredDefault
                ? new(IntuneUserNotificationOutcome.NotRequestedByOperator,
                    "No email was sent to the device's primary user: this module emails the user for this action by "
                    + "default, and the operator cleared that choice before confirming.")
                : new(IntuneUserNotificationOutcome.NotRequestedByDefault,
                    "No email was sent to the device's primary user: this module's configuration does not email the "
                    + "user for this action, and the operator did not ask for one.");
        }

        if (!userNotificationsEnabled)
        {
            return new(IntuneUserNotificationOutcome.SuppressedAppWide,
                "No email was sent to the device's primary user: user notifications are disabled for this whole "
                + "deployment (Email:NotifyUsersOnPermissionGrant), which overrides this module's setting. "
                + "An administrator must enable them before this option can do anything.");
        }

        if (string.IsNullOrWhiteSpace(primaryUserAddress) || !primaryUserAddress.Contains('@'))
        {
            return new(IntuneUserNotificationOutcome.NoAddress,
                "No email was sent: this device has no primary user address to send to.");
        }

        return new(IntuneUserNotificationOutcome.Send, $"Emailing the device's primary user at {primaryUserAddress.Trim()}.");
    }

    /// <summary>
    /// Maps a failed device mutation onto one of four distinct outcomes (T7 / AC15): a 403 names the
    /// consent the app registration is missing, a 404 says the device is already gone from Intune and
    /// nothing was done, a 5xx says Intune was unavailable and the action can be retried, and
    /// anything else reports its own status. The sanitized Graph error travels in both the message
    /// and SafeError. A bare "failed" would hide a misconfigured app registration, which is the one
    /// failure this module's permission split exists to expose.
    ///
    /// notFoundMessage overrides the 404 wording, because the Entra half's 404 is about a DIRECTORY
    /// object and saying "no longer in Intune" there would name the wrong object - and a 404 is
    /// exactly the symptom of addressing it by the wrong key (see EntraDeviceEndpoint).
    /// </summary>
    internal static IntuneDeviceActionResult BuildActionFailure(
        string actionDescription, string requiredPermission, HttpStatusCode status, string? safeError,
        string? notFoundMessage = null)
    {
        var detail = string.IsNullOrWhiteSpace(safeError) ? "" : $" Graph error: {safeError}";

        var message = status switch
        {
            HttpStatusCode.Forbidden =>
                $"Graph refused the {actionDescription} (403 Forbidden) - verify the app registration's {requiredPermission} consent.{detail}",
            HttpStatusCode.NotFound =>
                (notFoundMessage
                 ?? $"Graph reports this device is no longer in Intune (404 Not Found), so no {actionDescription} was performed.")
                + detail,
            _ when (int)status >= 500 =>
                $"Intune was unavailable for the {actionDescription} ({(int)status} {status}) - nothing was performed; retry.{detail}",
            _ => $"Graph rejected the {actionDescription} ({(int)status} {status}).{detail}"
        };

        return new IntuneDeviceActionResult(false, message, safeError);
    }

    private static InvalidOperationException BuildFailure(HttpStatusCode status, string context)
    {
        if (status == HttpStatusCode.Forbidden)
            return new InvalidOperationException(
                "Intune Devices is not available for this tenant - verify the app registration's DeviceManagementManagedDevices.Read.All consent.");

        if (status == HttpStatusCode.NotFound)
            return new InvalidOperationException($"{context} not found.");

        // 400 is the status a rejected $filter comes back as, and the search now leans on
        // startswith (T2 Revision 2026-09-02). The generic message below names only the number,
        // which reads as "something broke" - this one tells the operator the search FAILED and
        // was not an empty result, which is the whole point of T7. GraphTokenClient's GET helper
        // discards the response body on a non-success status, so Graph's own error text is not
        // available to quote here.
        if (status == HttpStatusCode.BadRequest)
            return new InvalidOperationException(
                $"Graph rejected the request for {context}: 400 Bad Request. On a search this means Graph would not "
                + "accept the search filter, so this is a failed search and not a device that does not exist - "
                + "report the exact term searched.");

        return new InvalidOperationException($"Graph request for {context} failed: {(int)status} {status}.");
    }

    /// <summary>
    /// $top clamp for the device search. Graph caps $top at 500 on this resource; an unparseable
    /// or non-positive SearchResultLimit config value falls back to the module's documented
    /// default of 50 rather than a silently unbounded or zero-row request.
    /// </summary>
    internal static int ClampSearchResultLimit(string? rawLimit)
    {
        if (!int.TryParse(rawLimit, out var parsed) || parsed <= 0)
            parsed = 50;

        return Math.Clamp(parsed, 1, 500);
    }

    /// <summary>
    /// The three per-field requests one search issues, in the order their results are merged, each
    /// paired with the wording its own failure is reported under (T7) - so "the serial-number
    /// request was throttled" is distinguishable from "the device-name request was refused".
    /// </summary>
    internal static (string Url, string FailureContext)[] BuildSearchRequests(string searchTerm, int top) =>
    [
        (BuildDeviceNameSearchUrl(searchTerm, top), "Intune devices (device name search)"),
        (BuildUserPrincipalNameSearchUrl(searchTerm, top), "Intune devices (user principal name search)"),
        (BuildSerialNumberSearchUrl(searchTerm, top), "Intune devices (serial number search)")
    ];

    /// <summary>The unfiltered first page, for a blank search term.</summary>
    internal static string BuildUnfilteredSearchUrl(int top) => $"{DevicesEndpoint}?{SearchQuery(top)}";

    /// <summary>
    /// Prefix match on deviceName. An exact-match-only search box is not a search box, and dev
    /// proved on 2026-09-02 that a full value typed against the previous `eq` form returned
    /// nothing usable (plan T2, Revision 2026-09-02). `contains` is deliberately NOT used:
    /// managedDevices rejects it with a 400.
    /// </summary>
    internal static string BuildDeviceNameSearchUrl(string searchTerm, int top) =>
        $"{DevicesEndpoint}?{SearchQuery(top)}&$filter=startswith(deviceName,{ODataStringLiteral(searchTerm)})";

    /// <summary>Prefix match on userPrincipalName. See BuildDeviceNameSearchUrl.</summary>
    internal static string BuildUserPrincipalNameSearchUrl(string searchTerm, int top) =>
        $"{DevicesEndpoint}?{SearchQuery(top)}&$filter=startswith(userPrincipalName,{ODataStringLiteral(searchTerm)})";

    /// <summary>
    /// Exact match on serialNumber - a serial is copied whole, never typed as a prefix. The spaces
    /// around `eq` are emitted as %20 rather than left literal, so the string this builder returns
    /// is the string that goes on the wire: System.Uri would escape a literal space anyway, and a
    /// builder whose output differs from the request is one a test cannot pin.
    /// </summary>
    internal static string BuildSerialNumberSearchUrl(string searchTerm, int top) =>
        $"{DevicesEndpoint}?{SearchQuery(top)}&$filter=serialNumber%20eq%20{ODataStringLiteral(searchTerm)}";

    private static string SearchQuery(int top) => $"$top={top}&$select={SelectFields}";

    /// <summary>
    /// One OData string literal: the quotes delimiting it are SYNTAX and stay literal, and only the
    /// typed value is percent-escaped - after doubling any single quote it contains, per the OData
    /// literal-escaping shape (M365GroupManagementService.cs:74, T4c).
    /// </summary>
    /// <remarks>
    /// Narrowed on 2026-09-03. The previous form ran Uri.EscapeDataString over the WHOLE filter
    /// expression, so `(`, `)`, `,` and the delimiting `'` reached Graph percent-encoded. That is
    /// legal in a URL and Graph answered 200 - with an empty value array every time. The escaping
    /// is now the value's alone, which is the shape Graph's own clients put on the wire.
    /// </remarks>
    private static string ODataStringLiteral(string value) =>
        $"'{Uri.EscapeDataString(EscapeODataLiteral(value))}'";

    private static string EscapeODataLiteral(string value) => value.Replace("'", "''");

    private static IntuneDevice ParseDevice(JsonElement item) => new()
    {
        Id = GetString(item, "id"),
        DeviceName = GetString(item, "deviceName"),
        ManagedDeviceName = GetString(item, "managedDeviceName"),
        UserPrincipalName = GetString(item, "userPrincipalName"),
        UserDisplayName = GetString(item, "userDisplayName"),
        UserId = GetString(item, "userId"),
        OperatingSystem = GetString(item, "operatingSystem"),
        OsVersion = GetString(item, "osVersion"),
        Manufacturer = GetString(item, "manufacturer"),
        Model = GetString(item, "model"),
        SerialNumber = GetString(item, "serialNumber"),
        Imei = GetString(item, "imei"),
        Meid = GetString(item, "meid"),
        WiFiMacAddress = GetString(item, "wiFiMacAddress"),
        EthernetMacAddress = GetString(item, "ethernetMacAddress"),
        EnrolledDateTime = GetDateTimeOffset(item, "enrolledDateTime"),
        LastSyncDateTime = GetDateTimeOffset(item, "lastSyncDateTime"),
        ComplianceState = GetString(item, "complianceState"),
        ManagementAgent = GetString(item, "managementAgent"),
        ManagedDeviceOwnerType = GetString(item, "managedDeviceOwnerType"),
        DeviceEnrollmentType = GetString(item, "deviceEnrollmentType"),
        DeviceRegistrationState = GetString(item, "deviceRegistrationState"),
        IsEncrypted = GetBool(item, "isEncrypted"),
        IsSupervised = GetBool(item, "isSupervised"),
        JailBroken = GetString(item, "jailBroken") == "True",
        AzureADDeviceId = GetString(item, "azureADDeviceId"),
        AzureADRegistered = GetBool(item, "azureADRegistered"),
        TotalStorageSpaceInBytes = GetLong(item, "totalStorageSpaceInBytes"),
        FreeStorageSpaceInBytes = GetLong(item, "freeStorageSpaceInBytes"),
        Notes = GetString(item, "notes")
    };

    private static string GetString(JsonElement item, string name) =>
        item.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.String ? prop.GetString() ?? "" : "";

    private static bool GetBool(JsonElement item, string name) =>
        item.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.True;

    private static long GetLong(JsonElement item, string name) =>
        item.TryGetProperty(name, out var prop) && prop.ValueKind == JsonValueKind.Number && prop.TryGetInt64(out var value) ? value : 0;

    private static DateTimeOffset? GetDateTimeOffset(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.String)
            return null;

        return DateTimeOffset.TryParse(prop.GetString(), out var value) ? value : null;
    }
}
