using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

public class IdentityMatchTests
{
    [Theory]
    [InlineData("DOMAIN\\jsmith", "jsmith@example.com", true)]
    [InlineData("DOMAIN\\jsmith", "j.smith@example.com", true)]
    [InlineData("DOMAIN\\j.smith", "jsmith@example.com", true)]
    [InlineData("DOMAIN\\JSMITH", "jsmith@example.com", true)]
    [InlineData("DOMAIN\\jsmith", "JSMITH@example.com", true)]
    [InlineData("DOMAIN\\jsmith", "jdoe@example.com", false)]
    [InlineData("DOMAIN\\jsmith", "john.smith@example.com", false)]
    [InlineData("jsmith@example.com", "jsmith@other.example", true)]
    [InlineData("jsmith@example.com", "JSMITH@example.com", true)]
    [InlineData("DOMAIN\\admin", "admin@example.com", true)]
    [InlineData("DOMAIN\\jdoe", "j.doe@example.com", true)]
    public void IdentitiesMatch_VariousFormats(string id1, string id2, bool expected)
    {
        Assert.Equal(expected, PermissionValidator.IdentitiesMatch(id1, id2));
    }
}
