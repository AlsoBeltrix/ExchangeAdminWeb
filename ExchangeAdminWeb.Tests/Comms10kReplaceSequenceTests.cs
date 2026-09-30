using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Comms-10k Slice 3 (docs/Comms10kBulkResolveScale-Plan.md, tests 9 and 18): the module runs no
/// principal-protection check of either kind, and its write binds to the resolved immutable
/// identity of the target group rather than to the configured name.
/// </summary>
/// <remarks>
/// <para>
/// <b>The absence of the protection check is a recorded owner ruling, not a gap.</b> Owner ruling
/// 2026-09-30, <c>.agents/decisions.md</c>, carried by <c>docs/ProjectConstitution.md</c> as a
/// named scoped exception: this module exists only to replace the membership of one broadcast
/// list that carries recipient counts past Microsoft's per-message limits, membership of it grants
/// access to nothing, and the member check cost a runspace and an AD-module import per member -
/// at ten thousand members the module's dominant cost. The tripwires below exist so a later sweep
/// that re-adds the check, or re-adds the servicer opt-in that only makes sense alongside it, has
/// to read the ruling first rather than "restoring" it as an obvious omission.
/// </para>
/// <para>
/// <b>The source tripwires strip comments before matching.</b> This repository has shipped three
/// guards that forbade a named antipattern and then matched the comment EXPLAINING the
/// antipattern, so they failed against prose rather than code. The explanatory comment in
/// <c>Comms10k.razor</c> is exactly that hazard here.
/// </para>
/// </remarks>
public class Comms10kReplaceSequenceTests
{
    // ----- test 9: the module holds no protection check, and advertises no servicer grant -----

    [Fact]
    public void ThePage_HoldsNoPrincipalProtectionReference()
    {
        var source = StripComments(
            File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "Comms10k.razor")));

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

    // ----- test 18: the write binds to the resolved identity, and resolution runs first -----

    [Fact]
    public void AResolutionFailure_RefusesBeforeAnythingIsWritten()
    {
        // The dividing line is attempted-or-not, never the error text: if resolution threw, the
        // write was never reached and the membership is provably untouched.
        var applyCalls = 0;

        var outcome = Comms10kService.RunReplace(
            "Broadcast-List",
            Dns(3),
            () => throw new InvalidOperationException("'Broadcast-List' matched 2 groups."),
            (_, _) => { applyCalls++; return 0; });

        Assert.False(outcome.Success);
        Assert.Equal(0, applyCalls);
        Assert.Contains("nothing was changed", outcome.Message, StringComparison.Ordinal);
        Assert.Contains("matched 2 groups", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWriteTargetsTheResolvedIdentity_NotTheConfiguredName()
    {
        // A rename, or a same-named object in another container, between the resolve and the write
        // would otherwise retarget the write mid-sequence. The Constitution requires a directory
        // mutation to bind to an immutable identifier.
        const string dn = "CN=Broadcast-List,OU=Groups,DC=example,DC=test";
        var guid = Guid.NewGuid();
        var seen = new List<Comms10kTarget>();

        var outcome = Comms10kService.RunReplace(
            "Broadcast-List",
            Dns(4),
            () => new Comms10kTarget(dn, guid),
            (target, dns) => { seen.Add(target); return dns.Count + 1; });

        Assert.True(outcome.Success);
        var target = Assert.Single(seen);
        Assert.Equal(dn, target.DistinguishedName);
        Assert.Equal(guid, target.ObjectGuid);
    }

    [Fact]
    public void TheTargetIsResolvedExactlyOnce()
    {
        var resolveCalls = 0;

        Comms10kService.RunReplace(
            "Broadcast-List",
            Dns(10),
            () => { resolveCalls++; return Target(); },
            (_, dns) => dns.Count);

        Assert.Equal(1, resolveCalls);
    }

    [Fact]
    public void ASuccessReportsTheCountReadBeforeTheWriteAndTheCountWritten()
    {
        var outcome = Comms10kService.RunReplace(
            "Broadcast-List",
            Dns(7),
            Target,
            (_, _) => 42);

        Assert.True(outcome.Success);
        Assert.Equal(42, outcome.InitialCount);
        Assert.Equal(7, outcome.FinalCount);
        Assert.Contains("7 members (was 42)", outcome.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AWriteFailure_IsReportedAsAWriteFailure_NotAsARefusal()
    {
        // Distinct from the resolution refusal above: this one reached the directory, so the
        // message must not claim nothing was changed.
        var outcome = Comms10kService.RunReplace(
            "Broadcast-List",
            Dns(3),
            Target,
            (_, _) => throw new InvalidOperationException("access denied"));

        Assert.False(outcome.Success);
        Assert.Contains("Failed to update group", outcome.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("nothing was changed", outcome.Message, StringComparison.Ordinal);
    }

    // ----- harness -------------------------------------------------------------------------

    private static List<string> Dns(int count) =>
        Enumerable.Range(0, count).Select(i => $"CN=User{i},OU=People,DC=example,DC=test").ToList();

    private static Comms10kTarget Target() =>
        new("CN=Broadcast-List,OU=Groups,DC=example,DC=test", Guid.NewGuid());

    /// <summary>
    /// Removes Razor (@* *@), block and line comments, so a guard against a named construct cannot
    /// be satisfied by the comment that explains why the construct is absent.
    /// </summary>
    private static string StripComments(string source)
    {
        source = Regex.Replace(source, @"@\*.*?\*@", " ", RegexOptions.Singleline);
        source = Regex.Replace(source, @"/\*.*?\*/", " ", RegexOptions.Singleline);
        return Regex.Replace(source, @"//[^\r\n]*", " ");
    }
}
