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
        if (fields == null) return null;

        var tenantId = fields.GetValueOrDefault("Tenant ID") ?? "";
        var clientId = fields.GetValueOrDefault("Application ID") ?? "";
        var clientSecret = fields.GetValueOrDefault("Client Secret") ?? "";

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            return null;

        return new GraphTokenClient(tenantId, clientId, clientSecret, _httpClientFactory.CreateClient("MicrosoftGraph"));
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
    private const string SelectFields =
        "id,deviceName,managedDeviceName,userPrincipalName,userDisplayName,userId,operatingSystem," +
        "osVersion,manufacturer,model,serialNumber,imei,meid,wiFiMacAddress,ethernetMacAddress," +
        "enrolledDateTime,lastSyncDateTime,complianceState,managementAgent,managedDeviceOwnerType," +
        "deviceEnrollmentType,deviceRegistrationState,isEncrypted,isSupervised,jailBroken," +
        "azureADDeviceId,azureADRegistered,totalStorageSpaceInBytes,freeStorageSpaceInBytes,notes";

    /// <summary>
    /// Bounded search across deviceName, serialNumber and userPrincipalName. Which of these
    /// Graph will actually accept in $filter eq is not yet verified against a live tenant (T2) -
    /// a device search that will not filter server-side does not read as "no matches" here,
    /// because a non-success status throws (T7) rather than being swallowed; a 400 for an
    /// unsupported filter property surfaces as a failed search, not an empty one.
    /// </summary>
    public async Task<IntuneDeviceSearchResult> SearchDevicesAsync(string? searchTerm)
    {
        var client = await _graphClientFactory() ?? throw new InvalidOperationException("Intune Devices Graph credentials not available.");

        var top = ClampSearchResultLimit(_moduleConfig?.GetValue("IntuneDevices", "SearchResultLimit"));

        var query = $"$top={top}&$select={SelectFields}";
        var filterExpression = BuildFilterExpression(searchTerm);
        if (filterExpression != null)
            query += $"&$filter={Uri.EscapeDataString(filterExpression)}";

        var (doc, status) = await client.GetWithStatusAsync($"{DevicesEndpoint}?{query}");

        // A failed request must never render as "no devices found" - an operator searching a
        // serial number who sees nothing must not conclude the device is unenrolled when the
        // real cause is a missing permission or a throttled tenant (T7).
        if (doc == null)
            throw BuildFailure(status, "Intune devices");

        using var responseDoc = doc;

        // @odata.nextLink is absolute and GraphTokenClient cannot follow it (T1) - its presence
        // must still be surfaced so a capped list never looks like a complete one.
        var truncated = responseDoc.RootElement.TryGetProperty("@odata.nextLink", out _);

        var devices = new List<IntuneDevice>();
        var searchedCount = 0;
        foreach (var item in responseDoc.RootElement.GetProperty("value").EnumerateArray())
        {
            searchedCount++;
            devices.Add(ParseDevice(item));
        }

        return new IntuneDeviceSearchResult(devices, truncated, searchedCount);
    }

    public async Task<IntuneDevice> GetDeviceAsync(string deviceId)
    {
        var client = await _graphClientFactory() ?? throw new InvalidOperationException("Intune Devices Graph credentials not available.");

        var (doc, status) = await client.GetWithStatusAsync($"{DevicesEndpoint}/{Uri.EscapeDataString(deviceId)}?$select={SelectFields}");

        if (doc == null)
            throw BuildFailure(status, "Intune device detail");

        using var responseDoc = doc;
        return ParseDevice(responseDoc.RootElement);
    }

    private static InvalidOperationException BuildFailure(HttpStatusCode status, string context)
    {
        if (status == HttpStatusCode.Forbidden)
            return new InvalidOperationException(
                "Intune Devices is not available for this tenant - verify the app registration's DeviceManagementManagedDevices.Read.All consent.");

        if (status == HttpStatusCode.NotFound)
            return new InvalidOperationException($"{context} not found.");

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
    /// Builds the $filter expression that matches searchTerm against deviceName, serialNumber or
    /// userPrincipalName (T2 - none of the three is yet verified filterable; Graph rejecting the
    /// clause is a failed request via GetWithStatusAsync, never a silent empty list). A single
    /// quote in the value is doubled per the OData literal-escaping shape
    /// (M365GroupManagementService.cs:74, T4c) rather than interpolated raw; the caller
    /// Uri.EscapeDataString's the whole expression as a query parameter.
    /// </summary>
    internal static string? BuildFilterExpression(string? searchTerm)
    {
        if (string.IsNullOrWhiteSpace(searchTerm))
            return null;

        var escaped = EscapeODataLiteral(searchTerm);
        return $"deviceName eq '{escaped}' or serialNumber eq '{escaped}' or userPrincipalName eq '{escaped}'";
    }

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
