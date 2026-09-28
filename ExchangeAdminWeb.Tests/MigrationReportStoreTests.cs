using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Hosting;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Behavioural coverage for where a per-mailbox migration report lives and how long it lives.
/// </summary>
/// <remarks>
/// R24c, R24d and R24f of docs/MigrationInterfaceRedesign-Plan.md. The keying tests matter most:
/// a report is identified by batch name plus mailbox address and BOTH are operator-supplied text,
/// so the failure modes are path traversal and one mailbox's report being served for another.
/// </remarks>
public class MigrationReportStoreTests
{
    private static MigrationReportStore StoreAt(string contentRoot)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(contentRoot);
        return new MigrationReportStore(env);
    }

    [Fact]
    public void TheStoreLivesWithTheOtherPerInstanceRuntimeFiles()
    {
        // Architectural invariant 3: config/ is per-instance runtime state that deploys exclude
        // and promotion never copies. A report is a point-in-time snapshot of ONE instance's view
        // of Exchange; copying it between dev and prod would be copying a stale answer.
        var store = StoreAt(Path.Combine("C:", "app"));

        Assert.Equal(
            Path.Combine("C:", "app", "config", "MigrationReports"),
            store.DirectoryPath);
    }

    [Theory]
    [InlineData("Wave1", "a@x.com")]
    [InlineData("../../etc", "a@x.com")]
    [InlineData("Wave1", "../../../secrets")]
    [InlineData("C:\\windows\\system32", "a@x.com")]
    [InlineData("a/b\\c:d*e?f", "g<h>i|j@x.com")]
    public void NoOperatorTextEverReachesTheFilesystem(string batchName, string email)
    {
        // The on-disk name is a hash, so hostile input cannot produce a hostile path. This is a
        // total whitelist by construction rather than a sanitiser with a "clean it up and carry
        // on" branch to get wrong.
        var name = MigrationReportStore.FileNameFor(batchName, email);

        Assert.Matches("^[0-9a-f]{64}\\.json$", name);
    }

    [Theory]
    [InlineData("../../etc/passwd", "a@x.com")]
    [InlineData("Wave1", "../../../boot.ini")]
    public void APathBuiltFromHostileInputStaysInsideTheStore(string batchName, string email)
    {
        var root = Path.Combine("C:", "app");
        var store = StoreAt(root);

        var path = store.PathFor(batchName, email);

        Assert.StartsWith(
            Path.GetFullPath(store.DirectoryPath) + Path.DirectorySeparatorChar,
            path,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TwoCasingsOfOneMailboxAreOneReport()
    {
        // Exchange is case-insensitive about both batch names and addresses. Treating them as two
        // reports would fetch the same twenty-minute call twice and leave two copies that can
        // disagree - which is exactly what R24c exists to prevent.
        Assert.Equal(
            MigrationReportStore.FileNameFor("Wave1", "Person@X.com"),
            MigrationReportStore.FileNameFor("wave1", "person@x.com"));
    }

    [Fact]
    public void DifferentMailboxesInOneBatchGetDifferentFiles()
    {
        Assert.NotEqual(
            MigrationReportStore.FileNameFor("Wave1", "a@x.com"),
            MigrationReportStore.FileNameFor("Wave1", "b@x.com"));
    }

    [Fact]
    public void OneMailboxInDifferentBatchesGetsDifferentFiles()
    {
        // A batch removed and recreated under the same name is a different migration (queue item
        // 3), and two different batches holding the same person are certainly different reports.
        Assert.NotEqual(
            MigrationReportStore.FileNameFor("Wave1", "a@x.com"),
            MigrationReportStore.FileNameFor("Wave2", "a@x.com"));
    }

    [Fact]
    public void TheKeyCannotBeSplitTwoWaysToCollide()
    {
        // The concatenation collision, and the reason the key folds in the batch name's LENGTH.
        // Without it ("ab","c") and ("a","bc") hash identically, and one mailbox's migration data
        // would be served under another's name. A separator that cannot appear in either field
        // does not exist, so the length is what disambiguates.
        Assert.NotEqual(
            MigrationReportStore.FileNameFor("ab", "c@x.com"),
            MigrationReportStore.FileNameFor("a", "bc@x.com"));

        Assert.NotEqual(
            MigrationReportStore.FileNameFor("Wave", "1a@x.com"),
            MigrationReportStore.FileNameFor("Wave1", "a@x.com"));
    }

    [Theory]
    [InlineData(null, "a@x.com")]
    [InlineData("", "a@x.com")]
    [InlineData("   ", "a@x.com")]
    [InlineData("Wave1", null)]
    [InlineData("Wave1", "")]
    public void AMissingHalfOfTheKeyIsRefusedRatherThanHashedAnyway(string? batchName, string? email)
    {
        // Hashing an empty half would give every report with that shape one shared file. Failing
        // here is the difference between a bug and silently serving the wrong person's data.
        Assert.ThrowsAny<ArgumentException>(() =>
            MigrationReportStore.FileNameFor(batchName!, email!));
    }

    [Fact]
    public void AReportExpiresTwelveHoursAfterItWasTaken()
    {
        // R24d. The outer bound; generation invalidation (R24b) still deletes immediately, because
        // a report whose batch was reloaded or recreated is misleading rather than merely old.
        var taken = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);

        Assert.Equal(taken.AddHours(12), MigrationReportStore.ExpiresAtUtc(taken));
        Assert.False(MigrationReportStore.IsExpired(taken, taken.AddHours(11).AddMinutes(59)));
        Assert.False(MigrationReportStore.IsExpired(taken, taken.AddHours(12)));
        Assert.True(MigrationReportStore.IsExpired(taken, taken.AddHours(12).AddSeconds(1)));
    }

    [Fact]
    public void AReportFromTheFutureIsNotTreatedAsExpired()
    {
        // Clock skew between a write and a read must not make a fresh report unservable. It fails
        // in the safe direction: too young, never too old.
        var taken = new DateTime(2026, 9, 28, 8, 0, 0, DateTimeKind.Utc);

        Assert.False(MigrationReportStore.IsExpired(taken, taken.AddMinutes(-5)));
    }
}
