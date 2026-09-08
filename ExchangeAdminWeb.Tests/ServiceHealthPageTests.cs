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
                || expression.EndsWith("ContentHtml", StringComparison.Ordinal),
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
}
