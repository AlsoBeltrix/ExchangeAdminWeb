using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Slice-4 coverage for the Message Analysis detail-export email path
/// (docs/MessageTraceDetail-Plan.md). The only SMTP-free, testable unit is the
/// recipient resolver, which enforces the owner exfiltration rule: the export may
/// go only to the logged-in user's address plus the configured admins, never to an
/// operator-typed address.
/// </summary>
public sealed class EmailServiceMessageTraceTests
{
    [Fact]
    public void ResolveRecipients_IncludesUserAndAdmins()
    {
        var recipients = EmailService.ResolveMessageTraceRecipients(
            "user@contoso.com", "admin1@contoso.com,admin2@contoso.com");

        Assert.Equal(3, recipients.Count);
        Assert.Contains("user@contoso.com", recipients);
        Assert.Contains("admin1@contoso.com", recipients);
        Assert.Contains("admin2@contoso.com", recipients);
    }

    [Fact]
    public void ResolveRecipients_TrimsAndDropsBlankAdminEntries()
    {
        var recipients = EmailService.ResolveMessageTraceRecipients(
            "  user@contoso.com  ", " admin@contoso.com , , ");

        Assert.Equal(2, recipients.Count);
        Assert.Contains("user@contoso.com", recipients);
        Assert.Contains("admin@contoso.com", recipients);
    }

    [Fact]
    public void ResolveRecipients_DeduplicatesCaseInsensitively()
    {
        // User is also an admin: appears once.
        var recipients = EmailService.ResolveMessageTraceRecipients(
            "User@Contoso.com", "user@contoso.com,admin@contoso.com");

        Assert.Equal(2, recipients.Count);
        Assert.Contains("admin@contoso.com", recipients);
        Assert.Single(recipients, r => string.Equals(r, "user@contoso.com", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ResolveRecipients_NoUser_FallsBackToAdminsOnly(string? userEmail)
    {
        var recipients = EmailService.ResolveMessageTraceRecipients(userEmail, "admin@contoso.com");

        Assert.Equal(new[] { "admin@contoso.com" }, recipients);
    }

    [Fact]
    public void ResolveRecipients_NoUserNoAdmin_IsEmpty()
    {
        Assert.Empty(EmailService.ResolveMessageTraceRecipients(null, ""));
    }

    [Fact]
    public void ResolveRecipients_AdminsBlankButUserPresent_ReturnsUser()
    {
        var recipients = EmailService.ResolveMessageTraceRecipients("user@contoso.com", "");

        Assert.Equal(new[] { "user@contoso.com" }, recipients);
    }
}
