using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-45. The two create refusals judge the ticket by the shared <see cref="TicketPresence"/> rule, so a
/// ticket made only of control or zero-width characters is refused as a blank one is. It used to pass
/// <c>IsNullOrWhiteSpace</c>, and nothing downstream asks again: Migration records the ticket and never
/// validates it (plan D2), so this check is the whole of the requirement.
/// </summary>
public class MigrationTicketPresenceTests
{
    private const string Required = "A ticket number is required; nothing was created.";

    private static MigrationEligibilityResult Eligible() =>
        new() { EmailAddress = "jo.doe@contoso.com", Status = MigrationStatus.Eligible };

    private static MigrationBatchResult CheckedFile() =>
        new() { BatchName = "October wave", EligibleUsers = 2, TotalUsers = 2 };

    public static TheoryData<string> NoTicket() => new()
    {
        "",
        "   ",
        "\u0001", // control only
        "\u200B", // zero-width only
        "\u200E\u200F", // bidi marks only
    };

    [Theory]
    [MemberData(nameof(NoTicket))]
    public void ASingleCreateWithNothingVisibleInItsTicket_IsRefused(string ticket)
    {
        Assert.True(MigrationService.RefusesSingleCreate(Eligible(), ticket, out var refusal));
        Assert.Equal(Required, refusal);
    }

    [Theory]
    [MemberData(nameof(NoTicket))]
    public void ABulkCreateWithNothingVisibleInItsTicket_IsRefused(string ticket)
    {
        Assert.True(MigrationService.RefusesBulkCreate(CheckedFile(), ticket, out var refusal));
        Assert.Equal(Required, refusal);
    }

    [Fact]
    public void ATicketAPersonTyped_RefusesNeither()
    {
        // The control for both theories: everything else about these two calls is fine, so a refusal
        // above is the ticket rule and not the eligibility clauses ahead of it.
        Assert.False(MigrationService.RefusesSingleCreate(Eligible(), "INC0042", out var single));
        Assert.Null(single);
        Assert.False(MigrationService.RefusesBulkCreate(CheckedFile(), "INC0042", out var bulk));
        Assert.Null(bulk);
    }

    [Fact]
    public void TheEligibilityClausesStillComeFirst()
    {
        // The ticket clause is last in each ternary, and must stay there: a run with nothing eligible
        // is refused for that, not for its ticket, whatever the ticket is.
        Assert.True(MigrationService.RefusesSingleCreate(
            new MigrationEligibilityResult { EmailAddress = "jo.doe@contoso.com", Status = MigrationStatus.Ineligible },
            "\u200B", out var single));
        Assert.Contains("only an eligible mailbox can be migrated", single, StringComparison.Ordinal);

        Assert.True(MigrationService.RefusesBulkCreate(
            new MigrationBatchResult { BatchName = "October wave", EligibleUsers = 0, TotalUsers = 2 },
            "\u200B", out var bulk));
        Assert.Contains("No mailbox in the checked file is eligible", bulk, StringComparison.Ordinal);
    }
}
