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

    // ---- round-trip, expiry and sweeping, against a real temporary directory -------------------

    private static (MigrationReportStore Store, string Root) TempStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "mrs_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return (StoreAt(root), root);
    }

    [Fact]
    public void AStoredReportComesBackByteForByte()
    {
        // R24c and R31e: the modal and the export zip read the SAME bytes. Two artifacts labelled
        // "the report" for one mailbox that do not match is the failure this prevents, so the
        // round trip has to preserve line endings, tabs and non-ASCII exactly.
        var (store, root) = TempStore();
        try
        {
            var taken = DateTime.UtcNow;
            var text = "Line one\r\nLine two\ttabbed\nUnicode: \u00e9\nend";

            store.Write("Wave1", "a@x.com", text, taken);
            var back = store.TryRead("Wave1", "a@x.com", taken.AddMinutes(1));

            Assert.NotNull(back);
            Assert.Equal(text, back!.Text);
            Assert.Equal("Wave1", back.BatchName);
            Assert.Equal("a@x.com", back.EmailAddress);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AnExpiredReportReadsAsAbsentRatherThanStale()
    {
        // R24d, fail-closed on serving. An operator cannot tell a stale report from a fresh one by
        // looking at it, so an expired one is not rendered at all - the caller refetches, which
        // costs twenty minutes of Exchange time and is always correct.
        var (store, root) = TempStore();
        try
        {
            Plant(store, "Wave1", "a@x.com", "old", DateTime.UtcNow.AddHours(-13));

            Assert.Null(store.TryRead("Wave1", "a@x.com", DateTime.UtcNow));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ACorruptFileReadsAsAbsentAndDoesNotThrow()
    {
        // A half-written file must not take the page down, and must not be served either.
        var (store, root) = TempStore();
        try
        {
            Directory.CreateDirectory(store.DirectoryPath);
            File.WriteAllText(store.PathFor("Wave1", "a@x.com"), "{ not json");

            Assert.Null(store.TryRead("Wave1", "a@x.com", DateTime.UtcNow));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DeleteRemovesOnlyTheMailboxNamed()
    {
        // R24b deletes the report whose rows were replaced. Taking a neighbour's with it would
        // throw away a twenty-minute fetch nobody asked to discard.
        var (store, root) = TempStore();
        try
        {
            var now = DateTime.UtcNow;
            store.Write("Wave1", "a@x.com", "A", now);
            store.Write("Wave1", "b@x.com", "B", now);

            store.Delete("Wave1", "a@x.com");

            Assert.Null(store.TryRead("Wave1", "a@x.com", now));
            Assert.NotNull(store.TryRead("Wave1", "b@x.com", now));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DeletingAReportThatIsNotThereIsNotAnError()
    {
        // Fail-soft on purging: the page invalidates on every row reload and usually there is
        // nothing to remove.
        var (store, root) = TempStore();
        try { store.Delete("Wave1", "nobody@x.com"); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TheSweepRemovesTheExpiredAndKeepsTheRest()
    {
        var (store, root) = TempStore();
        try
        {
            var now = DateTime.UtcNow;

            // The expired one is planted directly rather than written through the store, because
            // Write sweeps and would remove it as part of storing it. Planting it is also the
            // real scenario: a report written thirteen hours ago that outlived a restart.
            // BOTH planted, so this exercises Sweep alone. Storing the fresh one through Write
            // would sweep the expired one away as a side effect and leave Sweep nothing to do -
            // which is correct behaviour and is covered by AWriteSweepsTheExpiredAtTheSameTime,
            // but it is not what this test is about.
            Plant(store, "Wave1", "old@x.com", "old", now.AddHours(-13));
            Plant(store, "Wave1", "fresh@x.com", "fresh", now);

            var removed = store.Sweep(now);

            Assert.Equal(1, removed);
            Assert.Null(store.TryRead("Wave1", "old@x.com", now));
            Assert.NotNull(store.TryRead("Wave1", "fresh@x.com", now));
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>
    /// Writes a report file directly, bypassing <see cref="MigrationReportStore.Write"/> and the
    /// sweep it performs. Used to set up a report that is ALREADY expired, which Write cannot
    /// produce because it sweeps first.
    /// </summary>
    private static void Plant(
        MigrationReportStore store, string batchName, string email, string text, DateTime takenUtc)
    {
        Directory.CreateDirectory(store.DirectoryPath);
        File.WriteAllText(
            store.PathFor(batchName, email),
            System.Text.Json.JsonSerializer.Serialize(
                new MigrationReport(batchName, email, takenUtc, text)));
    }

    [Fact]
    public void TheSweepRemovesAFileItCannotParse()
    {
        // An unreadable file can never be served - TryRead fails closed on it - so keeping it is
        // pure disk use.
        var (store, root) = TempStore();
        try
        {
            Directory.CreateDirectory(store.DirectoryPath);
            File.WriteAllText(Path.Combine(store.DirectoryPath, "deadbeef.json"), "not json at all");

            Assert.Equal(1, store.Sweep(DateTime.UtcNow));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SweepingAStoreThatWasNeverWrittenToIsHarmless()
    {
        // Called once at application start, before anything has been stored.
        var (store, root) = TempStore();
        try { Assert.Equal(0, store.Sweep(DateTime.UtcNow)); }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AWriteSweepsTheExpiredAtTheSameTime()
    {
        // R24d: every touch sweeps, because there is no scheduler in this app and there will not
        // be one (owner, 2026-08-04). A store nobody opens would otherwise keep last week's.
        var (store, root) = TempStore();
        try
        {
            var now = DateTime.UtcNow;
            Plant(store, "Wave1", "old@x.com", "old", now.AddHours(-13));
            var expiredPath = store.PathFor("Wave1", "old@x.com");

            store.Write("Wave1", "new@x.com", "new", now);

            // Asserted on the FILE, not on TryRead. A mutation probe showed the TryRead form was
            // vacuous: TryRead refuses an expired report whether or not it was ever swept, so the
            // test passed with the sweep deleted from Write. Sweeping is about reclaiming disk,
            // and only the file's absence proves it happened.
            Assert.False(File.Exists(expiredPath), "the write did not sweep the expired report");
            Assert.NotNull(store.TryRead("Wave1", "new@x.com", now));
        }
        finally { Directory.Delete(root, true); }
    }

    private static Dictionary<string, string> ReadZip(byte[] bytes)
    {
        using var buffer = new MemoryStream(bytes);
        using var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Read);

        return zip.Entries.ToDictionary(
            e => e.Name,
            e => new StreamReader(e.Open()).ReadToEnd(),
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheZipCarriesTheSameBytesTheDialogRenders()
    {
        // R31e. The export packages what the store holds - it writes nothing new - so the file an
        // operator downloads and the text they were reading cannot be different documents.
        var (store, root) = TempStore();
        try
        {
            var now = DateTime.UtcNow;
            store.Write("Wave1", "a@x.com", "REPORT A\r\nline two", now);
            store.Write("Wave1", "b@x.com", "REPORT B", now);

            var entries = ReadZip(store.BuildExportZip("Wave1", ["a@x.com", "b@x.com"], now)!);

            Assert.Equal("REPORT A\r\nline two", entries["a@x.com.txt"]);
            Assert.Equal("REPORT B", entries["b@x.com.txt"]);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AReportThatExpiredBeforeTheDownloadIsNamedRatherThanSilentlyDropped()
    {
        // R31f, and the failure it prevents: a zip that quietly contains nine files when ten were
        // asked for. The operator cannot tell WHICH person is missing, and a gap in migration
        // diagnostics reads as "nothing to report about them".
        var (store, root) = TempStore();
        try
        {
            var now = DateTime.UtcNow;
            store.Write("Wave1", "present@x.com", "here", now);
            Plant(store, "Wave1", "gone@x.com", "old", now.AddHours(-13));

            var entries = ReadZip(store.BuildExportZip("Wave1", ["present@x.com", "gone@x.com"], now)!);

            Assert.True(entries.ContainsKey("present@x.com.txt"));
            Assert.False(entries.ContainsKey("gone@x.com.txt"));

            var manifest = entries["manifest.txt"];
            Assert.Contains("NOT INCLUDED (1)", manifest, StringComparison.Ordinal);
            Assert.Contains("gone@x.com", manifest, StringComparison.Ordinal);
            Assert.Contains("expired", manifest, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void NothingToPackageIsRefusedRatherThanDeliveredAsAnEmptyArchive()
    {
        // R31f. An empty zip looks like a successful export of nothing. Returning null lets the
        // caller say what actually happened.
        var (store, root) = TempStore();
        try
        {
            Assert.Null(store.BuildExportZip("Wave1", ["nobody@x.com"], DateTime.UtcNow));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TheManifestAlwaysNamesWhatIsInside()
    {
        // Even a complete export carries the list, so the zip is self-describing months later
        // when nobody remembers which batch it came from.
        var (store, root) = TempStore();
        try
        {
            var now = DateTime.UtcNow;
            store.Write("Wave1", "a@x.com", "A", now);

            var manifest = ReadZip(store.BuildExportZip("Wave1", ["a@x.com"], now)!)["manifest.txt"];

            Assert.Contains("Batch: Wave1", manifest, StringComparison.Ordinal);
            Assert.Contains("Included (1)", manifest, StringComparison.Ordinal);
            Assert.Contains("a@x.com", manifest, StringComparison.Ordinal);
            Assert.DoesNotContain("NOT INCLUDED", manifest, StringComparison.Ordinal);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AnAddressCannotBecomeAPathInsideTheArchive()
    {
        // The address is operator-supplied and this becomes a path something will extract.
        var (store, root) = TempStore();
        try
        {
            var now = DateTime.UtcNow;
            var hostile = "../../evil@x.com";
            store.Write("Wave1", hostile, "x", now);

            using var buffer = new MemoryStream(store.BuildExportZip("Wave1", [hostile], now)!);
            using var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Read);

            // FullName, not Name: Name already strips any path, so asserting on it would pass on
            // an entry that carries one. The traversal risk is the SEPARATORS, not the dots -
            // ".._.._evil@x.com" extracts as one harmless filename - so that is what is checked,
            // along with the entry being a bare filename by the framework's own reckoning.
            foreach (var entry in zip.Entries)
            {
                Assert.DoesNotContain('/', entry.FullName);
                Assert.DoesNotContain('\\', entry.FullName);
                Assert.Equal(entry.FullName, Path.GetFileName(entry.FullName));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ARequestedMailboxIsPackagedOnceEvenIfAskedForTwice()
    {
        var (store, root) = TempStore();
        try
        {
            var now = DateTime.UtcNow;
            store.Write("Wave1", "a@x.com", "A", now);

            var entries = ReadZip(store.BuildExportZip("Wave1", ["a@x.com", "A@X.COM"], now)!);

            Assert.Equal(2, entries.Count); // the report plus the manifest
        }
        finally { Directory.Delete(root, true); }
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
