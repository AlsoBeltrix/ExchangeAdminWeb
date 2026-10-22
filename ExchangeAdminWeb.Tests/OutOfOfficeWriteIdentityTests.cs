using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a2d, Out of Office: the auto-reply is written to the mailbox the protected-principal check
/// validated, never to the string the operator typed. The page-level capture guarantee (an edit
/// while the operation runs changes nothing) is <see cref="OutOfOfficeRequestCaptureTests"/>.
/// </summary>
public sealed class OutOfOfficeWriteIdentityTests : IDisposable
{
    private const string Typed = "sales";
    private static readonly Guid Checked = Guid.Parse("0c1c1c1c-1111-4222-8333-444444444444");
    private static readonly Guid Vip = Guid.Parse("0c2c2c2c-5555-4666-8777-888888888888");

    private readonly ExchangeWriteHarness _h = new();

    public OutOfOfficeWriteIdentityTests()
    {
        _h.AddMailbox("shared@contoso.test", Checked);
        _h.AddMailbox("vip@contoso.test", Vip, aliases: Typed);
    }

    public void Dispose() => _h.Dispose();

    private OutOfOfficeService Service() => new(_h.Pool, null!, NullLogger<OutOfOfficeService>.Instance, _h.Config);

    private static OutOfOfficeRequest Request(string state = "Enabled") =>
        OutOfOfficeRequest.FromForm(Typed, state, "Away", "Away (external)", DateTime.Today, DateTime.Today.AddDays(1),
            TimeZoneInfo.Utc, "INC100");

    [Theory]
    [InlineData("Enabled")]
    [InlineData("Disabled")]
    public async Task TheWrite_IsBoundToTheValidatedMailbox_NotTheStringTyped(string state)
    {
        var result = await Service().SetOutOfOfficeAsync(Request(state), ExchangeWriteHarness.Validated(Checked));

        Assert.True(result.Success, result.Message);
        Assert.Equal(Checked.ToString("D"),
            ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Set-MailboxAutoReplyConfiguration")), "Identity"));
        // The message still names what the operator asked for.
        Assert.Contains(Typed, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAValidatedMailbox_NothingIsWritten_AndNoConnectionIsBorrowed()
    {
        var result = await Service().SetOutOfOfficeAsync(Request(), null);

        Assert.False(result.Success);
        Assert.Equal(ValidatedMailbox.Refusal(Typed), result.Message);
        Assert.Empty(_h.Calls);
        Assert.Equal(0, _h.Connects);
    }

    // aud-a7e. The write boundary does not trust the page to have refused: a schedule with no zone, or
    // with a time its zone skips (02:30 on 8 March 2026 in New York, when the clocks go forward), names
    // no instant, so nothing is written and no connection is borrowed. Not guessed as UTC.
    [Theory]
    [InlineData(null, "2026-11-02T09:00:00")]
    [InlineData("America/New_York", "2026-03-08T02:30:00")]
    // aud-a7g: a time the clocks repeat names two instants - also none to write.
    [InlineData("America/New_York", "2026-11-01T01:30:00")]
    public async Task AScheduleThatNamesNoInstant_WritesNothing_AndBorrowsNoConnection(string? zoneId, string start)
    {
        var zone = zoneId is null ? null : TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var startTime = DateTime.Parse(start, System.Globalization.CultureInfo.InvariantCulture);
        var request = OutOfOfficeRequest.FromForm(Typed, "Scheduled", "Away", "Away (external)",
            startTime, startTime.AddDays(4), zone, "INC100");

        var result = await Service().SetOutOfOfficeAsync(request, ExchangeWriteHarness.Validated(Checked));

        Assert.False(result.Success);
        Assert.Empty(_h.Calls);
        Assert.Equal(0, _h.Connects);
    }

    // aud-a7g. The write boundary's own refusal of a repeated time - here the end - names the time and
    // the zone, as the page's does, and chooses no instant.
    [Fact]
    public async Task ATimeTheClocksRepeat_IsRefusedAtTheWrite_NamingTheTimeAndTheZone()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var request = OutOfOfficeRequest.FromForm(Typed, "Scheduled", "Away", "Away (external)",
            new DateTime(2026, 10, 30, 9, 0, 0), new DateTime(2026, 11, 1, 1, 30, 0), zone, "INC100");

        var result = await Service().SetOutOfOfficeAsync(request, ExchangeWriteHarness.Validated(Checked));

        Assert.False(result.Success);
        Assert.Contains("2026-11-01 01:30", result.Message, StringComparison.Ordinal);
        Assert.Contains("America/New_York", result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Calls);
        Assert.Equal(0, _h.Connects);
    }

    [Fact]
    public async Task ThePreviousStateRead_IsOfTheValidatedMailbox_AndWithoutOneReadsNothing()
    {
        // The audit's previousState must describe the mailbox that is about to be written.
        var read = await Service().GetOutOfOfficeAsync(ExchangeWriteHarness.Validated(Checked));
        var none = await Service().GetOutOfOfficeAsync((ValidatedMailbox?)null);

        Assert.NotNull(read);
        Assert.Null(read!.Error);
        Assert.Equal(Checked.ToString("D"),
            ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Get-MailboxAutoReplyConfiguration")), "Identity"));
        Assert.Null(none);
    }
}
