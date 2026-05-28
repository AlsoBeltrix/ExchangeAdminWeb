using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class HeaderAnalysisServiceTests
{
    private static HeaderAnalysisService CreateService() =>
        new(Substitute.For<ILogger<HeaderAnalysisService>>());

    [Fact]
    public void AnalyzeHeaderText_ExtractsTraceSuggestion()
    {
        var service = CreateService();
        var raw = """
From: Alice Sender <alice@example.com>
To: Bob Recipient <bob@analog.com>
Subject: Quarterly report
Date: Tue, 26 May 2026 10:00:00 -0400
Message-ID: <abc123@example.com>
Authentication-Results: mx.example.com; spf=pass smtp.mailfrom=example.com; dkim=pass header.d=example.com; dmarc=pass header.from=example.com
Received: from mail.example.com (mail.example.com [203.0.113.10]) by mx.analog.com with ESMTP id one; Tue, 26 May 2026 10:00:02 -0400

body
""";

        var result = service.AnalyzeHeaderText(raw);

        Assert.Equal("<abc123@example.com>", result.TraceSuggestion.MessageId);
        Assert.Equal("alice@example.com", result.TraceSuggestion.Sender);
        Assert.Equal("bob@analog.com", result.TraceSuggestion.Recipient);
        Assert.Equal("Quarterly report", result.TraceSuggestion.Subject);
        Assert.Equal("PASS", result.SpfResult);
        Assert.Single(result.RoutingPath);
    }

    [Fact]
    public void AnalyzeHeaderText_FlagsAuthenticationFailures()
    {
        var service = CreateService();
        var raw = """
From: Spoof <spoof@example.net>
To: Target <target@analog.com>
Subject: Auth failure
Message-ID: <fail@example.net>
Authentication-Results: mx; spf=fail smtp.mailfrom=example.net; dkim=fail header.d=example.net; dmarc=fail header.from=example.net

body
""";

        var result = service.AnalyzeHeaderText(raw);

        Assert.Contains(result.Findings, f => f.Category == "Authentication" && f.Severity == "High" && f.Message.Contains("SPF"));
        Assert.Contains(result.Findings, f => f.Category == "Authentication" && f.Severity == "High" && f.Message.Contains("DKIM"));
        Assert.Contains(result.Findings, f => f.Category == "Authentication" && f.Severity == "High" && f.Message.Contains("DMARC"));
    }

    [Fact]
    public void AnalyzeHeaderText_FindsDeliveryFailureClues()
    {
        var service = CreateService();
        var raw = """
From: sender@example.com
To: recipient@analog.com
Subject: Delivery failure
Message-ID: <bounce@example.com>
X-Failed-Recipients: recipient@analog.com
Diagnostic-Code: smtp; 550 5.7.1 Message rejected by policy and quarantined

body
""";

        var result = service.AnalyzeHeaderText(raw);

        Assert.Contains(result.Findings, f => f.Category == "Failure clue" && f.Message.Contains("550"));
        Assert.Contains(result.Findings, f => f.Category == "Failure clue" && f.Message.Contains("Blocking or quarantine"));
    }

    [Fact]
    public void AnalyzeHeaderText_DoesNotDuplicateLastHeaderAtBodySeparator()
    {
        var service = CreateService();
        var raw = """
From: sender@example.com
Message-ID: <one@example.com>
Received: from a by b; Tue, 26 May 2026 10:00:00 -0400

body
""";

        var result = service.AnalyzeHeaderText(raw);

        Assert.Single(result.Headers.GetHeaders("Received")!);
        Assert.Single(result.RoutingPath);
    }
}

