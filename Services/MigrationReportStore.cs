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
}
