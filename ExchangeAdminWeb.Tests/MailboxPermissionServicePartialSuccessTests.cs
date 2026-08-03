using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Structural guard that the four mailbox-permission methods still ROUTE their per-right results
/// through <see cref="ExchangeAdminWeb.Services.MailboxPermissionOutcome"/>, which is where the
/// partial-success rule now lives and is properly tested
/// (<see cref="MailboxPermissionOutcomeTests"/>).
/// </summary>
/// <remarks>
/// This file used to assert the aggregation itself by grepping the service source for
/// <c>successes.Add("FullAccess")</c> and friends. That was a workaround for the methods being
/// untestable - they run real Exchange cmdlets over a pooled runspace - and it had the defect that
/// workaround has: it tested the SHAPE OF THE TEXT, not the behavior. It broke when the variables
/// were renamed during the seam extraction, while the behavior was unchanged; it would equally
/// have passed if the aggregation had been correct-looking and wrong.
///
/// Now that the decision logic is extracted, the rule is tested properly on real inputs and this
/// file is reduced to the one thing source inspection is legitimately good for: proving the
/// untestable code still DELEGATES to the tested code. If a future edit re-inlines the
/// aggregation, the behavior tests would keep passing against an unused helper - this catches
/// that, and only that.
/// </remarks>
public class MailboxPermissionServicePartialSuccessTests
{
    [Theory]
    [InlineData("AddMailboxPermissionsAsync", "ForGrant")]
    [InlineData("RemoveMailboxPermissionsAsync", "ForRevoke")]
    [InlineData("AddMailboxPermissionsOnPremAsync", "ForGrant")]
    [InlineData("RemoveMailboxPermissionsOnPremAsync", "ForRevoke")]
    public void Method_DelegatesToTheTestedAggregator(string method, string composer)
    {
        var body = GetMethodBody("MailboxPermissionService.cs", method);

        Assert.Contains($"MailboxPermissionOutcome.{composer}(", body);

        // Each right is attempted through a helper that captures a throw instead of propagating
        // it, so a later right is still attempted and a partial result remains possible.
        Assert.Contains("TryRight", body);

        // The old single-shot shape wrapped the whole operation in one try, making partial state
        // impossible to report.
        Assert.DoesNotContain("return RunAsync(", body);
    }

    [Fact]
    public void NoMethodReimplementsTheAggregationInline()
    {
        // The failure this guards: someone re-inlines the logic, MailboxPermissionOutcome keeps
        // passing its own tests, and the service quietly stops using it.
        var source = ReadServiceSource("MailboxPermissionService.cs");

        Assert.DoesNotContain("Partial: granted", source);
        Assert.DoesNotContain("Partial: removed", source);
    }

    private static string GetMethodBody(string fileName, string methodName)
    {
        var source = ReadServiceSource(fileName);

        var signature = Regex.Match(source,
            $@"public\s+(async\s+)?Task<PermissionResult>\s+{Regex.Escape(methodName)}\s*\(");
        Assert.True(signature.Success, $"{fileName}: method '{methodName}' not found");

        // Body = from the signature to the next public method (or end of file). Coarse
        // but sufficient to detect a lost delegation.
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
