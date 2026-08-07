using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Source-level guards for how the Message Analysis page PRESENTS its two exports.
/// </summary>
/// <remarks>
/// TRIPWIRES, NOT BEHAVIOURAL COVERAGE, and deliberately so: this repo has no bUnit harness, the
/// assertions below are all about markup, and every recent defect in this area was the page being
/// wrong while the service was right.
///
/// The defect these guard is not a crash. Every export control worked exactly as designed, and a
/// competent operator still concluded the feature was broken: "it's unclear how to get anything...
/// it says export to get them all but select all is capped at 50 and the download button doesn't
/// work" (owner, 2026-08-07, on the deployed app).
///
/// Two causes, both purely presentational:
///   1. "export to get them all" sat on the results-count line, next to the summary CSV button but
///      directly above the selection controls, which cap at 50. It was true of the wrong control.
///   2. "Download details" is correctly disabled above 10 selected - each detail row is its own
///      Exchange round-trip - but a disabled button that states no reason reads as broken.
///
/// A guard here fails when the page stops explaining itself, which is the only failure mode that
/// matters for this work and the only one no test can otherwise see.
/// </remarks>
public class MessageTraceExportUxTests
{
    [Fact]
    public void ResultsCountLine_DoesNotTellTheOperatorToExportForEverything()
    {
        // The exact phrase that misdirected. It described the summary CSV while sitting above the
        // detail controls, so an operator hunting for "them all" tried the capped path.
        var source = ReadPage();

        Assert.DoesNotContain("export to get them all", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BothExports_StateTheirOwnScope()
    {
        // The root confusion: two exports with genuinely different scope and contents, presented
        // as one undifferentiated panel. Each must now say what it covers where it lives.
        var source = ReadPage();

        Assert.Contains("summary rows", source, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("full delivery detail", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheDetailCap_IsDescribedAsACeilingNotAPage()
    {
        // There is genuinely no way to get delivery detail for every result: the cap is a hard
        // ceiling because each row costs one Exchange call against a shared pool. Saying so is the
        // difference between a limit an operator can work with and one that looks like a bug.
        var source = ReadPage();

        Assert.Contains("ceiling, not a page", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheLiveDownloadButton_CarriesItsLimitInItsName()
    {
        // The limit belongs on the control. It was previously only in a note rendered BELOW the
        // buttons, which reads as commentary rather than as the reason the button is dead.
        var source = ReadPage();

        Assert.Matches(new Regex(@"Download details \(up to @MessageTraceDetailReport\.LiveMax\)"), source);
    }

    [Fact]
    public void TheDisabledLiveDownload_ExplainsWhyItIsUnavailable()
    {
        // Anchored to the `title` expression that renders on the button itself, so hovering a dead
        // control answers the question. Matching only the prose elsewhere on the page would pass
        // while the button stayed silent - the same false-coverage trap that blr-4 hit.
        var source = ReadPage();

        Assert.Contains("var liveTitle = liveBlocked", source, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"<button[^>]*title=""@liveTitle""", RegexOptions.Singleline), source);
        Assert.Contains("Live download is limited to", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheJobExportButton_IsNamedForWhatItProduces()
    {
        // It was "Email details", which described an optional notification rather than the export.
        // Since the notify box became clearable, the mail is genuinely optional and the export is
        // the point - an operator looking for a way to export should not have to read "email" as
        // "export".
        var source = ReadPage();

        Assert.Contains("Export details as a job", source, StringComparison.Ordinal);
        Assert.DoesNotContain(">Email details<", source, StringComparison.Ordinal);
    }

    private static string ReadPage() =>
        File.ReadAllText(Path.Combine(GetPagesDirectory(), "MessageTrace.razor"));

    private static string GetPagesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var pages = Path.Combine(dir.FullName, "Components", "Pages");
            if (Directory.Exists(pages))
                return pages;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Components/Pages from test base directory.");
    }
}
