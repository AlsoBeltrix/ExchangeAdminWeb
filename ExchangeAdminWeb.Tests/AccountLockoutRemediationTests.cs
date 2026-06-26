using ExchangeAdminWeb.Modules;
using Xunit;

namespace ExchangeAdminWeb.Tests;

public sealed class AccountLockoutRemediationTests
{
    [Fact]
    public void ModuleDescriptor_IsFailClosedAndDisabledByDefault()
    {
        var catalog = new ModuleCatalog();
        var module = catalog.GetById("AccountLockoutRemediation");

        Assert.NotNull(module);
        Assert.False(module!.EnabledByDefault);
        Assert.True(module.MainPermission.FailClosed);
        Assert.Equal("account-lockout-remediation", module.Route);
        Assert.Equal("AccountLockoutRemediation", module.MainPermission.PolicyAlias);
    }

    [Fact]
    public void ModuleDescriptor_ExposesLogoffGranularPermission()
    {
        var module = new ModuleCatalog().GetById("AccountLockoutRemediation");

        var permission = Assert.Single(module!.GranularPermissions);
        Assert.Equal("AccountLockoutRemediationLogoff", permission.PolicyAlias);
        Assert.True(permission.FailClosed);
    }
}
