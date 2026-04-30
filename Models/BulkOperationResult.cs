using System.Text;

namespace ExchangeAdminWeb.Models;

public class BulkOperationResult
{
    public int TotalRows { get; init; }
    public int SuccessCount { get; init; }
    public int FailedCount { get; init; }
    public List<string> Errors { get; init; } = new();
    public List<BulkOperationEntry> Entries { get; init; } = new();
    public string Summary => $"Processed {TotalRows} rows: {SuccessCount} succeeded, {FailedCount} failed";

    public byte[] GenerateCsvReport()
    {
        var csv = new StringBuilder();
        csv.AppendLine("Target,User,Permission,Status,Message");

        foreach (var entry in Entries)
        {
            var message = entry.Message.Replace("\"", "\"\"");
            csv.AppendLine($"\"{entry.Target}\",\"{entry.User}\",\"{entry.Permission}\",\"{entry.Status}\",\"{message}\"");
        }

        return Encoding.UTF8.GetBytes(csv.ToString());
    }
}

public class BulkOperationEntry
{
    public required string Target { get; init; }
    public required string User { get; init; }
    public required string Permission { get; init; }
    public required string Status { get; init; }
    public required string Message { get; init; }
}
