using ExchangeAdminWeb.Modules;
using Microsoft.AspNetCore.Authorization;

namespace ExchangeAdminWeb.Tests;

public class ModuleCatalogTests
{
    private readonly ModuleCatalog _catalog = new();

    [Fact]
    public void Catalog_HasExpectedModuleCount()
    {
        Assert.Equal(12, _catalog.GetAll().Count);
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
        Assert.DoesNotContain("AdminSettings", aliases);
        Assert.Equal(15, aliases.Count);
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
            "DelegationReport", "MessageTrace", "RecipientLookup", "OutOfOffice"
        };

        foreach (var name in expectedPolicies)
            Assert.NotNull(options.GetPolicy(name));
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
}
