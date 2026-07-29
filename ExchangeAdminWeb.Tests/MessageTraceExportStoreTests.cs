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
        // Pinned to the host scheduled task's window. Descriptive only - the app never deletes -
        // but the email promises this date, so a silent drift would misinform the operator.
        var submitted = new DateTime(2026, 7, 29, 14, 3, 9, DateTimeKind.Utc);

        Assert.Equal(new DateTime(2026, 8, 28, 14, 3, 9, DateTimeKind.Utc), _store.ExpiresAtUtc(submitted));
        Assert.Equal(30, MessageTraceExportStore.RetentionDays);
    }
}
