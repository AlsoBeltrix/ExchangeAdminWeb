using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The cloud size limit applies to the primary mailbox and the archive SEPARATELY.
/// Their combined size is not a migration criterion. These tests pin that rule; the
/// module previously blocked on mailbox + archive together.
/// </summary>
public class MigrationQuotaTests
{
    private static MigrationEligibilityResult Result(double mailboxGB, double archiveGB, long quotaGB = 99) =>
        new()
        {
            EmailAddress = "user@co.com",
            MailboxSizeGB = mailboxGB,
            ArchiveSizeGB = archiveGB,
            CloudQuotaGB = quotaGB
        };

    [Fact]
    public void DefaultQuota_Is99GB()
    {
        var result = new MigrationEligibilityResult { EmailAddress = "user@co.com" };
        Assert.Equal(99, result.CloudQuotaGB);
    }

    [Fact]
    public void CombinedOverLimit_DoesNotExceed_WhenEachSideIsUnder()
    {
        // The regression case: 99 + 99 is 198 GB combined and both sides are legal.
        var result = Result(99, 99);
        Assert.False(result.MailboxExceedsQuota);
        Assert.False(result.ArchiveExceedsQuota);
        Assert.False(result.ExceedsQuota);
    }

    [Fact]
    public void CombinedOverLimit_DoesNotExceed_ForOrdinarySizes()
    {
        var result = Result(60, 60);
        Assert.Equal(120, result.TotalSizeGB);
        Assert.False(result.ExceedsQuota);
    }

    [Fact]
    public void ExactlyAtLimit_IsAllowed()
    {
        var result = Result(99, 99);
        Assert.False(result.ExceedsQuota);
    }

    [Fact]
    public void MailboxOverLimit_FlagsMailboxOnly()
    {
        var result = Result(100, 5);
        Assert.True(result.MailboxExceedsQuota);
        Assert.False(result.ArchiveExceedsQuota);
        Assert.True(result.ExceedsQuota);
    }

    [Fact]
    public void ArchiveOverLimit_FlagsArchiveOnly()
    {
        var result = Result(5, 100);
        Assert.False(result.MailboxExceedsQuota);
        Assert.True(result.ArchiveExceedsQuota);
        Assert.True(result.ExceedsQuota);
    }

    [Fact]
    public void BothOverLimit_FlagsBoth()
    {
        var result = Result(120, 150);
        Assert.True(result.MailboxExceedsQuota);
        Assert.True(result.ArchiveExceedsQuota);
        Assert.True(result.ExceedsQuota);
    }

    [Fact]
    public void ConfiguredQuota_IsHonoured()
    {
        var result = Result(60, 60, quotaGB: 50);
        Assert.True(result.MailboxExceedsQuota);
        Assert.True(result.ArchiveExceedsQuota);
    }

    [Fact]
    public void NoArchive_MailboxUnderLimit_IsAllowed()
    {
        var result = Result(98.5, 0);
        Assert.False(result.ExceedsQuota);
    }
}
