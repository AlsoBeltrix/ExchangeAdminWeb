using System.Text.RegularExpressions;

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
