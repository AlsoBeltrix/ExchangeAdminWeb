using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-24. The shared ticket presence rule: blank, or carrying any control character, is no ticket.
/// The pages' and services' own tests drive the rule through each gate; these pin the rule itself.
/// Sweep item 40: and why - a ticket with nothing visible is missing, one carrying an invisible character says so.
/// </summary>
public class TicketPresenceTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("́")] // marks that draw nothing alone
    [InlineData("́̂")]
    [InlineData("")] // private use
    public void ATicketWithNothingVisibleInIt_IsMissing(string? ticket)
    {
        Assert.False(TicketPresence.IsPresent(ticket));
        Assert.Equal(TicketPresence.RequiredMessage, TicketPresence.Problem(ticket));

        var refusal = TicketPresence.Refusal(ticket);
        Assert.NotNull(refusal);
        Assert.False(refusal.IsValid);
        Assert.Equal("Ticket number is required", refusal.Message);
    }

    // Sweep item 40: a ticket carrying a control or format character is refused as that, not as a missing ticket - the
    // operator who pasted "INC0042" with a zero-width space in it sees a ticket in the box.
    [Theory]
    [InlineData("\u0000")]
    [InlineData("\u0001")]
    [InlineData("\u0000\u0001\u001F")]
    [InlineData("INC\u00000042")]
    [InlineData("INC0042\u0007")]
    [InlineData("\u007F")]
    [InlineData("​")] // kd-48: format characters - zero-width space, bidi marks, BOM, isolates
    [InlineData("‎")]
    [InlineData("‏")]
    [InlineData("﻿")]
    [InlineData("‮")]
    [InlineData("⁦")]
    [InlineData("​ ‏")]
    [InlineData("INC0042​")] // a format character inside a real ticket refuses it too
    [InlineData("INC‮0042")]
    [InlineData("INC­0042")] // a soft hyphen
    public void ATicketCarryingAnInvisibleCharacter_IsNotPresent_AndSaysSo(string ticket)
    {
        Assert.False(TicketPresence.IsPresent(ticket));
        Assert.Equal(TicketPresence.InvisibleCharacterMessage, TicketPresence.Problem(ticket));

        var refusal = TicketPresence.Refusal(ticket);
        Assert.NotNull(refusal);
        Assert.False(refusal.IsValid);
        Assert.Equal(TicketPresence.InvisibleCharacterMessage, refusal.Message);
        Assert.DoesNotContain("required", refusal.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TicketPresence.InvisibleCharacterMessage + ".", TicketPresence.Describe(ticket, "A ticket number is required."));
    }

    [Fact]
    public void ATicketWithAnUnpairedSurrogate_IsNotPresent()
    {
        // In a Fact rather than the theory above: theory data is serialized for the runner, and an unpaired
        // surrogate does not survive that - it arrives as U+FFFD, a visible character, and proves nothing.
        Assert.False(TicketPresence.IsPresent("\uDC00"));
        Assert.False(TicketPresence.IsPresent("INC\uD800"));
        Assert.False(TicketPresence.IsPresent("\uD800INC0042"));
        Assert.Equal(TicketPresence.InvisibleCharacterMessage, TicketPresence.Refusal("INC\uD800")?.Message);
    }

    [Theory]
    [InlineData("INC0042")]
    [InlineData("REQ1234567")]
    [InlineData("CHG 0042")]
    [InlineData("INC-0042/2")]
    [InlineData("ÉTÉ-42")] // kd-48: letters beyond ASCII are visible
    [InlineData("é")] // a combining mark after its base
    [InlineData("INC😀")] // a surrogate pair is one character
    public void ATicketAPersonTyped_IsPresent_PunctuationAndSpacesIncluded(string ticket)
    {
        // Validation off means any typed ticket is plain audit metadata (decision 2026-08-31), so
        // the rule must not quietly become the ServiceNow shape.
        Assert.True(TicketPresence.IsPresent(ticket));
        Assert.Null(TicketPresence.Refusal(ticket));
        Assert.Null(TicketPresence.Problem(ticket));
    }

    [Theory]
    [InlineData(" INC0042 ", true)]
    [InlineData("INC0042\n", true)] // trimmed as the checks trim it: the newline goes
    [InlineData("\tINC0042", true)]
    [InlineData("INC0042​", false)] // a zero-width space is not trimmed
    [InlineData("﻿INC0042", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void ATicketAsTyped_IsJudgedTrimmed_AsTheCheckBehindTheButtonJudgesIt(string? typed, bool present)
    {
        // Sweep item 40: a submit button is enabled by the rule the check behind it applies - the ticket trimmed, then
        // TicketPresence - so the button and the check agree.
        Assert.Equal(present, TicketPresence.IsPresentAsTyped(typed));
        Assert.Equal(present, TicketPresence.IsPresent(typed?.Trim()));
    }

    [Theory]
    [InlineData("INC0042​", true)]
    [InlineData(" ­ ", true)]
    [InlineData("   ", false)]
    [InlineData("INC0042", false)]
    [InlineData(null, false)]
    public void TheHintBesideATicketBox_ShowsOnlyForAnInvisibleCharacter(string? typed, bool shown) =>
        Assert.Equal(shown, TicketPresence.HasInvisibleCharacterAsTyped(typed));

    [Fact]
    public void Describe_UsesTheCallersWordsForAMissingTicket_AndNothingForAPresentOne()
    {
        Assert.Equal("A ticket number is required.", TicketPresence.Describe("  ", "A ticket number is required."));
        Assert.Null(TicketPresence.Describe("INC0042", "A ticket number is required."));
    }

    // Sweep item 40. For the gates' own tests: whether a ticket is refused for an invisible character, classified here
    // independently of TicketPresence (trimmed, then any control or format character or a lone surrogate half), and the words
    // a gate then gives - its own for a missing ticket, the invisible-character message otherwise.
    internal static bool CarriesAnInvisibleCharacter(string? ticket) =>
        !string.IsNullOrWhiteSpace(ticket) && ticket.Trim().Any(c => char.IsControl(c) || char.IsSurrogate(c)
            || System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format);

    internal static string Said(string? ticket, string whenMissing) =>
        CarriesAnInvisibleCharacter(ticket) ? TicketPresence.InvisibleCharacterMessage + "." : whenMissing;

    internal static string RefusalSaid(string? ticket) =>
        CarriesAnInvisibleCharacter(ticket) ? TicketPresence.InvisibleCharacterMessage : "Ticket number is required";

    // Sweep item 40, the pages: every ticket-gated control is enabled by TicketPresence.IsPresentAsTyped - the check behind
    // it - never by string.IsNullOrWhiteSpace, which a ticket carrying only a zero-width space passes; and every ticket box
    // whose control is gated so shows the invisible-character hint, so a control disabled for one says why.
    private static readonly string[] TicketVariables =
    [
        "ticketNumber", "bulkTicketNumber", "csvFinderTicket", "typeTicketNumber", "csvTypeTicket", "actionTicket",
        "memberTicketNumber", "formTicketNumber", "singleTicketNumber", "pendingActionTicket", "undoTicket",
    ];

    // Pages whose ticket is not gated by TicketPresence, each with its reason.
    private static readonly Dictionary<string, string> NotGatedByTicketPresence = new(StringComparer.Ordinal)
    {
        ["AccountLockoutRemediation.razor"] = "its service checks the ticket with IsNullOrWhiteSpace (AccountLockoutRemediationService); not in sweep item 41's list - recorded in sweep-e-40",
    };

    [Fact]
    public void EveryTicketGatedControl_IsEnabledByTheRuleOfTheCheckBehindIt_AndItsBoxShowsWhyNot()
    {
        var pages = Directory.GetFiles(Path.GetDirectoryName(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "Home.razor"))!, "*.razor");
        var gated = 0;
        foreach (var path in pages)
        {
            var name = Path.GetFileName(path);
            if (NotGatedByTicketPresence.ContainsKey(name))
                continue;
            var text = File.ReadAllText(path);
            foreach (var variable in TicketVariables)
            {
                foreach (var line in text.Split('\n').Where(l => l.Contains($"IsNullOrWhiteSpace({variable})", StringComparison.Ordinal)))
                {
                    Assert.False(line.Contains("disabled=", StringComparison.Ordinal) || line.Contains("title=", StringComparison.Ordinal)
                        || line.Contains("@if", StringComparison.Ordinal),
                        $"{name}: a control is gated on string.IsNullOrWhiteSpace({variable}): {line.Trim()}");
                }
                if (!text.Contains($"IsPresentAsTyped({variable})", StringComparison.Ordinal))
                    continue;
                gated++;
                Assert.True(text.Contains($"HasInvisibleCharacterAsTyped({variable})", StringComparison.Ordinal),
                    $"{name}: the ticket box bound to {variable} shows no invisible-character hint.");
            }
        }

        Assert.True(gated >= 25, $"only {gated} ticket boxes were found gated; the scan is not reading the pages.");
    }
}
