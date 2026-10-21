namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A7: Out of Office checked one mailbox and could write another.
/// </summary>
/// <remarks>
/// Source scans, not behaviour. They were the only guards when A7 shipped; aud-a7b added the
/// behavioural harness in <see cref="OutOfOfficeRequestCaptureTests"/>, which drives a real page
/// instance through the paths it can reach. These stay because they also cover the paths that
/// harness does not drive - the protected-principal refusal and the exception path.
///
/// Every assertion reads <see cref="ProgressScan.CodeView"/>, which blanks comments and string
/// literals. Reviewing aud-a3 showed raw-text tripwires passing against a call that had been
/// commented out, and the comments in this very method mention <c>setEmail</c> by name.
/// </remarks>
public class OutOfOfficeTargetCaptureTests
{
    private static string SetOofCode()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "OutOfOffice.razor"));

        var start = text.IndexOf("private async Task SetOof()", StringComparison.Ordinal);
        Assert.True(start >= 0, "SetOof not found - tripwire is stale.");

        var end = text.IndexOf("\n    private ", start + 1, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound SetOof - tripwire is stale.");

        return ProgressScan.CodeView(text[start..end]);
    }

    [Fact]
    public void SetOof_RefusesReentry_AndRaisesTheBusyFlag_BeforeAnyAwait()
    {
        // The window was: isSetting went up only AFTER the awaited protected-principal check,
        // so the mailbox box stayed enabled while resolution was in flight.
        var code = SetOofCode();

        var guard = code.IndexOf("if (isSetting) return;", StringComparison.Ordinal);
        Assert.True(guard >= 0, "SetOof must refuse re-entry.");

        var raise = code.IndexOf("isSetting = true;", guard, StringComparison.Ordinal);
        Assert.True(raise > guard, "The busy flag must be raised immediately after the guard.");

        var firstAwait = code.IndexOf("await ", StringComparison.Ordinal);
        Assert.True(firstAwait > raise,
            "The guard and the flag must both precede the first await, or the window is still open.");
    }

    [Fact]
    public void SetOof_LeavesNoWayOutBetweenRaisingTheBusyFlagAndTheTryThatClearsIt()
    {
        // Review of aud-a7. Raising isSetting before the awaited protected-principal check put
        // two new exits in front of the finally - the refusal's own return, and anything the
        // resolve threw - and either left the form disabled until the operator reloaded the
        // page. The schedule-validation return is the one exception: it clears the flag itself,
        // before any await, so it is matched and skipped here rather than silently tolerated.
        var code = SetOofCode();

        var raise = code.IndexOf("isSetting = true;", StringComparison.Ordinal);
        Assert.True(raise >= 0, "SetOof must raise the busy flag.");

        var tryStart = code.IndexOf("\n        try\n", raise, StringComparison.Ordinal);
        Assert.True(tryStart > raise, "SetOof must open a try after raising the busy flag.");

        var between = code[raise..tryStart];
        foreach (var (index, _) in Returns(between))
        {
            var preceding = between[..index];
            var lastClear = preceding.LastIndexOf("isSetting = false;", StringComparison.Ordinal);
            var lastOpenBrace = preceding.LastIndexOf('{');
            Assert.True(lastClear > lastOpenBrace,
                "A return between raising isSetting and the try must clear the flag first, or the form stays disabled.");
        }
    }

    private static IEnumerable<(int index, string text)> Returns(string code)
    {
        for (var i = code.IndexOf("return", StringComparison.Ordinal); i >= 0;
             i = code.IndexOf("return", i + 1, StringComparison.Ordinal))
        {
            yield return (i, "return");
        }
    }

    [Fact]
    public void SetOof_ClearsTheBusyFlagOnEveryPathOutOfTheTry()
    {
        var code = SetOofCode();
        var finallyAt = code.IndexOf("finally", StringComparison.Ordinal);
        Assert.True(finallyAt > 0, "SetOof must have a finally.");
        Assert.Contains("isSetting = false;", code[finallyAt..], StringComparison.Ordinal);
    }

    /// <summary>Every field the Set / Clear form binds, and so every one an edit can move.</summary>
    private static readonly string[] BoundFields =
        ["setEmail", "oofState", "internalMessage", "externalMessage", "startTime", "endTime", "ticketNumber"];

    [Fact]
    public void SetOof_CapturesEveryBoundInputOnce_BeforeTheFirstAwait_AndNeverRereadsOne()
    {
        // The guard closes the window; the capture means that even if something reopens it,
        // this run acts on what it validated. aud-a7 captured the mailbox; aud-a7b extends that to
        // every bound input, as one request. Re-reading any of them after the capture is the
        // defect itself. OutOfOfficeRequestCaptureTests proves the same as behaviour; this scan
        // also covers the paths that harness does not drive.
        var code = SetOofCode();

        var capture = code.IndexOf("OutOfOfficeRequest.FromForm(", StringComparison.Ordinal);
        Assert.True(capture >= 0, "SetOof must capture its inputs into one OutOfOfficeRequest.");
        var captureEnd = code.IndexOf(';', capture);
        Assert.True(code.IndexOf("await ", StringComparison.Ordinal) > captureEnd,
            "The capture must precede the first await.");

        var captured = code[capture..captureEnd];
        var after = code[(captureEnd + 1)..];
        foreach (var field in BoundFields)
        {
            Assert.Matches($@"\b{field}\b", captured);
            Assert.DoesNotMatch($@"\b{field}\b", after);
        }
    }
}
