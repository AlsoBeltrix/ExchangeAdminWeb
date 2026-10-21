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
    /// <remarks>
    /// <paramref name="isDirSynced"/> defaults to a definite <c>false</c>: since aud-a2b an absent
    /// flag fails the lookup, so a recipient built without one is no longer a neutral fixture.
    /// <paramref name="omitIsDirSynced"/> leaves the property off entirely.
    /// </remarks>
    private static PSObject Recipient(
        string? primarySmtp = "user@contoso.com",
        string? externalId = "ext-1",
        string? recipientType = "UserMailbox",
        object? isDirSynced = null,
        bool omitIsDirSynced = false,
        object? exchangeGuid = null)
    {
        var o = new PSObject();
        if (primarySmtp != null) o.Properties.Add(new PSNoteProperty("PrimarySmtpAddress", primarySmtp));
        if (externalId != null) o.Properties.Add(new PSNoteProperty("ExternalDirectoryObjectId", externalId));
        if (recipientType != null) o.Properties.Add(new PSNoteProperty("RecipientType", recipientType));
        if (!omitIsDirSynced) o.Properties.Add(new PSNoteProperty("IsDirSynced", isDirSynced ?? false));
        if (exchangeGuid != null) o.Properties.Add(new PSNoteProperty("ExchangeGuid", exchangeGuid));
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

    // aud-a2b hole 3. These two used to assert that an undetermined sync state "defaults to
    // cloud-only", on the reasoning that cloud-only assumes less. It assumes MORE: the cloud-only
    // branch skips the group, OU and pattern rules a synced recipient is subject to, so a synced
    // protected mailbox with unreadable metadata was checked against its user rows alone. They
    // are rewritten to the opposite assertion, not worked around.

    [Fact]
    public void MapRecipient_IsDirSyncedAbsent_FailsTheLookup()
    {
        Assert.Throws<InvalidOperationException>(() => ExchangeIdentityResolver.MapRecipient(
            Results(Recipient(omitIsDirSynced: true)), "user@contoso.com"));
    }

    [Fact]
    public void MapRecipient_IsDirSyncedUnparseable_FailsTheLookup()
    {
        Assert.Throws<InvalidOperationException>(() => ExchangeIdentityResolver.MapRecipient(
            Results(Recipient(isDirSynced: "sometimes")), "user@contoso.com"));
    }

    // ---- ExchangeGuid (aud-a2c) ----------------------------------------------
    // The mailbox GUID is what a synced mailbox's AD object must prove it holds. Dropping it here
    // would refuse every synced target; letting all zeros through would search AD for a zero GUID.
    // Neither absence nor garbage may fail the lookup - groups and contacts have no mailbox GUID.

    [Fact]
    public void MapRecipient_CarriesTheMailboxGuid()
    {
        var mailboxGuid = Guid.Parse("6f1d7c9e-2b4a-4e0f-9a3d-5c8b1e7f2a40");

        var typed = ExchangeIdentityResolver.MapRecipient(
            Results(Recipient(isDirSynced: true, exchangeGuid: mailboxGuid)), "vip@contoso.com");
        var asString = ExchangeIdentityResolver.MapRecipient(
            Results(Recipient(isDirSynced: true, exchangeGuid: "6F1D7C9E-2B4A-4E0F-9A3D-5C8B1E7F2A40")), "vip@contoso.com");
        var wrapped = ExchangeIdentityResolver.MapRecipient(
            Results(Recipient(isDirSynced: true, exchangeGuid: new PSObject(mailboxGuid))), "vip@contoso.com");

        Assert.Equal(mailboxGuid, typed!.ExchangeGuid);
        Assert.Equal(mailboxGuid, asString!.ExchangeGuid);
        Assert.Equal(mailboxGuid, wrapped!.ExchangeGuid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void MapRecipient_NoUsableMailboxGuid_IsNull_NotAFailedLookup(string? value)
    {
        var result = ExchangeIdentityResolver.MapRecipient(
            Results(Recipient(isDirSynced: true, exchangeGuid: value)), "group@contoso.com");

        Assert.NotNull(result);
        Assert.Null(result.ExchangeGuid);
    }

    [Fact]
    public void MapRecipient_AnAllZeroMailboxGuidObject_IsNull()
    {
        var result = ExchangeIdentityResolver.MapRecipient(
            Results(Recipient(isDirSynced: true, exchangeGuid: Guid.Empty)), "group@contoso.com");

        Assert.Null(result!.ExchangeGuid);
    }
}
