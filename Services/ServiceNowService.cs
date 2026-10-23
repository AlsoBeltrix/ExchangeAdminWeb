using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ExchangeAdminWeb.Services;

public class ServiceNowService
{
    /// <summary>
    /// The only shape a ticket number may take before it is put into a ServiceNow query.
    /// Audit finding A30: sysparm_query is parsed after url-decoding, so its grammar
    /// characters - notably "^" and "^OR" - must never reach it from operator input.
    /// </summary>
    internal static readonly System.Text.RegularExpressions.Regex TicketNumberPattern =
        new("^[A-Za-z0-9]{3,32}$", System.Text.RegularExpressions.RegexOptions.Compiled);

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
        // aud-a58. bool.Parse threw on a value that is neither true nor false, and this singleton, and every ticketed page,
        // failed with it. A mistyped switch is read as on: dormant would pass every ticket.
        _enabled = ConfigBoolean.Read(config, "ServiceNow:Enabled", whenBlank: false, whenNotBoolean: true, logger);

        _httpClient = httpClientFactory.CreateClient("ServiceNow");

        if (_enabled && !string.IsNullOrEmpty(_username) && !string.IsNullOrEmpty(_password))
        {
            var authToken = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{_username}:{_password}"));
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);
        }
    }

    /// <summary>
    /// Whether the ServiceNow integration is enabled on this deployment.
    /// </summary>
    /// <remarks>
    /// When disabled, <see cref="ValidateTicketAsync"/> deliberately passes every
    /// ticket (the integration is dormant, not failing). A consumer that promises
    /// real validation - the per-module <see cref="TicketValidationService"/> -
    /// must be able to see the difference, or its switch silently validates
    /// nothing.
    /// </remarks>
    public bool Enabled => _enabled;

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

        // Audit finding A30, first half. sysparm_query has its OWN grammar - "^" separates
        // clauses, "^OR" makes them alternatives, "=" binds a field - and ServiceNow parses it
        // AFTER url-decoding. So Uri.EscapeDataString below protects the transport and does
        // nothing about the grammar: a ticket number containing "^ORnumberISNOTEMPTY" arrives
        // as two clauses and matches any ticket at all.
        //
        // Refused on shape rather than escaped. Ticket numbers are alphanumeric, the operator
        // is the one who can correct a bad one, and a rejected input is a better answer than a
        // cleverly quoted one - the standing rule is that we do not guess at inaccurate data.
        if (!TicketNumberPattern.IsMatch(ticketNumber))
        {
            _logger.LogWarning("Rejected a ticket number that is not plain alphanumeric");
            return new TicketValidationResult
            {
                IsValid = false,
                Message = "Ticket number must be letters and digits only."
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

            // Audit finding A30, second half. The response was trusted to be about the ticket
            // that was asked for, and nothing checked. Any query that returns a row - through
            // a grammar break above, a ServiceNow-side alias, or simply a different match -
            // validated that row's state as though it were this ticket's.
            var returnedNumber = ticket.TryGetProperty("number", out var numberProp) ? numberProp.GetString() : null;
            if (!string.Equals(returnedNumber, ticketNumber, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("ServiceNow returned a different ticket than the one requested");
                return new TicketValidationResult
                {
                    IsValid = false,
                    Message = $"ServiceNow returned a different ticket than {ticketNumber}."
                };
            }

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
