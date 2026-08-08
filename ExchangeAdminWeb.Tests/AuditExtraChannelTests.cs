using System.Reflection;
using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Every audit method that ACCEPTS an <c>extra</c> dictionary must actually write it.
/// </summary>
/// <remarks>
/// Protected-principal servicing records which group authorised acting on a principal the app
/// would normally refuse. That record exists only on SUCCESS, and every audit method here writes
/// <c>["error"] = success ? null : errorDetail</c> - so the failure channel cannot carry it. The
/// `extra` dictionary is the only channel that survives a successful action.
///
/// The failure mode is silence: a group passed to a method that ignores it looks identical at the
/// call site, compiles, and produces an audit record missing the one field that explains why a VIP
/// mailbox was modified. That exact defect already occurred once (blr-era, Blocked Senders).
///
/// This caught a real instance while it was being written: nine methods gained the parameter and
/// one - LogLookupAction - was left without the merge. A parameter that is accepted and dropped is
/// worse than one that does not exist, because the call site reads as correct.
/// </remarks>
public class AuditExtraChannelTests
{
    [Fact]
    public void EveryAuditMethodTakingExtra_ActuallyMergesIt()
    {
        var source = ReadAuditService();

        var offenders = new List<string>();

        foreach (var method in MethodsAcceptingExtra(source))
        {
            var body = MethodBody(source, method);

            // The merge must precede the write, or the field never reaches the event.
            var merge = body.IndexOf("MergeExtra(evt, extra);", StringComparison.Ordinal);
            var write = body.IndexOf("WriteAuditEvent(evt);", StringComparison.Ordinal);

            if (merge < 0 || write < 0 || merge > write)
                offenders.Add(method);
        }

        Assert.True(offenders.Count == 0,
            "these audit methods accept an 'extra' dictionary and silently discard it: "
            + string.Join(", ", offenders));
    }

    [Fact]
    public void TheAuditMethodsUsedForServicing_AcceptExtra()
    {
        // The modules that gained protected-principal servicing audit through these. A method that
        // loses its extra parameter would make servicing unattributable on that module, and the
        // test above cannot see an absence.
        var source = ReadAuditService();
        var accepting = MethodsAcceptingExtra(source).ToHashSet(StringComparer.Ordinal);

        foreach (var required in new[]
        {
            "LogMailboxPermission",     // Mailbox Permissions, Calendar, Out of Office
            "LogCalendarPermission",
            "LogMfaResetAction",        // MFA Reset
            "LogConferenceRoomAction",  // Conference Rooms
            "LogMigrationBatch",        // Migration
            "LogMigrationAction",
            "LogADAttributeEdit",       // AD Attribute Editor
            "LogModuleAction",          // Blocked Senders, Emergency Disable, Comms-10k, groups
            "LogLookupAction",
        })
        {
            Assert.True(accepting.Contains(required),
                $"{required} no longer accepts an 'extra' dictionary, so a serviced action cannot record its authorising group");
        }
    }

    [Fact]
    public void ExtraIsMergedAfterTheComputedFields_SoACallerCanOverrideDeliberately()
    {
        // Ordering is a contract, not an accident: merging before the initialiser would let a
        // computed field silently overwrite what the caller asked to record.
        var source = ReadAuditService();
        var body = MethodBody(source, "LogModuleAction");

        var errorField = body.IndexOf("[\"error\"]", StringComparison.Ordinal);
        var merge = body.IndexOf("MergeExtra(evt, extra);", StringComparison.Ordinal);

        Assert.True(errorField >= 0 && merge > errorField,
            "extra must merge after the computed fields");
    }

    [Fact]
    public void NoWritePath_TestsTheServicedNoteAndThrowsItAway()
    {
        // The other tests in this file guard the audit METHODS. This one guards the CALL SITES,
        // which is where pps-3 happened: the note was computed, used only to decide the allow, and
        // discarded - so a protected principal was modified with nothing in the audit naming who
        // permitted it.
        //
        // The shape is `NoteFor(...) is null` used as a bare condition. NoteFor returns a nullable
        // NOTE rather than a bool precisely so permission and record cannot be separated; testing
        // it inline for null extracts the permission and drops the record. Binding it to a
        // variable first is what makes the note available to the audit call.
        //
        // ONE legitimate exception, allowlisted below: a PREVIEW performs no write and emits no
        // audit event, so there is no record for the note to belong to.
        const string previewExemption = "ADAttributeEditorUndoService.cs";

        var offenders = new List<string>();

        foreach (var file in EnumerateSourceFiles())
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("ProtectedPrincipalServicing.NoteFor", StringComparison.Ordinal))
                continue;

            // NoteFor(...) reached by an `is null` test without first being assigned. Matches
            // across newlines because the call is usually wrapped.
            var inlineNullTests = Regex.Matches(
                text,
                @"ProtectedPrincipalServicing\.NoteFor\((?:[^()]|\([^()]*\))*\)\s*is null",
                RegexOptions.Singleline);

            if (inlineNullTests.Count == 0)
                continue;

            var name = Path.GetFileName(file);
            if (string.Equals(name, previewExemption, StringComparison.OrdinalIgnoreCase))
            {
                // The exemption is narrow: one occurrence, in preview. A second would mean the
                // write path regressed to the discarding shape.
                Assert.True(inlineNullTests.Count == 1,
                    $"{name} has {inlineNullTests.Count} inline NoteFor null-tests; only the "
                    + "no-audit PREVIEW path may discard the note");
                continue;
            }

            offenders.Add(name);
        }

        Assert.True(offenders.Count == 0,
            "these files test the serviced note for null and discard it, so an allowed override "
            + "leaves no audit record of who permitted it: " + string.Join(", ", offenders));
    }

    private static IEnumerable<string> EnumerateSourceFiles()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var services = Path.Combine(dir.FullName, "Services");
            var pages = Path.Combine(dir.FullName, "Components", "Pages");
            if (Directory.Exists(services) && Directory.Exists(pages))
            {
                return Directory.EnumerateFiles(services, "*.cs", SearchOption.AllDirectories)
                    .Concat(Directory.EnumerateFiles(pages, "*.razor", SearchOption.AllDirectories));
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Services and Components/Pages from the test base directory.");
    }

    private static IEnumerable<string> MethodsAcceptingExtra(string source)
    {
        // Reflection would only see the signature, not whether the body honours it, so the
        // discard test must read source. Both tests use the same list for consistency.
        foreach (Match m in Regex.Matches(source, @"public (?:virtual )?void (Log\w+)\("))
        {
            var name = m.Groups[1].Value;
            var body = MethodBody(source, name);
            if (body.Contains("Dictionary<string, object?>? extra = null", StringComparison.Ordinal))
                yield return name;
        }
    }

    private static string MethodBody(string source, string methodName)
    {
        var signature = Regex.Match(source, $@"public (?:virtual )?void {Regex.Escape(methodName)}\(");
        Assert.True(signature.Success, $"audit method '{methodName}' not found");

        var start = signature.Index;
        var next = Regex.Match(source[(start + signature.Length)..], @"\n    (public|private|protected)\s");
        return next.Success
            ? source.Substring(start, signature.Length + next.Index)
            : source[start..];
    }

    private static string ReadAuditService()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var path = Path.Combine(dir.FullName, "Services", "AuditService.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate Services/AuditService.cs from the test base directory.");
    }
}
