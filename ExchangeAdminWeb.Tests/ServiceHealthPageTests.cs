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
    private static string PageSource()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "ExchangeAdminWeb.csproj")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        var path = Path.Combine(dir!, "Components", "Pages", "ServiceHealth.razor");
        Assert.True(File.Exists(path), $"Page not found at {path}");
        return File.ReadAllText(path);
    }

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
    public void Page_ServiceRowsAreClickableAndCarryAnIssueCount()
    {
        var source = PageSource();

        Assert.Contains("ToggleService(service.Id)", source);
        Assert.Contains("status.IssueCountFor(service.Id)", source);
        Assert.Contains("status.IssuesFor(service.Id)", source);
    }

    [Fact]
    public void Page_OffersTheServiceAndStatusFilters()
    {
        var source = PageSource();

        Assert.Contains("id=\"service-filter\"", source);
        Assert.Contains("id=\"status-filter\"", source);
        Assert.Contains("ClearFilters", source);
    }

    [Fact]
    public void Page_RendersEveryIncidentThroughOneSharedFragment()
    {
        // Two call sites (inline under a service, and the list below), one renderer - so the
        // expanded view can never drift between the two places an operator meets it.
        var source = PageSource();

        Assert.Single(Regex.Matches(source, @"private RenderFragment<ServiceIncident> IncidentCard"));
        Assert.Equal(2, Regex.Matches(source, @"@IncidentCard\(issue\)").Count);
    }

    // ---- Filter projectors (real behaviour, not source text) --------------------------------

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
            new ServiceIncident { Id = "TM1", ServiceId = "Teams" }
        ]
    };

    [Fact]
    public void FilterServices_AllAndAllReturnsEverything()
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
    public void FilterServices_NotHealthyExcludesOnlyTheOperationalOnes()
    {
        Assert.Equal(
            ["Exchange", "Teams"],
            ServiceHealth.FilterServices(Sample(), "all", "notHealthy").Select(s => s.Id));
    }

    [Fact]
    public void FilterIssues_FollowsTheServiceFilterOnly()
    {
        Assert.Equal(2, ServiceHealth.FilterIssues(Sample(), "all").Count);
        Assert.Equal(["EX1"], ServiceHealth.FilterIssues(Sample(), "Exchange").Select(i => i.Id));
        Assert.Empty(ServiceHealth.FilterIssues(Sample(), "SharePoint"));
    }

    [Fact]
    public void FilteredServiceName_FallsBackToTheIdWhenTheServiceIsGone()
    {
        Assert.Equal("Exchange Online", ServiceHealth.FilteredServiceName(Sample(), "exchange"));
        Assert.Equal("Ghost", ServiceHealth.FilteredServiceName(Sample(), "Ghost"));
    }

    [Theory]
    [InlineData("serviceOperational", "bg-success")]
    [InlineData("serviceDegradation", "bg-warning text-dark")]
    [InlineData("serviceInterruption", "bg-danger")]
    [InlineData("somethingNew", "bg-secondary")]
    public void ServiceBadgeClass_KeepsDegradedAndInterruptedApart(string status, string expected)
    {
        Assert.Equal(expected, ServiceHealth.ServiceBadgeClass(status));
    }
}
