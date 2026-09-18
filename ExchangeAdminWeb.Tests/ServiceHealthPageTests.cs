using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Services;
using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Source-text guards over Components/Pages/ServiceHealth.razor, explicitly NOT behavioural
/// coverage: there is no bUnit harness in this repo, so no test here can render the page or
/// observe which branch a handler takes. They are tripwires - a green suite is never proof the
/// page behaves correctly. The one they exist for is the MarkupString boundary
/// (docs/ServiceHealth-Plan.md AC13): the page renders third-party HTML, and it may only ever do
/// so from a field the service already sanitized.
/// </summary>
public class ServiceHealthPageTests
{
    private static string RepoFile(params string[] parts)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "ExchangeAdminWeb.csproj")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        var path = Path.Combine([dir!, .. parts]);
        Assert.True(File.Exists(path), $"File not found at {path}");
        return File.ReadAllText(path);
    }

    private static string PageSource() => RepoFile("Components", "Pages", "ServiceHealth.razor");

    private static string PageStyles() => RepoFile("Components", "Pages", "ServiceHealth.razor.css");

    [Fact]
    public void Page_MarkupStringIsOnlyEverUsedOnSanitizedFields()
    {
        var source = PageSource();
        var uses = Regex.Matches(source, @"\(MarkupString\)([A-Za-z0-9_\.]+)");

        Assert.NotEmpty(uses);
        foreach (Match use in uses)
        {
            var expression = use.Groups[1].Value;
            Assert.True(
                expression.EndsWith("ImpactDescriptionHtml", StringComparison.Ordinal)
                || expression.EndsWith("ContentHtml", StringComparison.Ordinal)
                || expression.EndsWith("ValueHtml", StringComparison.Ordinal),
                $"MarkupString applied to '{expression}', which is not a service-sanitized field.");
        }
    }

    [Fact]
    public void Page_IsGatedByTheCatalogPolicyAndRedirectsWhenDenied()
    {
        var source = PageSource();

        Assert.Contains("@attribute [Authorize(Policy = \"ServiceHealth\")]", source);
        Assert.Contains("AuthorizeAsync(user, \"ServiceHealth\")", source);
        Assert.Contains("access-denied", source);
    }

    [Fact]
    public void Page_ShowsTheNotConfiguredBannerRatherThanAnEmptyBoard()
    {
        var source = PageSource();

        Assert.Contains("!HealthService.IsAvailable", source);
        Assert.Contains("is not configured", source);
    }

    [Fact]
    public void Page_AuditsBothTheSuccessAndFailurePaths()
    {
        var source = PageSource();

        Assert.Equal(2, Regex.Matches(source, @"Audit\.LogLookupAction").Count);
    }

    [Fact]
    public void Page_CarriesTheModuleVersionMarker()
    {
        Assert.Contains("<ModuleVersion />", PageSource());
    }

    [Fact]
    public void Page_ServiceCardsAreClickableAndCarryAnIssueCount()
    {
        var source = PageSource();

        Assert.Contains("SelectService(service.Id)", source);
        Assert.Contains("status.IssueCountFor(service.Id)", source);
    }

    [Fact]
    public void Page_OffersTheServiceFilterStatusFilterAndSortOrder()
    {
        var source = PageSource();

        Assert.Contains("id=\"service-filter\"", source);
        Assert.Contains("id=\"status-filter\"", source);
        Assert.Contains("id=\"sort-order\"", source);
        Assert.Contains("ClearFilters", source);
    }

    [Fact]
    public void Page_MirrorsTheOriginalDashboardStructure()
    {
        // Owner ruling 2026-09-08: the module reproduces the standalone dashboard's appearance.
        // That board is a compact service grid over a single "Current Issues" list, and a card
        // click narrows both halves to that service rather than expanding in place.
        var source = PageSource();

        Assert.Contains("sh-summary", source);
        Assert.Contains("sh-services-grid", source);
        Assert.Contains("Current Issues", source);
        Assert.Contains("No issues for selected filters", source);
        Assert.Contains("No services match your filter criteria.", source);
        Assert.Single(Regex.Matches(source, @"private RenderFragment<ServiceIncident> IncidentCard"));
        Assert.Single(Regex.Matches(source, @"@IncidentCard\(issue\)"));
    }

    [Fact]
    public void Styles_UseThemeTokensRatherThanLiteralColours()
    {
        // wwwroot/app.css: "Everything below this block should reference a token, never a literal
        // hex." The port keeps the original's geometry but must not hard-code its light palette,
        // or the nine non-default themes render unreadable text on the wrong ground.
        var css = PageStyles();
        var hexes = Regex.Matches(css, @"#[0-9a-fA-F]{3,8}\b").Select(m => m.Value).ToArray();

        // The one deliberate literal is the header icon accent. It sits on the brand-filled
        // header band in every theme and so has no token to take.
        Assert.All(hexes, hex => Assert.Equal("#4fc3f7", hex));
        Assert.Contains("var(--ui-surface)", css);
        Assert.Contains("var(--ui-danger)", css);
    }

    [Fact]
    public void Styles_CarryNoGradient()
    {
        // Owner ruling 2026-09-09, in full: "no gradients". The 1.3.0 header band carried the
        // original stylesheet's own two-stop gradient; the band is a flat brand fill now. This
        // is the one place the port deliberately departs from the original, so nothing may
        // reintroduce a gradient here - not the header, not a card, not a hover state.
        Assert.DoesNotContain("gradient", PageStyles(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- Filter, sort and projection behaviour (real behaviour, not source text) --------------

    private static ServiceHealthStatus Sample() => new()
    {
        Services =
        [
            new ServiceHealthEntry { Id = "Exchange", DisplayName = "Exchange Online", Status = "serviceDegradation" },
            new ServiceHealthEntry { Id = "SharePoint", DisplayName = "SharePoint Online", Status = "serviceOperational" },
            new ServiceHealthEntry { Id = "Teams", DisplayName = "Microsoft Teams", Status = "serviceInterruption" }
        ],
        Issues =
        [
            new ServiceIncident { Id = "EX1", ServiceId = "Exchange" },
            new ServiceIncident { Id = "EX2", ServiceId = "Exchange" },
            new ServiceIncident { Id = "TM1", ServiceId = "Teams" }
        ]
    };

    [Fact]
    public void FilterServices_AllServicesAndAllStatusesReturnsEverything()
    {
        Assert.Equal(3, ServiceHealth.FilterServices(Sample(), "all", "all").Count);
    }

    [Fact]
    public void FilterServices_NarrowsByServiceThenByStatus()
    {
        Assert.Equal(["Exchange"], ServiceHealth.FilterServices(Sample(), "Exchange", "all").Select(s => s.Id));
        Assert.Equal(["SharePoint"], ServiceHealth.FilterServices(Sample(), "all", "serviceOperational").Select(s => s.Id));
        Assert.Empty(ServiceHealth.FilterServices(Sample(), "Exchange", "serviceOperational"));
    }

    [Fact]
    public void FilterServices_MatchesTheServiceIdExactlyNotAsASubstring()
    {
        // The dropdown carries an id. A substring match would let "Teams" also select
        // "TeamsLiveEvents", which is a different service with a different status.
        var status = new ServiceHealthStatus
        {
            Services =
            [
                new ServiceHealthEntry { Id = "Teams", DisplayName = "Microsoft Teams" },
                new ServiceHealthEntry { Id = "TeamsLiveEvents", DisplayName = "Teams Live Events" }
            ]
        };

        Assert.Equal(["Teams"], ServiceHealth.FilterServices(status, "Teams", "all").Select(s => s.Id));
    }

    [Fact]
    public void FilterServices_NotHealthyExcludesOnlyTheOperationalOnes()
    {
        Assert.Equal(
            ["Exchange", "Teams"],
            ServiceHealth.FilterServices(Sample(), "all", "notHealthy").Select(s => s.Id));
    }

    [Fact]
    public void FilterServices_WithIssuesKeepsOnlyTheServicesThatHaveOpenOnes()
    {
        Assert.Equal(
            ["Exchange", "Teams"],
            ServiceHealth.FilterServices(Sample(), "all", "withIssues").Select(s => s.Id));
    }

    [Fact]
    public void IssuesFor_ShowsOnlyTheIssuesOfTheServicesLeftOnTheGrid()
    {
        var status = Sample();
        var visible = ServiceHealth.FilterServices(status, "Exchange", "all");

        Assert.Equal(["EX1", "EX2"], ServiceHealth.IssuesFor(status, visible).Select(i => i.Id).Order());
    }

    [Fact]
    public void IssuesFor_PutsTheMostRecentlyStartedIssueFirst()
    {
        var status = new ServiceHealthStatus
        {
            Services = [new ServiceHealthEntry { Id = "Exchange", DisplayName = "Exchange Online" }],
            Issues =
            [
                new ServiceIncident { Id = "OLD", ServiceId = "Exchange", StartDateTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero) },
                new ServiceIncident { Id = "NEW", ServiceId = "Exchange", StartDateTime = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero) }
            ]
        };

        Assert.Equal(["NEW", "OLD"], ServiceHealth.IssuesFor(status, status.Services).Select(i => i.Id));
    }

    [Fact]
    public void ActiveIssueCount_CountsIssuesWithNoEndTimeNotUnresolvedOnes()
    {
        // This is the original dashboard's own definition. An issue can carry an end time while
        // Microsoft still reports it unresolved, and the original does not count those.
        var status = new ServiceHealthStatus
        {
            Issues =
            [
                new ServiceIncident { Id = "A", IsResolved = false },
                new ServiceIncident { Id = "B", IsResolved = false, EndDateTime = DateTimeOffset.UtcNow },
                new ServiceIncident { Id = "C", IsResolved = true, EndDateTime = DateTimeOffset.UtcNow }
            ]
        };

        // Only A. Counting "not resolved" instead would wrongly include B.
        Assert.Equal(1, ServiceHealth.ActiveIssueCount(status));
    }

    [Fact]
    public void SortServices_DefaultsToWorstFirstThenAlphabetical()
    {
        var status = Sample();
        var sorted = ServiceHealth.SortServices(status.Services, status, "status").Select(s => s.Id);

        Assert.Equal(["Teams", "Exchange", "SharePoint"], sorted);
    }

    [Fact]
    public void SortServices_BreaksAStatusTieOnIssueCountThenName()
    {
        var status = new ServiceHealthStatus
        {
            Services =
            [
                new ServiceHealthEntry { Id = "Zulu", DisplayName = "Zulu", Status = "serviceDegradation" },
                new ServiceHealthEntry { Id = "Alpha", DisplayName = "Alpha", Status = "serviceDegradation" },
                new ServiceHealthEntry { Id = "Busy", DisplayName = "Busy", Status = "serviceDegradation" }
            ],
            Issues = [new ServiceIncident { Id = "B1", ServiceId = "Busy" }]
        };

        Assert.Equal(
            ["Busy", "Alpha", "Zulu"],
            ServiceHealth.SortServices(status.Services, status, "status").Select(s => s.Id));
    }

    [Fact]
    public void SortServices_NameIgnoresStatusEntirely()
    {
        var status = Sample();

        Assert.Equal(
            ["Exchange", "Teams", "SharePoint"],
            ServiceHealth.SortServices(status.Services, status, "name").Select(s => s.Id));
    }

    [Fact]
    public void SortServices_IssuesPutsTheBusiestServiceFirst()
    {
        var status = Sample();

        Assert.Equal(
            ["Exchange", "Teams", "SharePoint"],
            ServiceHealth.SortServices(status.Services, status, "issues").Select(s => s.Id));
    }

    [Fact]
    public void StatusRank_SortsAnUnknownFutureStatusAboveHealthy()
    {
        Assert.True(ServiceHealth.StatusRank("somethingNew") < ServiceHealth.StatusRank("serviceOperational"));
        Assert.True(ServiceHealth.StatusRank("serviceInterruption") < ServiceHealth.StatusRank("serviceDegradation"));
        Assert.True(ServiceHealth.StatusRank("serviceDegradation") < ServiceHealth.StatusRank("somethingNew"));
    }

    [Theory]
    [InlineData("serviceOperational", "bi-check-circle-fill", "status-healthy")]
    [InlineData("serviceDegradation", "bi-exclamation-triangle-fill", "status-warning")]
    [InlineData("serviceInterruption", "bi-x-circle-fill", "status-error")]
    [InlineData("extendedRecovery", "bi-info-circle-fill", "status-info")]
    [InlineData("somethingNew", "bi-question-circle-fill", "status-unknown")]
    public void StatusIcon_KeepsEachSeverityVisuallyDistinct(string status, string icon, string colour)
    {
        Assert.Equal(icon, ServiceHealth.StatusIcon(status));
        Assert.Equal(colour, ServiceHealth.StatusIconClass(status));
    }

    [Theory]
    [InlineData("rootCauseSummary", "Root Cause Summary")]
    [InlineData("summary", "Summary")]
    [InlineData("", "")]
    public void Humanize_SplitsACamelCaseGraphKeyIntoWords(string key, string expected)
    {
        Assert.Equal(expected, ServiceHealth.Humanize(key));
    }

    // ---------------------------------------------------------------------------------------
    // Load-feedback tripwires (docs/ServiceHealthLoadFeedback-Plan.md).
    //
    // The page prerenders, and prerendering emits no HTML until OnInitializedAsync completes.
    // While the Graph call was awaited there, none of the page's three spinners could ever
    // render on a first load: the operator saw the previous page sit unchanged for the whole
    // round trip and clicked again. These guard the shape that fixes it.
    //
    // Every scanner below strips comments before matching. Two tripwires in the migration
    // button-gating work failed against correct code because they matched the words "await"
    // and "finally" inside explanatory comments - and the comments added by this very change
    // name OnAfterRenderAsync, LoadAsync and StateHasChanged.
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Removes Razor (@* *@), block and line comments. Applied to extracted method bodies
    /// rather than whole files; none of the bodies scanned here contain a "//" inside a string
    /// literal, which is the one case this would mangle.
    /// </summary>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"@\*.*?\*@", " ", RegexOptions.Singleline);
        source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(source, @"//[^\r\n]*", " ");
    }

    /// <summary>
    /// Returns the brace-matched body of the named member, comments already stripped.
    /// </summary>
    private static string MemberBody(string source, string signatureFragment)
    {
        var start = source.IndexOf(signatureFragment, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signatureFragment}' not found in source.");

        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"No body brace found after '{signatureFragment}'.");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return StripComments(source[(open + 1)..i]);
        }

        Assert.Fail($"Unbalanced braces in the body of '{signatureFragment}'.");
        return string.Empty;
    }

    [Fact]
    public void OnInitializedAsync_DoesNotAwaitTheHealthServiceDirectly()
    {
        var body = MemberBody(PageSource(), "protected override async Task OnInitializedAsync()");

        Assert.DoesNotContain("LoadAsync", body, StringComparison.Ordinal);
        Assert.DoesNotContain("HealthService", body, StringComparison.Ordinal);

        // It must still leave the page in its loading state, or the deferred load runs behind
        // an idle-looking page and the whole point is lost.
        Assert.Contains("isLoading = true", body, StringComparison.Ordinal);
        Assert.Contains("authChecked = true", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInitialLoadRunsFromOnAfterRenderAsyncBehindAOneShotGuard()
    {
        var body = MemberBody(PageSource(), "protected override async Task OnAfterRenderAsync(bool firstRender)");

        Assert.Contains("firstRender", body, StringComparison.Ordinal);
        Assert.Contains("loadStarted", body, StringComparison.Ordinal);
        // The denied path leaves authChecked false while NavigateTo is in flight. Without this
        // term an unauthorized visitor triggers a Graph call on the way out.
        Assert.Contains("authChecked", body, StringComparison.Ordinal);
        Assert.Contains("LoadAsync(forceRefresh: false)", body, StringComparison.Ordinal);

        var latch = body.IndexOf("loadStarted = true", StringComparison.Ordinal);
        var load = body.IndexOf("await LoadAsync", StringComparison.Ordinal);
        Assert.True(latch >= 0 && load > latch,
            "loadStarted must be latched before the load is awaited, or a re-render re-enters it.");
    }

    [Fact]
    public void LoadAsyncCallsStateHasChangedInItsFinallyBlock()
    {
        var body = MemberBody(PageSource(), "private async Task LoadAsync(bool forceRefresh)");
        var final = MemberBody(body, "finally");

        // Blazor does not auto-render after OnAfterRenderAsync. Without this the spinner stays
        // up and Refresh stays disabled forever - worse than the bug being fixed.
        Assert.Contains("StateHasChanged()", final, StringComparison.Ordinal);
        Assert.Contains("isLoading = false", final, StringComparison.Ordinal);
    }

    [Fact]
    public void TheModuleVersionWasBumpedForTheLoadFeedbackChange()
    {
        var module = new ExchangeAdminWeb.Modules.ModuleCatalog().GetById("ServiceHealth");

        Assert.NotNull(module);
        Assert.Equal("1.3.2", module!.Version);
    }
}
