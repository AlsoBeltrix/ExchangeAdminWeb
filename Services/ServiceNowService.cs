using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ExchangeAdminWeb.Services;

public class ServiceNowService
{
    private readonly string _instanceUrl;
    private readonly string _username;
    private readonly string _password;
    private readonly bool _enabled;
    private readonly ILogger<ServiceNowService> _logger;
    private readonly HttpClient _httpClient;

    public ServiceNowService(IConfiguration config, ILogger<ServiceNowService> logger, IHttpClientFactory httpClientFactory)
    {
        _logger = logger;
        _instanceUrl = config["ServiceNow:InstanceUrl"] ?? "";
        _username = config["ServiceNow:Username"] ?? "";
        _password = config["ServiceNow:Password"] ?? "";
        _enabled = bool.Parse(config["ServiceNow:Enabled"] ?? "false");

        _httpClient = httpClientFactory.CreateClient("ServiceNow");

        if (_enabled && !string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_password))
        {
            var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_username}:{_password}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
        }
    }

    public async Task<TicketValidationResult> ValidateTicketAsync(string ticketNumber)
    {
        if (!_enabled)
        {
            _logger.LogDebug("ServiceNow integration disabled, skipping ticket validation for {Ticket}", ticketNumber);
            return new TicketValidationResult
            {
                IsValid = true,
                Message = "Ticket validation disabled"
            };
        }

        if (string.IsNullOrWhiteSpace(ticketNumber))
        {
            return new TicketValidationResult
            {
                IsValid = false,
                Message = "Ticket number is required"
            };
        }

        try
        {
            var table = ticketNumber.StartsWith("REQ", StringComparison.OrdinalIgnoreCase) ? "sc_request" : "incident";
            var query = $"number={ticketNumber}";
            var url = $"{_instanceUrl}/api/now/table/{table}?sysparm_query={Uri.EscapeDataString(query)}&sysparm_fields=number,state,short_description,sys_id";

            _logger.LogInformation("Validating ticket {Ticket} via ServiceNow API: {Url}", ticketNumber, url);

            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogError("ServiceNow API request failed: {StatusCode} - {Reason}", response.StatusCode, response.ReasonPhrase);
                return new TicketValidationResult
                {
                    IsValid = false,
                    Message = $"ServiceNow API error: {response.StatusCode}"
                };
            }

            var content = await response.Content.ReadAsStringAsync();
            var jsonDoc = JsonDocument.Parse(content);
            var results = jsonDoc.RootElement.GetProperty("result");

            if (results.GetArrayLength() == 0)
            {
                _logger.LogWarning("Ticket {Ticket} not found in ServiceNow", ticketNumber);
                return new TicketValidationResult
                {
                    IsValid = false,
                    Message = $"Ticket {ticketNumber} not found in ServiceNow"
                };
            }

            var ticket = results[0];
            var state = ticket.GetProperty("state").GetString();
            var description = ticket.GetProperty("short_description").GetString();

            // State values in ServiceNow (typical):
            // 1 = New, 2 = In Progress, 3 = On Hold, 6 = Resolved, 7 = Closed, 8 = Canceled
            // We allow 1, 2, 3 (active tickets)
            var validStates = new[] { "1", "2", "3" };
            if (!validStates.Contains(state))
            {
                _logger.LogWarning("Ticket {Ticket} is not in an active state (state={State})", ticketNumber, state);
                return new TicketValidationResult
                {
                    IsValid = false,
                    Message = $"Ticket {ticketNumber} is not active (state: {GetStateName(state)})",
                    TicketNumber = ticketNumber,
                    State = state,
                    Description = description
                };
            }

            _logger.LogInformation("Ticket {Ticket} validated successfully (state={State})", ticketNumber, state);
            return new TicketValidationResult
            {
                IsValid = true,
                Message = "Ticket is valid and active",
                TicketNumber = ticketNumber,
                State = state,
                Description = description
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating ticket {Ticket}", ticketNumber);
            return new TicketValidationResult
            {
                IsValid = false,
                Message = $"Error validating ticket: {ex.Message}"
            };
        }
    }

    private static string GetStateName(string? state) => state switch
    {
        "1" => "New",
        "2" => "In Progress",
        "3" => "On Hold",
        "6" => "Resolved",
        "7" => "Closed",
        "8" => "Canceled",
        _ => $"Unknown ({state})"
    };
}

public class TicketValidationResult
{
    public bool IsValid { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? TicketNumber { get; set; }
    public string? State { get; set; }
    public string? Description { get; set; }
}
