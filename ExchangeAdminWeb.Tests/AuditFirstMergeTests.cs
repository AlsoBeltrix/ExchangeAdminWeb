using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A20: the Admin Event Log filled one FIFO with audit records and then
/// diagnostics, and trimmed it by dequeuing the oldest ENQUEUED entry - so every diagnostic
/// past the cap evicted an audit record, whatever its timestamp.
/// </summary>
/// <remarks>
/// The rule is extracted precisely so it can be tested as behaviour; the page's entry record
/// is private to the page. What this does NOT cover is the page's own wiring - that it hands
/// audit to the first argument and diagnostics to the second - which is a source scan below.
/// </remarks>
public class AuditFirstMergeTests
{
    private static (DateTime When, string What) Diag(int minute) =>
        (new DateTime(2026, 10, 19, 9, minute, 0, DateTimeKind.Utc), "diag-" + minute);

    [Fact]
    public void FitDiagnostics_ReportsExactlyWhatFitsAfterTheAudit()
    {
        // The page says how many diagnostics were dropped. The warning used to claim "showing last N of M", which described the old
        // single-buffer behaviour and no longer matched what the page did.
        var diagnostics = new[] { Diag(10), Diag(59), Diag(30) };

        var fitted = AuditFirstMerge.FitDiagnostics(auditCount: 8, diagnostics, cap: 10, e => e.When);

        Assert.Equal(2, fitted.Count);
        Assert.Contains(Diag(59), fitted);
        Assert.Contains(Diag(30), fitted);
    }

    [Fact]
    public void FitDiagnostics_KeepsNothingWhenAuditAlreadyFillsTheCap()
    {
        var fitted = AuditFirstMerge.FitDiagnostics(auditCount: 10, [Diag(59)], cap: 10, e => e.When);
        Assert.Empty(fitted);
    }

    [Fact]
    public void ThePageWarnsAboutWhichKindOfEntryWasDropped()
    {
        // Source scan. The point of the split is the message, and "showing last N of M" would
        // compile perfectly well while saying something untrue about what was kept.
        var page = RazorSyntax.CommentsBlanked(File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "AdminEventLog.razor")));

        Assert.Contains("audit events; the oldest were dropped", page, StringComparison.Ordinal);
        Assert.Contains("diagnostic entr", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Showing last {MaxTotalEvents}", page, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePageHandsAuditAndDiagnosticsToSeparateBuffers()
    {
        // Source scan: the fitting rule above is only correct if the page actually separates the
        // two sources. The page method is long and unharnessed, so this pins the wiring.
        var page = RazorSyntax.CommentsBlanked(File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "AdminEventLog.razor")));

        Assert.Contains("AuditFirstMerge.FitDiagnostics(buffer.Count, diagnostics,", page, StringComparison.Ordinal);

        // Trace and extended lines must go to the diagnostics queue, not the audit one.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(page, @"diagnostics\.Enqueue\(entry\);").Count);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, @"buffer\.Enqueue\(entry\);"));
    }
}
