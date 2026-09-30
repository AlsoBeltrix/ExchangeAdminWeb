using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The Comms-10k replace SEQUENCE (docs/Comms10kBulkResolveScale-Plan.md S3 test 9, S4 tests 17
/// and 18): the module runs no principal-protection check, and the order of resolve, lock,
/// re-read and write is what it claims to be.
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
/// <b>Source-level tripwires, explicitly NOT behavioural coverage.</b> A lock, a runspace and a
/// domain controller cannot be exercised here. The behaviour that CAN be tested - the outcome
/// procedure, the write ordering, the retry classification - lives in
/// <see cref="Comms10kReplaceWriterTests"/> against the pure writer, which is why that writer is
/// pure. These pin the wiring around it.
/// </para>
/// <para>
/// <b>Every tripwire strips comments before matching.</b> This repository has shipped three
/// guards that forbade a named construct and then matched the comment EXPLAINING it, failing
/// against prose rather than code. Both files scanned here carry exactly that hazard: the page
/// explains why there is no protection check, and the lock factory explains why it does not grant
/// FullControl.
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

    // ----- S4 test 18: resolve once, before the lock, and bind everything to that identity ------

    [Fact]
    public void TheTargetIsResolvedBeforeEitherLockIsTaken()
    {
        // Step 0 runs before the lock because its objectGUID IS the lock key. Asserted on
        // position: a resolve that happened after the lock would key the lock on something else.
        var body = MemberBody(ServiceSource(), "private Comms10kUpdateResult RunReplace(");

        var resolve = body.IndexOf("QueryTargetGroup(", StringComparison.Ordinal);
        var semaphore = body.IndexOf("GroupLocks.GetOrAdd(", StringComparison.Ordinal);
        var mutex = body.IndexOf("CreateHostLock(", StringComparison.Ordinal);

        Assert.True(resolve >= 0, "the replace no longer resolves the target group.");
        Assert.True(semaphore > resolve, "the in-process lock is taken before the target is resolved.");
        Assert.True(mutex > resolve, "the host lock is taken before the target is resolved.");
    }

    [Fact]
    public void AResolutionFailure_RefusesBeforeAnyChange()
    {
        var body = MemberBody(ServiceSource(), "private Comms10kUpdateResult RunReplace(");

        var catchBlock = Regex.Match(body,
            @"catch \(Exception ex\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        Assert.True(catchBlock.Success, "the resolution failure path was not found.");

        // Refused(...) is the only constructor of a refused-before-any-change result, and the
        // path must not fall through to the lock or the write.
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

    // ----- S4 test 17: the lock ------------------------------------------------------------------

    [Fact]
    public void TheHostLockIsAGlobalNamedMutexKeyedOnTheObjectGuid()
    {
        var body = MemberBody(ServiceSource(), "private static Mutex CreateHostLock(");

        // Global\, so it spans sessions - dev and prod run as separate processes on one host.
        Assert.Contains("Global\\\\", body, StringComparison.Ordinal);

        // Keyed on the GUID, never the name, so a rename cannot split the lock in two.
        Assert.Contains("objectGuid", body, StringComparison.Ordinal);
        Assert.DoesNotContain("group", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheHostLockIsCreatedWithAnExplicitDescriptor_GrantingOnlyWhatOrderingNeeds()
    {
        var body = MemberBody(ServiceSource(), "private static Mutex CreateHostLock(");

        // Default security would make the mutex unopenable by the other app-pool identity, so
        // the lock would quietly become per-instance again - the exact failure it prevents,
        // while looking like it worked.
        Assert.Contains("MutexSecurity", body, StringComparison.Ordinal);
        Assert.Contains("MutexAcl.Create", body, StringComparison.Ordinal);

        // Granted by a RULE, not by naming an account: no source file knows both pool
        // identities and naming either would break environment neutrality.
        Assert.Contains("WellKnownSidType.AuthenticatedUserSid", body, StringComparison.Ordinal);

        // Synchronize | Modify and nothing more. FullControl would let any holder rewrite the
        // descriptor and lock the other instance out.
        Assert.Contains("MutexRights.Synchronize", body, StringComparison.Ordinal);
        Assert.Contains("MutexRights.Modify", body, StringComparison.Ordinal);
        Assert.DoesNotContain("FullControl", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheInProcessLockIsTakenFirst_AndIsKeyedOnTheObjectGuid()
    {
        // Taking the host mutex first would let one process hold a machine-wide lock while it
        // queues on its own semaphore, blocking the other instance behind purely local
        // contention.
        var body = MemberBody(ServiceSource(), "private Comms10kUpdateResult RunReplace(");

        Assert.Contains("GroupLocks.GetOrAdd(target.ObjectGuid", body, StringComparison.Ordinal);

        // And the host lock with the SAME key. Asserted at the CALL SITE, not only inside the
        // factory: a factory that keys correctly on whatever it is handed is satisfied by a
        // caller handing it the wrong thing, and a mutation probe that changed exactly that
        // passed every other guard here.
        Assert.Contains("CreateHostLock(target.ObjectGuid)", body, StringComparison.Ordinal);

        Assert.True(
            body.IndexOf("semaphore.Wait(", StringComparison.Ordinal)
                < body.IndexOf("mutex.WaitOne(", StringComparison.Ordinal),
            "the host mutex is taken before the in-process semaphore.");
    }

    [Fact]
    public void AFailureToAcquireEitherLockRefusesBeforeTheClear_AndNeverProceedsUnlocked()
    {
        var body = MemberBody(ServiceSource(), "private Comms10kUpdateResult RunReplace(");

        // Bounded waits, never WaitOne() forever: a replace is a foreground operation behind a
        // spinner.
        Assert.Contains("semaphore.Wait(LockTimeout)", body, StringComparison.Ordinal);
        Assert.Contains("mutex.WaitOne(LockTimeout)", body, StringComparison.Ordinal);
        Assert.DoesNotContain("WaitOne()", body, StringComparison.Ordinal);

        // Both timeouts refuse. Neither falls through to WriteUnderLock.
        var semaphoreTimeout = Regex.Match(body,
            @"if \(!semaphore\.Wait\(LockTimeout\)\)\s*return Refused\(");
        Assert.True(semaphoreTimeout.Success, "an in-process lock timeout no longer refuses.");

        var mutexTimeout = Regex.Match(body,
            @"if \(!held\)\s*\{[^}]*return Refused\(", RegexOptions.Singleline);
        Assert.True(mutexTimeout.Success, "a host lock timeout no longer refuses.");
    }

    [Fact]
    public void AnAbandonedLockIsLoggedAndThenProceeds()
    {
        // The previous holder died mid-fill, so the list may be half-written. Clear-then-fill is
        // self-correcting and the recovery IS the operation - but swallowing this silently would
        // erase the only trace that a list was ever left incomplete.
        var body = MemberBody(ServiceSource(), "private Comms10kUpdateResult RunReplace(");

        var block = Regex.Match(body,
            @"catch \(AbandonedMutexException\)\s*\{(?<body>.*?)\n                \}", RegexOptions.Singleline);
        Assert.True(block.Success, "the abandoned-mutex path was not found.");

        Assert.Contains("held = true", block.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Contains("LogWarning", block.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLockIsReleasedInAFinally()
    {
        // On the same thread that took it, or the next run inherits an abandoned lock for no
        // reason at all.
        var body = MemberBody(ServiceSource(), "private Comms10kUpdateResult RunReplace(");

        var finallyBlocks = Regex.Matches(body, @"finally\s*\{(?<body>[^}]*)\}");
        var released = finallyBlocks.Any(m => m.Groups["body"].Value.Contains("ReleaseMutex", StringComparison.Ordinal));
        var semaphoreReleased = finallyBlocks.Any(m => m.Groups["body"].Value.Contains("semaphore.Release", StringComparison.Ordinal));

        Assert.True(released, "the host mutex is not released in a finally.");
        Assert.True(semaphoreReleased, "the in-process semaphore is not released in a finally.");
    }

    // ----- S4: the post-lock re-read, and where the point of no return is -----------------------

    [Fact]
    public void TheTargetIsReReadByGuidUnderTheLock_BeforeTheClear()
    {
        // An unbounded wait can sit between resolving and writing, and the Constitution requires
        // a re-read before write where practical. A failure here is still "refused before any
        // change" - it is the LAST point at which that claim is true.
        var body = MemberBody(ServiceSource(), "private Comms10kUpdateResult WriteUnderLock(");

        var reRead = body.IndexOf("ReadTargetByGuid(", StringComparison.Ordinal);
        var execute = body.IndexOf("Comms10kReplaceWriter.ExecuteUnderLock(", StringComparison.Ordinal);

        Assert.True(reRead >= 0, "the post-lock re-read is gone.");
        Assert.True(execute > reRead, "the write is issued before the target is re-read.");

        var failure = Regex.Match(body, @"catch \(Exception ex\)\s*\{(?<body>.*?)\n        \}", RegexOptions.Singleline);
        Assert.True(failure.Success, "the re-read failure path was not found.");
        Assert.Contains("return Refused(", failure.Groups["body"].Value, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWriteBindsToTheReReadIdentity_NotTheConfiguredName()
    {
        // A rename or a move while we queued for the lock retargets nothing: the GUID is what
        // the module is bound to, and the DN it writes is the one the directory returns now.
        var body = MemberBody(ServiceSource(), "private Comms10kUpdateResult WriteUnderLock(");

        Assert.Contains("ClearMembers(ps, credential, confirmed)", body, StringComparison.Ordinal);
        Assert.Contains("AddMemberBatch(ps, credential, confirmed, batch)", body, StringComparison.Ordinal);
        Assert.Contains("ReadBackMembers(ps, credential, confirmed)", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheClearAndEveryAddAreRetried_ButTheReadBackIsNot()
    {
        // Retry is per write OPERATION. The read-back is not a write; retrying it would only
        // delay the could-not-confirm answer the operator needs.
        var body = MemberBody(ServiceSource(), "private Comms10kUpdateResult WriteUnderLock(");

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
        var source = StripComments(PageSource());

        // The outcome rides the audit's TARGET field, because LogModuleAction drops errorDetail
        // on success - so "succeeded with exceptions" would otherwise look identical to a clean
        // success in the trail.
        Assert.Matches(
            new Regex(@"LogModuleAction\((?:[^;]*?)outcome \{result\.Outcome\}", RegexOptions.Singleline),
            source);

        Assert.Contains("[\"Outcome\"] = result.Outcome.ToString()", source, StringComparison.Ordinal);

        // Absent rather than guessed when the read-back failed.
        Assert.Contains("result.FinalCount?.ToString() ?? \"UNKNOWN", source, StringComparison.Ordinal);
    }

    // ----- harness -------------------------------------------------------------------------

    private static string PageSource() =>
        File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "Comms10k.razor"));

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
