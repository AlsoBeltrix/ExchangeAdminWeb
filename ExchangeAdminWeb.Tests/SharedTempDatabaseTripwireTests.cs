using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a26q: the permission validator's tests built their protected-principal, enablement, section-access and app-settings
/// stores in the temp directory itself - one <c>exchangeadmin.db</c> shared by every validator, every class and every run -
/// so a row any run left there changed what the next one's tests meant. aud-a26t: their audit log root was one directory
/// under it, shared the same way. aud-a74: five classes cleared every SQLite connection pool in the process, the pools of
/// the classes running beside them included.
/// </summary>
/// <remarks>
/// Two halves. The one helper every test store is built through refuses the temp directory itself, whatever expression
/// names it; and no test source names that directory except as the first segment of a <c>Path.Combine</c> - a directory
/// of its own under it - nor puts the config database directly in it, nor sets <c>Audit:LogRoot</c> from it.
/// <c>TestConfigStore.cs</c> and this class name it on purpose and are not scanned for it. Beside those, no test source
/// names <c>ClearAllPools</c>; that scan skips only this class, whose pattern names it.
/// </remarks>
public sealed class SharedTempDatabaseTripwireTests
{
    /// <summary>The two files that name the temp directory on purpose, not scanned for it.</summary>
    private static readonly string[] NameTheTempDirectory = ["TestConfigStore.cs", "SharedTempDatabaseTripwireTests.cs"];

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
        foreach (var (path, code) in ScannedSources(NameTheTempDirectory))
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

        var hits = ScannedSources(NameTheTempDirectory)
            .SelectMany(source => logRootFromTemp.Matches(source.Code)
                .Select(m => $"{source.Path}:{RazorSyntax.LineOf(source.Code, m.Index)} sets Audit:LogRoot from the temp directory"))
            .ToList();

        Assert.True(hits.Count == 0,
            "A test's audit logs go to a directory every test shares; put them under the test's own directory: " + string.Join("; ", hits));
    }

    [Fact]
    public void NoTestSource_ClearsEveryConnectionPoolInTheProcess()
    {
        // aud-a74. SqliteConnection.ClearAllPools clears every pool in the process, and the test classes run in parallel: one
        // class's call reached a connection another class was opening, whose PRAGMA then failed with ObjectDisposedException.
        // A test clears the pool of each database it made - ClearPool over a connection to that file, the
        // AttributeEditorHarness shape. Any mention in code counts: a call, a call through using static, a method group, a
        // name handed to reflection. Only this class, whose pattern names it, is not scanned.
        var clearsEveryPool = new Regex(@"\bClearAllPools\b");

        var hits = ScannedSources("SharedTempDatabaseTripwireTests.cs")
            .SelectMany(source => clearsEveryPool.Matches(source.Code)
                .Select(m => $"{source.Path}:{RazorSyntax.LineOf(source.Code, m.Index)} clears every connection pool in the process"))
            .ToList();

        Assert.True(hits.Count == 0,
            "A test clears the connection pools of the classes running beside it; clear only the pools of its own databases: "
            + string.Join("; ", hits));
    }

    /// <summary>Every test source, comments blanked, but the files named in <paramref name="exempt"/>.</summary>
    private static IEnumerable<(string Path, string Code)> ScannedSources(params string[] exempt)
    {
        var project = Path.GetDirectoryName(
            AuditCategoryFilingTests.FindRepoFile("ExchangeAdminWeb.Tests", "ExchangeAdminWeb.Tests.csproj"))!;
        var skipped = new HashSet<string>(exempt, StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories))
        {
            var path = Path.GetRelativePath(project, file).Replace('\\', '/');
            if (path.StartsWith("bin/", StringComparison.Ordinal) || path.StartsWith("obj/", StringComparison.Ordinal)
                || skipped.Contains(path))
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
