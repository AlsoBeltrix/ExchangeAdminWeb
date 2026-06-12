using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Source-level guard for the Constitution rule "every mutating operation must
/// re-check authorization immediately before the write". Razor event handlers
/// are not unit-testable without a component host, so these tests parse the
/// page sources (same approach as ModuleCatalogTests.RoutesHaveMatchingPagesAndPolicies)
/// and fail if a known mutating handler loses its pre-write re-check.
/// </summary>
public class PageAuthorizationRecheckTests
{
    [Theory]
    [InlineData("MailboxPermissions.razor", "SubmitSingle")]
    [InlineData("MailboxPermissions.razor", "ProcessBulk")]
    [InlineData("CalendarPermissions.razor", "SubmitSingle")]
    [InlineData("CalendarPermissions.razor", "ProcessBulk")]
    public void MutatingHandler_RechecksAuthorizationBeforeWrite(string page, string handler)
    {
        var body = GetMethodBody(page, handler);
        Assert.Contains("await ReauthorizeAsync()", body);
    }

    [Fact]
    public void AdminEventLog_ExecuteUndo_RechecksBothPoliciesBeforeWrite()
    {
        var body = GetMethodBody("AdminEventLog.razor", "ExecuteUndo");
        Assert.Contains("AuthorizeAsync(authState.User, \"EventLog\")", body);
        Assert.Contains("AuthorizeAsync(authState.User, \"UndoAuditedActions\")", body);
    }

    private static string GetMethodBody(string pageFile, string methodName)
    {
        var path = Path.Combine(GetPagesDirectory(), pageFile);
        var source = File.ReadAllText(path);

        var signature = Regex.Match(source,
            $@"private\s+async\s+Task(<[^>]+>)?\s+{Regex.Escape(methodName)}\s*\(");
        Assert.True(signature.Success, $"{pageFile}: handler '{methodName}' not found");

        // Body = everything from the signature to the next method declaration
        // (or end of file). Coarse, but sufficient to detect a removed re-check.
        var start = signature.Index;
        var next = Regex.Match(source[(start + signature.Length)..],
            @"\n    private\s+(async\s+)?[A-Za-z]");
        return next.Success
            ? source.Substring(start, signature.Length + next.Index)
            : source[start..];
    }

    private static string GetPagesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var pages = Path.Combine(dir.FullName, "Components", "Pages");
            if (Directory.Exists(pages))
                return pages;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Components/Pages from test base directory.");
    }
}
