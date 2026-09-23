using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using System.Text.Json;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Proves AC18 against the EMITTED JSON, not against the code that builds it.
/// </summary>
/// <remarks>
/// Closes the gap recorded in `.agents/review/findings/cpr-11.md`. The source-text guards there
/// prove the module SUPPLIES a uniform key set; they cannot prove the writer emits one, and the
/// writer is exactly where the nulls were being lost. `JsonlLogService` filters null-valued keys
/// and its serializer options ignore nulls, so a field the module set to null simply vanished -
/// which is why the audit now uses the "n/a" sentinel the plan pre-authorised.
///
/// This test writes real events through the real `AuditService` and `JsonlLogService`, reads the
/// file back, and compares key sets. It is the only thing in the suite that would catch the
/// sentinel being removed, or a future writer change re-introducing the drop.
/// </remarks>
public class CloudPasswordResetAuditShapeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "cpr-audit-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { /* a leftover temp directory is not worth failing a test over */ }
        GC.SuppressFinalize(this);
    }

    /// <summary>The field set the plan's "Audit fields, and Splunk" table specifies.</summary>
    private static readonly string[] RequiredExtraKeys =
    [
        "targetObjectId",
        "targetCloudOnly",
        "targetDirectoryRoles",
        "destinationAddress",
        "destinationEmployeeId",
        "forceChangePasswordNextSignIn",
        "passwordDelivery",
        "revealUsed",
        "refusalReason",
        "protectedPrincipalServiced",
    ];

    private const string NotApplicable = "n/a";

    private AuditService CreateAudit()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:LogRoot"] = _tempDir,
                ["Audit:RotationPeriod"] = "daily",
            })
            .Build();

        var log = new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>());
        return new AuditService(log, new OperationTraceService(config, log));
    }

    private JsonDocument[] ReadEvents()
    {
        var dir = Path.Combine(_tempDir, "ExchangeAdminWeb");
        var file = Directory.GetFiles(dir, "exchangeadmin_*.jsonl")
            .Single(f => !Path.GetFileNameWithoutExtension(f).EndsWith("_trace", StringComparison.OrdinalIgnoreCase));

        return File.ReadAllLines(file)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => JsonDocument.Parse(l))
            .ToArray();
    }

    /// <summary>
    /// The `extra` the page builds, with the same sentinel discipline, for a given outcome.
    /// </summary>
    /// <remarks>
    /// Mirrors `CloudPasswordReset.razor`'s AuditReset rather than calling it - the page needs a
    /// Blazor circuit. The source-text guard in CloudPasswordResetWritePathTests is what keeps
    /// the two from drifting; this one proves the SHAPE survives serialization.
    /// </remarks>
    private static Dictionary<string, object?> Extra(
        string delivery,
        string? refusal,
        string? destinationAddress,
        string? servicedNote) => new()
        {
            ["targetObjectId"] = "8f14e45f-ceea-467a-9575-28263f0c9629",
            ["targetCloudOnly"] = true,
            ["targetDirectoryRoles"] = new[] { "Global Administrator" },
            ["destinationAddress"] = destinationAddress ?? NotApplicable,
            ["destinationEmployeeId"] = "0001234",
            ["forceChangePasswordNextSignIn"] = true,
            ["passwordDelivery"] = delivery,
            ["revealUsed"] = delivery == "Revealed",
            ["refusalReason"] = refusal ?? NotApplicable,
            ["protectedPrincipalServiced"] = servicedNote ?? NotApplicable,
        };

    [Fact]
    public void Every_outcome_emits_an_identical_key_set()
    {
        // The claim AC18 actually makes, tested where it can actually fail. A success, a refusal,
        // a reveal and a delivery failure must be indistinguishable by SHAPE, so a Splunk search
        // that finds a missing field knows something is broken rather than that a branch differed.
        var audit = CreateAudit();

        audit.LogModuleAction("op", "10.0.0.1", "CloudPasswordReset_Execute", "CloudPasswordReset",
            "svc@example.test", true, "INC1", null, Extra("Sent", null, "jo@example.test", null));

        audit.LogModuleAction("op", "10.0.0.1", "CloudPasswordReset_Execute", "CloudPasswordReset",
            "svc@example.test", false, "INC1", "refused", Extra("NotAttempted", "DestinationNoMatch", null, null));

        audit.LogModuleAction("op", "10.0.0.1", "CloudPasswordReset_Revealed", "CloudPasswordReset",
            "svc@example.test", true, "INC1", null, Extra("Revealed", "DestinationNoMailbox", null, null));

        audit.LogModuleAction("op", "10.0.0.1", "CloudPasswordReset_DeliveryFailed", "CloudPasswordReset",
            "svc@example.test", false, "INC1", "send failed", Extra("SendFailed", null, "jo@example.test", "serviced by X"));

        var events = ReadEvents();
        Assert.Equal(4, events.Length);

        // "error" is excluded and the exclusion is the finding. AuditService.LogModuleAction
        // writes ["error"] = success ? null : errorDetail, and the writer drops nulls, so a
        // SUCCESS has no error key and a FAILURE does. That is a shared-service behaviour this
        // module cannot change without altering every other module's records, so AC18's "every
        // field on every event" is not achievable for that one key and the plan now says so.
        //
        // Excluding it here would be narrowing the test to fit the code. Instead the assertion
        // pins that it is the ONLY difference: if any other field ever starts varying by outcome,
        // this fails.
        var keySets = events
            .Select(e => e.RootElement.EnumerateObject()
                .Select(p => p.Name)
                .Where(n => n != "error")
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList())
            .ToList();

        foreach (var keys in keySets)
        {
            Assert.True(
                keySets[0].SequenceEqual(keys),
                "Audit events for different outcomes emitted different key sets:\n  "
                + string.Join("\n  ", keySets.Select(k => string.Join(",", k))));
        }

        // And the exception is exactly one key, not a licence for more.
        var withError = events.Count(e => e.RootElement.TryGetProperty("error", out _));
        Assert.Equal(2, withError);

        var fullKeySets = events
            .Select(e => e.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal))
            .ToList();
        var union = fullKeySets.Aggregate(new HashSet<string>(StringComparer.Ordinal), (a, b) => { a.UnionWith(b); return a; });
        var intersection = fullKeySets.Aggregate(new HashSet<string>(union, StringComparer.Ordinal), (a, b) => { a.IntersectWith(b); return a; });

        var varying = union.Except(intersection).ToList();
        Assert.True(
            varying.Count == 1 && varying[0] == "error",
            "The only key allowed to vary between outcomes is 'error', which AuditService nulls on "
            + "success. These varied: " + string.Join(", ", varying));
    }

    [Fact]
    public void Every_required_field_survives_serialization()
    {
        var audit = CreateAudit();

        audit.LogModuleAction("op", "10.0.0.1", "CloudPasswordReset_Execute", "CloudPasswordReset",
            "svc@example.test", true, "INC1", null, Extra("Sent", null, "jo@example.test", null));

        var root = ReadEvents().Single().RootElement;

        foreach (var key in RequiredExtraKeys)
        {
            Assert.True(root.TryGetProperty(key, out _), $"'{key}' did not survive serialization.");
        }
    }

    [Fact]
    public void A_null_value_really_does_vanish_which_is_why_the_sentinel_exists()
    {
        // Pins the CAUSE, not the workaround. If JsonlLogService is ever changed to preserve
        // nulls, this fails - and whoever changed it can then drop the sentinel deliberately
        // rather than leaving a defensive string nobody remembers the reason for.
        var audit = CreateAudit();

        var extra = Extra("Sent", null, "jo@example.test", null);
        extra["refusalReason"] = null;

        audit.LogModuleAction("op", "10.0.0.1", "CloudPasswordReset_Execute", "CloudPasswordReset",
            "svc@example.test", true, "INC1", null, extra);

        var root = ReadEvents().Single().RootElement;

        Assert.False(
            root.TryGetProperty("refusalReason", out _),
            "JsonlLogService now preserves nulls. The \"n/a\" sentinel in CloudPasswordReset.razor "
            + "was added only because it did not - remove it and update the plan.");
    }

    [Fact]
    public void Booleans_are_emitted_as_JSON_booleans_not_strings()
    {
        // Plan rule 2. "Yes" / "true" / "(set)" each need a different Splunk expression, and the
        // one somebody writes will be the one that silently matches nothing.
        var audit = CreateAudit();

        audit.LogModuleAction("op", "10.0.0.1", "CloudPasswordReset_Execute", "CloudPasswordReset",
            "svc@example.test", true, "INC1", null, Extra("Sent", null, "jo@example.test", null));

        var root = ReadEvents().Single().RootElement;

        Assert.Equal(JsonValueKind.True, root.GetProperty("targetCloudOnly").ValueKind);
        Assert.Equal(JsonValueKind.True, root.GetProperty("forceChangePasswordNextSignIn").ValueKind);
        Assert.Equal(JsonValueKind.False, root.GetProperty("revealUsed").ValueKind);
    }

    [Fact]
    public void Directory_roles_are_emitted_as_an_array()
    {
        // Plan rule 1, one fact per field: the roles must not be packed into a sentence the way
        // IntuneDevices packs its wipe flags, because every query against that shape needs a
        // field extraction first.
        var audit = CreateAudit();

        audit.LogModuleAction("op", "10.0.0.1", "CloudPasswordReset_Execute", "CloudPasswordReset",
            "svc@example.test", true, "INC1", null, Extra("Sent", null, "jo@example.test", null));

        var roles = ReadEvents().Single().RootElement.GetProperty("targetDirectoryRoles");

        Assert.Equal(JsonValueKind.Array, roles.ValueKind);
        Assert.Equal("Global Administrator", roles[0].GetString());
    }
}
