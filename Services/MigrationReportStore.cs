using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace ExchangeAdminWeb.Services;

/// <summary>A stored per-mailbox migration report: its bytes and when they were taken.</summary>
public sealed record MigrationReport(string BatchName, string EmailAddress, DateTime FetchedAtUtc, string Text);

/// <summary>
/// Owns where a per-mailbox migration report lives on disk, how it is named, and when it expires.
/// </summary>
/// <remarks>
/// <para>
/// R24c of docs/MigrationInterfaceRedesign-Plan.md: <b>there is exactly one copy and every
/// consumer reads it.</b> Until S6 the report was a <c>string?</c> field on Migration.razor - one
/// report, held in the operator's circuit memory. That is fine for one and wrong for many:
/// reports run to several megabytes, so a cache of fifty would pin hundreds of megabytes per
/// operator for the life of the circuit. It is also what makes R31e possible, where the export zip
/// and the modal must contain the same bytes: two artifacts labelled "the report" for one mailbox
/// that do not match is the failure this prevents.
/// </para>
/// <para>
/// <b>Why a store at all, rather than a bigger cache.</b> <c>Get-MigrationUserStatistics</c> can
/// take twenty minutes or more on a long or problematic migration (R24a), so closing and reopening
/// the dialog must never re-run it. That is a correctness requirement about operator time, not a
/// caching optimisation, and it is why "fetch again" is a deliberate act with its own button.
/// </para>
/// <para>
/// <b>Keying, and why it is a hash.</b> A report is identified by batch name plus mailbox address,
/// and both are operator-supplied text - a batch name is whatever whoever created it typed.
/// Neither may reach the filesystem as written. The on-disk name is a hash of the pair; the real
/// identity lives inside the file. That is a total whitelist by construction rather than a
/// sanitiser with a "clean it up and carry on" branch to get wrong, which is the same reasoning
/// <see cref="MessageTraceExportStore"/> applies to its GUID job ids.
/// </para>
/// <para>
/// <b>Retention: R24d.</b> Twelve hours, swept whenever the store is touched and again at
/// application start. There is no scheduler in this app and there will not be one (owner,
/// 2026-08-04), so a store nobody opens would otherwise keep last week's reports. <b>Fail-soft on
/// purging, fail-closed on serving:</b> a delete that fails during a sweep is logged and skipped
/// and must never fail the read it interrupted, and a report that cannot be shown to be current is
/// not rendered at all.
/// </para>
/// <para>
/// <b>Location.</b> Under the instance's own <c>config/</c> directory, alongside the jobs and usage
/// databases - per-instance runtime state that deploys exclude and promotion never copies
/// (.agents/repo-guidance.md, Architectural Invariants 3). A report is a point-in-time snapshot of
/// one instance's view; copying it between dev and prod would be copying a stale answer.
/// </para>
/// </remarks>
public sealed class MigrationReportStore
{
    /// <summary>
    /// R24d. The outer bound on a stored report's life. A constant rather than a setting for the
    /// same reason <see cref="MessageTraceExportStore.RetentionDays"/> is: the sweeper and
    /// anything that tells the operator how old a report may be read the same number, so a second
    /// knob could only create disagreement.
    /// </summary>
    public static readonly TimeSpan Retention = TimeSpan.FromHours(12);

    private readonly IWebHostEnvironment _env;
    private readonly ILogger<MigrationReportStore>? _logger;

    public MigrationReportStore(IWebHostEnvironment env, ILogger<MigrationReportStore>? logger = null)
    {
        _env = env;
        _logger = logger;
    }

    /// <summary>The store directory. Does not create it.</summary>
    public string DirectoryPath =>
        Path.Combine(_env.ContentRootPath, "config", "MigrationReports");

    /// <summary>
    /// The on-disk file name for one mailbox's report in one batch: a hash of the pair, never the
    /// operator's text. Case-insensitive, because Exchange is about both batch names and
    /// addresses and two casings of one mailbox are one report, not two.
    /// </summary>
    public static string FileNameFor(string batchName, string emailAddress)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchName);
        ArgumentException.ThrowIfNullOrWhiteSpace(emailAddress);

        // A separator that cannot occur in either field would be ideal and does not exist, so the
        // LENGTH of the first field is folded in. Without it, ("ab", "c") and ("a", "bc") hash the
        // same and one mailbox's report would serve another's - the classic concatenation
        // collision, and here it would mean showing an operator someone else's migration data.
        var batch = batchName.Trim().ToLowerInvariant();
        var email = emailAddress.Trim().ToLowerInvariant();
        var key = $"{batch.Length}:{batch}\u0000{email}";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return $"{Convert.ToHexString(hash).ToLowerInvariant()}.json";
    }

    /// <summary>
    /// The full path a report would occupy, with a containment check that refuses rather than
    /// corrects anything resolving outside <see cref="DirectoryPath"/> (R24f).
    /// </summary>
    public string PathFor(string batchName, string emailAddress)
    {
        var dir = DirectoryPath;
        var candidate = Path.Combine(dir, FileNameFor(batchName, emailAddress));

        // The hash makes this unreachable. It is here because "unreachable" is a property of
        // today's key derivation, and the cost of being wrong about that is path traversal with
        // operator-supplied text.
        var fullDir = Path.GetFullPath(dir);
        var fullPath = Path.GetFullPath(candidate);
        if (!fullPath.StartsWith(fullDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Composed migration report path escapes the store directory.");
        }

        return fullPath;
    }

    /// <summary>True when <paramref name="fetchedAtUtc"/> is older than <see cref="Retention"/>.</summary>
    public static bool IsExpired(DateTime fetchedAtUtc, DateTime nowUtc) =>
        nowUtc - fetchedAtUtc > Retention;

    /// <summary>When a report taken at <paramref name="fetchedAtUtc"/> stops being servable.</summary>
    public static DateTime ExpiresAtUtc(DateTime fetchedAtUtc) => fetchedAtUtc.Add(Retention);

    /// <summary>Stores <paramref name="text"/> as the report for one mailbox, replacing any prior copy.</summary>
    public void Write(string batchName, string emailAddress, string text, DateTime fetchedAtUtc)
    {
        var path = PathFor(batchName, emailAddress);
        Directory.CreateDirectory(DirectoryPath);

        // R24d: every touch sweeps. There is no scheduler in this app and there will not be one,
        // so a store nobody opens would otherwise keep last week's reports forever.
        //
        // BEFORE the write, not after. Sweeping afterwards deletes the report just stored
        // whenever its own timestamp is already past the window - which is not hypothetical,
        // because the caller passes the time the Exchange call STARTED and that call can run for
        // twenty minutes or more.
        Sweep(DateTime.UtcNow);

        var report = new MigrationReport(batchName, emailAddress, fetchedAtUtc, text);
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(report));
    }

    /// <summary>
    /// The stored report, or null when there is none, it cannot be read, or it has expired.
    /// </summary>
    /// <remarks>
    /// <b>Fail-closed on serving (R24d).</b> Anything that cannot be shown to be a current,
    /// well-formed report reads as absent - a corrupt file, an unparseable one, an expired one.
    /// The caller then fetches, which costs twenty minutes of Exchange time but is always
    /// correct. Rendering a report that cannot be shown to be current is the failure this avoids:
    /// the operator cannot tell a stale report from a fresh one by looking at it.
    /// </remarks>
    public MigrationReport? TryRead(string batchName, string emailAddress, DateTime nowUtc)
    {
        string path;
        try
        {
            path = PathFor(batchName, emailAddress);
        }
        catch (ArgumentException)
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
                return null;

            var report = System.Text.Json.JsonSerializer.Deserialize<MigrationReport>(File.ReadAllText(path));
            if (report == null || IsExpired(report.FetchedAtUtc, nowUtc))
                return null;

            return report;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger?.LogWarning(ex, "Migration report at {Path} could not be read; treating as absent", path);
            return null;
        }
    }

    /// <summary>
    /// Deletes one mailbox's stored report. R24b: called when the rows it describes are replaced,
    /// because a batch removed and recreated under the same name is a different migration.
    /// </summary>
    public void Delete(string batchName, string emailAddress)
    {
        try
        {
            var path = PathFor(batchName, emailAddress);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Fail-soft on purging. A report that could not be deleted is still expired-checked on
            // read, so the worst case is disk use, never a stale report served as current.
            _logger?.LogWarning(ex, "Migration report for {Email} could not be deleted", emailAddress);
        }
    }

    /// <summary>
    /// Deletes every stored report. Used when a batch's rows are reloaded and the page cannot
    /// cheaply enumerate which mailboxes had reports.
    /// </summary>
    public void DeleteAll() => SweepInternal(_ => true);

    /// <summary>
    /// Deletes reports older than <see cref="Retention"/>. Called on every write and once at
    /// application start. Returns how many were removed.
    /// </summary>
    public int Sweep(DateTime nowUtc) => SweepInternal(report => IsExpired(report.FetchedAtUtc, nowUtc));

    /// <summary>
    /// Builds the export zip for <paramref name="emailAddresses"/> from what the store holds, or
    /// null when it holds none of them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// R31e and R31f. <b>The zip writes nothing new</b> - it packages the same bytes the dialog
    /// renders, so the file an operator downloads and the text they read cannot disagree. It is
    /// assembled at CLICK time rather than when the job finished, because the bulk-job runner
    /// persists row outcomes and not files.
    /// </para>
    /// <para>
    /// <b>A report that expired between the job finishing and the download being clicked is NAMED
    /// IN THE MANIFEST, not silently omitted.</b> A zip that quietly contains nine files when ten
    /// were requested is the failure this avoids: the operator has no way to tell which person is
    /// missing, and a gap in migration diagnostics reads as "nothing to report".
    /// </para>
    /// <para>
    /// <b>Nothing to package returns null</b> so the caller can say so, rather than delivering an
    /// empty archive that looks like a successful export of nothing.
    /// </para>
    /// </remarks>
    public byte[]? BuildExportZip(string batchName, IEnumerable<string> emailAddresses, DateTime nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(batchName);
        ArgumentNullException.ThrowIfNull(emailAddresses);

        var included = new List<string>();
        var missing = new List<string>();
        var reports = new List<MigrationReport>();

        foreach (var email in emailAddresses.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var report = TryRead(batchName, email, nowUtc);
            if (report == null)
                missing.Add(email);
            else
            {
                reports.Add(report);
                included.Add(email);
            }
        }

        if (reports.Count == 0)
            return null;

        using var buffer = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(buffer, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var report in reports)
            {
                // One text file per report (R31), which is what makes a partial result
                // unambiguous: the operator sees which mailboxes are present by looking.
                var entry = zip.CreateEntry(SafeEntryName(report.EmailAddress) + ".txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write(report.Text);
            }

            var manifest = zip.CreateEntry("manifest.txt");
            using var manifestWriter = new StreamWriter(manifest.Open());
            manifestWriter.WriteLine($"Batch: {batchName}");
            manifestWriter.WriteLine($"Assembled (UTC): {nowUtc:yyyy-MM-dd HH:mm:ss}");
            manifestWriter.WriteLine();
            manifestWriter.WriteLine($"Included ({included.Count}):");
            foreach (var email in included)
                manifestWriter.WriteLine($"  {email}");

            if (missing.Count > 0)
            {
                manifestWriter.WriteLine();
                manifestWriter.WriteLine($"NOT INCLUDED ({missing.Count}):");
                manifestWriter.WriteLine(
                    "  These reports were not available when the zip was assembled - most likely");
                manifestWriter.WriteLine(
                    $"  they expired, since a stored report lives {Retention.TotalHours:0} hours.");
                manifestWriter.WriteLine("  Re-run the export for these mailboxes to fetch them again.");
                foreach (var email in missing)
                    manifestWriter.WriteLine($"  {email}");
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// A zip entry name derived from an address. The address is operator-supplied text and this
    /// becomes a path inside an archive that something will extract, so everything outside a
    /// known-safe set is replaced rather than trusted.
    /// </summary>
    private static string SafeEntryName(string emailAddress) =>
        new(emailAddress.Select(c =>
            char.IsLetterOrDigit(c) || c is '-' or '_' or '.' or '@' ? c : '_').ToArray());

    private int SweepInternal(Func<MigrationReport, bool> shouldDelete)
    {
        var removed = 0;

        try
        {
            if (!Directory.Exists(DirectoryPath))
                return 0;

            foreach (var path in Directory.EnumerateFiles(DirectoryPath, "*.json"))
            {
                MigrationReport? report;
                try
                {
                    report = System.Text.Json.JsonSerializer.Deserialize<MigrationReport>(File.ReadAllText(path));
                }
                catch (System.Text.Json.JsonException)
                {
                    // Unparseable, so it can never be served - TryRead fails closed on it - and
                    // keeping it is pure disk use. Deleted, not skipped. An earlier version caught
                    // this alongside the IO failures and skipped it, which contradicted the
                    // comment two lines away and left corrupt files forever.
                    report = null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // R24d, fail-soft: a file that cannot even be READ is left alone and the
                    // sweep continues. It must never fail the read that triggered it.
                    _logger?.LogWarning(ex, "Migration report at {Path} could not be read during sweep", path);
                    continue;
                }

                try
                {
                    if (report == null || shouldDelete(report))
                    {
                        File.Delete(path);
                        removed++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Fail-soft on purging: logged and skipped. The worst case is disk use,
                    // never a stale report served as current, because TryRead checks expiry too.
                    _logger?.LogWarning(ex, "Migration report at {Path} could not be deleted", path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger?.LogWarning(ex, "Migration report sweep could not enumerate {Directory}", DirectoryPath);
        }

        return removed;
    }
}
