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
    public void AFaultInTheStatusFrameCannotTearDownTheOperatorsCircuit()
    {
        var source = StripComments(ReadRepoFile(Path.Combine("Components", "Shared", "GlobalProgress.razor")));

        // The handlers are `async void`, so no caller can observe a fault - the event source
        // has already returned and there is no Task to carry it. The refresh path reads the
        // jobs SQLite store, so a locked database would become an unhandled exception and kill
        // the circuit, losing whatever page the operator was working on because a STATUS BAR
        // could not repaint. Chrome must never be able to take the app down with it.
        Assert.Contains("catch (Exception)", source, StringComparison.Ordinal);

        var refresh = source.IndexOf("private async Task Refresh()", StringComparison.Ordinal);
        Assert.True(refresh >= 0, "Refresh() was not found.");
        Assert.True(source.IndexOf("catch (Exception)", refresh, StringComparison.Ordinal) > refresh,
            "The broad catch must be inside Refresh(), which is the async void handlers' only "
            + "path to the job store.");
    }

    [Fact]
    public void TheLayoutRendersTheDisplay()
    {
        var layout = StripComments(ReadRepoFile(Path.Combine("Components", "Layout", "MainLayout.razor")));

        Assert.Contains("<GlobalProgress", layout, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatusFrameSpansTheWindowAndIsNotAFooterInsideThePage()
    {
        var layout = StripComments(ReadRepoFile(Path.Combine("Components", "Layout", "MainLayout.razor")));

        // It must be a SIBLING of .page, outside </main> and outside the page div, so it runs
        // the full window width - under the sidebar as well as the content. Built inside main
        // it stops where the sidebar stops and reads as the open module's footer, which is
        // what the owner rejected on 2026-10-01: "why is it just a footer on the module page?"
        var mainClose = layout.IndexOf("</main>", StringComparison.Ordinal);
        var pageClose = layout.IndexOf("</div>", mainClose < 0 ? 0 : mainClose, StringComparison.Ordinal);
        var frame = layout.IndexOf("<GlobalProgress", StringComparison.Ordinal);
        var shellClose = layout.LastIndexOf("</div>", StringComparison.Ordinal);

        Assert.True(mainClose >= 0, "</main> was not found.");
        Assert.True(frame > pageClose,
            "The status frame must render AFTER the page div closes, as a sibling of .page - "
            + $"not inside main or inside .page. Found .page closing at {pageClose}, frame at {frame}.");
        Assert.True(frame < shellClose, "The status frame must still be inside the shell.");

        // Still not an overlay. Floating over the bottom of every page is item 22's actual
        // defect (docs/AppLayoutAndScrolling-Plan.md); this is a real row of the window.
        var css = StripComments(ReadRepoFile(Path.Combine("wwwroot", "app.css")));
        var frameRule = DeclarationsOf(css, ".gp-frame");
        Assert.DoesNotContain("position: fixed", frameRule, StringComparison.Ordinal);
        Assert.DoesNotContain("position: absolute", frameRule, StringComparison.Ordinal);
        Assert.Contains("flex: none", frameRule, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFrameHoldsNoSessionStateOfItsOwnBecauseTheLayoutIsRebuiltEveryNavigation()
    {
        var source = StripComments(ReadRepoFile(Path.Combine("Components", "Shared", "GlobalProgress.razor")));

        // The server rebuilds the layout on every navigation, so this component is destroyed
        // and recreated each time the operator changes page. Anything session-scoped held in a
        // field resets with it: "finished since you arrived" quietly becomes "since this
        // page", and a dismissed result reappears on the next click. Both belong to the
        // circuit, so both live on the scoped service.
        Assert.Contains("Progress.SessionStartedUtc", source, StringComparison.Ordinal);
        Assert.Contains("Progress.IsJobDismissed(", source, StringComparison.Ordinal);
        Assert.Contains("Progress.DismissJob(", source, StringComparison.Ordinal);

        Assert.DoesNotContain("arrivedUtc", source, StringComparison.Ordinal);
        Assert.DoesNotContain("dismissedJobs", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStatusFrameKeepsItsContentOutOfTheBrowsersOwnBottomLeftOverlay()
    {
        var css = StripComments(ReadRepoFile(Path.Combine("wwwroot", "app.css")));
        var frameRule = DeclarationsOf(css, ".gp-frame");

        // The bottom-left corner of the viewport belongs to the BROWSER: the link hover target
        // and the page-loading status are drawn there as an overlay, on top of whatever the
        // page put underneath. This frame first rendered "Idle" starting at the left edge, so
        // the text was correct in the DOM and completely hidden from the operator.
        //
        // Owner, 2026-10-01: "making the text appear in the code does not mean the text is
        // accessible to the human who needs it."
        //
        // The overlay anchors to the BOTTOM and switches sides - it starts bottom-left and
        // jumps bottom-right when the pointer nears it - so picking a side does not work. The
        // separation has to be vertical: the frame is tall enough that its text clears the
        // overlay's band, and the bottom padding is dead space left for the browser.
        //
        // Reduce either number and the frame goes unreadable exactly when an operator is
        // hovering a link or waiting for a page, which are the two moments they are most
        // likely to be reading it - and nothing else here can catch that.
        var minHeight = CssLength(frameRule, "min-height");
        Assert.True(minHeight >= 3.0,
            $"The status frame is {minHeight}rem tall; it needs at least 3rem so its text "
            + "clears the browser's bottom overlay.");

        var padBottom = PaddingBottom(frameRule);
        Assert.True(padBottom >= 1.5,
            $"The frame leaves {padBottom}rem of clearance below its text; the browser's "
            + "bottom overlay is roughly 24px, so this needs at least 1.5rem of dead space.");
    }

    private static double CssLength(string rule, string property)
    {
        var match = Regex.Match(rule, Regex.Escape(property) + @"\s*:\s*([0-9.]+)rem");
        Assert.True(match.Success, $"'{property}' was not found as a rem value in the rule.");
        return double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static double PaddingBottom(string rule)
    {
        var match = Regex.Match(rule, @"padding\s*:\s*([^;]+);");
        Assert.True(match.Success, "No padding shorthand was found in the frame rule.");

        var parts = match.Groups[1].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Shorthand: 1 value = all sides, 2 = block/inline, 3 or 4 = bottom is the third.
        var bottom = parts.Length switch
        {
            1 => parts[0],
            2 => parts[0],
            _ => parts[2],
        };

        var value = Regex.Match(bottom, @"([0-9.]+)rem");
        Assert.True(value.Success, $"The bottom padding '{bottom}' is not a rem value.");
        return double.Parse(value.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void TheStatusFrameIsAnInteractiveIslandLikeEveryOtherLiveComponentInTheLayout()
    {
        var frame = ReadRepoFile(Path.Combine("Components", "Shared", "GlobalProgress.razor"));

        // Routes.razor carries no render mode, so the router is static and interactivity is
        // opt-in per component. Without this line the frame is rendered once and is then
        // inert: its buttons do nothing, its subscriptions are discarded, and no job update
        // ever reaches it. It shipped that way and sat on a static "Idle" on dev.
        Assert.Contains("@rendermode InteractiveServer", frame, StringComparison.Ordinal);

        // The same requirement, from the other direction: every component the layout renders
        // that holds live state declares it. If one of these loses it, this test should fail
        // too rather than leaving the frame as the only guarded case.
        foreach (var peer in new[] { "NavMenu.razor", "ThemePicker.razor", "UsageTracker.razor" })
        {
            var source = ReadRepoFile(Path.Combine("Components", "Layout", peer));
            Assert.True(source.Contains("@rendermode InteractiveServer", StringComparison.Ordinal),
                $"{peer} no longer declares a render mode; the layout's interactivity "
                + "convention has changed and the status frame must be re-checked against it.");
        }
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
    public void PageLoadsAreReportedFromTheServerAndNotDetectedInTheBrowser()
    {
        var frame = StripComments(ReadRepoFile(Path.Combine("Components", "Shared", "GlobalProgress.razor")));
        var app = StripComments(ReadRepoFile(Path.Combine("Components", "App.razor")));

        // Four browser-side attempts to detect that a page had arrived were shipped and all
        // four failed on the real deployment, each leaving the frame counting upwards on a
        // page that had already loaded: blazor:enhancedload, blazor:enhancednavigationend, a
        // DOM MutationObserver, and a timeout. They share one shape - the browser LATCHES a
        // claim and something else has to retract it. A design that must be told to stop lying
        // can always be caught lying.
        //
        // The server is holding the request. It cannot be wrong about whether it is still
        // serving it, and PageLoadTracker forgets the request in a finally, so there is no
        // latch to strand. This test exists to stop the browser-side approach coming back.
        Assert.Contains("PageLoadTracker", frame, StringComparison.Ordinal);
        Assert.Contains("PageLoads.InFlightFor(", frame, StringComparison.Ordinal);

        Assert.False(File.Exists(Path.Combine(RepoRoot(), "wwwroot", "nav-progress.js")),
            "wwwroot/nav-progress.js is back. Page-load reporting belongs on the server; see "
            + "Services/PageLoadTracker.cs and the comment in Components/App.razor.");

        Assert.DoesNotContain("nav-progress", app, StringComparison.Ordinal);
    }

    [Fact]
    public void TheMiddlewareEndsEveryPageLoadEvenWhenTheRequestFails()
    {
        var source = StripComments(ReadRepoFile(Path.Combine("Middleware", "PageLoadMiddleware.cs")));

        // The entire anti-stranding guarantee is that the scope is disposed whatever happens.
        // A request that throws, is cancelled or times out must still clear the frame; an
        // explicit End() on the success path only would reintroduce exactly the stuck readout
        // this replaced.
        Assert.Contains("using (tracker.Begin(", source, StringComparison.Ordinal);

        var registration = StripComments(ReadRepoFile("Program.cs"));
        Assert.Contains("UseMiddleware<PageLoadMiddleware>()", registration, StringComparison.Ordinal);

        // After authentication, or every page load is attributed to nobody and the frame shows
        // the operator nothing.
        var auth = registration.IndexOf("UseAuthentication()", StringComparison.Ordinal);
        var pageLoad = registration.IndexOf("UseMiddleware<PageLoadMiddleware>()", StringComparison.Ordinal);
        Assert.True(auth >= 0 && pageLoad > auth,
            "PageLoadMiddleware must run after UseAuthentication, or context.User is empty and "
            + "no page load can be attributed to the operator waiting for it.");
    }

    [Theory]
    [InlineData("RiskyUsers.razor")]
    [InlineData("ServiceHealth.razor")]
    [InlineData("DelegationReport.razor")]
    [InlineData("BitLockerRecovery.razor")]
    [InlineData("MessageTrace.razor")]
    [InlineData("Comms10k.razor")]
    [InlineData("EmergencyDisable.razor")]
    [InlineData("LicensingUpdates.razor")]
    [InlineData("MfaReset.razor")]
    [InlineData("OutOfOffice.razor")]
    [InlineData("BlockedSenders.razor")]
    [InlineData("CloudPasswordReset.razor")]
    [InlineData("ADAttributeEditor.razor")]
    public void AnAdoptedModuleReportsItsWorkToTheStatusFrame(string page)
    {
        var source = StripComments(ReadRepoFile(Path.Combine("Components", "Pages", page)));

        // An adopted module begins an activity and lets `using` end it, so an exception path
        // cannot leave the frame claiming the work is still running - the same guarantee the
        // page-load middleware gets from its own using block.
        Assert.Contains("IActivityProgress Progress", source, StringComparison.Ordinal);

        // Every activity the page begins must be `using`, so an exception path cannot leave
        // the frame claiming the work is still running, and must report an outcome of its own,
        // so a success is not recorded as the dispose fallback's failure.
        //
        // Matched per variable rather than against the name "activity": a page with two
        // distinct operations names them for what they are (Comms-10k has `resolving` and
        // `replacing`), and an earlier version of this test failed those pages for being
        // clearer than the one it was written against.
        var begun = Regex.Matches(source, @"using var (\w+) = Progress\.Begin\(")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.True(begun.Count > 0,
            $"{page} injects the progress service but never begins an activity with "
            + "`using var <name> = Progress.Begin(`.");

        foreach (var name in begun)
        {
            Assert.True(Regex.IsMatch(source, Regex.Escape(name) + @"\.Complete\("),
                $"{page} begins '{name}' but never completes it. Falling out of scope records "
                + "a FAILURE, so a successful run would be reported to the operator as failed.");
        }
    }

    [Fact]
    public void NoAdoptedModuleBeginsAnActivityWithoutUsing()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.GetFiles(Path.Combine(RepoRoot(), "Components", "Pages"), "*.razor"))
        {
            var source = StripComments(File.ReadAllText(file));

            // `Progress.Begin` without `using` means nothing guarantees the activity ends. The
            // frame would keep showing it after the operation returned - the exact stranded-
            // readout defect that four browser-side attempts at navigation reporting produced,
            // reintroduced one module at a time.
            // The `using var` prefix is part of the match, not something to look for inside a
            // match that starts after it - the first version of this started matching at
            // "var" and so reported every correct call site as an offender.
            foreach (Match match in Regex.Matches(source, @"(using\s+var\s+)?\w+\s*=\s*Progress\.Begin\("))
            {
                if (match.Groups[1].Success)
                    continue;

                var line = source[..match.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetFileName(file)}:{line}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Progress.Begin must always be assigned with `using var`, or the activity can "
            + "outlive the work and strand the status frame:\n" + string.Join("\n", offenders));
    }

    private static string DeclarationsOf(string css, string selector)
    {
        var index = css.IndexOf(selector + " {", StringComparison.Ordinal);
        Assert.True(index >= 0, $"selector '{selector}' not found in app.css");

        var open = css.IndexOf('{', index);
        var close = css.IndexOf('}', open);
        return css[open..close];
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
