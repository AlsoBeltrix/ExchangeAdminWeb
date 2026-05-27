using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

public class MailboxLocationClassifierTests
{
    [Theory]
    [InlineData("MailUser")]
    [InlineData(" MailUser ")]
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
    [InlineData("LegacyMailbox")]
    [InlineData("LinkedMailbox")]
    [InlineData("GroupMailbox")]
    public void ForLookupDisplay_CloudMailboxTypes_ReturnsCloud(string recipientTypeDetails)
    {
        Assert.Equal("Cloud", MailboxLocationClassifier.ForLookupDisplay(recipientTypeDetails));
    }

    [Theory]
    [InlineData("MailContact")]
    [InlineData("MailUniversalDistributionGroup")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ForLookupDisplay_NonMailboxTypes_ReturnsUnknown(string? recipientTypeDetails)
    {
        Assert.Equal("Unknown", MailboxLocationClassifier.ForLookupDisplay(recipientTypeDetails));
    }

    [Theory]
    [InlineData("MailUser", "OnPrem")]
    [InlineData(" MailUser ", "OnPrem")]
    [InlineData("RemoteUserMailbox", "OnPrem")]
    [InlineData("UserMailbox", "Cloud")]
    [InlineData("LegacyMailbox", "Cloud")]
    [InlineData("GroupMailbox", "Cloud")]
    [InlineData("MailContact", "Unknown")]
    [InlineData("   ", "Unknown")]
    public void ForOperationRouting_ReturnsExpectedRoute(string recipientTypeDetails, string expected)
    {
        Assert.Equal(expected, MailboxLocationClassifier.ForOperationRouting(recipientTypeDetails));
    }
}