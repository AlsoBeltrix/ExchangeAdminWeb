using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Coverage for the Message Analysis detail-export email path after the delivery redesign
/// (docs/MessageTraceDownloadLink-Plan.md slice 3): the mail carries a LINK to the Downloadable
/// Reports page, never the export itself, so an arbitrary recipient cannot receive trace data.
///
/// The SMTP-free seams are the recipient normalizer, the URL resolver, and the two body builders.
/// </summary>
public sealed class EmailServiceMessageTraceTests
{
    private static EmailService Create(string? publicBaseUrl = null, string? appName = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Email:AdminNotificationEmail"] = "admin@contoso.com",
        };
        if (publicBaseUrl is not null)
            settings["Application:PublicBaseUrl"] = publicBaseUrl;
        if (appName is not null)
            settings["Application:Name"] = appName;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return new EmailService(config, NullLogger<EmailService>.Instance);
    }

    // -------------------------------------------------------------------------
    // Recipients
    // -------------------------------------------------------------------------

    [Fact]
    public void Recipients_TrimsAndDropsBlanks()
    {
        var recipients = EmailService.NormalizeRecipients(["  user@contoso.com  ", "", "   ", null]);

        Assert.Equal(["user@contoso.com"], recipients);
    }

    [Fact]
    public void Recipients_DeduplicatesCaseInsensitively()
    {
        var recipients = EmailService.NormalizeRecipients(["User@Contoso.com", "user@contoso.com", "b@contoso.com"]);

        Assert.Equal(2, recipients.Count);
        Assert.Contains("b@contoso.com", recipients);
    }

    [Fact]
    public void Recipients_NullOrEmpty_IsEmpty()
    {
        Assert.Empty(EmailService.NormalizeRecipients(null));
        Assert.Empty(EmailService.NormalizeRecipients([]));
    }

    /// <summary>
    /// The security-relevant half of this slice: the admin address is configured, and the normalizer
    /// still must not introduce it. Before the redesign the resolver merged admins in deliberately,
    /// because the zip travelled in the mail and admins were an intended archive. Now the export is
    /// trace data behind a login gate and the owner ruled admins must not receive the results, so
    /// nothing may add a recipient the caller did not ask for.
    /// </summary>
    [Fact]
    public void Recipients_NeverAddsTheConfiguredAdmin()
    {
        var recipients = EmailService.NormalizeRecipients(["user@contoso.com"]);

        Assert.Equal(["user@contoso.com"], recipients);
        Assert.DoesNotContain("admin@contoso.com", recipients);
    }

    // -------------------------------------------------------------------------
    // Recipient input parsing (slice 4, plan D4)
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("a@contoso.com,b@contoso.com")]
    [InlineData("a@contoso.com; b@contoso.com")]
    [InlineData(" a@contoso.com , b@contoso.com ")]
    public void ParseRecipientInput_SplitsOnCommaAndSemicolonAndTrims(string input)
    {
        var parsed = EmailService.ParseRecipientInput(input, out var invalid);

        Assert.Equal(["a@contoso.com", "b@contoso.com"], parsed);
        Assert.Empty(invalid);
    }

    /// <summary>
    /// D4: "A box the operator has cleared is a valid submission." An empty result must come back
    /// clean - no invalid entries - so the page cannot be tempted into a required-field error.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData(",  ;  ,")]
    public void ParseRecipientInput_ClearedBox_IsValidAndEmpty(string? input)
    {
        var parsed = EmailService.ParseRecipientInput(input, out var invalid);

        Assert.Empty(parsed);
        Assert.Empty(invalid);
    }

    [Theory]
    [InlineData("not-an-address")]
    [InlineData("@contoso.com")]
    [InlineData("user@")]
    [InlineData("user@contoso")]
    [InlineData("user@@contoso.com")]
    [InlineData("user@.com")]
    [InlineData("user@contoso.")]
    public void ParseRecipientInput_ReportsMalformedAddresses(string input)
    {
        var parsed = EmailService.ParseRecipientInput(input, out var invalid);

        Assert.Empty(parsed);
        Assert.Equal([input], invalid);
    }

    [Fact]
    public void ParseRecipientInput_SeparatesTheGoodFromTheBad()
    {
        var parsed = EmailService.ParseRecipientInput("good@contoso.com, bogus, other@contoso.com", out var invalid);

        Assert.Equal(["good@contoso.com", "other@contoso.com"], parsed);
        Assert.Equal(["bogus"], invalid);
    }

    /// <summary>
    /// What the box accepts must be exactly what the send uses, or the page validates one set and
    /// the processor mails another.
    /// </summary>
    [Fact]
    public void ParseRecipientInput_DeduplicatesLikeTheNormalizer()
    {
        var parsed = EmailService.ParseRecipientInput("User@Contoso.com, user@contoso.com", out var invalid);

        Assert.Equal(["User@Contoso.com"], parsed);
        Assert.Empty(invalid);
    }

    /// <summary>
    /// D4 forbids domain allow-listing: the mail carries a login-gated link, not the data, so an
    /// external address is a legitimate submission.
    /// </summary>
    [Fact]
    public void ParseRecipientInput_DoesNotRestrictTheDomain()
    {
        var parsed = EmailService.ParseRecipientInput("someone@external-partner.example", out var invalid);

        Assert.Equal(["someone@external-partner.example"], parsed);
        Assert.Empty(invalid);
    }

    [Fact]
    public void ParseRecipientInput_NeverAddsTheConfiguredAdmin()
    {
        var parsed = EmailService.ParseRecipientInput("user@contoso.com", out _);

        Assert.DoesNotContain("admin@contoso.com", parsed);
    }

    // -------------------------------------------------------------------------
    // Reports URL (openreview F3)
    // -------------------------------------------------------------------------

    [Fact]
    public void ReportsUrl_WhenBaseUrlSet_IsAbsolute()
    {
        var url = Create("https://apps.contoso.com/ExchangeAdminWeb").ResolveReportsUrl();

        Assert.Equal("https://apps.contoso.com/ExchangeAdminWeb/message-analysis/reports", url);
    }

    [Fact]
    public void ReportsUrl_TrailingSlash_DoesNotDoubleUp()
    {
        var url = Create("https://apps.contoso.com/ExchangeAdminWeb/").ResolveReportsUrl();

        Assert.Equal("https://apps.contoso.com/ExchangeAdminWeb/message-analysis/reports", url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReportsUrl_WhenBaseUrlUnset_IsNull(string? baseUrl)
    {
        Assert.Null(Create(baseUrl).ResolveReportsUrl());
    }

    /// <summary>
    /// A relative value is treated exactly like an unset one. An email client has no origin to
    /// resolve it against, so emitting it would produce a dead hyperlink - worse than no link.
    /// </summary>
    [Theory]
    [InlineData("/ExchangeAdminWeb")]
    [InlineData("ExchangeAdminWeb")]
    public void ReportsUrl_WhenBaseUrlIsRelative_IsNull(string baseUrl)
    {
        Assert.Null(Create(baseUrl).ResolveReportsUrl());
    }

    // -------------------------------------------------------------------------
    // Ready body
    // -------------------------------------------------------------------------

    [Fact]
    public void ReadyBody_WithUrl_LinksToTheReportsPage_AndStatesTheExpiryDate()
    {
        var body = EmailService.BuildMessageTraceReadyBody(
            "https://apps.contoso.com/ExchangeAdminWeb/message-analysis/reports",
            "IT Admin Portal", 12, "INC42", "jdoe",
            new DateTime(2026, 8, 28, 0, 0, 0, DateTimeKind.Utc), "2026-07-29 10:00:00");

        Assert.Contains("href=\"https://apps.contoso.com/ExchangeAdminWeb/message-analysis/reports\"", body);
        Assert.Contains("2026-08-28", body);
        Assert.Contains("INC42", body);
    }

    /// <summary>The mail must not claim an attachment it no longer carries.</summary>
    [Fact]
    public void ReadyBody_DoesNotMentionAnAttachedZip()
    {
        var body = EmailService.BuildMessageTraceReadyBody(
            "https://apps.contoso.com/message-analysis/reports", "IT Admin Portal", 1, "INC1", "jdoe",
            DateTime.UtcNow, "2026-07-29 10:00:00");

        Assert.DoesNotContain("zip", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("attached as", body, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// F3: with no base URL, the body carries prose and NO hyperlink at all - specifically not a
    /// bare relative path, which is what the first draft would have emitted.
    /// </summary>
    [Fact]
    public void ReadyBody_WithoutUrl_HasNoHrefAndNoRelativePath_ButNamesTheApp()
    {
        var body = EmailService.BuildMessageTraceReadyBody(
            null, "IT Admin Portal", 3, "INC7", "jdoe", DateTime.UtcNow, "2026-07-29 10:00:00");

        Assert.DoesNotContain("href", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/message-analysis/reports", body);
        Assert.Contains("IT Admin Portal", body);
        Assert.Contains("Downloadable Reports", body);
    }

    [Fact]
    public void ReadyBody_HtmlEncodesInterpolatedValues()
    {
        var body = EmailService.BuildMessageTraceReadyBody(
            null, "IT Admin Portal", 1, "<script>alert(1)</script>", "jdoe",
            DateTime.UtcNow, "2026-07-29 10:00:00");

        Assert.DoesNotContain("<script>", body);
        Assert.Contains("&lt;script&gt;", body);
    }

    [Fact]
    public void ReadyBody_TellsTheOperatorATicketWillBeRequired()
    {
        var body = EmailService.BuildMessageTraceReadyBody(
            "https://apps.contoso.com/message-analysis/reports", "IT Admin Portal", 1, "INC1", "jdoe",
            DateTime.UtcNow, "2026-07-29 10:00:00");

        Assert.Contains("ticket number", body, StringComparison.OrdinalIgnoreCase);
    }

    // -------------------------------------------------------------------------
    // Failure body (openreview F1)
    // -------------------------------------------------------------------------

    /// <summary>
    /// The failure notice must not link anywhere: there is no file to download. A link here would
    /// land the operator on a row rendered Failed, which is confusing at best.
    /// </summary>
    [Fact]
    public void FailureBody_SaysItCouldNotBeStored_AndLinksNowhere()
    {
        var body = EmailService.BuildMessageTraceFailureBody(5, "INC9", "jdoe", "2026-07-29 10:00:00");

        Assert.Contains("not be stored", body);
        Assert.Contains("re-run", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("href", body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("INC9", body);
    }

    [Fact]
    public void FailureBody_HtmlEncodesInterpolatedValues()
    {
        var body = EmailService.BuildMessageTraceFailureBody(1, "<b>x</b>", "jdoe", "2026-07-29 10:00:00");

        Assert.DoesNotContain("<b>x</b>", body);
        Assert.Contains("&lt;b&gt;", body);
    }
}
