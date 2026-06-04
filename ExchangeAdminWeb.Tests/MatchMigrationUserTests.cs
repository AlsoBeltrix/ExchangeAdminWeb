using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

public class MatchMigrationUserTests
{
    private static List<(string Email, string? BatchId)> Users(
        params (string Email, string? BatchId)[] items) => items.ToList();

    [Fact]
    public void ExactMatch_ReturnsFound()
    {
        var users = Users(
            ("ann@co.com", "batch-1"),
            ("joann@co.com", "batch-2"));

        var result = MigrationService.MatchMigrationUser("ann@co.com", users);

        Assert.Equal("batch-1", result.BatchId);
        Assert.Equal("ann@co.com", result.Email);
        Assert.Equal(1, result.MatchCount);
    }

    [Fact]
    public void ExactMatch_CaseInsensitive()
    {
        var users = Users(("User@Co.Com", "batch-1"));

        var result = MigrationService.MatchMigrationUser("user@co.com", users);

        Assert.Equal("batch-1", result.BatchId);
        Assert.Equal("User@Co.Com", result.Email);
    }

    [Fact]
    public void ExactMatch_PreferredOverPartials()
    {
        var users = Users(
            ("carolann@co.com", "batch-2"),
            ("carol@co.com", "batch-1"));

        var result = MigrationService.MatchMigrationUser("carol@co.com", users);

        Assert.Equal("batch-1", result.BatchId);
        Assert.Equal("carol@co.com", result.Email);
    }

    [Fact]
    public void SinglePartialMatch_ReturnsFound()
    {
        var users = Users(
            ("carolann.solivan@co.com", "batch-1"),
            ("john.doe@co.com", "batch-2"));

        var result = MigrationService.MatchMigrationUser("carol", users);

        Assert.Equal("batch-1", result.BatchId);
        Assert.Equal("carolann.solivan@co.com", result.Email);
        Assert.Equal(1, result.MatchCount);
    }

    [Fact]
    public void MultiplePartialMatches_ReturnsAmbiguous()
    {
        var users = Users(
            ("carolann@co.com", "batch-1"),
            ("carol.jones@co.com", "batch-2"));

        var result = MigrationService.MatchMigrationUser("carol", users);

        Assert.Null(result.BatchId);
        Assert.Null(result.Email);
        Assert.Equal(2, result.MatchCount);
    }

    [Fact]
    public void NoMatch_ReturnsNotFound()
    {
        var users = Users(("john@co.com", "batch-1"));

        var result = MigrationService.MatchMigrationUser("carol", users);

        Assert.Equal(0, result.MatchCount);
        Assert.Null(result.BatchId);
    }

    [Fact]
    public void EmptyUserList_ReturnsNotFound()
    {
        var result = MigrationService.MatchMigrationUser("carol", new());

        Assert.Equal(0, result.MatchCount);
    }

    [Fact]
    public void ExactMatch_NullBatchId_ReturnsNotFound()
    {
        var users = Users(("carol@co.com", null));

        var result = MigrationService.MatchMigrationUser("carol@co.com", users);

        Assert.Equal(0, result.MatchCount);
        Assert.Null(result.BatchId);
    }

    [Fact]
    public void SinglePartialMatch_NullBatchId_ReturnsNotFound()
    {
        var users = Users(("carolann@co.com", null));

        var result = MigrationService.MatchMigrationUser("carol", users);

        Assert.Equal(0, result.MatchCount);
        Assert.Null(result.BatchId);
    }

    [Fact]
    public void SearchTerm_Trimmed()
    {
        var users = Users(("carol@co.com", "batch-1"));

        var result = MigrationService.MatchMigrationUser("  carol@co.com  ", users);

        Assert.Equal("batch-1", result.BatchId);
    }
}
