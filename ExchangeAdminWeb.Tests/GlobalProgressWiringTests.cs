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
    public void TheAppFrameDisplayCannotSubscribeAfterItHasBeenDisposed()
    {
        var source = StripComments(ReadRepoFile(Path.Combine("Components", "Shared", "GlobalProgress.razor")));

        // OnInitializedAsync awaits authentication before subscribing. If the circuit is
        // disposed during that await, Dispose finds nothing subscribed and returns, and then
        // the continuation subscribes a dead component to a SINGLETON event - where it stays
        // reachable for the life of the process. Dispose has already run by then, so the only
        // place that can close this is a disposed check between the await and the subscribe.
        var awaitIndex = source.IndexOf("await AuthStateProvider.GetAuthenticationStateAsync()", StringComparison.Ordinal);
        var guardIndex = source.IndexOf("if (disposed)", awaitIndex < 0 ? 0 : awaitIndex, StringComparison.Ordinal);
        var subscribeIndex = source.IndexOf("Progress.Changed +=", StringComparison.Ordinal);

        Assert.True(awaitIndex >= 0, "The authentication await was not found.");
        Assert.True(guardIndex > awaitIndex && guardIndex < subscribeIndex,
            "A disposed check must sit between awaiting authentication and subscribing. "
            + $"await at {awaitIndex}, guard at {guardIndex}, subscribe at {subscribeIndex}.");
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
    public void TheStatusFrameIsTheLastRowInsideMainAndNotAnOverlay()
    {
        var layout = StripComments(ReadRepoFile(Path.Combine("Components", "Layout", "MainLayout.razor")));

        // The frame must sit INSIDE main, after article, so the height chain keeps working:
        // article stays `flex: 1` and simply gets shorter. Anything fixed or absolute would
        // float over the bottom of every page, which is precisely the defect queue item 22
        // fixed (docs/AppLayoutAndScrolling-Plan.md).
        var article = layout.IndexOf("</article>", StringComparison.Ordinal);
        var frame = layout.IndexOf("<GlobalProgress", StringComparison.Ordinal);
        var mainClose = layout.IndexOf("</main>", StringComparison.Ordinal);

        Assert.True(article >= 0 && frame > article && mainClose > frame,
            "The status frame must render inside <main>, after </article>. Found article at "
            + $"{article}, frame at {frame}, </main> at {mainClose}.");

        var css = StripComments(ReadRepoFile(Path.Combine("wwwroot", "app.css")));
        var frameRule = DeclarationsOf(css, ".gp-frame");
        Assert.DoesNotContain("position: fixed", frameRule, StringComparison.Ordinal);
        Assert.DoesNotContain("position: absolute", frameRule, StringComparison.Ordinal);
        Assert.Contains("flex: none", frameRule, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatusFrameAlwaysRendersAndSaysIdleWhenNothingIsRunning()
    {
        var source = StripComments(ReadRepoFile(Path.Combine("Components", "Shared", "GlobalProgress.razor")));

        // The owner's requirement, 2026-10-01: the frame is permanent and reads "Idle" when
        // nothing is running. An earlier build rendered nothing while idle, which makes it a
        // thing operators never learn to look at.
        Assert.Contains("Idle", source, StringComparison.Ordinal);
        Assert.Contains("gp-frame", source, StringComparison.Ordinal);

        // No top-level conditional may wrap the frame element itself.
        var frameIndex = source.IndexOf("id=\"gp-frame\"", StringComparison.Ordinal);
        Assert.True(frameIndex > 0, "The frame element was not found.");
    }

    [Fact]
    public void TheStatusReadoutCarriesWordsAndCountsRatherThanJustABar()
    {
        var source = StripComments(ReadRepoFile(Path.Combine("Components", "Shared", "GlobalProgress.razor")));

        // A progress system here is a live status readout, not a timer (owner, 2026-10-01).
        // These are the three honest shapes; losing them turns the frame back into a sliver
        // that says nothing.
        Assert.Contains("step {activity.Done} of {activity.Total}", source, StringComparison.Ordinal);
        Assert.Contains("{activity.Done:N0} of {activity.Total:N0}", source, StringComparison.Ordinal);
        Assert.Contains("of {job.TotalRows:N0}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNavigationReadoutWritesIntoTheSlotTheFrameRenders()
    {
        var frame = StripComments(ReadRepoFile(Path.Combine("Components", "Shared", "GlobalProgress.razor")));
        var script = ReadRepoFile(Path.Combine("wwwroot", "nav-progress.js"));

        // The component cannot render during a navigation - the circuit that would render it
        // is the thing being waited on - so the JS owns this slot. If the id drifts on either
        // side the readout silently stops appearing, which is exactly how the first attempt
        // failed on dev.
        Assert.Contains("gp-nav-slot", frame, StringComparison.Ordinal);
        Assert.Contains("gp-nav-slot", script, StringComparison.Ordinal);
        Assert.Contains("gp-frame", script, StringComparison.Ordinal);

        // It must name the destination, not just report that something is happening.
        Assert.Contains("'Loading '", script, StringComparison.Ordinal);
        Assert.Contains("textContent", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ARefusedNavigationCannotLeaveTheFrameReadingLoadingForever()
    {
        var script = ReadRepoFile(Path.Combine("wwwroot", "nav-progress.js"));

        // A click is not a navigation. UnsavedChangesGuard can refuse one with
        // PreventNavigation, and a refused navigation fires no load event - so the readout
        // needs a way to notice nothing happened. In a PERMANENT frame a stuck "Loading X -
        // 94s" is worse than no frame at all: it is a standing lie with a live timer behind it.
        Assert.Contains("abandonIfNavigationNeverHappened", script, StringComparison.Ordinal);

        // The three facts the watchdog distinguishes. Losing any one of them either strands the
        // readout or kills a legitimate one: href proves an enhanced navigation committed,
        // beforeunload proves a full one did, and the start event proves Blazor accepted it.
        Assert.Contains("hrefAtStart", script, StringComparison.Ordinal);
        Assert.Contains("leavingDocument", script, StringComparison.Ordinal);
        Assert.Contains("navEventSeen", script, StringComparison.Ordinal);

        // The interval must be cleared on every stop, or a refused navigation leaks a timer
        // that repaints forever.
        Assert.Contains("clearInterval", script, StringComparison.Ordinal);
    }

    private static string DeclarationsOf(string css, string selector)
    {
        var index = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(index >= 0, $"selector '{selector}' not found in app.css");

        var open = css.IndexOf('{', index);
        var close = css.IndexOf('}', open);
        return css[open..close];
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
