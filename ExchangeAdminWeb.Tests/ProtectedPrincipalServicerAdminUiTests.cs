using System.Security.Claims;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards for making the protected-principal servicer grant configurable.
/// </summary>
/// <remarks>
/// The capability shipped on 2026-08-06 marked IMPLEMENTED and was unreachable: the service was
/// registered, consumed and tested, but nothing wrote the section-access key it reads, so no group
/// could ever be granted it. Both live config stores held zero such rows.
///
/// Two classes of guard here, and they fail differently:
///
/// 1. BEHAVIOURAL (the store tests). These are real: the hazard is that
///    SectionAccessService.SaveSectionAccess replaces the ENTIRE store via ClearAndInsert, so any
///    page that saves without first reading everything destroys the grants it did not know about.
///    Losing authorization state silently is the worst outcome this feature has, so it is tested
///    against a real SQLite store rather than asserted about markup.
///
/// 2. SOURCE-LEVEL (the page tests). Tripwires, because no bUnit harness exists and the opt-in
///    rule lives in markup. Stated as tripwires so nobody reads them as proof the editor renders.
/// </remarks>
public sealed class ProtectedPrincipalServicerAdminUiTests : IDisposable
{
    private readonly string _tempDir =
        Path.Combine(Path.GetTempPath(), $"pps-ui-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private SectionAccessRepository CreateRepository()
    {
        Directory.CreateDirectory(_tempDir);
        return new SectionAccessRepository(TestConfigStore.Create(_tempDir));
    }

    /// <summary>The stored map, as the page reads it before merging its own aliases.</summary>
    private static Dictionary<string, string[]> ReadAll(SectionAccessRepository repo)
    {
        Assert.True(repo.TryGetAll(out var access), "section-access store could not be read");
        return access;
    }

    // ---- The hazard: a whole-store replace must not lose the other grants ----------------------

    [Fact]
    public void SavingOrdinaryGrants_PreservesAConfiguredServicerGroup()
    {
        // THE test this work exists to justify. SaveAll clears and re-inserts everything, so the
        // page is safe only because it reads every alias and writes the full map back. If someone
        // later "optimises" that into saving just the module's own aliases, the servicer grant
        // vanishes - silently, with no error, and the only symptom is that an authorised team
        // quietly stops being able to do its job.
        var repo = CreateRepository();

        repo.SaveAll(new Dictionary<string, string[]>
        {
            ["BlockedSenders"] = ["S-1-5-21-1-2-3-1001"],
            ["ProtectedServicer:BlockedSenders"] = ["S-1-5-21-1-2-3-2001"],
        });

        // Simulate the page's read-modify-write with a CHANGED ordinary grant.
        var all = ReadAll(repo);
        all["BlockedSenders"] = ["S-1-5-21-1-2-3-1002"];
        repo.SaveAll(all);

        var after = ReadAll(repo);
        Assert.Equal(["S-1-5-21-1-2-3-1002"], after["BlockedSenders"]);
        Assert.True(after.ContainsKey("ProtectedServicer:BlockedSenders"),
            "saving a module's ordinary grants destroyed its servicer grant");
        Assert.Equal(["S-1-5-21-1-2-3-2001"], after["ProtectedServicer:BlockedSenders"]);
    }

    [Fact]
    public void SavingAServicerGrant_PreservesOtherModulesGrants()
    {
        // The same hazard pointed the other way: this page saves from one module's screen, and
        // must not disturb any other module.
        var repo = CreateRepository();

        repo.SaveAll(new Dictionary<string, string[]>
        {
            ["BlockedSenders"] = ["S-1-5-21-1-2-3-1001"],
            ["MfaReset"] = ["S-1-5-21-1-2-3-3001"],
        });

        var all = ReadAll(repo);
        all["ProtectedServicer:BlockedSenders"] = ["S-1-5-21-1-2-3-2001"];
        repo.SaveAll(all);

        var after = ReadAll(repo);
        Assert.Equal(["S-1-5-21-1-2-3-3001"], after["MfaReset"]);
        Assert.Equal(["S-1-5-21-1-2-3-1001"], after["BlockedSenders"]);
        Assert.Equal(["S-1-5-21-1-2-3-2001"], after["ProtectedServicer:BlockedSenders"]);
    }

    // ---- Fail-open on an unconfigured store (found by codex review, pre-deploy) ---------------
    //
    // GetGroupsForSection falls back to the legacy app-wide Security:AllowedGroups when NO
    // section-access source exists, unless the section is fail-closed. The fail-closed set is
    // built from catalog policy aliases - and a ProtectedServicer: key is not one, because no
    // descriptor declares it.
    //
    // So on a server where section access had never been configured, the most privileged grant in
    // the app defaulted to the widest group in it. Worse than the review stated: this needed no
    // admin page at all, because ProtectedPrincipalServicerService.Evaluate calls the same method
    // directly, so the bypass was live regardless of the UI.

    [Fact]
    public void AServicerKey_NeverFallsBackToAllowedGroups()
    {
        // The store is deliberately EMPTY and unconfigured, with a populated AllowedGroups - the
        // exact shape of a fresh server.
        var service = CreateSectionAccessOverEmptyStore(allowedGroups: ["ANALOG\\Domain Users"]);

        var ordinary = service.GetGroupsForSection("SomeNonFailClosedSection");
        var servicer = service.GetGroupsForSection(
            ProtectedPrincipalServicerService.SectionKeyFor("BlockedSenders"));

        // The legacy fallback still applies to what it always applied to...
        Assert.Equal(["ANALOG\\Domain Users"], ordinary);
        // ...and must never reach a protection bypass.
        Assert.Empty(servicer);
    }

    [Fact]
    public void OnAnUnconfiguredStore_NoOneCanServiceProtectedPrincipals()
    {
        // End to end through the decision the gates actually consult, not just the store read.
        var service = CreateSectionAccessOverEmptyStore(allowedGroups: ["ANALOG\\Domain Users"]);
        var servicers = new ProtectedPrincipalServicerService(
            service, NullLogger<ProtectedPrincipalServicerService>.Instance);

        var user = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ANALOG\\someone"), new Claim(ClaimTypes.Role, "ANALOG\\Domain Users")],
            "TestAuth"));

        var decision = servicers.Evaluate(user, "BlockedSenders");

        Assert.False(decision.Allowed);
        Assert.Null(decision.ServicerGroup);
    }

    private SectionAccessService CreateSectionAccessOverEmptyStore(string[] allowedGroups)
    {
        Directory.CreateDirectory(_tempDir);
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            allowedGroups.Select((g, i) => new KeyValuePair<string, string?>($"Security:AllowedGroups:{i}", g))
                         .ToDictionary(kv => kv.Key, kv => kv.Value)).Build();

        var env = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        return new SectionAccessService(
            config,
            NullLogger<SectionAccessService>.Instance,
            env,
            new Modules.ModuleCatalog(),
            new SectionAccessRepository(TestConfigStore.Create(_tempDir)));
    }

    [Fact]
    public void TheKeyTheEditorWrites_IsTheKeyTheServiceReads()
    {
        // Round-trip against the service's own constant. The page calls SectionKeyFor rather than
        // building "ProtectedServicer:" + id by hand, so a rename cannot leave the editor writing
        // a key nothing reads - which would look configured and grant nothing.
        var key = ProtectedPrincipalServicerService.SectionKeyFor("BlockedSenders");

        Assert.Equal("ProtectedServicer:BlockedSenders", key);
        Assert.StartsWith(ProtectedPrincipalServicerService.SectionKeyPrefix, key, StringComparison.Ordinal);
    }

    // ---- Source-level tripwires ---------------------------------------------------------------

    [Fact]
    public void ThePage_OffersAServicerEditor()
    {
        var source = ReadPage();

        Assert.Contains("Protected principal servicing", source, StringComparison.Ordinal);
        Assert.Contains("ProtectedPrincipalServicerService.SectionKeyFor(module.Id)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEditor_IsOfferedOnlyToModulesThatConsultTheServicerService()
    {
        // A grant against a module whose gate never calls Evaluate would grant nothing while
        // looking like it granted something. The opt-in list is the guard, and it must stay a list
        // rather than becoming "every module".
        var source = ReadPage();

        Assert.Contains("ModulesWithProtectedPrincipalServicing", source, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"ModulesWithProtectedPrincipalServicing\s*=\s*new\([^)]*\)\s*\{\s*""BlockedSenders""\s*\}"), source);
        Assert.Contains("ModulesWithProtectedPrincipalServicing.Contains(module.Id)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServicerAlias_SharesTheExistingSavePath()
    {
        // Pins the mitigation for the whole-store-replace hazard at the point it is applied: the
        // alias joins policyAliases, which is what the existing load, save, dirty-tracking and
        // per-alias audit all iterate. A separate save path is the failure mode.
        var source = ReadPage();

        Assert.Contains("policyAliases.Add(ServicerAlias)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheOrdinaryGrantList_DoesNotAlsoRenderTheServicerAlias()
    {
        // Because the alias is in policyAliases for the save path, the ordinary loop would render
        // it a second time - without the warning, and captioned as plain module access. That would
        // present a protection bypass as an ordinary grant, which is the one presentation this
        // feature must never have.
        var source = ReadPage();

        Assert.Contains("policyAliases.Where(a => a != ServicerAlias)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEditor_WarnsWhatTheGrantPermits()
    {
        var source = ReadPage();

        Assert.Contains("Members may act on protected principals in this module", source, StringComparison.Ordinal);
    }

    private static string ReadPage() =>
        File.ReadAllText(Path.Combine(GetPagesDirectory(), "ModuleConfig.razor"));

    private static string GetPagesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var pages = Path.Combine(dir.FullName, "Components", "Pages");
            if (Directory.Exists(pages))
                return pages;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Components/Pages from test base directory.");
    }
}
