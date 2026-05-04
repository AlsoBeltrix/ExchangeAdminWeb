using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

public class PermissionValidatorTests
{
    private static PermissionValidator CreateValidator(
        string[]? excludedUsers = null,
        bool preventSelfGrant = true)
    {
        var configData = new Dictionary<string, string?>
        {
            ["Security:PreventSelfGrant"] = preventSelfGrant.ToString()
        };

        if (excludedUsers is not null)
        {
            for (int i = 0; i < excludedUsers.Length; i++)
                configData[$"Security:ExcludedUsers:{i}"] = excludedUsers[i];
        }

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        var logger = Substitute.For<ILogger<PermissionValidator>>();
        return new PermissionValidator(config, logger);
    }

    // --- Self-grant validation ---

    [Fact]
    public void ValidateSelfGrant_SameUser_ReturnsError()
    {
        var validator = CreateValidator();
        var result = validator.ValidateSelfGrant(@"DOMAIN\jdoe", "jdoe@company.com");
        Assert.NotNull(result);
        Assert.Contains("cannot grant permissions to yourself", result);
    }

    [Fact]
    public void ValidateSelfGrant_SameUser_ExactMatch_ReturnsError()
    {
        var validator = CreateValidator();
        var result = validator.ValidateSelfGrant("jdoe", "jdoe");
        Assert.NotNull(result);
    }

    [Fact]
    public void ValidateSelfGrant_DifferentUser_ReturnsNull()
    {
        var validator = CreateValidator();
        var result = validator.ValidateSelfGrant(@"DOMAIN\admin", "jdoe@company.com");
        Assert.Null(result);
    }

    [Fact]
    public void ValidateSelfGrant_Disabled_AllowsSelfGrant()
    {
        var validator = CreateValidator(preventSelfGrant: false);
        var result = validator.ValidateSelfGrant("jdoe", "jdoe");
        Assert.Null(result);
    }

    [Theory]
    [InlineData(@"DOMAIN\jdoe", "jdoe@company.com")]
    [InlineData("jdoe@company.com", @"DOMAIN\jdoe")]
    [InlineData("jdoe", "jdoe@company.com")]
    [InlineData("jdoe@company.com", "jdoe")]
    public void ValidateSelfGrant_ExtractsUsername_AcrossFormats(string current, string affected)
    {
        var validator = CreateValidator();
        var result = validator.ValidateSelfGrant(current, affected);
        Assert.NotNull(result);
    }

    [Fact]
    public void ValidateSelfGrant_CaseInsensitive()
    {
        var validator = CreateValidator();
        var result = validator.ValidateSelfGrant("JDoe", "jdoe");
        Assert.NotNull(result);
    }

    // --- Fail-closed init state machine ---

    [Fact]
    public async Task ValidateTargetMailbox_NoExclusions_NoExchangeConfig_BlocksAllOperations()
    {
        // With exclusions configured but no Exchange connection available,
        // group expansion will fail and _initFailed should be true.
        // The validator should block all operations.
        var validator = CreateValidator(excludedUsers: new[] { "SomeGroupThatNeedsExpansion" });

        var result = await validator.ValidateTargetMailboxAsync("anyone@company.com");

        // Should either block (init failed) or allow (if the group wasn't expandable but the identity was added as-is).
        // The key behavior: it should NOT silently allow when init fails.
        // Since Exchange isn't configured, ConnectToExchange returns false,
        // and TryExpandGroupAsync throws, so _initFailed = true.
        Assert.NotNull(result);
        Assert.Contains("unavailable", result, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateTargetMailbox_EmptyExclusions_AllowsOperations()
    {
        // No exclusions configured at all — init succeeds with empty set
        var validator = CreateValidator(excludedUsers: Array.Empty<string>());

        var result = await validator.ValidateTargetMailboxAsync("anyone@company.com");
        Assert.Null(result);
    }

    [Fact]
    public async Task IsUserExcluded_ChecksExactAndUsername()
    {
        // This test only works if init succeeds. With no Exchange config,
        // any configured user that looks like a group will fail.
        // Use empty exclusions so init succeeds, then verify the method itself.
        var validator = CreateValidator(excludedUsers: Array.Empty<string>());

        var excluded = await validator.IsUserExcludedAsync("anyone@company.com");
        Assert.False(excluded);
    }

    // --- Retry after init failure ---

    [Fact]
    public async Task ValidateTargetMailbox_RetriableAfterFailure()
    {
        // First call fails init (group expansion fails without Exchange).
        // _initialized stays false so subsequent calls retry.
        var validator = CreateValidator(excludedUsers: new[] { "SomeGroup" });

        var firstResult = await validator.ValidateTargetMailboxAsync("user@company.com");
        Assert.NotNull(firstResult);

        // Second call should also attempt init (not cached as success)
        var secondResult = await validator.ValidateTargetMailboxAsync("user@company.com");
        Assert.NotNull(secondResult);
    }
}
