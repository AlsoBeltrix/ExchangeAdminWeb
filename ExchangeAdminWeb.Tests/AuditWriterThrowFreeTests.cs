using System.Reflection;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28q: an audit or trace call never throws into its caller.
/// </summary>
/// <remarks>
/// The JSONL writer serialised a record before its try, so a value System.Text.Json cannot write threw out of the
/// audit call; and both SamName helpers dereferenced the actor, so a null one threw. A handler that audits its
/// success directly then ended with an exception instead of its outcome, and the record was lost - the refusal
/// helpers' catches hid it on the refusal paths only. Whatever stops a record now is logged once by the writer,
/// naming the record's action and category and never its payload, and the call returns. A real writer over a temp
/// folder, a capturing logger; nothing else.
/// </remarks>
public sealed class AuditWriterThrowFreeTests : IDisposable
{
    private const string Marker = "payload-marker-5e1f";
    private const string GetterFailure = "the getter of this value throws";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "eaw-a28q-" + Guid.NewGuid().ToString("N"));
    private readonly CapturingLogger _logger = new();
    private readonly JsonlLogService _writer;
    private readonly OperationTraceService _trace;
    private readonly AuditService _audit;

    public AuditWriterThrowFreeTests()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _root,
            ["Audit:RotationPeriod"] = "daily",
        }).Build();

        _writer = new JsonlLogService(config, _logger);
        _trace = new OperationTraceService(config, _writer);
        _audit = new AuditService(_writer, _trace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort */ }
    }

    // ---- the tripwire: every public method, a null actor and a value the writer cannot write ----------------

    /// <summary>Every public method of the audit service, the trace service and its scope, by a readable key.</summary>
    public static TheoryData<string> PublicMethods()
    {
        var data = new TheoryData<string>();
        foreach (var method in Writers())
            data.Add(Key(method));
        return data;
    }

    private static IEnumerable<MethodInfo> Writers() =>
        new[] { typeof(AuditService), typeof(OperationTraceService), typeof(OperationTraceService.OperationScope) }
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(method => !method.IsSpecialName);

    private static string Key(MethodInfo method) =>
        $"{method.DeclaringType!.Name}.{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";

    [Theory]
    [MemberData(nameof(PublicMethods))]
    public void EveryPublicAuditAndTraceMethod_GivenANullActorAndAValueItCannotWrite_ReturnsAndLogsOneError(string key)
    {
        var method = Writers().Single(m => Key(m) == key);
        using var scope = method.DeclaringType == typeof(OperationTraceService.OperationScope)
            ? _trace.BeginOperation("TripwireModule", "TripwireAction", actor: null!, ipAddress: "10.0.0.5")
            : null;
        object target = method.DeclaringType == typeof(AuditService) ? _audit
            : method.DeclaringType == typeof(OperationTraceService) ? _trace
            : scope!;
        var parameters = method.GetParameters();
        var arguments = parameters.Select(Argument).ToArray();
        var carriesAValue = parameters.Any(p => IsDictionary(p.ParameterType));

        var thrown = Record.Exception(() =>
        {
            var returned = method.Invoke(target, BindingFlags.DoNotWrapExceptions, null, arguments, null);
            (returned as IDisposable)?.Dispose();
        });

        Assert.Null(thrown);
        if (carriesAValue)
        {
            var error = Assert.Single(_logger.Errors);
            Assert.False(string.IsNullOrEmpty(error.Value("Action")), "the error does not name the record's action");
            Assert.False(string.IsNullOrEmpty(error.Value("Category")), "the error does not name the record's category");
            AssertNoPayload(error);
        }
        else
        {
            // Nothing this method takes can carry a value, so the null actor is the whole case: no error.
            Assert.Empty(_logger.Errors);
        }
    }

    [Fact]
    public void TheTripwire_CoversTheMethodsThatExistToday()
    {
        // So the theory above cannot pass by finding nothing: the thirteen audit methods, the three trace ones and
        // the scope's two, each with a parameter the argument builder knows.
        var keys = Writers().Select(Key).ToList();

        Assert.Equal(13, keys.Count(k => k.StartsWith("AuditService.", StringComparison.Ordinal)));
        Assert.Equal(["BeginOperation", "BeginRootOperation", "Step"],
            Writers().Where(m => m.DeclaringType == typeof(OperationTraceService)).Select(m => m.Name).Order());
        Assert.Equal(["Complete", "Dispose"],
            Writers().Where(m => m.DeclaringType == typeof(OperationTraceService.OperationScope)).Select(m => m.Name).Order());
        Assert.All(Writers().SelectMany(m => m.GetParameters()), p => Argument(p));
        Assert.Equal(15, Writers().Count(m => m.GetParameters().Any(p => IsDictionary(p.ParameterType))));
    }

    // ---- the writer itself -------------------------------------------------------------------------------

    [Theory]
    [InlineData("a throwing getter", "audit")]
    [InlineData("a cycle", "audit")]
    [InlineData("a System.Type", "audit")]
    [InlineData("a throwing getter", "trace")]
    [InlineData("a cycle", "trace")]
    [InlineData("a System.Type", "trace")]
    public void TheWriter_GivenARecordItCannotWrite_LogsOneErrorByActionAndCategory_WritesNoLine_AndWritesTheNext(string value, string stream)
    {
        var audit = stream == "audit";
        var record = audit
            ? new Dictionary<string, object?>
            {
                ["eventType"] = "audit",
                ["action"] = "TripwireAction",
                ["category"] = "TripwireCategory",
                ["marker"] = Marker,
                ["unwritable"] = Unwritable(value),
            }
            : new Dictionary<string, object?>
            {
                ["eventType"] = "operation.step",
                ["action"] = "TripwireAction",
                ["module"] = "TripwireModule",
                ["marker"] = Marker,
                ["details"] = new Dictionary<string, object?> { ["unwritable"] = Unwritable(value) },
            };

        var thrown = Record.Exception(() => Write(audit, record));

        Assert.Null(thrown);
        var error = Assert.Single(_logger.Errors);
        Assert.Equal("TripwireAction", error.Value("Action"));
        Assert.Equal(audit ? "TripwireCategory" : "TripwireModule", error.Value("Category"));
        Assert.Contains("TripwireAction", error.Text, StringComparison.Ordinal);
        Assert.NotNull(error.Exception);
        AssertNoPayload(error);
        Assert.Empty(Lines(audit));

        // The writer is not left broken: the next record is written.
        Write(audit, new Dictionary<string, object?>
        {
            ["eventType"] = audit ? "audit" : "operation.step",
            ["action"] = "NextAction",
            ["category"] = "NextCategory",
        });
        Assert.Equal("NextAction", Assert.Single(Lines(audit)).GetProperty("action").GetString());
        Assert.Single(_logger.Errors);
    }

    [Fact]
    public void ANullActor_IsRecordedAsUnknown_InTheAuditRecordAndInItsTrace()
    {
        _audit.LogModuleAction(null!, "10.0.0.5", "TripwireAction", "TripwireCategory", "target", success: true);
        using (_trace.BeginOperation("TripwireModule", "TripwireBegin", actor: null!, ipAddress: "10.0.0.5"))
        {
        }

        Assert.Empty(_logger.Errors);
        Assert.Equal("unknown", Assert.Single(Lines(audit: true)).GetProperty("user").GetString());
        var trace = Lines(audit: false);
        Assert.NotEmpty(trace);
        Assert.All(trace, e => Assert.Equal("unknown", e.GetProperty("user").GetString()));
        Assert.Contains(trace, e => e.GetProperty("action").GetString() == "TripwireBegin");
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    /// <summary>An object System.Text.Json cannot write: reading its one property throws.</summary>
    private sealed class ThrowingGetter
    {
        public string Value => throw new InvalidOperationException(GetterFailure);
    }

    private sealed class Node
    {
        public Node? Next { get; set; }
    }

    private static object Unwritable(string value)
    {
        switch (value)
        {
            case "a throwing getter":
                return new ThrowingGetter();
            case "a cycle":
                var node = new Node();
                node.Next = node;
                return node;
            case "a System.Type":
                return typeof(string);
            default:
                throw new ArgumentOutOfRangeException(nameof(value), value, null);
        }
    }

    /// <summary>
    /// The argument a method is handed: null for the actor, a value the writer cannot write beside the marker in
    /// every dictionary, and a plain value for everything else. A type this does not know fails the tripwire, so
    /// the next method's author decides what it is handed.
    /// </summary>
    private static object? Argument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (parameter.Name is "performedBy" or "actor")
            return null;
        if (IsDictionary(type))
            return new Dictionary<string, object?> { ["unwritable"] = new ThrowingGetter(), ["marker"] = Marker };
        if (type == typeof(string))
            return $"tripwire-{parameter.Name}";
        if (type == typeof(bool))
            return true;
        if (type == typeof(int))
            return 1;
        if (type == typeof(bool?) || type == typeof(int?))
            return null;
        if (type == typeof(string[]))
            return new[] { "tripwire" };
        if (type == typeof(Exception))
            return new InvalidOperationException("tripwire");
        if (type == typeof(List<AttributeChange>))
            return new List<AttributeChange> { new("title", "Job Title", "Analyst", "Lead Analyst") };

        throw new InvalidOperationException(
            $"No argument for {parameter.Member.DeclaringType?.Name}.{parameter.Member.Name}'s {type} {parameter.Name}: extend this tripwire.");
    }

    private static bool IsDictionary(Type type) =>
        type == typeof(Dictionary<string, object>) || type == typeof(IReadOnlyDictionary<string, object>);

    private static void AssertNoPayload(LogEntry error)
    {
        Assert.DoesNotContain(Marker, error.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, error.Exception?.ToString() ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(Marker, string.Join("|", error.Values.Values), StringComparison.Ordinal);
    }

    private void Write(bool audit, Dictionary<string, object?> record)
    {
        if (audit)
            _writer.WriteAudit(record);
        else
            _writer.WriteTrace(record);
    }

    private List<JsonElement> Lines(bool audit)
    {
        var folder = Path.Combine(_root, "ExchangeAdminWeb");
        if (!Directory.Exists(folder))
            return [];

        return Directory.GetFiles(folder, "exchangeadmin_*.jsonl")
            .Where(f => Path.GetFileNameWithoutExtension(f).EndsWith("_trace", StringComparison.OrdinalIgnoreCase) != audit)
            .OrderBy(f => f, StringComparer.Ordinal)
            .SelectMany(File.ReadAllLines)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                return document.RootElement.Clone();
            })
            .ToList();
    }

    internal sealed record LogEntry(LogLevel Level, string Text, Exception? Exception, IReadOnlyDictionary<string, object?> Values)
    {
        public string? Value(string name) => Values.TryGetValue(name, out var value) ? value?.ToString() : null;
    }

    private sealed class CapturingLogger : ILogger<JsonlLogService>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Errors
        {
            get { lock (_entries) return _entries.Where(e => e.Level >= LogLevel.Error).ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = (state as IEnumerable<KeyValuePair<string, object?>> ?? [])
                .GroupBy(kv => kv.Key)
                .ToDictionary(g => g.Key, g => g.First().Value);
            lock (_entries)
                _entries.Add(new LogEntry(logLevel, formatter(state, exception), exception, values));
        }
    }
}
