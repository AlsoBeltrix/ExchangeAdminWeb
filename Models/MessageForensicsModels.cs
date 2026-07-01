namespace ExchangeAdminWeb.Models;

public class EmailHeaders
{
    private const int MaxTotalHeaders = 500;
    private const int MaxValuesPerHeader = 50;
    private int _totalHeaderCount;

    public Dictionary<string, List<string>> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? From { get; set; }
    public string? ReplyTo { get; set; }
    public string? To { get; set; }
    public string? Cc { get; set; }
    public string? Subject { get; set; }
    public DateTime? Date { get; set; }
    public string? MessageId { get; set; }
    public string? ReturnPath { get; set; }
    public string? ContentType { get; set; }
    public string? FileName { get; set; }
    public List<ReceivedHeader> ReceivedHeaders { get; set; } = new();
    public AuthenticationResults? Authentication { get; set; }

    public void AddHeader(string name, string value)
    {
        if (_totalHeaderCount >= MaxTotalHeaders)
            return;

        if (!Headers.ContainsKey(name))
            Headers[name] = new List<string>();

        if (Headers[name].Count >= MaxValuesPerHeader)
            return;

        Headers[name].Add(value);
        _totalHeaderCount++;
    }

    public string? GetHeader(string name) =>
        Headers.TryGetValue(name, out var values) && values.Count > 0 ? values[0] : null;

    public List<string>? GetHeaders(string name) =>
        Headers.TryGetValue(name, out var values) ? values : null;
}

public class ReceivedHeader
{
    public string? From { get; set; }
    public string? By { get; set; }
    public string? Via { get; set; }
    public string? With { get; set; }
    public string? Id { get; set; }
    public string? For { get; set; }
    public DateTimeOffset? Date { get; set; }
    public string RawValue { get; set; } = string.Empty;
    public int Order { get; set; }
}

public class AuthenticationResults
{
    public SpfResult? SPF { get; set; }
    public DkimResult? DKIM { get; set; }
    public DmarcResult? DMARC { get; set; }
    public string? RawValue { get; set; }
}

public class SpfResult
{
    public string Result { get; set; } = "none";
    public string? Domain { get; set; }
    public string? ClientIp { get; set; }
}

public class DkimResult
{
    public string Result { get; set; } = "none";
    public string? Domain { get; set; }
    public string? Selector { get; set; }
    public string? HeaderD { get; set; }
    public string? HeaderI { get; set; }
}

public class DmarcResult
{
    public string Result { get; set; } = "none";
    public string? Policy { get; set; }
    public string? Domain { get; set; }
}

public class HeaderAnalysisResult
{
    public EmailHeaders Headers { get; set; } = new();
    public string? From { get; set; }
    public string? ReplyTo { get; set; }
    public List<string> To { get; set; } = new();
    public List<string> Cc { get; set; } = new();
    public string? Subject { get; set; }
    public DateTime? Date { get; set; }
    public string? MessageId { get; set; }
    public string? ReturnPath { get; set; }
    public string SpfResult { get; set; } = "NONE";
    public string DkimResult { get; set; } = "NONE";
    public string DmarcResult { get; set; } = "NONE";
    public List<RoutingHop> RoutingPath { get; set; } = new();
    public int HopCount { get; set; }
    public TimeSpan? DeliveryTime { get; set; }
    public List<HeaderFinding> Findings { get; set; } = new();
    public List<string> InterestingHeaders { get; set; } = new();
    public Dictionary<string, string> SpamScores { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool HasAttachments { get; set; }
    public string? OriginatingIP { get; set; }
    public Dictionary<string, string> AllHeaders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HeaderTraceSuggestion TraceSuggestion { get; set; } = new();
}

public class RoutingHop
{
    public int Hop { get; set; }
    public string? From { get; set; }
    public string? By { get; set; }
    public string? With { get; set; }
    public DateTimeOffset? Date { get; set; }
    public TimeSpan? DelayFromPrevious { get; set; }
    public string RawValue { get; set; } = string.Empty;
}

public class HeaderFinding
{
    public string Severity { get; set; } = "Info";
    public string Category { get; set; } = "General";
    public string Message { get; set; } = string.Empty;
    public string? Evidence { get; set; }
}

public class HeaderTraceSuggestion
{
    public string? Sender { get; set; }
    public string? Recipient { get; set; }
    public string? Subject { get; set; }
    public string? MessageId { get; set; }
    public DateTime? StartDate { get; set; }
    public DateTime? EndDate { get; set; }
}
