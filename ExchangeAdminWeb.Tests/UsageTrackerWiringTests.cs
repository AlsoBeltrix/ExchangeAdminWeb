using ExchangeAdminWeb.Components.Layout;

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
}
