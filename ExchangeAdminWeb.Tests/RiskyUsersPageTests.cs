using System.Text.RegularExpressions;
using ExchangeAdminWeb.Components.Pages;
using ExchangeAdminWeb.Services;
using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the RiskyUsers read page (docs/RiskyUsersModule-Plan.md, S3): the pure filter-
/// description and risk-level-badge projectors, and the source-text wiring for the D2 audit-only
/// shape, the hasQueried three-state rule, and the visible truncation notice. Source-text guards
/// because there is no bUnit harness in this repo (plan, Verification).
/// </summary>
public class RiskyUsersPageTests
{
    [Fact]
    public void DescribeFilter_NoFieldsSet_ReturnsNoFilterMarker()
    {
        var target = RiskyUsers.DescribeFilter(new RiskyUserFilter(null, null, null));

        Assert.Equal("(no filter)", target);
    }

    [Fact]
    public void DescribeFilter_CombinesGivenFields()
    {
        var target = RiskyUsers.DescribeFilter(new RiskyUserFilter("high", "atRisk", "contoso"));

        Assert.Equal("riskLevel=high;riskState=atRisk;upnContains=contoso", target);
    }

    [Fact]
    public void DescribeFilter_BlankFieldsAreOmitted()
    {
        var target = RiskyUsers.DescribeFilter(new RiskyUserFilter("", "  ", null));

        Assert.Equal("(no filter)", target);
    }

    [Theory]
    [InlineData("high", "bg-danger")]
    [InlineData("medium", "bg-warning text-dark")]
    [InlineData("low", "bg-info text-dark")]
    [InlineData("hidden", "bg-secondary")]
    [InlineData("none", "bg-success")]
    [InlineData("HIGH", "bg-danger")]
    public void RiskLevelBadgeClass_DocumentedValue_MapsToExpectedBadge(string riskLevel, string expected)
    {
        Assert.Equal(expected, RiskyUsers.RiskLevelBadgeClass(riskLevel));
    }

    [Fact]
    public void RiskLevelBadgeClass_UnknownFutureValue_FallsBackToNeutralBadge()
    {
        // riskLevel/riskState are stored as plain strings and must still render with a neutral
        // badge rather than being dropped or miscategorized (S2 rule 4 / AC5). Both Microsoft's
        // own placeholder and an entirely undocumented literal must land here.
        Assert.Equal("bg-light text-dark border", RiskyUsers.RiskLevelBadgeClass("unknownFutureValue"));
        Assert.Equal("bg-light text-dark border", RiskyUsers.RiskLevelBadgeClass("somethingNewMicrosoftAdded"));
    }

    [Fact]
    public void RiskyUsers_DoesNotInjectOrCallEmailService()
    {
        // D2 (owner, 2026-08-31): reads are audited, never alert-emailed. AC17's audit-only shape
        // requires EmailService to be unreachable from the read path, so a later edit cannot wire
        // an alert in silently - this guard fails the instant one does.
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "RiskyUsers.razor"));

        Assert.DoesNotContain("EmailService", text);
        Assert.DoesNotContain("SendAdminNotificationAsync", text);
    }

    [Fact]
    public void RiskyUsers_AuditsListQueryOnBothSuccessAndFailure()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "RiskyUsers.razor"));

        var calls = Regex.Matches(
            text,
            @"LogModuleAction\(\s*[^;]*?""RiskyUsers_List""\s*,\s*""RiskyUsers""",
            RegexOptions.Singleline);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void RiskyUsers_AuditsHistoryQueryOnBothSuccessAndFailure()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "RiskyUsers.razor"));

        var calls = Regex.Matches(
            text,
            @"LogModuleAction\(\s*[^;]*?""RiskyUsers_History""\s*,\s*""RiskyUsers""",
            RegexOptions.Singleline);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void RiskyUsers_ClearsHasQueriedAtStartOfEveryQuery()
    {
        // blr-3 class defect: a second query must retract the first query's verdict before the
        // new one resolves, not only on the page's first ever query.
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "RiskyUsers.razor"));

        var start = text.IndexOf("private async Task LoadRiskyUsersAsync()", StringComparison.Ordinal);
        Assert.True(start >= 0, "LoadRiskyUsersAsync method not found in RiskyUsers.razor.");
        var end = text.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        var body = end > start ? text[start..end] : text[start..];

        Assert.Contains("hasQueried = false;", body);
        Assert.Contains("await Task.Yield();", body);
        Assert.Contains("hasQueried = true;", body);
    }

    [Fact]
    public void RiskyUsers_RendersVisibleTruncationNotice()
    {
        // AC7: a response carrying @odata.nextLink must render a visible truncation notice naming
        // the cap - a silently truncated risky-user list is the BitLocker cap-before-match defect
        // class recurring on a security surface.
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "RiskyUsers.razor"));

        Assert.Contains("@if (truncated)", text);
        Assert.Contains("more exist. Narrow the filter.", text);
    }

    [Fact]
    public void RiskyUsers_DisplaysModuleVersionNextToHeading()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "RiskyUsers.razor"));

        Assert.Contains("<ModuleVersion", text);
    }
}
