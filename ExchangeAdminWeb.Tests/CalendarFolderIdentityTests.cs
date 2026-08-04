using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// First tests to reach any of <c>CalendarPermissionService</c>'s logic. The service sat at 0%
/// coverage because everything ran behind a live Exchange call.
///
/// What is at stake: a wrong folder identity does not throw, it targets a DIFFERENT FOLDER. The
/// operator is told the grant succeeded, and someone quietly has access to something nobody
/// intended.
/// </summary>
public class CalendarFolderIdentityTests
{
    private const string Mailbox = "ceo@analog.com";

    [Fact]
    public void UsesTheFolderPathExchangeReported()
    {
        Assert.Equal(@"ceo@analog.com:\Calendar", CalendarFolderIdentity.Build(Mailbox, @"\Calendar"));
    }

    [Theory]
    [InlineData(@"\Kalender")]      // German
    [InlineData(@"\Calendrier")]    // French
    [InlineData(@"\Agenda")]        // Dutch
    public void PreservesALocalizedFolderName(string reported)
    {
        // The reason the caller queries Exchange at all instead of hardcoding "\Calendar": the
        // folder is named in the mailbox owner's language. Assuming the English name would
        // address a folder that does not exist - or worse, one that does and is not the calendar.
        Assert.Equal($"{Mailbox}:{reported}", CalendarFolderIdentity.Build(Mailbox, reported));
    }

    [Fact]
    public void ConvertsForwardSlashesExchangeOnlineMayReturn()
    {
        // EXO has been observed returning "/" where the cmdlets require "\".
        Assert.Equal(@"ceo@analog.com:\Calendar\Team", CalendarFolderIdentity.Build(Mailbox, "/Calendar/Team"));
    }

    [Fact]
    public void HandlesASubfolderPath()
    {
        Assert.Equal(@"ceo@analog.com:\Calendar\Personal", CalendarFolderIdentity.Build(Mailbox, @"\Calendar\Personal"));
    }

    // ---------------------------------------------------------------- The dangerous cases

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FallsBackToTheDefaultRatherThanAddressingTheMailboxRoot(string? reported)
    {
        // The one that matters. Before extraction this used `?? @"\Calendar"`, so null fell back
        // but an EMPTY string produced "ceo@analog.com:" - the mailbox ROOT. Granting calendar
        // rights there grants them across the whole mailbox, silently and successfully.
        Assert.Equal(@"ceo@analog.com:\Calendar", CalendarFolderIdentity.Build(Mailbox, reported));
    }

    [Fact]
    public void NeverProducesABareMailboxIdentity()
    {
        // Belt and braces on the same risk, stated as the property rather than the input: no
        // input may yield an identity that addresses the mailbox itself.
        foreach (var reported in new string?[] { null, "", "   ", "Calendar", "/", @"\" })
        {
            var identity = CalendarFolderIdentity.Build(Mailbox, reported);
            Assert.NotEqual($"{Mailbox}:", identity);
            Assert.StartsWith($"{Mailbox}:\\", identity);
        }
    }

    [Fact]
    public void AddsTheLeadingSeparatorWhenExchangeOmitsIt()
    {
        // "ceo@analog.com:Calendar" is rejected by the cmdlets. Loud rather than dangerous, but
        // an avoidable failure.
        Assert.Equal(@"ceo@analog.com:\Calendar", CalendarFolderIdentity.Build(Mailbox, "Calendar"));
    }

    [Fact]
    public void DoesNotDoubleTheSeparator()
    {
        Assert.Equal(@"ceo@analog.com:\Calendar", CalendarFolderIdentity.Build(Mailbox, @"\Calendar"));
    }

    [Fact]
    public void LeavesTheMailboxIdentityUntouched()
    {
        // The mailbox half is already-resolved directory output, not operator input. It must be
        // passed through verbatim - rewriting it would target a different mailbox.
        const string odd = "O'Brien.Test_1@sub.analog.com";

        Assert.StartsWith($"{odd}:", CalendarFolderIdentity.Build(odd, @"\Calendar"));
    }
}
