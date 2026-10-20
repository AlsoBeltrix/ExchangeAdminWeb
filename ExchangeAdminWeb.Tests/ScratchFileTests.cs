namespace ExchangeAdminWeb.Tests;

/// <summary>
/// No throwaway edit script is ever committed.
/// </summary>
/// <remarks>
/// This exists because I did it twice in one session. The shape is always the same: a
/// `tools/scratch-*.py` helper is written to make an exact edit, the script fails an
/// assertion partway, the cleanup `rm` never runs because it was chained after the failing
/// command, and the next `git add -A` sweeps the file into a commit.
///
/// Both times it was caught by reading `git status` output in the commit result, which is
/// luck rather than process. A guard costs nothing and does not rely on me noticing.
/// </remarks>
public class ScratchFileTests
{
    [Fact]
    public void NoScratchScriptIsTracked()
    {
        var repoRoot = Directory.GetParent(
            AuditCategoryFilingTests.FindRepoFile("ExchangeAdminWeb.csproj"))!.FullName;

        var tools = Path.Combine(repoRoot, "tools");
        Assert.True(Directory.Exists(tools), "the tools directory should exist");

        var strays = Directory.GetFiles(tools, "scratch-*")
            .Select(Path.GetFileName)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            strays.Count == 0,
            "Throwaway edit scripts must not be committed. Found: " + string.Join(", ", strays));
    }
}
