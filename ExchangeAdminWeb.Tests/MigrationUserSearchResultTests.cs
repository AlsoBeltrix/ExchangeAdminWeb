using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Tests;

public class MigrationUserSearchResultTests
{
    [Fact]
    public void Found_SetsMatchCountAndFields()
    {
        var result = MigrationUserSearchResult.Found("batch-1", "user@co.com");
        Assert.Equal("batch-1", result.BatchId);
        Assert.Equal("user@co.com", result.Email);
        Assert.Equal(1, result.MatchCount);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Ambiguous_SetsMatchCount_NoFields()
    {
        var result = MigrationUserSearchResult.Ambiguous(3);
        Assert.Null(result.BatchId);
        Assert.Null(result.Email);
        Assert.Equal(3, result.MatchCount);
        Assert.Null(result.Error);
    }

    [Fact]
    public void NotFound_ZeroMatchCount()
    {
        var result = MigrationUserSearchResult.NotFound();
        Assert.Null(result.BatchId);
        Assert.Null(result.Email);
        Assert.Equal(0, result.MatchCount);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failed_SetsError()
    {
        var result = MigrationUserSearchResult.Failed("Connection refused");
        Assert.Null(result.BatchId);
        Assert.Null(result.Email);
        Assert.Equal("Connection refused", result.Error);
    }
}
