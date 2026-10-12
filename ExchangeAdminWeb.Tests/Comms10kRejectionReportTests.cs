using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the errors report the owner ruled for on 2026-10-12: an address with no exact match
/// is not a match, and every non-matching address is reported with the reason it was rejected.
/// </summary>
/// <remarks>
/// <para>
/// Two separate properties are at stake and they fail differently, so they get separate tests.
/// The RESOLVER must attach a reason to every rejection - a rejection with no reason is a row
/// the operator cannot act on. The PAGE must export every rejection - the complaint that
/// produced this work was not "we are not told", it was <i>"requiring us to manually identify
/// the affected users"</i>, which is what a truncated list forces.
/// </para>
/// <para>
/// The page half is a source-level guard. There is no bUnit harness in this repo, so nothing
/// here proves the button renders or that a file reaches a browser; it proves the handler
/// writes the whole list rather than a sample. That limit is real and is why the module's
/// manual behaviour is confirmed in production use rather than here.
/// </para>
/// </remarks>
public class Comms10kRejectionReportTests
{
    // ---- the resolver: every rejection carries a reason, and the four causes are distinct ----

    [Fact]
    public void EveryRejectionCarriesANonEmptyReason()
    {
        // One input of each rejecting kind, plus one that resolves, so a change that starts
        // dropping the reason on any single path fails here rather than reaching a CSV column
        // full of blanks.
        var outcome = Comms10kAddressResolver.Resolve(
            ["", "ghost@example.com", "dup@example.com", "nodn@example.com", "ok@example.com"],
            [
                User("CN=D1,DC=x", upn: "dup@example.com"),
                User("CN=D2,DC=x", mail: "dup@example.com"),
                User(dn: "", upn: "nodn@example.com"),
                User("CN=OK,DC=x", upn: "ok@example.com"),
            ]);

        Assert.Equal(["CN=OK,DC=x"], outcome.ResolvedDns);
        Assert.Equal(4, outcome.Rejections.Count);
        Assert.All(outcome.Rejections, r => Assert.False(
            string.IsNullOrWhiteSpace(r.Reason),
            $"'{r.Address}' was rejected with no reason. The CSV column exists so the operator "
            + "can act on the row; a blank reason makes the row useless."));
    }

    [Fact]
    public void TheFourRejectionCausesAreReportedDistinctly()
    {
        var outcome = Comms10kAddressResolver.Resolve(
            ["   ", "ghost@example.com", "dup@example.com", "nodn@example.com"],
            [
                User("CN=D1,DC=x", upn: "dup@example.com"),
                User("CN=D2,DC=x", mail: "dup@example.com"),
                User(dn: "", upn: "nodn@example.com"),
            ]);

        Assert.Equal(
            [
                Comms10kAddressResolver.Reasons.Blank,
                Comms10kAddressResolver.Reasons.NoMatch,
                "Ambiguous: 2 matches",
                Comms10kAddressResolver.Reasons.NoDistinguishedName,
            ],
            outcome.Rejections.Select(r => r.Reason));
    }

    [Fact]
    public void TheRejectedAddressIsReportedExactlyAsTheOperatorWroteIt()
    {
        // The operator has to find this row in the workbook they uploaded. A trimmed or
        // lowercased address they never typed is unfindable - and the stray whitespace or the
        // casing may be the whole reason the match failed, so it is evidence, not noise.
        const string AsWritten = "  Ghost@Example.COM ";

        var outcome = Comms10kAddressResolver.Resolve([AsWritten], []);

        var rejection = Assert.Single(outcome.Rejections);
        Assert.Equal(AsWritten, rejection.Address);
    }

    [Fact]
    public void AnAliasThatDoesNotMatchExactlyIsRejectedRatherThanResolved()
    {
        // Owner ruling 2026-10-12: "if the address in the list as written has no exact match,
        // that is not a match." This pins the ruling rather than the implementation - a later
        // change that widens the keys to proxyAddresses to be helpful fails HERE, where the
        // reason is written down, instead of silently changing who receives the mail.
        var outcome = Comms10kAddressResolver.Resolve(
            ["old.name@example.com"],
            [User("CN=P,DC=x", upn: "new.name@example.com", mail: "new.name@example.com")]);

        Assert.Empty(outcome.ResolvedDns);
        var rejection = Assert.Single(outcome.Rejections);
        Assert.Equal(Comms10kAddressResolver.Reasons.NoMatch, rejection.Reason);
    }

    // ---- the page: the export is the whole set, and the ten-address cap is the banner's ----

    [Fact]
    public void TheExportWritesTheWholeRejectionListAndNotThePreview()
    {
        var source = StripComments(ReadRepoFile(Path.Combine("Components", "Pages", "Comms10k.razor")));

        var handler = HandlerBody(source, "DownloadRejections");

        Assert.Contains("csvWriter.WriteRecords(rejections)", handler, StringComparison.Ordinal);

        // The defect this guards is specific and it already shipped once on this page: the
        // banner shows Take(10) then a literal "...". If that cap ever reaches the export, the
        // operator is back to being unable to find out who was dropped, which is the entire
        // complaint the control was built to answer.
        Assert.DoesNotContain("Take(", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("Skip(", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void TheExportReadsALocalCapturedBeforeTheFirstAwait()
    {
        // resolveResult is a FIELD and Validate replaces it wholesale. The navigation guard's
        // OK path stops the UI waiting rather than the work, so a second Validate can land
        // while this handler is mid-transfer. Reading the field after an await would export a
        // different run's rejections than the ones the operator is looking at.
        var handler = HandlerBody(
            StripComments(ReadRepoFile(Path.Combine("Components", "Pages", "Comms10k.razor"))),
            "DownloadRejections");

        var capture = handler.IndexOf("var rejections = resolveResult", StringComparison.Ordinal);
        Assert.True(capture >= 0, "The handler must capture resolveResult's rejections into a local.");

        var firstAwait = handler.IndexOf("await", StringComparison.Ordinal);
        Assert.True(firstAwait > capture,
            "The rejection list must be captured BEFORE the first await; after one, Validate can "
            + "have replaced resolveResult and the export would not match the banner on screen.");

        // And nothing below may go back to the field. Counted from the end of the capture
        // STATEMENT, not from its start - the capture reads resolveResult itself, which is the
        // one read that is allowed and the one an off-by-one here would wrongly flag.
        var captureEnd = handler.IndexOf(';', capture);
        Assert.True(captureEnd > capture, "The capture statement is unterminated.");

        var afterCapture = handler[(captureEnd + 1)..];
        var fieldReadsAfterCapture = Regex.Matches(afterCapture, @"resolveResult\s*[?.]").Count;
        Assert.Equal(0, fieldReadsAfterCapture);
    }

    // ---- helpers ----

    private static Comms10kAddressResolver.Candidate User(string dn, string? upn = null, string? mail = null) =>
        new(dn, upn, mail);

    /// <summary>
    /// The text of one handler, from its signature to the start of the next member.
    /// </summary>
    /// <remarks>
    /// Brace-counted rather than regex-delimited. Three guards in this repo were written against
    /// prose and failed correct code (review finding prog-6); the comment strip above plus a
    /// real brace walk is what stops this one joining them.
    /// </remarks>
    private static string HandlerBody(string source, string handlerName)
    {
        var start = source.IndexOf($"Task {handlerName}(", StringComparison.Ordinal);
        Assert.True(start >= 0, $"{handlerName} was not found on the page.");

        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"{handlerName} has no body.");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}')
            {
                depth--;
                if (depth == 0)
                    return source[open..(i + 1)];
            }
        }

        throw new InvalidOperationException($"{handlerName}'s body is unbalanced.");
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

    private static string StripComments(string source)
    {
        var withoutBlocks = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        var withoutRazorComments = Regex.Replace(withoutBlocks, @"@\*.*?\*@", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutRazorComments, @"//[^\n]*", " ");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ExchangeAdminWeb.csproj")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test base directory.");
    }
}
