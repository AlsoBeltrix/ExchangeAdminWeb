using System.Text.RegularExpressions;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A23: a partial cloud permission change notified the affected user of nothing,
/// because the page gated the notice on overall success and a partial result is not a success.
/// </summary>
/// <remarks>
/// The aggregation half is driven for real - <see cref="MailboxPermissionOutcome"/> is the seam
/// that exists precisely so these decisions are provable without a tenant. That the PAGE reads
/// the new field instead of <c>opResult.Success</c> is a source scan, because the surrounding
/// handler needs a live Exchange connection to reach.
/// </remarks>
public class MailboxPartialNotificationTests
{
    private const string Target = "ceo@analog.com";
    private const string User = "jdoe@analog.com";

    [Fact]
    public void APartialGrant_NamesTheRightThatLanded()
    {
        // The finding. FullAccess really was granted; the user can open the mailbox and nobody
        // told them.
        var result = MailboxPermissionOutcome.ForGrant(Target, User, [
            RightOutcome.Ok("FullAccess"),
            RightOutcome.Failed("SendAs", "insufficient rights")
        ]);

        Assert.False(result.Success);
        Assert.Equal(["FullAccess"], result.AppliedRights);
    }

    [Fact]
    public void APartialRevoke_NamesTheRightThatWasRemoved()
    {
        var result = MailboxPermissionOutcome.ForRevoke(Target, User, [
            RightOutcome.Failed("FullAccess", "boom"),
            RightOutcome.Ok("SendAs")
        ]);

        Assert.False(result.Success);
        Assert.Equal(["SendAs"], result.AppliedRights);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFullGrant_NamesEveryRight(bool onPrem)
    {
        var result = MailboxPermissionOutcome.ForGrant(Target, User, [
            RightOutcome.Ok("FullAccess"),
            RightOutcome.Ok("SendAs")
        ], onPrem);

        Assert.True(result.Success);
        Assert.Equal(["FullAccess", "SendAs"], result.AppliedRights);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AFullRevoke_NamesEveryRight(bool onPrem)
    {
        var result = MailboxPermissionOutcome.ForRevoke(Target, User, [
            RightOutcome.Ok("FullAccess"),
            RightOutcome.Ok("SendAs")
        ], onPrem);

        Assert.True(result.Success);
        Assert.Equal(["FullAccess", "SendAs"], result.AppliedRights);
    }

    [Fact]
    public void ATotalFailure_AppliesNothing()
    {
        // Fail closed: nothing landed, so nobody is told their access changed.
        var result = MailboxPermissionOutcome.ForGrant(Target, User, [
            RightOutcome.Failed("FullAccess", "denied"),
            RightOutcome.Failed("SendAs", "denied")
        ]);

        Assert.False(result.Success);
        Assert.True(result.AppliedRights is null || result.AppliedRights.Count == 0);
    }

    [Fact]
    public void ASuccessfulCloudGrant_KeepsTheLinkOutOfTheAppliedRights()
    {
        // Why this is a field and not a parse of Detail. On the cloud success path Detail holds
        // the OWA link; reading rights out of it would put a URL in the permission-type line of
        // the user's mail.
        var result = MailboxPermissionOutcome.ForGrant(Target, User, [RightOutcome.Ok("FullAccess")]);

        Assert.Contains("outlook.office.com", result.Detail);
        Assert.Equal(["FullAccess"], result.AppliedRights);
        Assert.DoesNotContain(result.AppliedRights!, r => r.Contains("http", StringComparison.Ordinal));
    }

    [Fact]
    public void AServicedNote_DoesNotDropTheAppliedRights()
    {
        // WithServicedNote rebuilds the record field by field, so a new field is exactly the
        // kind of thing it silently loses.
        var result = MailboxPermissionOutcome
            .ForGrant(Target, User, [RightOutcome.Ok("FullAccess"), RightOutcome.Failed("SendAs", "boom")])
            .WithServicedNote("authorised by ExchangeServicers");

        Assert.Equal(["FullAccess"], result.AppliedRights);
        Assert.Equal("authorised by ExchangeServicers", result.ServicedNote);
    }

    [Fact]
    public void ThePageNotifiesOnWhatApplied_NotOnOverallSuccess()
    {
        // The wiring. A fix that populated the field and left the gate alone would pass every
        // test above and change nothing for the user it is for.
        var body = AuditCategoryFilingTests.MethodBody(
            File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "MailboxPermissions.razor")),
            "private async Task SubmitSingle");

        Assert.Contains("opResult.AppliedRights", body, StringComparison.Ordinal);
        Assert.Contains("if (applied.Count > 0)", body, StringComparison.Ordinal);

        // The notice must carry what landed, not the requested set.
        Assert.Contains("SendUserNotificationAsync(user, target, currentUser, notifyPerm", body, StringComparison.Ordinal);
        Assert.Contains("SendOwnerNotificationAsync(target, user, currentUser, notifyPerm", body, StringComparison.Ordinal);

        // Review of the A23 batch: this passed if the whole applied-rights block were
        // nested back under `if (opResult.Success)`, which re-breaks the partial case while
        // every string above is still present. Nothing between taking the applied set and
        // sending may test overall success.
        var from = body.IndexOf("var applied = opResult.AppliedRights", StringComparison.Ordinal);
        var to = body.IndexOf("SendOwnerNotificationAsync", StringComparison.Ordinal);
        Assert.True(from > 0 && to > from, "the applied-rights block must still be there");

        Assert.DoesNotContain("opResult.Success", body[from..to], StringComparison.Ordinal);
    }

    [Fact]
    public void TheOnPremPathReadsAppliedRights_NeverDetail()
    {
        // aud-a23b. Detail is overloaded - the applied rights on a partial, the OWA link on a
        // successful cloud grant - and this handler split it into rights, correct only while it
        // never sees a cloud result. AppliedRights says the same thing on every path. A scan, for
        // aud-a23's reason: the handler around it needs a live Exchange connection.
        var body = AuditCategoryFilingTests.MethodBody(
            File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "MailboxPermissions.razor")),
            "private async Task ExecuteOnPrem");

        Assert.Contains("var applied = opResult.AppliedRights ?? [];", body, StringComparison.Ordinal);

        // Every rights decision reads the applied set: both per-right audit rows, the _PARTIAL
        // marker on the admin notice, the notification gate, and the rights the notice names.
        Assert.Contains("opResult.Success || applied.Contains(\"FullAccess\")", body, StringComparison.Ordinal);
        Assert.Contains("opResult.Success || applied.Contains(\"SendAs\")", body, StringComparison.Ordinal);
        Assert.Contains("var isPartial = !opResult.Success && applied.Count > 0;", body, StringComparison.Ordinal);
        Assert.Contains("if (opResult.Success || applied.Count > 0)", body, StringComparison.Ordinal);
        Assert.Contains("string.Join(\"+\", applied.Count > 0 ? applied : new[] { permType })", body, StringComparison.Ordinal);

        // And the handler's CODE never touches Detail at all. Comments and literals are blanked,
        // so a remark explaining why does not count, and a parse re-spelled so that it no longer
        // matches the strings above still cannot hide.
        Assert.DoesNotMatch(@"\.\s*Detail\b", RazorSyntax.CodeView(body));
    }

    [Fact]
    public void NoProductionCodeParsesADetailMember()
    {
        // aud-a23b, the convention as a tripwire. PermissionResult.Detail is display text whose
        // meaning depends on the path that built it, so nothing may read rights - or anything
        // else - out of it. Text-based over every production .cs and .razor, comments blanked.
        // It refuses a .Detail of ANY type, which is broader than PermissionResult on purpose: no
        // Detail in this app is parsed today, and a new one that genuinely must be should be
        // argued for here, by name, rather than slip in.
        var root = Path.GetDirectoryName(AuditCategoryFilingTests.FindRepoFile("ExchangeAdminWeb.csproj"))!;
        var files = ProductionSourceFiles(root).ToList();

        // Not vacuous: the scan reaches the page this exists for, and the patterns bite on the
        // shapes they name and on nothing the app legitimately does with a Detail today.
        Assert.Contains(files, f => f.EndsWith(Path.Combine("Components", "Pages", "MailboxPermissions.razor"), StringComparison.OrdinalIgnoreCase));
        Assert.Contains(files, f => f.EndsWith(Path.Combine("Services", "MailboxPermissionOutcome.cs"), StringComparison.OrdinalIgnoreCase));
        foreach (var parse in DetailParseSamples)
            Assert.True(DetailParses.Any(p => p.IsMatch(parse)), $"the tripwire must refuse: {parse}");
        foreach (var read in DetailReadSamples)
            Assert.False(DetailParses.Any(p => p.IsMatch(read)), $"the tripwire must allow: {read}");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var raw = File.ReadAllText(file);
            var view = RazorSyntax.CommentsBlanked(raw);
            foreach (var pattern in DetailParses)
            {
                foreach (Match match in pattern.Matches(view))
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{RazorSyntax.LineOf(raw, match.Index)}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Detail is display text whose meaning differs by path (the applied rights on a partial, the OWA "
            + "link on a cloud grant); read PermissionResult.AppliedRights for rights. Parsed here: "
            + string.Join(", ", offenders));
    }

    private const string TextMethods =
        "Split|Contains|IndexOf|IndexOfAny|LastIndexOf|LastIndexOfAny|StartsWith|EndsWith|Substring|AsSpan"
        + "|Replace|Trim|TrimStart|TrimEnd|ToLower|ToLowerInvariant|ToUpper|ToUpperInvariant|Equals|CompareTo";

    private static readonly Regex[] DetailParses =
    [
        // x.Detail.Split(...), x.Detail?.Contains(...), x.Detail!.IndexOf(...)
        new($@"\.\s*Detail\b\s*[!?]?\s*\.\s*(?:{TextMethods})\s*\(", RegexOptions.CultureInvariant),
        // (x.Detail ?? "").Split(...), (x.Detail ?? string.Empty).Contains(...)
        new($@"\.\s*Detail\b\s*\?\?\s*(?:""""|[Ss]tring\s*\.\s*Empty)\s*\)\s*[!?]?\s*\.\s*(?:{TextMethods})\s*\(", RegexOptions.CultureInvariant),
        // Regex.Split(x.Detail, ...), string.Equals(x.Detail, ...)
        new(@"\b(?:Regex|[Ss]tring)\s*\.\s*(?:Split|Match|Matches|IsMatch|Replace|Count|EnumerateMatches|Equals|Compare|CompareOrdinal)\s*\(\s*[\w.?!()]*\.\s*Detail\b", RegexOptions.CultureInvariant),
        // x.Detail == "FullAccess", "SendAs" != x.Detail
        new(@"\.\s*Detail\b\s*[!=]=\s*@?""|""\s*[!=]=\s*[\w.?!()]*\.\s*Detail\b", RegexOptions.CultureInvariant),
    ];

    private static readonly string[] DetailParseSamples =
    [
        "var succeeded = opResult.Detail?.Split(',', StringSplitOptions.TrimEntries) ?? Array.Empty<string>();",
        "var fa = opResult.Detail.Contains(\"FullAccess\");",
        "var fa = opResult.Detail?.ToLowerInvariant().Contains(\"fullaccess\");",
        "var rights = (opResult.Detail ?? \"\").Split(',');",
        "var sa = (opResult.Detail ?? string.Empty).Contains(\"SendAs\");",
        "var rights = Regex.Split(opResult.Detail!, \",\");",
        "var fa = string.Equals(opResult.Detail, \"FullAccess\", StringComparison.Ordinal);",
        "var fa = opResult.Detail == \"FullAccess\";",
        "var sa = \"SendAs\" == opResult.Detail;",
    ];

    private static readonly string[] DetailReadSamples =
    [
        "<div class=\"mt-1\"><small>@result.Detail</small></div>",
        "@if (!string.IsNullOrEmpty(result.Detail))",
        "stepDetails[step.Step] = step.Status + (step.Detail != null ? $\" - {step.Detail}\" : \"\");",
        "Detail = result.Detail,",
        "return !string.IsNullOrWhiteSpace(last.Detail) ? last.Detail : last.Action;",
    ];

    private static IEnumerable<string> ProductionSourceFiles(string root)
    {
        static bool IsSource(string path) =>
            path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly).Where(IsSource))
            yield return file;

        foreach (var dir in new[] { "Authorization", "Components", "Middleware", "Models", "Modules", "Services" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(root, dir), "*", SearchOption.AllDirectories).Where(IsSource))
                yield return file;
        }
    }
}
