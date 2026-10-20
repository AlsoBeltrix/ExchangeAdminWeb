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
    private static (DateTime When, string What) Audit(int minute) =>
        (new DateTime(2026, 10, 19, 9, minute, 0, DateTimeKind.Utc), "audit-" + minute);

    private static (DateTime When, string What) Diag(int minute) =>
        (new DateTime(2026, 10, 19, 9, minute, 0, DateTimeKind.Utc), "diag-" + minute);

    private static List<(DateTime When, string What)> Merge(
        IReadOnlyCollection<(DateTime When, string What)> audit,
        IReadOnlyCollection<(DateTime When, string What)> diagnostics,
        int cap) =>
        AuditFirstMerge.Merge(audit, diagnostics, cap, e => e.When);

    [Fact]
    public void DiagnosticsNeverEvictAnAuditRecord()
    {
        // The defect: three audit records and a flood of diagnostics, cap of four. The old
        // FIFO dropped audit because audit was enqueued first.
        var audit = new[] { Audit(1), Audit(2), Audit(3) };
        var diagnostics = Enumerable.Range(10, 50).Select(Diag).ToArray();

        var merged = Merge(audit, diagnostics, cap: 4);

        Assert.Equal(4, merged.Count);
        foreach (var record in audit)
            Assert.Contains(record, merged);
    }

    [Fact]
    public void TheDiagnosticsThatSurvive_AreTheNewest()
    {
        // Room for one. It should be the most recent diagnostic, not whichever happened to be
        // read first.
        var merged = Merge([Audit(1)], [Diag(10), Diag(59), Diag(30)], cap: 2);

        Assert.Equal(2, merged.Count);
        Assert.Contains(Diag(59), merged);
    }

    [Fact]
    public void EverythingComesBackNewestFirst()
    {
        var merged = Merge([Audit(5), Audit(1)], [Diag(9), Diag(3)], cap: 10);

        Assert.Equal(
            merged.OrderByDescending(e => e.When).Select(e => e.What),
            merged.Select(e => e.What));
    }

    [Fact]
    public void AuditAloneCanStillExceedTheCap_AndIsNotSilentlyPadded()
    {
        // Audit is never dropped FOR A DIAGNOSTIC, which is not the same as never dropped. Too
        // many audit records is a real truncation the caller warns about; what matters is that
        // no diagnostic takes their place.
        var audit = Enumerable.Range(1, 10).Select(Audit).ToArray();

        var merged = Merge(audit, [Diag(59)], cap: 5);

        Assert.DoesNotContain(Diag(59), merged);
        Assert.All(merged, e => Assert.StartsWith("audit-", e.What, StringComparison.Ordinal));
    }

    [Fact]
    public void NoDiagnostics_IsJustTheAudit()
    {
        var merged = Merge([Audit(2), Audit(1)], [], cap: 10);
        Assert.Equal(2, merged.Count);
    }

    [Fact]
    public void FitDiagnostics_ReportsExactlyWhatFitsAfterTheAudit()
    {
        // Review of A20: split out from Merge so the page can say how many diagnostics were
        // dropped. The warning used to claim "showing last N of M", which described the old
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
        // Source scan: the merge rule above is only correct if the page actually separates the
        // two sources. The page method is long and unharnessed, so this pins the wiring.
        var page = RazorSyntax.CommentsBlanked(File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "AdminEventLog.razor")));

        Assert.Contains("AuditFirstMerge.FitDiagnostics(buffer.Count, diagnostics,", page, StringComparison.Ordinal);

        // Trace and extended lines must go to the diagnostics queue, not the audit one.
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(page, @"diagnostics\.Enqueue\(entry\);").Count);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(page, @"buffer\.Enqueue\(entry\);"));
    }
}
