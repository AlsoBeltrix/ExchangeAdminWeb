using System.Security.Claims;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// S1 of docs/ProtectedGroupWriteTarget-Plan.md: the write-target question ("may members be
/// added to or removed from this GROUP?") as a separate rule set from principal protection.
///
/// The rule set is Users + SamAccountNamePatterns + OrganizationalUnits + the new Protected
/// Targets list; the Groups list is EXCLUDED by design (AC8 anti-lockout - a group listed to
/// protect its MEMBERS stays manageable; plan Revision 2026-08-28). CheckWriteTarget is
/// deterministic and directory-free, so these are real behaviour tests against the real store,
/// not tripwires: config is written through SaveConfig (the admin page's save path) and read
/// back through the repository, per AC7's through-the-store requirement.
/// </summary>
public sealed class ProtectedGroupWriteTargetTests : IDisposable
{
    private const string TargetGuid = "11111111-2222-3333-4444-555555555555";
    private const string TargetDn = "CN=Domain Admins,CN=Users,DC=ad,DC=analog,DC=com";
    private const string ServicerSid = "S-1-5-21-1-2-3-4001";

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"pgwt-{Guid.NewGuid():N}");

    public ProtectedGroupWriteTargetTests()
    {
        Directory.CreateDirectory(_tempDir);
        Directory.CreateDirectory(Path.Combine(_tempDir, "config"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ----- ProtectedGroupTargetEntry: the stored "guid|dn" value -----

    [Fact]
    public void Entry_FormatAndParse_RoundTrip()
    {
        var stored = ProtectedGroupTargetEntry.Format(TargetGuid, TargetDn);
        var entry = ProtectedGroupTargetEntry.Parse(stored);

        Assert.Equal(TargetGuid, entry.ObjectGuid);
        Assert.Equal(TargetDn, entry.DistinguishedName);
        Assert.Equal(TargetDn, entry.Label);
    }

    [Fact]
    public void Entry_WithoutSeparator_MatchesAsGuidOrDn()
    {
        Assert.Equal(TargetGuid, ProtectedGroupTargetEntry.Parse(TargetGuid).ObjectGuid);
        Assert.Equal(TargetDn, ProtectedGroupTargetEntry.Parse(TargetDn).DistinguishedName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Entry_Blank_MatchesNothing(string? stored)
    {
        var entry = ProtectedGroupTargetEntry.Parse(stored);

        Assert.False(entry.Matches(Group(dn: TargetDn, guid: TargetGuid)));
    }

    [Fact]
    public void Entry_Matches_GuidFirst_DnAsFallback_CaseInsensitive()
    {
        var entry = ProtectedGroupTargetEntry.Parse($"{TargetGuid}|{TargetDn}");

        // GUID match survives a rename/move (different DN).
        Assert.True(entry.Matches(Group(dn: "CN=Renamed,OU=Elsewhere,DC=ad,DC=analog,DC=com", guid: TargetGuid.ToUpperInvariant())));
        // DN fallback catches a snapshot with no GUID.
        Assert.True(entry.Matches(Group(dn: TargetDn.ToLowerInvariant(), guid: null)));
        // Neither identifier -> no match.
        Assert.False(entry.Matches(Group(dn: "CN=Other,DC=ad,DC=analog,DC=com", guid: "99999999-8888-7777-6666-555555555555")));
    }

    // ----- CheckWriteTarget: rule coverage through the real store (AC7/AC9/AC8/AC5) -----

    [Fact]
    public void TargetList_SavedThroughTheStore_RefusesTheGroup()
    {
        var service = CreateRealService();
        service.SaveConfig(new ProtectedPrincipalConfig
        {
            GroupTargets = [ProtectedGroupTargetEntry.Format(TargetGuid, TargetDn)],
        });

        var result = service.CheckWriteTarget(Group(dn: TargetDn, guid: TargetGuid));

        Assert.True(result.IsProtected);
        Assert.False(result.CheckFailed);
        Assert.Contains($"Target:{TargetDn}", result.MatchedRules);
    }

    [Fact]
    public void PatternRule_RefusesATarget_WithoutAnyTargetListEntry()
    {
        // AC9 / pgwt-2: a group protected by sAMAccountName PATTERN must be refused - the rule
        // three snapshot identifiers make reachable, and the one a bare DN silently skips.
        var service = CreateRealService();
        service.SaveConfig(new ProtectedPrincipalConfig { SamAccountNamePatterns = ["adm-*"] });

        var result = service.CheckWriteTarget(Group(dn: "CN=adm-tier0,OU=Groups,DC=ad,DC=analog,DC=com", guid: TargetGuid, sam: "adm-tier0"));

        Assert.True(result.IsProtected);
        Assert.Contains("Pattern:adm-*", result.MatchedRules);
    }

    [Fact]
    public void OuRule_RefusesATargetInsideAProtectedOu()
    {
        var service = CreateRealService();
        service.SaveConfig(new ProtectedPrincipalConfig { OrganizationalUnits = ["OU=Tier0,DC=ad,DC=analog,DC=com"] });

        var result = service.CheckWriteTarget(Group(dn: "CN=SomeGroup,OU=Tier0,DC=ad,DC=analog,DC=com", guid: TargetGuid));

        Assert.True(result.IsProtected);
        Assert.Contains("OU:OU=Tier0,DC=ad,DC=analog,DC=com", result.MatchedRules);
    }

    [Fact]
    public void UsersRule_DirectIdentityEntry_RefusesTheTarget()
    {
        var service = CreateRealService();
        service.SaveConfig(new ProtectedPrincipalConfig { Users = [TargetDn] });

        var result = service.CheckWriteTarget(Group(dn: TargetDn, guid: TargetGuid));

        Assert.True(result.IsProtected);
        Assert.Contains($"User:{TargetDn}", result.MatchedRules);
    }

    [Fact]
    public void GroupsList_AloneDoesNotMakeATargetProtected_ButTheTargetListDoes()
    {
        // AC8, load-bearing: a group listed under Groups protects its MEMBERS; it must stay
        // manageable as a WRITE TARGET unless it is ALSO on the target list.
        var service = CreateRealService();
        service.SaveConfig(new ProtectedPrincipalConfig { Groups = [TargetDn] });

        var listedOnly = service.CheckWriteTarget(Group(dn: TargetDn, guid: TargetGuid));
        Assert.False(listedOnly.IsProtected);
        Assert.False(listedOnly.CheckFailed);

        service.SaveConfig(new ProtectedPrincipalConfig
        {
            Groups = [TargetDn],
            GroupTargets = [ProtectedGroupTargetEntry.Format(TargetGuid, TargetDn)],
        });
        var alsoTargeted = service.CheckWriteTarget(Group(dn: TargetDn, guid: TargetGuid));
        Assert.True(alsoTargeted.IsProtected);
    }

    [Fact]
    public void UnconfiguredStore_LeavesTargetsUnprotected()
    {
        var result = CreateRealService().CheckWriteTarget(Group(dn: TargetDn, guid: TargetGuid));

        Assert.False(result.IsProtected);
        Assert.False(result.CheckFailed);
    }

    [Fact]
    public void MalformedTargetEntry_NeitherCrashesNorMatches()
    {
        var service = CreateRealService();
        service.SaveConfig(new ProtectedPrincipalConfig { GroupTargets = ["garbage-that-is-neither-guid-nor-this-dn"] });

        var result = service.CheckWriteTarget(Group(dn: TargetDn, guid: TargetGuid));

        Assert.False(result.IsProtected);
        Assert.False(result.CheckFailed);
    }

    [Fact]
    public void ConfigLoadError_FailsTheTargetCheckClosed()
    {
        // AC5: an unreadable protection store denies; it never reads as "not protected".
        var service = new LoadErrorPpService(CreateRealService);

        var result = service.CheckWriteTarget(Group(dn: TargetDn, guid: TargetGuid));

        Assert.True(result.CheckFailed);
        Assert.False(result.IsProtected);
    }

    // ----- Repository: the new kind round-trips beside the four existing lists -----

    [Fact]
    public void Repository_RoundTripsGroupTargets()
    {
        var repo = TestConfigStore.CreateProtectedPrincipal(_tempDir);
        repo.Save(new ProtectedPrincipalData(
            ["u@x.com"], ["CN=G,DC=x"], ["OU=O,DC=x"], ["adm-*"],
            [ProtectedGroupTargetEntry.Format(TargetGuid, TargetDn)]));

        Assert.True(repo.TryRead(out var data, out var configured));
        Assert.True(configured);
        Assert.Equal([ProtectedGroupTargetEntry.Format(TargetGuid, TargetDn)], data.GroupTargets);
        Assert.Equal(["u@x.com"], data.Users);
    }

    // ----- ForWriteTarget: the shared gate's servicer and fail-closed semantics (AC3/AC5) -----

    [Fact]
    public void Gate_UnprotectedTarget_Allows()
    {
        var (pp, servicers) = GateHarness(servicerGroups: null, verdict: ProtectedPrincipalResult.NotProtected());

        var decision = ProtectedPrincipalServicing.ForWriteTarget(pp, servicers, Group(TargetDn, TargetGuid), UserIn(ServicerSid), "GroupManagement");

        Assert.True(decision.Allowed);
        Assert.Null(decision.ServicedNote);
    }

    [Fact]
    public void Gate_ProtectedTarget_RefusesAnOrdinaryOperator_WithNoVerbatimReason()
    {
        var (pp, servicers) = GateHarness(servicerGroups: null,
            verdict: ProtectedPrincipalResult.Protected("protected", "Target:CN=Domain Admins"));

        var decision = ProtectedPrincipalServicing.ForWriteTarget(pp, servicers, Group(TargetDn, TargetGuid), UserIn(ServicerSid), "GroupManagement");

        Assert.False(decision.Allowed);
        Assert.Null(decision.FailReason); // the caller supplies its audience's wording
        Assert.Null(decision.ServicedNote);
    }

    [Fact]
    public void Gate_AuthorisedServicer_IsAllowed_AndTheNoteNamesEverything()
    {
        var (pp, servicers) = GateHarness(servicerGroups: [ServicerSid],
            verdict: ProtectedPrincipalResult.Protected("protected", "Target:CN=Domain Admins"));

        var decision = ProtectedPrincipalServicing.ForWriteTarget(pp, servicers, Group(TargetDn, TargetGuid), UserIn(ServicerSid), "GroupManagement");

        Assert.True(decision.Allowed);
        Assert.NotNull(decision.ServicedNote);
        // AC3: the note names the authorising group, the rules overridden, and that it was the
        // WRITE TARGET being serviced - distinguishable from a serviced member in the audit.
        Assert.Contains(ServicerSid, decision.ServicedNote!, StringComparison.Ordinal);
        Assert.Contains("Target:CN=Domain Admins", decision.ServicedNote!, StringComparison.Ordinal);
        Assert.Contains("write target", decision.ServicedNote!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Gate_NullActingUser_Refuses_EvenWithAGrantConfigured()
    {
        var (pp, servicers) = GateHarness(servicerGroups: [ServicerSid],
            verdict: ProtectedPrincipalResult.Protected("protected", "Target:CN=Domain Admins"));

        var decision = ProtectedPrincipalServicing.ForWriteTarget(pp, servicers, Group(TargetDn, TargetGuid), actingUser: null, "GroupManagement");

        Assert.False(decision.Allowed);
        Assert.Null(decision.ServicedNote);
    }

    [Fact]
    public void Gate_AGrantInAnotherModule_ConfersNothing()
    {
        var (pp, servicers) = GateHarness(servicerGroups: null, otherModuleServicerGroups: [ServicerSid],
            verdict: ProtectedPrincipalResult.Protected("protected", "Target:CN=Domain Admins"));

        var decision = ProtectedPrincipalServicing.ForWriteTarget(pp, servicers, Group(TargetDn, TargetGuid), UserIn(ServicerSid), "GroupManagement");

        Assert.False(decision.Allowed);
    }

    [Fact]
    public void Gate_FailedCheck_DeniesWithTheReason_EvenForAServicer()
    {
        var (pp, servicers) = GateHarness(servicerGroups: [ServicerSid],
            verdict: ProtectedPrincipalResult.Failed("store unreadable"));

        var decision = ProtectedPrincipalServicing.ForWriteTarget(pp, servicers, Group(TargetDn, TargetGuid), UserIn(ServicerSid), "GroupManagement");

        Assert.False(decision.Allowed);
        Assert.Contains("Protection check failed: store unreadable", decision.FailReason!, StringComparison.Ordinal);
    }

    [Fact]
    public void Gate_ThrowingCheck_DeniesClosed()
    {
        var (pp, servicers) = GateHarness(servicerGroups: [ServicerSid], verdict: null /* throw */);

        var decision = ProtectedPrincipalServicing.ForWriteTarget(pp, servicers, Group(TargetDn, TargetGuid), UserIn(ServicerSid), "GroupManagement");

        Assert.False(decision.Allowed);
        Assert.Contains("Protection check error:", decision.FailReason!, StringComparison.Ordinal);
    }

    // ----- S4: the admin ADD decision produces the stored form enforcement matches (AC7) -----

    [Fact]
    public void Validator_GroupTarget_CanonicalisesToGuidPipeDn()
    {
        var match = new ADSearchResult("Domain Admins", TargetDn, "DomainAdmins", null, null, "Group", ObjectGuid: TargetGuid);

        var decision = ProtectedPrincipalEntryValidator.Decide(
            [], TargetDn, "GroupTarget", new DirectoryValidationResult(DirectoryLookupOutcome.Found, match));

        Assert.True(decision.Accepted);
        Assert.Equal(ProtectedGroupTargetEntry.Format(TargetGuid, TargetDn), decision.ValueToAdd);
    }

    [Fact]
    public void Validator_GroupTarget_RefusesWhenTheDirectoryGaveNoGuid()
    {
        // The immutable id is the entry's identity (T0); a DN-only row would silently
        // un-protect on a rename, so a GUID-less Found match is refused, not degraded.
        var match = new ADSearchResult("Domain Admins", TargetDn, "DomainAdmins", null, null, "Group");

        var decision = ProtectedPrincipalEntryValidator.Decide(
            [], TargetDn, "GroupTarget", new DirectoryValidationResult(DirectoryLookupOutcome.Found, match));

        Assert.False(decision.Accepted);
        Assert.Contains("identifier could not be read", decision.ErrorMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_GroupTarget_RefusesAnAmbiguousMatch()
    {
        // pgwt-4: an ambiguous typed name would persist whichever match AD returned first,
        // leaving the intended group writable while another appears protected.
        var match = new ADSearchResult("Ops", TargetDn, "Ops", null, null, "Group", ObjectGuid: TargetGuid);

        var decision = ProtectedPrincipalEntryValidator.Decide(
            [], "Ops", "GroupTarget", new DirectoryValidationResult(DirectoryLookupOutcome.Found, match, Ambiguous: true));

        Assert.False(decision.Accepted);
        Assert.Contains("more than one group", decision.ErrorMessage!, StringComparison.Ordinal);

        // The existing lists keep their existence-only semantics: ambiguity still accepts.
        var groupList = ProtectedPrincipalEntryValidator.Decide(
            [], "Ops", "Group", new DirectoryValidationResult(DirectoryLookupOutcome.Found, match, Ambiguous: true));
        Assert.True(groupList.Accepted);

        // And the lookup actually reports ambiguity (source tripwire - the live path needs AD).
        var svc = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "ADDirectorySearchService.cs"));
        Assert.Contains("Ambiguous: matches > 1", svc, StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_ValueToAdd_SavedThroughTheStore_IsWhatTheGateRefuses()
    {
        // AC7 end to end: the value the admin page's ADD decision produces, saved through
        // SaveConfig, is what CheckWriteTarget matches - never a hand-built fixture.
        var match = new ADSearchResult("Domain Admins", TargetDn, "DomainAdmins", null, null, "Group", ObjectGuid: TargetGuid);
        var decision = ProtectedPrincipalEntryValidator.Decide(
            [], TargetDn, "GroupTarget", new DirectoryValidationResult(DirectoryLookupOutcome.Found, match));
        Assert.True(decision.Accepted);

        var service = CreateRealService();
        service.SaveConfig(new ProtectedPrincipalConfig { GroupTargets = [decision.ValueToAdd!] });

        var result = service.CheckWriteTarget(Group(dn: TargetDn, guid: TargetGuid));
        Assert.True(result.IsProtected);
    }

    [Fact]
    public void Validation_RoutesDnShapedLookups_ToTheOwningDomain()
    {
        // pgwt-5 (source tripwire; the live cross-domain lookup needs a directory): a
        // forest-wide picker selection must not be revalidated against the local domain only,
        // or a WINROOT group can be picked but never saved.
        var svc = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "ADDirectorySearchService.cs"));
        var start = svc.IndexOf("private DirectoryValidationResult ExecuteValidateExists(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ExecuteValidateExists not found - tripwire is stale.");
        var end = svc.IndexOf("private static string[] ValidationProperties(", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound ExecuteValidateExists - update the tripwire.");
        var body = svc[start..end];

        var iRoute = body.IndexOf("DnsDomainFromDn(normalized)", StringComparison.Ordinal);
        var iInvoke = body.IndexOf("ps.Invoke()", StringComparison.Ordinal);
        Assert.True(iRoute >= 0, "DN-shaped validation lookups must route to the owning domain (pgwt-5).");
        Assert.True(iInvoke > iRoute, "The routing must be applied before the lookup runs (pgwt-5).");
        Assert.Contains("AddParameter(\"Server\", server)", body, StringComparison.Ordinal);
        // Scoped to GROUPS: only the group pickers are forest-wide, and the OU/User kinds have
        // a live-proven NotFound contract for bogus-domain DNs that routing would break.
        Assert.Contains("objectKind == \"Group\" && normalized.Contains('=')", body, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminPage_WiresTheTargetList()
    {
        // No bUnit harness exists, so the page wiring is pinned: its own captioned picker, the
        // GroupTarget decision kind over a Group directory lookup, the save mapping, and the
        // sweep's DN lookup for composite entries.
        var page = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Components", "Pages", "AdminSettings.razor"));

        Assert.Contains("Protected Group Targets", page, StringComparison.Ordinal);
        Assert.Contains("@bind-Value=\"ppNewTarget\"", page, StringComparison.Ordinal);
        Assert.Contains("\"GroupTarget\", v => ppNewTarget = v, lookupKind: \"Group\"", page, StringComparison.Ordinal);
        Assert.Contains("GroupTargets = ppTargets", page, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminPage_TargetRows_AreNotSwept_AndDedupeByGuid()
    {
        // pgwt-6: a renamed target still protects via its GUID, so a DN-keyed stale badge
        // would falsely tell the admin the row "protects nothing" - target rows leave the
        // sweep (the patterns precedent). And a re-added target REPLACES any row sharing its
        // GUID, so one group can never hold two rows.
        var page = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Components", "Pages", "AdminSettings.razor"));

        var sweepStart = page.IndexOf("private async Task SweepExistingEntriesAsync()", StringComparison.Ordinal);
        Assert.True(sweepStart >= 0, "SweepExistingEntriesAsync not found - tripwire is stale.");
        var sweepEnd = page.IndexOf("private async Task AddValidatedAsync(", sweepStart, StringComparison.Ordinal);
        Assert.True(sweepEnd > sweepStart, "Could not bound the sweep - update the tripwire.");
        Assert.DoesNotContain("ppTargets", page[sweepStart..sweepEnd], StringComparison.Ordinal);

        var addStart = page.IndexOf("private async Task AddValidatedAsync(", StringComparison.Ordinal);
        var addEnd = page.IndexOf("private async Task SaveProtectedPrincipals()", addStart, StringComparison.Ordinal);
        Assert.True(addStart >= 0 && addEnd > addStart, "Could not bound AddValidatedAsync - update the tripwire.");
        var add = page[addStart..addEnd];
        Assert.Contains("objectKind == \"GroupTarget\"", add, StringComparison.Ordinal);
        Assert.Contains("RemoveAll", add, StringComparison.Ordinal);
    }

    // ----- fsr-2: the self-service exception is a ruling, not an accident -----

    [Fact]
    public void SelfService_DoesNotConsultTheTargetGate_ButKeepsTheMemberGate()
    {
        // Owner ruling 2026-08-31 (.agents/decisions.md; the Constitution carries the scoped
        // exception): self-service eligibility means native AD write rights, so the target
        // gate is admin-module-only. The member-protection check is NOT part of the ruling
        // and must survive it.
        var ssg = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Services", "SelfServiceGroups", "SelfServiceGroupService.cs"));
        Assert.DoesNotContain("ForWriteTarget", ssg, StringComparison.Ordinal);
        Assert.Contains("CheckMemberProtectedAsync", ssg, StringComparison.Ordinal);
    }

    // ----- pgwt-7: post-override failures keep the serviced note for the audit -----

    [Fact]
    public void PostGateFailures_CarryTheServicedNote()
    {
        // Pure half: the helper is lossless and no-ops on a blank note.
        var fail = ExchangeAdminWeb.Models.PermissionResult.Fail("Add failed: boom", "detail");
        var noted = fail.WithServicedNote("note: authorised by G");
        Assert.False(noted.Success);
        Assert.Equal("Add failed: boom", noted.Message);
        Assert.Equal("detail", noted.Detail);
        Assert.Equal("note: authorised by G", noted.ServicedNote);
        Assert.Same(fail, fail.WithServicedNote(null));
        Assert.Same(fail, fail.WithServicedNote("  "));

        // Wiring: every post-override failure return in both services is wrapped, so a failed
        // write on a protected target still audits who was authorised to attempt it.
        var gm = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "GroupManagementService.cs"));
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(
            gm, System.Text.RegularExpressions.Regex.Escape(".WithServicedNote(combinedNote)")).Count);
        var ssg = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "SelfServiceGroups", "SelfServiceGroupService.cs"));
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(
            ssg, System.Text.RegularExpressions.Regex.Escape(".WithServicedNote(combinedNote)")).Count);
    }

    // ---- harness ------------------------------------------------------------------------------

    private static ResolvedDirectoryPrincipal Group(string dn, string? guid, string? sam = null) =>
        new("Test-AD", "Some Group", string.Empty, sam, null, dn, guid, null);

    private static ClaimsPrincipal UserIn(string groupSid) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ANALOG\\tester"), new Claim(ClaimTypes.Role, groupSid)],
            "TestAuth"));

    private (IConfiguration config, IWebHostEnvironment env, ModuleConfigService moduleConfig, DelineaService delinea) CoreDeps()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = _tempDir,
        }).Build();

        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        var moduleConfig = new ModuleConfigService(
            new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(_tempDir), NullLogger<ModuleConfigService>.Instance);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(_tempDir), NullLogger<ExtendedLogService>.Instance);
        var jsonlLog = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
        var trace = new OperationTraceService(config, jsonlLog);
        var delinea = new DelineaService(httpClientFactory, config, NullLogger<DelineaService>.Instance, extLog, trace);
        return (config, env, moduleConfig, delinea);
    }

    private ProtectedPrincipalService CreateRealService()
    {
        var (config, env, moduleConfig, delinea) = CoreDeps();
        return new ProtectedPrincipalService(
            env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea,
            NullLogger<ProtectedPrincipalService>.Instance);
    }

    private (ProtectedPrincipalService pp, ProtectedPrincipalServicerService servicers) GateHarness(
        string[]? servicerGroups,
        ProtectedPrincipalResult? verdict,
        string[]? otherModuleServicerGroups = null)
    {
        var (config, env, moduleConfig, delinea) = CoreDeps();

        var sectionAccessRepo = new SectionAccessRepository(TestConfigStore.Create(_tempDir));
        var rows = new Dictionary<string, string[]>();
        if (servicerGroups is { Length: > 0 })
            rows[ProtectedPrincipalServicerService.SectionKeyFor("GroupManagement")] = servicerGroups;
        if (otherModuleServicerGroups is { Length: > 0 })
            rows[ProtectedPrincipalServicerService.SectionKeyFor("SelfServiceGroups")] = otherModuleServicerGroups;
        rows["GroupManagement"] = ["S-1-5-21-1-2-3-500"];
        sectionAccessRepo.SaveAll(rows);
        var sectionAccess = new SectionAccessService(
            config, NullLogger<SectionAccessService>.Instance, env, new ModuleCatalog(), sectionAccessRepo);
        var servicers = new ProtectedPrincipalServicerService(
            sectionAccess, NullLogger<ProtectedPrincipalServicerService>.Instance);

        var pp = new ScriptedTargetPpService(env, config, moduleConfig,
            TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea)
        { Verdict = verdict };

        return (pp, servicers);
    }

    /// <summary>Scripted CheckWriteTarget; a null verdict throws, for the fail-closed path.</summary>
    private sealed class ScriptedTargetPpService : ProtectedPrincipalService
    {
        public ScriptedTargetPpService(IWebHostEnvironment env, IConfiguration config,
            ModuleConfigService moduleConfig, ProtectedPrincipalRepository repo, DelineaService delinea)
            : base(env, config, moduleConfig, repo, delinea, NullLogger<ProtectedPrincipalService>.Instance)
        { }

        public ProtectedPrincipalResult? Verdict { get; init; }

        public override ProtectedPrincipalResult CheckWriteTarget(ResolvedDirectoryPrincipal target)
            => Verdict ?? throw new InvalidOperationException("scripted throw");
    }

    /// <summary>Forces a config LOAD error so CheckWriteTarget's fail-closed branch is reachable.</summary>
    private sealed class LoadErrorPpService : ProtectedPrincipalService
    {
        public LoadErrorPpService(Func<ProtectedPrincipalService> _)
            : this(BuildDeps())
        { }

        private LoadErrorPpService((IWebHostEnvironment env, IConfiguration config, ModuleConfigService mc, ProtectedPrincipalRepository repo, DelineaService delinea) d)
            : base(d.env, d.config, d.mc, d.repo, d.delinea, NullLogger<ProtectedPrincipalService>.Instance)
        { }

        private static (IWebHostEnvironment, IConfiguration, ModuleConfigService, ProtectedPrincipalRepository, DelineaService) BuildDeps()
        {
            var dir = Path.Combine(Path.GetTempPath(), $"pgwt-err-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path.Combine(dir, "config"));
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Delinea:SecretServerUrl"] = "https://fake.local",
                ["Audit:LogRoot"] = dir,
            }).Build();
            var env = Substitute.For<IWebHostEnvironment>();
            env.ContentRootPath.Returns(dir);
            var moduleConfig = new ModuleConfigService(
                new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(dir), NullLogger<ModuleConfigService>.Instance);
            var httpClientFactory = Substitute.For<IHttpClientFactory>();
            httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
            var extLog = new ExtendedLogService(config, env, TestConfigStore.CreateAppSettings(dir), NullLogger<ExtendedLogService>.Instance);
            var jsonlLog = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
            var trace = new OperationTraceService(config, jsonlLog);
            var delinea = new DelineaService(httpClientFactory, config, NullLogger<DelineaService>.Instance, extLog, trace);
            return (env, config, moduleConfig, TestConfigStore.CreateProtectedPrincipal(dir), delinea);
        }

        public override (ProtectedPrincipalConfig? config, string[] legacyExclusions, string? error) LoadEffectiveConfig()
            => (null, [], "Protected-principals configuration is corrupt. Contact your administrator.");
    }
}
