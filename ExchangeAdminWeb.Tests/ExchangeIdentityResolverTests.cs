using System.Collections.ObjectModel;
using System.Management.Automation;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the absence/failure boundary in <see cref="ExchangeIdentityResolver"/>.
///
/// Why this boundary matters (docs/ProtectedPrincipalResolution-Plan.md, Design): a null return
/// from ResolveRecipientAsync is consumed as evidence that a recipient does not exist, and the
/// protected-principal gate may allow an operation on that basis. If a failed lookup - EXO
/// unreachable, credential failure - collapsed into null, an outage would present as an
/// affirmative absence and could un-protect a principal. Only Exchange's own "couldn't be found"
/// may produce null; everything else must propagate.
///
/// The pooled call itself needs a live EXO session, so these cover the two decision helpers the
/// pooled delegate defers to.
/// </summary>
public class ExchangeIdentityResolverTests
{
    private static PSObject Recipient(
        string? primarySmtp = "user@contoso.com",
        string? externalId = "ext-1",
        string? recipientType = "UserMailbox",
        object? isDirSynced = null)
    {
        var o = new PSObject();
        if (primarySmtp != null) o.Properties.Add(new PSNoteProperty("PrimarySmtpAddress", primarySmtp));
        if (externalId != null) o.Properties.Add(new PSNoteProperty("ExternalDirectoryObjectId", externalId));
        if (recipientType != null) o.Properties.Add(new PSNoteProperty("RecipientType", recipientType));
        if (isDirSynced != null) o.Properties.Add(new PSNoteProperty("IsDirSynced", isDirSynced));
        return o;
    }

    private static Collection<PSObject> Results(params PSObject[] items) => new(items);

    // ---- absence vs failure -------------------------------------------------

    [Fact]
    public void IsRecipientNotFound_ExchangeNotFoundError_IsAbsence()
    {
        // The message Exchange emits for an unknown identity.
        var ex = new InvalidOperationException(
            "The operation couldn't be performed because object 'nope@contoso.com' couldn't be found on 'DC01'.");

        Assert.True(ExchangeIdentityResolver.IsRecipientNotFound(ex));
    }

    [Theory]
    [InlineData("The term 'Get-Recipient' is not recognized")]
    [InlineData("Connecting to remote server outlook.office365.com failed")]
    [InlineData("The I/O operation has been aborted")]
    [InlineData("Access is denied")]
    [InlineData("")]
    public void IsRecipientNotFound_AnyOtherError_IsFailureNotAbsence(string message)
    {
        // Each of these is a lookup that did not run. Reporting absence here would let an EXO
        // outage un-protect a principal.
        Assert.False(ExchangeIdentityResolver.IsRecipientNotFound(new InvalidOperationException(message)));
    }

    // ---- mapping ------------------------------------------------------------

    [Fact]
    public void MapRecipient_EmptyResultSet_ReturnsNull()
    {
        Assert.Null(ExchangeIdentityResolver.MapRecipient(Results(), "nope@contoso.com"));
    }

    [Fact]
    public void MapRecipient_ReturnsCanonicalPrimaryAddress_NotTheQueriedAlias()
    {
        // The alias case: this is what closes the protected-principal alias bypass. Exchange is
        // queried with a secondary alias and answers with the real primary address, which the
        // caller then re-resolves against AD.
        var result = ExchangeIdentityResolver.MapRecipient(
            Results(Recipient(primarySmtp: "vincent.roche@analog.com")),
            "VRoche@O365.analog.com");

        Assert.NotNull(result);
        Assert.Equal("vincent.roche@analog.com", result.PrimarySmtpAddress);
    }

    [Fact]
    public void MapRecipient_TrimsPrimaryAddress()
    {
        var result = ExchangeIdentityResolver.MapRecipient(
            Results(Recipient(primarySmtp: "  user@contoso.com  ")), "user@contoso.com");

        Assert.Equal("user@contoso.com", result!.PrimarySmtpAddress);
    }

    [Fact]
    public void MapRecipient_CarriesObjectIdAndRecipientType()
    {
        var result = ExchangeIdentityResolver.MapRecipient(
            Results(Recipient(externalId: "abc-123", recipientType: "MailUniversalDistributionGroup")),
            "adspstaff@analog.com");

        Assert.Equal("abc-123", result!.ExternalDirectoryObjectId);
        Assert.Equal("MailUniversalDistributionGroup", result.RecipientType);
    }

    [Fact]
    public void MapRecipient_MissingPrimaryAddress_ThrowsRatherThanReportingAbsence()
    {
        // The recipient demonstrably exists but cannot be re-resolved or matched against the
        // protected list. Null would claim it does not exist; that is the un-protecting answer.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ExchangeIdentityResolver.MapRecipient(Results(Recipient(primarySmtp: null)), "odd@contoso.com"));

        Assert.Contains("PrimarySmtpAddress", ex.Message);
    }

    [Fact]
    public void MapRecipient_BlankPrimaryAddress_ThrowsRatherThanReportingAbsence()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ExchangeIdentityResolver.MapRecipient(Results(Recipient(primarySmtp: "   ")), "odd@contoso.com"));
    }

    // ---- ExistsOnPrem -------------------------------------------------------

    [Fact]
    public void MapRecipient_IsDirSyncedTrue_IsOnPrem()
    {
        var result = ExchangeIdentityResolver.MapRecipient(
            Results(Recipient(isDirSynced: true)), "user@contoso.com");

        Assert.True(result!.ExistsOnPrem);
    }

    [Fact]
    public void MapRecipient_IsDirSyncedStringTrue_IsOnPrem()
    {
        // PowerShell remoting can deserialize booleans as strings.
        var result = ExchangeIdentityResolver.MapRecipient(
            Results(Recipient(isDirSynced: "True")), "user@contoso.com");

        Assert.True(result!.ExistsOnPrem);
    }

    [Theory]
    [InlineData(false)]
    [InlineData("False")]
    public void MapRecipient_IsDirSyncedFalse_IsCloudOnly(object value)
    {
        var result = ExchangeIdentityResolver.MapRecipient(
            Results(Recipient(isDirSynced: value)), "Jabil.support@analog.com");

        Assert.False(result!.ExistsOnPrem);
    }

    [Fact]
    public void MapRecipient_IsDirSyncedAbsent_DefaultsToCloudOnly()
    {
        // Undetermined must take the conservative branch: cloud-only cannot match on-prem group
        // rules, so it assumes less than claiming the object is synced.
        var result = ExchangeIdentityResolver.MapRecipient(
            Results(Recipient(isDirSynced: null)), "user@contoso.com");

        Assert.False(result!.ExistsOnPrem);
    }

    [Fact]
    public void MapRecipient_IsDirSyncedUnparseable_DefaultsToCloudOnly()
    {
        var result = ExchangeIdentityResolver.MapRecipient(
            Results(Recipient(isDirSynced: "sometimes")), "user@contoso.com");

        Assert.False(result!.ExistsOnPrem);
    }
}
