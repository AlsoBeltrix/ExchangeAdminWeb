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

        Assert.Contains("ToggleService(service.Id, serviceIssues)", source);
        Assert.Contains("status.IssuesFor(service.Id)", source);
    }

    [Fact]
    public void Page_OffersTheNameFilterStatusFilterAndSortOrder()
    {
        var source = PageSource();

        Assert.Contains("id=\"service-filter\"", source);
        Assert.Contains("id=\"status-filter\"", source);
        Assert.Contains("id=\"sort-order\"", source);
        Assert.Contains("ClearFilters", source);
    }

    [Fact]
    public void Page_HasNoSeparateIncidentDumpBelowTheServices()
    {
        // Owner ruling 2026-09-08: incidents live under the service they belong to and nowhere
        // else. One call site, one renderer - a second one would be the flat list coming back.
        var source = PageSource();

        Assert.Single(Regex.Matches(source, @"private RenderFragment<ServiceIncident> IncidentCard"));
        Assert.Single(Regex.Matches(source, @"@IncidentCard\(issue\)"));
        Assert.DoesNotContain("Open incidents and advisories", source);
    }

    // ---- Filter and sort projectors (real behaviour, not source text) ------------------------

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
    public void FilterServices_AnEmptyNameAndAllStatusesReturnsEverything()
    {
        Assert.Equal(3, ServiceHealth.FilterServices(Sample(), "", "all").Count);
        Assert.Equal(3, ServiceHealth.FilterServices(Sample(), "*", "all").Count);
    }

    [Fact]
    public void FilterServices_NarrowsByNameThenByStatus()
    {
        Assert.Equal(["Exchange"], ServiceHealth.FilterServices(Sample(), "Exchange", "all").Select(s => s.Id));
        Assert.Equal(["SharePoint"], ServiceHealth.FilterServices(Sample(), "", "serviceOperational").Select(s => s.Id));
        Assert.Empty(ServiceHealth.FilterServices(Sample(), "Exchange", "serviceOperational"));
    }

    [Fact]
    public void FilterServices_NotHealthyExcludesOnlyTheOperationalOnes()
    {
        Assert.Equal(
            ["Exchange", "Teams"],
            ServiceHealth.FilterServices(Sample(), "", "notHealthy").Select(s => s.Id));
    }

    [Fact]
    public void FilterServices_WithIssuesKeepsOnlyTheServicesThatHaveOpenOnes()
    {
        Assert.Equal(
            ["Exchange", "Teams"],
            ServiceHealth.FilterServices(Sample(), "", "withIssues").Select(s => s.Id));
    }

    [Theory]
    [InlineData("exch", "Exchange")]
    [InlineData("EXCH", "Exchange")]
    [InlineData("online", "Exchange,SharePoint")]
    [InlineData("*online", "Exchange,SharePoint")]
    [InlineData("exch*", "Exchange")]
    [InlineData("*teams*", "Teams")]
    [InlineData("micros?ft teams", "Teams")]
    [InlineData("nothing", "")]
    public void MatchesName_TreatsAPlainWordAsContainsAndHonoursWildcards(string pattern, string expectedIds)
    {
        string[] expected = expectedIds.Length == 0 ? [] : expectedIds.Split(',');
        var actual = ServiceHealth.FilterServices(Sample(), pattern, "all").Select(s => s.Id).ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void MatchesName_AnchorsAWildcardPatternSoItCannotMatchEverything()
    {
        // "exch*" is anchored: it must start the name. A bare "contains" would also match
        // "Microsoft Exchange", which is not what an operator typing a trailing star asked for.
        var status = new ServiceHealthStatus
        {
            Services = [new ServiceHealthEntry { Id = "X", DisplayName = "Microsoft Exchange" }]
        };

        Assert.Empty(ServiceHealth.FilterServices(status, "exch*", "all"));
        Assert.Single(ServiceHealth.FilterServices(status, "exch", "all"));
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
    [InlineData("serviceOperational", "bg-success")]
    [InlineData("serviceDegradation", "bg-warning text-dark")]
    [InlineData("serviceInterruption", "bg-danger")]
    [InlineData("somethingNew", "bg-secondary")]
    public void ServiceBadgeClass_KeepsDegradedAndInterruptedApart(string status, string expected)
    {
        Assert.Equal(expected, ServiceHealth.ServiceBadgeClass(status));
    }
}
