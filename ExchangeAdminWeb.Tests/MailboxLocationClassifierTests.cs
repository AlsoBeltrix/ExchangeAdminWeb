using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

public class MailboxLocationClassifierTests
{
    [Theory]
    [InlineData("MailUser")]
    [InlineData("RemoteUserMailbox")]
    [InlineData("RemoteSharedMailbox")]
    [InlineData("RemoteRoomMailbox")]
    [InlineData(" RemoteEquipmentMailbox ")]
    public void ForLookupDisplay_OnPremisesRecipientTypes_ReturnsOnPremises(string recipientTypeDetails)
    {
        Assert.Equal("On-Premises", MailboxLocationClassifier.ForLookupDisplay(recipientTypeDetails));
    }

    [Theory]
    [InlineData("UserMailbox")]
    [InlineData("SharedMailbox")]
    [InlineData("RoomMailbox")]
    [InlineData("EquipmentMailbox")]
    public void ForLookupDisplay_CloudMailboxTypes_ReturnsCloud(string recipientTypeDetails)
    {
        Assert.Equal("Cloud", MailboxLocationClassifier.ForLookupDisplay(recipientTypeDetails));
    }

    [Theory]
    [InlineData("MailContact")]
    [InlineData("MailUniversalDistributionGroup")]
    [InlineData("")]
    [InlineData(null)]
    public void ForLookupDisplay_NonMailboxTypes_ReturnsUnknown(string? recipientTypeDetails)
    {
        Assert.Equal("Unknown", MailboxLocationClassifier.ForLookupDisplay(recipientTypeDetails));
    }

    [Theory]
    [InlineData("MailUser", "OnPrem")]
    [InlineData("RemoteUserMailbox", "OnPrem")]
    [InlineData("UserMailbox", "Cloud")]
    [InlineData("MailContact", "Unknown")]
    public void ForOperationRouting_ReturnsExpectedRoute(string recipientTypeDetails, string expected)
    {
        Assert.Equal(expected, MailboxLocationClassifier.ForOperationRouting(recipientTypeDetails));
    }
}