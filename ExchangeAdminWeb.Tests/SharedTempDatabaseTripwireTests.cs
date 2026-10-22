using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a26q: the permission validator's tests built their protected-principal, enablement, section-access and app-settings
/// stores in the temp directory itself - one <c>exchangeadmin.db</c> shared by every validator, every class and every run -
/// so a row any run left there changed what the next one's tests meant.
/// </summary>
/// <remarks>
/// Two halves. The one helper every test store is built through refuses the temp directory itself, whatever expression
/// names it; and no test source names that directory except as the first segment of a <c>Path.Combine</c> - a directory
/// of its own under it - nor puts the config database directly in it. <c>TestConfigStore.cs</c> and this class name it on
/// purpose and are not scanned.
/// </remarks>
public sealed class SharedTempDatabaseTripwireTests
{
    [Fact]
    public void TheTestStoreHelper_RefusesTheTempDirectoryItself()
    {
        var root = Path.GetTempPath();

        Assert.Throws<InvalidOperationException>(() => TestConfigStore.Create(root));
        Assert.Throws<InvalidOperationException>(() => TestConfigStore.Create(Path.TrimEndingDirectorySeparator(root)));
    }

    [Fact]
    public void NoTestSource_UsesTheTempDirectoryItself_OrPutsTheConfigDatabaseInIt()
    {
        var project = Path.GetDirectoryName(
            AuditCategoryFilingTests.FindRepoFile("ExchangeAdminWeb.Tests", "ExchangeAdminWeb.Tests.csproj"))!;
        var exempt = new HashSet<string>(StringComparer.Ordinal) { "TestConfigStore.cs", "SharedTempDatabaseTripwireTests.cs" };
        var tempRoot = new Regex(@"\bGetTempPath\s*\(\s*\)");
        var firstSegment = new Regex(@"\bCombine\s*\(\s*(?:System\s*\.\s*IO\s*\.\s*)?Path\s*\.\s*(?=GetTempPath\s*\(\s*\)\s*,)");
        var databaseInRoot = new Regex(@"\bGetTempPath\s*\(\s*\)\s*,\s*@?""exchangeadmin[^""]*\.db""", RegexOptions.IgnoreCase);

        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
        {
            var path = Path.GetRelativePath(project, file).Replace('\\', '/');
            if (path.StartsWith("bin/", StringComparison.Ordinal) || path.StartsWith("obj/", StringComparison.Ordinal)
                || exempt.Contains(path))
                continue;

            var code = WithoutComments(File.ReadAllText(file));
            var underCombine = firstSegment.Matches(code).Select(m => m.Index + m.Length).ToHashSet();
            hits.AddRange(tempRoot.Matches(code)
                .Where(m => !underCombine.Contains(m.Index))
                .Select(m => $"{path}:{RazorSyntax.LineOf(code, m.Index)} names the temp directory itself"));
            hits.AddRange(databaseInRoot.Matches(code)
                .Select(m => $"{path}:{RazorSyntax.LineOf(code, m.Index)} puts the config database directly in it"));
        }

        Assert.True(hits.Count == 0,
            "A test reaches the shared temp directory; give it a directory of its own under it: " + string.Join("; ", hits));
    }

    /// <summary>The source with every comment blanked to spaces, line breaks kept, so a line number still points at it.</summary>
    private static string WithoutComments(string code)
    {
        var buffer = code.ToCharArray();
        foreach (var trivia in CSharpSyntaxTree.ParseText(code).GetRoot().DescendantTrivia(descendIntoTrivia: true))
        {
            if (!trivia.IsKind(SyntaxKind.SingleLineCommentTrivia) && !trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
                && !trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
                && !trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia))
                continue;

            for (var i = trivia.FullSpan.Start; i < trivia.FullSpan.End; i++)
            {
                if (buffer[i] != '\n')
                    buffer[i] = ' ';
            }
        }

        return new string(buffer);
    }
}
