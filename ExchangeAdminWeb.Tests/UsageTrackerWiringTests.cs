using ExchangeAdminWeb.Components.Layout;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the usage-telemetry wiring in the Razor layer (docs/UsageTelemetry-Plan.md, AC6,
/// AC7). Source-text guards rather than bUnit: the repo has no bUnit dependency and the plan
/// forbids adding one, and what matters here is WHERE the calls sit - after the first
/// interactive render, inside Authorized, after setTheme - which a render test could not
/// distinguish from the wrong placement. The one piece of real logic, the route derivation, is
/// extracted and tested for value.
/// </summary>
public class UsageTrackerWiringTests
{
    private static string Tracker() =>
        File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Layout", "UsageTracker.razor"));

    [Fact]
    public void UsageTracker_RecordsOnLocationChanged_AndDisposes()
    {
        var text = Tracker();

        Assert.Contains("Navigation.LocationChanged += OnLocationChanged;", text, StringComparison.Ordinal);
        Assert.Contains("Navigation.LocationChanged -= OnLocationChanged;", text, StringComparison.Ordinal);
        Assert.Contains("Catalog.GetByRoute(", text, StringComparison.Ordinal);
        Assert.Contains("Usage.RecordSessionStart(", text, StringComparison.Ordinal);
        Assert.Contains("Usage.RecordOpen(", text, StringComparison.Ordinal);
        Assert.Contains("@implements IDisposable", text, StringComparison.Ordinal);
    }

    [Fact]
    public void UsageTracker_RecordsOnlyAfterFirstInteractiveRender()
    {
        var text = Tracker();

        // A prerendered pass must not record: it would double every visit and cannot reach JS.
        Assert.DoesNotContain("override void OnInitialized", text, StringComparison.Ordinal);
        Assert.DoesNotContain("override async Task OnInitializedAsync", text, StringComparison.Ordinal);

        var afterRender = text.IndexOf("OnAfterRenderAsync(bool firstRender)", StringComparison.Ordinal);
        Assert.True(afterRender >= 0, "UsageTracker no longer records from OnAfterRenderAsync.");

        var guard = text.IndexOf("if (!firstRender)", afterRender, StringComparison.Ordinal);
        Assert.True(guard > afterRender, "The first-render guard is gone.");

        foreach (var call in new[] { "Usage.RecordSessionStart(", "Navigation.LocationChanged += " })
        {
            var at = text.IndexOf(call, StringComparison.Ordinal);
            Assert.True(at > guard, $"{call} must run after the first-render guard.");
        }
    }

    [Fact]
    public void UsageTracker_RendersInsideAuthorized()
    {
        var layout = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Layout", "MainLayout.razor"));

        var open = layout.IndexOf("<Authorized>", StringComparison.Ordinal);
        var tracker = layout.IndexOf("<UsageTracker />", StringComparison.Ordinal);
        var close = layout.IndexOf("</Authorized>", StringComparison.Ordinal);

        Assert.True(open >= 0 && close > open, "MainLayout no longer has an Authorized block.");
        Assert.True(
            tracker > open && tracker < close,
            "UsageTracker must render inside <Authorized> so anonymous circuits record nothing.");
    }

    [Theory]
    [InlineData("group-management", "group-management")]
    [InlineData("/group-management/", "group-management")]
    [InlineData("bitlocker-recovery?device=PC1", "bitlocker-recovery")]
    [InlineData("?tab=1", "")]
    [InlineData("", "")]
    public void RouteToModule_MatchesModuleVersionDerivation(string baseRelative, string expected)
    {
        Assert.Equal(expected, UsageTracker.RouteOf(baseRelative));

        // The same input through the ModuleVersion.razor derivation, spelled out here so the
        // two cannot drift apart silently: a page that reports a version this records no opens
        // for would make the Usage view quietly wrong.
        var route = baseRelative;
        var queryIndex = route.IndexOf('?', StringComparison.Ordinal);
        if (queryIndex >= 0)
        {
            route = route[..queryIndex];
        }
        Assert.Equal(route.Trim('/'), UsageTracker.RouteOf(baseRelative));
    }

    [Fact]
    public void RouteToModule_TracksOnlyTheTwoModulelessRoutes()
    {
        Assert.True(UsageTracker.IsTrackedModulelessRoute(string.Empty));
        Assert.True(UsageTracker.IsTrackedModulelessRoute("access-denied"));
        Assert.False(UsageTracker.IsTrackedModulelessRoute("some-unknown-path"));
    }

    [Fact]
    public void ThemePicker_RecordsThemeAfterSet()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Layout", "ThemePicker.razor"));

        var set = text.IndexOf("InvokeVoidAsync(\"setTheme\"", StringComparison.Ordinal);
        var record = text.IndexOf("Usage.RecordTheme(", StringComparison.Ordinal);

        Assert.True(set >= 0, "ThemePicker no longer sets the theme.");
        Assert.True(
            record > set,
            "The theme must be recorded only after setTheme succeeds, never before.");
    }

    /// <summary>
    /// AC13: the notice and the behaviour cannot disagree, because the bullet is gated on the
    /// same Enabled() read the recorder uses, and the retention figure is read from the
    /// constant rather than typed a second time.
    /// </summary>
    [Fact]
    public void Home_DisclosesTelemetry_OnlyWhenEnabled()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "Home.razor"));

        const string phrase = "Anonymous usage data is collected";
        var first = text.IndexOf(phrase, StringComparison.Ordinal);
        Assert.True(first >= 0, "Home.razor no longer discloses usage telemetry.");
        Assert.Equal(first, text.LastIndexOf(phrase, StringComparison.Ordinal));

        var gate = text.IndexOf("@if (Usage.Enabled())", StringComparison.Ordinal);
        Assert.True(gate >= 0, "The disclosure is no longer gated on the telemetry switch.");
        Assert.True(gate < first, "The disclosure must sit inside the Enabled() gate.");

        var close = text.IndexOf('}', first);
        Assert.True(close > first, "The gated block is not closed.");

        Assert.Contains("@UsageTelemetryService.RetentionDays days", text, StringComparison.Ordinal);
        Assert.DoesNotContain("kept for 90 days", text, StringComparison.Ordinal);
    }
    /// <summary>
    /// The Usage module table reads as coverage, not as a top list: every catalog module is a
    /// row even when nobody opened it, and an aggregate key that is not a module id (an audited
    /// category such as Lookup) is still listed rather than dropped. The catalog count is read
    /// from the catalog, never written down here.
    /// </summary>
    [Fact]
    public void BuildModuleRows_FillsZerosForUnopenedModules()
    {
        var modules = new ModuleCatalog().GetOrdered();
        var withData = modules.Take(2).ToList();

        var summary = new List<ModuleUsage>
        {
            new(withData[0].Id, Opens: 4, Actions: 6, FailedActions: 1, DistinctSessions: 3),
            new(withData[1].Id, Opens: 0, Actions: 2, FailedActions: 0, DistinctSessions: 1),
            new("Lookup", Opens: 0, Actions: 5, FailedActions: 2, DistinctSessions: 2),
        };

        var rows = AdminEventLog.BuildModuleRows(summary, modules);

        Assert.Equal(modules.Count + 1, rows.Count);
        Assert.Equal(modules.Count, rows.Count(r => r.IsCatalogModule));

        var first = rows[0];
        Assert.Equal(withData[0].DisplayName, first.Name);
        Assert.Equal(4, first.Opens);
        Assert.Equal(6, first.Actions);
        Assert.Equal(1, first.Failed);
        Assert.Equal(3, first.Sessions);
        Assert.Equal(1.5, first.ActionsPerOpen, 3);

        // Actions with no open must not divide by zero.
        Assert.Equal(2, rows[1].Actions);
        Assert.Equal(0d, rows[1].ActionsPerOpen, 3);

        foreach (var row in rows.Skip(2).Where(r => r.IsCatalogModule))
        {
            Assert.Equal(0, row.Opens);
            Assert.Equal(0, row.Actions);
            Assert.Equal(0, row.Failed);
            Assert.Equal(0, row.Sessions);
            Assert.Equal(0d, row.ActionsPerOpen, 3);
        }

        var other = Assert.Single(rows, r => !r.IsCatalogModule);
        Assert.Equal("Lookup", other.Name);
        Assert.Equal(5, other.Actions);
        Assert.Equal(2, other.Failed);
    }

    /// <summary>
    /// Review finding utei-4: the date inputs sit above BOTH views, so a range edit made while
    /// Usage is showing must not leave the events table, its count and its CSV export on the
    /// old range. Source-text guard (no bUnit here, per the class remarks); each assertion is
    /// anchored inside the method that has to carry it, because loose substring checks would
    /// pass on a file that merely mentions the flag somewhere.
    /// </summary>
    [Fact]
    public void EventLog_ReloadsEventsWhenTheRangeMovedInTheUsageView()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "AdminEventLog.razor"));

        var changed = MethodBody(text, "private void OnDateRangeChanged()");
        var elseAt = changed.IndexOf("else", StringComparison.Ordinal);
        Assert.True(elseAt > 0, "OnDateRangeChanged no longer branches on the current view.");
        Assert.Contains("eventsRangeStale = true;", changed[..elseAt], StringComparison.Ordinal);

        var toEvents = MethodBody(text, "private void ShowEventsView()");
        Assert.Contains("showUsage = false;", toEvents, StringComparison.Ordinal);

        var gate = toEvents.IndexOf("if (eventsRangeStale)", StringComparison.Ordinal);
        Assert.True(gate >= 0, "ShowEventsView no longer checks whether the range moved.");
        Assert.True(
            toEvents.IndexOf("LoadEvents();", StringComparison.Ordinal) > gate,
            "The events reload must sit inside the stale-range gate.");
        Assert.True(
            toEvents.IndexOf("eventsRangeStale = false;", StringComparison.Ordinal) > gate,
            "The stale flag must be cleared once the reload has been ordered.");
    }

    /// <summary>
    /// The source text of one method, from its signature to its matching closing brace. Lets a
    /// source-text guard assert a statement sits in a PARTICULAR method rather than somewhere
    /// in a 1300-line page.
    /// </summary>
    private static string MethodBody(string text, string signature)
    {
        var start = text.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"{signature} is gone from the page.");

        var open = text.IndexOf('{', start);
        Assert.True(open > start, $"{signature} has no body.");

        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{')
            {
                depth++;
            }
            else if (text[i] == '}' && --depth == 0)
            {
                return text[start..(i + 1)];
            }
        }

        Assert.Fail($"{signature} has no matching closing brace.");
        return string.Empty;
    }
}
