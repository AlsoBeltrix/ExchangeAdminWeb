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
    [InlineData("\u200B")] // kd-48: format characters - zero-width space, bidi marks, BOM, isolates
    [InlineData("\u200E")]
    [InlineData("\u200F")]
    [InlineData("\uFEFF")]
    [InlineData("\u202E")]
    [InlineData("\u2066")]
    [InlineData("\u200B \u200F")]
    [InlineData("INC0042\u200B")] // a format character inside a real ticket refuses it too
    [InlineData("INC\u202E0042")]
    [InlineData("\u0301")] // marks that draw nothing alone
    [InlineData("\u0301\u0302")]
    [InlineData("\uE000")] // private use
    public void ATicketThatIsMissingOrCarriesAControlCharacter_IsNotPresent(string? ticket)
    {
        Assert.False(TicketPresence.IsPresent(ticket));

        var refusal = TicketPresence.Refusal(ticket);
        Assert.NotNull(refusal);
        Assert.False(refusal.IsValid);
        Assert.Equal("Ticket number is required", refusal.Message);
    }

    [Fact]
    public void ATicketWithAnUnpairedSurrogate_IsNotPresent()
    {
        // In a Fact rather than the theory above: theory data is serialized for the runner, and an unpaired
        // surrogate does not survive that - it arrives as U+FFFD, a visible character, and proves nothing.
        Assert.False(TicketPresence.IsPresent("\uDC00"));
        Assert.False(TicketPresence.IsPresent("INC\uD800"));
        Assert.False(TicketPresence.IsPresent("\uD800INC0042"));
        Assert.NotNull(TicketPresence.Refusal("INC\uD800"));
    }

    [Theory]
    [InlineData("INC0042")]
    [InlineData("REQ1234567")]
    [InlineData("CHG 0042")]
    [InlineData("INC-0042/2")]
    [InlineData("\u00C9T\u00C9-42")] // kd-48: letters beyond ASCII are visible
    [InlineData("e\u0301")] // a combining mark after its base
    [InlineData("INC\uD83D\uDE00")] // a surrogate pair is one character
    public void ATicketAPersonTyped_IsPresent_PunctuationAndSpacesIncluded(string ticket)
    {
        // Validation off means any typed ticket is plain audit metadata (decision 2026-08-31), so
        // the rule must not quietly become the ServiceNow shape.
        Assert.True(TicketPresence.IsPresent(ticket));
        Assert.Null(TicketPresence.Refusal(ticket));
    }
}
