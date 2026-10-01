using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The Comms-10k replace SEQUENCE (docs/Comms10kBulkResolveScale-Plan.md S3 test 9, S4 tests 17
/// test 18): the module runs no principal-protection check, is not serialised, and the order
/// of resolve, re-read and write is what it claims to be.
/// </summary>
/// <remarks>
/// <para>
/// <b>The absence of the protection check is a recorded owner ruling, not a gap.</b> Owner ruling
/// 2026-09-30, <c>.agents/decisions.md</c>, carried by <c>docs/ProjectConstitution.md</c> as a
/// named scoped exception: the module exists only to replace the membership of one broadcast
/// list that carries recipient counts past Microsoft's per-message limits, membership of it
/// grants access to nothing, and the member check cost a runspace and an AD-module import per
/// member. The tripwires exist so a later sweep that "restores" the check, or re-adds the
/// servicer opt-in that only makes sense alongside it, has to read the ruling first.
/// </para>
/// <para>
/// <b>Source-level tripwires, explicitly NOT behavioural coverage.</b> A runspace and a
/// domain controller cannot be exercised here. The behaviour that CAN be tested - the outcome
/// procedure, the write ordering, the retry classification - lives in
/// <see cref="Comms10kReplaceWriterTests"/> against the pure writer, which is why that writer is
/// pure. These pin the wiring around it.
/// </para>
/// <para>
/// <b>Every tripwire strips comments before matching.</b> This repository has shipped three
/// guards that forbade a named construct and then matched the comment EXPLAINING it, failing
/// against prose rather than code. Both files scanned here carry exactly that hazard: the page
/// explains why there is no protection check, and the service explains why the write is not
/// serialised - both in prose naming the very constructs the guards forbid.
/// </para>
/// </remarks>
public class Comms10kReplaceSequenceTests
{
    // ----- S3 test 9: no protection check, and no servicer grant that would have nothing to do --

    [Fact]
    public void ThePage_HoldsNoPrincipalProtectionReference()
    {
        var source = StripComments(PageSource());

        // Neither service may be injected and neither may be called. Anchored on the type names
        // rather than on a single call shape, because an injected service that is never used is
        // still the check coming back in the next edit.
        Assert.DoesNotContain("ProtectedPrincipalService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtectedPrincipalServicerService", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ProtectedPrincipalServicing", source, StringComparison.Ordinal);

        // The servicer module id is what pairs a page gate with a grant. With no gate, an id here
        // would be the unreachable-capability defect this repo has shipped twice.
        Assert.DoesNotContain("ServicerModuleId", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServicerOptInSet_DoesNotOfferAGrantForAModuleWithNoGate()
    {
        var moduleConfig = StripComments(
            File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "ModuleConfig.razor")));

        var declaration = Regex.Match(moduleConfig,
            @"ModulesWithProtectedPrincipalServicing\s*=\s*new\([^)]*\)\s*\{(?<members>[^}]*)\}");
        Assert.True(declaration.Success, "the servicer opt-in list is no longer an explicit set literal.");

        Assert.DoesNotContain("\"Comms10k\"", declaration.Groups["members"].Value, StringComparison.Ordinal);

        // Asserted against a module that IS gated, so an empty or unparsed match cannot pass this
        // test by matching nothing.
        Assert.Contains("\"GroupManagement\"", declaration.Groups["members"].Value, StringComparison.Ordinal);
    }

    // ----- S4 test 18: resolve once, and bind every operation to that identity -----------------

    [Fact]
    public void TheTargetIsResolvedBeforeTheWriteIsEntered()
    {
        // Asserted on position: the write must be handed an identity that has already been
        // resolved, never the configured name to resolve for itself.
        var body = MemberBody(ServiceSource(), "private Comms10kUpdateResult RunReplace(");

        var resolve = body.IndexOf("QueryTargetGroup(", StringComparison.Ordinal);
        var write = body.IndexOf("PerformReplace(", StringComparison.Ordinal);

        Assert.True(resolve >= 0, "the replace no longer resolves the target group.");
        Assert.True(write > resolve, "the write is entered before the target is resolved.");
        Assert.Contains("PerformReplace(group, target, resolvedDns, creds)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void ReplacesAreNotSerialised_AndTheCodeSaysSoRatherThanLeavingItToBeInferred()
    {
        // Owner ruling 2026-09-30: the serialisation this sequence was first built with was
        // never asked for and is removed. The risk it covered is real and accepted - two
        // concurrent replaces can interleave and leave the list holding neither file - so this
        // pins that the removal is deliberate and recorded, not an omission somebody should
        // "fix" on sight. A later owner ruling may put a lock back; this test goes with it.
        var source = ServiceSource();
        var stripped = StripComments(source);

        Assert.DoesNotContain("Mutex", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("SemaphoreSlim", stripped, StringComparison.Ordinal);

        // And the reason is written where the next reader of this method will find it.
        Assert.Contains("Replaces are NOT serialised", source, StringComparison.Ordinal);
        Assert.Contains(".agents/decisions.md", source, StringComparison.Ordinal);
    }

    [Fact]
    public void AResolutionFailure_RefusesBeforeAnyChange()
    {
        var body = MemberBody(ServiceSource(), "private Comms10kUpdateResult RunReplace(");

        var catchBlock = Regex.Match(body,
            @"catch \(Exception ex\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        Assert.True(catchBlock.Success, "the resolution failure path was not found.");

        // Refused(...) is the only constructor of a refused-before-any-change result, and the
        // path must not fall through to the write.
        Assert.Contains("return Refused(", catchBlock.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalHelperProducesRefusedBeforeAnyChange_AndNeverAReadBack()
    {
        var body = MemberBody(ServiceSource(), "private static Comms10kUpdateResult Refused(");

        // It routes through the one procedure that derives outcomes, with the refusal reason
        // set and no observed membership - which is what makes it "refused", not the wording.
        Assert.Contains("Comms10kReplaceWriter.Derive(", body, StringComparison.Ordinal);
        Assert.Contains("observed: null", body, StringComparison.Ordinal);
    }

    // ----- S4: the re-read, and where the point of no return is ---------------------------------

    [Fact]
    public void TheTargetIsReReadByGuid_ImmediatelyBeforeTheClear()
    {
        // The Constitution requires a re-read before write where practical, and here it is one
        // cheap query that also proves the object still exists. A failure here is still
        // "refused before any change" - it is the LAST point at which that claim is true.
        var body = MemberBody(ServiceSource(), "private Comms10kUpdateResult PerformReplace(");

        var reRead = body.IndexOf("ReadTargetByGuid(", StringComparison.Ordinal);
        var execute = body.IndexOf("Comms10kReplaceWriter.ExecuteSequence(", StringComparison.Ordinal);

        Assert.True(reRead >= 0, "the re-read before the write is gone.");
        Assert.True(execute > reRead, "the write is issued before the target is re-read.");

        var failure = Regex.Match(body, @"catch \(Exception ex\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        Assert.True(failure.Success, "the re-read failure path was not found.");
        Assert.Contains("return Refused(", failure.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWriteBindsToTheReReadIdentity_NotTheConfiguredName()
    {
        // A rename or a move between the resolve and the write retargets nothing: the GUID is what
        // the module is bound to, and the DN it writes is the one the directory returns now.
        var body = MemberBody(ServiceSource(), "private Comms10kUpdateResult PerformReplace(");

        Assert.Contains("ClearMembers(ps, credential, confirmed)", body, StringComparison.Ordinal);
        Assert.Contains("AddMemberBatch(ps, credential, confirmed, batch)", body, StringComparison.Ordinal);
        Assert.Contains("ReadBackMembers(ps, credential, confirmed)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClearAndEveryAddAreRetried_ButTheReadBackIsNot()
    {
        // Retry is per write OPERATION. The read-back is not a write; retrying it would only
        // delay the could-not-confirm answer the operator needs.
        var body = MemberBody(ServiceSource(), "private Comms10kUpdateResult PerformReplace(");

        Assert.Contains("clear: () => WithRetry(", body, StringComparison.Ordinal);
        Assert.Contains("addBatch: batch => WithRetry(", body, StringComparison.Ordinal);
        Assert.Contains("readBack: () => ReadBackMembers(", body, StringComparison.Ordinal);
    }

    // ----- S4 test 16: the upload guard names its limit -----------------------------------------

    [Fact]
    public void AnOversizedUploadNamesTheLimitRatherThanTheRawStreamError()
    {
        // It is a malformed-upload guard, not a membership cap, and the message has to say so:
        // reported as a raw stream error it reads as "your file is corrupt" to an operator whose
        // file is fine.
        var source = StripComments(PageSource());

        Assert.Contains("UploadSizeLimitBytes", source, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"catch \(IOException ex\) when \([^)]*exceeds the maximum"), source);
        Assert.Contains("upload limit", source, StringComparison.Ordinal);
        Assert.Contains("not against a large membership", source, StringComparison.Ordinal);
    }

    // ----- S4 test 15: the five outcomes reach the operator, the audit and the notification -----

    [Fact]
    public void ThePageBranchesOnTheOutcome_NotOnTheBool()
    {
        // "Partly applied" and "could not confirm" are both Success false and mean entirely
        // different things. A banner keyed on the bool would collapse them into one red box.
        var source = StripComments(PageSource());

        Assert.Contains("AlertClassFor(result.Outcome)", source, StringComparison.Ordinal);
        Assert.Contains("HeadlineFor(result.Outcome)", source, StringComparison.Ordinal);

        foreach (var outcome in Enum.GetNames<ExchangeAdminWeb.Services.Comms10kOutcome>().Where(n => n != "RefusedBeforeAnyChange"))
            Assert.Contains($"Comms10kOutcome.{outcome}", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAuditAndTheNotificationBothCarryTheOutcome()
    {
        // THE RULE IS UNCHANGED; THE WRITE MOVED to the background runner on 2026-10-01, so
        // both channels are now the processor's. Reading the page here would have passed on
        // an empty page and proved nothing.
        var source = StripComments(ProcessorSource());

        // The outcome must survive a SUCCESSFUL operation. LogModuleAction discards
        // errorDetail on success, so "succeeded with exceptions" - the one success an operator
        // may need to act on - would otherwise look identical to a clean one in the trail.
        // `extra` is the channel that survives; on the page it was the target field. Either is
        // fine, carrying it nowhere is not.
        Assert.Contains("[\"Outcome\"] = result.Outcome.ToString()", source, StringComparison.Ordinal);

        // The notification carries it too, with the counts.
        Assert.Contains("[\"Outcome\"] = outcome", source, StringComparison.Ordinal);

        // Absent rather than guessed when the read-back failed - which is precisely the moment
        // an invented number would be believed. Asserted on both channels.
        var unknowns = Regex.Matches(source, @"FinalCount\?\.ToString\(\) \?\? ""UNKNOWN").Count;
        Assert.True(unknowns >= 2,
            $"the unread final count must stay explicit on both the audit and the "
            + $"notification; found {unknowns} of 2.");
    }

    // ----- harness -------------------------------------------------------------------------

    private static string PageSource() =>
        File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "Comms10k.razor"));

    private static string ProcessorSource() =>
        File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "Jobs", "Comms10kReplaceProcessor.cs"));

    private static string ServiceSource() =>
        File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "Comms10kService.cs"));

    /// <summary>
    /// Removes Razor (@* *@), block and line comments, so a guard against a named construct
    /// cannot be satisfied - or defeated - by the comment that explains it.
    /// </summary>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"@\*.*?\*@", " ", RegexOptions.Singleline);
        source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(source, @"//[^\r\n]*", " ");
    }

    /// <summary>The brace-matched body of the named member, comments already stripped.</summary>
    private static string MemberBody(string source, string signatureFragment)
    {
        var start = source.IndexOf(signatureFragment, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{signatureFragment}' not found in source.");

        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"No body brace found after '{signatureFragment}'.");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0)
                return StripComments(source[(open + 1)..i]);
        }

        Assert.Fail($"Unbalanced braces in the body of '{signatureFragment}'.");
        return string.Empty;
    }
}
