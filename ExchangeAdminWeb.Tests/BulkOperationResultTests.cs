using System.Text;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Tests;

public class BulkOperationResultTests
{
    [Fact]
    public void Summary_FormatsCorrectly()
    {
        var result = new BulkOperationResult
        {
            TotalRows = 10,
            SuccessCount = 7,
            FailedCount = 3,
            Errors = new List<string> { "err1", "err2", "err3" },
            Entries = new List<BulkOperationEntry>()
        };

        Assert.Equal("Processed 10 rows: 7 succeeded, 3 failed", result.Summary);
    }

    [Fact]
    public void Summary_AllSuccess()
    {
        var result = new BulkOperationResult
        {
            TotalRows = 5,
            SuccessCount = 5,
            FailedCount = 0,
            Errors = new List<string>(),
            Entries = new List<BulkOperationEntry>()
        };

        Assert.Equal("Processed 5 rows: 5 succeeded, 0 failed", result.Summary);
    }

    [Fact]
    public void GenerateCsvReport_IncludesHeaderAndEntries()
    {
        var result = new BulkOperationResult
        {
            TotalRows = 2,
            SuccessCount = 1,
            FailedCount = 1,
            Errors = new List<string> { "target2/user2: Not found" },
            Entries = new List<BulkOperationEntry>
            {
                new()
                {
                    Target = "target1@co.com",
                    User = "user1@co.com",
                    Permission = "FullAccess",
                    Status = "SUCCESS",
                    Message = "Operation completed successfully."
                },
                new()
                {
                    Target = "target2@co.com",
                    User = "user2@co.com",
                    Permission = "SendAs",
                    Status = "FAILED",
                    Message = "Not found"
                }
            }
        };

        var bytes = result.GenerateCsvReport();
        var csv = Encoding.UTF8.GetString(bytes);
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length); // header + 2 entries
        Assert.StartsWith("Target,User,Permission,Status,Message", lines[0]);
        Assert.Contains("target1@co.com", lines[1]);
        Assert.Contains("SUCCESS", lines[1]);
        Assert.Contains("target2@co.com", lines[2]);
        Assert.Contains("FAILED", lines[2]);
    }

    [Fact]
    public void GenerateCsvReport_EscapesQuotesInMessage()
    {
        var result = new BulkOperationResult
        {
            TotalRows = 1,
            SuccessCount = 0,
            FailedCount = 1,
            Errors = new List<string>(),
            Entries = new List<BulkOperationEntry>
            {
                new()
                {
                    Target = "t@co.com",
                    User = "u@co.com",
                    Permission = "FullAccess",
                    Status = "FAILED",
                    Message = "Error: \"bad\" input"
                }
            }
        };

        var csv = Encoding.UTF8.GetString(result.GenerateCsvReport());
        Assert.Contains("\"\"bad\"\"", csv);
    }

    [Fact]
    public void GenerateCsvReport_EmptyEntries_ReturnsHeaderOnly()
    {
        var result = new BulkOperationResult
        {
            TotalRows = 0,
            SuccessCount = 0,
            FailedCount = 0,
            Errors = new List<string>(),
            Entries = new List<BulkOperationEntry>()
        };

        var csv = Encoding.UTF8.GetString(result.GenerateCsvReport());
        var lines = csv.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.StartsWith("Target,User,Permission,Status,Message", lines[0]);
    }
}

public class PermissionResultTests
{
    [Fact]
    public void Ok_SetsSuccessTrue()
    {
        var result = PermissionResult.Ok("Done");
        Assert.True(result.Success);
        Assert.Equal("Done", result.Message);
        Assert.Null(result.Detail);
    }

    [Fact]
    public void Ok_DefaultMessage()
    {
        var result = PermissionResult.Ok();
        Assert.True(result.Success);
        Assert.Equal("Operation completed successfully.", result.Message);
    }

    [Fact]
    public void Fail_SetsSuccessFalse()
    {
        var result = PermissionResult.Fail("Something broke", "detail here");
        Assert.False(result.Success);
        Assert.Equal("Something broke", result.Message);
        Assert.Equal("detail here", result.Detail);
    }

    [Fact]
    public void Fail_DetailOptional()
    {
        var result = PermissionResult.Fail("Bad");
        Assert.False(result.Success);
        Assert.Null(result.Detail);
    }
}
