using System.Collections;
using System.Collections.Concurrent;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using ExchangeAdminWeb.Components.Shared;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

#pragma warning disable BL0006 // a test renderer, so the component attaches without a circuit; no frames are read
/// <summary>
/// Finding kd-91, the autocomplete half: a newer keystroke or search supersedes the search in flight, so an older text's
/// results can never land after a newer one's. On dev, 2026-10-30, overlapping keystrokes in the two Mailbox Permissions
/// boxes queued at the connect gate one behind another, and whichever search finished last set the list - an older
/// keystroke's results could replace a newer one's.
/// </summary>
/// <remarks>
/// The real component, attached to a test renderer (no circuit, no frames read) and driven through its own handlers on the
/// renderer's dispatcher; the real pool over <see cref="ExchangeWriteHarness"/>'s settings, its connect seam handing over
/// opened stand-in runspaces whose <c>Get-Recipient</c> says it has started for its text and then waits on the test before
/// answering one row for it. Every wait is bounded. No Exchange.
/// </remarks>
public sealed class RecipientAutocompleteSupersededSearchTests : IDisposable
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    private readonly ExchangeWriteHarness _h = new();
    private readonly ConcurrentDictionary<string, ManualResetEventSlim> _release = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _started = new(StringComparer.Ordinal);
    private readonly List<ExoConnectionPool> _pools = [];
    private readonly List<Renderer> _renderers = [];
    private int _connects;

    public void Dispose()
    {
        foreach (var gate in _release.Values)
            gate.Set();
        foreach (var renderer in _renderers)
            renderer.Dispose();
        foreach (var pool in _pools)
            pool.Dispose();
        _h.Dispose();
        foreach (var gate in _release.Values)
            gate.Dispose();
    }

    // Two searches, the older finishing last: the newer one's list stays.
    [Fact]
    public async Task AnOlderSearchThatFinishesLast_NeverReplacesTheNewerOnesResults()
    {
        var (renderer, box, pool) = Mount();
        var older = renderer.Dispatcher.InvokeAsync(() => Search(box, "alpha"));
        await Started("alpha", 1);
        var newer = renderer.Dispatcher.InvokeAsync(() => Search(box, "alphabet"));
        await Started("alphabet", 1);

        Release("alphabet");
        await newer.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.Equal(["Result for alphabet"], await Shown(renderer, box));

        Release("alpha");
        await older.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Equal(["Result for alphabet"], await Shown(renderer, box));
        Assert.True(await Read<bool>(renderer, box, "_showDropdown"));
        Assert.False(await Read<bool>(renderer, box, "_isSearching"));
        Assert.Equal("alphabet", await Read<string>(renderer, box, "_lastSearchTerm"));
        Assert.Equal(2, pool.AvailableCount); // the superseded search's connection came back too
        Assert.Equal(5, pool.FreeSlots);
    }

    // The older search finishing first: nothing lands from it, and the newer search's spinner stays on until it lands.
    [Fact]
    public async Task ASupersededSearchThatFinishesFirst_LandsNothing_AndLeavesTheNewerSearchRunning()
    {
        var (renderer, box, _) = Mount();
        var older = renderer.Dispatcher.InvokeAsync(() => Search(box, "alpha"));
        await Started("alpha", 1);
        var newer = renderer.Dispatcher.InvokeAsync(() => Search(box, "alphabet"));
        await Started("alphabet", 1);

        Release("alpha");
        await older.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.Empty(await Shown(renderer, box));
        Assert.True(await Read<bool>(renderer, box, "_isSearching"));

        Release("alphabet");
        await newer.WaitAsync(Bound, TestContext.Current.CancellationToken);
        Assert.Equal(["Result for alphabet"], await Shown(renderer, box));
        Assert.False(await Read<bool>(renderer, box, "_isSearching"));
    }

    // A keystroke that shortens the text below three characters clears the list; the search it superseded lands nothing.
    [Fact]
    public async Task AKeystrokeThatClearsTheList_SupersedesTheSearchInFlight()
    {
        var (renderer, box, pool) = Mount();
        var search = renderer.Dispatcher.InvokeAsync(() => Search(box, "alpha"));
        await Started("alpha", 1);

        await renderer.Dispatcher.InvokeAsync(() => Type(box, "al"));
        Release("alpha");
        await search.WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.Empty(await Shown(renderer, box));
        Assert.False(await Read<bool>(renderer, box, "_showDropdown"));
        Assert.False(await Read<bool>(renderer, box, "_isSearching"));
        Assert.Equal(1, pool.AvailableCount);
        Assert.Equal(5, pool.FreeSlots);
    }

    // The list shows bravo's results. A search for alpha starts, the operator types "alphaz" and back to "alpha" inside the
    // debounce: the first alpha search was superseded before it landed, so the debounced search for alpha must run, not be
    // taken for already answered - which would leave bravo's results under the text "alpha".
    [Fact]
    public async Task ASearchSupersededBeforeItLanded_IsRunAgainForTheSameText()
    {
        var (renderer, box, _) = Mount();
        Release("bravo");
        Release("alphaz");
        await renderer.Dispatcher.InvokeAsync(() => Search(box, "bravo"));
        Assert.Equal(["Result for bravo"], await Shown(renderer, box));

        var superseded = renderer.Dispatcher.InvokeAsync(() => Search(box, "alpha"));
        await Started("alpha", 1);
        await renderer.Dispatcher.InvokeAsync(() => Type(box, "alphaz"));
        await renderer.Dispatcher.InvokeAsync(() => Type(box, "alpha"));

        // The debounced search for "alpha" (300 ms) reaches Get-Recipient a second time.
        await Started("alpha", 2, TimeSpan.FromSeconds(15));
        Release("alpha");
        await superseded.WaitAsync(Bound, TestContext.Current.CancellationToken);
        await WaitUntil(async () => !await Read<bool>(renderer, box, "_isSearching"), "the debounced search lands");

        Assert.Equal(["Result for alpha"], await Shown(renderer, box));
        Assert.Equal("alpha", await Read<string>(renderer, box, "_lastSearchTerm"));
        Assert.Equal(0, _started.GetValueOrDefault("alphaz"));
    }

    // ---- the component, the pool, the stand-ins --------------------------------------------------------------------------

    private (Renderer Renderer, RecipientAutocomplete Box, ExoConnectionPool Pool) Mount()
    {
        var pool = new ExoConnectionPool(_h.Config, _h.ModuleConfig, _h.Enablement, NullLogger<ExoConnectionPool>.Instance, _h.Trace,
            (_, generation, _) => StandIn(generation, $"conn-{Interlocked.Increment(ref _connects)}"));
        _pools.Add(pool);
        Assert.True(pool.IsConfigured, "The harness must configure Exchange Online, or the box never searches.");

        var box = new RecipientAutocomplete();
        typeof(RecipientAutocomplete).GetProperty("ExoPool", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(box, pool);
        var renderer = new TestRenderer();
        _renderers.Add(renderer);
        renderer.Attach(box);
        return (renderer, box, pool);
    }

    private static Task Search(RecipientAutocomplete box, string term) => Call(box, "PerformSearch", term);

    private static Task Type(RecipientAutocomplete box, string text) => Call(box, "OnInput", new ChangeEventArgs { Value = text });

    private static Task Call(RecipientAutocomplete box, string method, object argument) =>
        (Task)typeof(RecipientAutocomplete).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(box, [argument])!;

    private static Task<T> Read<T>(Renderer renderer, RecipientAutocomplete box, string field) =>
        renderer.Dispatcher.InvokeAsync(() =>
            (T)typeof(RecipientAutocomplete).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(box)!);

    /// <summary>The display names in the suggestion list, read on the dispatcher.</summary>
    private static Task<List<string>> Shown(Renderer renderer, RecipientAutocomplete box) =>
        renderer.Dispatcher.InvokeAsync(() =>
        {
            var results = (IList)typeof(RecipientAutocomplete).GetField("_results", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(box)!;
            return results.Cast<object>()
                .Select(row => (string)row.GetType().GetProperty("DisplayName")!.GetValue(row)!)
                .ToList();
        });

    private ManualResetEventSlim Gate(string term) => _release.GetOrAdd(term, _ => new ManualResetEventSlim(false));

    private void Release(string term) => Gate(term).Set();

    private async Task Started(string term, int times, TimeSpan? within = null) =>
        await WaitUntil(() => Task.FromResult(_started.GetValueOrDefault(term) >= times),
            $"Get-Recipient has started {times} time(s) for '{term}'", within);

    /// <summary>
    /// An opened runspace for one connection. Get-Recipient counts a start for the text between the filter's wildcards, waits
    /// for the test to release that text, and answers one recipient named for it; Disconnect-ExchangeOnline does nothing.
    /// </summary>
    private PooledRunspace StandIn(long generation, string connectionId)
    {
        var rs = RunspaceFactory.CreateRunspace();
        rs.Open();
        rs.SessionStateProxy.SetVariable("standIn", new StandInHooks(this));
        using (var init = PowerShell.Create())
        {
            init.Runspace = rs;
            init.AddScript("""
                function global:Get-Recipient {
                    [CmdletBinding()] param($Filter, $ResultSize)
                    $term = [regex]::Match($Filter, '\*([^*]+)\*').Groups[1].Value
                    $global:standIn.Start($term)
                    [pscustomobject]@{ DisplayName = "Result for $term"; PrimarySmtpAddress = "$term@contoso.test"; RecipientTypeDetails = 'UserMailbox' }
                }
                function global:Disconnect-ExchangeOnline { param($ConnectionId, $Confirm) }
                """);
            init.Invoke();
            Assert.False(init.HadErrors, "The stand-in cmdlets could not be defined.");
        }

        var ps = PowerShell.Create();
        ps.Runspace = rs;
        return new PooledRunspace(rs, ps, generation, connectionId);
    }

    /// <summary>What the stand-in Get-Recipient calls back into: count the start, then wait for the text's release.</summary>
    public sealed class StandInHooks
    {
        private readonly RecipientAutocompleteSupersededSearchTests _owner;

        internal StandInHooks(RecipientAutocompleteSupersededSearchTests owner) => _owner = owner;

        public void Start(string term)
        {
            _owner._started.AddOrUpdate(term, 1, (_, count) => count + 1);
            if (!_owner.Gate(term).Wait(Bound))
                throw new TimeoutException($"The test never released '{term}'.");
        }
    }

    private static async Task WaitUntil(Func<Task<bool>> condition, string what, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? Bound);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {what}.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }

    private sealed class TestRenderer() : Renderer(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();
        public int Attach(IComponent component) => AssignRootComponentId(component);
        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;
        protected override void HandleException(Exception exception) => throw exception;
    }
#pragma warning restore BL0006
}
