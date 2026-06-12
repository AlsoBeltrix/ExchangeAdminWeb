using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Source-level hygiene guard for Blazor page handlers: a discarded task
/// (`_ = SomethingAsync()`) means Blazor never observes completion, so the
/// page does not re-render when the work finishes - results stay invisible
/// until the next UI event ("click again and it appears"). Handlers must be
/// async Task and await their work.
/// </summary>
public class PageHandlerHygieneTests
{
    [Fact]
    public void Pages_DoNotFireAndForgetAsyncWork()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.GetFiles(GetPagesDirectory(), "*.razor"))
        {
            var source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source, @"_\s*=\s*[A-Z]\w*\s*\("))
            {
                var line = source[..match.Index].Count(c => c == '\n') + 1;
                offenders.Add($"{Path.GetFileName(file)}:{line}: {match.Value.Trim()}...");
            }
        }

        Assert.True(offenders.Count == 0,
            "Discarded tasks in page handlers (await them so the page re-renders on completion):\n" +
            string.Join("\n", offenders));
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
