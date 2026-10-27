using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-72. What the submitted-value tests share: a page driven through its compiled handlers by reflection, a
/// synchronization context that holds a handler at its first await while a test changes a bound field, and the
/// audit rows the run wrote.
/// </summary>
/// <remarks>
/// <para>
/// The bug class: a handler reads a field a control is bound to after its first await. A change event landing
/// while the handler is suspended - in production, the change Enter's keydown had outrun - then makes the run, its
/// label, its audit row or its notice name a value the operator did not submit. A test proves the fix by being that
/// change event: it lets the handler start, changes the field while the handler is suspended, then lets it finish,
/// and asserts every record of the run names the value submitted.
/// </para>
/// <para>
/// Two ways to hold a handler at its first await. Where that await is a service call, a stand-in that blocks until
/// released. Where it is <c>await Task.Yield()</c>, <see cref="HeldContinuations"/>: the yield posts its continuation
/// to the current context, so with this one the handler is really suspended - asserted - until the test has changed
/// the field and pumps. Blazor's renderer dispatcher does the same, which is why a queued event can land there. A
/// handler that renders before its yield needs a renderer: <see cref="RunOnDispatcherWithChangeInTheYield"/> runs it on
/// a <see cref="HandlerRenderer"/>'s dispatcher and applies the change in the same work item, after the handler has
/// yielded and before its continuation - which that dispatcher queues behind the work item - can run.
/// </para>
/// </remarks>
internal static class SubmittedValueHarness
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    /// <summary>Sets an injected property on the page.</summary>
    public static void Set(object page, string property, object? value) =>
        Find(page, type => type.GetProperty(property, Members | BindingFlags.DeclaredOnly), "property", property)
            .SetValue(page, value);

    /// <summary>Sets a page field - what a bound control's change event does.</summary>
    public static void SetField(object page, string field, object? value) =>
        FieldOf(page, field).SetValue(page, value);

    /// <summary>Reads a page field.</summary>
    public static T? Field<T>(object page, string field) => (T?)FieldOf(page, field).GetValue(page);

    /// <summary>Invokes a handler and returns its task.</summary>
    public static Task Call(object page, string method, params object?[] arguments) =>
        (Task)Find(page, type => type.GetMethod(method, Members | BindingFlags.DeclaredOnly), "method", method)
            .Invoke(page, arguments.Length == 0 ? null : arguments)!;

    private static FieldInfo FieldOf(object page, string field) =>
        Find(page, type => type.GetField(field, Members | BindingFlags.DeclaredOnly), "field", field);

    // Up the hierarchy, because a page is often a render-nothing subclass and its members are the base's privates.
    private static T Find<T>(object page, Func<Type, T?> member, string kind, string name) where T : MemberInfo
    {
        for (var type = page.GetType(); type is not null; type = type.BaseType)
        {
            if (member(type) is { } found)
                return found;
        }

        throw new InvalidOperationException($"{page.GetType().Name} has no {kind} {name}");
    }

    /// <summary>
    /// Runs <paramref name="handler"/> on <paramref name="renderer"/>'s dispatcher and, in the same work item once it
    /// has yielded, <paramref name="inTheYield"/> - the change event. The handler's continuation is queued behind the
    /// work item, so the change lands in the yield. Returns the handler's task, still running.
    /// </summary>
    public static Task RunOnDispatcherWithChangeInTheYield(HandlerRenderer renderer, Func<Task> handler, Action inTheYield)
    {
        Task running = Task.CompletedTask;
        renderer.Dispatcher.InvokeAsync(() =>
        {
            running = handler();
            Assert.False(running.IsCompleted, "The handler finished without yielding, so nothing could change under it.");
            inTheYield();
        }).GetAwaiter().GetResult();
        return running;
    }

#pragma warning disable BL0006 // the minimal handler renderer, the EventLogUndoRefusalTests pattern
    /// <summary>A renderer that renders into nothing: what a page needs to call StateHasChanged in a test.</summary>
    internal sealed class HandlerRenderer() : Renderer(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        /// <summary>Attaches <paramref name="component"/> as a root, on the dispatcher.</summary>
        public void Attach(IComponent component) =>
            Dispatcher.InvokeAsync(() => AssignRootComponentId(component)).GetAwaiter().GetResult();

        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;

        protected override void HandleException(Exception exception) => throw exception;
    }
#pragma warning restore BL0006

    /// <summary>
    /// Starts <paramref name="handler"/> under a <see cref="HeldContinuations"/> context, asserts it is suspended at
    /// an await, runs <paramref name="whileSuspended"/> - the change event - and then pumps the handler to the end.
    /// </summary>
    public static void RunWithChangeWhileSuspended(Func<Task> handler, Action whileSuspended)
    {
        var previous = SynchronizationContext.Current;
        var pump = new HeldContinuations();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            var running = handler();
            Assert.False(running.IsCompleted, "The handler finished without yielding, so nothing could change under it.");

            whileSuspended();

            pump.RunUntil(running, TimeSpan.FromSeconds(30));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>
    /// kd-74. As <see cref="RunWithChangeWhileSuspended"/>, but the change lands at the first suspension at which
    /// <paramref name="when"/> holds rather than at the first await: the handler is pumped one continuation at a time,
    /// <paramref name="when"/> read between them - where a queued browser event lands - and once it holds with the
    /// handler still suspended, <paramref name="change"/> runs and the handler is pumped to the end.
    /// </summary>
    public static void RunWithChangeWhen(Func<Task> handler, Func<bool> when, Action change)
    {
        var previous = SynchronizationContext.Current;
        var pump = new HeldContinuations();
        SynchronizationContext.SetSynchronizationContext(pump);
        try
        {
            var running = handler();
            pump.RunUntil(() => running.IsCompleted || when(), TimeSpan.FromSeconds(30));
            Assert.False(running.IsCompleted, "The handler finished before the moment the change was to land in.");

            change();

            pump.RunUntil(running, TimeSpan.FromSeconds(30));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>A context that holds posted continuations until the test runs them.</summary>
    internal sealed class HeldContinuations : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _posted = new();

        public override void Post(SendOrPostCallback d, object? state) => _posted.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

        /// <summary>kd-74. Runs posted continuations, one at a time, until <paramref name="condition"/> holds.</summary>
        public void RunUntil(Func<bool> condition, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!condition())
            {
                var left = deadline - DateTime.UtcNow;
                Assert.True(left > TimeSpan.Zero, "the moment never came");
                if (_posted.TryTake(out var next, TimeSpan.FromMilliseconds(Math.Min(left.TotalMilliseconds, 100))))
                    next.Callback(next.State);
            }
        }

        public void RunUntil(Task task, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (!task.IsCompleted)
            {
                var left = deadline - DateTime.UtcNow;
                Assert.True(left > TimeSpan.Zero, "the handler never finished");
                if (_posted.TryTake(out var next, TimeSpan.FromMilliseconds(Math.Min(left.TotalMilliseconds, 100))))
                    next.Callback(next.State);
            }

            task.GetAwaiter().GetResult();
        }
    }

    /// <summary>The audit rows written under <paramref name="logRoot"/> (Audit:LogRoot), oldest first.</summary>
    public static List<JsonElement> AuditRows(string logRoot)
    {
        var folder = Path.Combine(logRoot, "ExchangeAdminWeb");
        if (!Directory.Exists(folder))
            return [];

        return Directory.GetFiles(folder, "exchangeadmin_*.jsonl")
            .Where(file => !Path.GetFileNameWithoutExtension(file).EndsWith("_trace", StringComparison.OrdinalIgnoreCase))
            .OrderBy(file => file, StringComparer.Ordinal)
            .SelectMany(File.ReadAllLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .Where(row => row.TryGetProperty("eventType", out var type) && type.GetString() == "audit")
            .ToList();
    }

    /// <summary>
    /// The markup tag that binds <paramref name="field"/> on <paramref name="page"/> (a file under Components/Pages),
    /// read with the page's comments blanked - the repo's markup tripwires read the source, there is no renderer here.
    /// </summary>
    public static string BoundInputTag(string page, string field)
    {
        var source = File.ReadAllText(Path.Combine(ProgressScan.RepoRoot(), "Components", "Pages", page));
        var markup = RazorSyntax.CommentsBlanked(source);
        var bind = markup.IndexOf($"@bind=\"{field}\"", StringComparison.Ordinal);
        Assert.True(bind >= 0, $"{page} no longer binds {field}");
        Assert.True(markup.IndexOf($"@bind=\"{field}\"", bind + 1, StringComparison.Ordinal) < 0, $"{page} binds {field} twice");
        var tagStart = markup.LastIndexOf('<', bind);
        var tagEnd = markup.IndexOf('>', bind);
        return markup[tagStart..(tagEnd + 1)];
    }

    /// <summary>A string property of an audit row, or null when the row leaves it out.</summary>
    public static string? Text(JsonElement row, string property) =>
        row.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
