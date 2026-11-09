using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-24. The shared ticket presence rule, and the one form a ticket is recorded in: the typed value with every control
/// character, format character and unpaired surrogate removed, then trimmed (.agents/decisions.md 2026-11-09). A ticket is
/// missing only when nothing visible remains. The pages' and services' own tests drive the rule through each gate; these
/// pin the rule itself.
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
    [InlineData("\u0000")] // kd-24: control only
    [InlineData("\u0000\u0001\u001F")]
    [InlineData("\u007F")]
    [InlineData("​")] // kd-48: format only - zero-width space, bidi marks, BOM, isolates
    [InlineData("‎‏")]
    [InlineData("﻿")]
    [InlineData("‮⁦")]
    [InlineData("​ ‏")] // format characters around white space
    [InlineData("­")] // a soft hyphen alone
    public void ATicketWithNothingVisibleLeft_IsMissing(string? ticket)
    {
        Assert.False(TicketPresence.IsPresent(ticket));
        Assert.False(TicketPresence.IsPresentAsTyped(ticket));
        Assert.Equal(TicketPresence.RequiredMessage, TicketPresence.Problem(ticket));
        Assert.Equal("A ticket number is required.", TicketPresence.Describe(ticket, "A ticket number is required."));

        var refusal = TicketPresence.Refusal(ticket);
        Assert.NotNull(refusal);
        Assert.False(refusal.IsValid);
        Assert.Equal("Ticket number is required", refusal.Message);
    }

    // 2026-11-09: a ticket carrying an invisible character is not refused; it is recorded with the character removed.
    [Theory]
    [InlineData("INC​0042", "INC0042")] // a zero-width space inside a ticket
    [InlineData("INC0042​", "INC0042")]
    [InlineData("﻿INC0042", "INC0042")] // a byte-order mark
    [InlineData("INC‮0042", "INC0042")] // bidi marks
    [InlineData("‎INC0042‏", "INC0042")]
    [InlineData("INC­0042", "INC0042")] // a soft hyphen
    [InlineData("INC\u00000042", "INC0042")] // control characters
    [InlineData("INC0042\u0007", "INC0042")]
    [InlineData("CHG 0042", "CHG 0042")] // white space inside a ticket is kept
    [InlineData("INC0042 ", "INC0042")] // and trimmed at the ends
    [InlineData("\tINC0042\n", "INC0042")]
    [InlineData("INC0042 ​", "INC0042")] // trimmed after the strip, so the space the strip exposed goes too
    [InlineData("é-42", "é-42")] // a combining mark on its base letter is kept
    [InlineData("INC-0042/2", "INC-0042/2")]
    public void ATicketIsRecordedSanitized_AndIsPresent(string typed, string recorded)
    {
        Assert.Equal(recorded, TicketPresence.Sanitize(typed));
        Assert.True(TicketPresence.IsPresent(typed));
        Assert.True(TicketPresence.IsPresentAsTyped(typed));
        Assert.Null(TicketPresence.Problem(typed));
        Assert.Null(TicketPresence.Refusal(typed));
    }

    [Fact]
    public void AnUnpairedSurrogate_IsStripped()
    {
        // In a Fact rather than the theory above: theory data is serialized for the runner, and an unpaired
        // surrogate does not survive that - it arrives as U+FFFD, a visible character, and proves nothing.
        Assert.Equal("INC", TicketPresence.Sanitize("INC\uD800"));
        Assert.Equal("INC0042", TicketPresence.Sanitize("\uD800INC0042"));
        Assert.Equal("INC0042", TicketPresence.Sanitize("INC\uDC000042"));
        Assert.True(TicketPresence.IsPresent("INC\uD800"));
        Assert.False(TicketPresence.IsPresent("\uDC00"));
        Assert.Equal(TicketPresence.RequiredMessage, TicketPresence.Refusal("\uD800 \uDC00\uDC00")?.Message);
        Assert.Equal("INC😀", TicketPresence.Sanitize("INC😀")); // a surrogate pair is one character, kept
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("​", "")]
    [InlineData("INC0042", "INC0042")]
    public void Sanitize_GivesAStringForEveryInput(string? typed, string recorded) =>
        Assert.Equal(recorded, TicketPresence.Sanitize(typed));

    [Theory]
    [InlineData("INC0042")]
    [InlineData("REQ1234567")]
    [InlineData("CHG 0042")]
    [InlineData("INC-0042/2")]
    [InlineData("ÉTÉ-42")] // kd-48: letters beyond ASCII are visible
    [InlineData("é")] // a combining mark after its base
    [InlineData("INC😀")] // a surrogate pair is one character
    public void ATicketAPersonTyped_IsPresent_AndRecordedAsTyped_PunctuationAndSpacesIncluded(string ticket)
    {
        // Validation off means any typed ticket is plain audit metadata (decision 2026-08-31), so
        // the rule must not quietly become the ServiceNow shape.
        Assert.True(TicketPresence.IsPresent(ticket));
        Assert.Null(TicketPresence.Refusal(ticket));
        Assert.Null(TicketPresence.Problem(ticket));
        Assert.Equal(ticket, TicketPresence.Sanitize(ticket));
    }

    [Fact]
    public void Describe_UsesTheCallersWordsForAMissingTicket_AndNothingForAPresentOne()
    {
        Assert.Equal("A ticket number is required.", TicketPresence.Describe("  ", "A ticket number is required."));
        Assert.Null(TicketPresence.Describe("INC0042", "A ticket number is required."));
        Assert.Null(TicketPresence.Describe("INC​0042", "A ticket number is required."));
    }

    // For the gates' own tests: a ticket that carries a zero-width space, and the form it must be recorded in.
    internal const string TypedWithZeroWidthSpace = "INC​0042";
    internal const string RecordedSanitized = "INC0042";

    // 2026-11-09, the pages: every ticket-gated control is enabled by TicketPresence.IsPresentAsTyped - the check behind
    // it - never by string.IsNullOrWhiteSpace, which a ticket carrying only a zero-width space passes; no ticket box shows
    // an invisible-character refusal any more; and no handler records its ticket merely trimmed - it reads the box through
    // TicketPresence.Sanitize, so what is checked is what is recorded.
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
    public void EveryTicketGatedControl_IsEnabledByTheRuleOfTheCheckBehindIt_AndItsTicketIsRecordedSanitized()
    {
        var pages = Directory.GetFiles(Path.GetDirectoryName(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "Home.razor"))!, "*.razor");
        var gated = 0;
        foreach (var path in pages)
        {
            var name = Path.GetFileName(path);
            var text = File.ReadAllText(path);
            Assert.False(text.Contains("InvisibleCharacter", StringComparison.Ordinal),
                $"{name}: a ticket box still refuses an invisible character; it is sanitized instead (decision 2026-11-09).");
            if (NotGatedByTicketPresence.ContainsKey(name))
                continue;
            foreach (var variable in TicketVariables)
            {
                // Sweep item 41: nor is any handler - the undo's and Migration's confirm returned on it, unrecorded.
                Assert.False(text.Contains($"IsNullOrWhiteSpace({variable})", StringComparison.Ordinal),
                    $"{name}: the ticket {variable} is judged by string.IsNullOrWhiteSpace, not by TicketPresence.");
                if (!text.Contains($"IsPresentAsTyped({variable})", StringComparison.Ordinal))
                    continue;
                gated++;
            }
        }

        Assert.True(gated >= 25, $"only {gated} ticket boxes were found gated; the scan is not reading the pages.");
    }
}
