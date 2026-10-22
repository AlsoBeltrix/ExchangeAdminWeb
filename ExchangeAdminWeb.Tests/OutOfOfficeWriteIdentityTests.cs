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
        OutOfOfficeRequest.FromForm(Typed, state, "Away", "Away (external)", DateTime.Today, DateTime.Today.AddDays(1), "INC100");

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
