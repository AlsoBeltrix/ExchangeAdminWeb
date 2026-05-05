using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

public class IdentityMatchTests
{
    [Theory]
    [InlineData("DOMAIN\\jsmith", "jsmith@analog.com", true)]
    [InlineData("DOMAIN\\jsmith", "j.smith@analog.com", true)]
    [InlineData("DOMAIN\\j.smith", "jsmith@analog.com", true)]
    [InlineData("DOMAIN\\JSMITH", "jsmith@analog.com", true)]
    [InlineData("DOMAIN\\jsmith", "JSMITH@analog.com", true)]
    [InlineData("DOMAIN\\jsmith", "jdoe@analog.com", false)]
    [InlineData("DOMAIN\\jsmith", "john.smith@analog.com", false)]
    [InlineData("jsmith@analog.com", "jsmith@other.com", true)]
    [InlineData("jsmith@analog.com", "JSMITH@analog.com", true)]
    [InlineData("DOMAIN\\admin", "admin@analog.com", true)]
    [InlineData("DOMAIN\\mcoelho", "m.coelho@analog.com", true)]
    public void IdentitiesMatch_VariousFormats(string id1, string id2, bool expected)
    {
        Assert.Equal(expected, PermissionValidator.IdentitiesMatch(id1, id2));
    }
}
