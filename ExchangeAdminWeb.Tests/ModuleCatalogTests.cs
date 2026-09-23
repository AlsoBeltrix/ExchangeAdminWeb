using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using System.Reflection;
using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

public class ModuleCatalogTests
{
    private readonly ModuleCatalog _catalog = new();

    [Fact]
    public void Catalog_HasExpectedModuleCount()
    {
        Assert.Equal(30, _catalog.GetAll().Count); // 30 modules (29 operational + 1 config-only)
    }

    [Fact]
    public void Catalog_HasRiskyUsersModule()
    {
        var module = _catalog.GetById("RiskyUsers");
        Assert.NotNull(module);
        Assert.Equal("risky-users", module!.Route);
        Assert.Equal("Identity & Access", module.Category);
        Assert.False(module.EnabledByDefault);
        Assert.False(module.IsSystemModule);
    }

    [Fact]
    public void Catalog_LicensingUpdates_IsUnderIdentityAndAccess()
    {
        // It writes extensionAttribute11 in AD and is not an ExchangeOnline dependent, so
        // "Exchange" was the wrong nav home. Category is display grouping only - the section
        // access key is the policy alias - but the grouping is what an operator navigates by.
        var module = _catalog.GetById("LicensingUpdates");
        Assert.NotNull(module);
        Assert.Equal("Identity & Access", module!.Category);
    }

    [Fact]
    public void Catalog_RiskyUsers_MainPermissionIsFailClosed()
    {
        var module = _catalog.GetById("RiskyUsers")!;
        Assert.Equal("RiskyUsers", module.MainPermission.PolicyAlias);
        Assert.True(module.MainPermission.FailClosed);
    }

    [Fact]
    public void Catalog_RiskyUsers_HasFailClosedRemediateGranular()
    {
        var module = _catalog.GetById("RiskyUsers")!;
        var granular = Assert.Single(module.GranularPermissions);
        Assert.Equal("RiskyUsersRemediate", granular.PolicyAlias);
        Assert.True(granular.FailClosed);
    }

    [Fact]
    public void Catalog_RiskyUsers_PolicyAliasesAreConfigurable()
    {
        var aliases = _catalog.GetConfigurablePolicyAliases();
        Assert.Contains("RiskyUsers", aliases);
        Assert.Contains("RiskyUsersRemediate", aliases);
    }

    [Fact]
    public void Catalog_HasIntuneDevicesModule()
    {
        var module = _catalog.GetById("IntuneDevices");
        Assert.NotNull(module);
        Assert.Equal("intune-devices", module!.Route);
        Assert.Equal("Infrastructure", module.Category);
        Assert.False(module.EnabledByDefault);
        Assert.False(module.IsSystemModule);
    }

    [Fact]
    public void Catalog_IntuneDevices_MainPermissionIsFailClosed()
    {
        var module = _catalog.GetById("IntuneDevices")!;
        Assert.Equal("IntuneDevices", module.MainPermission.PolicyAlias);
        Assert.True(module.MainPermission.FailClosed);
    }

    [Fact]
    public void Catalog_IntuneDevices_HasThreeFailClosedGranularPermissions()
    {
        // D1 (Delete / Privileged) and D3 (EntraDelete): three separately gated destructive
        // actions, all fail-closed. All three aliases are declared here in S2, even though
        // Privileged and EntraDelete are not consumed until S4/S5 - a policy alias is data in a
        // list and creates no compile dependency (ru-2).
        var module = _catalog.GetById("IntuneDevices")!;
        Assert.Equal(3, module.GranularPermissions.Count);
        Assert.Contains(module.GranularPermissions, p => p.Name == "Delete" && p.PolicyAlias == "IntuneDevicesDelete" && p.FailClosed);
        Assert.Contains(module.GranularPermissions, p => p.Name == "Privileged" && p.PolicyAlias == "IntuneDevicesPrivileged" && p.FailClosed);
        Assert.Contains(module.GranularPermissions, p => p.Name == "EntraDelete" && p.PolicyAlias == "IntuneDevicesEntraDelete" && p.FailClosed);
    }

    [Fact]
    public void Catalog_IntuneDevices_PolicyAliasesAreConfigurable()
    {
        var aliases = _catalog.GetConfigurablePolicyAliases();
        Assert.Contains("IntuneDevices", aliases);
        Assert.Contains("IntuneDevicesDelete", aliases);
        Assert.Contains("IntuneDevicesPrivileged", aliases);
        Assert.Contains("IntuneDevicesEntraDelete", aliases);
    }

    [Fact]
    public void Catalog_HasServiceHealthModule()
    {
        var module = _catalog.GetById("ServiceHealth");
        Assert.NotNull(module);
        Assert.Equal("service-health", module!.Route);
        Assert.Equal("Infrastructure", module.Category);
        Assert.False(module.EnabledByDefault);
        Assert.False(module.IsSystemModule);
    }

    [Fact]
    public void Catalog_ServiceHealth_MainPermissionIsFailClosed()
    {
        var module = _catalog.GetById("ServiceHealth")!;
        Assert.Equal("ServiceHealth", module.MainPermission.PolicyAlias);
        Assert.True(module.MainPermission.FailClosed);
    }

    [Fact]
    public void Catalog_ServiceHealth_HasNoGranularPermissions()
    {
        // The module reads and renders; there is no second tier to grant. A granular permission
        // appearing here later means something mutating was added without a plan.
        var module = _catalog.GetById("ServiceHealth")!;
        Assert.Empty(module.GranularPermissions);
    }

    [Fact]
    public void Catalog_ServiceHealth_PolicyAliasIsConfigurable()
    {
        Assert.Contains("ServiceHealth", _catalog.GetConfigurablePolicyAliases());
    }

    [Fact]
    public void Catalog_ServiceHealth_DeclaresGraphSecretConfigField()
    {
        var module = _catalog.GetById("ServiceHealth")!;
        var field = Assert.Single(module.ConfigFields);
        Assert.Equal("GraphDelineaSecretId", field.Key);
    }

    [Fact]
    public void Catalog_HasDefenderEndpointDevicesModule()
    {
        var module = _catalog.GetById("DefenderEndpointDevices");
        Assert.NotNull(module);
        Assert.Equal("defender-endpoint-devices", module!.Route);
        Assert.Equal("Infrastructure", module.Category);
        // Optional module, per the Constitution's optional-module rule: it ships configurable but
        // not reachable, which is what makes landing it before the live reconnaissance safe.
        Assert.False(module.EnabledByDefault);
        Assert.False(module.IsSystemModule);
        Assert.Equal("1.1.0", module.Version);
    }

    [Fact]
    public void Catalog_DefenderEndpointDevices_MainPermissionIsFailClosed()
    {
        // Device inventory carrying IP and MAC addresses, exposure levels and a map of which
        // machines have no sensor is not address-book data, so the 2026-06-30 open-by-default
        // classification does not transfer (docs/DefenderEndpointDevices-Plan.md, descriptor notes).
        var module = _catalog.GetById("DefenderEndpointDevices")!;
        Assert.Equal("DefenderEndpointDevices", module.MainPermission.PolicyAlias);
        Assert.True(module.MainPermission.FailClosed);
    }

    [Fact]
    public void Catalog_DefenderEndpointDevices_HasNoGranularPermissions()
    {
        // The module reads and exports and mutates nothing, so there is no second tier to grant.
        // A granular permission appearing here later means a device ACTION was added - which needs
        // a wider app-registration grant and its own plan, not a quiet descriptor edit.
        var module = _catalog.GetById("DefenderEndpointDevices")!;
        Assert.Empty(module.GranularPermissions);
    }

    [Fact]
    public void Catalog_DefenderEndpointDevices_DeclaresItsThreeConfigFields()
    {
        // Pinned by key, type and default rather than by count alone: MaxDevices is read at run
        // time by DefenderEndpointDeviceService.ClampMaxDevices, so renaming it here silently
        // reverts every deployment to the built-in ceiling.
        var module = _catalog.GetById("DefenderEndpointDevices")!;
        Assert.Equal(3, module.ConfigFields.Count);

        var secret = module.ConfigFields.Single(f => f.Key == "GraphDelineaSecretId");
        Assert.True(secret.Required);

        var maxDevices = module.ConfigFields.Single(f => f.Key == DefenderEndpointDeviceService.MaxDevicesConfigKey);
        Assert.False(maxDevices.Required);
        // The default must stay above what one request can return: 20000 read as a big number and
        // was in fact below the size of a real tenant, so the first live load refused on the ceiling.
        Assert.Equal("100000", maxDevices.DefaultValue);
        Assert.True(int.Parse(maxDevices.DefaultValue!) == DefenderEndpointDeviceService.DefaultMaxDevices);

        var discovery = module.ConfigFields.Single(f => f.Key == "IncludeDiscoverySources");
        Assert.False(discovery.Required);
        Assert.Equal(ConfigFieldType.Boolean, discovery.FieldType);
        Assert.Equal("true", discovery.DefaultValue);
    }

    [Fact]
    public void Catalog_MessageTrace_HasFailClosedSearchGranular()
    {
        // The module is one page with two capabilities of very different reach: header analysis
        // parses bytes the operator already holds, while trace search reads any user's mail
        // metadata across Exchange Online and the on-premises transport logs. Only the second
        // sits behind this grant (docs/MessageTracePermissionSplit-Plan.md).
        var module = _catalog.GetById("MessageTrace")!;
        var granular = Assert.Single(module.GranularPermissions);
        Assert.Equal("Search", granular.Name);
        Assert.Equal("MessageTraceSearch", granular.PolicyAlias);
        Assert.True(granular.FailClosed);
    }

    [Fact]
    public void Catalog_MessageTrace_DescriptionsSayTheSplitIsNotEnforcedYet()
    {
        // These two sentences are rendered beside the grants on the Module Config Access tab
        // (ModuleConfig.razor:184-187), and they are the only thing telling an administrator what
        // a grant does. While the split is declared but inert, describing the FINAL behaviour
        // makes the page lie: every gate still accepts the parent policy, so a group granted only
        // MessageTrace can still search traces (review finding mtps-1).
        //
        // DELIBERATE TRIPWIRE ON THE ENFORCEMENT SLICE. When the pages and the job processor
        // actually consult MessageTraceSearch, the descriptions become true and must be rewritten
        // to the final wording - and this test must be rewritten with them, to assert the final
        // wording instead. It fails until someone does, which is how the copy and the code are
        // kept from drifting apart.
        var module = _catalog.GetById("MessageTrace")!;
        var granular = Assert.Single(module.GranularPermissions);

        Assert.Contains("NOT YET SPLIT", module.MainPermission.Description);
        Assert.Contains("NOT YET ENFORCED", granular.Description);
    }

    [Fact]
    public void Catalog_MessageTrace_PermissionsAreLabelledForTheAdministratorGrantingThem()
    {
        // The Access tab used to head both rows with the raw alias, so the choice an administrator
        // was making read as "MessageTrace" versus "MessageTraceSearch" - which names neither
        // capability. What is actually being granted is header analysis versus trace search
        // (owner ruling 2026-09-22).
        //
        // The aliases themselves are deliberately NOT renamed and must not be: each is the
        // section-access storage key, and the store is fail-closed, so renaming one orphans every
        // group granted against it and denies them rather than failing open.
        var module = _catalog.GetById("MessageTrace")!;
        var granular = Assert.Single(module.GranularPermissions);

        Assert.Equal("Header Analysis", module.MainPermission.DisplayName);
        Assert.Equal("Trace Search", granular.DisplayName);

        Assert.Equal("MessageTrace", module.MainPermission.PolicyAlias);
        Assert.Equal("MessageTraceSearch", granular.PolicyAlias);
    }

    [Fact]
    public void Catalog_PermissionDisplayNameIsOptionalAndDefaultsToNull()
    {
        // The Access tab falls back to the alias when a permission declares no display name, which
        // is how every module other than MessageTrace still renders. If this ever defaults to
        // something non-null, those modules silently change heading, so the default is pinned
        // here rather than left to the record declaration.
        var module = _catalog.GetById("MailboxPermissions")!;

        Assert.Null(module.MainPermission.DisplayName);
        Assert.All(module.GranularPermissions, gp => Assert.Null(gp.DisplayName));
    }

    [Fact]
    public void Catalog_MessageTrace_PolicyAliasesAreConfigurable()
    {
        // The alias has to reach the Module Config Access tab, which builds its list from here:
        // until it does, no group can be granted the new permission at all.
        var aliases = _catalog.GetConfigurablePolicyAliases();
        Assert.Contains("MessageTrace", aliases);
        Assert.Contains("MessageTraceSearch", aliases);
    }

    [Fact]
    public void Catalog_IntuneDevices_CarriesNoNotificationOrEntraDefaultConfigFields()
    {
        // Owner ruling 2026-09-02 (.agents/decisions.md, superseding D2's config half): whether to
        // email the affected user, and whether to also remove the Entra ID device object, are the
        // acting operator's decisions at the moment of the wipe - not deployment-wide settings. The
        // page's fixed starting states live in IntuneDeviceService and are pinned by
        // IntuneDeviceServiceTests. Pinned here by name so re-adding one as a config field fails.
        var module = _catalog.GetById("IntuneDevices")!;
        foreach (var key in new[] { "NotifyUserOnDelete", "NotifyUserOnRetire", "NotifyUserOnWipe", "RemoveEntraObjectByDefault" })
        {
            Assert.DoesNotContain(module.ConfigFields, f => f.Key == key);
        }

        // The two that do belong there - the Graph credential and the search cap - are untouched.
        Assert.Contains(module.ConfigFields, f => f.Key == "GraphDelineaSecretId");
        Assert.Contains(module.ConfigFields, f => f.Key == "SearchResultLimit");
    }

    [Fact]
    public void Catalog_EveryCategoryIsAModuleCategoriesConstant()
    {
        // Category is nav grouping only and reaches no authorization path, but a value that
        // matches no PrimaryCategoryOrder entry in NavMenu.razor silently drops the module out
        // of the primary nav - a typo nothing else would catch. Every descriptor therefore names
        // a ModuleCategories constant, and this is the tripwire for a raw string creeping back.
        var known = typeof(ModuleCategories)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        // Five and only five: Exchange, Directory & Groups, Identity & Access, Infrastructure
        // and Administration. Adding a sixth is a nav change, not an incidental edit.
        Assert.Equal(5, known.Count);

        foreach (var module in _catalog.GetAll())
        {
            Assert.True(
                known.Contains(module.Category),
                $"Module '{module.Id}' declares category '{module.Category}', which is not a ModuleCategories constant.");
        }
    }

    [Fact]
    public void Catalog_AdministrationCategory_ContainsExactlyTheAdminPages()
    {
        // NavMenu.razor splits the sidebar on ModuleCategories.Administration: a module in this
        // category renders in the bottom Administration block, everything else in the primary
        // nav. That split used to read a SortOrder >= 900 threshold, and the two selections were
        // verified identical when the threshold was removed. This pins the set so the swap
        // cannot quietly change meaning later - adding a module here moves it out of the
        // primary nav, which is a nav decision, not a descriptor detail.
        var administration = _catalog.GetAll()
            .Where(m => string.Equals(m.Category, ModuleCategories.Administration, StringComparison.OrdinalIgnoreCase))
            .Select(m => m.Id)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "AdminBulkJobs", "AdminEventLog", "AdminSettings" }, administration);
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
        Assert.Contains("BitLockerRecovery", aliases);
        Assert.Contains("EventLog", aliases);
        Assert.Contains("UndoAuditedActions", aliases);
        Assert.DoesNotContain("AdminSettings", aliases);
        Assert.DoesNotContain("ExchangeOnline", aliases); // config-only modules excluded
        Assert.Contains("AccountLockoutRemediation", aliases);
        Assert.Contains("AccountLockoutRemediationLogoff", aliases);
        Assert.Contains("BlockedSenders", aliases);
        Assert.Contains("BlockedSendersUnblock", aliases);
        Assert.Contains("SelfServiceGroups", aliases);
        // Cross-module job administration. Configurable so the groups entitled to see every
        // module's jobs at once are set deliberately, not inherited from another module's grant.
        Assert.Contains("AdminBulkJobs", aliases);
        Assert.Contains("RiskyUsers", aliases);
        Assert.Contains("RiskyUsersRemediate", aliases);
        Assert.Contains("IntuneDevices", aliases);
        Assert.Contains("IntuneDevicesDelete", aliases);
        Assert.Contains("IntuneDevicesPrivileged", aliases);
        Assert.Contains("IntuneDevicesEntraDelete", aliases);
        Assert.Contains("ServiceHealth", aliases);
        Assert.Contains("DefenderEndpointDevices", aliases);
        Assert.Contains("MessageTraceSearch", aliases);
        Assert.Contains("CloudPasswordReset", aliases);
        Assert.Contains("CloudPasswordResetReveal", aliases);
        Assert.Equal(45, aliases.Count);
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
        // assertion), not merely require authentication - so an undeclared endpoint is
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
    public void Catalog_GetOrdered_ReturnsSortedByDisplayName()
    {
        // Replaces the old SortOrder assertion. Every list the app renders from the catalog -
        // sidebar sections, Home tiles, the Module Config tree, the Admin Settings list - reads
        // this order, so it is the single place alphabetical ordering has to hold.
        // OrdinalIgnoreCase, matching GetOrdered(): stable across hosts and locales.
        var ordered = _catalog.GetOrdered();

        for (var i = 1; i < ordered.Count; i++)
        {
            Assert.True(
                StringComparer.OrdinalIgnoreCase.Compare(ordered[i - 1].DisplayName, ordered[i].DisplayName) <= 0,
                $"'{ordered[i - 1].DisplayName}' must not sort after '{ordered[i].DisplayName}'.");
        }
    }

    [Fact]
    public void Catalog_GetConfigurablePolicyAliases_IsOrderedByDisplayName()
    {
        // The alias list seeds the Module Config access tree, so it follows the same rule as
        // GetOrdered(): a module's aliases appear in display-name order, with each module's main
        // alias immediately before its own granular aliases.
        var expected = _catalog.GetAll()
            .Where(m => !m.IsSystemModule && !m.IsConfigOnly)
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .SelectMany(m => new[] { m.MainPermission.PolicyAlias }
                .Concat(m.GranularPermissions.Select(gp => gp.PolicyAlias)))
            .ToArray();

        Assert.Equal(expected, _catalog.GetConfigurablePolicyAliases());
    }

    [Fact]
    public void Catalog_PolicyAliases_AreUnaffectedByOrdering()
    {
        // Non-goal guard for docs/AlphabeticalModuleOrdering-Plan.md. Section access is keyed on
        // policy alias (SectionAccessService.BuildFailClosedSet), never on category or list
        // position, so reordering the catalog must not add, drop or rename a single alias - every
        // stored section_access row keeps pointing at the same thing. Pinned as a literal set:
        // deriving it from the catalog would make the test agree with any change.
        var expected = new HashSet<string>(StringComparer.Ordinal)
        {
            "ExchangeOnline",
            "MailboxPermissions", "MailboxPermissionsOnPrem",
            "CalendarPermissions", "CalendarPermissionsOnPrem",
            "MigrationCheck", "MigrationCreate", "MigrationManage",
            "DelegationReport",
            "MessageTrace", "MessageTraceSearch",
            "CloudPasswordReset", "CloudPasswordResetReveal",
            "RecipientLookup",
            "OutOfOffice",
            "BlockedSenders", "BlockedSendersUnblock",
            "GroupManagement", "GroupManagementOnPrem",
            "M365GroupManagement",
            "Comms10k",
            "SelfServiceGroups",
            "MfaReset",
            "AccountLockoutRemediation", "AccountLockoutRemediationLogoff",
            "ConferenceRooms",
            "NamedLocations",
            "EmergencyDisable",
            "RiskyUsers", "RiskyUsersRemediate",
            "DhcpAuthorization",
            "BitLockerRecovery",
            "IntuneDevices", "IntuneDevicesDelete", "IntuneDevicesPrivileged", "IntuneDevicesEntraDelete",
            "ServiceHealth",
            "DefenderEndpointDevices",
            "LicensingUpdates",
            "ADAttributeEditor", "ADAttributeEditorLevel1", "ADAttributeEditorLevel2", "ADAttributeEditorLevel3",
            "AdminSettings",
            "EventLog", "UndoAuditedActions",
            "AdminBulkJobs"
        };

        var actual = _catalog.GetAll()
            .SelectMany(m => new[] { m.MainPermission.PolicyAlias }
                .Concat(m.GranularPermissions.Select(gp => gp.PolicyAlias)))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expected, actual);
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
    public void Catalog_EveryPermissionCarriesAnOperatorFacingDescription()
    {
        // The Module Config Access tab used to list a module's permissions by internal alias
        // alone - "IntuneDevicesPrivileged  0 group(s)" - so an admin granting a group could not
        // tell what they were approving (owner ruling 2026-09-02). Every permission now carries a
        // sentence, and this is the tripwire that keeps the next module from shipping without one.
        //
        // Restating the Name or the PolicyAlias is the specific failure worth failing on: it
        // satisfies "non-blank" while telling the approver nothing, which is the state this
        // whole field exists to end.
        foreach (var module in _catalog.GetAll())
        {
            var permissions = new[] { module.MainPermission }.Concat(module.GranularPermissions);
            foreach (var permission in permissions)
            {
                var where = $"{module.Id}.{permission.PolicyAlias}";

                Assert.False(
                    string.IsNullOrWhiteSpace(permission.Description),
                    $"{where} has no permission description. The Access tab would show the alias alone.");

                Assert.False(
                    string.Equals(permission.Description.Trim(), permission.Name, StringComparison.OrdinalIgnoreCase),
                    $"{where} restates its permission name instead of describing what a member can do.");

                Assert.False(
                    string.Equals(permission.Description.Trim(), permission.PolicyAlias, StringComparison.OrdinalIgnoreCase),
                    $"{where} restates its policy alias instead of describing what a member can do.");

                Assert.EndsWith(".", permission.Description.Trim(), StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void ModuleConfig_AccessTab_RendersEachPermissionsDescription()
    {
        // Source-text guard, not behavioural coverage: there is no bUnit harness in this repo, so
        // no test can render the Access tab. It still catches the render or its lookup being
        // deleted, which would put the page back to listing bare aliases - the exact state the
        // owner ruled invalid.
        var text = File.ReadAllText(Path.Combine(GetPagesDirectory(), "ModuleConfig.razor"));

        Assert.Contains("@PermissionDescriptionFor(capturedAlias)", text, StringComparison.Ordinal);
        Assert.Contains("private string PermissionDescriptionFor(string alias)", text, StringComparison.Ordinal);

        // Pins WHERE the sentence comes from. A hardcoded string or a second copy on the page
        // could drift from the catalog, and a stale description is worse than none.
        Assert.Contains("module.MainPermission.Description", text, StringComparison.Ordinal);
        Assert.Contains("gp.Description", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Catalog_BooleanDefaultedFieldsDeclareBooleanType()
    {
        // A boolean setting rendered as a text box can be mistyped, and a mistyped
        // security switch is the btv-1 failure class. Owner ruling 2026-09-01
        // (.agents/decisions.md): non-ambiguous controls only. A field whose
        // default parses as a boolean IS a boolean setting and must say so.
        foreach (var module in _catalog.GetAll())
        {
            foreach (var field in module.ConfigFields)
            {
                if (bool.TryParse(field.DefaultValue, out _))
                {
                    Assert.True(
                        field.FieldType == ConfigFieldType.Boolean,
                        $"{module.Id}.{field.Key} defaults to '{field.DefaultValue}' but is not ConfigFieldType.Boolean.");
                }
            }
        }
    }

    [Fact]
    public void ModuleConfig_RendersBooleanFieldsAsCheckbox()
    {
        // Wiring hint only (a comment could satisfy it - blr-3/blr-4); the real
        // proof is the manual check on a deployed instance. It still catches the
        // branch being deleted outright, which would silently send Boolean
        // fields back to the text fallthrough.
        var text = File.ReadAllText(Path.Combine(GetPagesDirectory(), "ModuleConfig.razor"));
        Assert.Contains("field.FieldType == ConfigFieldType.Boolean", text);
        Assert.Contains("SetBooleanConfigValue", text);
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
            // Sub-page of the Message Analysis module, not a module of its own: it reuses that
            // module's MessageTrace policy and has no separate catalog descriptor by design.
            "/message-analysis/reports",
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
