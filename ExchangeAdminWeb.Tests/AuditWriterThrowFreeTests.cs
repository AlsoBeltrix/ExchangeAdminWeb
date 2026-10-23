using System.Reflection;
using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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
        var (thrown, carriesAValue) = CallWithANullActorAndAValueItCannotWrite(key);

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

    /// <summary>
    /// Calls the method <paramref name="key"/> names with a null actor and, in every dictionary, a value the writer
    /// cannot write - on the audit service, the trace service, or a scope begun for the call - and returns what it
    /// threw and whether it was handed a value at all.
    /// </summary>
    private (Exception? Thrown, bool CarriesAValue) CallWithANullActorAndAValueItCannotWrite(string key)
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

        var thrown = Record.Exception(() =>
        {
            var returned = method.Invoke(target, BindingFlags.DoNotWrapExceptions, null, arguments, null);
            (returned as IDisposable)?.Dispose();
        });

        return (thrown, parameters.Any(p => IsDictionary(p.ParameterType)));
    }

    [Theory]
    [MemberData(nameof(PublicMethods))]
    public void EveryPublicAuditAndTraceMethod_GivenALoggerThatThrows_StillReturns(string key)
    {
        // aud-a28u, folded in from codex attr-batch-11: the writer's report of a record it dropped was itself
        // unguarded, so a logger that throws took its exception out of the call the writer exists not to fail.
        _logger.Throws = true;

        var (thrown, carriesAValue) = CallWithANullActorAndAValueItCannotWrite(key);

        Assert.Null(thrown);
        // The one report the writer tried to make for the record it dropped; nothing else was reported.
        Assert.Equal(carriesAValue ? 1 : 0, _logger.Errors.Count);
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

    // ---- aud-a28u: the writer says whether it wrote the record, and the trace says so ----------------------

    private const string CannotSerialise = "a value it cannot serialise";
    private const string FileHeld = "its file held open";

    [Theory]
    [InlineData("audit", "written")]
    [InlineData("audit", CannotSerialise)]
    [InlineData("audit", FileHeld)]
    [InlineData("trace", "written")]
    [InlineData("trace", CannotSerialise)]
    [InlineData("trace", FileHeld)]
    public void TheWriter_SaysWhetherItWroteTheRecord(string stream, string cause)
    {
        var audit = stream == "audit";
        var record = RecordFor(audit, cause);

        JsonlWriteOutcome outcome;
        using (cause == FileHeld ? HoldTheFile(audit) : null)
            outcome = Write(audit, record);

        var expected = cause switch
        {
            "written" => JsonlWriteOutcome.Written,
            CannotSerialise => JsonlWriteOutcome.NotSerializable,
            _ => JsonlWriteOutcome.FileWriteFailed,
        };
        Assert.Equal(expected, outcome);
        Assert.Equal(cause == "written" ? 1 : 0, Lines(audit).Count);
        Assert.Equal(cause == "written" ? 0 : 1, _logger.Errors.Count);
    }

    [Theory]
    [InlineData(CannotSerialise, "the audit row was not written: the record could not be serialised")]
    [InlineData(FileHeld, "the audit row was not written: the log file could not be written")]
    public void ARecordTheWriterDrops_IsAFailedAuditWrittenStep_IsNotCounted_AndTheOperationKeepsItsOutcome(string cause, string reason)
    {
        // The step read Success and the action was counted whatever became of the row, so the trace claimed an audit
        // row the log did not have.
        var usage = new CountingTelemetry(_root);
        var service = new AuditService(_writer, _trace, usage);
        var extra = cause == CannotSerialise
            ? new Dictionary<string, object?> { ["unwritable"] = new ThrowingGetter() }
            : null;

        Exception? thrown;
        using (cause == FileHeld ? HoldTheFile(audit: true) : null)
        {
            thrown = Record.Exception(() => service.LogModuleAction(
                "operator", "10.0.0.5", "TripwireAction", "TripwireCategory", "target", success: true, extra: extra));
        }

        Assert.Null(thrown);
        Assert.Empty(Lines(audit: true));
        var trace = Lines(audit: false);
        var step = Assert.Single(trace, e => e.GetProperty("stage").GetString() == "AuditWritten");
        Assert.Equal("Failed", step.GetProperty("result").GetString());
        Assert.Equal(reason, step.GetProperty("details").GetProperty("reason").GetString());
        Assert.Equal("Success", step.GetProperty("details").GetProperty("auditResult").GetString());

        // The action succeeded, and its operation says so: the failure is the audit row's, not the action's.
        var complete = Assert.Single(trace, e => e.GetProperty("eventType").GetString() == "operation.complete");
        Assert.Equal("Success", complete.GetProperty("result").GetString());
        Assert.Equal(0, usage.Actions);
        Assert.Single(_logger.Errors);
    }

    [Fact]
    public void ARecordTheWriterWrites_IsASuccessfulAuditWrittenStep_AndIsCounted()
    {
        var usage = new CountingTelemetry(_root);
        var service = new AuditService(_writer, _trace, usage);

        service.LogModuleAction("operator", "10.0.0.5", "TripwireAction", "TripwireCategory", "target", success: true);

        Assert.Single(Lines(audit: true));
        var step = Assert.Single(Lines(audit: false), e => e.GetProperty("stage").GetString() == "AuditWritten");
        Assert.Equal("Success", step.GetProperty("result").GetString());
        Assert.False(step.GetProperty("details").TryGetProperty("reason", out _));
        Assert.Equal(1, usage.Actions);
        Assert.Empty(_logger.Errors);
    }

    [Theory]
    [InlineData("mailbox", "written")]
    [InlineData("mailbox", CannotSerialise)]
    [InlineData("mailbox", FileHeld)]
    [InlineData("calendar", "written")]
    [InlineData("calendar", CannotSerialise)]
    [InlineData("calendar", FileHeld)]
    public void ThePermissionRecords_HandBackWhetherTheirRowWasWritten(string record, string cause)
    {
        // aud-a50, part 4. The Mailbox Permissions and Calendar Permissions pages could tell that a refusal's row was not
        // written only from a throw, which the writer no longer does (aud-a28q); the two methods they audit through hand back
        // what the writer said.
        var extra = cause == CannotSerialise ? new Dictionary<string, object?> { ["unwritable"] = Unwritable("a cycle") } : null;

        JsonlWriteOutcome outcome;
        using (cause == FileHeld ? HoldTheFile(audit: true) : null)
        {
            outcome = record == "mailbox"
                ? _audit.LogMailboxPermission("operator", "10.0.0.5", "AddFullAccess_SelfGrantAttempt", "shared@contoso.test",
                    "jdoe@contoso.test", "FullAccess", false, "INC100", errorDetail: "refused", extra: extra)
                : _audit.LogCalendarPermission("operator", "10.0.0.5", "SetCalendar_SelfGrantAttempt", "shared@contoso.test",
                    "jdoe@contoso.test", "Reviewer", false, "INC100", errorDetail: "refused", extra: extra);
        }

        var expected = cause switch
        {
            "written" => JsonlWriteOutcome.Written,
            CannotSerialise => JsonlWriteOutcome.NotSerializable,
            _ => JsonlWriteOutcome.FileWriteFailed,
        };
        Assert.Equal(expected, outcome);
        Assert.Equal(cause == "written" ? 1 : 0, Lines(audit: true).Count);
    }

    [Theory]
    [InlineData("audit", CannotSerialise)]
    [InlineData("audit", FileHeld)]
    [InlineData("trace", CannotSerialise)]
    [InlineData("trace", FileHeld)]
    public void TheWriter_GivenALoggerThatThrows_StillReturnsItsOutcome(string stream, string cause)
    {
        // aud-a28u, folded in from codex attr-batch-11: the report of a dropped record, the writer's one recovery
        // path for both streams, can itself fail.
        _logger.Throws = true;
        var audit = stream == "audit";

        var outcome = JsonlWriteOutcome.Written;
        Exception? thrown;
        using (cause == FileHeld ? HoldTheFile(audit) : null)
            thrown = Record.Exception(() => outcome = Write(audit, RecordFor(audit, cause)));

        Assert.Null(thrown);
        Assert.Equal(cause == CannotSerialise ? JsonlWriteOutcome.NotSerializable : JsonlWriteOutcome.FileWriteFailed, outcome);
        Assert.Single(_logger.Errors);
        Assert.Empty(Lines(audit));

        // Nothing is left broken: the next record is written.
        Assert.Equal(JsonlWriteOutcome.Written, Write(audit, RecordFor(audit, "written")));
        Assert.Single(Lines(audit));
    }

    // ---- aud-a28z: a value read as text never throws, a row reported Written is a line the event log reads,
    //      and a dropped row is reported with who did what -----------------------------------------------------

    private const string Unreadable = "(unreadable ThrowingToString)";

    [Theory]
    [InlineData("category")]
    [InlineData("action")]
    [InlineData("user")]
    [InlineData("ip")]
    [InlineData("target")]
    [InlineData("ticket")]
    [InlineData("result")]
    public void AValueWhoseToStringThrows_UnderAFieldTheAuditReads_IsMarkerText_AndTheRowIsWritten(string field)
    {
        // A caller's extra can replace any field; the audit read each by ToString - for the trace's operation, the
        // step's details, the operation's result and the usage count - so a ToString that threw escaped the call.
        var thrown = Record.Exception(() => _audit.LogModuleAction(
            "operator", "10.0.0.5", "TripwireAction", "TripwireCategory", "target", success: true,
            extra: new Dictionary<string, object?> { [field] = new ThrowingToString() }));

        Assert.Null(thrown);
        var row = Assert.Single(Lines(audit: true));
        Assert.Equal(JsonValueKind.Object, row.GetProperty(field).ValueKind);
        Assert.Contains(Unreadable, TraceText(), StringComparison.Ordinal);
        var step = Assert.Single(Lines(audit: false), e => e.GetProperty("stage").GetString() == "AuditWritten");
        Assert.Equal("Success", step.GetProperty("result").GetString());
        Assert.Empty(_logger.Errors);
    }

    [Fact]
    public void TheWriter_GivenAnEventTypeWhoseToStringThrows_WritesTheRecord_AsAudit()
    {
        var outcome = JsonlWriteOutcome.FileWriteFailed;
        var thrown = Record.Exception(() => outcome = _writer.Write(new Dictionary<string, object?>
        {
            ["eventType"] = new ThrowingToString(),
            ["action"] = "TripwireAction",
            ["category"] = "TripwireCategory",
        }));

        Assert.Null(thrown);
        Assert.Equal(JsonlWriteOutcome.Written, outcome);
        Assert.Equal("TripwireAction", Assert.Single(Lines(audit: true)).GetProperty("action").GetString());
        Assert.Empty(_logger.Errors);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("trace")]
    public void ALineThatFailsHalfWritten_IsTakenBack_SoTheFileHoldsExactlyTheRowsReportedWritten(string stream)
    {
        // Folded in from codex attr-batch-12, part 3. The half line stayed, the next row was appended onto it and
        // reported Written - yet the event log, parsing line by line, dropped the two together.
        var audit = stream == "audit";
        var failNext = false;
        _writer.WriteLineBytes = (file, line) =>
        {
            if (failNext)
            {
                failNext = false;
                file.Write(line, 0, line.Length / 2);
                throw new IOException("the disk is full");
            }

            file.Write(line);
        };

        var outcomes = new List<JsonlWriteOutcome> { Write(audit, Row(audit, "Before")) };
        failNext = true;
        outcomes.Add(Write(audit, Row(audit, "Lost")));
        outcomes.Add(Write(audit, Row(audit, "After")));
        outcomes.Add(Write(audit, Row(audit, "AfterThat")));

        Assert.Equal(
            [JsonlWriteOutcome.Written, JsonlWriteOutcome.FileWriteFailed, JsonlWriteOutcome.Written, JsonlWriteOutcome.Written],
            outcomes);
        var (rows, unparsed) = ReadAsTheEventLogDoes(audit);
        Assert.Equal(0, unparsed);
        Assert.Equal(["Before", "After", "AfterThat"], rows.Select(r => r.GetProperty("action").GetString()));
        Assert.Single(_logger.Errors);
        Assert.DoesNotContain(_logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData("audit")]
    [InlineData("trace")]
    public void ATailADeadProcessLeft_IsEndedFirst_SoTheNextRowParsesOnItsOwnLine_AndTheRepairIsReported(string stream)
    {
        // A writer that died mid-line - nothing in this process could take its half line back - left a tail the next
        // row would have been appended onto.
        var audit = stream == "audit";
        var path = (audit ? _writer.GetAuditLogPaths(DateTime.Now) : _writer.GetTraceLogPaths(DateTime.Now)).Last();
        File.WriteAllText(path, "{\"action\":\"Earlier\"}" + Environment.NewLine + "{\"action\":\"CutOff");

        var outcome = Write(audit, Row(audit, "Next"));

        Assert.Equal(JsonlWriteOutcome.Written, outcome);
        var (rows, unparsed) = ReadAsTheEventLogDoes(audit);
        Assert.Equal(["Earlier", "Next"], rows.Select(r => r.GetProperty("action").GetString()));
        Assert.Equal(1, unparsed);
        var repair = Assert.Single(_logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(path, repair.Value("Path"));
        Assert.Empty(_logger.Errors);
    }

    [Fact]
    public void ADroppedRowsReport_NamesWhoDidWhat_WhenTheTraceIsDroppedToo()
    {
        // Folded in from codex attr-batch-12, part 4. The report named the stream, action, category and path only: with
        // the trace gone as well, nothing tied the missing row to an operation or an actor.
        string operationId;
        using (HoldTheFile(audit: true))
        using (HoldTheFile(audit: false))
        {
            using var scope = _trace.BeginOperation(
                "TripwireModule", "TripwireOperation", "EXAMPLE\\operator", "10.0.0.5", target: "alex@example.test", ticket: "CHG0300");
            operationId = scope.OperationId;
            _audit.LogModuleAction(
                "EXAMPLE\\operator", "10.0.0.5", "TripwireAction", "TripwireCategory", "alex@example.test", success: true,
                ticketNumber: "CHG0300", extra: new Dictionary<string, object?> { ["marker"] = Marker });
        }

        var report = Assert.Single(_logger.Errors, e => e.Value("Stream") == "Audit");
        Assert.Equal("TripwireAction", report.Value("Action"));
        Assert.Equal("TripwireCategory", report.Value("Category"));
        Assert.Equal(operationId, report.Value("OperationId"));
        Assert.Equal("operator", report.Value("User"));
        Assert.Equal("10.0.0.5", report.Value("Ip"));
        Assert.Equal("alex@example.test", report.Value("Target"));
        Assert.Equal("CHG0300", report.Value("Ticket"));
        Assert.Equal("Success", report.Value("Result"));
        AssertNoPayload(report);
        Assert.Empty(Lines(audit: true));
        Assert.Empty(Lines(audit: false));
    }

    /// <summary>A plain record for the stream, by its action.</summary>
    private static Dictionary<string, object?> Row(bool audit, string action) => new()
    {
        ["eventType"] = audit ? "audit" : "operation.step",
        ["action"] = action,
        ["category"] = "TripwireCategory",
    };

    /// <summary>
    /// The stream's lines as the Admin Event Log reads them - each non-blank line parsed on its own, a line that does not
    /// parse dropped - and how many were dropped.
    /// </summary>
    private (List<JsonElement> Rows, int Unparsed) ReadAsTheEventLogDoes(bool audit)
    {
        var rows = new List<JsonElement>();
        var unparsed = 0;
        foreach (var line in LogFiles(audit).SelectMany(File.ReadAllLines).Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                rows.Add(document.RootElement.Clone());
            }
            catch (JsonException)
            {
                unparsed++;
            }
        }

        return (rows, unparsed);
    }

    private string TraceText() => string.Concat(LogFiles(audit: false).Select(File.ReadAllText));

    private IEnumerable<string> LogFiles(bool audit)
    {
        var folder = Path.Combine(_root, "ExchangeAdminWeb");
        if (!Directory.Exists(folder))
            return [];

        return Directory.GetFiles(folder, "exchangeadmin_*.jsonl")
            .Where(f => Path.GetFileNameWithoutExtension(f).EndsWith("_trace", StringComparison.OrdinalIgnoreCase) != audit)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
    }

    // ---- helpers ---------------------------------------------------------------------------------------

    /// <summary>A value whose ToString throws; System.Text.Json writes it as an empty object, having nothing to read.</summary>
    private sealed class ThrowingToString
    {
        public override string ToString() => throw new InvalidOperationException("this value's ToString throws");
    }

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

    private JsonlWriteOutcome Write(bool audit, Dictionary<string, object?> record) =>
        audit ? _writer.WriteAudit(record) : _writer.WriteTrace(record);

    /// <summary>A plain record for the stream, carrying a value the writer cannot serialise when the cause is that.</summary>
    private static Dictionary<string, object?> RecordFor(bool audit, string cause)
    {
        var record = new Dictionary<string, object?>
        {
            ["eventType"] = audit ? "audit" : "operation.step",
            ["action"] = "TripwireAction",
            ["category"] = "TripwireCategory",
        };
        if (cause == CannotSerialise)
            record["unwritable"] = new ThrowingGetter();
        return record;
    }

    /// <summary>
    /// Holds the stream's active log file open with no sharing, as another process can, so the writer cannot open it:
    /// this minute's file and the next minute's, so a run across midnight still holds the one written to.
    /// </summary>
    private IDisposable HoldTheFile(bool audit)
    {
        var now = DateTime.Now;
        var streams = new[] { now, now.AddMinutes(1) }
            .Select(date => (audit ? _writer.GetAuditLogPaths(date) : _writer.GetTraceLogPaths(date)).Last())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            .ToList();
        return new HeldFiles(streams);
    }

    private sealed class HeldFiles(List<FileStream> streams) : IDisposable
    {
        public void Dispose()
        {
            foreach (var stream in streams)
                stream.Dispose();
        }
    }

    /// <summary>
    /// Counts the actions the audit service hands usage telemetry, at the service's write seam; the switch is on. Its
    /// module config is a real store over the test's folder, which nothing here reads.
    /// </summary>
    private sealed class CountingTelemetry(string folder) : UsageTelemetryService(
        new UsageEventRepository(new SqliteConnectionFactory(Path.Combine(folder, "usage.db"))),
        ModuleConfigIn(folder),
        new ModuleCatalog(),
        NullLogger<UsageTelemetryService>.Instance)
    {
        private int _actions;

        public int Actions => Volatile.Read(ref _actions);

        public override bool Enabled() => true;

        internal override void Enqueue(UsageEvent e)
        {
            if (e.Kind == "action")
                Interlocked.Increment(ref _actions);
        }

        private static ModuleConfigService ModuleConfigIn(string folder)
        {
            var env = Substitute.For<IWebHostEnvironment>();
            env.ContentRootPath.Returns(folder);
            return new ModuleConfigService(
                new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(folder), NullLogger<ModuleConfigService>.Instance);
        }
    }

    private List<JsonElement> Lines(bool audit)
    {
        return LogFiles(audit)
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

        /// <summary>When set, every Log call throws once it has recorded its entry, as a failing provider can (aud-a28u).</summary>
        public bool Throws { get; set; }

        public IReadOnlyList<LogEntry> Errors
        {
            get { lock (_entries) return _entries.Where(e => e.Level >= LogLevel.Error).ToList(); }
        }

        public IReadOnlyList<LogEntry> Entries
        {
            get { lock (_entries) return _entries.ToList(); }
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

            if (Throws)
                throw new InvalidOperationException("the logging provider failed");
        }
    }
}
