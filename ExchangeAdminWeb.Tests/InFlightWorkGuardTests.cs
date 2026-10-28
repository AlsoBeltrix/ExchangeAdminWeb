using System.Reflection;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Components.Shared;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Progress;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Structural guards for the navigate-away warning (docs/GlobalProgressSystem-Plan.md S2).
///
/// Source scans, because nothing here can render a component or drive a browser dialog. They
/// check the properties whose absence is silent and dangerous rather than merely cosmetic.
/// </summary>
public class InFlightWorkGuardTests
{
    private static string Guard() => ReadRepoFile(Path.Combine("Components", "Shared", "InFlightWorkGuard.razor"));

    [Fact]
    public void ConfirmingTheWarningActuallyCancelsTheWork()
    {
        var source = StripComments(Guard());

        // The owner's requirement verbatim: "actually kill it if the user clicks okay". A
        // warning that says the work will be cancelled and then does not cancel it is worse
        // than no warning - the operator walks away believing it stopped.
        Assert.Contains("Progress.CancelAll()", source, StringComparison.Ordinal);

        // And Cancel must keep them on the page.
        Assert.Contains("context.PreventNavigation()", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BackgroundWorkIsNotPresentedAsSomethingThatWillBeKilled()
    {
        var source = StripComments(Guard());

        // The two cases have opposite advice. Telling an operator their background job will
        // die, or telling them a foreground operation is safe to leave, are both worse than
        // saying nothing - so there are two messages and they must stay distinct.
        Assert.Contains("ForegroundMessage", source, StringComparison.Ordinal);
        Assert.Contains("BackgroundMessage", source, StringComparison.Ordinal);

        // The background path must not reach the cancel. Checked by position: the only
        // CancelAll in the file sits inside the foreground branch, above the background one.
        var cancel = source.IndexOf("Progress.CancelAll()", StringComparison.Ordinal);
        var backgroundOnly = source.IndexOf("await Confirm(BackgroundMessage(", StringComparison.Ordinal);
        Assert.True(cancel >= 0 && backgroundOnly > cancel,
            "The background-only path must come after the foreground branch returns, so that "
            + "leaving a page with only background work cannot cancel anything.");
    }

    [Fact]
    public void TheBackgroundPromptDoesNotPromiseAStatusBarCompletionNotice()
    {
        var source = StripComments(Guard());

        // The frame is LIVE ONLY (owner ruling 2026-10-02, `.agents/decisions.md`): a job's
        // line disappears when it ends and the bar returns to Idle, saying nothing about
        // whether it succeeded. This prompt used to say "the status bar will tell you when it
        // is done", which sent the operator to watch a surface that no longer reports the
        // event - a worse outcome than sending them nowhere. Found by codex review of
        // ad3c1e9..e5f9e5c; the removal and the prompt shipped in the same range.
        Assert.DoesNotContain("tell you when it is done", source, StringComparison.Ordinal);
        Assert.DoesNotContain("status bar will tell", source, StringComparison.Ordinal);

        // Bulk Jobs is where a finished job's result actually lands, so the prompt must point
        // there rather than at the bar.
        Assert.Contains("Bulk Jobs page", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailedPromptNeitherNavigatesNorCancels()
    {
        var source = StripComments(Guard());

        // If the dialog cannot be shown - circuit going away, interop disposed - the safe
        // answer is to stay put. Silently killing an operator's in-flight work because a
        // dialog failed is the worst outcome available here, and a bare `return true` in that
        // catch would do exactly that.
        var catchIndex = source.IndexOf("catch (Exception)", StringComparison.Ordinal);
        Assert.True(catchIndex > 0, "The confirm helper must handle a failed prompt.");

        var tail = source[catchIndex..];
        var firstReturn = Regex.Match(tail, @"return\s+(true|false);");
        Assert.True(firstReturn.Success, "The catch must return a decision.");
        Assert.Equal("false", firstReturn.Groups[1].Value);
    }

    [Fact]
    public void TheGuardIsRenderedOnceByTheLayoutRatherThanPerPage()
    {
        var layout = StripComments(ReadRepoFile(Path.Combine("Components", "Layout", "MainLayout.razor")));
        Assert.Contains("<InFlightWorkGuard", layout, StringComparison.Ordinal);

        // A per-page guard would need adding to 36 pages and would silently miss any that
        // forgot - the same failure shape as the per-module spinners this replaces.
        var pages = Directory.GetFiles(Path.Combine(RepoRoot(), "Components", "Pages"), "*.razor")
            .Where(f => File.ReadAllText(f).Contains("<InFlightWorkGuard", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(pages.Count == 0,
            "The guard belongs in the layout only; these pages render their own copy, which "
            + "would prompt twice: " + string.Join(", ", pages));
    }

    [Fact]
    public void TheGuardUnsubscribesFromWhatItSubscribesTo()
    {
        var source = StripComments(Guard());

        // It lives in the layout, so a leaked handler persists for the life of the process.
        Assert.Contains("@implements IDisposable", source, StringComparison.Ordinal);
        Assert.Contains("Progress.Changed +=", source, StringComparison.Ordinal);
        Assert.Contains("Progress.Changed -=", source, StringComparison.Ordinal);

        // Same subscribe-after-dispose hole the status frame had: the subscription happens
        // after an await, so Dispose can run first and unsubscribe nothing.
        var await = source.IndexOf("await AuthStateProvider.GetAuthenticationStateAsync()", StringComparison.Ordinal);
        var guardCheck = source.IndexOf("if (disposed)", await < 0 ? 0 : await, StringComparison.Ordinal);
        var subscribe = source.IndexOf("Progress.Changed +=", StringComparison.Ordinal);
        Assert.True(await >= 0 && guardCheck > await && guardCheck < subscribe,
            "A disposed check must sit between awaiting authentication and subscribing.");
    }

    [Fact]
    public void AtLeastOneModuleActuallyHonoursTheCancellationTokenTheWarningPromises()
    {
        // The warning tells the operator their work will be cancelled. Cancelling sets a
        // token; work that never reads the token keeps running regardless, so for a module
        // that ignores it the popup is only half true - the UI stops waiting, the work does
        // not stop.
        //
        // This asserts the promise is kept SOMEWHERE rather than everywhere, because adoption
        // is incremental and most services do not accept a token yet. Its job is to stop the
        // last honouring call site being deleted and the promise quietly becoming false
        // everywhere at once.
        var honouring = Directory
            .GetFiles(Path.Combine(RepoRoot(), "Components", "Pages"), "*.razor")
            .Where(f => File.ReadAllText(f).Contains("activity.CancellationToken", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(honouring.Count > 0,
            "No page passes activity.CancellationToken into the work it reports. The "
            + "navigate-away warning says the operation will be cancelled, so at least one "
            + "module must actually honour it or the warning is a lie everywhere.");
    }

    /// <summary>
    /// kd-58, docs/CancellationDepth-Plan.md 3.8a (owner, 2026-10-28): for work that takes the operator's cancel, the leave
    /// prompt states the three cases in plain words - work still waiting its turn is cancelled; a command already sent to
    /// Exchange or the directory finishes and is recorded; a bulk job is not affected, because it never depended on the page.
    /// The old prompt said, on every page, only that leaving would cancel the work and that what it had changed would stay
    /// changed.
    /// </summary>
    internal const string LeavePromptsThreeCases =
        "Leaving this page will CANCEL it:\n"
        + "- Work still waiting its turn is cancelled.\n"
        + "- A command already sent to Exchange or the directory finishes and is recorded. Anything already changed stays changed.\n"
        + "- A bulk job is not affected, because it never depended on this page.\n\n"
        + "OK to leave and cancel, or Cancel to stay.";

    /// <summary>kd-58 3.8a (the supervisor's ruling, 2026-10-28): the prompt for work that does not take the cancel.</summary>
    internal const string LeavePromptsNoStop =
        "Leaving will not stop the operation in progress; it will complete and be recorded.\n\n"
        + "OK to leave, or Cancel to stay.";

    /// <summary>
    /// kd-58 3.8a. The activities whose work takes the operator's cancel, by page: the label each page's Progress.Begin call
    /// passes, as written, where that call declares it (<c>ActivitySize.TakingTheCancel()</c>). No page's activity is here
    /// until its work honours the activity's token that far - work still waiting its turn cancelled, a command already sent
    /// finishing - and every other activity's prompt says leaving will not stop it, which is true of work that takes no token.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string[]> TakesTheCancel = new Dictionary<string, string[]>
    {
        // kd-37, kd-58 A2 and A3: the lookup's Exchange Online read and its on-premises reads.
        ["RecipientLookup.razor"] = ["$\"Looking up {target}\""],
        // kd-81, kd-58 A2, A3 and A6/A7: the single change - its location read, its wait for a pool connection and its look before
        // its first write command; kd-59, kd-58 A6/A7: the on-premises change - every step before its first command is sent.
        ["MailboxPermissions.razor"] = ["$\"Updating permissions on {target}\"", "$\"Updating on-premises permissions on {onPremTarget}\""],
        ["CalendarPermissions.razor"] = ["$\"Updating calendar permissions on {target}\"", "$\"Updating on-premises calendar permissions on {onPremTarget}\""],
        // kd-6, kd-76: an unblock is never sent once the operator has cancelled; one already sent runs on and is recorded.
        ["BlockedSenders.razor"] = ["$\"Unblocking {address}\""],
    };

    public static TheoryData<string> PagesWithActivities => [.. PagesThatBeginActivities()];

    // kd-58 3.8a, per page: exactly the pinned activities declare that their work takes the operator's cancel - none, on a page
    // whose work does not take it - and a page that declares one hands its activity's token on. The mutations: a page's
    // declaration added or removed.
    [Theory]
    [MemberData(nameof(PagesWithActivities))]
    public void EachPagesActivities_DeclareWhetherTheirWorkTakesTheCancel_AsPinned(string page)
    {
        var source = StripComments(ReadRepoFile(Path.Combine("Components", "Pages", page)));
        var calls = BeginCalls(source);
        Assert.NotEmpty(calls);
        Assert.All(calls, arguments => Assert.Equal(3, arguments.Count)); // every call read whole: module, label, size
        var declared = calls
            .Where(arguments => arguments[2].Contains(".TakingTheCancel()", StringComparison.Ordinal))
            .Select(arguments => arguments[1])
            .ToArray();

        Assert.Equal(TakesTheCancel.GetValueOrDefault(page, []), declared);
        if (declared.Length > 0)
            Assert.Contains("ctivity.CancellationToken", source, StringComparison.Ordinal);
        Assert.DoesNotContain("NavigationLock", source, StringComparison.Ordinal); // the guard's prompt is the only one
        Assert.DoesNotContain("@layout", source, StringComparison.Ordinal);
    }

    // 3.8a: an activity whose work takes the cancel is told the three cases - with background jobs named after them.
    [Theory]
    [InlineData("Looking up alice@contoso.test")]
    [InlineData("Unblocking spam@fabrikam.test")]
    public void ThePrompt_ForWorkThatTakesTheCancel_StatesTheThreeCases(string label)
    {
        Assert.Equal($"{label} is still running.\n\n" + LeavePromptsThreeCases, ForegroundPrompt([Activity(label, takesTheCancel: true)], []));

        var withJobs = ForegroundPrompt([Activity(label, takesTheCancel: true)], [Job()]);
        Assert.StartsWith($"{label} is still running.\n\n" + LeavePromptsThreeCases, withJobs, StringComparison.Ordinal);
        Assert.EndsWith("(1 background job(s) will keep running and are not affected - see Bulk Jobs for those.)", withJobs, StringComparison.Ordinal);
    }

    // 3.8a: an activity whose work does not take the cancel is told plainly that leaving will not stop it - never the three
    // cases, which would promise a cancel its work does not honour.
    [Theory]
    [InlineData("Processing the permissions CSV")]
    [InlineData("Searching message trace")]
    public void ThePrompt_ForWorkThatDoesNotTakeTheCancel_SaysLeavingWillNotStopIt(string label)
    {
        var prompt = ForegroundPrompt([Activity(label, takesTheCancel: false)], []);

        Assert.Equal($"{label} is still running.\n\n" + LeavePromptsNoStop, prompt);
        Assert.DoesNotContain("CANCEL", prompt, StringComparison.Ordinal);
    }

    // 3.8a: both kinds at once - each named with what leaving does to it.
    [Fact]
    public void ThePrompt_ForBothKindsAtOnce_SaysWhichIsWhich()
    {
        var prompt = ForegroundPrompt(
            [Activity("Looking up alice@contoso.test", takesTheCancel: true), Activity("Loading blocked senders", takesTheCancel: false)], []);

        Assert.Equal(
            "2 operations are still running.\n\n"
            + "For Looking up alice@contoso.test, leaving this page will CANCEL the work:\n"
            + InFlightWorkGuard.CancelsWaitingWork + "\n\n"
            + "For Loading blocked senders: " + InFlightWorkGuard.WillNotStop + "\n\n"
            + "OK to leave, or Cancel to stay.",
            prompt);
    }

    // 3.8a: the declaration reaches the guard - the progress service hands it on in the activity the guard reads, the size the
    // activity declared kept beside it.
    [Fact]
    public void TheProgressService_HandsTheDeclarationToTheGuard()
    {
        using var progress = new ActivityProgressService();
        using var takes = progress.Begin("Recipient Lookup", "Looking up alice@contoso.test", ActivitySize.Unknown.TakingTheCancel());
        using var doesNot = progress.Begin("Migration", "Loading batches", ActivitySize.Steps(2));

        Assert.Equal([true, false], progress.Active.Select(activity => activity.TakesTheCancel));
        Assert.Equal([ActivityShape.Indeterminate, ActivityShape.Steps], progress.Active.Select(activity => activity.Shape));
        Assert.False(ActivitySize.Unknown.TakesTheCancel); // the shared default is never changed by a declaration
    }

    private static ProgressActivity Activity(string label, bool takesTheCancel) => new()
    {
        Id = "activity-" + label,
        ModuleId = "a module",
        Label = label,
        Shape = ActivityShape.Indeterminate,
        Total = 0,
        Done = 0,
        StartedUtc = DateTime.UtcNow,
        CancellationRequested = false,
        TakesTheCancel = takesTheCancel,
    };

    private static IEnumerable<string> PagesThatBeginActivities() =>
        Directory.GetFiles(Path.Combine(RepoRoot(), "Components", "Pages"), "*.razor")
            .Where(file => File.ReadAllText(file).Contains("Progress.Begin(", StringComparison.Ordinal))
            .Select(file => Path.GetFileName(file))
            .Order(StringComparer.Ordinal);

    // Every Progress.Begin call's arguments, split at the commas between them: string literals skipped whole, brackets of
    // every kind balanced, each argument trimmed.
    private static List<List<string>> BeginCalls(string source)
    {
        var calls = new List<List<string>>();
        for (var at = source.IndexOf("Progress.Begin(", StringComparison.Ordinal); at >= 0;
             at = source.IndexOf("Progress.Begin(", at + 1, StringComparison.Ordinal))
        {
            var arguments = new List<string>();
            var start = at + "Progress.Begin(".Length;
            var depth = 0;
            for (var i = start; i < source.Length; i++)
            {
                var c = source[i];
                if (c == '"')
                {
                    var verbatim = source[i - 1] == '@' || (source[i - 1] == '$' && source[i - 2] == '@');
                    for (i++; i < source.Length && source[i] != '"'; i++)
                    {
                        if (!verbatim && source[i] == '\\')
                            i++;
                    }
                    continue;
                }
                if (c == '\'')
                {
                    for (i++; i < source.Length && source[i] != '\''; i++)
                    {
                        if (source[i] == '\\')
                            i++;
                    }
                    continue;
                }
                if (c is '(' or '[' or '{')
                {
                    depth++;
                }
                else if (c is ')' or ']' or '}')
                {
                    if (depth-- == 0)
                    {
                        arguments.Add(source[start..i].Trim());
                        break;
                    }
                }
                else if (c == ',' && depth == 0)
                {
                    arguments.Add(source[start..i].Trim());
                    start = i + 1;
                }
            }
            calls.Add(arguments);
        }
        return calls;
    }

    private static BulkJob Job() => new()
    {
        Id = "job-1",
        ModuleId = "a module",
        JobType = "A_Job",
        SubmittedBy = @"CONTOSO\operator",
        SubmittedIp = "127.0.0.1",
        PayloadJson = "{}",
        SubmittedAtUtc = DateTime.UtcNow,
    };

    private static string ForegroundPrompt(IReadOnlyList<ProgressActivity> foreground, IReadOnlyList<BulkJob> background) =>
        (string)typeof(InFlightWorkGuard).GetMethod("ForegroundMessage", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [foreground, background])!;

    private static string ReadRepoFile(string relativePath) =>
        File.ReadAllText(Path.Combine(RepoRoot(), relativePath));

    private static string StripComments(string source)
    {
        var withoutRazor = Regex.Replace(source, @"@\*.*?\*@", " ", RegexOptions.Singleline);
        return Regex.Replace(withoutRazor, @"//[^\n]*", " ");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ExchangeAdminWeb.csproj")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
