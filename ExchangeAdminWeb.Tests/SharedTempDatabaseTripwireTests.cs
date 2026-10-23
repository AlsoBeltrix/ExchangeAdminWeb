using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a26q: the permission validator's tests built their protected-principal, enablement, section-access and app-settings
/// stores in the temp directory itself - one <c>exchangeadmin.db</c> shared by every validator, every class and every run -
/// so a row any run left there changed what the next one's tests meant. aud-a26t: their audit log root was one directory
/// under it, shared the same way.
/// </summary>
/// <remarks>
/// Two halves. The one helper every test store is built through refuses the temp directory itself, whatever expression
/// names it; and no test source names that directory except as the first segment of a <c>Path.Combine</c> - a directory
/// of its own under it - nor puts the config database directly in it, nor sets <c>Audit:LogRoot</c> from it.
/// <c>TestConfigStore.cs</c> and this class name it on purpose and are not scanned.
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
        var tempRoot = new Regex(@"\bGetTempPath\s*\(\s*\)");
        var firstSegment = new Regex(@"\bCombine\s*\(\s*(?:System\s*\.\s*IO\s*\.\s*)?Path\s*\.\s*(?=GetTempPath\s*\(\s*\)\s*,)");
        var databaseInRoot = new Regex(@"\bGetTempPath\s*\(\s*\)\s*,\s*@?""exchangeadmin[^""]*\.db""", RegexOptions.IgnoreCase);

        var hits = new List<string>();
        foreach (var (path, code) in ScannedSources())
        {
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

    [Fact]
    public void NoTestSource_SetsTheAuditLogRootFromTheTempDirectory()
    {
        // aud-a26t. A log root named in place under the temp directory is one directory every test and every run shares,
        // and nothing deletes it: a test's logs go under the directory it made for itself, which it deletes. The value is
        // read up to the end of its entry - an indexer assignment or a collection-initializer pair.
        var logRootFromTemp = new Regex(@"""Audit:LogRoot""\s*(?:\]\s*=|,)\s*[^,;\r\n}]*\bGetTempPath\s*\(");

        var hits = ScannedSources()
            .SelectMany(source => logRootFromTemp.Matches(source.Code)
                .Select(m => $"{source.Path}:{RazorSyntax.LineOf(source.Code, m.Index)} sets Audit:LogRoot from the temp directory"))
            .ToList();

        Assert.True(hits.Count == 0,
            "A test's audit logs go to a directory every test shares; put them under the test's own directory: " + string.Join("; ", hits));
    }

    /// <summary>Every test source, comments blanked, but the two files that name the temp directory on purpose.</summary>
    private static IEnumerable<(string Path, string Code)> ScannedSources()
    {
        var project = Path.GetDirectoryName(
            AuditCategoryFilingTests.FindRepoFile("ExchangeAdminWeb.Tests", "ExchangeAdminWeb.Tests.csproj"))!;
        var exempt = new HashSet<string>(StringComparer.Ordinal) { "TestConfigStore.cs", "SharedTempDatabaseTripwireTests.cs" };

        foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
        {
            var path = Path.GetRelativePath(project, file).Replace('\\', '/');
            if (path.StartsWith("bin/", StringComparison.Ordinal) || path.StartsWith("obj/", StringComparison.Ordinal)
                || exempt.Contains(path))
                continue;

            yield return (path, WithoutComments(File.ReadAllText(file)));
        }
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
