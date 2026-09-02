using System.Text.RegularExpressions;
using ExchangeAdminWeb.Components.Pages;
using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the IntuneDevices page (docs/IntuneDeviceManagement-Plan.md S2): the pure projectors,
/// and the source-text wiring for the read-only search/detail shape - the visible truncation
/// notice (T1), the hasSearched three-state rule (the blr-3 class), the audit-on-read via
/// LogLookupAction (Read-alerting classification), the D3 standing note, and the absence of any
/// write action in this slice (S3-S5 are not yet implemented).
/// </summary>
/// <remarks>
/// Source-text guards, explicitly NOT behavioural coverage: there is no bUnit harness in this
/// repo (plan, Verification / Test plan), so no test can render the page or observe which branch
/// a handler takes. Stated as tripwires so a green suite is never read as proof the page behaves
/// correctly.
/// </remarks>
public class IntuneDevicesPageTests
{
    [Fact]
    public void DescribeSearch_NoTerm_ReturnsNoSearchTermMarker()
    {
        Assert.Equal("(no search term)", IntuneDevices.DescribeSearch(null));
        Assert.Equal("(no search term)", IntuneDevices.DescribeSearch(""));
        Assert.Equal("(no search term)", IntuneDevices.DescribeSearch("   "));
    }

    [Fact]
    public void DescribeSearch_TrimsAndReturnsTheTerm()
    {
        Assert.Equal("contoso-laptop-01", IntuneDevices.DescribeSearch("  contoso-laptop-01  "));
    }

    [Theory]
    [InlineData("compliant", "bg-success")]
    [InlineData("noncompliant", "bg-danger")]
    [InlineData("conflict", "bg-warning text-dark")]
    [InlineData("error", "bg-danger")]
    [InlineData("inGracePeriod", "bg-warning text-dark")]
    [InlineData("configManager", "bg-info text-dark")]
    [InlineData("unknown", "bg-secondary")]
    [InlineData("COMPLIANT", "bg-success")]
    public void ComplianceBadgeClass_DocumentedValue_MapsToExpectedBadge(string complianceState, string expected)
    {
        Assert.Equal(expected, IntuneDevices.ComplianceBadgeClass(complianceState));
    }

    [Fact]
    public void ComplianceBadgeClass_UnknownFutureValue_FallsBackToNeutralBadge()
    {
        // complianceState is stored as a plain string and must still render with a neutral badge
        // rather than being dropped or miscategorized, mirroring RiskyUsers' RiskLevelBadgeClass
        // rule for the same reason (Graph extends these enums without notice).
        Assert.Equal("bg-light text-dark border", IntuneDevices.ComplianceBadgeClass("somethingNewMicrosoftAdded"));
    }

    [Fact]
    public void FormatStorage_ZeroTotal_ReportsUnknownRatherThanZeroOfZero()
    {
        Assert.Equal("(unknown)", IntuneDevices.FormatStorage(0, 0));
    }

    [Fact]
    public void FormatStorage_ComputesGigabytesFromBytes()
    {
        var oneGb = 1024L * 1024 * 1024;
        Assert.Equal("2.0 GB free of 64.0 GB", IntuneDevices.FormatStorage(2 * oneGb, 64 * oneGb));
    }

    [Fact]
    public void IntuneDevices_ReadPathNeverAlertEmails()
    {
        // Read-alerting classification (owner-reviewed, plan): reads are audited, never
        // alert-emailed. Scoped to the two read handlers so a later write slice's legitimate
        // admin notification cannot be mistaken for a violation here.
        var read = MethodBody("SearchAsync") + MethodBody("ToggleDetailAsync");

        Assert.DoesNotContain("Email.", read);
        Assert.DoesNotContain("SendAdminNotificationAsync", read);
        Assert.Contains("LogLookupAction", read);
    }

    [Fact]
    public void IntuneDevices_HasNoWriteActionsInThisSlice()
    {
        // S2 is read-only (search + detail). Delete/Retire/Wipe/EntraDelete are S3-S5; this page
        // must not reach ahead of its own slice.
        var text = PageSource();

        Assert.DoesNotContain("DeleteDeviceAsync", text);
        Assert.DoesNotContain("RetireDeviceAsync", text);
        Assert.DoesNotContain("WipeDeviceAsync", text);
        Assert.DoesNotContain("RemoveEntraDeviceAsync", text);
        Assert.DoesNotContain("IntuneDevicesDelete\"", text);
        Assert.DoesNotContain("IntuneDevicesPrivileged\"", text);
        Assert.DoesNotContain("IntuneDevicesEntraDelete\"", text);
    }

    [Fact]
    public void IntuneDevices_AuditsSearchOnBothSuccessAndFailure()
    {
        var text = PageSource();

        var calls = Regex.Matches(
            text,
            @"LogLookupAction\(\s*[^;]*?""IntuneDevices_Search""",
            RegexOptions.Singleline);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void IntuneDevices_AuditsDetailLookupOnBothSuccessAndFailure()
    {
        var text = PageSource();

        var calls = Regex.Matches(
            text,
            @"LogLookupAction\(\s*[^;]*?""IntuneDevices_Detail""",
            RegexOptions.Singleline);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public void IntuneDevices_ClearsHasSearchedAtStartOfEverySearch()
    {
        // blr-3 class defect: a second search must retract the first search's verdict before the
        // new one resolves, not only on the page's first ever search.
        var body = MethodBody("SearchAsync");

        Assert.Contains("hasSearched = false;", body);
        Assert.Contains("await Task.Yield();", body);
        Assert.Contains("hasSearched = true;", body);
    }

    [Fact]
    public void IntuneDevices_RendersVisibleTruncationNotice()
    {
        // T1: a response carrying @odata.nextLink must render a visible truncation notice - a
        // silently truncated device list is the exact failure mode T1 exists to prevent.
        var text = PageSource();

        Assert.Contains("@if (truncated)", text);
        Assert.Contains("more devices exist. Narrow the search.", text);
    }

    [Fact]
    public void IntuneDevices_TruncatedEmptyResult_DoesNotClaimTheDeviceDoesNotExist()
    {
        // T2 fallback rule: a client-side match over a truncated page must never render as "no
        // such device" - it renders as "no match in the first N devices searched".
        var text = PageSource();

        Assert.Contains("No match in the first", text);
        Assert.Contains("searchedCount", text);
    }

    [Fact]
    public void IntuneDevices_DisplaysModuleVersionNextToHeading()
    {
        Assert.Contains("<ModuleVersion", PageSource());
    }

    [Fact]
    public void IntuneDevices_StandingNoteAboutEntraAndCompanyDataSurvives()
    {
        // D3 / T3: a standing note that deleting the Intune record neither removes company data
        // from the device nor removes the Entra ID device object.
        var text = PageSource();

        Assert.Contains("does not remove company data", text);
        Assert.Contains("does not remove the device's Entra ID object", text);
    }

    [Fact]
    public void IntuneDevices_PageAuthorizesOnTheMainPolicy()
    {
        var text = PageSource();

        Assert.Contains("[Authorize(Policy = \"IntuneDevices\")]", text);
        Assert.Contains("AuthorizeAsync(user, \"IntuneDevices\")", text);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static string PageSource() =>
        File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "IntuneDevices.razor"));

    /// <summary>
    /// A brace-balanced method body from the page's @code block, so a marker appearing later in
    /// the file cannot end the slice early and report a real change as missing.
    /// </summary>
    private static string MethodBody(string methodName)
    {
        var source = PageSource();
        var signature = Regex.Match(source, $@"(private|internal|protected)[^\r\n]*\b{Regex.Escape(methodName)}\(");
        Assert.True(signature.Success, $"'{methodName}' is no longer declared in IntuneDevices.razor.");

        var open = source.IndexOf('{', signature.Index);
        Assert.True(open > 0, $"no body found for '{methodName}'.");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return source[signature.Index..(i + 1)];
        }

        throw new InvalidOperationException($"unbalanced braces after '{methodName}'.");
    }
}
