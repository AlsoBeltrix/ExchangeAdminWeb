using System.Text.RegularExpressions;
using ExchangeAdminWeb.Models;
using MimeKit;

namespace ExchangeAdminWeb.Services;

public class HeaderAnalysisService
{
    private const long MaxFileSizeBytes = 50 * 1024 * 1024;
    private const string MsgTempFilePrefix = "ExchangeAdminWeb_";
    private const string MsgTempFilePattern = "ExchangeAdminWeb_*.msg";
    private static readonly TimeSpan StaleTempFileAge = TimeSpan.FromHours(4);
    private static readonly TimeSpan StaleTempFileSweepInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly object MsgTempSweepLock = new();
    private static DateTime _lastMsgTempSweepUtc = DateTime.MinValue;

    private static readonly Regex ReceivedFromRegex = new(@"from\s+([^\s]+)(?:\s+\(([^)]+)\))?", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex IpInParensRegex = new(@"\[?(\d+\.\d+\.\d+\.\d+)\]?", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex ReceivedByRegex = new(@"by\s+([^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex ReceivedViaRegex = new(@"via\s+([^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex ReceivedWithRegex = new(@"with\s+([^\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex ReceivedIdRegex = new(@"id\s+([^\s;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex ReceivedForRegex = new(@"for\s+<?([^>;\s]+)>?", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex ReceivedDateRegex = new(@";\s*(.+)$", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex SpfRegex = new(@"spf=(\w+)(?:\s+\(([^)]+)\))?", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex DkimRegex = new(@"dkim=(\w+)(?:\s+\(([^)]+)\))?", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex DmarcRegex = new(@"dmarc=(\w+)(?:\s+\(([^)]+)\))?", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex SmtpMailFromRegex = new(@"smtp\.mailfrom=([^;\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex ClientIpRegex = new(@"client-ip=([^;\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex HeaderDRegex = new(@"header\.d=([^;\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex SelectorRegex = new(@"selector=([^;\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex PolicyRegex = new(@"p=(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex HeaderFromRegex = new(@"header\.from=([^;\s]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex EmailRegex = new(@"[A-Z0-9._%+\-']+@[A-Z0-9.\-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex IPv4Regex = new(@"\[?(\d+\.\d+\.\d+\.\d+)\]?", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex IPv6Regex = new(@"\[?([0-9a-fA-F:]{2,39})\]?", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex SpamScoreRegex = new(@"score=([0-9.\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex BclRegex = new(@"BCL:(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex SclRegex = new(@"SCL:([0-9\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex DomainRegex = new(@"@([^>\s]+)", RegexOptions.Compiled, RegexTimeout);
    private static readonly Regex FailureCodeRegex = new(@"\b(4\.\d\.\d|5\.\d\.\d|550|554|451|452|421)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled, RegexTimeout);

    private static readonly string[] InterestingHeaderNames =
    {
        "X-Originating-IP", "X-Sender-IP", "X-Mailer", "User-Agent", "X-Forwarded-For", "X-Real-IP",
        "X-Priority", "Importance", "Reply-To", "Errors-To", "Bounces-To", "List-Unsubscribe", "Precedence",
        "X-MS-Exchange-Organization-SCL", "X-MS-Exchange-Organization-PCL", "X-MS-Exchange-Organization-BCL",
        "X-Forefront-Antispam-Report", "X-Microsoft-Antispam", "X-Microsoft-Antispam-Mailbox-Delivery"
    };

    private static readonly string[] SecurityHeaderNames =
    {
        "Authentication-Results", "DKIM-Signature", "ARC-Authentication-Results", "ARC-Message-Signature", "ARC-Seal",
        "X-Spam-Status", "X-Spam-Score", "X-Spam-Level", "X-Virus-Scanned", "X-Forefront-Antispam-Report", "X-Microsoft-Antispam"
    };

    private readonly ILogger<HeaderAnalysisService> _logger;

    public HeaderAnalysisService(ILogger<HeaderAnalysisService> logger)
    {
        _logger = logger;
    }

    public HeaderAnalysisResult AnalyzeHeaderText(string headerText)
    {
        var headers = new EmailHeaders { FileName = "Pasted Headers" };
        ParseRawHeaders(headerText, headers);
        return Analyze(headers);
    }

    public async Task<HeaderAnalysisResult> AnalyzeFileAsync(Stream stream, string fileName, long fileSize)
    {
        if (fileSize > MaxFileSizeBytes)
            throw new InvalidOperationException($"File size ({fileSize / (1024 * 1024)} MB) exceeds the maximum allowed size of {MaxFileSizeBytes / (1024 * 1024)} MB.");

        var extension = Path.GetExtension(fileName).ToLowerInvariant();
        var headers = extension switch
        {
            ".eml" => await ParseEmlAsync(stream, fileName),
            ".msg" => await ParseMsgAsync(stream, fileName),
            _ => throw new InvalidOperationException("Only .eml and .msg files are supported.")
        };

        return Analyze(headers);
    }

    private static async Task<EmailHeaders> ParseEmlAsync(Stream stream, string fileName)
    {
        var headers = new EmailHeaders { FileName = fileName };
        var message = await MimeMessage.LoadAsync(stream);

        headers.From = message.From?.ToString();
        headers.ReplyTo = message.ReplyTo?.ToString();
        headers.To = message.To?.ToString();
        headers.Cc = message.Cc?.ToString();
        headers.Subject = message.Subject;
        headers.MessageId = message.MessageId;
        headers.Date = message.Date.DateTime;

        foreach (var header in message.Headers)
        {
            headers.AddHeader(header.Field, header.Value);
            ProcessSpecialHeaders(header.Field, header.Value, headers);
        }

        headers.AddHeader("X-Has-Attachments", CountAttachments(message.Body) > 0 ? "Yes" : "No");
        headers.ContentType = message.Body?.ContentType?.ToString();
        return headers;
    }

    private async Task<EmailHeaders> ParseMsgAsync(Stream stream, string fileName)
    {
        SweepStaleMsgTempFiles();

        var tempPath = Path.Combine(Path.GetTempPath(), $"{MsgTempFilePrefix}{Guid.NewGuid():N}.msg");
        try
        {
            await using (var temp = File.Create(tempPath))
                await stream.CopyToAsync(temp);

            var headers = new EmailHeaders { FileName = fileName };
            using var message = new MsgReader.Outlook.Storage.Message(tempPath);

            headers.From = message.Sender?.Email ?? message.Sender?.DisplayName;
            headers.Subject = message.Subject;
            headers.MessageId = null;
            if (message.SentOn != null)
                headers.Date = message.SentOn.Value.DateTime;

            if (message.Recipients != null)
            {
                headers.To = string.Join("; ", message.Recipients
                    .Where(r => r.Type == MsgReader.Outlook.RecipientType.To)
                    .Select(r => r.Email ?? r.DisplayName)
                    .Where(s => !string.IsNullOrWhiteSpace(s)));

                headers.Cc = string.Join("; ", message.Recipients
                    .Where(r => r.Type == MsgReader.Outlook.RecipientType.Cc)
                    .Select(r => r.Email ?? r.DisplayName)
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            if (!string.IsNullOrWhiteSpace(message.TransportMessageHeaders))
                ParseRawHeaders(message.TransportMessageHeaders, headers);

            headers.AddHeader("X-Has-Attachments", message.Attachments?.Any() == true ? "Yes" : "No");
            var importance = message.ImportanceText;
            if (!string.IsNullOrWhiteSpace(importance))
                headers.AddHeader("Importance", importance);

            return headers;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse MSG upload {FileName}", fileName);
            throw new InvalidOperationException($"Failed to parse MSG file: {ex.Message}", ex);
        }
        finally
        {
            DeleteTempMsgFile(tempPath);
        }
    }

    private void SweepStaleMsgTempFiles()
    {
        lock (MsgTempSweepLock)
        {
            var now = DateTime.UtcNow;
            if (now - _lastMsgTempSweepUtc < StaleTempFileSweepInterval)
                return;
            _lastMsgTempSweepUtc = now;
        }

        try
        {
            var cutoff = DateTime.UtcNow - StaleTempFileAge;
            foreach (var path in Directory.EnumerateFiles(Path.GetTempPath(), MsgTempFilePattern))
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.LastWriteTimeUtc < cutoff)
                        DeleteTempMsgFile(path);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to inspect stale MSG temp file {Path}", path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate stale MSG temp files");
        }
    }

    private void DeleteTempMsgFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete MSG temp file {Path}", path);
        }
    }

    private HeaderAnalysisResult Analyze(EmailHeaders headers)
    {
        var result = new HeaderAnalysisResult
        {
            Headers = headers,
            From = headers.From,
            ReplyTo = headers.ReplyTo ?? headers.GetHeader("Reply-To"),
            Subject = headers.Subject,
            Date = headers.Date,
            MessageId = headers.MessageId,
            ReturnPath = headers.ReturnPath,
            HasAttachments = headers.GetHeader("X-Has-Attachments")?.Equals("Yes", StringComparison.OrdinalIgnoreCase) == true
        };

        result.To = SplitAddresses(headers.To);
        result.Cc = SplitAddresses(headers.Cc);
        PopulateAllHeaders(headers, result);
        AnalyzeRoutingPath(headers, result);
        AnalyzeAuthentication(headers, result);
        ExtractSpamScores(headers, result);
        FindInterestingHeaders(headers, result);
        ExtractOriginatingIP(headers, result);
        GenerateFailureFindings(headers, result);
        BuildTraceSuggestion(headers, result);
        return result;
    }

    private static void PopulateAllHeaders(EmailHeaders headers, HeaderAnalysisResult result)
    {
        foreach (var header in headers.Headers)
            result.AllHeaders[header.Key] = string.Join("; ", header.Value);
    }

    private static void AnalyzeRoutingPath(EmailHeaders headers, HeaderAnalysisResult result)
    {
        var sorted = headers.ReceivedHeaders.OrderByDescending(r => r.Order).ToList();
        result.HopCount = sorted.Count;
        DateTimeOffset? previousDate = null;
        var hopNumber = 1;

        foreach (var received in sorted)
        {
            TimeSpan? delay = null;
            if (previousDate.HasValue && received.Date.HasValue)
                delay = received.Date.Value - previousDate.Value;

            result.RoutingPath.Add(new RoutingHop
            {
                Hop = hopNumber++,
                From = received.From,
                By = received.By,
                With = received.With,
                Date = received.Date,
                DelayFromPrevious = delay,
                RawValue = received.RawValue
            });

            if (received.Date.HasValue)
                previousDate = received.Date.Value;
        }

        var firstDate = sorted.FirstOrDefault()?.Date;
        var lastDate = sorted.LastOrDefault()?.Date;
        if (firstDate.HasValue && lastDate.HasValue)
            result.DeliveryTime = lastDate.Value - firstDate.Value;
    }

    private static void AnalyzeAuthentication(EmailHeaders headers, HeaderAnalysisResult result)
    {
        if (headers.Authentication == null)
        {
            var authHeader = headers.GetHeader("Authentication-Results");
            if (!string.IsNullOrWhiteSpace(authHeader))
                ParseAuthenticationResults(authHeader, headers);
        }

        if (headers.Authentication == null)
        {
            AddFinding(result, "Warning", "Authentication", "No SPF, DKIM, or DMARC authentication results were found.", null);
            return;
        }

        result.SpfResult = headers.Authentication.SPF?.Result?.ToUpperInvariant() ?? "NONE";
        result.DkimResult = headers.Authentication.DKIM?.Result?.ToUpperInvariant() ?? "NONE";
        result.DmarcResult = headers.Authentication.DMARC?.Result?.ToUpperInvariant() ?? "NONE";

        AddAuthFinding(result, "SPF", result.SpfResult, headers.Authentication.SPF?.Domain ?? headers.Authentication.SPF?.ClientIp);
        AddAuthFinding(result, "DKIM", result.DkimResult, headers.Authentication.DKIM?.HeaderD ?? headers.Authentication.DKIM?.Selector);
        AddAuthFinding(result, "DMARC", result.DmarcResult, headers.Authentication.DMARC?.Domain ?? headers.Authentication.DMARC?.Policy);
    }

    private static void AddAuthFinding(HeaderAnalysisResult result, string mechanism, string value, string? evidence)
    {
        if (value == "PASS")
            return;

        var severity = value is "FAIL" or "PERMERROR" or "TEMPERROR" ? "High" : "Warning";
        AddFinding(result, severity, "Authentication", $"{mechanism} result is {value}.", evidence);
    }

    private static void ExtractSpamScores(EmailHeaders headers, HeaderAnalysisResult result)
    {
        AddSpamHeader(headers, result, "X-Spam-Status");
        AddSpamHeader(headers, result, "X-Spam-Score");
        AddSpamHeader(headers, result, "X-Spam-Level");
        AddSpamHeader(headers, result, "X-MS-Exchange-Organization-SCL");
        AddSpamHeader(headers, result, "X-MS-Exchange-Organization-BCL");

        var spamStatus = headers.GetHeader("X-Spam-Status");
        if (!string.IsNullOrWhiteSpace(spamStatus))
        {
            var scoreMatch = SpamScoreRegex.Match(spamStatus);
            if (scoreMatch.Success)
                result.SpamScores["Spam Score"] = scoreMatch.Groups[1].Value;
        }

        var msAntispam = headers.GetHeader("X-Microsoft-Antispam");
        if (!string.IsNullOrWhiteSpace(msAntispam))
        {
            var bclMatch = BclRegex.Match(msAntispam);
            if (bclMatch.Success)
                result.SpamScores["Bulk Complaint Level"] = bclMatch.Groups[1].Value;
        }

        var forefront = headers.GetHeader("X-Forefront-Antispam-Report");
        if (!string.IsNullOrWhiteSpace(forefront))
        {
            var sclMatch = SclRegex.Match(forefront);
            if (sclMatch.Success)
                result.SpamScores["Spam Confidence Level"] = sclMatch.Groups[1].Value;
        }
    }

    private static void AddSpamHeader(EmailHeaders headers, HeaderAnalysisResult result, string name)
    {
        var value = headers.GetHeader(name);
        if (!string.IsNullOrWhiteSpace(value))
            result.SpamScores[name] = Truncate(value, 240);
    }

    private static void FindInterestingHeaders(EmailHeaders headers, HeaderAnalysisResult result)
    {
        foreach (var headerName in InterestingHeaderNames)
        {
            var values = headers.GetHeaders(headerName);
            if (values == null)
                continue;

            foreach (var value in values.Where(v => !string.IsNullOrWhiteSpace(v)))
                result.InterestingHeaders.Add($"{headerName}: {Truncate(value, 240)}");
        }

        foreach (var header in headers.Headers)
        {
            if (!header.Key.StartsWith("X-", StringComparison.OrdinalIgnoreCase))
                continue;
            if (SecurityHeaderNames.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                continue;
            if (InterestingHeaderNames.Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                continue;

            var value = header.Value.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            if (!string.IsNullOrWhiteSpace(value))
                result.InterestingHeaders.Add($"{header.Key}: {Truncate(value, 160)}");
        }
    }

    private static void ExtractOriginatingIP(EmailHeaders headers, HeaderAnalysisResult result)
    {
        var originatingIp = headers.GetHeader("X-Originating-IP") ?? headers.GetHeader("X-Sender-IP");
        if (!string.IsNullOrWhiteSpace(originatingIp))
        {
            result.OriginatingIP = ExtractIP(originatingIp);
            if (!string.IsNullOrWhiteSpace(result.OriginatingIP))
                return;
        }

        var firstReceived = headers.ReceivedHeaders.OrderByDescending(r => r.Order).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(firstReceived?.From))
            result.OriginatingIP = ExtractIP(firstReceived.From);
    }

    private static void GenerateFailureFindings(EmailHeaders headers, HeaderAnalysisResult result)
    {
        if (string.IsNullOrWhiteSpace(result.MessageId))
            AddFinding(result, "Warning", "Traceability", "Message-ID is missing, which makes trace correlation harder.", null);

        if (result.DeliveryTime.HasValue && result.DeliveryTime.Value.TotalHours > 24)
            AddFinding(result, "Warning", "Routing", $"Unusual total delivery time: {result.DeliveryTime.Value.TotalHours:F1} hours.", null);

        foreach (var hop in result.RoutingPath.Where(h => h.DelayFromPrevious.HasValue && h.DelayFromPrevious.Value.TotalMinutes > 30))
            AddFinding(result, "Warning", "Routing", $"Long delay before hop {hop.Hop}: {hop.DelayFromPrevious!.Value:g}.", hop.RawValue);

        if (result.HopCount > 15)
            AddFinding(result, "Info", "Routing", $"High hop count detected: {result.HopCount} received headers.", null);

        var fromDomain = ExtractDomain(result.From);
        var returnDomain = ExtractDomain(result.ReturnPath);
        if (!string.IsNullOrWhiteSpace(fromDomain) && !string.IsNullOrWhiteSpace(returnDomain) && !fromDomain.Equals(returnDomain, StringComparison.OrdinalIgnoreCase))
            AddFinding(result, "Warning", "Identity", $"Return-Path domain ({returnDomain}) does not match From domain ({fromDomain}).", result.ReturnPath);

        var replyDomain = ExtractDomain(result.ReplyTo);
        if (!string.IsNullOrWhiteSpace(fromDomain) && !string.IsNullOrWhiteSpace(replyDomain) && !fromDomain.Equals(replyDomain, StringComparison.OrdinalIgnoreCase))
            AddFinding(result, "Info", "Identity", $"Reply-To domain ({replyDomain}) differs from From domain ({fromDomain}).", result.ReplyTo);

        foreach (var score in result.SpamScores)
        {
            if (score.Key.Contains("SCL", StringComparison.OrdinalIgnoreCase) && int.TryParse(score.Value, out var scl) && scl >= 5)
                AddFinding(result, "High", "Filtering", $"Spam Confidence Level is {scl}.", score.Key);
            if (score.Key.Contains("BCL", StringComparison.OrdinalIgnoreCase) && int.TryParse(score.Value, out var bcl) && bcl >= 7)
                AddFinding(result, "Warning", "Filtering", $"Bulk Complaint Level is {bcl}.", score.Key);
        }

        ScanHeadersForFailureTerms(headers, result);

        if (result.HasAttachments)
            AddFinding(result, "Info", "Content", "Message has attachments.", null);
    }

    private static void ScanHeadersForFailureTerms(EmailHeaders headers, HeaderAnalysisResult result)
    {
        foreach (var header in headers.Headers)
        {
            foreach (var value in header.Value)
            {
                var sample = Truncate(value, 320);
                var codeMatch = FailureCodeRegex.Match(sample);
                if (codeMatch.Success)
                    AddFinding(result, "High", "Failure clue", $"SMTP-style failure code found in {header.Key}: {codeMatch.Value}.", sample);

                if (ContainsAny(sample, "reject", "rejected", "blocked", "blacklist", "deny", "denied", "quarantine"))
                    AddFinding(result, "High", "Failure clue", $"Blocking or quarantine language found in {header.Key}.", sample);
                else if (ContainsAny(sample, "defer", "deferred", "timeout", "throttle", "tls", "certificate", "tempfail"))
                    AddFinding(result, "Warning", "Failure clue", $"Delivery delay or transport issue language found in {header.Key}.", sample);
                else if (ContainsAny(sample, "malware", "phish", "spoof", "spam"))
                    AddFinding(result, "Warning", "Security clue", $"Security filtering language found in {header.Key}.", sample);
            }
        }
    }

    private static void BuildTraceSuggestion(EmailHeaders headers, HeaderAnalysisResult result)
    {
        var date = result.Date ?? DateTime.Today;
        result.TraceSuggestion = new HeaderTraceSuggestion
        {
            Sender = ExtractEmail(result.From) ?? ExtractEmail(result.ReturnPath),
            Recipient = result.To.Select(ExtractEmail).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e)),
            Subject = result.Subject,
            MessageId = result.MessageId?.Trim(),
            StartDate = date.AddDays(-2).Date,
            EndDate = date.AddDays(2).Date
        };

        if (string.IsNullOrWhiteSpace(result.TraceSuggestion.Recipient))
        {
            var receivedFor = headers.ReceivedHeaders.Select(h => ExtractEmail(h.For)).FirstOrDefault(e => !string.IsNullOrWhiteSpace(e));
            result.TraceSuggestion.Recipient = receivedFor;
        }
    }

    private static void ParseRawHeaders(string headerText, EmailHeaders headers)
    {
        if (string.IsNullOrWhiteSpace(headerText))
            return;

        var lines = headerText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        string? currentHeader = null;
        var currentValue = string.Empty;

        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                if (currentHeader != null)
                {
                    headers.AddHeader(currentHeader, currentValue);
                    ProcessSpecialHeaders(currentHeader, currentValue, headers);
                    currentHeader = null;
                    currentValue = string.Empty;
                }
                break;
            }

            if (line[0] == ' ' || line[0] == '\t')
            {
                if (currentHeader != null)
                    currentValue += " " + line.Trim();
                continue;
            }

            if (currentHeader != null)
            {
                headers.AddHeader(currentHeader, currentValue);
                ProcessSpecialHeaders(currentHeader, currentValue, headers);
            }

            var colonIndex = line.IndexOf(':');
            if (colonIndex > 0)
            {
                currentHeader = line[..colonIndex].Trim();
                currentValue = line[(colonIndex + 1)..].Trim();
            }
            else
            {
                currentHeader = null;
                currentValue = string.Empty;
            }
        }

        if (currentHeader != null)
        {
            headers.AddHeader(currentHeader, currentValue);
            ProcessSpecialHeaders(currentHeader, currentValue, headers);
        }
    }

    private static void ProcessSpecialHeaders(string headerName, string headerValue, EmailHeaders headers)
    {
        switch (headerName.ToLowerInvariant())
        {
            case "from":
                headers.From ??= headerValue;
                break;
            case "to":
                headers.To ??= headerValue;
                break;
            case "cc":
                headers.Cc ??= headerValue;
                break;
            case "subject":
                headers.Subject ??= headerValue;
                break;
            case "message-id":
                headers.MessageId ??= headerValue;
                break;
            case "reply-to":
                headers.ReplyTo ??= headerValue;
                break;
            case "return-path":
                headers.ReturnPath = headerValue;
                break;
            case "content-type":
                headers.ContentType = headerValue;
                break;
            case "date":
                if (headers.Date == null && DateTimeOffset.TryParse(headerValue, out var dateOffset))
                    headers.Date = dateOffset.DateTime;
                break;
            case "received":
                ParseReceivedHeader(headerValue, headers.ReceivedHeaders.Count, headers);
                break;
            case "authentication-results":
            case "arc-authentication-results":
                ParseAuthenticationResults(headerValue, headers);
                break;
            case "x-originating-ip":
                var ipMatch = IPv4Regex.Match(headerValue);
                if (ipMatch.Success)
                    headers.AddHeader("X-Originating-IP-Extracted", ipMatch.Groups[1].Value);
                break;
        }
    }

    private static void ParseReceivedHeader(string value, int order, EmailHeaders headers)
    {
        var received = new ReceivedHeader { RawValue = value, Order = order };
        var truncated = value.Length > 8192 ? value[..8192] : value;

        var fromMatch = ReceivedFromRegex.Match(truncated);
        if (fromMatch.Success)
        {
            received.From = fromMatch.Groups[1].Value;
            if (fromMatch.Groups[2].Success)
            {
                var ipInParens = IpInParensRegex.Match(fromMatch.Groups[2].Value);
                if (ipInParens.Success)
                    received.From += $" [{ipInParens.Groups[1].Value}]";
            }
        }

        var byMatch = ReceivedByRegex.Match(truncated);
        if (byMatch.Success)
            received.By = byMatch.Groups[1].Value;

        var viaMatch = ReceivedViaRegex.Match(truncated);
        if (viaMatch.Success)
            received.Via = viaMatch.Groups[1].Value;

        var withMatch = ReceivedWithRegex.Match(truncated);
        if (withMatch.Success)
            received.With = withMatch.Groups[1].Value;

        var idMatch = ReceivedIdRegex.Match(truncated);
        if (idMatch.Success)
            received.Id = idMatch.Groups[1].Value;

        var forMatch = ReceivedForRegex.Match(truncated);
        if (forMatch.Success)
            received.For = forMatch.Groups[1].Value;

        var dateMatch = ReceivedDateRegex.Match(truncated);
        if (dateMatch.Success && DateTimeOffset.TryParse(dateMatch.Groups[1].Value, out var date))
            received.Date = date;

        headers.ReceivedHeaders.Add(received);
    }

    private static void ParseAuthenticationResults(string value, EmailHeaders headers)
    {
        headers.Authentication ??= new AuthenticationResults
        {
            SPF = new SpfResult(),
            DKIM = new DkimResult(),
            DMARC = new DmarcResult()
        };
        headers.Authentication.RawValue = value;

        var truncated = value.Length > 8192 ? value[..8192] : value;

        var spfMatch = SpfRegex.Match(truncated);
        if (spfMatch.Success && headers.Authentication.SPF!.Result == "none")
        {
            headers.Authentication.SPF.Result = spfMatch.Groups[1].Value.ToLowerInvariant();
            var domainMatch = SmtpMailFromRegex.Match(truncated);
            if (domainMatch.Success)
                headers.Authentication.SPF.Domain = domainMatch.Groups[1].Value;
            var clientIpMatch = ClientIpRegex.Match(truncated);
            if (clientIpMatch.Success)
                headers.Authentication.SPF.ClientIp = clientIpMatch.Groups[1].Value;
        }

        var dkimMatch = DkimRegex.Match(truncated);
        if (dkimMatch.Success && headers.Authentication.DKIM!.Result == "none")
        {
            headers.Authentication.DKIM.Result = dkimMatch.Groups[1].Value.ToLowerInvariant();
            var domainMatch = HeaderDRegex.Match(truncated);
            if (domainMatch.Success)
                headers.Authentication.DKIM.HeaderD = domainMatch.Groups[1].Value;
            var selectorMatch = SelectorRegex.Match(truncated);
            if (selectorMatch.Success)
                headers.Authentication.DKIM.Selector = selectorMatch.Groups[1].Value;
        }

        var dmarcMatch = DmarcRegex.Match(truncated);
        if (dmarcMatch.Success && headers.Authentication.DMARC!.Result == "none")
        {
            headers.Authentication.DMARC.Result = dmarcMatch.Groups[1].Value.ToLowerInvariant();
            var policyMatch = PolicyRegex.Match(truncated);
            if (policyMatch.Success)
                headers.Authentication.DMARC.Policy = policyMatch.Groups[1].Value;
            var domainMatch = HeaderFromRegex.Match(truncated);
            if (domainMatch.Success)
                headers.Authentication.DMARC.Domain = domainMatch.Groups[1].Value;
        }
    }

    private static int CountAttachments(MimeEntity? entity)
    {
        if (entity == null)
            return 0;

        if (entity is Multipart multipart)
            return multipart.Sum(CountAttachments);

        return entity is MimePart part && part.IsAttachment ? 1 : 0;
    }

    private static List<string> SplitAddresses(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? new List<string>()
            : value.Split(',', ';').Select(v => v.Trim()).Where(v => !string.IsNullOrWhiteSpace(v)).ToList();

    private static string? ExtractIP(string value)
    {
        var ipv4Match = IPv4Regex.Match(value);
        if (ipv4Match.Success)
            return ipv4Match.Groups[1].Value;

        var ipv6Match = IPv6Regex.Match(value);
        if (ipv6Match.Success && ipv6Match.Groups[1].Value.Contains(':'))
            return ipv6Match.Groups[1].Value;

        return null;
    }

    private static string? ExtractEmail(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var match = EmailRegex.Match(value);
        return match.Success ? match.Value : null;
    }

    private static string? ExtractDomain(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return null;

        var match = DomainRegex.Match(email);
        return match.Success ? match.Groups[1].Value.TrimEnd('>', ';', ',') : null;
    }

    private static string? NormalizeMessageId(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return null;

        return messageId.Trim().Trim('<', '>');
    }

    private static void AddFinding(HeaderAnalysisResult result, string severity, string category, string message, string? evidence)
    {
        if (result.Findings.Any(f => f.Severity == severity && f.Category == category && f.Message == message && f.Evidence == evidence))
            return;

        result.Findings.Add(new HeaderFinding
        {
            Severity = severity,
            Category = category,
            Message = message,
            Evidence = evidence
        });
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";
}



