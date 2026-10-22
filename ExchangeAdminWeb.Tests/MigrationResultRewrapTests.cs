using System.Reflection;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a23c. Migration re-wrapped a <see cref="PermissionResult"/> field by field in five places -
/// the protected-principal exclusion in <c>CreateMigrationBatchAsync</c> and four failed-audit
/// catches on the page - and every one of them left out <see cref="PermissionResult.AppliedRights"/>
/// (aud-a23) and <see cref="PermissionResult.Changed"/> (aud-a25). An initializer that leaves out an
/// init property leaves it at its default, so the copy said null and TRUE whatever the result it
/// wrapped said: a no-op re-wrapped this way is announced as a change.
/// </summary>
/// <remarks>
/// The two helpers are the module's only copies now. The reflection test sets every property and
/// checks each one the helper does not own came through, so the next field the model gains is held
/// to the same rule without anyone remembering this file.
/// </remarks>
public class MigrationResultRewrapTests
{
    [Fact]
    public void TheExclusionRewrap_KeepsAppliedRightsAndChanged()
    {
        var applied = new[] { "FullAccess" };
        var inner = new PermissionResult
        {
            Success = true,
            Message = "Migration batch 'b1' created successfully with 2 user(s).",
            Detail = "Batch Name: b1",
            AppliedRights = applied,
            Changed = false,
        };
        var excluded = new List<string> { "ceo@contoso.com - protected principal" };

        var result = MigrationService.WithProtectionOutcome(inner, excluded, "serviced: ceo@contoso.com");

        Assert.Same(applied, result.AppliedRights);
        Assert.False(result.Changed);
        Assert.True(result.Success);
        Assert.Equal("Batch Name: b1", result.Detail);

        // The three fields it owns, exactly as the inline re-wrap set them.
        Assert.Same(excluded, result.ExcludedTargets);
        Assert.Equal("serviced: ceo@contoso.com", result.ServicedNote);
        Assert.Equal(
            "Migration batch 'b1' created successfully with 2 user(s). NOTE: 1 protected principal(s) were excluded and NOT migrated.",
            result.Message);
    }

    [Fact]
    public void TheExclusionRewrap_OfAFailure_LeavesItsMessageAlone()
    {
        var inner = new PermissionResult
        {
            Success = false,
            Message = "New-MigrationBatch failed.",
            AppliedRights = ["SendAs"],
            Changed = false,
        };

        var result = MigrationService.WithProtectionOutcome(inner, ["ceo@contoso.com - protected principal"], null);

        Assert.False(result.Success);
        Assert.Equal("New-MigrationBatch failed.", result.Message);
        Assert.Equal(["SendAs"], result.AppliedRights);
        Assert.False(result.Changed);
        Assert.Null(result.ServicedNote);
    }

    [Fact]
    public void TheAuditFailureRewrap_KeepsEveryFieldButDetail()
    {
        var excluded = new[] { "ceo@contoso.com - protected principal" };
        var applied = new[] { "FullAccess" };
        var inner = new PermissionResult
        {
            Success = true,
            Message = "Migration batch 'b1' created.",
            Detail = "Batch Name: b1",
            ExcludedTargets = excluded,
            AppliedRights = applied,
            Changed = false,
            ServicedNote = "serviced: ceo@contoso.com",
        };

        var result = MigrationService.WithDetail(inner, "Audit logging failed: disk full");

        Assert.Equal("Audit logging failed: disk full", result.Detail);
        Assert.Same(applied, result.AppliedRights);
        Assert.False(result.Changed);
        Assert.True(result.Success);
        Assert.Equal("Migration batch 'b1' created.", result.Message);
        Assert.Same(excluded, result.ExcludedTargets);
        Assert.Equal("serviced: ceo@contoso.com", result.ServicedNote);
    }

    public static TheoryData<string> Rewraps =>
        new() { nameof(MigrationService.WithProtectionOutcome), nameof(MigrationService.WithDetail) };

    [Theory]
    [MemberData(nameof(Rewraps))]
    public void EveryRewrap_CarriesEveryFieldItDoesNotOwn(string rewrap)
    {
        var defaults = new PermissionResult();
        var source = EveryFieldSet();
        var (result, owned) = rewrap switch
        {
            nameof(MigrationService.WithProtectionOutcome) => (
                MigrationService.WithProtectionOutcome(source, ["ceo@contoso.com - protected principal"], "serviced: rewrap"),
                new[] { nameof(PermissionResult.Message), nameof(PermissionResult.ExcludedTargets), nameof(PermissionResult.ServicedNote) }),
            nameof(MigrationService.WithDetail) => (
                MigrationService.WithDetail(source, "Audit logging failed: rewrap"),
                new[] { nameof(PermissionResult.Detail) }),
            _ => throw new ArgumentOutOfRangeException(nameof(rewrap), rewrap, "not a Migration re-wrap"),
        };

        var properties = Properties().ToList();
        Assert.Contains(properties, p => p.Name == nameof(PermissionResult.AppliedRights));
        Assert.Contains(properties, p => p.Name == nameof(PermissionResult.Changed));

        foreach (var property in properties)
        {
            var before = property.GetValue(source);
            Assert.False(Equals(before, property.GetValue(defaults)), $"PermissionResult.{property.Name} was left at its default, so this test would not see it dropped.");

            if (owned.Contains(property.Name))
            {
                // Owned: the helper sets it, so it must not simply pass the source's value on.
                Assert.False(Equals(before, property.GetValue(result)), $"{rewrap} is listed as setting PermissionResult.{property.Name} but passed the source's value through.");
                continue;
            }

            Assert.True(Equals(before, property.GetValue(result)),
                $"{rewrap} dropped PermissionResult.{property.Name}: the re-wrapped result carries '{property.GetValue(result)}', not '{before}'.");
        }
    }

    [Fact]
    public void TheExclusionPath_ReturnsThroughTheHelper_AndTheServiceHoldsNoOtherCopy()
    {
        var code = RazorSyntax.CommentsBlanked(File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "MigrationService.cs")));
        var create = AuditCategoryFilingTests.MethodBody(code, "public async Task<PermissionResult> CreateMigrationBatchAsync(");

        Assert.Contains("return WithProtectionOutcome(result, excludedTargets, servicedSummary);", create);
        Assert.DoesNotMatch(FieldByFieldCopy, create);

        // The two helpers' own copies, and nothing else in the file.
        Assert.Equal(2, FieldByFieldCopy.Matches(code).Count);
    }

    [Fact]
    public void ThePage_CopiesNoResultFieldByField_AndNotesAFailedAuditThroughTheHelper()
    {
        var page = RazorSyntax.CommentsBlanked(File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "Migration.razor")));

        Assert.DoesNotMatch(FieldByFieldCopy, page);
        Assert.Equal(4, Regex.Matches(page, @"MigrationService\.WithDetail\(\w+, \$""Audit logging failed: \{ex\.Message\}""\);").Count);
    }

    // An initializer line that copies Success from another result: the signature of a field-by-field
    // re-wrap, and how the survey on the finding found all five.
    private static readonly Regex FieldByFieldCopy = new(@"\bSuccess\s*=\s*\w+\.Success\b");

    private static IEnumerable<PropertyInfo> Properties() =>
        typeof(PermissionResult).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite);

    // Every settable property at a value that is not its default, so a re-wrap that drops one is
    // visible. A property of a type this cannot fill fails the test rather than being skipped.
    private static PermissionResult EveryFieldSet()
    {
        var defaults = new PermissionResult();
        var source = new PermissionResult();
        foreach (var property in Properties())
        {
            object value = property.PropertyType switch
            {
                var t when t == typeof(bool) => !(bool)property.GetValue(defaults)!,
                var t when t == typeof(string) => $"value-of-{property.Name}",
                var t when t == typeof(IReadOnlyList<string>) => new[] { $"value-of-{property.Name}" },
                var t => throw new InvalidOperationException(
                    $"PermissionResult.{property.Name} is a {t}. Give this test a non-default value for it, so every Migration re-wrap is held to carrying it."),
            };
            property.SetValue(source, value);
        }

        return source;
    }
}
