using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Slice-1 coverage for the Message Analysis export resolver
/// (docs/MessageTraceDownloadLink-Plan.md). The store is the single owner of the export directory,
/// the filename convention, and jobId validation, shared by the detail-export writer and the
/// Downloadable Reports page - so these tests pin the on-disk contract that keeps the two from
/// drifting apart, and prove traversal is rejected rather than merely unresolved.
/// </summary>
public sealed class MessageTraceExportStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly MessageTraceExportStore _store;

    public MessageTraceExportStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"mtes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new MessageTraceExportStore(Config(_tempDir));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static IConfiguration Config(string? logRoot) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = logRoot,
        }).Build();

    private static string NewJobId() => Guid.NewGuid().ToString("N");

    [Fact]
    public void DirectoryPath_IsUnderTheAuditLogRoot()
    {
        Assert.Equal(Path.Combine(_tempDir, "ExchangeAdminWeb", "MessageTraceExports"), _store.DirectoryPath);
    }

    [Fact]
    public void DirectoryPath_WithoutLogRoot_FailsLoud()
    {
        var store = new MessageTraceExportStore(Config(null));

        var ex = Assert.Throws<InvalidOperationException>(() => store.DirectoryPath);
        Assert.Equal(AuditLogRoot.UnsetMessage, ex.Message);
    }

    [Fact]
    public void FileNameFor_MatchesTheNameAlreadyOnDisk()
    {
        // Pins the exact format the detail-export processor has always written. A rename here
        // orphans every export produced to date, which is only visible as an empty reports page.
        var jobId = NewJobId();
        var submitted = new DateTime(2026, 7, 29, 14, 3, 9, DateTimeKind.Utc);

        Assert.Equal($"MessageTraceDetail_{jobId}_20260729-140309.csv", _store.FileNameFor(jobId, submitted));
    }

    [Fact]
    public void PathFor_ComposesUnderTheExportDirectory()
    {
        var jobId = NewJobId();
        var submitted = new DateTime(2026, 7, 29, 14, 3, 9, DateTimeKind.Utc);

        var path = _store.PathFor(jobId, submitted);

        Assert.Equal(Path.Combine(_store.DirectoryPath, _store.FileNameFor(jobId, submitted)), path);
    }

    [Fact]
    public void TryResolve_MissingFile_ReturnsFalse_DoesNotThrow_DoesNotCreateDirectory()
    {
        // The normal state once the host retention task has run: an ordinary "expired" outcome,
        // never an error, and the reader must not conjure the directory as a side effect.
        var resolved = _store.TryResolve(NewJobId(), DateTime.UtcNow, out var path);

        Assert.False(resolved);
        Assert.NotEmpty(path);
        Assert.False(Directory.Exists(_store.DirectoryPath));
    }

    [Fact]
    public void TryResolve_ExistingFile_ReturnsTrueAndThePath()
    {
        var jobId = NewJobId();
        var submitted = new DateTime(2026, 7, 29, 14, 3, 9, DateTimeKind.Utc);
        Directory.CreateDirectory(_store.DirectoryPath);
        var expected = Path.Combine(_store.DirectoryPath, _store.FileNameFor(jobId, submitted));
        File.WriteAllText(expected, "csv");

        Assert.True(_store.TryResolve(jobId, submitted, out var path));
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData(@"..\..\windows\win.ini")]
    [InlineData(@"../../windows/win.ini")]
    [InlineData(@"C:\windows\win.ini")]
    [InlineData(@"\\server\share\win.ini")]
    [InlineData(@"abc\def")]
    [InlineData("abc/def")]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("0123456789abcdef0123456789abcde")]   // 31 chars
    [InlineData("0123456789abcdef0123456789abcdef0")]  // 33 chars
    [InlineData("0123456789abcdef0123456789abcdeg")]   // 32 chars, non-hex
    public void InvalidJobId_IsRejected_NotJustUnresolved(string jobId)
    {
        // Assert REJECTION, not a false return: a refactor must not be able to satisfy this by
        // quietly failing to find the traversed file. An invalid id is a caller bug, and reporting
        // it as an expired export would hide it.
        Assert.Throws<ArgumentException>(() => _store.PathFor(jobId, DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => _store.FileNameFor(jobId, DateTime.UtcNow));
        Assert.Throws<ArgumentException>(() => _store.TryResolve(jobId, DateTime.UtcNow, out _));
    }

    [Fact]
    public void ValidJobId_IsAcceptedInEitherCase()
    {
        Assert.NotEmpty(_store.PathFor("0123456789ABCDEF0123456789abcdef", DateTime.UtcNow));
    }

    [Fact]
    public void ExpiresAtUtc_IsThirtyDaysAfterSubmission()
    {
        // The email and the reports page promise this date and PruneExpired now enforces the same
        // window, so these are one number by construction rather than two that could drift.
        var submitted = new DateTime(2026, 7, 29, 14, 3, 9, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 8, 28, 14, 3, 9, DateTimeKind.Utc), _store.ExpiresAtUtc(submitted));
        Assert.Equal(30, MessageTraceExportStore.RetentionDays);
    }

    // -------------------------------------------------------------------------
    // PruneExpired. Retention moved in-process after the owner ruled there are and will be no
    // scheduled tasks (docs/AdminBulkJobs-Plan.md Part A).
    //
    // The risk here is NOT failing to delete an expired export. It is deleting something else:
    // this sweep runs inside the audit log root. Most of what follows asserts what SURVIVES.
    // -------------------------------------------------------------------------

    /// <summary>Writes a file into the export directory with a controlled age.</summary>
    private string SeedFile(string fileName, DateTime lastWriteUtc)
    {
        Directory.CreateDirectory(_store.DirectoryPath);
        var path = Path.Combine(_store.DirectoryPath, fileName);
        File.WriteAllText(path, "x");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }

    private static string ExportName(string? jobId = null) =>
        $"MessageTraceDetail_{jobId ?? NewJobId()}_20260101-120000.csv";

    [Fact]
    public void PruneExpired_DeletesAnExportPastTheWindow()
    {
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        var old = SeedFile(ExportName(), now.AddDays(-45));

        var removed = _store.PruneExpired(now);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(old));
    }

    [Fact]
    public void PruneExpired_KeepsAnExportInsideTheWindow()
    {
        // The two real files on the ADI host were 6 days old when this landed. They must survive.
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        var recent = SeedFile(ExportName(), now.AddDays(-6));

        Assert.Equal(0, _store.PruneExpired(now));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public void PruneExpired_KeepsAFileExactlyAtTheBoundary()
    {
        // A file whose age equals the window is not yet past it. Asserted so the comparison cannot
        // silently flip to inclusive and delete a day early - the reports page would then say
        // Available for a file already gone.
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        var boundary = SeedFile(ExportName(), now.AddDays(-MessageTraceExportStore.RetentionDays));

        Assert.Equal(0, _store.PruneExpired(now));
        Assert.True(File.Exists(boundary));
    }

    [Fact]
    public void PruneExpired_LeavesANonExportFileAloneHoweverOld()
    {
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        var stray = SeedFile("operator-notes.csv", now.AddDays(-900));

        Assert.Equal(0, _store.PruneExpired(now));
        Assert.True(File.Exists(stray));
    }

    [Fact]
    public void PruneExpired_LeavesANearMissFilenameAlone()
    {
        // Right prefix, wrong id length - not something this app's export path wrote.
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        var nearMiss = SeedFile("MessageTraceDetail_abc_20260101-120000.csv", now.AddDays(-900));

        Assert.Equal(0, _store.PruneExpired(now));
        Assert.True(File.Exists(nearMiss));
    }

    [Fact]
    public void PruneExpired_NeverTouchesAuditLogsAboveTheExportDirectory()
    {
        // The deletion this must never make. The export directory sits inside the audit log root,
        // which is why the sweep matches an anchored filename pattern and does not recurse.
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(_store.DirectoryPath);

        var auditDir = Path.Combine(_tempDir, "ExchangeAdminWeb");
        var auditLog = Path.Combine(auditDir, "exchangeadmin_20240101.jsonl");
        File.WriteAllText(auditLog, "{}");
        File.SetLastWriteTimeUtc(auditLog, now.AddDays(-900));

        _store.PruneExpired(now);

        Assert.True(File.Exists(auditLog));
    }

    [Fact]
    public void PruneExpired_DoesNotRecurseIntoSubdirectories()
    {
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(_store.DirectoryPath);
        var sub = Path.Combine(_store.DirectoryPath, "archive");
        Directory.CreateDirectory(sub);
        var nested = Path.Combine(sub, ExportName());
        File.WriteAllText(nested, "x");
        File.SetLastWriteTimeUtc(nested, now.AddDays(-900));

        Assert.Equal(0, _store.PruneExpired(now));
        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void PruneExpired_DeletesOnlyTheExpiredOnesFromAMixedDirectory()
    {
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        var expiredA = SeedFile(ExportName(), now.AddDays(-31));
        var expiredB = SeedFile(ExportName(), now.AddDays(-400));
        var fresh = SeedFile(ExportName(), now.AddDays(-1));
        var stray = SeedFile("keepme.txt", now.AddDays(-900));

        Assert.Equal(2, _store.PruneExpired(now));

        Assert.False(File.Exists(expiredA));
        Assert.False(File.Exists(expiredB));
        Assert.True(File.Exists(fresh));
        Assert.True(File.Exists(stray));
    }

    [Fact]
    public void PruneExpired_IsQuietWhenTheExportDirectoryDoesNotExist()
    {
        // The ordinary state before the first export is ever written. Startup must not care.
        Assert.Equal(0, _store.PruneExpired(DateTime.UtcNow));
    }

    [Fact]
    public void PruneExpired_DoesNotCreateTheExportDirectory()
    {
        _store.PruneExpired(DateTime.UtcNow);

        Assert.False(Directory.Exists(_store.DirectoryPath));
    }

    [Fact]
    public void PruneExpired_WithoutLogRoot_ReturnsZeroRatherThanThrowing()
    {
        // An unset Audit:LogRoot is fatal elsewhere by design. This path runs at startup, so it
        // must not be what surfaces it - retention must never be able to stop the app booting.
        var store = new MessageTraceExportStore(Config(null));

        Assert.Equal(0, store.PruneExpired(DateTime.UtcNow));
    }
}
