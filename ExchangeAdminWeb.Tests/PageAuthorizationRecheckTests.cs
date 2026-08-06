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

    [Fact]
    public void ModuleConfig_SaveAllowlist_RechecksCorruptionBeforeWrite()
    {
        // The corrupt-config fail-closed rule: a corrupt ad-editable-attributes.json must
        // not be overwritten from the UI. The disabled buttons are UI only; the authoritative
        // gate is a re-check immediately before AttrEditorService.SaveAllowlist. This guard
        // fails if that recheck (IsAllowlistCorrupt() -> abort) is removed, or if the
        // recheck no longer precedes the save call. The gate must call IsAllowlistCorrupt
        // (disk-fresh), not GetAllowlist (cached): see
        // ADAttributeEditorServiceTests.IsAllowlistCorrupt_* for the behavioral proof that
        // the disk-fresh check catches corruption a cached GetAllowlist would miss.
        var body = GetMethodBody("ModuleConfig.razor", "SaveAllowlistAsync");

        var recheck = body.IndexOf("AttrEditorService.IsAllowlistCorrupt()", StringComparison.Ordinal);
        Assert.True(recheck >= 0, "SaveAllowlistAsync no longer rechecks corruption before saving");

        var save = body.IndexOf("AttrEditorService.SaveAllowlist(", StringComparison.Ordinal);
        Assert.True(save >= 0, "SaveAllowlist call not found");
        Assert.True(recheck < save, "corruption recheck must precede the SaveAllowlist write");
    }

    // ---- Protected-principal target gate ---------------------------------------------------
    //
    // Distinct from the authorization re-check above, and the distinction is the whole point:
    // re-authorization asks "may this OPERATOR act", the protection gate asks "may this TARGET be
    // acted upon". Blocked Senders had the first and not the second - it re-checked the operator,
    // audited the outcome, and mutated a protected principal's mail-flow state without ever
    // looking at the target (docs/ProtectedPrincipalGapFix-Plan.md GAP A).

    [Fact]
    public void BlockedSenders_ConfirmUnblock_ChecksTheTargetBeforeTheWrite()
    {
        var body = GetMethodBody("BlockedSenders.razor", "ConfirmUnblock");

        var gate = body.IndexOf("Validator.ValidateTargetMailboxAsync(", StringComparison.Ordinal);
        Assert.True(gate >= 0,
            "ConfirmUnblock no longer gates the target through the protected-principal check");

        var write = body.IndexOf("BlockedSenderSvc.UnblockSenderAsync(", StringComparison.Ordinal);
        Assert.True(write >= 0, "UnblockSenderAsync call not found");

        // Ordering is the guarantee, not mere presence: a check after the write protects nothing.
        Assert.True(gate < write,
            "the protected-principal gate must precede the unblock write");
    }

    [Fact]
    public void BlockedSenders_ConfirmUnblock_RefusesRatherThanContinuing()
    {
        // Fail-closed: a non-null result from the validator is a denial, and the handler must
        // return. Falling through to the write with a denial message displayed would be worse
        // than no gate at all, because the audit would then record a denial beside a completed
        // mutation.
        var body = GetMethodBody("BlockedSenders.razor", "ConfirmUnblock");

        var gate = body.IndexOf("var protectedError = await Validator.ValidateTargetMailboxAsync(", StringComparison.Ordinal);
        Assert.True(gate >= 0, "the protected-principal gate is not in its expected form");

        var afterGate = body[gate..];
        var branch = afterGate.IndexOf("if (protectedError is not null)", StringComparison.Ordinal);
        Assert.True(branch >= 0, "the gate result is not branched on");

        var returnStatement = afterGate.IndexOf("return;", StringComparison.Ordinal);
        var write = afterGate.IndexOf("BlockedSenderSvc.UnblockSenderAsync(", StringComparison.Ordinal);
        Assert.True(returnStatement >= 0 && returnStatement < write,
            "a denied unblock must return before reaching the write");
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
