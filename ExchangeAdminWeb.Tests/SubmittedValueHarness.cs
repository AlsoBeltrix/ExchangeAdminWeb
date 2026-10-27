using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;

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
/// the field and pumps. Blazor's renderer dispatcher does the same, which is why a queued event can land there.
/// </para>
/// </remarks>
internal static class SubmittedValueHarness
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

    /// <summary>Sets an injected property on the page.</summary>
    public static void Set(object page, string property, object? value) =>
        (page.GetType().GetProperty(property, Members)
            ?? throw new InvalidOperationException($"{page.GetType().Name} has no property {property}"))
            .SetValue(page, value);

    /// <summary>Sets a page field - what a bound control's change event does.</summary>
    public static void SetField(object page, string field, object? value) =>
        FieldOf(page, field).SetValue(page, value);

    /// <summary>Reads a page field.</summary>
    public static T? Field<T>(object page, string field) => (T?)FieldOf(page, field).GetValue(page);

    /// <summary>Invokes a handler and returns its task.</summary>
    public static Task Call(object page, string method, params object?[] arguments) =>
        (Task)(page.GetType().GetMethod(method, Members)
            ?? throw new InvalidOperationException($"{page.GetType().Name} has no method {method}"))
            .Invoke(page, arguments.Length == 0 ? null : arguments)!;

    private static FieldInfo FieldOf(object page, string field) =>
        page.GetType().GetField(field, Members)
            ?? throw new InvalidOperationException($"{page.GetType().Name} has no field {field}");

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

    /// <summary>A context that holds posted continuations until the test runs them.</summary>
    internal sealed class HeldContinuations : SynchronizationContext
    {
        private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _posted = new();

        public override void Post(SendOrPostCallback d, object? state) => _posted.Add((d, state));

        public override void Send(SendOrPostCallback d, object? state) => d(state);

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
