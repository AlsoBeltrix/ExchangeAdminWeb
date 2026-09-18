using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Slice 1 of docs/ClickGatingAudit-Plan.md: the prerequisite stuck-flag fixes. A page-level busy
/// predicate widens a flag's blast radius from one dead button to the whole page, so any flag that
/// can be raised and not lowered has to be fixed BEFORE its page joins
/// <see cref="ClickGateRegistry.Pages"/>, not after.
/// </summary>
/// <remarks>
/// <para>
/// These are page-specific rather than registry-driven on purpose. Shared assertion 3 in
/// <see cref="ClickGateTests"/> qualifies a field as an in-flight flag only once it is lowered in a
/// <c>finally</c> - which is what slice 1 creates - so the two flags this file exists for are not
/// reliably visible to the detector that would otherwise cover them (Revision 1, falsification 8).
/// </para>
/// <para>
/// Honest limit, the same one the shared suite carries: there is no bUnit harness in this repo, so
/// nothing here renders a page. Every assertion is a source-level scan over comment-stripped text
/// and proves a shape, never a behaviour. The manual acceptance checks in the plan are what prove
/// the operator sees the fix.
/// </para>
/// </remarks>
public sealed class ClickGateStuckFlagTests
{
    // ---- BlockedSenders.ConfirmUnblock ---------------------------------------------------------
    //
    // isLoading is raised at the top of the handler and was lowered only by straight-line statements
    // on three normal-exit paths. The authorization round trip, the protected-principal gate and
    // BeginOperation all sat outside every try, so a throw from any of them left the method without
    // lowering it - and, with no ErrorBoundary anywhere in this app, took the circuit with it.
    //
    // The remedy is a catch, NOT a try/finally over the method. That distinction is the point of
    // ConfirmUnblock_LowersIsLoadingBeforeTheRefresh and ConfirmUnblock_HasNoFinally below, and it
    // is not a style preference: the obvious refactor is actively wrong here.

    [Fact]
    public void ConfirmUnblock_RaisesIsLoadingBeforeItsFirstAwait()
    {
        var body = ConfirmUnblock();

        var raise = body.IndexOf("isLoading = true;", StringComparison.Ordinal);
        Assert.True(raise >= 0, "ConfirmUnblock no longer raises isLoading");

        var firstAwait = FirstAwait(body);
        Assert.True(firstAwait >= 0, "ConfirmUnblock has no await; this guard is pointed at the wrong method");

        // Single-flight: a flag raised after the first await leaves a window in which a second
        // click sees an idle page. This is the shape shared assertion 4 generalises.
        Assert.True(raise < firstAwait,
            "ConfirmUnblock must raise isLoading before its first await, or a second click slips in");
    }

    [Fact]
    public void ConfirmUnblock_PreflightAwaitsAreInsideTheTry()
    {
        var body = ConfirmUnblock();
        var (tryStart, tryLength) = BlockAfter(body, "try", 0);
        Assert.True(tryStart >= 0, "ConfirmUnblock no longer opens a try around its preflight");

        var tryBlock = body.Substring(tryStart, tryLength);

        // Each of these can throw, and each used to sit outside every try.
        foreach (var call in new[]
                 {
                     "AuthStateProvider.GetAuthenticationStateAsync()",
                     "AuthorizationService.AuthorizeAsync(authState.User, \"BlockedSendersUnblock\")",
                     "ProtectionGate.EvaluateAsync(address, authState.User)",
                     "OperationTrace.BeginOperation(",
                 })
        {
            Assert.True(tryBlock.Contains(call, StringComparison.Ordinal),
                $"ConfirmUnblock: {call} is outside the preflight try, so a throw there escapes the handler");
        }
    }

    [Fact]
    public void ConfirmUnblock_OnlyTaskYieldAwaitsBeforeTheTry()
    {
        var body = ConfirmUnblock();
        var raise = body.IndexOf("isLoading = true;", StringComparison.Ordinal);
        var (tryStart, _) = BlockAfter(body, "try", 0);
        Assert.True(raise >= 0 && tryStart > raise, "ConfirmUnblock: expected a try after the isLoading raise");

        // await Task.Yield() between the raise and the try is the house idiom on every page here -
        // it lets the spinner render before the slow work starts, and it cannot throw. Any OTHER
        // await in that gap is unprotected, which is the defect this slice closes.
        foreach (Match match in Regex.Matches(body[raise..tryStart], @"await\s+[^\r\n;]*;"))
        {
            Assert.True(match.Value.Trim() == "await Task.Yield();",
                $"ConfirmUnblock: '{match.Value.Trim()}' runs before the preflight try, unprotected");
        }
    }

    [Fact]
    public void ConfirmUnblock_PreflightCatchFailsClosed()
    {
        var body = ConfirmUnblock();
        var (tryStart, tryLength) = BlockAfter(body, "try", 0);
        Assert.True(tryStart >= 0, "ConfirmUnblock no longer opens a try around its preflight");

        var (catchStart, catchLength) = BlockAfter(body, "catch", tryStart + tryLength);
        Assert.True(catchStart >= 0, "the preflight try has no catch, so a throw still escapes the handler");

        var catchBlock = body.Substring(catchStart, catchLength);

        Assert.True(catchBlock.Contains("PermissionResult.Fail", StringComparison.Ordinal),
            "the preflight catch must turn the throw into a visible failure, not swallow it");
        Assert.True(catchBlock.Contains("isLoading = false;", StringComparison.Ordinal),
            "the preflight catch must lower isLoading, or the page is left permanently busy");
        Assert.True(Regex.IsMatch(catchBlock, @"(?<![A-Za-z0-9_])return\s*;"),
            "the preflight catch must return: authorization was never confirmed, so the write must not proceed");

        // Fail closed. A catch that falls through to the write would turn an unreadable
        // authorization answer into an unauthorized Exchange mutation.
        Assert.False(catchBlock.Contains("UnblockSenderAsync", StringComparison.Ordinal),
            "the preflight catch must not reach the unblock write");
    }

    [Fact]
    public void ConfirmUnblock_LowersIsLoadingBeforeTheRefresh()
    {
        var body = ConfirmUnblock();

        var refresh = body.IndexOf("await LoadBlockedSenders();", StringComparison.Ordinal);
        Assert.True(refresh >= 0, "ConfirmUnblock no longer refreshes the list after a successful unblock");

        var lastClear = body.LastIndexOf("isLoading = false;", StringComparison.Ordinal);
        Assert.True(lastClear >= 0, "ConfirmUnblock no longer lowers isLoading");

        // The ordering is load-bearing and invisible. LoadBlockedSenders raises isLoading itself,
        // and once it also consults a page-level busy predicate (the sweep this slice unblocks) a
        // still-true isLoading makes the post-unblock refresh a silent no-op - the operator is told
        // the unblock succeeded while the sender stays listed. Wrapping the method in try/finally
        // moves the lowering after this call and produces exactly that.
        Assert.True(lastClear < refresh,
            "isLoading must be lowered BEFORE LoadBlockedSenders, or the confirming refresh can no-op");
    }

    [Fact]
    public void ConfirmUnblock_HasNoFinally()
    {
        var body = ConfirmUnblock();

        // Deliberately the negative. A finally reads as the safer shape and is the first thing a
        // later reader will reach for, but here it relocates the lowering past the refresh above
        // and cannot rescue the circuit it would be written for - there is no ErrorBoundary in this
        // app, so the throw kills the page whether or not a flag was lowered on the way out.
        Assert.False(Regex.IsMatch(body, @"(?<![A-Za-z0-9_])finally(?![A-Za-z0-9_])"),
            "ConfirmUnblock must not use a finally: it would move the isLoading clear past "
            + "LoadBlockedSenders. Use a catch, as the existing preflight and write catches do.");
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static string ConfirmUnblock() =>
        ClickGateSource.Load("BlockedSenders.razor").MethodBody("ConfirmUnblock");

    /// <summary>Offset of the first <c>await</c> statement, or -1.</summary>
    private static int FirstAwait(string source)
    {
        var match = Regex.Match(source, @"(?<![A-Za-z0-9_])await(?![A-Za-z0-9_])");
        return match.Success ? match.Index : -1;
    }

    /// <summary>
    /// The brace-balanced block introduced by the next whole-word <paramref name="keyword"/> at or
    /// after <paramref name="from"/>, as (offset of the opening brace, length). Returns (-1, 0)
    /// when the keyword is absent, leaving the caller to decide whether that is the failure.
    /// </summary>
    /// <remarks>
    /// Whole-word anchored so "try" does not match inside an identifier, and brace-balanced rather
    /// than regex-terminated because every one of these blocks contains nested blocks - the audit
    /// idiom wraps each audit write in its own try/catch.
    /// </remarks>
    private static (int Start, int Length) BlockAfter(string source, string keyword, int from)
    {
        var match = Regex.Match(source[from..],
            $@"(?<![A-Za-z0-9_]){Regex.Escape(keyword)}\s*(?:\([^)]*\))?\s*\{{");
        if (!match.Success)
            return (-1, 0);

        var open = from + match.Index + match.Length - 1;
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return (open, i - open + 1);
            }
        }

        return (-1, 0);
    }
}
