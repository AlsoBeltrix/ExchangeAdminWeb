using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// sweep-d item 32. Address lists were split on commas only, so a semicolon list - the separator Outlook uses - was one
/// malformed address and Emergency Disable reported "SECURITY TEAM NOT NOTIFIED". Every list EmailService sends to, and
/// the administrators' list Migration hands to Exchange, is split by one rule that takes both.
/// </summary>
public class EmailAddressListTests
{
    [Theory]
    [InlineData("soc@contoso.test", new[] { "soc@contoso.test" })]
    [InlineData("soc@contoso.test,ir@contoso.test", new[] { "soc@contoso.test", "ir@contoso.test" })]
    [InlineData("soc@contoso.test;ir@contoso.test", new[] { "soc@contoso.test", "ir@contoso.test" })]
    [InlineData(" soc@contoso.test ; ir@contoso.test , ciso@contoso.test ", new[] { "soc@contoso.test", "ir@contoso.test", "ciso@contoso.test" })]
    [InlineData("soc@contoso.test;;,", new[] { "soc@contoso.test" })]
    [InlineData("soc@contoso.test;SOC@contoso.test", new[] { "soc@contoso.test" })]
    [InlineData(" ; , ", new string[0])]
    [InlineData("", new string[0])]
    [InlineData(null, new string[0])]
    public void ALists_AddressesAreSplitOnCommasAndSemicolons(string? list, string[] expected)
    {
        Assert.Equal(expected, EmailService.SplitAddresses(list));
    }

    [Theory]
    [InlineData("admins@contoso.test;oncall@contoso.test")]
    [InlineData("admins@contoso.test, oncall@contoso.test")]
    public void TheAdministratorsList_TakesEitherSeparator(string configured)
    {
        var email = new EmailService(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Email:AdminNotificationEmail"] = configured,
            }).Build(),
            NullLogger<EmailService>.Instance);

        Assert.Equal(["admins@contoso.test", "oncall@contoso.test"], email.AdminRecipients);
    }

    [Fact]
    public void EverySendSplitsThroughTheOneRule()
    {
        // The sends themselves need a mail server, so their use of the rule is pinned at the source: no recipient
        // list in EmailService or in Migration's administrators list is split on a comma alone any more.
        var email = RazorSyntax.CommentsBlanked(File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "EmailService.cs")));
        Assert.DoesNotContain(".Split(',', StringSplitOptions.RemoveEmptyEntries)", email, StringComparison.Ordinal);
        Assert.Contains("foreach (var email in SplitAddresses(_adminEmail))", email, StringComparison.Ordinal);
        Assert.Contains("var addresses = SplitAddresses(recipients);", email, StringComparison.Ordinal);

        var migration = RazorSyntax.CommentsBlanked(File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "MigrationService.cs")));
        Assert.Contains("_adminNotificationEmails = EmailService.SplitAddresses(adminEmail).ToArray();", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryHelpTextThatNamesASeparator_NamesBoth()
    {
        // The help must be true: a field described as "comma-separated" for a recipient list would now be wrong by omission.
        var catalog = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Modules", "ModuleCatalog.cs"));
        var field = catalog[catalog.IndexOf("new(\"NotifySecurityTeam\"", StringComparison.Ordinal)..];
        field = field[..field.IndexOf('\n')];
        Assert.Contains("comma- or semicolon-separated", field, StringComparison.Ordinal);
        Assert.Contains("administrators", field, StringComparison.Ordinal);
    }
}
