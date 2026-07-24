using ExchangeAdminWeb.Services.SelfServiceGroups;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the pure, AD-free invariants of the self-service group service (plan
/// docs/SelfServiceGroupManagement-Plan.md task 1). The live reverse-lookup query needs a real DC
/// (manual-validation-on-dev, no dev tenant); what is unit-testable is the caller-identity boundary:
/// the self-service owner is ALWAYS the authenticated principal, so only a genuine Windows SID is
/// accepted - an alternate identity form (DN, GUID, sAMAccountName, UPN) that Get-ADUser -Identity
/// would otherwise resolve to a DIFFERENT principal must be rejected (AC6, codex SID-provenance
/// finding).
/// </summary>
public class SelfServiceGroupServiceTests
{
    [Theory]
    [InlineData("S-1-5-21-3623811015-3361044348-30300820-1013")]
    [InlineData("S-1-5-18")] // well-known Local System
    [InlineData("S-1-1-0")]  // well-known Everyone
    public void IsSecurityIdentifier_accepts_valid_sids(string sid)
    {
        Assert.True(SelfServiceGroupService.IsSecurityIdentifier(sid));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CN=Jane,OU=Users,DC=contoso,DC=com")]           // a DN
    [InlineData("jsmith")]                                        // sAMAccountName
    [InlineData("jane@contoso.com")]                              // UPN
    [InlineData("12345678-1234-1234-1234-123456789012")]         // objectGUID
    [InlineData("S-1-5-21-notdigits")]                            // malformed SID (non-numeric sub-authority)
    [InlineData("not-a-sid")]
    public void IsSecurityIdentifier_rejects_non_sids(string value)
    {
        Assert.False(SelfServiceGroupService.IsSecurityIdentifier(value));
    }

    [Theory]
    [InlineData("BA")] // BUILTIN\Administrators
    [InlineData("DA")] // Domain Admins
    [InlineData("SY")] // Local System
    [InlineData("WD")] // Everyone
    [InlineData("ba")] // lower-case alias
    public void IsSecurityIdentifier_rejects_sddl_aliases(string alias)
    {
        // new SecurityIdentifier("BA") SUCCEEDS and resolves to a real SID, so parse-success alone
        // is not enough - an alias would reach Get-ADUser -Identity as a DIFFERENT principal than the
        // authenticated caller (codex slice-2 finding). Only the literal SID string may pass.
        Assert.False(SelfServiceGroupService.IsSecurityIdentifier(alias));
    }

    [Fact]
    public void IsSecurityIdentifier_rejects_padded_sid()
    {
        // A padded SID string parses but is not the exact value that reaches AD; reject it.
        Assert.False(SelfServiceGroupService.IsSecurityIdentifier(" S-1-5-18 "));
    }
}
