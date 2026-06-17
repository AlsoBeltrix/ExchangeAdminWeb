using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Source-level guard for the cloud Add/Remove mailbox-permission methods. Each grants
/// FullAccess and SendAs sequentially; if the second right fails after the first was
/// applied, the result must report partial success (so the bulk audit row and CSV report
/// record what actually landed) rather than a single blanket FAILED.
///
/// These methods run real Exchange Online cmdlets over a pooled runspace, so they cannot
/// be unit-hosted; this guard parses the service source (same approach as
/// PageAuthorizationRecheckTests and ADAttributeEditorServiceTests.PerformSave_*) and
/// fails if a method reverts to the old single-Fail shape that lost partial state.
/// </summary>
public class MailboxPermissionServicePartialSuccessTests
{
    [Theory]
    [InlineData("AddMailboxPermissionsAsync", "Partial: granted")]
    [InlineData("RemoveMailboxPermissionsAsync", "Partial: removed")]
    public void CloudMethod_AggregatesPerRight_AndReportsPartial(string method, string partialMarker)
    {
        var body = GetMethodBody("MailboxPermissionService.cs", method);

        // Per-right aggregation: each right tracked independently.
        Assert.Contains("successes.Add(\"FullAccess\")", body);
        Assert.Contains("successes.Add(\"SendAs\")", body);
        Assert.Contains("failures.Add($\"FullAccess:", body);
        Assert.Contains("failures.Add($\"SendAs:", body);

        // Mixed outcome surfaces as a partial result, not a blanket failure.
        Assert.Contains(partialMarker, body);
        Assert.Contains("failures.Count > 0 && successes.Count > 0", body);

        // Must NOT be the old single-shot shape that converted any throw into one Fail.
        // RunAsync wrapped the whole operation in one try; partial state was impossible.
        Assert.DoesNotContain("return RunAsync(", body);
    }

    private static string GetMethodBody(string fileName, string methodName)
    {
        var source = ReadServiceSource(fileName);

        var signature = Regex.Match(source,
            $@"public\s+Task<PermissionResult>\s+{Regex.Escape(methodName)}\s*\(");
        Assert.True(signature.Success, $"{fileName}: method '{methodName}' not found");

        // Body = from the signature to the next public method (or end of file). Coarse
        // but sufficient to detect a reverted aggregation shape.
        var start = signature.Index;
        var next = Regex.Match(source[(start + signature.Length)..],
            @"\n    public\s+");
        return next.Success
            ? source.Substring(start, signature.Length + next.Index)
            : source[start..];
    }

    private static string ReadServiceSource(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var path = Path.Combine(dir.FullName, "Services", fileName);
            if (File.Exists(path))
                return File.ReadAllText(path);
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate Services/{fileName} from test base directory.");
    }
}
