using System.Management.Automation;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class MessageTraceService : ExchangeServiceBase
{
    public MessageTraceService(ExoConnectionPool exoPool, DelineaService delineaService, ILogger logger, string onPremServerUri)
        : base(exoPool, delineaService, logger, onPremServerUri) { }

    public async Task<MessageTraceResponse> GetMessageTraceAsync(string? sender, string? recipient, DateTime startDate, DateTime endDate, string? subjectFilter)
    {
        return await RunPooledQueryAsync(ps =>
        {
            var response = new MessageTraceResponse();

            try
            {
                var allResults = new List<MessageTraceResult>();
                var page = 1;
                const int pageSize = 200;
                const int maxPages = 10;

                while (allResults.Count < MessageTraceResponse.MaxResults && page <= maxPages)
                {
                    ps.AddCommand("Get-MessageTrace")
                      .AddParameter("StartDate", startDate)
                      .AddParameter("EndDate", endDate)
                      .AddParameter("PageSize", pageSize)
                      .AddParameter("Page", page)
                      .AddParameter("ErrorAction", "Stop");

                    if (!string.IsNullOrWhiteSpace(sender))
                        ps.AddParameter("SenderAddress", sender);
                    if (!string.IsNullOrWhiteSpace(recipient))
                        ps.AddParameter("RecipientAddress", recipient);

                    var results = Invoke(ps);
                    if (!results.Any())
                        break;

                    foreach (var msg in results)
                    {
                        var subject = msg.Properties["Subject"]?.Value?.ToString() ?? "";
                        if (!string.IsNullOrWhiteSpace(subjectFilter) &&
                            !subject.Contains(subjectFilter, StringComparison.OrdinalIgnoreCase))
                            continue;

                        allResults.Add(new MessageTraceResult
                        {
                            Received = msg.Properties["Received"]?.Value as DateTime? ?? DateTime.MinValue,
                            SenderAddress = msg.Properties["SenderAddress"]?.Value?.ToString() ?? "",
                            RecipientAddress = msg.Properties["RecipientAddress"]?.Value?.ToString() ?? "",
                            Subject = subject,
                            Status = msg.Properties["Status"]?.Value?.ToString() ?? "",
                            MessageId = msg.Properties["MessageId"]?.Value?.ToString() ?? "",
                            Size = msg.Properties["Size"]?.Value as long? ?? 0,
                            FromIP = msg.Properties["FromIP"]?.Value?.ToString() ?? "",
                            ToIP = msg.Properties["ToIP"]?.Value?.ToString() ?? "",
                            MessageTraceId = msg.Properties["MessageTraceId"]?.Value?.ToString()
                                ?? msg.Properties["MessageTraceID"]?.Value?.ToString() ?? ""
                        });

                        if (allResults.Count >= MessageTraceResponse.MaxResults)
                        {
                            response.Truncated = true;
                            break;
                        }
                    }

                    if (results.Count < pageSize)
                        break;

                    page++;
                }

                if (page > maxPages && allResults.Count < MessageTraceResponse.MaxResults)
                    response.Truncated = true;

                response.Results = allResults;
                response.TotalAvailable = allResults.Count;
            }
            catch (Exception ex)
            {
                response.Error = ex.Message;
                _logger.LogError(ex, "Error running message trace");
            }

            return response;
        });
    }

    public async Task<HistoricalSearchResponse> StartHistoricalSearchAsync(string? sender, string? recipient, DateTime startDate, DateTime endDate, string notifyAddress, string reportTitle)
    {
        return await RunPooledQueryAsync(ps =>
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

                var results = Invoke(ps);
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
}
