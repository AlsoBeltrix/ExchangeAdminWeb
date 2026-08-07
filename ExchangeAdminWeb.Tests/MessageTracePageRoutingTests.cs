using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Source-level guard: the Message Analysis page must have exactly one search path.
/// </summary>
/// <remarks>
/// THIS IS A TRIPWIRE, NOT BEHAVIOURAL COVERAGE, and that is the right instrument here.
///
/// The defect it guards was a REINTRODUCED BRANCH. The page routed any range wider than 9 days to
/// Start-HistoricalSearch and told the operator results would be emailed, so the chunked 90-day
/// search built in 03a9999 never ran. It reached dev and prod in 2.6.0.
///
/// Nothing caught it: 1483 unit tests passed against it, because this repo has no bUnit harness and
/// no test can see which branch a Razor handler takes. The service was correct and separately well
/// tested the whole time - the planner and chunking tests all passed. Correctness of the parts
/// proved nothing about which part the page called.
///
/// So a string match on the page source is the only automation that can see this class of defect.
/// It cannot prove the search works; it can prove the second path has not come back. Same approach
/// and same reasoning as PageAuthorizationRecheckTests.
///
/// History worth keeping, because it is how the defect survived: 72b8047 deleted this branch
/// correctly but rested on a false premise about Get-MessageTraceV2 and was reverted whole in
/// 90486d2 - taking the correct deletion with it. 03a9999 then rebuilt the chunking in the service
/// and never restored the page deletion. A revert that undoes two things and a repair that redoes
/// one is not a shape a diff review reliably catches.
/// </remarks>
public class MessageTracePageRoutingTests
{
    [Theory]
    [InlineData("IsHistoricalRange")]
    [InlineData("RunHistoricalSearch")]
    [InlineData("StartHistoricalSearchAsync")]
    [InlineData("historicalSubmitted")]
    public void Page_HasNoHistoricalSearchPath(string symbol)
    {
        var source = ReadPage();

        Assert.DoesNotContain(symbol, source, StringComparison.Ordinal);
    }

    [Fact]
    public void RunTrace_RunsTheRealtimeTraceUnconditionally()
    {
        // The realtime path is the chunked path: MessageTraceService splits a wide range into
        // windows itself. The page must not decide anything about width.
        var body = GetMethodBody("RunTrace");

        Assert.Contains("await RunRealtimeTrace()", body);
    }

    [Fact]
    public void Page_DoesNotComputeHowWideTheRangeIs()
    {
        // The specific shape that broke: a day-count comparison used to pick a search path.
        // Matching the arithmetic rather than the retired identifier is what makes this survive a
        // rename - the flag could come back as "IsWideRange" and the name-based guard above would
        // miss it.
        //
        // Scoped to the WHOLE PAGE, not to RunTrace's body, and that is deliberate: the original
        // defect declared the comparison as a field (`IsHistoricalRange => (endDate - startDate)
        // .TotalDays > 9`) and only USED the flag inside RunTrace. A body-scoped assertion looks
        // in the one place the arithmetic was not. Verified: with the branch reinstated, the
        // body-scoped version of this test passed while the defect was present.
        //
        // The page has no legitimate need to measure the range: the service splits wide ranges
        // into windows, and MessageTraceWindowPlanner owns every date rule.
        var source = ReadPage();

        Assert.DoesNotMatch(new Regex(@"TotalDays\s*[<>]"), source);
    }

    [Fact]
    public void Page_DoesNotPromiseToEmailTraceResults()
    {
        // The operator-visible symptom, guarded in the operator's own terms: the page told people
        // their results would be emailed. The detail EXPORT is still delivered by email and is a
        // different feature, so this looks only for the retired promise about trace results.
        var source = ReadPage();

        Assert.DoesNotContain("Results will be emailed", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Historical search", source, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Page_StillOffersTheDetailExportEmail()
    {
        // The counterweight: the guards above must not be satisfiable by deleting the email feature
        // that is still wanted. This fails if the retirement was over-greedy.
        var source = ReadPage();

        Assert.Contains("EmailSelectedDetails", source, StringComparison.Ordinal);
        Assert.Contains("userEmail", source, StringComparison.Ordinal);
    }

    private static string ReadPage() =>
        File.ReadAllText(Path.Combine(GetPagesDirectory(), "MessageTrace.razor"));

    private static string GetMethodBody(string methodName)
    {
        var source = ReadPage();

        var signature = Regex.Match(source,
            $@"private\s+async\s+Task(<[^>]+>)?\s+{Regex.Escape(methodName)}\s*\(");
        Assert.True(signature.Success, $"handler '{methodName}' not found");

        var start = signature.Index;
        var next = Regex.Match(source[(start + signature.Length)..],
            @"\n    private\s+(async\s+)?[A-Za-z]");
        return next.Success
            ? source.Substring(start, signature.Length + next.Index)
            : source[start..];
    }

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
