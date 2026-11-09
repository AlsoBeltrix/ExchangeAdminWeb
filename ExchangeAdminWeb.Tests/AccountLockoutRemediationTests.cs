using System.Reflection;
using ExchangeAdminWeb.Models.AccountLockoutRemediation;
using ExchangeAdminWeb.Modules;
using Xunit;
using Page = ExchangeAdminWeb.Components.Pages.AccountLockoutRemediation;

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

    // rev-j1: both execute buttons are enabled by the rule of the check behind them - TicketPresence, on the ticket
    // sanitized - so a ticket of only a zero-width space leaves them disabled, and one carrying a zero-width space inside
    // a real ticket enables them (it is recorded as "INC0042").
    [Theory]
    [InlineData("\u200B", false)]
    [InlineData("INC\u200B0042", true)]
    public void ExecuteButtons_AreEnabledByTheSanitizedTicket(string ticket, bool enabled)
    {
        var page = PageReadyToExecute(ticket);

        Assert.Equal(enabled, Read(page, "CanExecuteSource"));
        Assert.Equal(enabled, Read(page, "CanExecuteSweep"));
    }

    private static Page PageReadyToExecute(string ticket)
    {
        var page = new Page();
        Set(page, "discoveryResult", new AccountLockoutDiscoveryResult(true, "", [], ["PC1"], []));
        Set(page, "sweepUsers", "jdoe");
        Set(page, "searchBase", "OU=Test,DC=x");
        Set(page, "typedConfirmation", "LOG OFF");
        Set(page, "ticketNumber", ticket);
        return page;
    }

    private static void Set(Page page, string field, object value) =>
        typeof(Page).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static bool Read(Page page, string property) =>
        (bool)typeof(Page).GetProperty(property, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
}
