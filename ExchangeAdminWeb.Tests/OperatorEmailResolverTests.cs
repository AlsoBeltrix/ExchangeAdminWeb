using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Coverage for <see cref="OperatorEmailResolver"/> (docs/OperatorEmailResolution-Plan.md).
/// The suite must never need a domain controller, so the directory is substituted through
/// <see cref="IOperatorDirectory"/> -- the one-member seam that exists because
/// <c>ADDirectorySearchService</c> is sealed.
/// <para>
/// The load-bearing test here is <see cref="ResolveAsync_MalformedIdentity_NeverReachesTheDirectory"/>:
/// it is what keeps an account name from re-entering the identity path, which openreview F1
/// showed could mail mail-flow data to a same-named account in another trusted domain.
/// </para>
/// </summary>
public sealed class OperatorEmailResolverTests
{
    private const string ValidSid = "S-1-5-21-1004336348-1177238915-682003330-512";

    private readonly IOperatorDirectory _directory = Substitute.For<IOperatorDirectory>();

    private OperatorEmailResolver CreateResolver()
        => new(_directory, NullLogger<OperatorEmailResolver>.Instance);

    private static ADSearchResult User(string? mail, string? upn) => new(
        DisplayName: "Jane Doe",
        DistinguishedName: "CN=Jane Doe,OU=Users,DC=contoso,DC=com",
        SamAccountName: "jdoe",
        UserPrincipalName: upn,
        Email: mail,
        ObjectType: "User");

    [Fact]
    public async Task ResolveAsync_MailPresent_ReturnsItTrimmed()
    {
        _directory.FindUserBySid(ValidSid).Returns(User("  jane.doe@contoso.com  ", "jdoe@contoso.com"));

        var result = await CreateResolver().ResolveAsync(ValidSid);

        Assert.Equal("jane.doe@contoso.com", result);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ResolveAsync_MailMissingOrBlank_FallsBackToUpn(string? mail)
    {
        // D2: "UPN is the same as email here". A whitespace mail attribute must not win over a
        // real UPN -- that would return a blank address the caller treats as resolved.
        _directory.FindUserBySid(ValidSid).Returns(User(mail, "  jdoe@contoso.com  "));

        var result = await CreateResolver().ResolveAsync(ValidSid);

        Assert.Equal("jdoe@contoso.com", result);
    }

    [Fact]
    public async Task ResolveAsync_NeitherMailNorUpn_ReturnsNullNotEmptyString()
    {
        // Callers branch on null only. An empty string would read as a resolved address.
        _directory.FindUserBySid(ValidSid).Returns(User(null, "   "));

        var result = await CreateResolver().ResolveAsync(ValidSid);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_ValidSid_PassesItToTheDirectoryUnmodified()
    {
        // Asserting the identity the directory received, not merely the result: a refactor that
        // mangled the SID (stripping, lowercasing, reformatting) would still return an address
        // from a substitute matched loosely, so only this assertion catches it.
        _directory.FindUserBySid(Arg.Any<string>()).Returns(User("jane.doe@contoso.com", null));

        await CreateResolver().ResolveAsync(ValidSid);

        _directory.Received(1).FindUserBySid(ValidSid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("CONTOSO\\jdoe")]
    [InlineData("jdoe")]
    [InlineData("jdoe@contoso.com")]
    [InlineData("Unknown")]
    [InlineData("S-1-5-not-a-sid")]
    public async Task ResolveAsync_MalformedIdentity_NeverReachesTheDirectory(string? identity)
    {
        // The highest-value guard in this file. Every one of these is a name or a non-SID, and
        // a name must never become a directory lookup key: openreview F1 showed that resolving
        // by name can return one confidently-wrong user across a domain trust.
        var result = await CreateResolver().ResolveAsync(identity);

        Assert.Null(result);
        _directory.DidNotReceive().FindUserBySid(Arg.Any<string>());
    }

    [Fact]
    public async Task ResolveAsync_DirectoryThrows_ReturnsNullAndDoesNotPropagate()
    {
        // Fail-soft: this runs inside OnInitializedAsync, where an escaping exception takes the
        // whole page down over a pre-filled text box.
        _directory.FindUserBySid(ValidSid).Returns<ADSearchResult?>(_ => throw new InvalidOperationException("AD is down"));

        var result = await CreateResolver().ResolveAsync(ValidSid);

        Assert.Null(result);
    }

    [Fact]
    public async Task ResolveAsync_NoSuchUser_ReturnsNull()
    {
        _directory.FindUserBySid(ValidSid).Returns((ADSearchResult?)null);

        var result = await CreateResolver().ResolveAsync(ValidSid);

        Assert.Null(result);
    }
}
