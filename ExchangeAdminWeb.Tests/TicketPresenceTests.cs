using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-24. The shared ticket presence rule: blank, or carrying any control character, is no ticket.
/// The pages' and services' own tests drive the rule through each gate; these pin the rule itself.
/// </summary>
public class TicketPresenceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\u0000")]
    [InlineData("\u0001")]
    [InlineData("\u0000\u0001\u001F")]
    [InlineData("INC\u00000042")]
    [InlineData("INC0042\u0007")]
    [InlineData("\u007F")]
    public void ATicketThatIsMissingOrCarriesAControlCharacter_IsNotPresent(string? ticket)
    {
        Assert.False(TicketPresence.IsPresent(ticket));

        var refusal = TicketPresence.Refusal(ticket);
        Assert.NotNull(refusal);
        Assert.False(refusal.IsValid);
        Assert.Equal("Ticket number is required", refusal.Message);
    }

    [Theory]
    [InlineData("INC0042")]
    [InlineData("REQ1234567")]
    [InlineData("CHG 0042")]
    [InlineData("INC-0042/2")]
    public void ATicketAPersonTyped_IsPresent_PunctuationAndSpacesIncluded(string ticket)
    {
        // Validation off means any typed ticket is plain audit metadata (decision 2026-08-31), so
        // the rule must not quietly become the ServiceNow shape.
        Assert.True(TicketPresence.IsPresent(ticket));
        Assert.Null(TicketPresence.Refusal(ticket));
    }
}
