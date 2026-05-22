using System.Text.Json;

namespace ExchangeAdminWeb.Services;

public class NamedLocationsService
{
    private readonly ILogger<NamedLocationsService> _logger;
    private readonly ModuleConfigService _moduleConfig;
    private readonly DelineaService _delineaService;
    private readonly IHttpClientFactory _httpClientFactory;

    public NamedLocationsService(ILogger<NamedLocationsService> logger, ModuleConfigService moduleConfig, DelineaService delineaService, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _moduleConfig = moduleConfig;
        _delineaService = delineaService;
        _httpClientFactory = httpClientFactory;
    }

    private async Task<GraphTokenClient> GetGraphClientAsync()
    {
        var secretIdStr = _moduleConfig.GetValue("NamedLocations", "DelineaSecretId");
        if (!int.TryParse(secretIdStr, out var secretId) || secretId <= 0)
            throw new InvalidOperationException("Named Locations module is not configured. Set DelineaSecretId in Module Config.");

        var fields = await _delineaService.GetSecretFieldsAsync(secretId);
        if (fields == null)
            throw new InvalidOperationException("Cannot retrieve credentials from Secret Server.");

        var tenantId = fields.GetValueOrDefault("Tenant ID") ?? "";
        var clientId = fields.GetValueOrDefault("Application ID") ?? "";
        var clientSecret = fields.GetValueOrDefault("Client Secret") ?? "";

        if (string.IsNullOrEmpty(tenantId) || string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret))
            throw new InvalidOperationException("Graph API credentials incomplete in Secret Server.");

        return new GraphTokenClient(tenantId, clientId, clientSecret, _httpClientFactory.CreateClient("MicrosoftGraph"));
    }

    public bool IsAvailable
    {
        get
        {
            var secretIdStr = _moduleConfig.GetValue("NamedLocations", "DelineaSecretId");
            return int.TryParse(secretIdStr, out var id) && id > 0;
        }
    }

    public async Task<List<NamedLocation>> GetAllAsync()
    {
        var client = await GetGraphClientAsync();
        using var doc = await client.GetAsync("/identity/conditionalAccess/namedLocations");
        if (doc == null) return [];

        var locations = new List<NamedLocation>();
        foreach (var item in doc.RootElement.GetProperty("value").EnumerateArray())
        {
            locations.Add(ParseNamedLocation(item));
        }

        return locations.OrderBy(l => l.DisplayName).ToList();
    }

    public async Task<NamedLocation?> GetByIdAsync(string id)
    {
        var client = await GetGraphClientAsync();
        using var doc = await client.GetAsync($"/identity/conditionalAccess/namedLocations/{Uri.EscapeDataString(id)}");
        if (doc == null) return null;
        return ParseNamedLocation(doc.RootElement);
    }

    public async Task<NamedLocationResult> CreateIpLocationAsync(string displayName, List<string> cidrRanges, bool isTrusted)
    {
        var client = await GetGraphClientAsync();
        var body = new Dictionary<string, object>
        {
            ["@odata.type"] = "#microsoft.graph.ipNamedLocation",
            ["displayName"] = displayName,
            ["isTrusted"] = isTrusted,
            ["ipRanges"] = cidrRanges.Select(c => new Dictionary<string, string>
            {
                ["@odata.type"] = c.Contains(':') ? "#microsoft.graph.iPv6CidrRange" : "#microsoft.graph.iPv4CidrRange",
                ["cidrAddress"] = c
            }).ToList()
        };

        using var doc = await client.PostAsync("/identity/conditionalAccess/namedLocations", body);
        if (doc == null)
            return new NamedLocationResult { Success = false, Message = "Failed to create named location. Check permissions (Policy.ReadWrite.ConditionalAccess)." };

        var id = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        _logger.LogInformation("Created IP named location '{Name}' (id={Id})", displayName, id);
        return new NamedLocationResult { Success = true, Message = $"Created IP location '{displayName}'." };
    }

    public async Task<NamedLocationResult> CreateCountryLocationAsync(string displayName, List<string> countryCodes, bool includeUnknown)
    {
        var client = await GetGraphClientAsync();
        var body = new Dictionary<string, object>
        {
            ["@odata.type"] = "#microsoft.graph.countryNamedLocation",
            ["displayName"] = displayName,
            ["countriesAndRegions"] = countryCodes,
            ["includeUnknownCountriesAndRegions"] = includeUnknown
        };

        using var doc = await client.PostAsync("/identity/conditionalAccess/namedLocations", body);
        if (doc == null)
            return new NamedLocationResult { Success = false, Message = "Failed to create named location. Check permissions (Policy.ReadWrite.ConditionalAccess)." };

        _logger.LogInformation("Created country named location '{Name}'", displayName);
        return new NamedLocationResult { Success = true, Message = $"Created country location '{displayName}'." };
    }

    public async Task<NamedLocationResult> UpdateIpLocationAsync(string id, string displayName, List<string> cidrRanges, bool isTrusted)
    {
        var client = await GetGraphClientAsync();
        var body = new Dictionary<string, object>
        {
            ["@odata.type"] = "#microsoft.graph.ipNamedLocation",
            ["displayName"] = displayName,
            ["isTrusted"] = isTrusted,
            ["ipRanges"] = cidrRanges.Select(c => new Dictionary<string, string>
            {
                ["@odata.type"] = c.Contains(':') ? "#microsoft.graph.iPv6CidrRange" : "#microsoft.graph.iPv4CidrRange",
                ["cidrAddress"] = c
            }).ToList()
        };

        var success = await client.PatchAsync($"/identity/conditionalAccess/namedLocations/{Uri.EscapeDataString(id)}", body);
        if (!success)
            return new NamedLocationResult { Success = false, Message = "Failed to update named location." };

        _logger.LogInformation("Updated IP named location '{Name}' (id={Id})", displayName, id);
        return new NamedLocationResult { Success = true, Message = $"Updated '{displayName}'." };
    }

    public async Task<NamedLocationResult> UpdateCountryLocationAsync(string id, string displayName, List<string> countryCodes, bool includeUnknown)
    {
        var client = await GetGraphClientAsync();
        var body = new Dictionary<string, object>
        {
            ["@odata.type"] = "#microsoft.graph.countryNamedLocation",
            ["displayName"] = displayName,
            ["countriesAndRegions"] = countryCodes,
            ["includeUnknownCountriesAndRegions"] = includeUnknown
        };

        var success = await client.PatchAsync($"/identity/conditionalAccess/namedLocations/{Uri.EscapeDataString(id)}", body);
        if (!success)
            return new NamedLocationResult { Success = false, Message = "Failed to update named location." };

        _logger.LogInformation("Updated country named location '{Name}' (id={Id})", displayName, id);
        return new NamedLocationResult { Success = true, Message = $"Updated '{displayName}'." };
    }

    public async Task<NamedLocationResult> DeleteAsync(string id, string displayName)
    {
        var client = await GetGraphClientAsync();
        var success = await client.DeleteAsync($"/identity/conditionalAccess/namedLocations/{Uri.EscapeDataString(id)}");
        if (!success)
            return new NamedLocationResult { Success = false, Message = "Failed to delete named location." };

        _logger.LogInformation("Deleted named location '{Name}' (id={Id})", displayName, id);
        return new NamedLocationResult { Success = true, Message = $"Deleted '{displayName}'." };
    }

    private static NamedLocation ParseNamedLocation(JsonElement item)
    {
        var odataType = item.TryGetProperty("@odata.type", out var t) ? t.GetString() ?? "" : "";
        var loc = new NamedLocation
        {
            Id = item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
            DisplayName = item.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "",
            CreatedDateTime = item.TryGetProperty("createdDateTime", out var cd) ? cd.GetString() ?? "" : "",
            ModifiedDateTime = item.TryGetProperty("modifiedDateTime", out var md) ? md.GetString() ?? "" : "",
        };

        if (odataType.Contains("ipNamedLocation"))
        {
            loc.LocationType = NamedLocationType.Ip;
            loc.IsTrusted = item.TryGetProperty("isTrusted", out var tr) && tr.GetBoolean();
            if (item.TryGetProperty("ipRanges", out var ranges))
            {
                foreach (var range in ranges.EnumerateArray())
                {
                    var cidr = range.TryGetProperty("cidrAddress", out var c) ? c.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(cidr))
                        loc.IpRanges.Add(cidr);
                }
            }
        }
        else if (odataType.Contains("countryNamedLocation"))
        {
            loc.LocationType = NamedLocationType.Country;
            loc.IncludeUnknownCountries = item.TryGetProperty("includeUnknownCountriesAndRegions", out var iu) && iu.GetBoolean();
            if (item.TryGetProperty("countriesAndRegions", out var countries))
            {
                foreach (var country in countries.EnumerateArray())
                {
                    var code = country.GetString() ?? "";
                    if (!string.IsNullOrEmpty(code))
                        loc.CountryCodes.Add(code);
                }
            }
        }

        return loc;
    }
}

public enum NamedLocationType { Ip, Country }

public class NamedLocation
{
    public string Id { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public NamedLocationType LocationType { get; set; }
    public bool IsTrusted { get; set; }
    public List<string> IpRanges { get; set; } = new();
    public List<string> CountryCodes { get; set; } = new();
    public bool IncludeUnknownCountries { get; set; }
    public string CreatedDateTime { get; set; } = "";
    public string ModifiedDateTime { get; set; } = "";
}

public class NamedLocationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}
