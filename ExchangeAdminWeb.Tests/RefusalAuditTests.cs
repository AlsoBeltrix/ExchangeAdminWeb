namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A28: protection and preflight refusals returned before recording anything,
/// so a refused attempt left no durable audit at all.
/// </summary>
/// <remarks>
/// Source scans, and the limit is real: both services reach a live directory or Delinea
/// before most of these refusals, so nothing here drives them. What they pin is that no
/// refusal path returns without writing a record first - which is the property that was
/// missing, and the one a later edit would quietly undo.
///
/// The operation trace already recorded these. It is diagnostic: different store, different
/// retention, different readers. "Who tried to disable this account and was stopped?" is
/// asked of the audit log, and the audit log did not know.
/// </remarks>
public class RefusalAuditTests
{
    private static string Source(string file) =>
        File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", file));

    private static string Body(string source, string signature, string endMarker)
    {
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start > 0, signature + " not found - this guard is stale.");
        var end = source.IndexOf(endMarker, start + 1, StringComparison.Ordinal);
        return source[start..(end > start ? end : source.Length)];
    }

    [Fact]
    public void EveryEmergencyDisableRefusal_WritesAnAuditRecord()
    {
        // Eight early returns, all of them before the success-path LogAudit.
        var body = Body(Source("EmergencyDisableService.cs"),
            "public async Task<EmergencyDisableResult> DisableAsync", "\n    private ");

        var refusals = System.Text.RegularExpressions.Regex.Matches(
            body, @"return new EmergencyDisableResult\(false,");
        Assert.True(refusals.Count >= 8, $"Expected the refusal returns; found {refusals.Count}.");

        var audited = System.Text.RegularExpressions.Regex.Matches(body, @"LogRefusal\(target,");
        Assert.Equal(refusals.Count, audited.Count);
    }

    [Fact]
    public void EveryAttributeSaveRefusal_WritesAnAuditRecord()
    {
        var source = Source("ADAttributeEditorService.cs");
        var body = Body(source, "public async Task<AttributeSaveResult> SaveAsync", "\n    private ");

        var refusals = System.Text.RegularExpressions.Regex.Matches(body, @"return new\(false, ");
        Assert.True(refusals.Count >= 7, $"Expected the refusal returns; found {refusals.Count}.");

        var audited = System.Text.RegularExpressions.Regex.Matches(body, @"LogRefusal\(target,");
        Assert.Equal(refusals.Count, audited.Count);
    }

    [Theory]
    [InlineData("EmergencyDisableService.cs", "public async Task<EmergencyDisableResult> DisableAsync", "return new EmergencyDisableResult(false,")]
    [InlineData("ADAttributeEditorService.cs", "public async Task<AttributeSaveResult> SaveAsync", "return new(false, ")]
    public void EveryRefusalReturnHasItsLogImmediatelyAbove(string file, string signature, string refusal)
    {
        // Review of the A28 batch: counting calls and returns proves neither ORDER nor
        // PAIRING. The counts stay equal if a LogRefusal is moved BELOW its return (dead
        // code), or if a braced LogRefusal has its return outside the block. Both leave a
        // refusal unrecorded while every count guard stays green.
        //
        // So: walk the method, and for each refusal return require a LogRefusal within the
        // few lines directly above it.
        var body = Body(Source(file), signature, "\n    private ");
        var lines = body.Split('\n');

        var unlogged = new List<string>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (!lines[i].TrimStart().StartsWith(refusal, StringComparison.Ordinal))
                continue;

            var window = lines.Skip(Math.Max(0, i - 6)).Take(i - Math.Max(0, i - 6));
            if (!window.Any(l => l.Contains("LogRefusal(", StringComparison.Ordinal)))
                unlogged.Add($"line {i + 1}: {lines[i].Trim()}");
        }

        Assert.True(unlogged.Count == 0,
            $"{file}: refusal return(s) with no LogRefusal above them - " + string.Join("; ", unlogged));
    }

    [Theory]
    [InlineData("EmergencyDisableService.cs")]
    [InlineData("ADAttributeEditorService.cs")]
    public void ARefusalLogSitsInsideABracedBlock(string file)
    {
        // This caught a real one. The first A28 edit inserted the LogRefusal call above a
        // return that a brace-less `if` had governed alone, so the `if` then guarded only the
        // logging and the refusal return became UNCONDITIONAL - SaveAsync failed every call.
        //
        // The counting guards above passed anyway: the call and the return were both present,
        // so the totals matched. Counting proves a line exists, not that it is reachable on
        // the path it was written for. This pins the shape instead.
        var source = Source(file).Split('\n');
        var brittle = new List<string>();

        for (var i = 1; i < source.Length; i++)
        {
            if (!source[i].TrimStart().StartsWith("LogRefusal(", StringComparison.Ordinal))
                continue;

            var previous = source[i - 1].Trim();
            if (previous.StartsWith("if (", StringComparison.Ordinal) && !previous.EndsWith("{", StringComparison.Ordinal))
                brittle.Add($"line {i + 1}: {previous}");
        }

        Assert.True(
            brittle.Count == 0,
            $"{file}: a LogRefusal is the lone statement of a brace-less if, which leaves the "
            + "refusal return below it unconditional. Wrap the refusal in braces. "
            + string.Join("; ", brittle));
    }

    [Theory]
    [InlineData("EmergencyDisableService.cs")]
    [InlineData("ADAttributeEditorService.cs")]
    public void ARefusalRecordSaysNoMutationWasAttempted(string file)
    {
        // Without this the row is indistinguishable from a failed mutation, which is a
        // materially different event - one changed nothing, the other may have changed
        // something and then failed.
        var source = Source(file);

        Assert.Contains("[\"mutationAttempted\"] = false", source, StringComparison.Ordinal);
    }
}
