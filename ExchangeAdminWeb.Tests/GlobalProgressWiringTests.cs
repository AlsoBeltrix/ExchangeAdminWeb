using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Structural guards for the global progress system's wiring
/// (docs/GlobalProgressSystem-Plan.md sections 5.1, 5.2 and 8).
///
/// Every defect guarded here is silent at runtime. A captive singleton dependency, an unscoped
/// job read and a leaked event handler all build, all pass every functional test, and all
/// misbehave only with a second operator or a second circuit - which nothing in this repo can
/// simulate, because there is no bUnit harness. Source scanning is the only thing that catches
/// them before a deploy does.
/// </summary>
public class GlobalProgressWiringTests
{
    private const string ProgressAbstraction = "IActivityProgress";

    [Fact]
    public void NoSingletonTakesThePerCircuitProgressService()
    {
        var program = StripComments(ReadRepoFile("Program.cs"));

        // The registration itself must be scoped. A singleton registration would hand the first
        // circuit's sink to every later one and report one operator's work to another.
        Assert.Matches(
            new Regex(@"AddScoped<[^>]*\bIActivityProgress\b[^>]*>", RegexOptions.Singleline),
            program);
        Assert.DoesNotMatch(
            new Regex(@"AddSingleton<[^>]*\bIActivityProgress\b[^>]*>", RegexOptions.Singleline),
            program);

        // And no singleton-registered type may depend on it. Comments are stripped first: three
        // guards in this repo have already failed by matching the prose that explained the
        // antipattern instead of the code (.agents/state.md, 2026-09-30).
        var singletonTypes = Regex.Matches(program, @"AddSingleton<\s*([A-Za-z0-9_.]+)\s*>")
            .Select(m => m.Groups[1].Value.Split('.').Last())
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(singletonTypes);

        var offenders = new List<string>();
        foreach (var file in EnumerateSource())
        {
            var typeName = Path.GetFileNameWithoutExtension(file);
            if (!singletonTypes.Contains(typeName, StringComparer.Ordinal))
                continue;

            var source = StripComments(File.ReadAllText(file));
            if (Regex.IsMatch(source, @"\bIActivityProgress\b"))
                offenders.Add(typeName);
        }

        Assert.True(offenders.Count == 0,
            "These types are registered AddSingleton and reference the per-circuit progress "
            + "service. A singleton capturing it reports one operator's progress to another. "
            + "Take IProgress<ActivityUpdate> and a CancellationToken as method arguments "
            + "instead (docs/GlobalProgressSystem-Plan.md section 5.1):\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void TheAppFrameDisplayNeverReadsTheUnscopedJobList()
    {
        var source = StripComments(ReadRepoFile(Path.Combine("Components", "Shared", "GlobalProgress.razor")));

        // GetActiveJobs() spans every module AND every operator. BulkJobService says so itself.
        // This component renders in the app frame, on every page, for everyone.
        Assert.DoesNotContain("GetActiveJobs()", source, StringComparison.Ordinal);
        Assert.Contains("GetActiveJobsBySubmitter(", source, StringComparison.Ordinal);
        Assert.Contains("GetRecentJobsBySubmitter(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAppFrameDisplayUnsubscribesFromEverythingItSubscribesTo()
    {
        var source = StripComments(ReadRepoFile(Path.Combine("Components", "Shared", "GlobalProgress.razor")));

        Assert.Contains("@implements IDisposable", source, StringComparison.Ordinal);

        // A layout component outlives every page, so a leaked handler persists for the life of
        // the process and keeps a dead circuit's component reachable from a singleton.
        foreach (var subscription in new[] { "Progress.Changed", "BulkJobs.JobChanged" })
        {
            Assert.Contains($"{subscription} +=", source, StringComparison.Ordinal);
            Assert.Contains($"{subscription} -=", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheAppFrameDisplayMarshalsItsEventHandlersOntoTheRenderer()
    {
        var source = StripComments(ReadRepoFile(Path.Combine("Components", "Shared", "GlobalProgress.razor")));

        // JobChanged is raised from the job pump's thread and Changed from whatever thread the
        // reporting module is on; touching component state from either without InvokeAsync is a
        // race that will not reproduce on a developer's single-user dev box.
        Assert.Contains("InvokeAsync(", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLayoutRendersTheDisplay()
    {
        var layout = StripComments(ReadRepoFile(Path.Combine("Components", "Layout", "MainLayout.razor")));

        Assert.Contains("<GlobalProgress", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNavigationBarIsLoadedAndCarriesBothStartSignals()
    {
        var app = StripComments(ReadRepoFile(Path.Combine("Components", "App.razor")));
        Assert.Contains("nav-progress.js", app, StringComparison.Ordinal);

        var script = ReadRepoFile(Path.Combine("wwwroot", "nav-progress.js"));

        // Two independent start signals, because only the end signal is proven in this app. If
        // the documented start event turns out not to fire on the installed Blazor version, the
        // click fallback still raises the bar; losing either one silently halves the fix.
        Assert.Contains("blazor:enhancednavigationstart", script, StringComparison.Ordinal);
        Assert.Contains("blazor:enhancedload", script, StringComparison.Ordinal);
        Assert.Contains("addEventListener('click'", script, StringComparison.Ordinal);
    }

    private static IEnumerable<string> EnumerateSource()
    {
        var root = RepoRoot();
        foreach (var dir in new[] { "Services", "Components", "Modules" })
        {
            var path = Path.Combine(root, dir);
            if (!Directory.Exists(path))
                continue;

            foreach (var file in Directory.EnumerateFiles(path, "*.cs", SearchOption.AllDirectories))
                yield return file;
        }
    }

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

    // Block and line comments go before any match, so a guard cannot be satisfied or tripped by
    // the prose that explains it.
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
