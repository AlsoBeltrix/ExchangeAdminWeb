using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class MessageTraceService : ExchangeServiceBase
{
    public MessageTraceService(ExoConnectionPool exoPool, DelineaService delineaService, ILogger<MessageTraceService> logger, IConfiguration config, ModuleCredentialService moduleCredentials, OperationTraceService operationTrace)
        : base(exoPool, delineaService, logger, config["OnPremExchange:ServerUri"] ?? "", moduleCredentials, "MessageTrace", operationTrace) { }

    public async Task<MessageTraceResponse> GetMessageTraceAsync(string? sender, string? recipient, DateTime startDate, DateTime endDate, string? subjectFilter, string? messageId = null)
    {
        var responses = await Task.WhenAll(
            RunMessageTraceBackendAsync(() => GetCloudMessageTraceAsync(sender, recipient, startDate, endDate, subjectFilter, messageId), "Exchange Online"),
            RunMessageTraceBackendAsync(() => GetOnPremMessageTraceAsync(sender, recipient, startDate, endDate, subjectFilter, messageId), "On-prem"));

        var merged = new MessageTraceResponse();
        foreach (var partial in responses)
        {
            merged.Results.AddRange(partial.Results);
            if (partial.Truncated)
                merged.Truncated = true;
            if (!string.IsNullOrWhiteSpace(partial.Error))
                merged.Warnings.Add(partial.Error);
            merged.Warnings.AddRange(partial.Warnings);
        }

        merged.Results = merged.Results
            .OrderByDescending(r => r.Received)
            .Take(MessageTraceResponse.MaxResults)
            .ToList();
        merged.TotalAvailable = merged.Results.Count;
        if (merged.Results.Count >= MessageTraceResponse.MaxResults)
            merged.Truncated = true;

        if (merged.Results.Count == 0 && merged.Warnings.Count > 0)
            merged.Error = string.Join(" | ", merged.Warnings.Distinct(StringComparer.OrdinalIgnoreCase));

        return merged;
    }

    public async Task<HistoricalSearchResponse> StartHistoricalSearchAsync(string? sender, string? recipient, DateTime startDate, DateTime endDate, string notifyAddress, string reportTitle)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            var response = new HistoricalSearchResponse();

            try
            {
                ps.AddCommand("Start-HistoricalSearch")
                  .AddParameter("StartDate", startDate)
                  .AddParameter("EndDate", endDate)
                  .AddParameter("ReportTitle", reportTitle)
                  .AddParameter("ReportType", "MessageTrace")
                  .AddParameter("NotifyAddress", new[] { notifyAddress })
                  .AddParameter("ErrorAction", "Stop");

                if (!string.IsNullOrWhiteSpace(sender))
                    ps.AddParameter("SenderAddress", sender);
                if (!string.IsNullOrWhiteSpace(recipient))
                    ps.AddParameter("RecipientAddress", recipient);

                var results = Invoke(ps, tracker);
                var result = results.FirstOrDefault();
                response.JobId = result?.Properties["JobId"]?.Value?.ToString();
                response.Success = true;
            }
            catch (Exception ex)
            {
                response.Error = ex.Message;
                _logger.LogError(ex, "Error starting historical search");
            }

            return response;
        });
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    private async Task<MessageTraceResponse> RunMessageTraceBackendAsync(Func<Task<MessageTraceResponse>> query, string backend)
    {
        try
        {
            return await query();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Backend} message trace backend failed before returning a response", backend);
            return new MessageTraceResponse { Error = $"{backend} trace failed: {ex.Message}" };
        }
    }

    private async Task<MessageTraceResponse> GetCloudMessageTraceAsync(string? sender, string? recipient, DateTime startDate, DateTime endDate, string? subjectFilter, string? messageId)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            var response = new MessageTraceResponse();

            try
            {
                var allResults = new List<MessageTraceResult>();
                var normalizedMessageId = NormalizeMessageId(messageId);

                ps.AddCommand("Get-MessageTraceV2")
                  .AddParameter("StartDate", startDate)
                  .AddParameter("EndDate", endDate)
                  .AddParameter("ResultSize", 2000)
                  .AddParameter("ErrorAction", "Stop");

                if (!string.IsNullOrWhiteSpace(sender))
                    ps.AddParameter("SenderAddress", sender);
                if (!string.IsNullOrWhiteSpace(recipient))
                    ps.AddParameter("RecipientAddress", recipient);
                if (!string.IsNullOrWhiteSpace(messageId))
                    ps.AddParameter("MessageId", messageId.Trim());
                if (!string.IsNullOrWhiteSpace(subjectFilter))
                {
                    ps.AddParameter("Subject", subjectFilter);
                    ps.AddParameter("SubjectFilterType", "Contains");
                }

                var results = Invoke(ps, tracker);

                foreach (var msg in results)
                {
                    var subject = msg.Properties["Subject"]?.Value?.ToString() ?? "";
                    var resultMessageId = msg.Properties["MessageId"]?.Value?.ToString() ?? "";
                    if (!MessageIdMatches(resultMessageId, normalizedMessageId))
                        continue;

                    allResults.Add(new MessageTraceResult
                    {
                        Received = GetPropertyDate(msg, "Received"),
                        SenderAddress = GetPropertyString(msg, "SenderAddress"),
                        RecipientAddress = GetPropertyString(msg, "RecipientAddress"),
                        Subject = subject,
                        Status = GetPropertyString(msg, "Status"),
                        MessageId = resultMessageId,
                        Size = GetPropertyLong(msg, "Size"),
                        FromIP = GetPropertyString(msg, "FromIP"),
                        ToIP = GetPropertyString(msg, "ToIP"),
                        MessageTraceId = GetPropertyString(msg, "MessageTraceId", "MessageTraceID"),
                        Backend = "ExchangeOnline"
                    });

                    if (allResults.Count >= MessageTraceResponse.MaxResults)
                    {
                        response.Truncated = true;
                        break;
                    }
                }

                response.Results = allResults;
                response.TotalAvailable = allResults.Count;
            }
            catch (Exception ex) when (ex.Message.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
                                    || ex.Message.Contains("is not recognized", StringComparison.OrdinalIgnoreCase)
                                    || ex.Message.Contains("CommandNotFoundException", StringComparison.OrdinalIgnoreCase))
            {
                response.Error = "Get-MessageTraceV2 requires ExchangeOnlineManagement 3.x or later. Please update the module.";
                _logger.LogError(ex, "Get-MessageTraceV2 not available — ExchangeOnlineManagement module may be outdated");
            }
            catch (Exception ex)
            {
                response.Error = $"Exchange Online trace failed: {ex.Message}";
                _logger.LogError(ex, "Error running Exchange Online message trace");
            }

            return response;
        });
    }

    private async Task<MessageTraceResponse> GetOnPremMessageTraceAsync(string? sender, string? recipient, DateTime startDate, DateTime endDate, string? subjectFilter, string? messageId)
    {
        var response = new MessageTraceResponse();
        if (string.IsNullOrWhiteSpace(_onPremServerUri))
        {
            response.Warnings.Add("On-prem message tracking skipped: OnPremExchange:ServerUri is not configured.");
            return response;
        }

        var creds = await GetModuleCredentialsAsync("on-prem message tracking");
        if (creds is null)
        {
            response.Error = "On-prem message tracking failed: Message Analysis DelineaSecretId is not configured or credentials are unavailable.";
            return response;
        }

        return await ThrottledAsync(() => Task.Run(() =>
        {
            var result = new MessageTraceResponse();
            var iss = InitialSessionState.CreateDefault();
            iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
            using var runspace = RunspaceFactory.CreateRunspace(iss);
            runspace.Open();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            try
            {
                ConnectOnPrem(ps, creds.Value.username, creds.Value.password, creds.Value.domain);
                var session = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");
                var normalizedMessageId = NormalizeMessageId(messageId);
                var messageIdValues = MessageIdFilterValues(messageId);
                if (messageIdValues.Length == 0)
                    messageIdValues = [string.Empty];

                var servers = GetOnPremTransportServers(ps, session);
                if (servers.Length == 0)
                    servers = [string.Empty];

                var tracking = new List<PSObject>();
                var queryFailures = new List<string>();

                foreach (var server in servers)
                {
                    foreach (var messageIdValue in messageIdValues)
                    {
                        try
                        {
                            tracking.AddRange(InvokeOnPremMessageTrackingQuery(
                                ps,
                                session,
                                string.IsNullOrWhiteSpace(server) ? null : server,
                                startDate,
                                endDate,
                                sender,
                                recipient,
                                subjectFilter,
                                string.IsNullOrWhiteSpace(messageIdValue) ? null : messageIdValue));
                        }
                        catch (Exception ex)
                        {
                            ps.Commands.Clear();
                            ps.Streams.Error.Clear();
                            var targetServer = string.IsNullOrWhiteSpace(server) ? "default server" : server;
                            queryFailures.Add($"{targetServer}: {ex.Message}");
                        }
                    }
                }

                var mapped = new List<MessageTraceResult>();
                foreach (var item in tracking)
                {
                    var itemMessageId = GetPropertyString(item, "MessageId");
                    if (!MessageIdMatches(itemMessageId, normalizedMessageId))
                        continue;

                    var recipients = GetRecipients(item.Properties["Recipients"]?.Value).DefaultIfEmpty(string.Empty);
                    foreach (var itemRecipient in recipients)
                    {
                        mapped.Add(new MessageTraceResult
                        {
                            Received = GetPropertyDate(item, "Timestamp"),
                            SenderAddress = GetPropertyString(item, "Sender"),
                            RecipientAddress = itemRecipient,
                            Subject = GetPropertyString(item, "MessageSubject"),
                            Status = GetPropertyString(item, "EventId"),
                            MessageId = itemMessageId,
                            Size = GetPropertyLong(item, "TotalBytes"),
                            FromIP = GetPropertyString(item, "ClientIp"),
                            ToIP = GetPropertyString(item, "ServerIp"),
                            MessageTraceId = GetPropertyString(item, "InternalMessageId"),
                            Backend = "OnPrem",
                            EventId = GetPropertyString(item, "EventId"),
                            Server = GetPropertyString(item, "ServerHostname")
                        });
                    }
                }

                result.Results = mapped
                    .GroupBy(r => $"{r.Server}|{r.MessageTraceId}|{r.MessageId}|{r.RecipientAddress}|{r.Received:O}", StringComparer.OrdinalIgnoreCase)
                    .Select(g => g.First())
                    .OrderByDescending(r => r.Received)
                    .Take(MessageTraceResponse.MaxResults)
                    .ToList();
                result.TotalAvailable = result.Results.Count;
                if (tracking.Count >= MessageTraceResponse.MaxResults || result.Results.Count >= MessageTraceResponse.MaxResults)
                    result.Truncated = true;

                if (queryFailures.Count > 0)
                {
                    var sample = string.Join("; ", queryFailures.Take(3));
                    result.Warnings.Add($"Some on-prem message tracking queries failed ({queryFailures.Count}): {sample}");
                }
            }
            catch (Exception ex)
            {
                result.Error = $"On-prem message tracking failed: {ex.Message}";
                _logger.LogError(ex, "Error running on-prem message tracking log search");
            }
            finally
            {
                RemoveOnPremSession(ps);
            }

            return result;
        }), _onPremThrottle);
    }

    private static string[] GetOnPremTransportServers(PowerShell ps, object? session)
    {
        if (session is null)
            return Array.Empty<string>();

        try
        {
            var script = ScriptBlock.Create("Get-TransportService -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name");
            ps.AddCommand("Invoke-Command")
              .AddParameter("Session", session)
              .AddParameter("ScriptBlock", script);

            return InvokeOptional(ps)
                .Select(r => r.BaseObject?.ToString() ?? r.ToString())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            ps.Commands.Clear();
            ps.Streams.Error.Clear();
            return Array.Empty<string>();
        }
    }

    private static Collection<PSObject> InvokeOnPremMessageTrackingQuery(
        PowerShell ps,
        object? session,
        string? server,
        DateTime startDate,
        DateTime endDate,
        string? sender,
        string? recipient,
        string? subject,
        string? messageId)
    {
        if (session is null)
            return new Collection<PSObject>();

        var command = new StringBuilder();
        command.Append("Get-MessageTrackingLog");
        command.Append(" -Start ").Append(PowerShellLiteral(startDate));
        command.Append(" -End ").Append(PowerShellLiteral(endDate));
        command.Append(" -ResultSize ").Append(MessageTraceResponse.MaxResults.ToString(CultureInfo.InvariantCulture));
        command.Append(" -ErrorAction SilentlyContinue");
        AddMessageTrackingParameter(command, "Server", server);
        AddMessageTrackingParameter(command, "Sender", sender);
        AddMessageTrackingParameter(command, "Recipients", recipient);
        AddMessageTrackingParameter(command, "MessageSubject", subject);
        AddMessageTrackingParameter(command, "MessageId", messageId);
        command.Append(" | Select-Object Timestamp,Sender,Recipients,MessageSubject,EventId,MessageId,TotalBytes,ClientIp,ServerIp,ServerHostname,InternalMessageId");

        var script = ScriptBlock.Create(command.ToString());
        ps.AddCommand("Invoke-Command")
          .AddParameter("Session", session)
          .AddParameter("ScriptBlock", script);

        return InvokeOptional(ps);
    }

    private static void AddMessageTrackingParameter(StringBuilder command, string parameterName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        command.Append(" -").Append(parameterName).Append(' ').Append(PowerShellLiteral(value.Trim()));
    }

    private static string PowerShellLiteral(DateTime value) =>
        PowerShellLiteral(value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));

    private static string PowerShellLiteral(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string GetPropertyString(PSObject obj, params string[] names)
    {
        foreach (var name in names)
        {
            var value = obj.Properties[name]?.Value;
            if (value != null)
                return value.ToString() ?? string.Empty;
        }
        return string.Empty;
    }

    private static DateTime GetPropertyDate(PSObject obj, string name) =>
        obj.Properties[name]?.Value is DateTime dt ? dt : DateTime.MinValue;

    private static long GetPropertyLong(PSObject obj, string name)
    {
        var value = obj.Properties[name]?.Value;
        if (value is long l) return l;
        if (value is int i) return i;
        return long.TryParse(value?.ToString(), out var parsed) ? parsed : 0;
    }

    private static IEnumerable<string> GetRecipients(object? value)
    {
        if (value == null)
            yield break;
        if (value is string s)
        {
            if (!string.IsNullOrWhiteSpace(s))
                yield return s;
            yield break;
        }
        if (value is System.Collections.IEnumerable items)
        {
            foreach (var item in items)
            {
                var text = item?.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                    yield return text;
            }
        }
    }

    private static bool MessageIdMatches(string value, string? normalizedFilter)
    {
        if (string.IsNullOrWhiteSpace(normalizedFilter))
            return true;
        return string.Equals(NormalizeMessageId(value), normalizedFilter, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] MessageIdFilterValues(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return Array.Empty<string>();

        var trimmed = messageId.Trim();
        var normalized = NormalizeMessageId(trimmed);
        if (string.IsNullOrWhiteSpace(normalized))
            return Array.Empty<string>();

        return new[] { trimmed, normalized, $"<{normalized}>" }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? NormalizeMessageId(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return null;
        return messageId.Trim().Trim('<', '>');
    }
}
