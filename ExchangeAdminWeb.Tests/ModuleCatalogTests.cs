using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Modules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

public class ModuleCatalogTests
{
    private readonly ModuleCatalog _catalog = new();

    [Fact]
    public void Catalog_HasExpectedModuleCount()
    {
        Assert.Equal(22, _catalog.GetAll().Count); // 22 modules (21 operational + 1 config-only)
    }

    [Fact]
    public void Catalog_AllModulesHaveUniqueIds()
    {
        var ids = _catalog.GetAll().Select(m => m.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Catalog_AllModulesHaveUniqueRoutes()
    {
        var routes = _catalog.GetAll().Select(m => m.Route).ToList();
        Assert.Equal(routes.Count, routes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Catalog_GetById_ReturnsCorrectModule()
    {
        var module = _catalog.GetById("Migration");
        Assert.NotNull(module);
        Assert.Equal("Exchange Migration", module.DisplayName);
        Assert.Equal("migration", module.Route);
    }

    [Fact]
    public void Catalog_GetByRoute_ReturnsCorrectModule()
    {
        var module = _catalog.GetByRoute("mailbox-permissions");
        Assert.NotNull(module);
        Assert.Equal("MailboxPermissions", module.Id);
    }

    [Fact]
    public void Catalog_GetByPolicyAlias_MainPermission()
    {
        var module = _catalog.GetByPolicyAlias("MigrationCheck");
        Assert.NotNull(module);
        Assert.Equal("Migration", module.Id);
    }

    [Fact]
    public void Catalog_GetByPolicyAlias_GranularPermission()
    {
        var module = _catalog.GetByPolicyAlias("MailboxPermissionsOnPrem");
        Assert.NotNull(module);
        Assert.Equal("MailboxPermissions", module.Id);
    }

    [Fact]
    public void Catalog_GetConfigurablePolicyAliases_MatchesExpected()
    {
        var aliases = _catalog.GetConfigurablePolicyAliases();

        Assert.Contains("MailboxPermissions", aliases);
        Assert.Contains("MailboxPermissionsOnPrem", aliases);
        Assert.Contains("CalendarPermissions", aliases);
        Assert.Contains("CalendarPermissionsOnPrem", aliases);
        Assert.Contains("MigrationCheck", aliases);
        Assert.Contains("MigrationCreate", aliases);
        Assert.Contains("MigrationManage", aliases);
        Assert.Contains("DelegationReport", aliases);
        Assert.Contains("MessageTrace", aliases);
        Assert.Contains("RecipientLookup", aliases);
        Assert.Contains("OutOfOffice", aliases);
        Assert.Contains("MfaReset", aliases);
        Assert.Contains("Comms10k", aliases);
        Assert.Contains("GroupManagement", aliases);
        Assert.Contains("GroupManagementOnPrem", aliases);
        Assert.Contains("M365GroupManagement", aliases);
        Assert.Contains("ConferenceRooms", aliases);
        Assert.Contains("NamedLocations", aliases);
        Assert.Contains("LicensingUpdates", aliases);
        Assert.Contains("ADAttributeEditor", aliases);
        Assert.Contains("ADAttributeEditorLevel1", aliases);
        Assert.Contains("ADAttributeEditorLevel2", aliases);
        Assert.Contains("ADAttributeEditorLevel3", aliases);
        Assert.Contains("EmergencyDisable", aliases);
        Assert.Contains("DhcpAuthorization", aliases);
        Assert.Contains("EventLog", aliases);
        Assert.Contains("UndoAuditedActions", aliases);
        Assert.DoesNotContain("AdminSettings", aliases);
        Assert.DoesNotContain("ExchangeOnline", aliases); // config-only modules excluded
        Assert.Contains("AccountLockoutRemediation", aliases);
        Assert.Contains("AccountLockoutRemediationLogoff", aliases);
        Assert.Contains("BlockedSenders", aliases);
        Assert.Contains("BlockedSendersUnblock", aliases);
        Assert.Equal(31, aliases.Count);
    }

    [Fact]
    public void Catalog_ConfigureAuthorizationPolicies_GeneratesExpectedPolicies()
    {
        var options = new AuthorizationOptions();
        _catalog.ConfigureAuthorizationPolicies(options, new[] { "TestGroup" }, new[] { "AdminGroup" });

        var expectedPolicies = new[]
        {
            "GroupPolicy", "AdminSettings",
            "MailboxPermissions", "MailboxPermissionsOnPrem",
            "CalendarPermissions", "CalendarPermissionsOnPrem",
            "MigrationCheck", "MigrationCreate", "MigrationManage",
            "DelegationReport", "MessageTrace", "RecipientLookup", "OutOfOffice",
            "EventLog"
        };

        foreach (var name in expectedPolicies)
            Assert.NotNull(options.GetPolicy(name));
    }

    [Fact]
    public async Task Catalog_FallbackPolicy_DeniesByDefault_WithoutGroupGate()
    {
        // Endpoints that declare no authorization metadata fall under FallbackPolicy.
        // True deny-by-default: the policy must DENY every principal (a failing
        // assertion), not merely require authentication — so an undeclared endpoint is
        // blocked for all users until it declares its own catalog-backed policy. It must
        // also NOT carry the legacy AllowedGroups group requirement (which would silently
        // resurrect the removed app-wide group gate on any undeclared endpoint).
        var options = new AuthorizationOptions();
        _catalog.ConfigureAuthorizationPolicies(options, new[] { "TestGroup" }, new[] { "AdminGroup" });

        var fallback = options.FallbackPolicy;
        Assert.NotNull(fallback);

        // Requires authentication (DenyAnonymousAuthorizationRequirement is what
        // RequireAuthenticatedUser adds).
        Assert.Contains(fallback!.Requirements, r => r is DenyAnonymousAuthorizationRequirement);

        // Carries a deny-all assertion: evaluate it and confirm it fails even for a
        // fully authenticated user. This is what makes the fallback true deny-by-default.
        var assertion = fallback.Requirements.OfType<AssertionRequirement>().SingleOrDefault();
        Assert.NotNull(assertion);

        var authenticatedUser = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Name, "anyone") },
                authenticationType: "Test"));
        var ctx = new AuthorizationHandlerContext(new[] { assertion! }, authenticatedUser, resource: null);
        await assertion!.HandleAsync(ctx);
        Assert.False(ctx.HasSucceeded);

        // Does NOT inherit the AllowedGroups group gate.
        Assert.DoesNotContain(fallback.Requirements, r => r is GroupAuthorizationRequirement);
    }

    [Fact]
    public void Catalog_SystemModules_AreNotConfigurable()
    {
        var configurable = _catalog.GetConfigurablePolicyAliases();
        var systemModules = _catalog.GetAll().Where(m => m.IsSystemModule);

        foreach (var sm in systemModules)
            Assert.DoesNotContain(sm.MainPermission.PolicyAlias, configurable);
    }

    [Fact]
    public void Catalog_GetOrdered_ReturnsSortedBySortOrder()
    {
        var ordered = _catalog.GetOrdered();
        for (int i = 1; i < ordered.Count; i++)
            Assert.True(ordered[i].SortOrder >= ordered[i - 1].SortOrder);
    }

    [Fact]
    public void Catalog_MigrationGranularPermissions_HasCreateAndManage()
    {
        var migration = _catalog.GetById("Migration")!;
        Assert.Equal(2, migration.GranularPermissions.Count);
        Assert.Contains(migration.GranularPermissions, p => p.Name == "Create" && p.PolicyAlias == "MigrationCreate");
        Assert.Contains(migration.GranularPermissions, p => p.Name == "Manage" && p.PolicyAlias == "MigrationManage");
    }

    [Fact]
    public void Catalog_RoutesHaveMatchingPagesAndPolicies()
    {
        var pageRoutes = ReadPageRoutes();
        var pagePolicies = ReadPagePolicies();

        foreach (var module in _catalog.GetAll())
        {
            var route = "/" + module.Route.Trim('/');
            Assert.Contains(route, pageRoutes);
            Assert.True(pagePolicies.TryGetValue(route, out var policy), $"Missing authorize policy for {route}");

            // Config-only modules use AdminSettings policy, not their own MainPermission
            var expectedPolicy = module.IsConfigOnly ? "AdminSettings" : module.MainPermission.PolicyAlias;
            Assert.Equal(expectedPolicy, policy);
        }
    }

    [Fact]
    public void ModulePagesHaveCatalogDescriptors()
    {
        var catalogRoutes = _catalog.GetAll()
            .Select(m => "/" + m.Route.Trim('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var allowedNonCatalogRoutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/",
            "/Error",
            "/access-denied",
            "/message-trace",
            "/module-config/{ModuleId}"
        };

        var modulePageRoutes = ReadPageRoutes()
            .Where(r => !allowedNonCatalogRoutes.Contains(r))
            .ToArray();

        Assert.All(modulePageRoutes, route => Assert.Contains(route, catalogRoutes));
    }

    private static HashSet<string> ReadPageRoutes()
    {
        var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(GetPagesDirectory(), "*.razor"))
        {
            var text = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(text, "@page\\s+\"([^\"]+)\""))
                routes.Add(match.Groups[1].Value);
        }

        return routes;
    }

    private static Dictionary<string, string> ReadPagePolicies()
    {
        var policies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(GetPagesDirectory(), "*.razor"))
        {
            var text = File.ReadAllText(file);
            var policyMatch = Regex.Match(text, "\\[Authorize\\(Policy\\s*=\\s*\"([^\"]+)\"\\)\\]");
            if (!policyMatch.Success)
                continue;

            foreach (Match routeMatch in Regex.Matches(text, "@page\\s+\"([^\"]+)\""))
                policies[routeMatch.Groups[1].Value] = policyMatch.Groups[1].Value;
        }

        return policies;
    }

    [Fact]
    public void Catalog_ConfigOnlyModules_UseAdminSettingsPolicy()
    {
        var pageRoutes = ReadPageRoutes();
        var pagePolicies = ReadPagePolicies();

        foreach (var module in _catalog.GetAll().Where(m => m.IsConfigOnly))
        {
            var route = "/" + module.Route.Trim('/');
            Assert.Contains(route, pageRoutes);
            Assert.True(pagePolicies.TryGetValue(route, out var policy),
                $"Config-only module '{module.Id}' at {route} must have an [Authorize] policy");
            Assert.Equal("AdminSettings", policy);
        }
    }

    [Fact]
    public void Catalog_ConfigOnlyModules_HaveRoute()
    {
        foreach (var module in _catalog.GetAll().Where(m => m.IsConfigOnly))
        {
            Assert.False(string.IsNullOrWhiteSpace(module.Route),
                $"Config-only module '{module.Id}' must have a route");
        }
    }

    [Fact]
    public void Catalog_ExchangeOnlineModule_IsConfigOnly()
    {
        var module = _catalog.GetById("ExchangeOnline");
        Assert.NotNull(module);
        Assert.True(module.IsConfigOnly);
        Assert.Equal("exchange-online-config", module.Route);
        Assert.Equal("Exchange", module.Category);
    }

    [Fact]
    public void Catalog_ExchangeDependentModules_DependOnExchangeOnline()
    {
        var expectedDependents = new[]
        {
            "MailboxPermissions", "CalendarPermissions", "Migration",
            "DelegationReport", "MessageTrace", "RecipientLookup",
            "OutOfOffice", "ConferenceRooms"
        };

        foreach (var id in expectedDependents)
        {
            var module = _catalog.GetById(id);
            Assert.NotNull(module);
            Assert.Equal("ExchangeOnline", module.DependsOn);
        }
    }

    [Fact]
    public void Catalog_IndependentModules_HaveNoDependsOn()
    {
        var independentModuleIds = new[]
        {
            "GroupManagement", "M365GroupManagement", "ADAttributeEditor", "LicensingUpdates",
            "EmergencyDisable", "MfaReset", "NamedLocations",
            "DhcpAuthorization", "Comms10k", "AdminSettings", "AdminEventLog"
        };

        foreach (var id in independentModuleIds)
        {
            var module = _catalog.GetById(id);
            Assert.NotNull(module);
            Assert.Null(module.DependsOn);
        }
    }

    [Fact]
    public void Catalog_DependsOn_ReferencesExistingModules()
    {
        foreach (var module in _catalog.GetAll().Where(m => m.DependsOn != null))
        {
            var parent = _catalog.GetById(module.DependsOn!);
            Assert.NotNull(parent);
        }
    }

    [Fact]
    public void Catalog_MutatingModulePermissions_AreFailClosed()
    {
        // Fail-closed means: when section access has no source for a module,
        // access is denied instead of falling back to the global AllowedGroups.
        // Only genuinely read-only modules may rely on the legacy fallback.
        // Deploys have purged runtime config before (commit 0021502), so a
        // missing sectionaccess.json must never open up mutating modules.
        var readOnlyAllowlist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "DelegationReport",
            "RecipientLookup"
        };

        foreach (var module in _catalog.GetAll().Where(m => !m.IsSystemModule && !m.IsConfigOnly))
        {
            if (readOnlyAllowlist.Contains(module.Id))
                continue;

            Assert.True(module.MainPermission.FailClosed,
                $"Module '{module.Id}' main permission '{module.MainPermission.PolicyAlias}' must be FailClosed");
            foreach (var granular in module.GranularPermissions)
            {
                Assert.True(granular.FailClosed,
                    $"Module '{module.Id}' granular permission '{granular.PolicyAlias}' must be FailClosed");
            }
        }
    }

    [Fact]
    public void Catalog_ConferenceRooms_HasNoOnPremCredentialField()
    {
        // On-prem Exchange is decommissioned (ProdReadiness plan Q1/AC14). The
        // OnPremDelineaSecretId field was retired - it was also never read by any
        // code (ModuleCredentialService reads "DelineaSecretId"), so reintroducing
        // it would resurrect a dead, misleading config field.
        var module = _catalog.GetById("ConferenceRooms");
        Assert.NotNull(module);
        Assert.DoesNotContain(module.ConfigFields, f => f.Key == "OnPremDelineaSecretId");
    }

    [Fact]
    public void Catalog_NoCyclicDependencies()
    {
        // If there were cycles, ModuleCatalog constructor would have thrown.
        // This test verifies the catalog constructs successfully and
        // walking DependsOn chains terminates.
        foreach (var module in _catalog.GetAll().Where(m => m.DependsOn != null))
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { module.Id };
            var current = module.DependsOn;
            while (current != null)
            {
                Assert.True(visited.Add(current), $"Cycle detected at module '{current}' in chain from '{module.Id}'");
                var parent = _catalog.GetById(current);
                current = parent?.DependsOn;
            }
        }
    }

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
