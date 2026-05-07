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
            csv.AppendLine($"{SanitizeCsvField(entry.Target)},{SanitizeCsvField(entry.User)},{SanitizeCsvField(entry.Permission)},{SanitizeCsvField(entry.Status)},{SanitizeCsvField(entry.Message)}");
        }

        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    private static string SanitizeCsvField(string field)
    {
        if (string.IsNullOrEmpty(field))
            return "";

        var sanitized = field;
        if (sanitized[0] is '=' or '+' or '-' or '@' or '\t' or '\r' or '\n')
            sanitized = "'" + sanitized;

        if (sanitized.Contains(',') || sanitized.Contains('"') || sanitized.Contains('\n') || sanitized.Contains('\r'))
            return $"\"{sanitized.Replace("\"", "\"\"")}\"";

        return sanitized;
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
