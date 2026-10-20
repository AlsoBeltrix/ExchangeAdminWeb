using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit findings A21 and A22: an ARC pass masked a real authentication failure, and the two
/// spam-score findings never fired at all.
/// </summary>
/// <remarks>
/// Behaviour, through the real <c>AnalyzeHeaderText</c> entry point - raw header text in,
/// analysed result out. Nothing is stubbed; this is the same path the page uses.
/// </remarks>
public class HeaderAnalysisMaskingTests
{
    private static HeaderAnalysisService Service() =>
        new(Substitute.For<ILogger<HeaderAnalysisService>>());

    // ---- A21: ARC must not mask the receiving system's own verdict ---------------------------

    [Fact]
    public void AnArcPassAboveARealFailure_DoesNotHideTheFailure()
    {
        // The order is the point. An intermediary PREPENDS its ARC block, so it sits above the
        // receiving system's Authentication-Results - and the parser kept the first value it
        // saw, so spf=fail underneath was discarded.
        var raw = """
ARC-Authentication-Results: i=1; mx.relay.example; spf=pass smtp.mailfrom=example.com; dkim=pass header.d=example.com; dmarc=pass header.from=example.com
Authentication-Results: mx.analog.com; spf=fail smtp.mailfrom=example.com; dkim=fail header.d=example.com; dmarc=fail header.from=example.com
From: Alice Sender <alice@example.com>
To: Bob Recipient <bob@analog.com>
Subject: Invoice
Message-ID: <masked@example.com>

body
""";

        var result = Service().AnalyzeHeaderText(raw);

        Assert.Equal("FAIL", result.SpfResult);
        Assert.Equal("FAIL", result.DkimResult);
        Assert.Equal("FAIL", result.DmarcResult);
    }

    [Fact]
    public void ArcStillFillsWhatTheDirectHeaderDidNotSay()
    {
        // ARC is not discarded - it is only prevented from overruling. With no direct header
        // at all, its values are the best available and must still be reported.
        var raw = """
ARC-Authentication-Results: i=1; mx.relay.example; spf=pass smtp.mailfrom=example.com
From: Alice Sender <alice@example.com>
To: Bob Recipient <bob@analog.com>
Subject: Invoice
Message-ID: <arconly@example.com>

body
""";

        var result = Service().AnalyzeHeaderText(raw);

        Assert.Equal("PASS", result.SpfResult);
    }

    [Fact]
    public void ADirectHeaderBelowArc_IsStillReadWhenItAgrees()
    {
        // Guards against "fixed" by simply ignoring ARC and breaking the ordinary case.
        var raw = """
ARC-Authentication-Results: i=1; mx.relay.example; spf=pass smtp.mailfrom=example.com
Authentication-Results: mx.analog.com; spf=pass smtp.mailfrom=example.com
From: Alice Sender <alice@example.com>
To: Bob Recipient <bob@analog.com>
Subject: Invoice
Message-ID: <agree@example.com>

body
""";

        var result = Service().AnalyzeHeaderText(raw);

        Assert.Equal("PASS", result.SpfResult);
    }

    [Fact]
    public void AnUpstreamDirectHeaderBelowTheReceivingSystems_DoesNotOverrideIt()
    {
        // Review of aud-a21 caught this as a regression the first fix introduced. Every hop
        // may add its own Authentication-Results and the TOPMOST is the receiving system's -
        // so making each direct header overwrite just moved the masking, letting a lower
        // upstream pass beat the tenant's own fail.
        var raw = """
Authentication-Results: mx.analog.com; spf=fail smtp.mailfrom=example.com; dkim=fail header.d=example.com; dmarc=fail header.from=example.com
Authentication-Results: relay.upstream.example; spf=pass smtp.mailfrom=example.com; dkim=pass header.d=example.com; dmarc=pass header.from=example.com
From: Alice Sender <alice@example.com>
To: Bob Recipient <bob@analog.com>
Subject: Invoice
Message-ID: <twodirect@example.com>

body
""";

        var result = Service().AnalyzeHeaderText(raw);

        Assert.Equal("FAIL", result.SpfResult);
        Assert.Equal("FAIL", result.DkimResult);
        Assert.Equal("FAIL", result.DmarcResult);
    }

    // ---- A22: the spam-score thresholds never matched ----------------------------------------

    [Fact]
    public void AHighSpamConfidenceLevel_RaisesItsFinding()
    {
        // The parser stores the key as "Spam Confidence Level", and the threshold tested for
        // the substring "SCL" - which that name does not contain. The finding never fired.
        var raw = """
From: Alice Sender <alice@example.com>
To: Bob Recipient <bob@analog.com>
Subject: Cheap pills
Message-ID: <spammy@example.com>
X-MS-Exchange-Organization-SCL: 9

body
""";

        var result = Service().AnalyzeHeaderText(raw);

        Assert.Contains(result.Findings, f =>
            f.Category == "Filtering" && f.Message.Contains("Spam Confidence Level is 9", StringComparison.Ordinal));
    }

    [Fact]
    public void AHighBulkComplaintLevel_RaisesItsFinding()
    {
        var raw = """
From: Alice Sender <alice@example.com>
To: Bob Recipient <bob@analog.com>
Subject: Newsletter
Message-ID: <bulky@example.com>
X-Microsoft-Antispam: BCL:8

body
""";

        var result = Service().AnalyzeHeaderText(raw);

        Assert.Contains(result.Findings, f =>
            f.Category == "Filtering" && f.Message.Contains("Bulk Complaint Level is 8", StringComparison.Ordinal));
    }

    [Fact]
    public void ScoresBelowTheThreshold_RaiseNothing()
    {
        // Without this, a fix that always raised the finding would satisfy the two above.
        var raw = """
From: Alice Sender <alice@example.com>
To: Bob Recipient <bob@analog.com>
Subject: Ordinary mail
Message-ID: <clean@example.com>
X-MS-Exchange-Organization-SCL: 1
X-Microsoft-Antispam: BCL:0

body
""";

        var result = Service().AnalyzeHeaderText(raw);

        Assert.DoesNotContain(result.Findings, f => f.Category == "Filtering");
    }
}
