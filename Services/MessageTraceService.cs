using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class MessageTraceService : ExchangeServiceBase, Jobs.IMessageTraceDetailSource
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

    /// <summary>
    /// Submits an asynchronous historical search to Exchange Online. **Currently has no caller.**
    /// </summary>
    /// <remarks>
    /// Retained deliberately rather than deleted: it is the only route to trace data older than
    /// the 90-day realtime window, so it is the starting point if that is ever needed.
    ///
    /// It is NOT wired to the trace page any more. The page routed anything wider than 10 days here
    /// on the belief that realtime trace could not reach further; measured against this tenant
    /// 2026-08-05, <c>Get-MessageTraceV2</c> serves the full 90 days synchronously (rows returned at
    /// 9/11/20/45/89/90 days back, refused at 91), so every window the page was sending here could
    /// be answered in-app instead.
    ///
    /// **Before reviving this, know what it cannot do.** The report is not returned by any cmdlet -
    /// <c>Get-HistoricalSearch</c> yields a <c>FileUrl</c> on
    /// <c>admin.protection.outlook.com</c>, and fetching it with this app's certificate identity
    /// redirects to <c>login.microsoftonline.com</c> and returns a sign-in page (measured
    /// 2026-08-05). The report reaches a human only as Microsoft's own email to
    /// <c>NotifyAddress</c>, which is exactly the barrier for operators without a cloud admin
    /// account. Also note <c>Status = "Done"</c> does not imply a report exists: a zero-row search
    /// is Done with an empty FileUrl. See docs/HistoricalSearchInApp-Plan.md (Superseded).
    /// </remarks>
    public async Task<HistoricalSearchResponse> StartHistoricalSearchAsync(string? sender, string? recipient, DateTime startDate, DateTime endDate, string notifyAddress, string reportTitle)
    {
        // Single-write (Start-HistoricalSearch): safe to retry on a dead pooled session.
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
        }, allowRetry: true);
    }

    // -------------------------------------------------------------------------
    // Per-message delivery detail (the full per-hop trail for one message)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Fetch the full per-hop delivery trail for a single <paramref name="message"/>. Routes by
    /// <see cref="MessageTraceResult.Backend"/>: on-prem re-runs Get-MessageTrackingLog scoped to
    /// the one message with the reason fields and NO collapse (every event row, ordered by
    /// timestamp); cloud calls Get-MessageTraceDetailV2 keyed by MessageTraceId + RecipientAddress.
    /// Fail-soft: any failure sets <see cref="MessageTraceDetail.Error"/> with empty
    /// <see cref="MessageTraceDetail.Events"/>; it never throws. A cloud message aged out of the
    /// trace window returns empty events with an explanatory message, not an exception. The live
    /// PowerShell paths run through the sealed connection pool / on-prem runspace and are
    /// manual-validation-only; the routing and mapping seams below are unit-covered.
    /// </summary>
    public async Task<MessageTraceDetail> GetMessageDetailAsync(MessageTraceResult message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        // Outer fail-soft guard through a pure seam: the inner catches only cover failures once
        // the pooled delegate / on-prem Task.Run body is running. EXO borrow/config/pool/connect
        // and throttle-timeout failures throw before that point; RunDetailBackendAsync converts
        // any such throw to a fail-soft detail so the caller never sees an exception (mirrors the
        // summary path's RunMessageTraceBackendAsync wrapper).
        return await RunDetailBackendAsync(
            message,
            () => ClassifyDetailBackend(message.Backend) switch
            {
                DetailBackend.OnPrem => GetOnPremMessageDetailAsync(message, ct),
                DetailBackend.Cloud => GetCloudMessageDetailAsync(message),
                _ => Task.FromResult(UnknownBackendDetail(message))
            },
            ex => _logger.LogError(ex, "Message delivery detail failed before returning a result for {MessageId}", message.MessageId));
    }

    // Pure outer fail-soft guard, extracted so it is unit-testable without a live pool / on-prem
    // runspace (pool-backed services cannot be unit-hosted; mirrors RunWithRetryCoreAsync). Any
    // throw from the backend query - including EXO borrow/config/pool/connect and on-prem
    // throttle-timeout failures that occur before the inner catches run - becomes a fail-soft
    // detail with Error set and Events empty. GetMessageDetailAsync therefore never throws.
    internal static async Task<MessageTraceDetail> RunDetailBackendAsync(
        MessageTraceResult message,
        Func<Task<MessageTraceDetail>> query,
        Action<Exception>? onError = null)
    {
        try
        {
            return await query();
        }
        catch (Exception ex)
        {
            onError?.Invoke(ex);
            return new MessageTraceDetail { Summary = message, Error = $"Delivery detail failed: {ex.Message}" };
        }
    }

    internal enum DetailBackend { OnPrem, Cloud, Unknown }

    internal static DetailBackend ClassifyDetailBackend(string? backend) => backend switch
    {
        "OnPrem" => DetailBackend.OnPrem,
        "ExchangeOnline" => DetailBackend.Cloud,
        _ => DetailBackend.Unknown
    };

    internal static MessageTraceDetail UnknownBackendDetail(MessageTraceResult message) => new()
    {
        Summary = message,
        Error = $"Cannot fetch delivery detail: unrecognized backend '{message.Backend}'."
    };

    private async Task<MessageTraceDetail> GetCloudMessageDetailAsync(MessageTraceResult message)
    {
        // Read-only single query: safe to retry on a dead pooled session.
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            try
            {
                ps.AddCommand("Get-MessageTraceDetailV2")
                  .AddParameter("MessageTraceId", message.MessageTraceId)
                  .AddParameter("RecipientAddress", message.RecipientAddress)
                  .AddParameter("ErrorAction", "Stop");

                var events = Invoke(ps, tracker);
                return BuildCloudDetail(message, events);
            }
            catch (Exception ex) when (IsOutdatedModuleError(ex))
            {
                _logger.LogError(ex, "Get-MessageTraceDetailV2 not available - ExchangeOnlineManagement module may be outdated");
                return new MessageTraceDetail
                {
                    Summary = message,
                    Error = "Get-MessageTraceDetailV2 requires ExchangeOnlineManagement 3.7.0 or later. Please update the module."
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching Exchange Online message trace detail for {MessageTraceId}", message.MessageTraceId);
                return new MessageTraceDetail
                {
                    Summary = message,
                    Error = $"Exchange Online trace detail failed: {ex.Message}"
                };
            }
        }, allowRetry: true);
    }

    private async Task<MessageTraceDetail> GetOnPremMessageDetailAsync(MessageTraceResult message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_onPremServerUri))
            return new MessageTraceDetail { Summary = message, Error = "On-prem delivery detail unavailable: OnPremExchange:ServerUri is not configured." };

        var creds = await GetModuleCredentialsAsync("on-prem message tracking detail");
        if (creds is null)
            return new MessageTraceDetail { Summary = message, Error = "On-prem delivery detail unavailable: credentials could not be retrieved from Delinea." };

        return await ThrottledAsync(() => Task.Run(() =>
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
                var session = ps.Runspace.SessionStateProxy.GetVariable("onpremSession");
                var server = string.IsNullOrWhiteSpace(message.Server) ? null : message.Server;
                var tracking = InvokeOnPremMessageDetailQuery(ps, session, server, message);
                return BuildOnPremDetail(message, tracking);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching on-prem message tracking detail for {MessageId}", message.MessageId);
                return new MessageTraceDetail { Summary = message, Error = $"On-prem delivery detail failed: {ex.Message}" };
            }
            finally
            {
                RemoveOnPremSession(ps);
            }
        }, ct), _onPremThrottle);
    }

    // Detail query: scoped to the one message, with the reason fields Select-Object drops for the
    // summary list (Source, SourceContext, RecipientStatus), and NO collapse - every event row is
    // returned so the trail is intact. Narrow time window around the summary timestamp keeps it cheap.
    private static Collection<PSObject> InvokeOnPremMessageDetailQuery(PowerShell ps, object? session, string? server, MessageTraceResult message)
    {
        if (session is null)
            return new Collection<PSObject>();

        var hasTimestamp = message.Received != DateTime.MinValue;
        var start = hasTimestamp ? message.Received.AddMinutes(-5) : DateTime.Now.AddDays(-7);
        var end = hasTimestamp ? message.Received.AddMinutes(5) : DateTime.Now;

        var command = new StringBuilder();
        command.Append("Get-MessageTrackingLog");
        command.Append(" -Start ").Append(PowerShellLiteral(start));
        command.Append(" -End ").Append(PowerShellLiteral(end));
        command.Append(" -ResultSize ").Append(MessageTraceResponse.MaxResults.ToString(CultureInfo.InvariantCulture));
        command.Append(" -ErrorAction SilentlyContinue");
        AddMessageTrackingParameter(command, "Server", server);
        AddMessageTrackingParameter(command, "MessageId", message.MessageId);
        command.Append(" | Select-Object Timestamp,EventId,Source,SourceContext,RecipientStatus,Recipients,MessageId,MessageSubject,ServerHostname");

        var script = ScriptBlock.Create(command.ToString());
        ps.AddCommand("Invoke-Command")
          .AddParameter("Session", session)
          .AddParameter("ScriptBlock", script);

        return InvokeOptional(ps);
    }

    // Map the on-prem tracking rows for one message into the delivery trail. NO collapse: every row
    // is preserved (contrast the summary path's GroupBy(...).First()), ordered by timestamp, with the
    // reason fields carried through (Source; SourceContext + RecipientStatus joined into Detail).
    internal static MessageTraceDetail BuildOnPremDetail(MessageTraceResult summary, IEnumerable<PSObject> tracking)
    {
        var detail = new MessageTraceDetail
        {
            Summary = summary,
            Events = MapOnPremDetailEvents(tracking, summary.MessageId)
        };
        if (detail.Events.Count == 0)
            detail.Error = "No delivery events were found for this message in the on-prem tracking log.";
        return detail;
    }

    internal static List<MessageTraceDetailEvent> MapOnPremDetailEvents(IEnumerable<PSObject> tracking, string? messageIdFilter)
    {
        var normalized = NormalizeMessageId(messageIdFilter);
        var events = new List<MessageTraceDetailEvent>();
        foreach (var item in tracking)
        {
            // Null pipeline element: skip the row rather than crash the whole mapping.
            if (item is null)
                continue;

            if (!MessageIdMatches(GetPropertyString(item, "MessageId"), normalized))
                continue;

            var sourceContext = GetPropertyString(item, "SourceContext");
            var recipientStatus = GetPropertyString(item, "RecipientStatus");
            var reason = string.Join(" | ", new[] { sourceContext, recipientStatus }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            events.Add(new MessageTraceDetailEvent
            {
                Date = GetPropertyDate(item, "Timestamp"),
                Event = GetPropertyString(item, "EventId"),
                Action = string.Empty,
                Detail = reason,
                Source = GetPropertyString(item, "Source")
            });
        }
        return events.OrderBy(e => e.Date).ToList();
    }

    internal static MessageTraceDetail BuildCloudDetail(MessageTraceResult summary, IEnumerable<PSObject> events)
    {
        var detail = new MessageTraceDetail
        {
            Summary = summary,
            Events = MapCloudDetailEvents(events)
        };
        if (detail.Events.Count == 0)
            detail.Error = "No delivery detail is available for this message; it may have aged out of the trace window.";
        return detail;
    }

    internal static List<MessageTraceDetailEvent> MapCloudDetailEvents(IEnumerable<PSObject> events)
    {
        var mapped = new List<MessageTraceDetailEvent>();
        foreach (var evt in events)
        {
            // Null pipeline element: skip the row rather than crash the whole mapping.
            if (evt is null)
                continue;

            mapped.Add(new MessageTraceDetailEvent
            {
                Date = GetPropertyDate(evt, "Date"),
                Event = GetPropertyString(evt, "Event"),
                Action = GetPropertyString(evt, "Action"),
                Detail = GetPropertyString(evt, "Detail", "Data"),
                Source = string.Empty
            });
        }
        return mapped.OrderBy(e => e.Date).ToList();
    }

    // The outdated-module signature shared by the summary and detail cloud paths: the V2 cmdlet is
    // absent on ExchangeOnlineManagement < 3.7.0, surfacing as a "not recognized" command error.
    internal static bool IsOutdatedModuleError(Exception ex) =>
        ex.Message.Contains("not recognized", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("is not recognized", StringComparison.OrdinalIgnoreCase)
        || ex.Message.Contains("CommandNotFoundException", StringComparison.OrdinalIgnoreCase);

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
        // Read-only: safe to retry on a dead pooled session.
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
                    // A null pipeline element crashes every GetProperty* read below (they
                    // dereference obj.Properties directly). Skip the row, keep the batch.
                    if (msg is null)
                        continue;

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
            catch (Exception ex) when (IsOutdatedModuleError(ex))
            {
                response.Error = "Get-MessageTraceV2 requires ExchangeOnlineManagement 3.7.0 or later. Please update the module.";
                _logger.LogError(ex, "Get-MessageTraceV2 not available - ExchangeOnlineManagement module may be outdated");
            }
            catch (Exception ex)
            {
                response.Error = $"Exchange Online trace failed: {ex.Message}";
                _logger.LogError(ex, "Error running Exchange Online message trace");
            }

            return response;
        }, allowRetry: true);
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
                    // Null pipeline element: skip the row rather than crash the whole trace.
                    if (item is null)
                        continue;

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
